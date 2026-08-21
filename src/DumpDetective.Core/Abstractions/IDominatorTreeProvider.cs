namespace DumpDetective.Core.Abstractions;

/// <summary>
/// "What would freeing this object free?" — the exact dominator tree computed during Phase 1 (§10.4,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Backed entirely by disk
/// sections written by <c>DiskBackedObjectIndexWriter.Build</c>'s Stage B; there is no in-memory
/// equivalent — recomputing it per query would mean re-running the walk/fold/Lengauer-Tarjan
/// pipeline, so callers must treat a missing provider as "exact tree unavailable" and fall back to
/// a different strategy (e.g. the existing top-K heuristic) rather than simulate one.
/// </summary>
public interface IDominatorTreeProvider
{
    /// <summary>
    /// Returns <c>true</c> and the immediate-dominator address if <paramref name="address"/> was
    /// reachable when this tree was built. A dominator address of <c>0</c> means
    /// <paramref name="address"/> is a direct child of the virtual root (dominated only by a GC
    /// root, not by any other object).
    /// </summary>
    bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress);

    /// <summary>
    /// Returns <c>true</c> and the exact retained bytes (subtree sum, including
    /// <paramref name="address"/>'s own shallow size) if <paramref name="address"/> was reachable
    /// when this tree was built. <c>false</c> also covers a legacy cache.bin written before this
    /// column existed — see <c>DominatorTreeIndexReader</c>.
    /// </summary>
    bool TryGetRetainedBytes(ulong address, out ulong retainedBytes);

    /// <summary>
    /// Streams every object <paramref name="address"/> transitively dominates, including
    /// <paramref name="address"/> itself — the set of objects that would become collectible if
    /// <paramref name="address"/> were freed. Empty if <paramref name="address"/> wasn't reachable
    /// when this tree was built. Walks the persisted child index on demand; no resident array, so
    /// this can be large for an object near the tree's root — callers that only need the byte count
    /// should use <see cref="TryGetRetainedBytes"/> instead.
    /// </summary>
    IEnumerable<ulong> EnumerateRetainedSet(ulong address);

    /// <summary>Whole-tree total retained bytes (the sum of every direct GC-root child's retained bytes).</summary>
    ulong TotalRetainedBytes { get; }

    /// <summary>
    /// Returns <c>true</c> and the exact retained bytes summed over every reachable instance of
    /// <paramref name="methodTable"/>, whether or not each instance survived leaf-folding.
    /// </summary>
    bool TryGetRetainedBytesByMethodTable(ulong methodTable, out ulong retainedBytes);
}
