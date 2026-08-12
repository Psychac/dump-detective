using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Execution;
using DumpDetective.Core.Abstractions;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildHeapIndexStage(AnalyzerExecutionService analyzerExecutionService) : IAnalysisStage
{
    private const int HeartbeatMs = 300;
    private readonly AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;

    public string Name => "Scan + Index heap";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        DumpIndexPaths.ResolveCacheDirectory(
            state.Resolved.DumpPath,
            state.Resolved.CacheDirectory,
            onTempFallback: dir => ConsoleUx.Warning($"Dump folder is not writable; caching index in temp folder: {dir}"));

        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;

        // Wall-clock stopwatch owned by this stage so elapsed always ticks forward,
        // regardless of whether the writer has stopped its own internal stopwatch.
        Stopwatch wallClock = Stopwatch.StartNew();

        // Shared state updated by the progress callback and read by the heartbeat.
        // Progress<T> callbacks run on a ThreadPool thread in a console app (no SynchronizationContext),
        // so writes here race with the heartbeat reads. Use a simple wrapper class whose string
        // fields are written under a monitor so the heartbeat always sees a consistent snapshot.
        long lastScanned = 0;
        string lastPhase = "scanning heap";
        string? lastDetail = null;
        var progressLock = new object();
        var timeline = new PhaseTimeline();
        timeline.Record(lastPhase, wallClock.Elapsed);

        var progress = new Progress<AnalyzerProgressReport>(r =>
        {
            // Only update the shared state here — do NOT call ConsoleUx.ObjectScanProgress.
            // Progress<T> callbacks run on a ThreadPool thread; calling the console renderer
            // here races with the heartbeat loop below, producing garbled output and stale phases.
            // The heartbeat is the sole renderer.
            Interlocked.Exchange(ref lastScanned, r.ScannedCount);
            lock (progressLock)
            {
                lastPhase = r.Phase;
                lastDetail = string.IsNullOrWhiteSpace(r.Detail) ? r.Phase : r.Detail;
            }
            timeline.Record(r.Phase, wallClock.Elapsed);
        });

        // Run the synchronous index build on a thread-pool thread so the heartbeat
        // loop below can keep the spinner's elapsed counter live between progress events.
        Task<HeapIndexBuildResult> buildTask = Task.Run(
            () => heapBuilder.PrebuildHeapIndex(
                state.LoadContext!.Heap,
                state.Resolved.DumpPath,
                cancellationToken,
                progress: progress),
            cancellationToken);

        while (true)
        {
            Task done = await Task.WhenAny(buildTask, Task.Delay(HeartbeatMs, cancellationToken));
            if (done == buildTask)
                break;

            // Print any phase that finished since the last tick before rendering the live line for
            // the current one — otherwise the breakdown only ever appears once, all at once, after
            // the whole stage is done.
            ConsoleUx.PhaseBreakdown(timeline.DrainCompletedSegments());

            // Heartbeat: re-render the spinner with the wall-clock elapsed so the
            // timer keeps ticking even when the writer hasn't fired a progress event.
            string details;
            lock (progressLock)
                details = string.IsNullOrWhiteSpace(lastDetail) ? lastPhase : lastDetail;
            ConsoleUx.ObjectScanProgress(Name, Interlocked.Read(ref lastScanned), wallClock.Elapsed, details);
        }

        HeapIndexBuildResult heapIndex = await buildTask;
        wallClock.Stop();

        ConsoleUx.ObjectScanComplete(Name, heapIndex.ObjectCount, heapIndex.Elapsed, Path.GetFileName(heapIndex.IndexPath));
        ConsoleUx.PhaseBreakdown(timeline.GetRemainingSegments(wallClock.Elapsed));

        if (heapIndex.SatelliteWarnings is { Count: > 0 } satelliteWarnings)
        {
            foreach (string w in satelliteWarnings)
                ConsoleUx.Warning($"Satellite index write failed — dependent analyzers may produce incomplete results: {w}");
        }

        if (state.Resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Index built: objects={heapIndex.ObjectCount:N0}, elapsed={heapIndex.Elapsed.TotalSeconds:F1}s");
        }

        // Both properties point to the same HeapAnalysisCache instance, typed through their respective interfaces.
        state.HeapIndexBuilder = heapBuilder;
        state.HeapCache = heapCache;
        state.HeapIndex = heapIndex;

        RuntimeAnalysisContext context = _analyzerExecutionService.BuildContext(
            state.Resolved,
            state.LoadContext!,
            heapCache,
            state.ActiveAnalyzers);
        AnalysisPipeline pipeline = _analyzerExecutionService.CreatePipeline(state.ActiveAnalyzers);

        // Shared heap-index/thread-stack scan passes are one pass over the index fanned out to
        // every participating analyzer, not an individual analyzer — run them here, under this
        // stage's header/timer, instead of letting them run inside "Run analyzers".
        _analyzerExecutionService.RunSharedScans(pipeline, context, cancellationToken);

        state.Context = context;
        state.Pipeline = pipeline;
    }
}

