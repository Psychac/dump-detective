namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// The <c>heap.reachability</c> capability — split out from <see cref="IHeapDominatorQuery"/> 2026-09-10
/// (see docs/refactor/modularity/phase-1-sdk-review-findings.md item 4) because it's backed by a
/// genuinely different, independently-gated build stage: reachability is a **Stage A** product
/// (the reverse-edge index + walk), while everything left in <see cref="IHeapDominatorQuery"/> is
/// **Stage B** (the full dominator tree, gated on an analyzer implementing
/// <c>IRequiresDominatorTreeIndex</c> — genuinely optional even when Stage A succeeds). Bundling
/// both behind one capability meant a session where Stage A succeeded but Stage B didn't (or wasn't
/// requested) had no way to expose reachability alone. Mirrors <c>IReachableAddressProvider</c>.
/// </summary>
public interface IHeapReachabilityQuery
{
    bool IsReachableFromRoot(ulong address);
}
