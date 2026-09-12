using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Runs the retyped <see cref="CollectionAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. Uses
/// <see cref="TypedResourceCandidateStreamingLegacyAdapter{TSelf, TSdkAnalyzer}"/>, the same shared
/// base <c>TimerLeakAnalyzer</c>/<c>WeakReferenceAnalyzer</c>/etc. use — an exact fit here, since the
/// pre-retyping analyzer's own <c>OnHeapEntry</c> already classified every heap object by name during
/// the pipeline's one shared heap-index scan, precisely the discovery shape this base assumes. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c> marker directly, same
/// reasoning as <c>WeakReferenceAnalyzer</c>'s/<c>TimerLeakAnalyzer</c>'s remarks — the pre-retyping
/// analyzer only requests Stage A (reachability, via <c>GetOrBuildValidRoots</c>/root-path search),
/// never <c>IRequiresDominatorTreeIndex</c>: Stage B's dominator tree (retained bytes) is consumed
/// opportunistically when already available for other reasons, never required by this analyzer
/// itself.
/// </remarks>
public sealed class CollectionAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<CollectionAnalyzerLegacyAdapter, CollectionAnalyzer>, IRequiresReachableGraphIndex
{
    public CollectionAnalyzerLegacyAdapter() : base(new CollectionAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => CollectionAnalyzer.IsCandidateType(typeName);

    protected override object? ResolveOptions(AnalysisOptions options) =>
        new CollectionAnalyzerOptions(options.Collection, options.ReferenceChain);
}
