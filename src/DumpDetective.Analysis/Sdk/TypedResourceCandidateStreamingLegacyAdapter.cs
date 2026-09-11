using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Shared base for Tier-2 adapters whose candidate discovery is "find every heap object whose type
/// name is in a fixed set, during the pipeline's one shared heap-index scan" — the shape
/// <c>SqlConnectionPoolAnalyzerLegacyAdapter</c> (the Tier-2 pilot) originally hand-rolled directly.
/// Extracted once a second real consumer was known to be coming — the Tier-2 wave has roughly a
/// dozen more analyzers built on this exact discovery shape (<c>WcfChannelAnalyzer</c>,
/// <c>HttpObjectAnalyzer</c>, <c>TimerLeakAnalyzer</c>, <c>StringAnalyzer</c>, <c>CrashAnalyzer</c>,
/// ...) — matching this project's own "extract once a second consumer exists, not speculatively
/// ahead of one" precedent (<c>CapabilityBoundedGraphWalk</c>, Batch 4 of
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// </summary>
/// <remarks>
/// Candidates are collected as <see cref="HeapObjectRef"/> — the existing Tier-1 shape, not a
/// bespoke per-analyzer record — and handed to the inner SDK analyzer through the normal
/// <c>heap.objects</c> capability slot (<c>AnalysisContext.HeapObjects</c>) via a
/// <see cref="PrecomputedHeapObjectStream"/>, the same pattern <c>LockGraphAnalyzerLegacyAdapter</c>
/// already established for <c>IRuntimeThreadQuery</c> — not <c>AnalyzerOptions</c>, which stays
/// reserved for actual analyzer configuration per its own documented contract. (The pilot originally
/// used <c>AnalyzerOptions</c> for this; corrected here once the overload was recognized as a real
/// design smell, not a one-off pilot shortcut.)
///
/// <typeparamref name="TSelf"/> is the CRTP self-type, letting <see cref="IParallelHeapIndexScanParticipant.CreateWorkerInstance"/>
/// construct a fresh worker (<c>new TSelf()</c>) without every derived adapter overriding a factory
/// method — mirrors <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}"/>'s own <c>where TSdkAnalyzer :
/// ..., new()</c> constraint shape.
/// </remarks>
public abstract class TypedResourceCandidateStreamingLegacyAdapter<TSelf, TSdkAnalyzer> :
    LegacyAnalyzerAdapter<TSdkAnalyzer>, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource
    where TSelf : TypedResourceCandidateStreamingLegacyAdapter<TSelf, TSdkAnalyzer>, new()
    where TSdkAnalyzer : Sdk.Analysis.IAnalyzer, IProducesAnalyzerDomainResult
{
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private readonly List<HeapObjectRef> _candidates = new();

    protected TypedResourceCandidateStreamingLegacyAdapter(TSdkAnalyzer inner) : base(inner)
    {
    }

    /// <summary>The dump-resolved type names this adapter's inner analyzer wants candidate objects for.</summary>
    protected abstract IReadOnlySet<string> CandidateTypeNames { get; }

    public bool IsCandidateType(string typeName) => CandidateTypeNames.Contains(typeName);

    public void BeforeHeapIndexScan(Core.Abstractions.AnalysisContext context) =>
        _candidateMts = TypedResourceScanDriver.DiscoverCandidates(this, context.Heap, context.Cache);

    // Explicit interface implementations: HeapEntry/IHeapIndexScanParticipant are internal, so a
    // public member can't expose them in its signature (CS0050/CS0051) — same accessibility
    // constraint LockGraphAnalyzerLegacyAdapter's own OnThreadStack already hit for the same reason.
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        if (_candidateMts is null || !_candidateMts.TryGetValue(entry.MethodTable, out var candidate))
            return;
        _candidates.Add(new HeapObjectRef(
            entry.Address,
            SdkTypeRefFactory.FromName(candidate.TypeName, entry.MethodTable),
            entry.Size,
            candidate.TypeName));
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new TSelf();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            if (p is TypedResourceCandidateStreamingLegacyAdapter<TSelf, TSdkAnalyzer> other)
                _candidates.AddRange(other._candidates);
        }
    }

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(Core.Abstractions.AnalysisContext context) =>
        LegacyAnalysisContextTranslator.Translate(
            context, ResolveOptions(context.AnalysisOptions), heapObjectsOverride: new PrecomputedHeapObjectStream(_candidates));
}
