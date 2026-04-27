using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Stages;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Pipeline;
using DumpDetective.Reporting.Services;

using System.Diagnostics;

namespace DumpDetective.Cli.Services;

/// <summary>
/// Orchestrates a single-dump analysis run: stages → pipeline runner → console summary → exit code.
/// </summary>
internal sealed class SingleDumpOrchestrationService(
    DumpLoader dumpLoader,
    FindingGenerationPipeline findingGenerationPipeline,
    ReportBuilderFacade reportBuilderFacade)
{
    private readonly DumpLoader _dumpLoader = dumpLoader;
    private readonly FindingGenerationPipeline _findingGenerationPipeline = findingGenerationPipeline;
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;

    private const string TemporaryAdaptiveIndexingNotice =
        "TEMP-ADAPTIVE-INDEXING: Auto mode uses a provisional dump-size threshold; tune memory-vs-disk selection with large-dump profiling.";

    public async Task<int> ExecuteAsync(
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        CancellationToken cancellationToken)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        ConsoleUx.Header("DumpDetective Analysis");
        ConsoleUx.Warning(TemporaryAdaptiveIndexingNotice);

        if (resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Config source: {(resolved.UsedConfigFile ? $"file ({resolved.ConfigPath})" : "CLI fallback")}");
            ConsoleUx.Info($"Active analyzers ({activeAnalyzers.Count}): {string.Join(", ", activeAnalyzers.Select(a => a.Name))}");
        }
        else
        {
            ConsoleUx.Info($"Analyzing dump: {Path.GetFileName(resolved.DumpPath)}");
            ConsoleUx.Info($"Running {activeAnalyzers.Count} analyzers...");
        }

        IReadOnlyList<IAnalysisStage> stages = BuildStages();

        using SingleDumpPipelineState state = new()
        {
            Resolved = resolved,
            ActiveAnalyzers = activeAnalyzers
        };

        await new StagedPipelineRunner().RunAsync(stages, state, cancellationToken);

        if (resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Pipeline completed in {state.PipelineStopwatch.Elapsed.TotalSeconds:F1}s");
            ConsoleUx.Info($"Run summary: {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success)} success, {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed)} failed, {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Skipped)} skipped.");
            PrintDiagnosticsSummary(state.Runs);
        }

        totalStopwatch.Stop();
        ConsoleUx.Success($"Total analysis time: {totalStopwatch.Elapsed.TotalSeconds:F1}s");

        return state.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed)
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }

    private IReadOnlyList<IAnalysisStage> BuildStages() =>
    [
        new LoadDumpStage(_dumpLoader),
        new BuildHeapIndexStage(),
        new RunAnalyzersPipelineStage(),
        new GenerateFindingsStage(_findingGenerationPipeline),
        new BuildReportStage(_reportBuilderFacade),
        new WriteOutputStage()
    ];

    private static void PrintDiagnosticsSummary(IReadOnlyList<AnalyzerRunResult> runs)
    {
        if (runs.Count == 0)
            return;

        long totalScans = runs.Sum(r => r.ObjectScanCount);
        long totalCacheHits = runs.Sum(r => r.CacheHits);
        long totalCacheMisses = runs.Sum(r => r.CacheMisses);
        long cacheTotal = totalCacheHits + totalCacheMisses;
        double cacheHitRatio = cacheTotal == 0 ? 0 : totalCacheHits * 100.0 / cacheTotal;

        ConsoleUx.Info($"Scan summary: object-scans={totalScans:N0}, cache-hits={totalCacheHits:N0}, cache-misses={totalCacheMisses:N0}, hit-ratio={cacheHitRatio:F1}%");

        IReadOnlyList<AnalyzerRunResult> topSlow = runs
            .OrderByDescending(r => r.Duration)
            .Take(5)
            .ToList();

        ConsoleUx.Info("Top slow analyzers:");
        foreach (AnalyzerRunResult run in topSlow)
            ConsoleUx.Info($"  - {run.AnalyzerName}: {run.Duration.TotalMilliseconds:F0} ms, findings={run.FindingCount}, warnings={run.WarningCount}, scans={run.ObjectScanCount:N0}");
    }
}
