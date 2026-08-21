namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Builds the dominator child index's row-ordered CSR — §10.4 (Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): "what would freeing this
/// object free, one level down," row-aligned with <c>DominatorReachableAddresses</c> via
/// <see cref="DominatorRowMapping"/>. Merges two edge sources into one per-row child list:
/// <see cref="DominatorTreeComputeResult.ChildOffsets"/>/<c>ChildTargets</c> (real dominator-tree
/// edges) and <see cref="LeafFoldResult.FoldedLeafOffsets"/>/<c>FoldedLeafOldIds</c> (folded leaves,
/// §D8/§10.5, included as ordinary children per §5's original motivation). The virtual root itself
/// is excluded — it has no address to key a row on; its direct children (real GC roots) are already
/// identifiable via <c>DominatorImmediateDominatorAddresses</c>' <c>0</c> sentinel.
/// </summary>
internal static class DominatorChildIndexBuilder
{
    public static DominatorChildIndexBuildResult Build(ReachableGraph graph, DominatorTreeComputeResult tree, int[] oldIdToRow)
    {
        LeafFoldResult fold = tree.LeafFold;
        int n = graph.NodeCount;

        var childCountByRow = new int[n];
        for (int parentNewId = 0; parentNewId < tree.VirtualRoot; parentNewId++)
        {
            int parentRow = oldIdToRow[fold.NewToOldId[parentNewId]];
            childCountByRow[parentRow] += tree.ChildOffsets[parentNewId + 1] - tree.ChildOffsets[parentNewId];
        }
        for (int parentNewId = 0; parentNewId < fold.ReducedNodeCount; parentNewId++)
        {
            int parentRow = oldIdToRow[fold.NewToOldId[parentNewId]];
            childCountByRow[parentRow] += fold.FoldedLeafOffsets[parentNewId + 1] - fold.FoldedLeafOffsets[parentNewId];
        }

        var childOffsetsByRow = new int[n + 1];
        for (int row = 0; row < n; row++)
            childOffsetsByRow[row + 1] = childOffsetsByRow[row] + childCountByRow[row];

        var childAddressesByRow = new ulong[childOffsetsByRow[n]];
        var cursor = (int[])childOffsetsByRow.Clone();
        for (int parentNewId = 0; parentNewId < tree.VirtualRoot; parentNewId++)
        {
            int parentRow = oldIdToRow[fold.NewToOldId[parentNewId]];
            for (int e = tree.ChildOffsets[parentNewId]; e < tree.ChildOffsets[parentNewId + 1]; e++)
            {
                int childOldId = fold.NewToOldId[tree.ChildTargets[e]];
                childAddressesByRow[cursor[parentRow]++] = graph.Addresses[childOldId];
            }
        }
        for (int parentNewId = 0; parentNewId < fold.ReducedNodeCount; parentNewId++)
        {
            int parentRow = oldIdToRow[fold.NewToOldId[parentNewId]];
            for (int e = fold.FoldedLeafOffsets[parentNewId]; e < fold.FoldedLeafOffsets[parentNewId + 1]; e++)
                childAddressesByRow[cursor[parentRow]++] = graph.Addresses[fold.FoldedLeafOldIds[e]];
        }

        return new DominatorChildIndexBuildResult(childOffsetsByRow, childAddressesByRow);
    }
}

internal sealed class DominatorChildIndexBuildResult
{
    public int[] ChildOffsetsByRow { get; }
    public ulong[] ChildAddressesByRow { get; }

    public DominatorChildIndexBuildResult(int[] childOffsetsByRow, ulong[] childAddressesByRow)
    {
        ChildOffsetsByRow = childOffsetsByRow;
        ChildAddressesByRow = childAddressesByRow;
    }
}
