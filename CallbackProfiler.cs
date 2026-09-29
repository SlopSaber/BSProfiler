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
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnUpdate", "OnLateUpdate", "OnFixedUpdate"
        };
        private static readonly HashSet<string> TickCallbacks = new HashSet<string>(StringComparer.Ordinal)
        {
            "Tick", "LateTick", "FixedTick"
        };

        private static CallbackProfiler? _active;
        private readonly Harmony _harmony = new Harmony(HarmonyId);
        private readonly Dictionary<MethodBase, Descriptor> _methods = new Dictionary<MethodBase, Descriptor>();
        private readonly Dictionary<MethodBase, Sample> _samples = new Dictionary<MethodBase, Sample>();
        private readonly int _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        private bool _disposed;

        public int HookCount { get; private set; }
        public int FailedCount { get; private set; }

        public CallbackProfiler(string directory, string gameRoot)
        {
            try { Install(directory, gameRoot); }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Install(string directory, string gameRoot)
        {
            string plugins = Path.GetFullPath(Path.Combine(gameRoot, "Plugins")) + Path.DirectorySeparatorChar;
            string libraries = Path.GetFullPath(Path.Combine(gameRoot, "Libs")) + Path.DirectorySeparatorChar;
            var prefix = new HarmonyMethod(typeof(CallbackProfiler).GetMethod(nameof(Begin), BindingFlags.NonPublic | BindingFlags.Static));
            var postfix = new HarmonyMethod(typeof(CallbackProfiler).GetMethod(nameof(End), BindingFlags.NonPublic | BindingFlags.Static));
            var catalog = new List<string> { "assembly,callback,kind,source,status" };

            _active = this;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string path;
                try { path = assembly.IsDynamic ? "" : Path.GetFullPath(assembly.Location); }
                catch { continue; }
                if (!path.StartsWith(plugins, StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(libraries, StringComparison.OrdinalIgnoreCase)) continue;
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
                        isStateMachine = typeof(IEnumerator).IsAssignableFrom(type) ||
                                         typeof(IAsyncStateMachine).IsAssignableFrom(type);
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
                            method.Name != "Prefix" && method.Name != "Postfix" && method.Name != "Finalizer") continue;
                        string? kind = null;
                        try
                        {
                            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                            if (isBehaviour && UnityCallbacks.Contains(method.Name) && IsVoidWithoutParameters(method))
                                kind = "Unity callback";
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

                        var descriptor = new Descriptor(assembly.GetName().Name ?? "", type.FullName + "." + method.Name);
                        _methods[method] = descriptor;
                        string status = "installed";
                        try
                        {
                            _harmony.Patch(method, prefix: prefix, postfix: postfix);
                            HookCount++;
                        }
                        catch (Exception ex)
                        {
                            _methods.Remove(method);
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

        private static bool HasAttribute(IList<CustomAttributeData> attributes, string name) =>
            attributes.Any(attribute => attribute.AttributeType.FullName == name);

        private static void Begin(out long __state)
        {
            CallbackProfiler? profiler = _active;
            __state = profiler != null && Thread.CurrentThread.ManagedThreadId == profiler._mainThreadId
                ? Stopwatch.GetTimestamp() : 0;
        }

        private static void End(MethodBase __originalMethod, long __state)
        {
            CallbackProfiler? profiler = _active;
            if (profiler == null || __state == 0 || !profiler._methods.TryGetValue(__originalMethod, out Descriptor descriptor)) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            if (ticks < 0) return;
            if (!profiler._samples.TryGetValue(__originalMethod, out Sample sample))
                profiler._samples.Add(__originalMethod, new Sample(descriptor, ticks));
            else
            {
                sample.Calls++;
                sample.TotalTicks += ticks;
                sample.MaxTicks = Math.Max(sample.MaxTicks, ticks);
            }
        }

        public void CaptureFrame(CaptureWriter writer, double elapsedMs, int frame, string scene, double frameMs, double thresholdMs)
        {
            if (frameMs >= thresholdMs)
            {
                var assemblies = new Dictionary<string, Sample>(StringComparer.Ordinal);
                foreach (Sample sample in _samples.Values)
                {
                    if (!assemblies.TryGetValue(sample.Descriptor.Assembly, out Sample total))
                        assemblies.Add(sample.Descriptor.Assembly, new Sample(new Descriptor(sample.Descriptor.Assembly, ""), sample.TotalTicks)
                        { Calls = sample.Calls, MaxTicks = sample.MaxTicks });
                    else
                    {
                        total.Calls += sample.Calls;
                        total.TotalTicks += sample.TotalTicks;
                        total.MaxTicks = Math.Max(total.MaxTicks, sample.MaxTicks);
                    }
                }
                foreach (Sample sample in assemblies.Values.OrderByDescending(s => s.TotalTicks).Take(8))
                    Write(writer, elapsedMs, frame, scene, frameMs, "assembly", sample);
                foreach (Sample sample in _samples.Values.OrderByDescending(s => s.TotalTicks).Take(20))
                    Write(writer, elapsedMs, frame, scene, frameMs, "method", sample);
            }
            _samples.Clear();
        }

        private static void Write(CaptureWriter writer, double elapsedMs, int frame, string scene, double frameMs,
            string scope, Sample sample)
        {
            writer.Callback(string.Join(",", CaptureWriter.Number(elapsedMs), frame.ToString(),
                CaptureWriter.Csv(scene), CaptureWriter.Number(frameMs), scope,
                CaptureWriter.Csv(sample.Descriptor.Assembly), CaptureWriter.Csv(sample.Descriptor.Callback),
                sample.Calls.ToString(), CaptureWriter.Number(sample.TotalTicks * 1000.0 / Stopwatch.Frequency),
                CaptureWriter.Number(sample.MaxTicks * 1000.0 / Stopwatch.Frequency)));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _active = null;
            _harmony.UnpatchSelf();
            _samples.Clear();
            _methods.Clear();
        }

        private sealed class Descriptor
        {
            public readonly string Assembly;
            public readonly string Callback;
            public Descriptor(string assembly, string callback) { Assembly = assembly; Callback = callback; }
        }

        private sealed class Sample
        {
            public readonly Descriptor Descriptor;
            public int Calls;
            public long TotalTicks;
            public long MaxTicks;
            public Sample(Descriptor descriptor, long ticks)
            {
                Descriptor = descriptor;
                Calls = 1;
                TotalTicks = MaxTicks = ticks;
            }
        }
    }
}
