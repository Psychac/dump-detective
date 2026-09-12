using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.reachability</c> capability, built for
/// <c>WeakReferenceAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md),
/// its first real consumer. Pure pass-through over the already-existing
/// <see cref="IReachableAddressProvider"/> Stage A product, the same "no branching beyond an
/// optional-provider null check" shape <c>HeapDominatorQuery</c>/<c>HeapRootQuery</c> already are —
/// constructed with an already-resolved provider (not a <c>Func</c>/cache reference), so
/// <c>AnalysisContext.HeapReachability</c> itself is the single "is Stage A's walk available for
/// this session at all" gate, matching <c>HeapDominators</c>'s own precedent for Stage B.
/// </summary>
internal sealed class HeapReachabilityQuery(IReachableAddressProvider provider) : IHeapReachabilityQuery
{
    public bool IsReachableFromRoot(ulong address) => provider.IsReachable(address);
}
