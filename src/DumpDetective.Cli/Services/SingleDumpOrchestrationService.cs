using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Insight;
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
    IDumpLoader dumpLoader,
    FindingGenerationPipeline findingGenerationPipeline,
    ReportBuilderFacade reportBuilderFacade)
{
    private readonly IDumpLoader _dumpLoader = dumpLoader;
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

        // Run the cross-cutting insight engine after all analyzer findings are generated.
        state.Insights = new InsightEngine().Analyze(state.Runs);
        if (state.Insights.Count > 0)
            PrintInsights(state.Insights, resolved.DiagnosticMode);

        if (resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Pipeline completed in {state.PipelineStopwatch.Elapsed.TotalSeconds:F1}s");
            ConsoleUx.Info($"Run summary: {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success)} success, {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed)} failed, {state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Skipped)} skipped.");
            PrintDiagnosticsSummary(state.Runs);
        }

        if (resolved.Diagnostics.EnableMemoryDiagnostics)
            PrintMemorySummary(state.Runs, state.StageMemoryStats);

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

    private static void PrintInsights(IReadOnlyList<InsightFinding> insights, bool diagnosticMode)
    {
        if (diagnosticMode)
            ConsoleUx.Info($"InsightEngine: {insights.Count} cross-cutting finding(s).");

        for (int i = 0; i < insights.Count; i++)
        {
            InsightFinding f = insights[i];
            string prefix = f.Severity switch
            {
                FindingSeverity.Critical => "[CRITICAL]",
                FindingSeverity.Warning  => "[WARNING]",
                _                        => "[INFO]"
            };

            if (f.Severity == FindingSeverity.Critical)
                ConsoleUx.Error($"{prefix} {f.Title}");
            else if (f.Severity == FindingSeverity.Warning)
                ConsoleUx.Warning($"{prefix} {f.Title}");
            else
                ConsoleUx.Info($"{prefix} {f.Title}");

            if (diagnosticMode)
                ConsoleUx.Info($"  Evidence: {f.Evidence}");
        }
    }

    private static void PrintMemorySummary(
        IReadOnlyList<AnalyzerRunResult> runs,
        List<(string StageName, AnalyzerMemoryStats Stats)> stageStats)
    {
        // ── Stage table ──────────────────────────────────────────────────────
        if (stageStats.Count > 0)
        {
            ConsoleUx.MemoryStageTableHeader();
            foreach ((string name, AnalyzerMemoryStats s) in stageStats)
                ConsoleUx.MemoryTableRow(name, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
        }

        // ── Analyzer table ───────────────────────────────────────────────────
        var withStats = runs
            .Where(r => r.MemoryStats is not null)
            .ToList();

        if (withStats.Count > 0)
        {
            ConsoleUx.MemoryTableHeader();
            foreach (AnalyzerRunResult run in withStats)
            {
                AnalyzerMemoryStats s = run.MemoryStats!;
                ConsoleUx.MemoryTableRow(run.AnalyzerName, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
            }
        }

        // ── Process peak across all measured scopes ──────────────────────────
        long baseline = stageStats.Count > 0
            ? stageStats[0].Stats.WorkingSetBefore
            : withStats.Count > 0 ? withStats[0].MemoryStats!.WorkingSetBefore : 0;

        long peakFromStages    = stageStats.Count > 0    ? stageStats.Max(s => s.Stats.WorkingSetAfter)    : 0;
        long peakFromAnalyzers = withStats.Count > 0     ? withStats.Max(r => r.MemoryStats!.WorkingSetAfter) : 0;
        long peak = Math.Max(peakFromStages, peakFromAnalyzers);

        if (peak > 0)
            ConsoleUx.MemoryTableFooter(peak, baseline);
    }

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
