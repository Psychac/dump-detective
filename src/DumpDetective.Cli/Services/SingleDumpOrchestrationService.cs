using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Insight;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Stages;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Output;

using System.Diagnostics;
using DumpDetective.Cli.Execution;

namespace DumpDetective.Cli.Services;

/// <summary>
/// Orchestrates a single-dump analysis run: stages → pipeline runner → console summary → exit code.
/// </summary>
internal sealed class SingleDumpOrchestrationService(
    ReportBuilderFacade reportBuilderFacade,
    ReportOutputWriter outputWriter,
    IDumpLoader dumpLoader,
    DumpDetective.Cli.Execution.AnalyzerExecutionService analyzerExecutionService)
{
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly ReportOutputWriter _outputWriter = outputWriter;
    private readonly IDumpLoader _dumpLoader = dumpLoader;
    private readonly DumpDetective.Cli.Execution.AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;

    private const string TemporaryAdaptiveIndexingNotice =
        "TEMP-ADAPTIVE-INDEXING: Auto mode uses a provisional dump-size threshold; tune memory-vs-disk selection with large-dump profiling.";

    public async Task<int> ExecuteAsync(
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        CancellationToken cancellationToken)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        ConsoleUx.Header("DumpDetective Analysis");
        ConsoleUx.DumpInfo(Path.GetFileName(resolved.DumpPath), TryGetFileSize(resolved.DumpPath));
        //ConsoleUx.Note(TemporaryAdaptiveIndexingNotice);

        if (resolved.DiagnosticMode)
            ConsoleUx.Info(AnalysisSummaryFormatter.FormatConfigSummary(resolved, activeAnalyzers));

        IReadOnlyList<IAnalysisStage> stages = BuildStages();

        using SingleDumpPipelineState state = new()
        {
            Resolved = resolved,
            AllAnalyzers = allAnalyzers,
            ActiveAnalyzers = activeAnalyzers
        };

        await new StagedPipelineRunner().RunAsync(stages, state, cancellationToken);

        // Run the cross-cutting insight engine after all analyzer findings are generated.
        state.Insights = new InsightEngine().Analyze(state.Runs);
        if (state.Insights.Count > 0)
            PrintInsights(state.Insights, resolved.DiagnosticMode);

        if (resolved.DiagnosticMode)
        {
            int success = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success);
            int failed = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
            int skippedByFilter = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter);
            int skippedByCancellation = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation);
            int findings = state.Runs.Sum(r => r.FindingCount);
            ConsoleUx.RunStatusSummary(success, failed, skippedByFilter, skippedByCancellation, findings);
            PrintDiagnosticsSummary(state.Runs);
        }

        if (resolved.Diagnostics.EnableMemoryDiagnostics)
            PrintMemorySummary(state.Runs, state.StageMemoryStats);

        totalStopwatch.Stop();
        ConsoleUx.Footer(totalStopwatch.Elapsed);

        return state.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed)
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }

    private IReadOnlyList<IAnalysisStage> BuildStages() =>
    [
        new LoadDumpStage(_dumpLoader),
        new BuildHeapIndexStage(),
        new RunAnalyzersPipelineStage(_analyzerExecutionService),
        new BuildReportStage(_reportBuilderFacade),
        new WriteOutputStage(_outputWriter)
    ];

    private static void PrintInsights(IReadOnlyList<InsightFinding> insights, bool diagnosticMode)
    {
        ConsoleUx.InsightsHeader(insights.Count);
        foreach (InsightFinding f in insights)
            ConsoleUx.InsightLine(f.Severity, f.Title, f.Evidence, diagnosticMode);
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
        bool printedAnyAnalyzerRow = false;
        long firstRunWorkingSetBefore = 0;
        long peakFromAnalyzers = 0;

        foreach (AnalyzerRunResult run in runs)
        {
            if (run.MemoryStats is null)
                continue;

            if (!printedAnyAnalyzerRow)
            {
                ConsoleUx.MemoryTableHeader();
                printedAnyAnalyzerRow = true;
                firstRunWorkingSetBefore = run.MemoryStats.WorkingSetBefore;
            }

            AnalyzerMemoryStats s = run.MemoryStats;
            ConsoleUx.MemoryTableRow(run.AnalyzerName, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
            if (s.WorkingSetAfter > peakFromAnalyzers) peakFromAnalyzers = s.WorkingSetAfter;
        }

        // ── Process peak across all measured scopes ──────────────────────────
        long baseline = stageStats.Count > 0
            ? stageStats[0].Stats.WorkingSetBefore
            : (printedAnyAnalyzerRow ? firstRunWorkingSetBefore : 0);

        long peakFromStages = stageStats.Count > 0 ? stageStats.Max(s => s.Stats.WorkingSetAfter) : 0;
        long peak = Math.Max(peakFromStages, peakFromAnalyzers);

        if (peak > 0)
            ConsoleUx.MemoryTableFooter(peak, baseline);
    }

    private static void PrintDiagnosticsSummary(IReadOnlyList<AnalyzerRunResult> runs)
    {
        if (runs.Count == 0)
            return;

        AnalyzerRunResult[] topSlow = runs
            .OrderByDescending(r => r.Duration)
            .Take(5)
            .ToArray();

        ConsoleUx.TopSlowAnalyzers(topSlow);
    }

    private static long? TryGetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }
}
