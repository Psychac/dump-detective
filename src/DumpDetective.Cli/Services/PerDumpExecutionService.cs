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
        using DumpLoadContext loadContext = await _dumpLoader.LoadAsync(dumpPath, cancellationToken);

        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;

        var heapIndex = heapBuilder.PrebuildHeapIndex(
            loadContext.Heap,
            dumpPath,
            cancellationToken,
            progress: progress,
            mode: resolved.IndexPrebuildMode);

        RuntimeAnalysisContext context = _analyzerExecutionService.BuildContext(resolved, loadContext, heapCache, activeAnalyzers);
        IReadOnlyList<AnalyzerRunResult> runs = await _analyzerExecutionService.ExecuteAsync(context, activeAnalyzers, cancellationToken);

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
        return new PerDumpExecutionResult(heapIndex, runs, incidentContext, heapCache, stopwatch.Elapsed);
    }
}

internal sealed record PerDumpExecutionResult(
    HeapIndexBuildResult HeapIndex,
    IReadOnlyList<AnalyzerRunResult> Runs,
    AnalysisIncidentContext IncidentContext,
    HeapAnalysisCache HeapCache,
    TimeSpan Elapsed);