using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Shared <see cref="IHeapIndexScanParticipant"/>/<see cref="IParallelHeapIndexScanParticipant"/>
/// state for analyzers whose candidate discovery is "find every heap object whose type name is in
/// a fixed set, during the pipeline's one shared heap-index scan" — the shape
/// <c>SqlConnectionPoolAnalyzer</c> (the Tier-2 pilot) originally hand-rolled directly, later
/// extracted into <c>TypedResourceCandidateStreamingLegacyAdapter</c> (now retired: composed
/// directly onto each analyzer instead of a separate bridging adapter class).
/// </summary>
/// <remarks>
/// Candidates are collected as <see cref="HeapObjectRef"/> and handed to the analyzer's own
/// <see cref="ICustomizesAnalysisContext.BuildAnalysisContext"/> through the normal
/// <c>heap.objects</c> capability slot via a <see cref="PrecomputedHeapObjectStream"/>.
/// </remarks>
internal sealed class TypedResourceHeapIndexScanState
{
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private readonly List<HeapObjectRef> _candidates = new();
    private bool _scanSucceeded = true;

    public IReadOnlyList<HeapObjectRef> Candidates => _scanSucceeded ? _candidates : [];

    public void BeforeHeapIndexScan(ITypedResourceCandidateSource source, RuntimeAnalysisContext context) =>
        _candidateMts = TypedResourceScanDriver.DiscoverCandidates(source, context.Heap, context.Cache);

    public void OnHeapEntry(in HeapEntry entry)
    {
        if (_candidateMts is null || !_candidateMts.TryGetValue(entry.MethodTable, out var candidate))
            return;
        _candidates.Add(new HeapObjectRef(
            entry.Address,
            SdkTypeRefFactory.FromName(candidate.TypeName, entry.MethodTable),
            entry.Size,
            candidate.TypeName));
    }

    /// <summary>
    /// Either the shared scan finished normally, or <see cref="IHeapIndexScanParticipant.BeforeHeapIndexScan"/>/
    /// <see cref="IHeapIndexScanParticipant.OnHeapEntry"/> threw and the dispatcher isolated the
    /// failure — every consumer degrades to an empty candidate list rather than treating a failed
    /// scan's partial candidate list as "found some candidates".
    /// </summary>
    public void OnHeapIndexScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

    public void MergeFrom(IEnumerable<TypedResourceHeapIndexScanState> others)
    {
        foreach (TypedResourceHeapIndexScanState other in others)
        {
            _candidates.AddRange(other._candidates);
            if (!other._scanSucceeded)
                _scanSucceeded = false;
        }
    }
}
