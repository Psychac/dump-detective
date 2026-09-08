using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Lazily builds a per-segment classification pass over <see cref="ClrHeap.Segments"/> once per
/// heap and memoizes it, so <c>HeapTopologyAnalyzer</c> and <c>SegmentReservationAnalyzer</c> (and
/// any future segment-level analyzer) each pay the (cheap — segment counts are small, even for
/// regions-based GC) classification cost exactly once regardless of how many of them run. See
/// docs/refactor/heap-segment-shared-pass-plan.md. Not thread-safe by design: <see cref="IAnalyzer.IsThreadSafe"/>
/// defaults to <c>false</c> for the analyzers that consume this, matching every other lazy sub-cache
/// in <see cref="HeapAnalysisCache"/> (e.g. <c>StatisticsCache</c>).
/// </summary>
internal sealed class SegmentSummaryCache
{
    private IReadOnlyList<SegmentSummary>? _summaries;

    public IReadOnlyList<SegmentSummary> GetOrBuild(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_summaries is not null)
            return _summaries;

        _summaries = Build(heap);
        return _summaries;
    }

    /// <summary>
    /// One-off classification pass, exposed so callers without access to a memoized
    /// <see cref="HeapAnalysisCache"/> instance (e.g. a bare <see cref="IHeapAnalysisCache"/> test
    /// double) can still get identical per-segment classification without duplicating this loop.
    /// </summary>
    public static IReadOnlyList<SegmentSummary> Build(ClrHeap heap)
    {
        var summaries = new List<SegmentSummary>(64);
        foreach (ClrSegment segment in heap.Segments)
        {
            HeapSegmentKind kind = SegmentKindMapper.Map(segment);

            ulong gen0Bytes = 0, gen1Bytes = 0, gen2Bytes = 0;
            if (kind == HeapSegmentKind.SmallObjectHeap)
            {
                gen0Bytes = SegmentKindMapper.GetRangeLength(segment.Generation0);
                gen1Bytes = SegmentKindMapper.GetRangeLength(segment.Generation1);
                gen2Bytes = SegmentKindMapper.GetRangeLength(segment.Generation2);
            }

            summaries.Add(new SegmentSummary(
                Segment: segment,
                Kind: kind,
                RegionKind: SegmentKindMapper.MapRegionKind(segment),
                CommittedBytes: SegmentKindMapper.GetCommittedBytes(segment),
                ReservedBytes: SegmentKindMapper.GetReservedBytes(segment),
                LogicalHeapIndex: segment.SubHeap?.Index ?? -1,
                IsEphemeral: SegmentKindMapper.IsEphemeral(segment),
                Gen0Bytes: gen0Bytes,
                Gen1Bytes: gen1Bytes,
                Gen2Bytes: gen2Bytes));
        }

        return summaries;
    }
}
