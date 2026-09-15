namespace DumpDetective.Orchestration.Graph;

/// <summary>
/// Decides execution order/concurrency for a graph's nodes. Step 1: strictly sequential, in the
/// graph's topological order — these are single-artifact, session-scoped nodes with no
/// <c>IsThreadSafe</c>/artifact-parallelism axis yet. Real scheduling (respecting node thread-safety
/// and the project's hard "no concurrent dump loading" constraint) lands when artifacts multiply.
/// </summary>
public static class NodeScheduler
{
    public static IReadOnlyList<IPipelineNode> ScheduleSequential(PipelineGraph graph) => graph.OrderedNodes;
}
