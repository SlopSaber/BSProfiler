# BSProfiler

BSProfiler records Beat Saber 1.45.1 performance data under `UserData/BSProfiler/<UTC start>-<process ID>/` in the selected game instance. It starts with the game and closes files on exit. It uses no UI or hotkeys.

- `frames.csv`: one row per Unity `Update` call, with wall-clock frame time, approximate FPS, display budget, Unity CPU main-thread time, Unity GPU frame span, XR app/compositor GPU time, dropped/presented frame counts, motion-to-photon latency, memory, GC collection counters, and available Unity profiler markers. Empty cells mean the counter was unavailable. Counter names and units are in the header.
- `incidents.csv`: runs of frames exceeding `max(12 ms, 1.5 × display frame budget)`. A gap of one second without a slow frame closes a run. The peak frame number links to `frames.csv`.
- `summaries.csv`: ten-second frame-time percentiles and GC collection deltas. The percentile sample keeps up to 4,096 frames per interval; the mean and worst use every frame.
- `events.csv`: scene changes and Unity warnings, errors, and exceptions with capture time. IPA's own log remains the source for messages that bypass Unity logging.
- `session.txt`: hardware, graphics settings, recorder availability, tracked markers, and loaded plugin versions.
- `available-markers.csv`: Unity's runtime profiler marker catalog, including markers that BSProfiler did not record.

Compare an incident's peak frame with nearby `frames.csv` rows and `events.csv`. High CPU main-thread time, rising GC counts, and high render or script marker durations can narrow the search. The GPU frame span includes waiting and does not measure GPU utilization. Frame counters and a loaded plugin list cannot identify a specific mod as the cause by themselves. For a mod-level conclusion, reproduce the same scene with a suspected mod disabled or add timing around that mod's own work.

The capture writes a row every frame and can grow by hundreds of megabytes over a long session. The writer uses a bounded queue and writes on a background thread; `queue_dropped` in `summaries.csv` reveals lost rows. Profiler marker collection and per-frame serialization add some overhead, so compare runs with the same capture setup.

Build for the selected instance with `dotnet build BSProfiler.csproj -c Release -p:BeatSaberDir=<instance> -p:DisableCopyToPlugins=true`. Install only `bin/Release/net48/BSProfiler.dll` into the instance's `Plugins` directory, or `IPA/Pending/Plugins` while the game runs.
