using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Nodes;
using DumpDetective.Core.Abstractions;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Core.Models;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Services;
using DumpDetective.Reporting.Services;

using System.Diagnostics;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Orchestrates a dump analysis run — single dump, N-dump trend, or either combined with a trace
/// artifact — as one <see cref="PipelineGraph"/>: nodes → pipeline executor → console summary →
/// exit code. Absorbed the former <c>TrendOrchestrationService</c> in migration step 6
/// (docs/refactor/modularity/phase-4-session-orchestration.md) — that class built the exact same
/// node types (Load/Index/Analyze) for N artifacts this class already built for one, just via its
/// own copy of the loop; the only real difference was which report node closes the run
/// (<see cref="BuildReportNode"/> for N=1, <see cref="TrendBuildReportNode"/> for N&gt;1), so
/// keeping two top-level orchestration classes for that one branch point was the duplication this
/// step's own "retire the old orchestrators" goal named. Renamed from
/// <c>SingleDumpOrchestrationService</c> once step 7 settled the CLI-shape question — the old name
/// stopped describing this class the moment it absorbed the trend (N-dump) case in step 6; kept the
/// dump-centric name rather than something session-generic (e.g. <c>SessionOrchestrationService</c>)
/// since this class still always requires at least one dump — a trace-only run has no dump artifact
/// at all and stays on <see cref="TraceOrchestrationService"/>'s own separate path.
/// </summary>
internal sealed class DumpOrchestrationService(
    IDumpLoader dumpLoader,
    AnalyzerExecutionService analyzerExecutionService,
    ReportBuilderFacade reportBuilderFacade,
    TrendAnalyzer trendAnalyzer,
    ReportOutputWriter outputWriter,
    SynthesisFindingEnricher synthesisFindingEnricher)
{
    private const string TemporaryAdaptiveIndexingNotice =
        "TEMP-ADAPTIVE-INDEXING: Auto mode uses a provisional dump-size threshold; tune memory-vs-disk selection with large-dump profiling.";

    /// <param name="dumpPaths">One path for a single-dump run, or N ordered oldest-to-newest for a
    /// trend run — the only thing that decides which of the two node shapes below gets built.</param>
    /// <param name="tracePath">
    /// When set, appends a trace artifact's own load/index/analyze/report nodes
    /// (<see cref="TraceNodeFactory"/>) to this run's nodes so both run through one shared
    /// <see cref="PipelineGraph"/>/<see cref="PipelineNodeContext"/> — migration step 5f. The two
    /// artifacts' nodes share no data keys, so relative ordering is just list order (dump nodes
    /// first, matching this method's own console output order). Only ever paired with a single
    /// dump path — <c>SessionValidator</c> rejects a trend+trace combination, and
    /// <c>DumpAnalysisService</c> checks for it earlier with a clearer message.
    /// </param>
    public async Task<int> ExecuteAsync(
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        IReadOnlyList<string> dumpPaths,
        CancellationToken cancellationToken,
        string? tracePath = null,
        IReadOnlyList<CoverageRecord>? coverage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isTrend = dumpPaths.Count > 1;
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        if (isTrend)
        {
            ConsoleUx.Header("DumpDetective Trend Analysis");
            ConsoleUx.Info($"Trend dumps ({dumpPaths.Count}): {string.Join(" -> ", dumpPaths.Select(Path.GetFileName))}");
            ConsoleUx.Info($"Running {activeAnalyzers.Count} analyzers per dump...");
        }
        else
        {
            ConsoleUx.Header("DumpDetective Analysis");
            ConsoleUx.DumpInfo(Path.GetFileName(dumpPaths[0]), TryGetFileSize(dumpPaths[0]));
            //ConsoleUx.Note(TemporaryAdaptiveIndexingNotice);
        }

        if (resolved.DiagnosticMode)
            ConsoleUx.Info(AnalysisSummaryFormatter.FormatConfigSummary(resolved, activeAnalyzers));

        using SingleDumpPipelineState sessionState = new()
        {
            Resolved = resolved,
            AllAnalyzers = allAnalyzers,
            ActiveAnalyzers = activeAnalyzers,
            Coverage = coverage ?? []
        };

        var artifactStates = new List<(SingleDumpPipelineState State, ArtifactPipelineKeys Keys)>();
        List<IPipelineNode> nodes = [];

        if (isTrend)
        {
            for (int i = 0; i < dumpPaths.Count; i++)
            {
                string dumpPath = dumpPaths[i];
                string fileLabel = $"[{Path.GetFileName(dumpPath)}]";
                ArtifactPipelineKeys keys = ArtifactPipelineKeys.ForArtifact($"dump{i}");

                var artifactState = new SingleDumpPipelineState
                {
                    Resolved = resolved with { DumpPath = dumpPath },
                    AllAnalyzers = allAnalyzers,
                    ActiveAnalyzers = activeAnalyzers,
                    Coverage = coverage ?? []
                };
                artifactStates.Add((artifactState, keys));

                nodes.Add(new LoadDumpNode(dumpLoader, keys, id: $"{fileLabel} Load dump"));
                nodes.Add(new BuildHeapIndexNode(analyzerExecutionService, keys, id: $"{fileLabel} Scan + Index heap"));
                nodes.Add(new RunAnalyzersNode(analyzerExecutionService, synthesisFindingEnricher, keys, id: $"{fileLabel} Run analyzers", mode: "Trend"));
            }

            nodes.Add(new TrendBuildReportNode(reportBuilderFacade, trendAnalyzer, artifactStates));
        }
        else
        {
            // N=1 collapses the "per-artifact state" and "session state" into the same object —
            // ArtifactPipelineKeys.SingleDump's key constants equal SingleDumpPipelineKeys' own, so
            // BuildReportNode/WriteOutputNode (hardcoded to the latter) read this same sessionState.
            ArtifactPipelineKeys keys = ArtifactPipelineKeys.SingleDump;
            nodes.Add(new LoadDumpNode(dumpLoader, keys, id: "Load dump"));
            nodes.Add(new BuildHeapIndexNode(analyzerExecutionService, keys, id: "Scan + Index heap"));
            nodes.Add(new RunAnalyzersNode(analyzerExecutionService, synthesisFindingEnricher, keys, id: "Run analyzers", mode: "Single"));
            nodes.Add(new BuildReportNode(reportBuilderFacade));
        }

        using TracePipelineState? traceState = tracePath is null
            ? null
            : new TracePipelineState { TracePath = tracePath, OutputPath = null };
        if (traceState is not null)
            nodes.AddRange(TraceNodeFactory.BuildNodes());

        nodes.Add(new WriteOutputNode(outputWriter));

        PipelineGraph graph = PipelineGraphBuilder.Build(nodes);

        var context = new PipelineNodeContext();
        context.Set(SingleDumpPipelineKeys.State, sessionState);
        foreach ((SingleDumpPipelineState state, ArtifactPipelineKeys keys) in artifactStates)
            context.Set(keys.State, state);
        if (traceState is not null)
            context.Set(TracePipelineKeys.State, traceState);

        var observer = new ConsoleMemoryPipelineObserver(sessionState);
        try
        {
            await new PipelineExecutor().RunAsync(graph, context, cancellationToken, observer);
        }
        finally
        {
            foreach ((SingleDumpPipelineState state, _) in artifactStates)
                state.Dispose();
        }

        if (traceState is not null)
            ConsoleUx.Info($"Trace half complete — {traceState.Observations.Count} observation(s), see the trace's own report.json.");

        // Cross-analyzer insights print for a single dump only — the trend path never printed them
        // (TrendOrchestrationService had no InsightEngine step), preserved as-is.
        if (!isTrend && sessionState.Insights.Count > 0)
            SingleDumpConsolePresenter.PrintInsights(sessionState.Insights, resolved.DiagnosticMode);

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

    private static long? TryGetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }
}
