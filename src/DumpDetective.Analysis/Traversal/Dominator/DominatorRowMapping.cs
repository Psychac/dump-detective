namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Maps each reachable node's old (discovery-order) id to its row in a sorted-address array — §10.4
/// (Batch 2b, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Computed once
/// and shared between the idom section and the dominator child index, instead of each writer
/// re-deriving its own address-sorted order independently.
/// </summary>
internal static class DominatorRowMapping
{
    /// <param name="graph">Supplies each old id's address via <see cref="ReachableGraph.Addresses"/>.</param>
    /// <param name="sortedAddresses">
    /// Every reachable node's address, sorted ascending — normally
    /// <c>ReachableGraphWalkResult.ReachableAddresses</c>, already sorted, so this only binary-searches
    /// it rather than re-sorting from scratch.
    /// </param>
    /// <returns>Old id -&gt; row in <paramref name="sortedAddresses"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// A node's address wasn't found in <paramref name="sortedAddresses"/> — this should be
    /// impossible when both come from the same walk, so it's treated as a correctness bug in the
    /// caller rather than a recoverable condition.
    /// </exception>
    public static int[] Compute(ReachableGraph graph, ulong[] sortedAddresses)
    {
        var oldIdToRow = new int[graph.NodeCount];
        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
        {
            ulong address = graph.Addresses[oldId];
            int row = Array.BinarySearch(sortedAddresses, address);
            if (row < 0)
            {
                throw new InvalidOperationException(
                    $"Address {address:X} from the reachable graph was not found in its own walk's sorted address list.");
            }
            oldIdToRow[oldId] = row;
        }

        return oldIdToRow;
    }
}
