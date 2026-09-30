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
            "GC Allocated In Frame", "GC Used Memory", "GC Reserved Memory", "System Used Memory",
            "Profiler Used Memory", "CPU Total Frame Time", "CPU Render Thread Frame Time",
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
        private CallbackProfiler? _callbackProfiler;
        private MemoryDiagnostics? _memoryDiagnostics;
        private MemoryTracker? _memoryTracker;
        private Process? _process;
        private XRDisplaySubsystem? _display;
        private long _startTicks;
        private DateTime _startUtc;
        private long _lastFrameTicks;
        private long _lastLateTicks;
        private long _lastAllocatedBytes = -1;
        private readonly int[] _frameGcCounts = new int[3];
        private double _previousProfilerMs;
        private long _previousProfilerBytes = -1;
        private double _nextSystemSampleMs;
        private double _nextSummaryMs = 10000;
        private string _scene = "";
        private string _lastWriterError = "";
        private long _monoUsedBytes;
        private long _totalAllocatedBytes;
        private long _workingSetBytes = -1;
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
        private string? _captureDirectory;
        private readonly List<string> _startupEvents = new List<string>();

        private void Awake()
        {
            try
            {
                _process = Process.GetCurrentProcess();
                _startUtc = DateTime.UtcNow;
                _startTicks = Stopwatch.GetTimestamp();
                _lastFrameTicks = 0;
                _scene = SceneManager.GetActiveScene().name;
                string root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
                string directory = Path.Combine(root, "UserData", "BSProfiler", _startUtc.ToString("yyyyMMdd-HHmmss") + "-" + _process.Id);
                Directory.CreateDirectory(directory);
                _captureDirectory = directory;
                Plugin.Log?.Info("BSProfiler bootstrap started");
                StartupPhase("recorder-discovery", () => DiscoverMetrics(directory));
                StartupPhase("frame-recorders", () =>
                {
                    _cpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
                    _gpuRecorder = new ProfilerRecorder("GPU Frame Time", options: ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                });
                StartupPhase("capture-writer", () => _writer = new CaptureWriter(directory, FrameHeader()));
                StartupPhase("memory-tracker", () => _memoryTracker = new MemoryTracker(_writer!, _startTicks, _startUtc));
                StartupPhase("callback-setup", () => _callbackProfiler = new CallbackProfiler(directory, root, _writer!, _startTicks, _memoryTracker!.Retention));
                StartupPhase("memory-parser-hooks", () => _memoryDiagnostics = new MemoryDiagnostics(_writer!, _startTicks));
                foreach (string row in _startupEvents) _writer!.Event(row);
                _startupEvents.Clear();
                WriteSession(directory);
                Plugin.Log?.Info("BSProfiler callback installation scheduled; startup callback coverage is partial until completion");
                Plugin.Log?.Info("BSProfiler memory hooks: " + _memoryDiagnostics!.HookCount + " installed, " + _memoryDiagnostics.FailedCount + " failed");
                if (!MemoryDiagnostics.AllocationCounterAvailable)
                    Plugin.Log?.Warn("BSProfiler allocation readings unavailable: " + MemoryDiagnostics.AllocationCounterStatus);
                for (int generation = 0; generation < 3; generation++)
                {
                    _lastGcCounts[generation] = GC.CollectionCount(generation);
                    _frameGcCounts[generation] = _lastGcCounts[generation];
                }
                SceneManager.activeSceneChanged += SceneChanged;
                SceneManager.sceneLoaded += SceneLoaded;
                SceneManager.sceneUnloaded += SceneUnloaded;
                Application.logMessageReceivedThreaded += LogReceived;
                Plugin.Log?.Info("BSProfiler capture: " + directory);
                StartCoroutine(_callbackProfiler!.InstallRoutine(() =>
                {
                    if (_stopped) return;
                    WriteSession(directory);
                    Plugin.Log?.Info("BSProfiler callback hooks: " + _callbackProfiler.HookCount + " installed, " +
                        _callbackProfiler.FailedCount + " failed; complete=" + _callbackProfiler.InstallationComplete);
                }));
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("BSProfiler could not start: " + ex);
                StopCapture();
                enabled = false;
            }
        }

        private void StartupPhase(string name, Action work)
        {
            long start = Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            try { work(); }
            finally
            {
                long end = Stopwatch.GetTimestamp();
                _startupEvents.Add(CaptureWriter.Number((end - _startTicks) * 1000.0 / Stopwatch.Frequency) +
                    ",startup-phase," + CaptureWriter.Csv(name + "; start_ms=" + CaptureWriter.Number((start - _startTicks) * 1000.0 / Stopwatch.Frequency) +
                    "; work_ms=" + CaptureWriter.Number((end - start) * 1000.0 / Stopwatch.Frequency) +
                    "; managed_heap_delta_bytes=" + (UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - heap) +
                    "; gc_delta=" + (GC.CollectionCount(0) - gc0) + "/" + (GC.CollectionCount(1) - gc1) + "/" + (GC.CollectionCount(2) - gc2)));
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
            session.AppendLine("UTC start: " + _startUtc.ToString("O"));
            session.AppendLine("Capture schema: 6");
            if (_process != null)
            {
                try
                {
                    DateTime processStart = _process.StartTime.ToUniversalTime();
                    session.AppendLine("Process UTC start: " + processStart.ToString("O"));
                    session.AppendLine("Process start to capture ms: " + CaptureWriter.Number((_startUtc - processStart).TotalMilliseconds));
                }
                catch (Exception ex) { session.AppendLine("Process start unavailable: " + ex.GetType().Name); }
            }
            session.AppendLine("Callback installation complete: " + (_callbackProfiler?.InstallationComplete ?? false));
            session.AppendLine("Callback setup is incremental on the main thread, soft 2 ms work slices; individual discovery/patch operations can exceed the slice. Callback startup coverage is partial until callback-install-finished. Early process/IPA loading before capture requires game logs or an external trace. events.csv records startup phases, per-assembly discovery/patch timing and completion.");
            session.AppendLine("Main thread ID: " + System.Threading.Thread.CurrentThread.ManagedThreadId);
            session.AppendLine("Memory operations include CustomJSONData v2/v3 top-level parser spans on their actual thread, with start/end elapsed bounds and most recently observed frame/scene. Nested parser/memory hooks on the same thread are suppressed. GC overlap remains unattributed.");
            session.AppendLine("BSProfiler module MVID: " + typeof(ProfilerBehaviour).Assembly.ManifestModule.ModuleVersionId);
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
            session.AppendLine("Callback hooks: " + (_callbackProfiler?.HookCount ?? 0));
            session.AppendLine("Callback hook failures: " + (_callbackProfiler?.FailedCount ?? 0));
            session.AppendLine("General delegate/UI hooks: " + (_callbackProfiler?.ExtraHooks ?? 0) +
                "; omitted by limits: " + (_callbackProfiler?.ExtraHooksOmitted ?? 0) +
                "; discovery failures: " + (_callbackProfiler?.DiscoveryFailures ?? 0));
            session.AppendLine("Plugin delegate targets discovered from managed IL; Harmony/MonoMod/Cecil internals excluded; named Handle/On/Refresh methods also covered. Max 256 extra hooks per assembly / 2048 overall, depth 128; omissions in catalog. Runtime subscriptions to existing targets covered; generic library delegates excluded; late-loaded assemblies not rediscovered.");
            session.AppendLine("self_ms excludes hooked descendant spans and their measured diagnostics, but includes unhooked descendants/native work and overlapping GC. GC overlap is not allocation-caller attribution. Hook overhead excludes background writer and unsupported nested overflow.");
            session.AppendLine("Per-thread allocation counter: " + MemoryDiagnostics.AllocationCounterAvailable);
            session.AppendLine("Allocation counter capability: " + MemoryDiagnostics.AllocationCounterStatus);
            session.AppendLine("Memory hooks: " + (_memoryDiagnostics?.HookCount ?? 0));
            session.AppendLine("Memory hook failures: " + (_memoryDiagnostics?.FailedCount ?? 0));
            session.AppendLine("Memory trends: every second and on observed GC; Windows GetProcessMemoryInfo reports resident/private committed memory, blanks on failure.");
            session.AppendLine("Mod retention: weak callback receiver observations, including background receivers; direct instance fields every 30 seconds, 8192 instance / 32 reference-field limits.");
            session.AppendLine("Retention scan: max four instances / soft 0.5 ms per frame; field counts are not retained heap sizes or proof of a leak; shared native references can repeat.");
            session.AppendLine("Static roots and arbitrary object graphs require heap snapshots; no forced GC or global collection-policy changes.");
            session.AppendLine("Snapshot request: write a label to UserData/BSProfiler/memory-snapshot.request; consumed after MainMenu is active for 10 seconds. Full snapshots can pause and produce large files; unsupported captures report failure.");
            session.AppendLine("Slow-call detail: >=8 ms, GC crossing, exception, or focus callback; max 32 rows per second.");
            session.AppendLine("Recorder values can lag Update wall-clock samples; inspect neighboring frames.");
            session.AppendLine("Callback time and allocation totals are inclusive; nested rows must not be summed.");
            session.AppendLine("Loaded plugins:");
            foreach (var metadata in PluginManager.EnabledPlugins.OrderBy(metadata => metadata.Id, StringComparer.OrdinalIgnoreCase))
                session.AppendLine("  " + metadata.Id + " | " + metadata.Name + " | " + metadata.HVersion);
            File.WriteAllText(Path.Combine(directory, "session.txt"), session.ToString(), new UTF8Encoding(false));
        }

        private string FrameHeader()
        {
            var line = new StringBuilder("elapsed_ms,frame,scene,focused,frame_ms,fps,refresh_hz,budget_ms,cpu_main_ms,gpu_span_ms,xr_app_gpu_ms,xr_compositor_gpu_ms,xr_dropped_frames,xr_present_count,xr_motion_to_photon_ms,mono_used_bytes,total_allocated_bytes,working_set_bytes,gc_gen0,gc_gen1,gc_gen2,main_thread_alloc_bytes_since_update,previous_profiler_update_ms,previous_profiler_update_alloc_bytes,previous_update_to_late_ms,previous_late_to_update_ms,slow_call_details_dropped");
            foreach (Metric metric in _metrics)
                line.Append(',').Append(CaptureWriter.Csv(metric.Name + " [" + metric.Unit + "]"));
            return line.ToString();
        }

        private void Update()
        {
            if (_writer == null) return;
            long nowTicks = Stopwatch.GetTimestamp();
            long allocatedAtStart = MemoryDiagnostics.AllocatedBytes();
            long allocationDelta = allocatedAtStart < 0 || _lastAllocatedBytes < 0 ? -1 : Math.Max(0, allocatedAtStart - _lastAllocatedBytes);
            _lastAllocatedBytes = allocatedAtStart;
            double elapsedMs = (nowTicks - _startTicks) * 1000.0 / Stopwatch.Frequency;
            double frameMs = _lastFrameTicks == 0 ? double.NaN : (nowTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            double updateToLateMs = _lastLateTicks > _lastFrameTicks && _lastFrameTicks != 0
                ? (_lastLateTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency : double.NaN;
            double lateToUpdateMs = _lastLateTicks > _lastFrameTicks && _lastFrameTicks != 0
                ? (nowTicks - _lastLateTicks) * 1000.0 / Stopwatch.Frequency : double.NaN;
            _lastFrameTicks = nowTicks;
            _memoryDiagnostics?.SetFrame(Time.frameCount, _scene);
            _memoryTracker?.Tick(elapsedMs, Time.frameCount, _scene);
            _monoUsedBytes = _memoryTracker?.MonoUsed ?? 0;
            _totalAllocatedBytes = _memoryTracker?.UnityAllocated ?? 0;
            _workingSetBytes = _memoryTracker?.WorkingSet ?? -1;

            if (elapsedMs >= _nextSystemSampleMs)
            {
                _nextSystemSampleMs = elapsedMs + 1000;
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
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            bool observedGc = gc0 != _frameGcCounts[0] || gc1 != _frameGcCounts[1] || gc2 != _frameGcCounts[2];
            if (gc0 != _frameGcCounts[0] || gc1 != _frameGcCounts[1] || gc2 != _frameGcCounts[2])
            {
                _writer.Event(CaptureWriter.Number(elapsedMs) + ",gc-observed," + CaptureWriter.Csv(
                    "frame=" + Time.frameCount + "; gc_delta=" + (gc0 - _frameGcCounts[0]) + "/" + (gc1 - _frameGcCounts[1]) + "/" +
                    (gc2 - _frameGcCounts[2]) + "; main_thread_alloc_bytes=" + allocationDelta +
                    "; no caller attribution for automatic/native collections"));
            }
            _frameGcCounts[0] = gc0; _frameGcCounts[1] = gc1; _frameGcCounts[2] = gc2;
            double gcBytes = allocationDelta < 0 ? double.NaN : allocationDelta;
            double gcMarkerMs = double.NaN;
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
                .Append(_monoUsedBytes).Append(',').Append(_totalAllocatedBytes).Append(',')
                .Append(_workingSetBytes < 0 ? "" : _workingSetBytes.ToString()).Append(',')
                .Append(gc0).Append(',').Append(gc1).Append(',').Append(gc2).Append(',')
                .Append(allocationDelta < 0 ? "" : allocationDelta.ToString()).Append(',')
                .Append(CaptureWriter.Number(_previousProfilerMs)).Append(',')
                .Append(_previousProfilerBytes < 0 ? "" : _previousProfilerBytes.ToString()).Append(',')
                .Append(CaptureWriter.Number(updateToLateMs)).Append(',').Append(CaptureWriter.Number(lateToUpdateMs)).Append(',')
                .Append(_callbackProfiler?.DetailsDropped ?? 0);
            foreach (Metric metric in _metrics)
            {
                long value = metric.Read();
                _frameLine.Append(',');
                if (value >= 0) _frameLine.Append(value);
                if (metric.Name == "GC Allocated In Frame" && value >= 0) gcBytes = value;
                if (metric.Name == "GC.Collect" && value >= 0) gcMarkerMs = value / 1000000.0;
            }
            _writer.Frame(_frameLine.ToString());

            if (!double.IsNaN(frameMs))
            {
                _callbackProfiler?.CaptureFrame(_writer, elapsedMs, Time.frameCount, _scene, frameMs,
                    Math.Max(12, budgetMs * 1.5), observedGc, gcMarkerMs);
                UpdateStatistics(elapsedMs, frameMs, cpuMs, gpuMs, gcBytes, budgetMs);
            }
            if (_writer.Failure != null && _lastWriterError.Length == 0)
            {
                _lastWriterError = _writer.Failure.ToString();
                Plugin.Log?.Error("BSProfiler writer failed: " + _lastWriterError);
            }
            _previousProfilerMs = (Stopwatch.GetTimestamp() - nowTicks) * 1000.0 / Stopwatch.Frequency;
            _previousProfilerBytes = allocatedAtStart < 0 ? -1 : Math.Max(0, MemoryDiagnostics.AllocatedBytes() - allocatedAtStart);
        }

        private void LateUpdate() => _lastLateTicks = Stopwatch.GetTimestamp();

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

        private static double ReadMilliseconds(ref ProfilerRecorder recorder) => recorder.Valid && recorder.Count > 0 && recorder.LastValue >= 0
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

        private void SceneLoaded(Scene scene, LoadSceneMode mode) => _writer?.Event(
            CaptureWriter.Number(ElapsedMs()) + ",scene-loaded," + CaptureWriter.Csv(scene.name + "; mode=" + mode));

        private void SceneUnloaded(Scene scene) => _writer?.Event(
            CaptureWriter.Number(ElapsedMs()) + ",scene-unloaded," + CaptureWriter.Csv(scene.name));

        private void OnApplicationFocus(bool focused) => _writer?.Event(
            CaptureWriter.Number(ElapsedMs()) + ",focus," + CaptureWriter.Csv(focused ? "Focused" : "Unfocused"));

        private void LogReceived(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log) return;
            string detail = message + (string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace);
            if (detail.Length > 6000) detail = detail.Substring(0, 6000);
            _writer?.Event(CaptureWriter.Number(ElapsedMs()) + "," + CaptureWriter.Csv(type.ToString()) + "," + CaptureWriter.Csv(detail));
        }

        private double ElapsedMs() => (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;

        public void StopCapture(bool processQuitting = false)
        {
            if (_stopped) return;
            _stopped = true;
            StopAllCoroutines();
            Plugin.Log?.Info("BSProfiler stopping capture; processQuitting=" + processQuitting);
            SceneManager.activeSceneChanged -= SceneChanged;
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            Application.logMessageReceivedThreaded -= LogReceived;
            CloseIncident();
            WriteSummary(ElapsedMs());
            _writer?.Event(CaptureWriter.Number(ElapsedMs()) + ",stop," + CaptureWriter.Csv("Capture stopped"));
            if (_captureDirectory != null)
            {
                try { WriteSession(_captureDirectory); }
                catch (Exception ex) { Plugin.Log?.Warn("BSProfiler final session write failed: " + ex.Message); }
            }
            _callbackProfiler?.Stop(processQuitting);
            _callbackProfiler = null;
            _memoryTracker?.Dispose();
            _memoryTracker = null;
            _memoryDiagnostics?.Stop(processQuitting);
            _memoryDiagnostics = null;
            _writer?.Dispose();
            _writer = null;
            _cpuRecorder.Dispose();
            _gpuRecorder.Dispose();
            foreach (Metric metric in _metrics) metric.Dispose();
            _metrics.Clear();
            _process?.Dispose();
            _process = null;
            Plugin.Log?.Info("BSProfiler capture stopped");
        }

        private void OnApplicationQuit() => StopCapture(true);
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
