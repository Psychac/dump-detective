using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using System.Diagnostics;

namespace DumpDetective.Cli.Execution;

internal sealed class PerDumpExecutionService(
    IDumpLoader dumpLoader,
    AnalyzerExecutionService analyzerExecutionService)
{
    private readonly IDumpLoader _dumpLoader = dumpLoader;
    private readonly AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;

    public async Task<PerDumpExecutionResult> ExecuteAsync(
        string mode,
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        string dumpPath,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        Process currentProcess = Process.GetCurrentProcess();
        currentProcess.Refresh();
        long wsBeforeLoad = currentProcess.WorkingSet64;
        long managedBeforeLoad = GC.GetTotalMemory(false);
        long allocBeforeLoad = GC.GetTotalAllocatedBytes(precise: false);

        progress?.Report(new AnalyzerProgressReport(0, "loading dump", Detail: null, Elapsed: stopwatch.Elapsed));
        using DumpLoadContext loadContext = await _dumpLoader.LoadAsync(dumpPath, cancellationToken);
        progress?.Report(new AnalyzerProgressReport(0, "preparing index", Detail: null, Elapsed: stopwatch.Elapsed));

        currentProcess.Refresh();
        long wsAfterLoad = currentProcess.WorkingSet64;
        long managedAfterLoad = GC.GetTotalMemory(false);
        long allocAfterLoad = GC.GetTotalAllocatedBytes(precise: false);

        DumpIndexPaths.ResolveCacheDirectory(
            dumpPath,
            resolved.CacheDirectory,
            onTempFallback: dir => ConsoleUx.Warning($"Dump folder is not writable; caching index in temp folder: {dir}"));

        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;

        var heapIndex = heapBuilder.PrebuildHeapIndex(
            loadContext.Heap,
            dumpPath,
            cancellationToken,
            progress: progress);

        RuntimeAnalysisContext context = _analyzerExecutionService.BuildContext(resolved, loadContext, heapCache, activeAnalyzers);
        AnalysisPipeline pipeline = _analyzerExecutionService.CreatePipeline(activeAnalyzers);

        // Shared heap-index/thread-stack scan passes are conceptually part of index preparation
        // (one shared pass over the index, not an individual analyzer), so run them here — before
        // the "running analyzers" phase marker — so their time/memory is attributed to indexing.
        _analyzerExecutionService.RunSharedScans(pipeline, context, cancellationToken);

        progress?.Report(new AnalyzerProgressReport(
            heapIndex.ObjectCount,
            "running analyzers",
            Detail: Path.GetFileName(heapIndex.IndexPath),
            Elapsed: heapIndex.Elapsed));

        currentProcess.Refresh();
        long wsAfterIndex = currentProcess.WorkingSet64;
        long managedAfterIndex = GC.GetTotalMemory(false);
        long allocAfterIndex = GC.GetTotalAllocatedBytes(precise: false);

        IReadOnlyList<AnalyzerRunResult> runs = await _analyzerExecutionService.ExecuteAsync(pipeline, context, cancellationToken);

        currentProcess.Refresh();
        long wsAfterAnalyze = currentProcess.WorkingSet64;
        long managedAfterAnalyze = GC.GetTotalMemory(false);
        long allocAfterAnalyze = GC.GetTotalAllocatedBytes(precise: false);

        runs = AnalyzerFilterService.BuildSkippedByFilterResults(allAnalyzers, activeAnalyzers)
            .Concat(runs)
            .ToArray();

        AnalysisIncidentContext incidentContext = IncidentContextFactory.Create(
            mode: mode,
            loadContext: loadContext,
            resolved: resolved,
            activeAnalyzers: activeAnalyzers,
            elapsed: stopwatch.Elapsed);

        stopwatch.Stop();
        return new PerDumpExecutionResult(
            heapIndex,
            runs,
            incidentContext,
            heapCache,
            stopwatch.Elapsed,
            new List<(string StageName, AnalyzerMemoryStats Stats)>
            {
                ("Load dump", new AnalyzerMemoryStats(wsBeforeLoad, wsAfterLoad, managedBeforeLoad, managedAfterLoad, allocBeforeLoad, allocAfterLoad)),
                ("Scan + Index heap", new AnalyzerMemoryStats(wsAfterLoad, wsAfterIndex, managedAfterLoad, managedAfterIndex, allocAfterLoad, allocAfterIndex)),
                ("Run analyzers", new AnalyzerMemoryStats(wsAfterIndex, wsAfterAnalyze, managedAfterIndex, managedAfterAnalyze, allocAfterIndex, allocAfterAnalyze))
            });
    }
}

internal sealed record PerDumpExecutionResult(
    HeapIndexBuildResult HeapIndex,
    IReadOnlyList<AnalyzerRunResult> Runs,
    AnalysisIncidentContext IncidentContext,
    HeapAnalysisCache HeapCache,
    TimeSpan Elapsed,
    IReadOnlyList<(string StageName, AnalyzerMemoryStats Stats)> StageMemoryStats);
