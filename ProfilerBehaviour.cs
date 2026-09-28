using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using IPA.Loader;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;

namespace BSProfiler
{
    internal sealed class ProfilerBehaviour : MonoBehaviour
    {
        private static readonly HashSet<string> WantedMarkers = new HashSet<string>(StringComparer.Ordinal)
        {
            "GC Allocated In Frame", "GC Used Memory", "System Used Memory",
            "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count",
            "PlayerLoop", "BehaviourUpdate", "ScriptRunBehaviourUpdate", "ScriptRunBehaviourLateUpdate",
            "ScriptRunBehaviourFixedUpdate", "ScriptRunDelayedTasks", "Camera.Render",
            "RenderPipelineManager.DoRenderLoop_Internal", "Physics.Simulate", "Physics.ProcessReports",
            "Canvas.BuildBatch", "UI.CanvasUpdate", "Gfx.WaitForPresentOnGfxThread",
            "Gfx.WaitForRenderThread", "WaitForTargetFPS", "GC.Collect", "XR.WaitForGPU"
        };

        private readonly List<Metric> _metrics = new List<Metric>();
        private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();
        private readonly StringBuilder _frameLine = new StringBuilder(768);
        private readonly double[] _window = new double[4096];
        private ProfilerRecorder _cpuRecorder;
        private ProfilerRecorder _gpuRecorder;
        private CaptureWriter? _writer;
        private Process? _process;
        private XRDisplaySubsystem? _display;
        private long _startTicks;
        private long _lastFrameTicks;
        private double _nextSystemSampleMs;
        private double _nextSummaryMs = 10000;
        private string _scene = "";
        private string _lastWriterError = "";
        private long _monoUsedBytes;
        private long _totalAllocatedBytes;
        private long _workingSetBytes;
        private float _refreshHz = 90f;
        private int _windowUsed;
        private int _windowFrames;
        private int _windowSlowFrames;
        private double _windowSum;
        private double _windowWorst;
        private double _windowCpuSum;
        private int _windowCpuCount;
        private double _windowGpuSum;
        private int _windowGpuCount;
        private readonly int[] _lastGcCounts = new int[3];
        private int _incidentSlowFrames;
        private double _incidentStartMs;
        private double _incidentLastSlowMs;
        private double _incidentPeakMs;
        private double _incidentPeakCpuMs;
        private double _incidentPeakGpuMs;
        private long _incidentPeakGcBytes;
        private double _incidentThresholdMs;
        private int _incidentPeakFrame;
        private string _incidentScene = "";
        private bool _stopped;

        private void Awake()
        {
            try
            {
                _process = Process.GetCurrentProcess();
                _startTicks = Stopwatch.GetTimestamp();
                _lastFrameTicks = 0;
                _scene = SceneManager.GetActiveScene().name;
                string root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
                string directory = Path.Combine(root, "UserData", "BSProfiler", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + _process.Id);
                Directory.CreateDirectory(directory);
                DiscoverMetrics(directory);
                _cpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
                _gpuRecorder = new ProfilerRecorder("GPU Frame Time", options: ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                WriteSession(directory);
                _writer = new CaptureWriter(directory, FrameHeader());
                for (int generation = 0; generation < 3; generation++)
                    _lastGcCounts[generation] = GC.CollectionCount(generation);
                SceneManager.activeSceneChanged += SceneChanged;
                Application.logMessageReceivedThreaded += LogReceived;
                Plugin.Log?.Info("BSProfiler capture: " + directory);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("BSProfiler could not start: " + ex);
                StopCapture();
                enabled = false;
            }
        }

        private void DiscoverMetrics(string directory)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var catalog = new StringBuilder("category,name,unit,flags\n");
            foreach (ProfilerRecorderHandle handle in handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                string name = description.Name;
                catalog.Append(CaptureWriter.Csv(description.Category.ToString())).Append(',')
                    .Append(CaptureWriter.Csv(name)).Append(',')
                    .Append(CaptureWriter.Csv(description.UnitType.ToString())).Append(',')
                    .Append(CaptureWriter.Csv(description.Flags.ToString())).AppendLine();
                if (!WantedMarkers.Contains(name) || _metrics.Any(metric => metric.Name == name))
                    continue;
                var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                if (recorder.Valid)
                    _metrics.Add(new Metric(name, description.UnitType.ToString(), recorder));
                else
                    recorder.Dispose();
            }
            File.WriteAllText(Path.Combine(directory, "available-markers.csv"), catalog.ToString(), new UTF8Encoding(false));
        }

        private void WriteSession(string directory)
        {
            var session = new StringBuilder();
            session.AppendLine("BSProfiler 0.1.0");
            session.AppendLine("UTC start: " + DateTime.UtcNow.ToString("O"));
            session.AppendLine("Game version: " + Application.version);
            session.AppendLine("Unity version: " + Application.unityVersion);
            session.AppendLine("CPU: " + SystemInfo.processorType + " (" + SystemInfo.processorCount + " logical)");
            session.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType);
            session.AppendLine("System memory MB: " + SystemInfo.systemMemorySize);
            session.AppendLine("Graphics memory MB: " + SystemInfo.graphicsMemorySize);
            session.AppendLine("Resolution: " + Screen.width + "x" + Screen.height);
            session.AppendLine("Quality level: " + QualitySettings.GetQualityLevel());
            session.AppendLine("VSync count: " + QualitySettings.vSyncCount);
            session.AppendLine("Target frame rate: " + Application.targetFrameRate);
            session.AppendLine("CPU main recorder: " + _cpuRecorder.Valid);
            session.AppendLine("GPU span recorder: " + _gpuRecorder.Valid);
            session.AppendLine("Tracked markers: " + string.Join(", ", _metrics.Select(metric => metric.Name)));
            session.AppendLine("Loaded plugins:");
            foreach (var metadata in PluginManager.EnabledPlugins.OrderBy(metadata => metadata.Id, StringComparer.OrdinalIgnoreCase))
                session.AppendLine("  " + metadata.Id + " | " + metadata.Name + " | " + metadata.HVersion);
            File.WriteAllText(Path.Combine(directory, "session.txt"), session.ToString(), new UTF8Encoding(false));
        }

        private string FrameHeader()
        {
            var line = new StringBuilder("elapsed_ms,frame,scene,focused,frame_ms,fps,refresh_hz,budget_ms,cpu_main_ms,gpu_span_ms,xr_app_gpu_ms,xr_compositor_gpu_ms,xr_dropped_frames,xr_present_count,xr_motion_to_photon_ms,mono_used_bytes,total_allocated_bytes,working_set_bytes,gc_gen0,gc_gen1,gc_gen2");
            foreach (Metric metric in _metrics)
                line.Append(',').Append(CaptureWriter.Csv(metric.Name + " [" + metric.Unit + "]"));
            return line.ToString();
        }

        private void Update()
        {
            if (_writer == null) return;
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsedMs = (nowTicks - _startTicks) * 1000.0 / Stopwatch.Frequency;
            double frameMs = _lastFrameTicks == 0 ? double.NaN : (nowTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTicks = nowTicks;

            if (elapsedMs >= _nextSystemSampleMs)
            {
                _nextSystemSampleMs = elapsedMs + 1000;
                _monoUsedBytes = Profiler.GetMonoUsedSizeLong();
                _totalAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
                _process?.Refresh();
                _workingSetBytes = _process?.WorkingSet64 ?? 0;
                RefreshDisplay();
            }

            double cpuMs = ReadMilliseconds(ref _cpuRecorder);
            double gpuMs = ReadMilliseconds(ref _gpuRecorder);
            double xrAppGpuMs = double.NaN;
            double xrCompositorGpuMs = double.NaN;
            int xrDroppedFrames = -1;
            int xrPresentCount = -1;
            double xrMotionToPhotonMs = double.NaN;
            if (_display != null && _display.running)
            {
                if (_display.TryGetAppGPUTimeLastFrame(out float appGpuSeconds) && appGpuSeconds >= 0 && appGpuSeconds < 0.5f)
                    xrAppGpuMs = appGpuSeconds * 1000.0;
                if (_display.TryGetCompositorGPUTimeLastFrame(out float compositorGpuSeconds) && compositorGpuSeconds >= 0 && compositorGpuSeconds < 0.5f)
                    xrCompositorGpuMs = compositorGpuSeconds * 1000.0;
                if (_display.TryGetDroppedFrameCount(out int dropped)) xrDroppedFrames = dropped;
                if (_display.TryGetFramePresentCount(out int presented)) xrPresentCount = presented;
                if (_display.TryGetMotionToPhoton(out float latencySeconds) && latencySeconds >= 0 && latencySeconds < 0.5f)
                    xrMotionToPhotonMs = latencySeconds * 1000.0;
            }
            double gcBytes = double.NaN;
            _frameLine.Clear();
            double budgetMs = 1000.0 / _refreshHz;
            _frameLine.Append(CaptureWriter.Number(elapsedMs)).Append(',').Append(Time.frameCount).Append(',')
                .Append(CaptureWriter.Csv(_scene)).Append(',').Append(Application.isFocused ? '1' : '0').Append(',')
                .Append(CaptureWriter.Number(frameMs)).Append(',')
                .Append(CaptureWriter.Number(1000.0 / frameMs)).Append(',')
                .Append(CaptureWriter.Number(_refreshHz)).Append(',')
                .Append(CaptureWriter.Number(budgetMs)).Append(',')
                .Append(CaptureWriter.Number(cpuMs)).Append(',')
                .Append(CaptureWriter.Number(gpuMs)).Append(',')
                .Append(CaptureWriter.Number(xrAppGpuMs)).Append(',')
                .Append(CaptureWriter.Number(xrCompositorGpuMs)).Append(',')
                .Append(xrDroppedFrames < 0 ? "" : xrDroppedFrames.ToString()).Append(',')
                .Append(xrPresentCount < 0 ? "" : xrPresentCount.ToString()).Append(',')
                .Append(CaptureWriter.Number(xrMotionToPhotonMs)).Append(',')
                .Append(_monoUsedBytes).Append(',').Append(_totalAllocatedBytes).Append(',').Append(_workingSetBytes).Append(',')
                .Append(GC.CollectionCount(0)).Append(',').Append(GC.CollectionCount(1)).Append(',').Append(GC.CollectionCount(2));
            foreach (Metric metric in _metrics)
            {
                long value = metric.Read();
                _frameLine.Append(',');
                if (value >= 0) _frameLine.Append(value);
                if (metric.Name == "GC Allocated In Frame" && value >= 0) gcBytes = value;
            }
            _writer.Frame(_frameLine.ToString());

            if (!double.IsNaN(frameMs))
                UpdateStatistics(elapsedMs, frameMs, cpuMs, gpuMs, gcBytes, budgetMs);
            if (_writer.Failure != null && _lastWriterError.Length == 0)
            {
                _lastWriterError = _writer.Failure.ToString();
                Plugin.Log?.Error("BSProfiler writer failed: " + _lastWriterError);
            }
        }

        private void RefreshDisplay()
        {
            _displays.Clear();
            SubsystemManager.GetSubsystems(_displays);
            _display = null;
            foreach (XRDisplaySubsystem display in _displays)
            {
                if (!display.running) continue;
                _display = display;
                if (display.TryGetDisplayRefreshRate(out float hz) && hz > 0)
                {
                    _refreshHz = hz;
                    return;
                }
            }
            double desktopHz = Screen.currentResolution.refreshRateRatio.value;
            if (desktopHz > 0) _refreshHz = (float)desktopHz;
        }

        private static double ReadMilliseconds(ref ProfilerRecorder recorder) => recorder.Valid && recorder.Count > 0
            ? recorder.LastValue * 0.000001
            : double.NaN;

        private void UpdateStatistics(double elapsedMs, double frameMs, double cpuMs, double gpuMs, double gcBytes, double budgetMs)
        {
            double thresholdMs = Math.Max(12, budgetMs * 1.5);
            bool slow = frameMs >= thresholdMs;
            _windowFrames++;
            _windowSum += frameMs;
            _windowWorst = Math.Max(_windowWorst, frameMs);
            if (_windowUsed < _window.Length) _window[_windowUsed++] = frameMs;
            if (slow) _windowSlowFrames++;
            if (!double.IsNaN(cpuMs)) { _windowCpuSum += cpuMs; _windowCpuCount++; }
            if (!double.IsNaN(gpuMs)) { _windowGpuSum += gpuMs; _windowGpuCount++; }

            if (slow)
            {
                if (_incidentSlowFrames == 0)
                {
                    _incidentStartMs = elapsedMs;
                    _incidentScene = _scene;
                    _incidentPeakMs = 0;
                }
                _incidentSlowFrames++;
                _incidentLastSlowMs = elapsedMs;
                if (frameMs > _incidentPeakMs)
                {
                    _incidentPeakMs = frameMs;
                    _incidentPeakCpuMs = cpuMs;
                    _incidentPeakGpuMs = gpuMs;
                    _incidentPeakGcBytes = double.IsNaN(gcBytes) ? -1 : (long)gcBytes;
                    _incidentThresholdMs = thresholdMs;
                    _incidentPeakFrame = Time.frameCount;
                }
            }
            else if (_incidentSlowFrames > 0 && elapsedMs - _incidentLastSlowMs >= 1000)
                CloseIncident();

            if (elapsedMs >= _nextSummaryMs)
            {
                WriteSummary(elapsedMs);
                _nextSummaryMs = elapsedMs + 10000;
            }
        }

        private void CloseIncident()
        {
            if (_writer == null || _incidentSlowFrames == 0) return;
            _writer.Incident(string.Join(",", CaptureWriter.Number(_incidentStartMs), CaptureWriter.Number(_incidentLastSlowMs),
                CaptureWriter.Csv(_incidentScene), _incidentSlowFrames.ToString(), _incidentPeakFrame.ToString(),
                CaptureWriter.Number(_incidentPeakMs), CaptureWriter.Number(_incidentPeakCpuMs),
                CaptureWriter.Number(_incidentPeakGpuMs), _incidentPeakGcBytes < 0 ? "" : _incidentPeakGcBytes.ToString(),
                CaptureWriter.Number(_incidentThresholdMs)));
            _incidentSlowFrames = 0;
        }

        private void WriteSummary(double elapsedMs)
        {
            if (_writer == null || _windowFrames == 0) return;
            Array.Sort(_window, 0, _windowUsed);
            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            _writer.Summary(string.Join(",", CaptureWriter.Number(elapsedMs), CaptureWriter.Csv(_scene),
                _windowFrames.ToString(), CaptureWriter.Number(_windowSum / _windowFrames),
                Percentile(0.50), Percentile(0.95), Percentile(0.99), CaptureWriter.Number(_windowWorst),
                _windowSlowFrames.ToString(), CaptureWriter.Number(_windowCpuCount == 0 ? double.NaN : _windowCpuSum / _windowCpuCount),
                CaptureWriter.Number(_windowGpuCount == 0 ? double.NaN : _windowGpuSum / _windowGpuCount),
                (gc0 - _lastGcCounts[0]).ToString(), (gc1 - _lastGcCounts[1]).ToString(),
                (gc2 - _lastGcCounts[2]).ToString(), _writer.DroppedRecords.ToString()));
            _lastGcCounts[0] = gc0;
            _lastGcCounts[1] = gc1;
            _lastGcCounts[2] = gc2;
            _windowUsed = _windowFrames = _windowSlowFrames = 0;
            _windowSum = _windowWorst = _windowCpuSum = _windowGpuSum = 0;
            _windowCpuCount = _windowGpuCount = 0;
        }

        private string Percentile(double quantile) => _windowUsed == 0 ? "" :
            CaptureWriter.Number(_window[Math.Min(_windowUsed - 1, (int)Math.Ceiling(_windowUsed * quantile) - 1)]);

        private void SceneChanged(Scene previous, Scene current)
        {
            _scene = current.name;
            _writer?.Event(CaptureWriter.Number(ElapsedMs()) + ",scene," + CaptureWriter.Csv(previous.name + " -> " + current.name));
        }

        private void LogReceived(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log) return;
            string detail = message + (string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace);
            if (detail.Length > 6000) detail = detail.Substring(0, 6000);
            _writer?.Event(CaptureWriter.Number(ElapsedMs()) + "," + CaptureWriter.Csv(type.ToString()) + "," + CaptureWriter.Csv(detail));
        }

        private double ElapsedMs() => (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;

        public void StopCapture()
        {
            if (_stopped) return;
            _stopped = true;
            SceneManager.activeSceneChanged -= SceneChanged;
            Application.logMessageReceivedThreaded -= LogReceived;
            CloseIncident();
            WriteSummary(ElapsedMs());
            _writer?.Event(CaptureWriter.Number(ElapsedMs()) + ",stop," + CaptureWriter.Csv("Capture stopped"));
            _writer?.Dispose();
            _writer = null;
            _cpuRecorder.Dispose();
            _gpuRecorder.Dispose();
            foreach (Metric metric in _metrics) metric.Dispose();
            _metrics.Clear();
            _process?.Dispose();
            _process = null;
        }

        private void OnDestroy() => StopCapture();

        private sealed class Metric : IDisposable
        {
            public string Name { get; }
            public string Unit { get; }
            private ProfilerRecorder _recorder;

            public Metric(string name, string unit, ProfilerRecorder recorder)
            {
                Name = name;
                Unit = unit;
                _recorder = recorder;
            }

            public long Read() => _recorder.Valid && _recorder.Count > 0 ? _recorder.LastValue : -1;
            public void Dispose() => _recorder.Dispose();
        }
    }
}
