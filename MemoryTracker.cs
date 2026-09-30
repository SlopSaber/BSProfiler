using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Unity.Profiling.Memory;
using UnityEngine.Profiling;

namespace BSProfiler
{
    internal sealed class MemoryTracker : IDisposable
    {
        private readonly CaptureWriter _writer;
        private readonly long _startTicks;
        private readonly DateTime _startUtc;
        private readonly string _requestPath;
        private Task<ProcessMemory>? _processTask;
        private ProcessMemory _process;
        private bool _hasProcess;
        private double _nextSample, _sceneStart;
        private string _scene = "", _processStatus = "not sampled";
        private int _sceneVisit, _gc0, _gc1, _gc2;
        private long _firstMono = -1, _firstPrivate = -1, _previousMono = -1, _previousPrivate = -1;
        private long _previousMenuMono = -1, _previousMenuPrivate = -1;
        private bool _menuCheckpoint;
        private double _snapshotDeadline;
        private string _snapshotPath = "", _snapshotScene = "";
        private int _snapshotFrame;
        private bool _snapshotTimeoutReported;
        private volatile bool _snapshotActive, _disposed;
        public long MonoUsed { get; private set; }
        public long UnityAllocated { get; private set; }
        public long WorkingSet => _hasProcess ? _process.WorkingSet : -1;
        public ModRetentionTracker Retention { get; }

        public MemoryTracker(CaptureWriter writer, long startTicks, DateTime startUtc)
        {
            _writer = writer; _startTicks = startTicks; _startUtc = startUtc;
            _requestPath = Path.Combine(Path.GetDirectoryName(writer.DirectoryPath)!, "memory-snapshot.request");
            Retention = new ModRetentionTracker(writer, startTicks);
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        }

        public void Tick(double elapsedMs, int frame, string scene)
        {
            if (_disposed) return;
            if (scene != _scene)
            {
                _scene = scene; _sceneVisit++; _sceneStart = elapsedMs; _menuCheckpoint = false;
            }
            if (_processTask != null && _processTask.IsCompleted)
            {
                try { _process = _processTask.GetAwaiter().GetResult(); _hasProcess = true; }
                catch (Exception ex) { _hasProcess = false; _processStatus = ex.GetType().Name; }
                _processTask = null;
                if (_hasProcess && _processStatus != _process.Status)
                {
                    _processStatus = _process.Status;
                    _writer.Event(CaptureWriter.Number(elapsedMs) + ",process-memory-counter," + CaptureWriter.Csv(_processStatus));
                }
            }
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            bool collected = gc0 != _gc0 || gc1 != _gc1 || gc2 != _gc2;
            if (elapsedMs >= _nextSample || collected)
            {
                bool periodic = elapsedMs >= _nextSample;
                if (periodic)
                {
                    _nextSample = elapsedMs + 1000;
                    if (_processTask == null) _processTask = Task.Run(ProcessMemory.Read);
                    if (_snapshotActive && !_snapshotTimeoutReported && elapsedMs >= _snapshotDeadline)
                    {
                        _snapshotTimeoutReported = true;
                        _writer.Snapshot(string.Join(",", CaptureWriter.Number(elapsedMs), _snapshotFrame.ToString(),
                            CaptureWriter.Csv(_snapshotScene), CaptureWriter.Csv(_snapshotPath), "timed-out,,",
                            CaptureWriter.Csv("No completion callback within 60 seconds; waiting without starting another capture")));
                    }
                }
                Sample(elapsedMs, frame, scene, collected ? "gc-observed" : "periodic", gc0, gc1, gc2);
                if (periodic && scene == "MainMenu" && elapsedMs - _sceneStart >= 10000)
                {
                    if (!_menuCheckpoint)
                    {
                        Sample(elapsedMs, frame, scene, "settled-menu", gc0, gc1, gc2);
                        _previousMenuMono = MonoUsed;
                        _previousMenuPrivate = _hasProcess ? _process.PrivateBytes : -1;
                        _menuCheckpoint = true;
                    }
                    CheckSnapshotRequest(elapsedMs, frame, scene);
                }
            }
            try { Retention.Tick(elapsedMs, frame, scene); }
            catch (Exception ex)
            {
                Retention.Dispose();
                _writer.Event(CaptureWriter.Number(elapsedMs) + ",retention-tracking-disabled," + CaptureWriter.Csv(ex.ToString()));
            }
        }

        private void Sample(double elapsedMs, int frame, string scene, string reason, int gc0, int gc1, int gc2)
        {
            MonoUsed = Profiler.GetMonoUsedSizeLong();
            long monoReserved = Profiler.GetMonoHeapSizeLong();
            UnityAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long unityReserved = Profiler.GetTotalReservedMemoryLong();
            long privateBytes = _hasProcess ? _process.PrivateBytes : -1;
            if (_firstMono < 0) _firstMono = MonoUsed;
            if (_firstPrivate < 0 && privateBytes >= 0) _firstPrivate = privateBytes;
            _writer.MemoryTrend(string.Join(",", CaptureWriter.Number(elapsedMs), CaptureWriter.Csv(_startUtc.AddMilliseconds(elapsedMs).ToString("O")),
                frame.ToString(), CaptureWriter.Csv(scene), _sceneVisit.ToString(), reason,
                MonoUsed.ToString(), monoReserved.ToString(), UnityAllocated.ToString(), unityReserved.ToString(),
                Value(WorkingSet), Value(privateBytes), Value(_hasProcess ? _process.PeakWorkingSet : -1),
                _hasProcess ? CaptureWriter.Number((Stopwatch.GetTimestamp() - _process.SampleTicks) * 1000.0 / Stopwatch.Frequency) : "",
                CaptureWriter.Csv(_processStatus), Delta(MonoUsed, _previousMono), Delta(MonoUsed, _firstMono),
                Delta(privateBytes, _previousPrivate), Delta(privateBytes, _firstPrivate),
                reason == "settled-menu" ? Delta(MonoUsed, _previousMenuMono) : "",
                reason == "settled-menu" ? Delta(privateBytes, _previousMenuPrivate) : "",
                gc0.ToString(), gc1.ToString(), gc2.ToString(), (gc0 - _gc0).ToString(), (gc1 - _gc1).ToString(), (gc2 - _gc2).ToString()));
            _previousMono = MonoUsed; _previousPrivate = privateBytes;
            _gc0 = gc0; _gc1 = gc1; _gc2 = gc2;
        }

        private void CheckSnapshotRequest(double elapsedMs, int frame, string scene)
        {
            if (_snapshotActive || !File.Exists(_requestPath)) return;
            string label;
            try
            {
                label = File.ReadAllText(_requestPath).Trim();
                File.Delete(_requestPath);
            }
            catch (Exception ex)
            {
                _writer.Event(CaptureWriter.Number(elapsedMs) + ",snapshot-request-error," + CaptureWriter.Csv(ex.Message));
                return;
            }
            // The label cannot become a path, and snapshots never replace earlier files.
            if (label.Length > 48) label = label.Substring(0, 48);
            char[] safeLabel = label.ToCharArray();
            for (int i = 0; i < safeLabel.Length; i++)
                if (!char.IsLetterOrDigit(safeLabel[i]) && safeLabel[i] != '-' && safeLabel[i] != '_') safeLabel[i] = '_';
            string path = Path.Combine(_writer.DirectoryPath, "memory-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + new string(safeLabel) + ".snap");
            long started = Stopwatch.GetTimestamp();
            _snapshotPath = path; _snapshotScene = scene; _snapshotFrame = frame;
            _snapshotDeadline = elapsedMs + 60000; _snapshotTimeoutReported = false;
            _snapshotActive = true;
            _writer.Snapshot(string.Join(",", CaptureWriter.Number(elapsedMs), frame.ToString(), CaptureWriter.Csv(scene),
                CaptureWriter.Csv(path), "requested,,,"));
            try
            {
                MemoryProfiler.TakeSnapshot(path, (file, success) =>
                {
                    _snapshotActive = false;
                    if (_disposed) return;
                    long bytes = -1;
                    try { if (File.Exists(file)) bytes = new FileInfo(file).Length; }
                    catch { }
                    _writer.Snapshot(string.Join(",", CaptureWriter.Number((Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency),
                        frame.ToString(), CaptureWriter.Csv(scene), CaptureWriter.Csv(file), success && bytes > 0 ? "complete" : "failed",
                        CaptureWriter.Number((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency), Value(bytes),
                        CaptureWriter.Csv(success && bytes > 0 ? "" : "Runtime snapshot unavailable or empty; no ownership conclusion")));
                }, CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects);
            }
            catch (Exception ex)
            {
                _snapshotActive = false;
                _writer.Snapshot(string.Join(",", CaptureWriter.Number(elapsedMs), frame.ToString(), CaptureWriter.Csv(scene),
                    CaptureWriter.Csv(path), "failed", CaptureWriter.Number((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency), "", CaptureWriter.Csv(ex.Message)));
            }
        }

        private static string Value(long value) => value < 0 ? "" : value.ToString();
        private static string Delta(long value, long baseline) => value < 0 || baseline < 0 ? "" : (value - baseline).ToString();
        public void Dispose() { _disposed = true; Retention.Dispose(); }
    }
}
