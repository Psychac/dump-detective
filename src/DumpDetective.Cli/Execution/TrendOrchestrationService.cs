using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Nodes;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;

using System.Diagnostics;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Orchestrates a multi-dump trend analysis run: N per-artifact pipelines → trend synthesis →
/// output, all as one <see cref="PipelineGraph"/> — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 4. Node types
/// (Load/Index/Analyze) are the same ones <see cref="SingleDumpOrchestrationService"/> uses for its
/// single-dump path, one instance per trend dump path, namespaced via
/// <see cref="ArtifactPipelineKeys.ForArtifact"/> so N artifacts' nodes share one
/// <see cref="PipelineNodeContext"/> without colliding.
/// </summary>
internal sealed class TrendOrchestrationService(
    IDumpLoader dumpLoader,
    AnalyzerExecutionService analyzerExecutionService,
    ReportBuilderFacade reportBuilderFacade,
    TrendAnalyzer trendAnalyzer,
    ReportOutputWriter outputWriter)
{
    public async Task<int> ExecuteAsync(
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        IReadOnlyList<string> trendDumpPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        ConsoleUx.Header("DumpDetective Trend Analysis");
        ConsoleUx.Info($"Trend dumps ({trendDumpPaths.Count}): {string.Join(" -> ", trendDumpPaths.Select(Path.GetFileName))}");
        ConsoleUx.Info($"Running {activeAnalyzers.Count} analyzers per dump...");
        if (resolved.DiagnosticMode)
            ConsoleUx.Info(AnalysisSummaryFormatter.FormatConfigSummary(resolved, activeAnalyzers));

        // Session-level state: holds the artifacts' combined runs and the one trend report/output
        // this run produces, regardless of N — the same role SingleDumpNodeFactory's one artifact
        // state plays for the single-dump path.
        var sessionState = new SingleDumpPipelineState
        {
            Resolved = resolved,
            AllAnalyzers = allAnalyzers,
            ActiveAnalyzers = activeAnalyzers
        };

        var artifactStates = new List<(SingleDumpPipelineState State, ArtifactPipelineKeys Keys)>(trendDumpPaths.Count);
        var nodes = new List<IPipelineNode>();

        for (int i = 0; i < trendDumpPaths.Count; i++)
        {
            string dumpPath = trendDumpPaths[i];
            string fileLabel = $"[{Path.GetFileName(dumpPath)}]";
            ArtifactPipelineKeys keys = ArtifactPipelineKeys.ForArtifact($"dump{i}");

            var artifactState = new SingleDumpPipelineState
            {
                Resolved = resolved with { DumpPath = dumpPath },
                AllAnalyzers = allAnalyzers,
                ActiveAnalyzers = activeAnalyzers
            };
            artifactStates.Add((artifactState, keys));

            nodes.Add(new LoadDumpNode(dumpLoader, keys, id: $"{fileLabel} Load dump"));
            nodes.Add(new BuildHeapIndexNode(analyzerExecutionService, keys, id: $"{fileLabel} Scan + Index heap"));
            nodes.Add(new RunAnalyzersNode(analyzerExecutionService, keys, id: $"{fileLabel} Run analyzers", mode: "Trend"));
        }

        nodes.Add(new TrendBuildReportNode(reportBuilderFacade, trendAnalyzer, artifactStates));
        nodes.Add(new WriteOutputNode(outputWriter));

        PipelineGraph graph = PipelineGraphBuilder.Build(nodes);

        var context = new PipelineNodeContext();
        context.Set(SingleDumpPipelineKeys.State, sessionState);
        foreach ((SingleDumpPipelineState state, ArtifactPipelineKeys keys) in artifactStates)
            context.Set(keys.State, state);

        var observer = new ConsoleMemoryPipelineObserver(sessionState);
        try
        {
            await new PipelineExecutor().RunAsync(graph, context, cancellationToken, observer);
        }
        finally
        {
            sessionState.Dispose();
            foreach ((SingleDumpPipelineState state, _) in artifactStates)
                state.Dispose();
        }

        if (resolved.DiagnosticMode)
        {
            int success = sessionState.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Success);
            int failed = sessionState.Runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
            int skippedByFilter = sessionState.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter);
            int skippedByCancellation = sessionState.Runs.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation);
            int findings = sessionState.Runs.Sum(r => r.FindingCount);
            ConsoleUx.RunStatusSummary(success, failed, skippedByFilter, skippedByCancellation, findings);
            SingleDumpConsolePresenter.PrintDiagnosticsSummary(sessionState.Runs);
        }

        if (resolved.Diagnostics.EnableMemoryDiagnostics)
            SingleDumpConsolePresenter.PrintMemorySummary(sessionState.Runs, sessionState.StageMemoryStats);

        totalStopwatch.Stop();
        ConsoleUx.Footer(totalStopwatch.Elapsed);

        return sessionState.Runs.Any(r => r.Status == AnalyzerExecutionStatus.Failed)
            ? ExitCodes.AnalysisFailure
            : ExitCodes.Success;
    }
}
