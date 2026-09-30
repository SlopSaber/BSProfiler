using System;
using System.Collections;
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
        private readonly Dictionary<MethodBase, Sample> _samples = new Dictionary<MethodBase, Sample>();
        private readonly Dictionary<string, Sample> _assemblies = new Dictionary<string, Sample>(StringComparer.Ordinal);
        private readonly List<Sample> _frameSamples = new List<Sample>();
        private readonly List<Sample> _frameAssemblies = new List<Sample>();
        private readonly HashSet<MethodBase> _focusHandlers = new HashSet<MethodBase>();
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
            try { Install(directory, gameRoot); }
            catch
            {
                Dispose();
                throw;
            }
            _installing = false;
        }

        private void Install(string directory, string gameRoot)
        {
            string plugins = Path.GetFullPath(Path.Combine(gameRoot, "Plugins")) + Path.DirectorySeparatorChar;
            string libraries = Path.GetFullPath(Path.Combine(gameRoot, "Libs")) + Path.DirectorySeparatorChar;
            var prefix = new HarmonyMethod(typeof(CallbackProfiler).GetMethod(nameof(Begin), BindingFlags.NonPublic | BindingFlags.Static));
            var finalizer = new HarmonyMethod(typeof(CallbackProfiler).GetMethod(nameof(End), BindingFlags.NonPublic | BindingFlags.Static));
            var catalog = new List<string> { "assembly,callback,kind,source,status" };

            try
            {
                FieldInfo? field = typeof(Application).GetField("focusChanged", BindingFlags.Static | BindingFlags.NonPublic);
                if (field?.GetValue(null) is Delegate handlers)
                    foreach (Delegate handler in handlers.GetInvocationList()) _focusHandlers.Add(handler.Method);
            }
            catch (Exception ex) { Plugin.Log?.Warn("BSProfiler focus subscriber discovery failed: " + ex.Message); }

            _active = this;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string path;
                try { path = assembly.IsDynamic ? "" : Path.GetFullPath(assembly.Location); }
                catch { continue; }
                bool isPluginAssembly = path.StartsWith(plugins, StringComparison.OrdinalIgnoreCase);
                if (!isPluginAssembly && !path.StartsWith(libraries, StringComparison.OrdinalIgnoreCase)) continue;
                if (assembly == typeof(CallbackProfiler).Assembly) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(type => type != null).Cast<Type>().ToArray(); }
                catch { continue; }

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
                        if (!isBehaviour && !isStateMachine && !isTickable && !isHarmonyClass &&
                            method.Name != "Prefix" && method.Name != "Postfix" && method.Name != "Finalizer" &&
                            !_focusHandlers.Contains(method) && !IsEnvironmentSetup(method) && !IsReplayEventDetail(method)) continue;
                        string? kind = null;
                        try
                        {
                            if (IsReplayEventDetail(method)) kind = "Replay event detail";
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
                        if (kind == null) continue;

                        var descriptor = new Descriptor(assembly.GetName().Name ?? "", type.FullName + "." + method.Name,
                            kind == "Unity focus/pause callback" || kind == "Application.focusChanged subscriber", assembly);
                        _samples[method] = new Sample(descriptor);
                        if (!_assemblies.ContainsKey(descriptor.Assembly))
                            _assemblies.Add(descriptor.Assembly, new Sample(new Descriptor(descriptor.Assembly, "")));
                        string status = "installed";
                        try
                        {
                            _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                            HookCount++;
                        }
                        catch (Exception ex)
                        {
                            _samples.Remove(method);
                            FailedCount++;
                            status = ex.GetType().Name + ": " + ex.Message;
                        }
                        catalog.Add(string.Join(",", CaptureWriter.Csv(descriptor.Assembly),
                            CaptureWriter.Csv(descriptor.Callback), CaptureWriter.Csv(kind),
                            CaptureWriter.Csv(path), CaptureWriter.Csv(status)));
                    }
                }
            }
            File.WriteAllLines(Path.Combine(directory, "callback-catalog.csv"), catalog);
        }

        private static bool IsVoidWithoutParameters(MethodInfo method) =>
            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;

        private static bool IsEnvironmentSetup(MethodInfo method) =>
            (method.DeclaringType?.FullName == "Chroma.EnvironmentEnhancement.EnvironmentEnhancementManager" && method.Name == "GetAllGameObjects") ||
            (method.DeclaringType?.FullName == "Chroma.EnvironmentEnhancement.LookupID" && method.Name == "Get");

        // Tick includes these synchronous subscribers. Record their nested time to distinguish
        // event dispatch from note lookup, score emulation, and visual work without changing replay logic.
        private static bool IsReplayEventDetail(MethodInfo method)
        {
            string? type = method.DeclaringType?.FullName;
            if (type == "BeatLeader.Replayer.Emulation.ReplayerNotesCutter")
                return method.Name == "ProcessNote" || method.Name == "TryFindSpawnedNote";
            if (type == "BeatLeader.Replayer.Emulation.ReplayerScoreProcessor")
                return method.Name == "SetupEmulator" || method.Name == "SimulateNoteWasCut" ||
                    method.Name == "SimulateNoteWasMissed" || method.Name == "HandleNoteBeatmapEventDequeued" ||
                    method.Name == "HandleWallBeatmapEventDequeued";
            if (type == "BeatLeader.Replayer.ReplayBeatmapData") return method.Name == "FindNoteDataForEvent";
            return type == "BeatLeader.Replayer.BeatmapVisualsController" && method.Name == "HandleNoteBeatmapEventDequeued";
        }

        private static bool HasAttribute(IList<CustomAttributeData> attributes, string name) =>
            attributes.Any(attribute => attribute.AttributeType.FullName == name);

        private static void Begin(out TimingState __state)
        {
            CallbackProfiler? profiler = _active;
            __state = default;
            if (profiler == null || profiler._installing || Thread.CurrentThread.ManagedThreadId != profiler._mainThreadId) return;
            __state.AllocatedBytes = MemoryDiagnostics.AllocatedBytes();
            __state.Gc0 = GC.CollectionCount(0);
            __state.Gc1 = GC.CollectionCount(1);
            __state.Gc2 = GC.CollectionCount(2);
            __state.Ticks = Stopwatch.GetTimestamp();
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
            long bytes = __state.AllocatedBytes < 0 ? 0 : Math.Max(0, MemoryDiagnostics.AllocatedBytes() - __state.AllocatedBytes);
            int gc0 = GC.CollectionCount(0) - __state.Gc0;
            int gc1 = GC.CollectionCount(1) - __state.Gc1;
            int gc2 = GC.CollectionCount(2) - __state.Gc2;
            // Take timing/allocation endpoints before observer registration allocates metadata.
            profiler.ObserveReceiver(__instance, sample);
            bool crossing = gc0 > 0 || gc1 > 0 || gc2 > 0;
            if (sample.Calls == 0) profiler._frameSamples.Add(sample);
            sample.Add(ticks, bytes, crossing);
            Sample assembly = profiler._assemblies[sample.Descriptor.Assembly];
            if (assembly.Calls == 0) profiler._frameAssemblies.Add(assembly);
            assembly.Add(ticks, bytes, crossing);
            if (ticks * 1000.0 / Stopwatch.Frequency < 8 && !crossing && __exception == null && !sample.Descriptor.IsFocus) return;
            if (now - profiler._detailWindowTicks >= Stopwatch.Frequency)
            {
                profiler._detailWindowTicks = now;
                profiler._detailCount = 0;
            }
            if (profiler._detailCount++ >= 32) { profiler.DetailsDropped++; return; }
            try
            {
                string stack = new StackTrace(2, false).ToString();
                if (stack.Length > 12000) stack = stack.Substring(0, 12000);
                profiler._writer.SlowCall(string.Join(",", CaptureWriter.Number((now - profiler._startTicks) * 1000.0 / Stopwatch.Frequency),
                    Time.frameCount.ToString(), CaptureWriter.Csv(sample.Descriptor.Assembly), CaptureWriter.Csv(sample.Descriptor.Callback),
                    CaptureWriter.Number(ticks * 1000.0 / Stopwatch.Frequency), __state.AllocatedBytes < 0 ? "" : bytes.ToString(),
                    gc0.ToString(), gc1.ToString(), gc2.ToString(), CaptureWriter.Csv(__exception?.GetType().FullName),
                    CaptureWriter.Csv(stack)));
            }
            catch { profiler.DetailsDropped++; }
        }

        private void ObserveReceiver(object? instance, Sample sample)
        {
            try { _retention.Observe(instance, sample.Descriptor.OwnerAssembly!); }
            catch { } // Observation must preserve the original callback and exception.
        }

        public void CaptureFrame(CaptureWriter writer, double elapsedMs, int frame, string scene, double frameMs, double thresholdMs)
        {
            if (frameMs >= thresholdMs)
            {
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
                MemoryDiagnostics.AllocationCounterAvailable ? sample.MaxBytes.ToString() : "", sample.Crossings.ToString()));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _active = null;
            try { _harmony.UnpatchSelf(); }
            catch (Exception ex) { Plugin.Log?.Error("BSProfiler callback unpatch failed: " + ex); }
            finally
            {
                _samples.Clear();
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
            public Descriptor(string assembly, string callback, bool isFocus = false, Assembly? ownerAssembly = null)
            { Assembly = assembly; Callback = callback; IsFocus = isFocus; OwnerAssembly = ownerAssembly; }
        }

        private sealed class Sample
        {
            public readonly Descriptor Descriptor;
            public int Calls;
            public long TotalTicks;
            public long MaxTicks;
            public long Bytes, MaxBytes, WindowBytes, WindowMaxBytes, WindowTicks;
            public int Crossings, WindowCalls, WindowCrossings;
            public Sample(Descriptor descriptor) => Descriptor = descriptor;
            public void Add(long ticks, long bytes, bool crossing)
            {
                Calls++; WindowCalls++;
                TotalTicks += ticks; WindowTicks += ticks;
                MaxTicks = Math.Max(MaxTicks, ticks);
                Bytes += bytes; WindowBytes += bytes;
                MaxBytes = Math.Max(MaxBytes, bytes); WindowMaxBytes = Math.Max(WindowMaxBytes, bytes);
                if (crossing) { Crossings++; WindowCrossings++; }
            }
            public void ResetFrame() { Calls = Crossings = 0; TotalTicks = MaxTicks = Bytes = MaxBytes = 0; }
            public void ResetWindow() { WindowCalls = WindowCrossings = 0; WindowTicks = WindowBytes = WindowMaxBytes = 0; }
        }

        private struct TimingState
        {
            public long Ticks, AllocatedBytes;
            public int Gc0, Gc1, Gc2;
        }
    }
}
