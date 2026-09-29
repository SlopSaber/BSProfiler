# BSProfiler

BSProfiler records Beat Saber 1.45.2 performance data under `UserData/BSProfiler/<UTC start>-<process ID>/` in the selected game instance. It starts with the game and closes files on exit. It uses no UI or hotkeys.

- `frames.csv`: one row per Unity `Update` call, with wall-clock frame time, approximate FPS, display budget, Unity CPU main-thread time, Unity GPU frame span, XR app/compositor GPU time, dropped/presented frame counts, motion-to-photon latency, memory, GC collection counters, and available Unity profiler markers. Empty cells mean the counter was unavailable. Counter names and units are in the header.
- `incidents.csv`: runs of frames exceeding `max(12 ms, 1.5 × display frame budget)`. A gap of one second without a slow frame closes a run. The peak frame number links to `frames.csv`.
- `summaries.csv`: ten-second frame-time percentiles and GC collection deltas. The percentile sample keeps up to 4,096 frames per interval; the mean and worst use every frame.
- `events.csv`: scene changes and Unity warnings, errors, and exceptions with capture time. IPA's own log remains the source for messages that bypass Unity logging.
- `session.txt`: hardware, graphics settings, recorder availability, tracked markers, and loaded plugin versions.
- `available-markers.csv`: Unity's runtime profiler marker catalog, including markers that BSProfiler did not record.
- `callback-spikes.csv`: on slow frames, the top eight installed-mod assembly totals and top twenty individual callbacks, with call counts, inclusive total time, and longest call. It times Unity update callbacks, Zenject ticks, coroutine and async steps, and Harmony callbacks on the main thread. Match `frame` with `frames.csv`; `assembly` names the mod DLL. Rows outside the top eight assemblies or top twenty callbacks are omitted.
- `callback-catalog.csv`: every callback BSProfiler attempted to time, its DLL path, and whether the hook installed. Check this file when the slow frame has no matching callback.

Compare an incident's peak frame with nearby `frames.csv` and `callback-spikes.csv` rows. Repeated slow frames dominated by the same callback identify where to investigate; assembly totals can include nested calls counted twice. The hooks do not cover game-owned methods, native work, or arbitrary mod methods outside these entry points. A slow frame with no large callback still needs targeted timing or a controlled mod comparison. The GPU frame span includes waiting and does not measure GPU utilization.

The capture writes a row every frame and can grow by hundreds of megabytes over a long session. The writer uses a bounded queue and writes on a background thread; `queue_dropped` in `summaries.csv` reveals lost rows. Callback hooks add timing overhead, so compare runs with the same capture setup. BSProfiler removes its hooks on exit.

Build for the selected instance with `dotnet build BSProfiler.csproj -c Release -p:BeatSaberDir=<instance> -p:DisableCopyToPlugins=true`. Install only `bin/Release/net48/BSProfiler.dll` into the instance's `Plugins` directory, or `IPA/Pending/Plugins` while the game runs.
