using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;

using Microsoft.Diagnostics.Runtime;

using SdkHeapObjectRef = DumpDetective.Sdk.Analysis.HeapObjectRef;
using SdkHeapSegmentRef = DumpDetective.Sdk.Analysis.HeapSegmentRef;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.segments</c> capability, built for the
/// <c>SegmentReservationAnalyzer</c>/<c>HeapTopologyAnalyzer</c> retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Lives here rather than a
/// dedicated <c>Sources.ClrDump</c> project for the same reason as
/// <see cref="HeapTypeStatisticsQuery"/> — that project doesn't exist yet.
/// </summary>
/// <remarks>
/// Delegates to <c>HeapAnalysisCache.GetOrBuildSegmentSummaries</c> — the one shared per-run pass
/// the two analyzers this batch retypes already both used directly — falling back to a fresh
/// <c>SegmentSummaryCache.Build</c> when <paramref name="cache"/> isn't the concrete
/// <c>HeapAnalysisCache</c> (e.g. a bare <c>IHeapAnalysisCache</c> test double), matching the
/// fallback both analyzers' own pre-retyping code already had.
/// </remarks>
internal sealed class HeapSegmentQuery(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache cache) : Sdk.Analysis.IHeapSegmentQuery
{
    public int DumpPointerSize => runtime.DataTarget.DataReader.PointerSize;

    public bool IsServerGc => heap.IsServer;

    public int LogicalHeapCount => heap.SubHeaps.Length;

    public IEnumerable<SdkHeapSegmentRef> EnumerateSegments()
    {
        IReadOnlyList<SegmentSummary> summaries = cache is HeapAnalysisCache heapCache
            ? heapCache.GetOrBuildSegmentSummaries(heap)
            : SegmentSummaryCache.Build(heap);

        foreach (SegmentSummary summary in summaries)
            yield return ToRef(summary);
    }

    public bool TryGetSegment(ulong address, out SdkHeapSegmentRef segment)
    {
        foreach (SdkHeapSegmentRef candidate in EnumerateSegments())
        {
            if (address >= candidate.Start && address < candidate.End)
            {
                segment = candidate;
                return true;
            }
        }

        segment = default;
        return false;
    }

    public IEnumerable<SdkHeapObjectRef> EnumerateObjects(SdkHeapSegmentRef segment)
    {
        ClrSegment? clrSegment = heap.GetSegmentByAddress(segment.Start);
        if (clrSegment is null)
            yield break;

        foreach (ClrObject obj in clrSegment.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsFree || obj.Type is null)
                continue;

            string name = obj.Type.Name ?? $"MT:0x{obj.Type.MethodTable:x}";
            yield return new SdkHeapObjectRef(obj.Address, SdkTypeRefFactory.FromName(name, obj.Type.MethodTable), obj.Size, name);
        }
    }

    private static SdkHeapSegmentRef ToRef(SegmentSummary summary) => new()
    {
        Start = summary.Segment.Start,
        End = summary.Segment.End,
        Address = summary.Segment.Address,
        Generation = SdkSegmentKindMapper.ToLegacyGenerationNumber(summary.RegionKind),
        Kind = SdkSegmentKindMapper.ToSdk(summary.Kind),
        RegionKind = SdkSegmentKindMapper.ToSdk(summary.RegionKind),
        CommittedBytes = summary.CommittedBytes,
        ReservedBytes = summary.ReservedBytes,
        LogicalHeapIndex = summary.LogicalHeapIndex,
        IsEphemeral = summary.IsEphemeral,
        Gen0Bytes = summary.Gen0Bytes,
        Gen1Bytes = summary.Gen1Bytes,
        Gen2Bytes = summary.Gen2Bytes,
    };
}
