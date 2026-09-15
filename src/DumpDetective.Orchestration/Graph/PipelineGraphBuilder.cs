namespace DumpDetective.Orchestration.Graph;

/// <summary>
/// Builds a <see cref="PipelineGraph"/> from a set of nodes by topologically sorting on their
/// declared <see cref="IPipelineNode.Produces"/>/<see cref="IPipelineNode.Consumes"/> keys
/// (Kahn's algorithm), rather than trusting the order nodes were handed in.
/// </summary>
public static class PipelineGraphBuilder
{
    public static PipelineGraph Build(IReadOnlyList<IPipelineNode> nodes)
    {
        Dictionary<string, IPipelineNode> producerByKey = new(StringComparer.Ordinal);
        foreach (IPipelineNode node in nodes)
        {
            foreach (string key in node.Produces)
            {
                if (producerByKey.TryGetValue(key, out IPipelineNode? existing))
                    throw new InvalidOperationException(
                        $"Data key '{key}' is produced by both '{existing.Id}' and '{node.Id}'.");

                producerByKey[key] = node;
            }
        }

        Dictionary<IPipelineNode, List<IPipelineNode>> dependents = nodes.ToDictionary(n => n, _ => new List<IPipelineNode>());
        Dictionary<IPipelineNode, int> remainingDependencies = nodes.ToDictionary(n => n, _ => 0);

        foreach (IPipelineNode node in nodes)
        {
            var producerNodes = new HashSet<IPipelineNode>();
            foreach (string key in node.Consumes)
            {
                if (!producerByKey.TryGetValue(key, out IPipelineNode? producer))
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' consumes data key '{key}' that no node produces.");

                producerNodes.Add(producer);
            }

            foreach (IPipelineNode producer in producerNodes)
                dependents[producer].Add(node);

            remainingDependencies[node] = producerNodes.Count;
        }

        var ready = new Queue<IPipelineNode>(nodes.Where(n => remainingDependencies[n] == 0));
        var ordered = new List<IPipelineNode>(nodes.Count);

        while (ready.Count > 0)
        {
            IPipelineNode node = ready.Dequeue();
            ordered.Add(node);

            foreach (IPipelineNode dependent in dependents[node])
            {
                if (--remainingDependencies[dependent] == 0)
                    ready.Enqueue(dependent);
            }
        }

        if (ordered.Count != nodes.Count)
            throw new InvalidOperationException("Pipeline graph has a cycle among its declared data dependencies.");

        return new PipelineGraph(ordered);
    }
}
