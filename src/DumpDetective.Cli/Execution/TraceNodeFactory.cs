using DumpDetective.Cli.Pipeline.Nodes;
using DumpDetective.Orchestration.Graph;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Builds a trace artifact's own load/index/analyze/report node triple-plus-one — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5f. Used both for a
/// standalone trace-only run (<see cref="TraceOrchestrationService"/>) and, appended to a dump
/// artifact's own nodes, for a combined dump+trace session
/// (<see cref="DumpOrchestrationService"/>) — the same node types either way, differing only
/// in what else shares the <see cref="PipelineNodeContext"/>, which is the whole point of this
/// phase (docs/refactor/modularity/phase-4-session-orchestration.md's "Goal" section).
/// </summary>
internal static class TraceNodeFactory
{
    public static IReadOnlyList<IPipelineNode> BuildNodes() =>
        [
            new BuildTraceIndexNode(id: "Index trace"),
            new RunTraceAnalyzersNode(id: "Run trace analyzers"),
            new WriteTraceReportNode(id: "Write trace report"),
        ];
}
