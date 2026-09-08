namespace DumpDetective.Core.Abstractions;

/// <summary>
/// "Is this object reachable from a GC root?" Backed by the disk-backed
/// <c>DominatorReachableAddresses</c> section (see
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §5), populated by Stage
/// A's reachability walk during Phase 1. There is no in-memory equivalent — answering this
/// without the persisted section would require re-running the walk, so callers must treat a
/// missing provider as "reachability unknown" and fall back to a different strategy (or skip
/// the check) rather than simulate one.
/// </summary>
public interface IReachableAddressProvider
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="address"/> was visited by Stage A's reachability
    /// walk. <c>false</c> covers both "not reachable from any GC root" and "not a live heap
    /// object at all" — the section carries no separate signal for the two.
    /// </summary>
    bool IsReachable(ulong address);
}
