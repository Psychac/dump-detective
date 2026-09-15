namespace DumpDetective.Orchestration.Graph;

/// <summary>
/// A set of nodes already ordered so that every node appears after everything it consumes.
/// </summary>
public sealed class PipelineGraph
{
    public IReadOnlyList<IPipelineNode> OrderedNodes { get; }

    internal PipelineGraph(IReadOnlyList<IPipelineNode> orderedNodes)
    {
        OrderedNodes = orderedNodes;
    }
}
