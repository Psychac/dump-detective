using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Gc;

/// <summary>
/// Runs the retyped <see cref="WeakReferenceAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. Uses
/// <see cref="TypedResourceCandidateStreamingLegacyAdapter{TSelf, TSdkAnalyzer}"/>, the same shared
/// base <c>TimerLeakAnalyzer</c>/<c>SqlCommandAnalyzer</c>/etc. use, so <c>context.HeapObjects</c>
/// gives the inner analyzer every WeakReference-shaped instance the pipeline's one shared
/// heap-index scan already found. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c> marker directly,
/// same reasoning as <c>DbConnectionAnalyzerLegacyAdapter</c>'s/<c>TimerLeakAnalyzerLegacyAdapter</c>'s
/// remarks — the pre-retyping analyzer only requests Stage A (reachability), never
/// <c>IRequiresDominatorTreeIndex</c>: Stage B's dominator tree is consumed opportunistically when
/// already available for other reasons, never required by this analyzer itself.
/// </remarks>
public sealed class WeakReferenceAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<WeakReferenceAnalyzerLegacyAdapter, WeakReferenceAnalyzer>, IRequiresReachableGraphIndex
{
    public WeakReferenceAnalyzerLegacyAdapter() : base(new WeakReferenceAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => WeakReferenceAnalyzer.IsCandidateType(typeName);
}
