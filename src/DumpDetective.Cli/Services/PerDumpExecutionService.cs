using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using System.Diagnostics;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Models;

namespace DumpDetective.Cli.Services;

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

        progress?.Report(new AnalyzerProgressReport(0, "loading dump", Detail: null, Elapsed: stopwatch.Elapsed));
        using DumpLoadContext loadContext = await _dumpLoader.LoadAsync(dumpPath, cancellationToken);
        progress?.Report(new AnalyzerProgressReport(0, "preparing index", Detail: null, Elapsed: stopwatch.Elapsed));

        currentProcess.Refresh();
        long wsAfterLoad = currentProcess.WorkingSet64;
        long managedAfterLoad = GC.GetTotalMemory(false);

        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;

        var heapIndex = heapBuilder.PrebuildHeapIndex(
            loadContext.Heap,
            dumpPath,
            cancellationToken,
            progress: progress,
            mode: resolved.IndexPrebuildMode);

        // Signal stage transition: outer heartbeat uses this to commit the "Scan + Index heap"
        // section and stop rendering, handing off to ConsoleDiagnosticsSink for analyzers.
        progress?.Report(new AnalyzerProgressReport(
            heapIndex.ObjectCount,
            "running analyzers",
            Detail: heapIndex.StorageKind == HeapIndexStorageKind.Memory
                ? "in-memory"
                : Path.GetFileName(heapIndex.IndexPath),
            Elapsed: heapIndex.Elapsed));

        currentProcess.Refresh();
        long wsAfterIndex = currentProcess.WorkingSet64;
        long managedAfterIndex = GC.GetTotalMemory(false);

        RuntimeAnalysisContext context = _analyzerExecutionService.BuildContext(resolved, loadContext, heapCache, activeAnalyzers);
        IReadOnlyList<AnalyzerRunResult> runs = await _analyzerExecutionService.ExecuteAsync(context, activeAnalyzers, cancellationToken);

        currentProcess.Refresh();
        long wsAfterAnalyze = currentProcess.WorkingSet64;
        long managedAfterAnalyze = GC.GetTotalMemory(false);

        runs = AnalyzerFilterService.BuildSkippedByFilterResults(allAnalyzers, activeAnalyzers)
            .Concat(runs)
            .ToList();

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
                ("Load dump", new AnalyzerMemoryStats(wsBeforeLoad, wsAfterLoad, managedBeforeLoad, managedAfterLoad)),
                ("Scan + Index heap", new AnalyzerMemoryStats(wsAfterLoad, wsAfterIndex, managedAfterLoad, managedAfterIndex)),
                ("Run analyzers", new AnalyzerMemoryStats(wsAfterIndex, wsAfterAnalyze, managedAfterIndex, managedAfterAnalyze))
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