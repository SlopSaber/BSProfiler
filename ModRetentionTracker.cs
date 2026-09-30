using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine.Profiling;

namespace BSProfiler
{
    // Weak observations and direct fields are evidence of retention, not a heap walk or ownership total.
    internal sealed class ModRetentionTracker : IDisposable
    {
        private const int InstanceLimit = 8192, FieldLimit = 32;
        private readonly ConditionalWeakTable<object, Instance> _known = new ConditionalWeakTable<object, Instance>();
        private readonly ConcurrentQueue<Instance> _pending = new ConcurrentQueue<Instance>();
        private readonly List<Instance> _instances = new List<Instance>();
        private readonly Dictionary<Type, TypeSample> _types = new Dictionary<Type, TypeSample>();
        private readonly object _observeLock = new object();
        private readonly CaptureWriter _writer;
        private readonly long _startTicks;
        private int _instanceCount, _nextId, _dropped, _index, _sampleId, _sampleGc;
        private double _nextScan = 30000, _scanStart;
        private bool _scanning;
        private volatile bool _disposed;

        public ModRetentionTracker(CaptureWriter writer, long startTicks) { _writer = writer; _startTicks = startTicks; }

        public void Observe(object? instance, Assembly ownerAssembly)
        {
            if (_disposed || instance == null || instance.GetType().IsValueType || _known.TryGetValue(instance, out _)) return;
            // Do not assign a game-owned inherited receiver to a mod merely because its callback was patched.
            if (instance.GetType().Assembly != ownerAssembly) return;
            // Saturated receivers can run millions of times. Reject without metadata allocation or a lock.
            if (Volatile.Read(ref _instanceCount) >= InstanceLimit) { Interlocked.Increment(ref _dropped); return; }
            lock (_observeLock)
            {
                if (_disposed || _known.TryGetValue(instance, out _)) return;
                if (Volatile.Read(ref _instanceCount) >= InstanceLimit) { Interlocked.Increment(ref _dropped); return; }
                var tracked = new Instance(++_nextId, instance,
                    (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency, GC.CollectionCount(2));
                _known.Add(instance, tracked);
                Interlocked.Increment(ref _instanceCount);
                _pending.Enqueue(tracked);
            }
        }

        public void Tick(double elapsedMs, int frame, string scene)
        {
            if (_disposed) return;
            if (!_scanning)
            {
                if (elapsedMs < _nextScan) return;
                while (_pending.TryDequeue(out Instance instance)) _instances.Add(instance);
                foreach (TypeSample sample in _types.Values) sample.Reset();
                _index = 0; _sampleId++; _sampleGc = GC.CollectionCount(2); _scanStart = elapsedMs; _scanning = true;
                _writer.Event(CaptureWriter.Number(elapsedMs) + ",retention-scan-start," + CaptureWriter.Csv("sample=" + _sampleId));
            }
            long start = Stopwatch.GetTimestamp();
            int processed = 0;
            // Work is spread across frames; a single reflection/native query can exceed this soft budget.
            while (_index < _instances.Count && processed++ < 4)
            {
                Instance tracked = _instances[_index];
                object? target = tracked.Reference.Target;
                if (target == null)
                {
                    int last = _instances.Count - 1;
                    _instances[_index] = _instances[last]; _instances.RemoveAt(last);
                    Interlocked.Decrement(ref _instanceCount);
                }
                else
                {
                    Inspect(tracked, target, elapsedMs, frame, scene);
                    _index++;
                }
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= 0.5) break;
            }
            if (_index < _instances.Count) return;
            foreach (TypeSample sample in _types.Values)
            {
                _writer.ModMemory(string.Join(",", _sampleId.ToString(), CaptureWriter.Number(_scanStart), CaptureWriter.Number(elapsedMs),
                    frame.ToString(), CaptureWriter.Csv(scene), CaptureWriter.Csv(sample.Assembly), CaptureWriter.Csv(sample.Type.FullName),
                    sample.Alive.ToString(), sample.AfterGc.ToString(), sample.Destroyed.ToString(),
                    sample.PreviousAlive < 0 ? "" : (sample.Alive - sample.PreviousAlive).ToString(),
                    sample.Fields.Length.ToString(), sample.OmittedFields.ToString(), sample.FieldFailures.ToString(), Volatile.Read(ref _dropped).ToString()));
                sample.PreviousAlive = sample.Alive;
            }
            _writer.Event(CaptureWriter.Number(elapsedMs) + ",retention-scan-complete," + CaptureWriter.Csv(
                "sample=" + _sampleId + "; observed_instances=" + _instances.Count + "; dropped_observation_attempts=" + Volatile.Read(ref _dropped)));
            _scanning = false; _nextScan = elapsedMs + 30000;
        }

        private void Inspect(Instance tracked, object target, double elapsedMs, int frame, string scene)
        {
            Type type = target.GetType();
            if (!_types.TryGetValue(type, out TypeSample sample))
            {
                sample = new TypeSample(type); _types.Add(type, sample);
            }
            sample.Alive++;
            if (_sampleGc > tracked.FirstGc) sample.AfterGc++;
            if (target is UnityEngine.Object unity && !unity) sample.Destroyed++;
            if (tracked.PreviousCounts == null)
            {
                tracked.PreviousCounts = new long[sample.Fields.Length];
                for (int i = 0; i < tracked.PreviousCounts.Length; i++) tracked.PreviousCounts[i] = -1;
            }
            for (int i = 0; i < sample.Fields.Length; i++)
            {
                FieldInfo field = sample.Fields[i];
                try
                {
                    object? value = field.GetValue(target);
                    long count = -1, payload = -1, native = -1;
                    string kind;
                    if (value == null)
                    {
                        if (tracked.PreviousCounts[i] == -1) continue;
                        kind = "null"; count = 0; payload = 0;
                    }
                    else if (value is string text) { kind = "string"; count = text.Length; payload = count * 2; }
                    else if (value is Array array)
                    {
                        kind = "array"; count = array.LongLength;
                        if (array.GetType().GetElementType()!.IsPrimitive)
                            try { payload = Buffer.ByteLength(array); } catch (ArgumentException) { }
                    }
                    else if (value is UnityEngine.Object asset)
                    {
                        kind = asset ? "unity-object" : "destroyed-unity-shell";
                        if (asset) { long bytes = Profiler.GetRuntimeMemorySizeLong(asset); if (bytes > 0) native = bytes; }
                    }
                    else
                    {
                        Type valueType = value.GetType();
                        // Inspect BCL containers only; do not invoke arbitrary mod properties/enumerators.
                        if (valueType.Assembly != typeof(List<>).Assembly && valueType.Assembly != typeof(HashSet<>).Assembly) continue;
                        if (valueType.Namespace == "System.Collections.Concurrent") continue;
                        if (value is ICollection collection) { kind = "collection"; count = collection.Count; }
                        else if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(HashSet<>))
                        { kind = "collection"; count = (int)valueType.GetProperty("Count")!.GetValue(value, null)!; }
                        else continue;
                    }
                    long previous = tracked.PreviousCounts[i];
                    _writer.ModField(string.Join(",", _sampleId.ToString(), CaptureWriter.Number(elapsedMs), frame.ToString(),
                        CaptureWriter.Csv(scene), CaptureWriter.Csv(sample.Assembly), CaptureWriter.Csv(type.FullName), tracked.Id.ToString(),
                        CaptureWriter.Number(tracked.FirstObserved), (_sampleGc > tracked.FirstGc ? "1" : "0"),
                        CaptureWriter.Csv(field.DeclaringType?.FullName + "." + field.Name), CaptureWriter.Csv(value?.GetType().FullName), kind,
                        count < 0 ? "" : count.ToString(), count < 0 || previous < 0 ? "" : (count - previous).ToString(),
                        payload < 0 ? "" : payload.ToString(), native < 0 ? "" : native.ToString()));
                    tracked.PreviousCounts[i] = count < 0 ? -2 : count;
                }
                catch { sample.FieldFailures++; } // A field/object may change concurrently; keep coverage visible.
            }
        }

        public void Dispose() { _disposed = true; }

        private sealed class Instance
        {
            public readonly int Id, FirstGc;
            public readonly double FirstObserved;
            public readonly WeakReference Reference;
            public long[]? PreviousCounts;
            public Instance(int id, object target, double firstObserved, int gc)
            { Id = id; Reference = new WeakReference(target); FirstObserved = firstObserved; FirstGc = gc; }
        }

        private sealed class TypeSample
        {
            public readonly Type Type;
            public readonly string Assembly;
            public readonly FieldInfo[] Fields;
            public readonly int OmittedFields;
            public int Alive, AfterGc, Destroyed, FieldFailures, PreviousAlive = -1;
            public TypeSample(Type type)
            {
                Type = type; Assembly = type.Assembly.GetName().Name ?? "";
                var fields = new List<FieldInfo>();
                for (Type? current = type; current != null && current.Assembly == type.Assembly; current = current.BaseType)
                    foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (field.FieldType.IsValueType) continue;
                        if (fields.Count < FieldLimit) fields.Add(field); else OmittedFields++;
                    }
                Fields = fields.ToArray();
            }
            public void Reset() { Alive = AfterGc = Destroyed = FieldFailures = 0; }
        }
    }
}
