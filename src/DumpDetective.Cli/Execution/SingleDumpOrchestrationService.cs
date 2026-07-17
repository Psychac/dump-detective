using DumpDetective.Analysis.Insight;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Services;

using System.Diagnostics;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Orchestrates a single-dump analysis run: stages → pipeline runner → console summary → exit code.
/// </summary>
internal sealed class SingleDumpOrchestrationService(
    SingleDumpStageFactory stageFactory)
{
    private readonly SingleDumpStageFactory _stageFactory = stageFactory;

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

        IReadOnlyList<IAnalysisStage> stages = _stageFactory.BuildStages();

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
            SingleDumpConsolePresenter.PrintInsights(state.Insights, resolved.DiagnosticMode);

        if (resolved.DiagnosticMode)
        {
            int success = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success);
            int failed = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
            int skippedByFilter = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter);
            int skippedByCancellation = state.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation);
            int findings = state.Runs.Sum(r => r.FindingCount);
            ConsoleUx.RunStatusSummary(success, failed, skippedByFilter, skippedByCancellation, findings);
            SingleDumpConsolePresenter.PrintDiagnosticsSummary(state.Runs);
        }

        if (resolved.Diagnostics.EnableMemoryDiagnostics)
            SingleDumpConsolePresenter.PrintMemorySummary(state.Runs, state.StageMemoryStats);

        totalStopwatch.Stop();
        ConsoleUx.Footer(totalStopwatch.Elapsed);

        return state.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed)
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }

    private static long? TryGetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }
}
