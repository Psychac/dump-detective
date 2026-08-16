namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// §D8: excludes single-parent leaves (out-degree 0, in-degree 1 — "foldable") from the node set
/// fed to Lengauer-Tarjan, folding each one's shallow size into its sole parent's accumulator
/// instead of giving it a full node/LT-array slot. Purely graph-structural — no
/// <c>MethodTableHasOutgoingRefs</c>/type-metadata lookup needed, just the degree arrays
/// <see cref="ReachableGraphWalker"/> already builds. Nodes with out-degree 0 and in-degree &gt;1
/// (shared leaves) are never folded — determining their <c>idom</c> is exactly what LT's
/// semidominator computation exists to do, and no local rule can substitute for it.
///
/// Operates on plain arrays (no <c>ReachableGraph</c>/ClrHeap dependency), so it's directly
/// unit-testable with hand-built graphs, same pattern as <c>LengauerTarjanTests</c>.
/// </summary>
internal static class LeafFolder
{
    public static LeafFoldResult Fold(
        int nodeCount,
        int[] outDegree,
        int[] inDegree,
        int[] fwdOffsets,
        int[] fwdTargets,
        int[] revOffsets,
        int[] revTargets,
        ulong[] shallowSizes,
        bool[]? isRoot = null)
    {
        var isFoldable = new bool[nodeCount];
        int foldableCount = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            // A GC root must never be folded even if out-degree 0 and in-degree 1: it has an
            // "invisible" incoming edge from LT's virtual root that this CSR doesn't represent, so
            // folding it into its one real-object parent would silently misattribute its
            // directly-rooted status (and retained bytes) to that parent instead.
            if (isRoot is not null && isRoot[i])
                continue;

            if (outDegree[i] == 0 && inDegree[i] == 1)
            {
                isFoldable[i] = true;
                foldableCount++;
            }
        }

        // Assign dense new ids to surviving (non-foldable) nodes only, preserving relative order.
        var oldToNewId = new int[nodeCount];
        var newToOldId = new int[nodeCount - foldableCount];
        int nextNewId = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            if (isFoldable[i])
            {
                oldToNewId[i] = -1;
                continue;
            }

            oldToNewId[i] = nextNewId;
            newToOldId[nextNewId] = i;
            nextNewId++;
        }

        int reducedNodeCount = nextNewId;
        var foldedBytesByNewId = new ulong[reducedNodeCount];

        // Fold each leaf's shallow size into its sole parent. A foldable leaf's single predecessor
        // is guaranteed to survive: any node with an edge to another node has out-degree >= 1, so it
        // can never itself be foldable (foldable requires out-degree == 0).
        for (int i = 0; i < nodeCount; i++)
        {
            if (!isFoldable[i])
                continue;

            int parentOldId = revTargets[revOffsets[i]]; // inDegree[i] == 1 — exactly one entry
            int parentNewId = oldToNewId[parentOldId];
            foldedBytesByNewId[parentNewId] += shallowSizes[i];
        }

        // Rebuild forward CSR over the reduced id space, dropping any edge whose target was folded
        // away (its contribution is already captured in foldedBytesByNewId).
        var reducedOutDegree = new int[reducedNodeCount];
        for (int newId = 0; newId < reducedNodeCount; newId++)
        {
            int oldId = newToOldId[newId];
            for (int e = fwdOffsets[oldId]; e < fwdOffsets[oldId + 1]; e++)
            {
                if (oldToNewId[fwdTargets[e]] >= 0)
                    reducedOutDegree[newId]++;
            }
        }

        var reducedFwdOffsets = new int[reducedNodeCount + 1];
        for (int newId = 0; newId < reducedNodeCount; newId++)
            reducedFwdOffsets[newId + 1] = reducedFwdOffsets[newId] + reducedOutDegree[newId];

        long reducedEdgeCount = reducedFwdOffsets[reducedNodeCount];
        var reducedFwdTargets = new int[reducedEdgeCount];
        var reducedInDegree = new int[reducedNodeCount];
        var cursor = (int[])reducedFwdOffsets.Clone();

        for (int newId = 0; newId < reducedNodeCount; newId++)
        {
            int oldId = newToOldId[newId];
            for (int e = fwdOffsets[oldId]; e < fwdOffsets[oldId + 1]; e++)
            {
                int oldTarget = fwdTargets[e];
                int newTarget = oldToNewId[oldTarget];
                if (newTarget < 0)
                    continue;

                reducedFwdTargets[cursor[newId]++] = newTarget;
                reducedInDegree[newTarget]++;
            }
        }

        var reducedRevOffsets = new int[reducedNodeCount + 1];
        for (int newId = 0; newId < reducedNodeCount; newId++)
            reducedRevOffsets[newId + 1] = reducedRevOffsets[newId] + reducedInDegree[newId];

        var reducedRevTargets = new int[reducedEdgeCount];
        var revCursor = (int[])reducedRevOffsets.Clone();
        for (int newFrom = 0; newFrom < reducedNodeCount; newFrom++)
        {
            for (int e = reducedFwdOffsets[newFrom]; e < reducedFwdOffsets[newFrom + 1]; e++)
            {
                int newTo = reducedFwdTargets[e];
                reducedRevTargets[revCursor[newTo]++] = newFrom;
            }
        }

        return new LeafFoldResult(
            reducedNodeCount,
            oldToNewId,
            newToOldId,
            reducedFwdOffsets,
            reducedFwdTargets,
            reducedRevOffsets,
            reducedRevTargets,
            foldedBytesByNewId);
    }
}

/// <summary>Result of <see cref="LeafFolder.Fold"/> — the LT-facing reduced graph.</summary>
internal sealed class LeafFoldResult
{
    public int ReducedNodeCount { get; }
    /// <summary>Length = original <c>nodeCount</c>; -1 for folded-away (foldable leaf) nodes.</summary>
    public int[] OldToNewId { get; }
    /// <summary>Length = <see cref="ReducedNodeCount"/>.</summary>
    public int[] NewToOldId { get; }
    public int[] ReducedFwdOffsets { get; }
    public int[] ReducedFwdTargets { get; }
    public int[] ReducedRevOffsets { get; }
    public int[] ReducedRevTargets { get; }
    /// <summary>
    /// Extra shallow-size bytes folded into each surviving node from its foldable-leaf children,
    /// indexed by new id — add to the node's own subtree sum during the retained-bytes rollup.
    /// </summary>
    public ulong[] FoldedBytesByNewId { get; }

    public LeafFoldResult(
        int reducedNodeCount,
        int[] oldToNewId,
        int[] newToOldId,
        int[] reducedFwdOffsets,
        int[] reducedFwdTargets,
        int[] reducedRevOffsets,
        int[] reducedRevTargets,
        ulong[] foldedBytesByNewId)
    {
        ReducedNodeCount = reducedNodeCount;
        OldToNewId = oldToNewId;
        NewToOldId = newToOldId;
        ReducedFwdOffsets = reducedFwdOffsets;
        ReducedFwdTargets = reducedFwdTargets;
        ReducedRevOffsets = reducedRevOffsets;
        ReducedRevTargets = reducedRevTargets;
        FoldedBytesByNewId = foldedBytesByNewId;
    }
}
