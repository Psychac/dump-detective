using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildHeapIndexStage : IAnalysisStage
{
    private const int HeartbeatMs = 300;

    public string Name => "Scan + Index heap";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;

        // Wall-clock stopwatch owned by this stage so elapsed always ticks forward,
        // regardless of whether the writer has stopped its own internal stopwatch.
        Stopwatch wallClock = Stopwatch.StartNew();

        // Shared state updated by the progress callback and read by the heartbeat.
        long lastScanned = 0;
        string lastPhase = "scanning heap";
        string? lastDetail = null;

        var progress = new Progress<AnalyzerProgressReport>(r =>
        {
            Interlocked.Exchange(ref lastScanned, r.ScannedCount);
            lastPhase = r.Phase;
            lastDetail = string.IsNullOrWhiteSpace(r.Detail) ? r.Phase : r.Detail;
            ConsoleUx.ObjectScanProgress(Name, r.ScannedCount, wallClock.Elapsed, lastDetail);
        });

        // Run the synchronous index build on a thread-pool thread so the heartbeat
        // loop below can keep the spinner's elapsed counter live between progress events.
        Task<HeapIndexBuildResult> buildTask = Task.Run(
            () => heapBuilder.PrebuildHeapIndex(
                state.LoadContext!.Heap,
                state.Resolved.DumpPath,
                cancellationToken,
                progress: progress,
                mode: state.Resolved.IndexPrebuildMode),
            cancellationToken);

        while (true)
        {
            Task done = await Task.WhenAny(buildTask, Task.Delay(HeartbeatMs, cancellationToken));
            if (done == buildTask)
                break;

            // Heartbeat: re-render the spinner with the wall-clock elapsed so the
            // timer keeps ticking even when the writer hasn't fired a progress event.
            string? details = string.IsNullOrWhiteSpace(lastDetail) ? lastPhase : lastDetail;
            ConsoleUx.ObjectScanProgress(Name, Interlocked.Read(ref lastScanned), wallClock.Elapsed, details);
        }

        HeapIndexBuildResult heapIndex = await buildTask;
        wallClock.Stop();

        ConsoleUx.ObjectScanComplete(Name, heapIndex.ObjectCount, heapIndex.Elapsed, Path.GetFileName(heapIndex.IndexPath));

        if (heapIndex.SatelliteWarnings is { Count: > 0 } satelliteWarnings)
        {
            foreach (string w in satelliteWarnings)
                ConsoleUx.Warning($"Satellite index write failed — dependent analyzers may produce incomplete results: {w}");
        }

        if (state.Resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Index built: requested={state.Resolved.IndexPrebuildMode}, selected={heapIndex.StorageKind}, objects={heapIndex.ObjectCount:N0}, elapsed={heapIndex.Elapsed.TotalSeconds:F1}s");
        }

        // Both properties point to the same HeapAnalysisCache instance, typed through their respective interfaces.
        state.HeapIndexBuilder = heapBuilder;
        state.HeapCache = heapCache;
        state.HeapIndex = heapIndex;
    }
}

