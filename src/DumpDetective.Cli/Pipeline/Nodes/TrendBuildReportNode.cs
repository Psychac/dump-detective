using DumpDetective.Sdk.Synthesis;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Execution;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Trend;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Synthesizes N artifacts' analyzer runs into one trend report — replaces the inline
/// snapshot-building/comparison block that used to live in <c>TrendOrchestrationService</c>. See
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 4.
/// </summary>
/// <remarks>
/// <paramref name="artifactStates"/> is given directly (not resolved purely through
/// <see cref="PipelineNodeContext"/> keys) because this node needs each artifact's full
/// <see cref="SingleDumpPipelineState"/> — runs, incident context, elapsed, memory stats — not just
/// its analyzer-run list; <see cref="Consumes"/> still declares each artifact's
/// <see cref="ArtifactPipelineKeys.AnalyzerRuns"/> key purely so <c>PipelineGraphBuilder</c> orders
/// this node after every artifact's analysis completes, matching the convention every other node in
/// this pipeline already follows (state read directly, context keys used for DAG edges).
/// </remarks>
internal sealed class TrendBuildReportNode(
    ReportBuilderFacade reportBuilderFacade,
    TrendAnalyzer trendAnalyzer,
    IReadOnlyList<(SingleDumpPipelineState State, ArtifactPipelineKeys Keys)> artifactStates) : IPipelineNode
{
    public string Id => "Build trend report";
    public IReadOnlyCollection<string> Produces { get; } = [SingleDumpPipelineKeys.ReportDocument];
    public IReadOnlyCollection<string> Consumes { get; } =
        artifactStates.Select(a => a.Keys.AnalyzerRuns).ToArray();

    public ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SingleDumpPipelineState sessionState = context.Get<SingleDumpPipelineState>(SingleDumpPipelineKeys.State);

        var executions = new List<TrendDumpExecution>(artifactStates.Count);
        foreach ((SingleDumpPipelineState state, _) in artifactStates)
        {
            executions.Add(new TrendDumpExecution(
                state.Resolved.DumpPath,
                state.Runs,
                state.AnalysisElapsed,
                state.IncidentContext!,
                DateTime.UtcNow,
                state.StageMemoryStats,
                MemoryStats: null,
                Observations: state.Observations));
        }

        AnalysisSnapshot[] snapshots = executions
            .Select((execution, index) => BuildSnapshot(index, execution))
            .ToArray();

        IReadOnlyList<Observation>[] observationsPerSnapshot = executions.Select(e => e.Observations).ToArray();

        AnalysisSnapshot baseline = snapshots[0];
        AnalysisSnapshot current = snapshots[^1];
        FindingLifecycleResult lifecycle = FindingLifecycleComparer.Compare(baseline, current);

        TrendReportData trendData = new(
            Steps: trendAnalyzer.CompareSeries(snapshots, observationsPerSnapshot),
            Overall: trendAnalyzer.CompareAll(baseline, current, observationsPerSnapshot[0], observationsPerSnapshot[^1]),
            Timeline: trendAnalyzer.ExtractTimeline(snapshots, observationsPerSnapshot),
            ScopedTimeline: trendAnalyzer.ExtractScopedTimeline(snapshots),
            Snapshots: snapshots,
            NewFindings: lifecycle.NewFindings,
            PersistentFindings: lifecycle.PersistentFindings,
            ResolvedFindings: lifecycle.ResolvedFindings,
            SeverityChanges: lifecycle.SeverityChanges);

        if (sessionState.Resolved.DiagnosticMode)
            TrendConsolePresenter.PrintTrendOverallSummary(trendData, sessionState.Resolved.DiagnosticMode);

        TrendDumpExecution currentExecution = executions[^1];
        AnalysisReportDocument trendDoc = reportBuilderFacade.BuildTrendReportDocument(
            currentExecution.Runs,
            sessionState.PipelineStopwatch.Elapsed,
            currentExecution.IncidentContext,
            trendData);

        sessionState.Observations = executions.SelectMany(e => e.Observations).ToArray();
        sessionState.ReportDocument = trendDoc;
        sessionState.Runs = executions.SelectMany(e => e.Runs).ToArray();
        sessionState.AnalysisElapsed = sessionState.PipelineStopwatch.Elapsed;

        context.Set(SingleDumpPipelineKeys.ReportDocument, trendDoc);
        return ValueTask.CompletedTask;
    }

    private static AnalysisSnapshot BuildSnapshot(int index, TrendDumpExecution execution)
    {
        Dictionary<string, AnalyzerDomainResult> domains = new(StringComparer.Ordinal);
        var findings = new List<Claim>();

        foreach (AnalyzerRunResult run in execution.Runs)
        {
            if (run.Status != AnalyzerExecutionStatus.Success || run.Result is null)
                continue;

            domains[run.AnalyzerName] = run.Result;
            findings.AddRange(run.Findings);
        }

        return new AnalysisSnapshot(
            Index: index,
            DumpPath: execution.DumpPath,
            Runs: execution.Runs,
            Findings: findings,
            DomainResults: domains,
            GeneratedAtUtc: execution.GeneratedAtUtc,
            IncidentContext: execution.IncidentContext);
    }
}
