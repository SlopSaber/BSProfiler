using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace BSProfiler
{
    // Times managed entry points owned by installed mods. These are inclusive timings:
    // nested callbacks can appear in both their caller's and callee's totals.
    internal sealed class CallbackProfiler : IDisposable
    {
        private const string HarmonyId = "BSProfiler.callback-timing";
        private static readonly HashSet<string> UnityCallbacks = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnUpdate", "OnLateUpdate", "OnFixedUpdate",
            "Awake", "OnEnable", "OnDisable", "OnDestroy"
        };
        private static readonly HashSet<string> TickCallbacks = new HashSet<string>(StringComparer.Ordinal)
        {
            "Tick", "LateTick", "FixedTick"
        };

        private static CallbackProfiler? _active;
        private readonly Harmony _harmony = new Harmony(HarmonyId);
        private readonly ConcurrentDictionary<MethodBase, Sample> _samples = new ConcurrentDictionary<MethodBase, Sample>();
        private readonly string _directory, _gameRoot;
        private readonly List<string> _catalog = new List<string> { "assembly,callback,kind,source,status,method_signature" };
        public bool InstallationComplete { get; private set; }
        private readonly Dictionary<string, Sample> _assemblies = new Dictionary<string, Sample>(StringComparer.Ordinal);
        private readonly List<Sample> _frameSamples = new List<Sample>();
        private readonly List<Sample> _frameAssemblies = new List<Sample>();
        private readonly HashSet<MethodBase> _focusHandlers = new HashSet<MethodBase>();
        private readonly HashSet<MethodBase> _delegateTargets = new HashSet<MethodBase>();
        private readonly ActiveCall[] _calls = new ActiveCall[128];
        private int _depth;
        private long _nextCallId, _hookTicks, _rootTicks;
        private int _rootGcCrossings;
        public int ExtraHooks { get; private set; }
        public int ExtraHooksOmitted { get; private set; }
        public int DiscoveryFailures { get; private set; }
        public int DepthDropped { get; private set; }
        private readonly int _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly CaptureWriter _writer;
        private readonly long _startTicks;
        private readonly ModRetentionTracker _retention;
        private volatile bool _installing = true;
        private double _allocationWindowStartMs;
        private int _detailCount;
        private long _detailWindowTicks;
        public int DetailsDropped { get; private set; }
        private bool _disposed;

        public int HookCount { get; private set; }
        public int FailedCount { get; private set; }

        public CallbackProfiler(string directory, string gameRoot, CaptureWriter writer, long startTicks, ModRetentionTracker retention)
        {
            _writer = writer;
            _startTicks = startTicks;
            _retention = retention;
            _directory = directory;
            _gameRoot = gameRoot;
            _installing = false;
        }

        public IEnumerator InstallRoutine(Action completed)
        {
            // Let the game produce frames before starting discovery and method rewriting.
            yield return null;
            long start = Stopwatch.GetTimestamp();
            long workTicks = 0;
            using (IEnumerator<object?> steps = Install(_directory, _gameRoot))
            {
                while (!_disposed)
                {
                    long before = Stopwatch.GetTimestamp();
                    bool more;
                    _installing = true;
                    try { more = steps.MoveNext(); }
                    catch (Exception ex)
                    {
                        Plugin.Log?.Error("BSProfiler callback installation failed: " + ex);
                        _writer.Event(CaptureWriter.Number(ElapsedMs()) + ",callback-install-failed," + CaptureWriter.Csv(ex.Message));
                        more = false;
                    }
                    finally { _installing = false; workTicks += Stopwatch.GetTimestamp() - before; }
                    if (!more) break;
                    yield return null;
                }
            }
            if (_disposed) yield break;
            // Install marks success only after the complete catalog has been written.
            _writer.Event(CaptureWriter.Number(ElapsedMs()) + ",callback-install-finished," + CaptureWriter.Csv(
                "complete=" + InstallationComplete + "; work_ms=" + CaptureWriter.Number(workTicks * 1000.0 / Stopwatch.Frequency) +
                "; wall_ms=" + CaptureWriter.Number((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency) + "; hooks=" + HookCount));
            completed();
        }

        private double ElapsedMs() => (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;

        private IEnumerator<object?> Install(string directory, string gameRoot)
        {
            string plugins = Path.GetFullPath(Path.Combine(gameRoot, "Plugins")) + Path.DirectorySeparatorChar;
            string libraries = Path.GetFullPath(Path.Combine(gameRoot, "Libs")) + Path.DirectorySeparatorChar;
            var prefix = new HarmonyMethod(typeof(CallbackProfiler).GetMethod(nameof(Begin), BindingFlags.NonPublic | BindingFlags.Static));
            var finalizer = new HarmonyMethod(typeof(CallbackProfiler).GetMethod(nameof(End), BindingFlags.NonPublic | BindingFlags.Static));
            var catalog = _catalog;
            var slice = Stopwatch.StartNew();

            try
            {
                FieldInfo? field = typeof(Application).GetField("focusChanged", BindingFlags.Static | BindingFlags.NonPublic);
                if (field?.GetValue(null) is Delegate handlers)
                    foreach (Delegate handler in handlers.GetInvocationList()) _focusHandlers.Add(handler.Method);
            }
            catch (Exception ex) { Plugin.Log?.Warn("BSProfiler focus subscriber discovery failed: " + ex.Message); }

            _active = this;
            var assemblyTypes = new Dictionary<Assembly, Type[]>();
            int discoveryFailures = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                long discoveryStart = Stopwatch.GetTimestamp();
                try
                {
                    string source = assembly.IsDynamic ? "" : Path.GetFullPath(assembly.Location);
                    if (assembly == typeof(CallbackProfiler).Assembly || IsInstrumentationFramework(assembly) ||
                        (!source.StartsWith(plugins, StringComparison.OrdinalIgnoreCase) &&
                         !source.StartsWith(libraries, StringComparison.OrdinalIgnoreCase))) continue;
                    Type[] discovered;
                    try { discovered = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { discovered = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
                    assemblyTypes.Add(assembly, discovered);
                    // Extra discovery belongs to mod assemblies; reflection/JIT/serializer libraries
                    // must not consume hook budgets or intercept the machinery that installs hooks.
                    if (source.StartsWith(plugins, StringComparison.OrdinalIgnoreCase))
                        CallbackDiscovery.Collect(discovered, _delegateTargets, ref discoveryFailures);
                }
                catch { discoveryFailures++; }
                _writer.Event(CaptureWriter.Number(ElapsedMs()) + ",callback-discovery," + CaptureWriter.Csv(
                    assembly.GetName().Name + "; work_ms=" + CaptureWriter.Number((Stopwatch.GetTimestamp() - discoveryStart) * 1000.0 / Stopwatch.Frequency)));
                if (slice.Elapsed.TotalMilliseconds >= 2) { yield return null; slice.Restart(); }
            }
            DiscoveryFailures = discoveryFailures;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string path;
                try { path = assembly.IsDynamic ? "" : Path.GetFullPath(assembly.Location); }
                catch { continue; }
                bool isPluginAssembly = path.StartsWith(plugins, StringComparison.OrdinalIgnoreCase);
                if (!isPluginAssembly && !path.StartsWith(libraries, StringComparison.OrdinalIgnoreCase)) continue;
                if (assembly == typeof(CallbackProfiler).Assembly || IsInstrumentationFramework(assembly)) continue;

                if (!assemblyTypes.TryGetValue(assembly, out Type[] types)) continue;
                int assemblyExtraHooks = 0;
                long assemblyWorkTicks = 0;
                long assemblyStart = Stopwatch.GetTimestamp();

                foreach (Type type in types)
                {
                    bool isBehaviour;
                    bool isStateMachine;
                    bool isTickable;
                    bool isHarmonyClass;
                    MethodInfo[] methods;
                    try
                    {
                        if (!type.IsClass || type.ContainsGenericParameters) continue;
                        isBehaviour = typeof(MonoBehaviour).IsAssignableFrom(type);
                        isStateMachine = isPluginAssembly &&
                            (typeof(IEnumerator).IsAssignableFrom(type) ||
                             typeof(IAsyncStateMachine).IsAssignableFrom(type));
                        isTickable = type.GetInterfaces().Any(i => i.FullName == "Zenject.ITickable" ||
                            i.FullName == "Zenject.ILateTickable" || i.FullName == "Zenject.IFixedTickable");
                        isHarmonyClass = HasAttribute(type.GetCustomAttributesData(), "HarmonyLib.HarmonyPatch");
                        methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    foreach (MethodInfo method in methods)
                    {
                        if (slice.Elapsed.TotalMilliseconds >= 2) { yield return null; slice.Restart(); }
                        if (!isBehaviour && !isStateMachine && !isTickable && !isHarmonyClass &&
                            method.Name != "Prefix" && method.Name != "Postfix" && method.Name != "Finalizer" &&
                            !_focusHandlers.Contains(method) && !IsEnvironmentSetup(method) &&
                            !(isPluginAssembly && (_delegateTargets.Contains(method) || CallbackDiscovery.IsNamedHandler(method)))) continue;
                        string? kind = null;
                        try
                        {
                            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                            if (isBehaviour && UnityCallbacks.Contains(method.Name) && IsVoidWithoutParameters(method))
                                kind = "Unity callback";
                            else if (isBehaviour && (method.Name == "OnApplicationFocus" || method.Name == "OnApplicationPause") &&
                                     method.ReturnType == typeof(void) && method.GetParameters().Length == 1 &&
                                     method.GetParameters()[0].ParameterType == typeof(bool))
                                kind = "Unity focus/pause callback";
                            else if (_focusHandlers.Contains(method))
                                kind = "Application.focusChanged subscriber";
                            else if (IsEnvironmentSetup(method))
                                kind = "Chroma environment setup";
                            else if (isTickable && TickCallbacks.Contains(method.Name) && IsVoidWithoutParameters(method))
                                kind = "Zenject tick";
                            else if (isStateMachine && method.Name == "MoveNext" && method.GetParameters().Length == 0)
                                kind = "coroutine/async step";
                            else if (HasAttribute(method.GetCustomAttributesData(), "HarmonyLib.HarmonyPrefix") ||
                                     HasAttribute(method.GetCustomAttributesData(), "HarmonyLib.HarmonyPostfix") ||
                                     HasAttribute(method.GetCustomAttributesData(), "HarmonyLib.HarmonyFinalizer") ||
                                     (isHarmonyClass && (method.Name == "Prefix" || method.Name == "Postfix" || method.Name == "Finalizer")))
                                kind = "Harmony callback";
                        }
                        catch { continue; }
                        bool extra = kind == null && isPluginAssembly &&
                            (_delegateTargets.Contains(method) || CallbackDiscovery.IsNamedHandler(method));
                        if (extra) kind = _delegateTargets.Contains(method) ? "Mod delegate target" : "Mod event/UI handler";
                        if (kind == null) continue;

                        var descriptor = new Descriptor(assembly.GetName().Name ?? "", type.FullName + "." + method.Name,
                            kind == "Unity focus/pause callback" || kind == "Application.focusChanged subscriber", assembly, method.ToString());
                        if (extra && (assemblyExtraHooks >= 256 || ExtraHooks >= 2048))
                        {
                            ExtraHooksOmitted++;
                            catalog.Add(string.Join(",", CaptureWriter.Csv(descriptor.Assembly), CaptureWriter.Csv(descriptor.Callback),
                                CaptureWriter.Csv(kind), CaptureWriter.Csv(path), "omitted: extra hook limit", CaptureWriter.Csv(descriptor.Signature)));
                            continue;
                        }
                        _samples[method] = new Sample(descriptor);
                        if (!_assemblies.ContainsKey(descriptor.Assembly))
                            _assemblies.Add(descriptor.Assembly, new Sample(new Descriptor(descriptor.Assembly, "")));
                        string status = "installed";
                        long patchStart = Stopwatch.GetTimestamp();
                        try
                        {
                            _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                            HookCount++;
                            if (extra) { ExtraHooks++; assemblyExtraHooks++; }
                        }
                        catch (Exception ex)
                        {
                            _samples.TryRemove(method, out _);
                            FailedCount++;
                            status = ex.GetType().Name + ": " + ex.Message;
                        }
                        assemblyWorkTicks += Stopwatch.GetTimestamp() - patchStart;
                        catalog.Add(string.Join(",", CaptureWriter.Csv(descriptor.Assembly),
                            CaptureWriter.Csv(descriptor.Callback), CaptureWriter.Csv(kind),
                            CaptureWriter.Csv(path), CaptureWriter.Csv(status), CaptureWriter.Csv(descriptor.Signature)));
                    }
                }
                _writer.Event(CaptureWriter.Number(ElapsedMs()) + ",callback-assembly-installed," + CaptureWriter.Csv(
                    assembly.GetName().Name + "; patch_ms=" + CaptureWriter.Number(assemblyWorkTicks * 1000.0 / Stopwatch.Frequency) +
                    "; wall_ms=" + CaptureWriter.Number((Stopwatch.GetTimestamp() - assemblyStart) * 1000.0 / Stopwatch.Frequency) + "; total_hooks=" + HookCount));
            }
            File.WriteAllLines(Path.Combine(directory, "callback-catalog.csv"), catalog);
            InstallationComplete = true;
        }

        private static bool IsVoidWithoutParameters(MethodInfo method) =>
            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;

        private static bool IsEnvironmentSetup(MethodInfo method) =>
            (method.DeclaringType?.FullName == "Chroma.EnvironmentEnhancement.EnvironmentEnhancementManager" && method.Name == "GetAllGameObjects") ||
            (method.DeclaringType?.FullName == "Chroma.EnvironmentEnhancement.LookupID" && method.Name == "Get");

        private static bool HasAttribute(IList<CustomAttributeData> attributes, string name) =>
            attributes.Any(attribute => attribute.AttributeType.FullName == name);

        private static void Begin(MethodBase __originalMethod, out TimingState __state)
        {
            long entry = Stopwatch.GetTimestamp();
            CallbackProfiler? profiler = _active;
            __state = default;
            if (profiler == null || profiler._installing || Thread.CurrentThread.ManagedThreadId != profiler._mainThreadId) return;
            if (profiler._depth >= profiler._calls.Length) { profiler.DepthDropped++; return; }
            if (!profiler._samples.TryGetValue(__originalMethod, out Sample sample)) return;
            __state.AllocatedBytes = MemoryDiagnostics.AllocatedBytes();
            __state.Gc0 = GC.CollectionCount(0);
            __state.Gc1 = GC.CollectionCount(1);
            __state.Gc2 = GC.CollectionCount(2);
            __state.Ticks = Stopwatch.GetTimestamp();
            __state.EntryTicks = entry;
            __state.Index = profiler._depth++;
            __state.CallId = ++profiler._nextCallId;
            profiler._calls[__state.Index] = new ActiveCall { Sample = sample, CallId = __state.CallId };
            profiler._hookTicks += __state.Ticks - entry;
        }

        private static void End(MethodBase __originalMethod, object? __instance, TimingState __state, Exception? __exception)
        {
            CallbackProfiler? profiler = _active;
            if (profiler == null || profiler._installing || !profiler._samples.TryGetValue(__originalMethod, out Sample sample)) return;
            if (__state.Ticks == 0)
            {
                profiler.ObserveReceiver(__instance, sample);
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long ticks = now - __state.Ticks;
            if (ticks < 0) return;
            ActiveCall call = profiler._calls[__state.Index];
            ActiveCall parent = __state.Index > 0 ? profiler._calls[__state.Index - 1] : default;
            profiler._calls[__state.Index] = default;
            profiler._depth = __state.Index;
            long selfTicks = Math.Max(0, ticks - call.ChildrenTicks);
            long bytes = __state.AllocatedBytes < 0 ? 0 : Math.Max(0, MemoryDiagnostics.AllocatedBytes() - __state.AllocatedBytes);
            int gc0 = GC.CollectionCount(0) - __state.Gc0;
            int gc1 = GC.CollectionCount(1) - __state.Gc1;
            int gc2 = GC.CollectionCount(2) - __state.Gc2;
            // Take timing/allocation endpoints before observer registration allocates metadata.
            profiler.ObserveReceiver(__instance, sample);
            bool crossing = gc0 > 0 || gc1 > 0 || gc2 > 0;
            if (sample.Calls == 0) profiler._frameSamples.Add(sample);
            sample.Add(ticks, selfTicks, bytes, crossing);
            Sample assembly = profiler._assemblies[sample.Descriptor.Assembly];
            if (assembly.Calls == 0) profiler._frameAssemblies.Add(assembly);
            assembly.Add(ticks, selfTicks, bytes, crossing);
            if (__state.Index == 0) { profiler._rootTicks += ticks; if (crossing) profiler._rootGcCrossings++; }
            try { profiler.WriteCallDetail(sample, __state, parent, ticks, selfTicks, bytes, now, gc0, gc1, gc2, __exception); }
            finally
            {
                long finish = Stopwatch.GetTimestamp();
                profiler._hookTicks += finish - now;
                if (__state.Index > 0) profiler._calls[__state.Index - 1].ChildrenTicks += finish - __state.EntryTicks;
            }
        }

        private void WriteCallDetail(Sample sample, TimingState state, ActiveCall parent, long ticks, long selfTicks,
            long bytes, long now, int gc0, int gc1, int gc2, Exception? exception)
        {
            bool crossing = gc0 > 0 || gc1 > 0 || gc2 > 0;
            if (ticks * 1000.0 / Stopwatch.Frequency < 8 && !crossing && exception == null && !sample.Descriptor.IsFocus) return;
            if (now - _detailWindowTicks >= Stopwatch.Frequency)
            {
                _detailWindowTicks = now;
                _detailCount = 0;
            }
            if (_detailCount++ >= 32) { DetailsDropped++; return; }
            try
            {
                string stack = new StackTrace(3, false).ToString();
                if (stack.Length > 12000) stack = stack.Substring(0, 12000);
                _writer.SlowCall(string.Join(",", CaptureWriter.Number((now - _startTicks) * 1000.0 / Stopwatch.Frequency),
                    Time.frameCount.ToString(), CaptureWriter.Csv(sample.Descriptor.Assembly), CaptureWriter.Csv(sample.Descriptor.Callback),
                    CaptureWriter.Number(ticks * 1000.0 / Stopwatch.Frequency), state.AllocatedBytes < 0 ? "" : bytes.ToString(),
                    gc0.ToString(), gc1.ToString(), gc2.ToString(), CaptureWriter.Csv(exception?.GetType().FullName),
                    CaptureWriter.Csv(stack), state.CallId.ToString(), parent.CallId == 0 ? "" : parent.CallId.ToString(),
                    CaptureWriter.Csv(parent.Sample?.Descriptor.Callback), state.Index.ToString(),
                    CaptureWriter.Number(selfTicks * 1000.0 / Stopwatch.Frequency),
                    crossing ? "gc-overlap: allocation caller unknown" : "no observed GC; unhooked descendants included",
                    CaptureWriter.Number((state.Ticks - _startTicks) * 1000.0 / Stopwatch.Frequency), CaptureWriter.Csv(sample.Descriptor.Signature)));
            }
            catch { DetailsDropped++; }
        }

        private void ObserveReceiver(object? instance, Sample sample)
        {
            try { _retention.Observe(instance, sample.Descriptor.OwnerAssembly!); }
            catch { } // Observation must preserve the original callback and exception.
        }

        public void CaptureFrame(CaptureWriter writer, double elapsedMs, int frame, string scene, double frameMs, double thresholdMs,
            bool observedGc, double gcMarkerMs)
        {
            if (frameMs >= thresholdMs)
            {
                writer.FrameEvidence(string.Join(",", CaptureWriter.Number(elapsedMs), frame.ToString(), CaptureWriter.Csv(scene),
                    CaptureWriter.Number(frameMs), CaptureWriter.Number(_rootTicks * 1000.0 / Stopwatch.Frequency),
                    CaptureWriter.Number(_hookTicks * 1000.0 / Stopwatch.Frequency), _rootGcCrossings.ToString(),
                    observedGc ? "1" : "0", CaptureWriter.Number(gcMarkerMs), DepthDropped.ToString(),
                    observedGc || _rootGcCrossings > 0 ? "GC overlap; do not assign whole span to mod" :
                    "Compare root spans; uncovered native/game/background/wait work possible"));
                foreach (Sample sample in _frameAssemblies.OrderByDescending(s => s.TotalTicks).Take(8))
                    Write(writer, elapsedMs, frame, scene, frameMs, "assembly", sample);
                foreach (Sample sample in _frameSamples.OrderByDescending(s => s.TotalTicks).Take(20))
                    Write(writer, elapsedMs, frame, scene, frameMs, "method", sample);
            }
            if (elapsedMs - _allocationWindowStartMs >= 1000)
            {
                foreach (Sample sample in _assemblies.Values.Where(s => s.WindowBytes > 0).OrderByDescending(s => s.WindowBytes).Take(8))
                    WriteAllocation(writer, elapsedMs, frame, scene, "assembly", sample);
                foreach (Sample sample in _samples.Values.Where(s => s.WindowBytes > 0).OrderByDescending(s => s.WindowBytes).Take(20))
                    WriteAllocation(writer, elapsedMs, frame, scene, "method", sample);
                _allocationWindowStartMs = elapsedMs;
                foreach (Sample sample in _samples.Values) sample.ResetWindow();
                foreach (Sample sample in _assemblies.Values) sample.ResetWindow();
            }
            foreach (Sample sample in _frameSamples) sample.ResetFrame();
            foreach (Sample sample in _frameAssemblies) sample.ResetFrame();
            _frameSamples.Clear();
            _frameAssemblies.Clear();
            _hookTicks = _rootTicks = 0;
            _rootGcCrossings = 0;
        }

        private void WriteAllocation(CaptureWriter writer, double elapsedMs, int frame, string scene, string scope, Sample sample)
        {
            writer.Allocation(string.Join(",", CaptureWriter.Number(elapsedMs), CaptureWriter.Number(elapsedMs - _allocationWindowStartMs),
                frame.ToString(), CaptureWriter.Csv(scene), scope, CaptureWriter.Csv(sample.Descriptor.Assembly), CaptureWriter.Csv(sample.Descriptor.Callback),
                sample.WindowCalls.ToString(), sample.WindowBytes.ToString(), sample.WindowMaxBytes.ToString(),
                CaptureWriter.Number(sample.WindowTicks * 1000.0 / Stopwatch.Frequency), sample.WindowCrossings.ToString()));
        }

        private static void Write(CaptureWriter writer, double elapsedMs, int frame, string scene, double frameMs,
            string scope, Sample sample)
        {
            writer.Callback(string.Join(",", CaptureWriter.Number(elapsedMs), frame.ToString(),
                CaptureWriter.Csv(scene), CaptureWriter.Number(frameMs), scope,
                CaptureWriter.Csv(sample.Descriptor.Assembly), CaptureWriter.Csv(sample.Descriptor.Callback),
                sample.Calls.ToString(), CaptureWriter.Number(sample.TotalTicks * 1000.0 / Stopwatch.Frequency),
                CaptureWriter.Number(sample.MaxTicks * 1000.0 / Stopwatch.Frequency),
                MemoryDiagnostics.AllocationCounterAvailable ? sample.Bytes.ToString() : "",
                MemoryDiagnostics.AllocationCounterAvailable ? sample.MaxBytes.ToString() : "", sample.Crossings.ToString(),
                CaptureWriter.Number(sample.SelfTicks * 1000.0 / Stopwatch.Frequency),
                CaptureWriter.Number(sample.NoGcTicks * 1000.0 / Stopwatch.Frequency),
                CaptureWriter.Number(sample.GcTicks * 1000.0 / Stopwatch.Frequency), CaptureWriter.Csv(sample.Descriptor.Signature)));
        }

        private static bool IsInstrumentationFramework(Assembly assembly)
        {
            string name = assembly.GetName().Name ?? "";
            return name == "0Harmony" || name == "Harmony" ||
                name.StartsWith("MonoMod", StringComparison.Ordinal) || name.StartsWith("Mono.Cecil", StringComparison.Ordinal);
        }

        public void Dispose() => Stop(false);

        public void Stop(bool processQuitting)
        {
            if (_disposed) return;
            _disposed = true;
            _active = null;
            // Disabled callbacks become no-ops. Process teardown does not need thousands of
            // synchronous method rewrites; normal live capture disposal still removes hooks.
            try { if (!processQuitting) _harmony.UnpatchSelf(); }
            catch (Exception ex) { Plugin.Log?.Error("BSProfiler callback unpatch failed: " + ex); }
            finally
            {
                if (!InstallationComplete)
                {
                    try { File.WriteAllLines(Path.Combine(_directory, "callback-catalog.csv"), _catalog); }
                    catch (Exception ex) { Plugin.Log?.Warn("BSProfiler partial catalog write failed: " + ex.Message); }
                }
                // Background finalizers may already hold this instance. Keep their immutable
                // method lookup intact; it is released with the disposed profiler instance.
                _assemblies.Clear();
                _frameSamples.Clear();
                _frameAssemblies.Clear();
            }
        }

        private sealed class Descriptor
        {
            public readonly string Assembly;
            public readonly string Callback;
            public readonly bool IsFocus;
            public readonly Assembly? OwnerAssembly;
            public readonly string? Signature;
            public Descriptor(string assembly, string callback, bool isFocus = false, Assembly? ownerAssembly = null, string? signature = null)
            { Assembly = assembly; Callback = callback; IsFocus = isFocus; OwnerAssembly = ownerAssembly; Signature = signature; }
        }

        private sealed class Sample
        {
            public readonly Descriptor Descriptor;
            public int Calls;
            public long TotalTicks;
            public long SelfTicks, NoGcTicks, GcTicks;
            public long MaxTicks;
            public long Bytes, MaxBytes, WindowBytes, WindowMaxBytes, WindowTicks;
            public int Crossings, WindowCalls, WindowCrossings;
            public Sample(Descriptor descriptor) => Descriptor = descriptor;
            public void Add(long ticks, long selfTicks, long bytes, bool crossing)
            {
                Calls++; WindowCalls++;
                TotalTicks += ticks; WindowTicks += ticks;
                SelfTicks += selfTicks;
                if (crossing) GcTicks += ticks; else NoGcTicks += ticks;
                MaxTicks = Math.Max(MaxTicks, ticks);
                Bytes += bytes; WindowBytes += bytes;
                MaxBytes = Math.Max(MaxBytes, bytes); WindowMaxBytes = Math.Max(WindowMaxBytes, bytes);
                if (crossing) { Crossings++; WindowCrossings++; }
            }
            public void ResetFrame() { Calls = Crossings = 0; TotalTicks = MaxTicks = Bytes = MaxBytes = SelfTicks = NoGcTicks = GcTicks = 0; }
            public void ResetWindow() { WindowCalls = WindowCrossings = 0; WindowTicks = WindowBytes = WindowMaxBytes = 0; }
        }

        private struct TimingState
        {
            public long Ticks, AllocatedBytes, EntryTicks, CallId;
            public int Gc0, Gc1, Gc2, Index;
        }

        private struct ActiveCall
        {
            public Sample? Sample;
            public long CallId, ChildrenTicks;
        }
    }
}
