using DumpDetective.Sdk.Identity;

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
/// <summary>
/// How many reachable objects of type <paramref name="Dominated"/> have an object of type <paramref name="Dominator"/> as their immediate
/// dominator. Types are canonical, so pairs that differ only in a compiler-assigned ordinal or generic instantiation arrive merged.
/// </summary>
public readonly record struct DominatedTypePair(TypeRef Dominator, TypeRef Dominated, long ObjectCount);

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
    /// <c>StaticRootAnalyzer</c>'s retyping — its first real consumer; every prior
    /// <see cref="IHeapDominatorQuery"/> consumer only needed the aggregate. Deliberately uncapped
    /// (no <c>maxNodes</c>/<c>maxDepth</c> parameter), matching this project's "no sampling"
    /// convention and the pre-retyping analyzer's own unbounded walk — callers needing a bounded
    /// walk cap at their own call site, the same way every other uncapped Tier-1 stream works.
    /// </summary>
    IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress);

    /// <summary>
    /// Aggregate retained bytes across every live instance of <paramref name="type"/> — mirrors
    /// <c>IDominatorTreeProvider.TryGetRetainedBytesByMethodTable</c>. Added 2026-09-13 for
    /// <c>DominatorAnalyzer</c>'s retyping: distinct in shape from <see cref="TryGetRetainedSize"/>,
    /// which is per-*address*, not per-*type* — this sums the dominator subtree of every instance
    /// sharing <paramref name="type"/>'s identity, computed once during the Lengauer-Tarjan build,
    /// not derived by calling <see cref="TryGetRetainedSize"/> per instance here. Keyed by
    /// <see cref="TypeRef"/> rather than a raw method table, matching this interface's other
    /// members — the dump-side implementation reads <see cref="TypeRef.MethodTable"/> internally.
    /// Returns <c>false</c> when <paramref name="type"/> has no dump-local method table or the tree
    /// has no record for it.
    /// </summary>
    bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes);

    /// <summary>
    /// Every distinct (immediate dominator type, dominated type) pair over all reachable objects, with the number of objects having it. Exact
    /// and uncapped. Empty when the tree cannot answer. Added for the dossier's relations (docs/ReportStructure/m2b-relations-plan.md, step 4b).
    /// </summary>
    IReadOnlyList<DominatedTypePair> GetImmediateDominatorTypePairs() => [];
}
