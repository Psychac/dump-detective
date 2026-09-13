namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// The <c>heap.dominators</c> capability — Stage B only (the full dominator-tree build, gated on an
/// analyzer implementing <c>IRequiresDominatorTreeIndex</c>, genuinely optional even when the
/// Stage A reachability walk succeeds). Covers what
/// <c>IHeapAnalysisCache.TryGetDominatorTreeProvider</c>/<c>TryGetThreadRetentionProvider</c>
/// jointly provide today — kept as one capability since both are backed by the same Stage B build
/// and gated together in practice. Reachability was split out to
/// <see cref="IHeapReachabilityQuery"/> 2026-09-10 (its own Stage A product, independently
/// available) — see docs/refactor/modularity/phase-1-sdk-review-findings.md item 4 for why bundling
/// them was wrong: a session where Stage A succeeded but Stage B didn't had no way to expose
/// reachability alone.
/// </summary>
public interface IHeapDominatorQuery
{
    /// <summary>Mirrors <c>IDominatorTreeProvider</c> — "what would freeing this object free?"</summary>
    bool TryGetRetainedSize(ulong address, out ulong retainedBytes);

    bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress);

    /// <summary>Mirrors <c>IThreadRetentionProvider</c> — "how much would become collectible if this
    /// thread exited?", keyed by the thread's OS id.</summary>
    bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes);

    /// <summary>
    /// Every object in <paramref name="rootAddress"/>'s dominator subtree (mirrors
    /// <c>IDominatorTreeProvider.EnumerateRetainedSet</c>) — the per-object companion to
    /// <see cref="TryGetRetainedSize"/>'s aggregate byte total. Added 2026-09-13 for
    /// <c>StaticRootLeakDetector</c>'s retyping — its first real consumer; every prior
    /// <see cref="IHeapDominatorQuery"/> consumer only needed the aggregate. Deliberately uncapped
    /// (no <c>maxNodes</c>/<c>maxDepth</c> parameter), matching this project's "no sampling"
    /// convention and the pre-retyping analyzer's own unbounded walk — callers needing a bounded
    /// walk cap at their own call site, the same way every other uncapped Tier-1 stream works.
    /// </summary>
    IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress);
}
