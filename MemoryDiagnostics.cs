using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace BSProfiler
{
    internal sealed class MemoryDiagnostics : IDisposable
    {
        private static string _allocationCounterStatus = "Not initialized";
        private static readonly Func<long>? AllocationCounter = CreateAllocationCounter();
        private static MemoryDiagnostics? _active;
        [ThreadStatic] private static int _depth;
        private readonly Harmony _harmony = new Harmony("BSProfiler.memory-diagnostics");
        private readonly CaptureWriter _writer;
        private readonly long _startTicks;
        private int _frame;
        private string _scene = "";
        private bool _disposed;
        public int HookCount { get; private set; }
        public int FailedCount { get; private set; }

        public static bool AllocationCounterAvailable => AllocationCounter != null;
        public static string AllocationCounterStatus => _allocationCounterStatus;
        public static long AllocatedBytes() => AllocationCounter == null ? -1 : AllocationCounter();

        private static Func<long>? CreateAllocationCounter()
        {
            try
            {
                MethodInfo? method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
                if (method == null)
                {
                    _allocationCounterStatus = "API absent";
                    return null;
                }
                var counter = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                long before = counter();
                byte[] probe = new byte[4096];
                long after = counter();
                GC.KeepAlive(probe);
                if (after - before < probe.Length)
                {
                    _allocationCounterStatus = "API did not count a 4096-byte startup allocation; readings unavailable";
                    return null;
                }
                _allocationCounterStatus = "Startup allocation observed";
                return counter;
            }
            catch (Exception ex)
            {
                _allocationCounterStatus = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        public MemoryDiagnostics(CaptureWriter writer, long startTicks)
        {
            _writer = writer;
            _startTicks = startTicks;
            _active = this;
            try { Install(); }
            catch { Dispose(); throw; }
        }

        private void Install()
        {
            var catalog = new List<string> { "operation,status" };
            var prefix = new HarmonyMethod(typeof(MemoryDiagnostics).GetMethod(nameof(Begin), BindingFlags.Static | BindingFlags.NonPublic));
            var finalizer = new HarmonyMethod(typeof(MemoryDiagnostics).GetMethod(nameof(End), BindingFlags.Static | BindingFlags.NonPublic));
            var methods = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(GC).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (method.Name == "Collect" && method.GetMethodBody() != null) methods.Add(method);
            MethodInfo? unload = typeof(Resources).GetMethod(nameof(Resources.UnloadUnusedAssets), Type.EmptyTypes);
            if (unload != null && unload.GetMethodBody() != null) methods.Add(unload);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "CustomJSONData") continue;
                foreach (string name in new[] { "Version2_6_0AndEarlierCustomBeatmapSaveData", "Version3CustomBeatmapSaveData" })
                {
                    Type? type = assembly.GetType("CustomJSONData.CustomBeatmap." + name);
                    if (type == null) continue;
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (method.Name == "Deserialize" && method.GetMethodBody() != null) methods.Add(method);
                }
            }
            foreach (MethodInfo method in methods)
            {
                string status = "installed";
                try
                {
                    _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    HookCount++;
                }
                catch (Exception ex)
                {
                    FailedCount++;
                    status = ex.GetType().Name + ": " + ex.Message;
                }
                catalog.Add(CaptureWriter.Csv(method.DeclaringType?.FullName + "." + method) + "," + CaptureWriter.Csv(status));
            }
            File.WriteAllLines(Path.Combine(_writer.DirectoryPath, "memory-hooks.csv"), catalog);
        }

        public void SetFrame(int frame, string scene)
        {
            Volatile.Write(ref _frame, frame);
            Volatile.Write(ref _scene, scene);
        }

        private static void Begin(out OperationState __state)
        {
            __state = default;
            if (_active == null) return;
            __state.Entered = true;
            if (_depth++ != 0) return;
            try
            {
                __state.Frame = Volatile.Read(ref _active._frame);
                __state.Scene = Volatile.Read(ref _active._scene);
                __state.Stack = new StackTrace(2, false).ToString();
                if (__state.Stack.Length > 12000) __state.Stack = __state.Stack.Substring(0, 12000);
                __state.Gc0 = GC.CollectionCount(0);
                __state.Gc1 = GC.CollectionCount(1);
                __state.Gc2 = GC.CollectionCount(2);
                __state.Ticks = Stopwatch.GetTimestamp();
            }
            catch { } // Diagnostics must not prevent a requested collection.
        }

        private static void End(MethodBase __originalMethod, OperationState __state, Exception? __exception)
        {
            if (!__state.Entered) return;
            _depth--;
            MemoryDiagnostics? active = _active;
            if (active == null || __state.Ticks == 0) return;
            long now = Stopwatch.GetTimestamp();
            try
            {
                active._writer.Memory(string.Join(",", CaptureWriter.Number((now - active._startTicks) * 1000.0 / Stopwatch.Frequency),
                    Volatile.Read(ref active._frame).ToString(), CaptureWriter.Csv(Volatile.Read(ref active._scene)),
                    Thread.CurrentThread.ManagedThreadId.ToString(), CaptureWriter.Csv(__originalMethod.DeclaringType?.FullName + "." + __originalMethod),
                    CaptureWriter.Number((now - __state.Ticks) * 1000.0 / Stopwatch.Frequency),
                    (GC.CollectionCount(0) - __state.Gc0).ToString(), (GC.CollectionCount(1) - __state.Gc1).ToString(),
                    (GC.CollectionCount(2) - __state.Gc2).ToString(), CaptureWriter.Csv(__exception?.GetType().FullName), CaptureWriter.Csv(__state.Stack),
                    CaptureWriter.Number((__state.Ticks - active._startTicks) * 1000.0 / Stopwatch.Frequency), __state.Frame.ToString(),
                    CaptureWriter.Csv(__state.Scene), __originalMethod.DeclaringType?.Assembly.GetName().Name == "CustomJSONData" ? "json-parser" : "explicit-memory-operation"));
            }
            catch { } // Preserve the original operation and any original exception.
        }

        public void Dispose() => Stop(false);

        public void Stop(bool processQuitting)
        {
            if (_disposed) return;
            _disposed = true;
            _active = null;
            try { if (!processQuitting) _harmony.UnpatchSelf(); }
            catch (Exception ex) { Plugin.Log?.Error("BSProfiler memory hook cleanup failed: " + ex); }
        }

        private struct OperationState
        {
            public bool Entered;
            public long Ticks;
            public int Gc0, Gc1, Gc2;
            public string? Stack;
            public int Frame;
            public string? Scene;
        }
    }
}
