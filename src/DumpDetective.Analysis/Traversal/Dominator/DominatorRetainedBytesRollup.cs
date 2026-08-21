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

        // A folded leaf (§D8) has no dominator-tree id of its own, but as a leaf its own subtree is
        // just itself, so its retained bytes are simply its shallow size. This makes the per-type
        // total exact over *every* reachable node of that type, not just the ones that survived
        // folding.
        var retainedByMethodTable = new Dictionary<ulong, ulong>();
        for (int oldId = 0; oldId < graph.NodeCount; oldId++)
        {
            int newId = tree.LeafFold.OldToNewId[oldId];
            ulong nodeRetained = newId >= 0 ? tree.RetainedBytes[newId] : graph.ShallowSizes[oldId];
            ulong methodTable = graph.MethodTables[oldId];
            retainedByMethodTable[methodTable] = retainedByMethodTable.TryGetValue(methodTable, out ulong existing)
                ? existing + nodeRetained
                : nodeRetained;
        }

        return new DominatorRetainedBytesRollupResult(totalRetainedBytes, retainedByMethodTable);
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
