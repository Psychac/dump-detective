namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Whole-tree total retained bytes plus a per-<c>MethodTable</c> retained-bytes rollup — one O(N)
/// pass over a computed <see cref="DominatorTreeComputeResult"/>. Extracted from
/// <c>DominatorAnalyzer.TryComputeExactDominatorTree</c> (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) so both that Phase 2 caller
/// and <c>DiskBackedObjectIndexWriter.Build</c>'s Phase 1 <c>DominatorTreeMetadata</c> persistence use
/// the exact same computation instead of two copies of it.
/// </summary>
internal static class DominatorRetainedBytesRollup
{
    public static DominatorRetainedBytesRollupResult Compute(ReachableGraph graph, DominatorTreeComputeResult tree)
    {
        // tree.Idom has length VirtualRoot+1 (LT's array includes the virtual root's own slot), but
        // tree.RetainedBytes only covers the real (reduced-id) nodes 0..VirtualRoot-1 — stop one
        // short to avoid indexing RetainedBytes with the virtual root's own id.
        ulong totalRetainedBytes = 0;
        for (int i = 0; i < tree.VirtualRoot; i++)
        {
            if (tree.Idom[i] == tree.VirtualRoot)
                totalRetainedBytes += tree.RetainedBytes[i];
        }

        // A node's RetainedBytes already sums its whole subtree, so also adding a descendant of the
        // same MethodTable double-counts it — for a chain of same-typed nodes (linked list, tree of
        // same-typed nodes referencing each other) this inflates the type's total from O(bytes) to
        // O(bytes x depth), the exact shape this tool exists to catch as a leak. Walk the dominator
        // tree via its own child CSR (iterative, real-dump scale — same style as
        // DominatorTreeReaderProvider.EnumerateRetainedSet), keeping a depth-ordered ancestor stack
        // so a node's retained bytes are only credited to its type's bucket when no ancestor already
        // claimed that type; each node's check is then O(1) amortized instead of an O(depth) Idom
        // walk per node. Folded leaves (LeafFoldResult.FoldedLeafOldIds, §10.5) are walked alongside
        // their surviving parent so they get the same exclusion check against their parent's
        // ancestor chain — not unconditionally attributed to their own type, which is what produced
        // the double count in the first place when a folded leaf shares its parent's type.
        var retainedByMethodTable = new Dictionary<ulong, ulong>();
        var ancestorStack = new Stack<(int Depth, ulong MethodTable)>();
        var ancestorMethodTableCounts = new Dictionary<ulong, int>();
        var traversal = new Stack<int>();
        LeafFoldResult leafFold = tree.LeafFold;

        for (int e = tree.ChildOffsets[tree.VirtualRoot]; e < tree.ChildOffsets[tree.VirtualRoot + 1]; e++)
            traversal.Push(tree.ChildTargets[e]);

        while (traversal.Count > 0)
        {
            int newId = traversal.Pop();
            int nodeDepth = tree.Depth[newId];

            while (ancestorStack.Count > 0 && ancestorStack.Peek().Depth >= nodeDepth)
            {
                (int _, ulong poppedMethodTable) = ancestorStack.Pop();
                if (--ancestorMethodTableCounts[poppedMethodTable] == 0)
                    ancestorMethodTableCounts.Remove(poppedMethodTable);
            }

            ulong methodTable = graph.MethodTables[leafFold.NewToOldId[newId]];
            CreditIfNoAncestorOfSameType(retainedByMethodTable, ancestorMethodTableCounts, methodTable, tree.RetainedBytes[newId]);

            ancestorStack.Push((nodeDepth, methodTable));
            ancestorMethodTableCounts[methodTable] = ancestorMethodTableCounts.TryGetValue(methodTable, out int count) ? count + 1 : 1;

            for (int leaf = leafFold.FoldedLeafOffsets[newId]; leaf < leafFold.FoldedLeafOffsets[newId + 1]; leaf++)
            {
                int leafOldId = leafFold.FoldedLeafOldIds[leaf];
                ulong leafMethodTable = graph.MethodTables[leafOldId];
                CreditIfNoAncestorOfSameType(retainedByMethodTable, ancestorMethodTableCounts, leafMethodTable, graph.ShallowSizes[leafOldId]);
            }

            for (int e = tree.ChildOffsets[newId]; e < tree.ChildOffsets[newId + 1]; e++)
                traversal.Push(tree.ChildTargets[e]);
        }

        return new DominatorRetainedBytesRollupResult(totalRetainedBytes, retainedByMethodTable);
    }

    private static void CreditIfNoAncestorOfSameType(
        Dictionary<ulong, ulong> retainedByMethodTable,
        Dictionary<ulong, int> ancestorMethodTableCounts,
        ulong methodTable,
        ulong bytes)
    {
        if (ancestorMethodTableCounts.ContainsKey(methodTable))
            return;

        retainedByMethodTable[methodTable] = retainedByMethodTable.TryGetValue(methodTable, out ulong existing)
            ? existing + bytes
            : bytes;
    }
}

internal sealed class DominatorRetainedBytesRollupResult
{
    public ulong TotalRetainedBytes { get; }
    public IReadOnlyDictionary<ulong, ulong> RetainedBytesByMethodTable { get; }

    public DominatorRetainedBytesRollupResult(ulong totalRetainedBytes, IReadOnlyDictionary<ulong, ulong> retainedBytesByMethodTable)
    {
        TotalRetainedBytes = totalRetainedBytes;
        RetainedBytesByMethodTable = retainedBytesByMethodTable;
    }
}
