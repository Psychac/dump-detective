namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// The <c>heap.dominators</c> capability — a new capability name (not previously in
/// <see cref="Artifacts.CapabilityVocabulary"/>) covering what
/// <c>IHeapAnalysisCache.TryGetDominatorTreeProvider</c>/<c>TryGetReachableAddressProvider</c>/
/// <c>TryGetThreadRetentionProvider</c> jointly provide today. Kept as one capability rather than
/// three: all three are backed by the same Stage A/B dominator-tree build and are gated together in
/// practice (see docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — splitting
/// them would suggest an independence the underlying index doesn't have.
/// </summary>
public interface IHeapDominatorQuery
{
    /// <summary>Mirrors <c>IDominatorTreeProvider</c> — "what would freeing this object free?"</summary>
    bool TryGetRetainedSize(ulong address, out ulong retainedBytes);

    bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress);

    /// <summary>Mirrors <c>IReachableAddressProvider</c>.</summary>
    bool IsReachableFromRoot(ulong address);

    /// <summary>Mirrors <c>IThreadRetentionProvider</c> — "how much would become collectible if this
    /// thread exited?", keyed by the thread's OS id.</summary>
    bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes);
}
