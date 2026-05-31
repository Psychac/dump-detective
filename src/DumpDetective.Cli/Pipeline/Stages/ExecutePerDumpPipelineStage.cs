using DumpDetective.Cli.Console;
using DumpDetective.Cli.Execution;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class ExecutePerDumpPipelineStage(PerDumpExecutionService perDumpExecutionService) : IAnalysisStage
{
    public string Name => "Load + scan + analyze";
    private const int HeartbeatMs = 300;
    private readonly PerDumpExecutionService _perDumpExecutionService = perDumpExecutionService;

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        Stopwatch wallClock = Stopwatch.StartNew();

        // Shared state updated by the progress callback, read by the heartbeat.
        // Phase-transition markers ("preparing index", "running analyzers") must NEVER be
        // dropped — so no ThrottledProgress here. ThrottledProgress is applied inside
        // AnalysisPipeline.ExecuteAnalyzerWithProgressAsync for context.Progress instead.
        long lastScanned = 0;
        string lastPhase = "loading dump";
        string? lastDetail = null;
        bool loadDone = false;
        bool indexDone = false;
        TimeSpan loadElapsed = TimeSpan.Zero;
        long indexScanned = 0;
        TimeSpan indexElapsed = TimeSpan.Zero;
        string? indexDetail = null;
        var progressLock = new object();

        var progress = new Progress<AnalyzerProgressReport>(r =>
        {
            Interlocked.Exchange(ref lastScanned, r.ScannedCount);
            lock (progressLock)
            {
                lastPhase = r.Phase;
                lastDetail = string.IsNullOrWhiteSpace(r.Detail) ? r.Phase : r.Detail;
                if (!loadDone && r.Phase == "preparing index")
                {
                    loadDone = true;
                    loadElapsed = r.Elapsed ?? wallClock.Elapsed;
                }
                if (!indexDone && r.Phase == "running analyzers")
                {
                    indexDone = true;
                    indexScanned = r.ScannedCount;
                    indexElapsed = r.Elapsed ?? wallClock.Elapsed;
                    indexDetail = r.Detail;
                }
            }
        });

        Task<PerDumpExecutionResult> executeTask = Task.Run(
            () => _perDumpExecutionService.ExecuteAsync(
                "Single",
                state.Resolved,
                state.AllAnalyzers,
                state.ActiveAnalyzers,
                state.Resolved.DumpPath,
                progress,
                cancellationToken),
            cancellationToken);

        bool renderedLoadComplete = false;
        bool renderedIndexComplete = false;

        while (true)
        {
            Task done = await Task.WhenAny(executeTask, Task.Delay(HeartbeatMs, cancellationToken));
            if (done == executeTask)
                break;

            bool isLoadDone, isIndexDone;
            TimeSpan loadEl, idxEl;
            long idxScanned;
            string? idxDtl, details;
            lock (progressLock)
            {
                isLoadDone = loadDone;
                isIndexDone = indexDone;
                loadEl = loadElapsed;
                idxEl = indexElapsed;
                idxScanned = indexScanned;
                idxDtl = indexDetail;
                details = string.IsNullOrWhiteSpace(lastDetail) ? lastPhase : lastDetail;
            }
            long scanned = Interlocked.Read(ref lastScanned);

            if (isLoadDone && !renderedLoadComplete)
            {
                ConsoleUx.ObjectScanComplete("Load dump", 0, loadEl, null);
                renderedLoadComplete = true;
            }

            if (isIndexDone && !renderedIndexComplete)
            {
                ConsoleUx.ObjectScanComplete("Scan + Index heap", idxScanned, idxEl, idxDtl);
                renderedIndexComplete = true;
                // ConsoleDiagnosticsSink owns all rendering from here — stop the outer heartbeat.
                break;
            }

            ConsoleUx.ObjectScanProgress(Name, scanned, wallClock.Elapsed, details);
        }

        PerDumpExecutionResult result = await executeTask;
        wallClock.Stop();

        // Fallback: commit stages not yet observed via heartbeat (e.g. entire run < 300ms).
        if (!renderedLoadComplete)
            ConsoleUx.ObjectScanComplete("Load dump", 0, wallClock.Elapsed, null);
        if (!renderedIndexComplete)
        {
            string fallbackTarget = result.HeapIndex.StorageKind == DumpDetective.Analysis.Indexing.HeapIndexStorageKind.Memory
                ? "in-memory"
                : Path.GetFileName(result.HeapIndex.IndexPath);
            ConsoleUx.ObjectScanComplete("Scan + Index heap", result.HeapIndex.ObjectCount, result.HeapIndex.Elapsed, fallbackTarget);
        }

        state.HeapIndex = result.HeapIndex;
        state.Runs = result.Runs;
        state.IncidentContext = result.IncidentContext;
        state.HeapCache = result.HeapCache;
        state.HeapIndexBuilder = result.HeapCache;
        state.AnalysisElapsed = result.Elapsed;
        state.HasDetailedStageMemoryStats = true;
        state.StageMemoryStats.Clear();
        state.StageMemoryStats.AddRange(result.StageMemoryStats);

        if (result.HeapIndex.SatelliteWarnings is { Count: > 0 } satelliteWarnings)
        {
            foreach (string warning in satelliteWarnings)
                ConsoleUx.Warning($"Satellite index write failed — dependent analyzers may produce incomplete results: {warning}");
        }

        if (state.Resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Index built: requested={state.Resolved.IndexPrebuildMode}, selected={result.HeapIndex.StorageKind}, objects={result.HeapIndex.ObjectCount:N0}, elapsed={result.HeapIndex.Elapsed.TotalSeconds:F1}s");
        }
    }
}