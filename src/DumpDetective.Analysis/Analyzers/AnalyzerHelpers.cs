using DumpDetective.Analysis.Indexing;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Shared computation helpers used by multiple analyzers to avoid code duplication.
/// </summary>
internal static class AnalyzerHelpers
{
    /// <summary>
    /// Computes exact per-generation byte totals directly from heap segments, excluding LOH/POH.
    /// Uses <see cref="ClrSegment.Generation0"/>/<see cref="ClrSegment.Generation1"/>/
    /// <see cref="ClrSegment.Generation2"/> sub-range lengths on every SOH-kind segment rather than
    /// attributing a whole segment's committed bytes to one generation: a classic (non-regions)
    /// <see cref="GCSegmentKind.Ephemeral"/> segment holds the Gen0/Gen1 budget plus the tail of
    /// Gen2 all in one segment, so lumping its full committed size into Gen2 (as this method used
    /// to do) overstates Gen2 and zeroes out Gen0/Gen1 whenever a dump has no separate
    /// regions-based Generation0/Generation1 segments — which is every classic-GC dump. This is
    /// the same per-segment split <see cref="Cache.SegmentSummaryCache"/> already uses.
    /// This is the authoritative method and should be preferred over approximations.
    /// </summary>
    public static void ComputeExactGenBytes(
        ClrHeap heap,
        out ulong gen0Bytes,
        out ulong gen1Bytes,
        out ulong gen2Bytes)
    {
        gen0Bytes = 0;
        gen1Bytes = 0;
        gen2Bytes = 0;

        foreach (ClrSegment segment in heap.Segments)
        {
            if (SegmentKindMapper.Map(segment) != HeapSegmentKind.SmallObjectHeap)
                continue;

            gen0Bytes += SegmentKindMapper.GetRangeLength(segment.Generation0);
            gen1Bytes += SegmentKindMapper.GetRangeLength(segment.Generation1);
            gen2Bytes += SegmentKindMapper.GetRangeLength(segment.Generation2);
        }
    }

    /// <summary>
    /// Computes Pinned Object Heap (POH) byte and object totals (.NET 5+).
    /// POH is a separate heap for pinned objects, distinct from LOH.
    /// </summary>
    public static void ComputePohMetrics(
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates,
        out ulong pohBytes,
        out long pohObjects)
    {
        pohBytes = 0;
        pohObjects = 0;

        // POH objects are not typically in TypeAggregates since they're pinned separately.
        // For now, return zeros as POH detection requires runtime-level segment inspection.
        // This method provides the contract for POH metric extraction when available.
    }

    /// <summary>
    /// Approximates per-generation byte totals from <paramref name="aggregates"/> using the
    /// average non-LOH object size per MethodTable multiplied by its per-generation count.
    /// This is the same heuristic applied by both <see cref="GCGenerationAnalyzer"/> and
    /// <see cref="AllocationPatternAnalyzer"/>.
    /// </summary>
    [Obsolete("Use ComputeExactGenBytes instead for accurate segment-based data.")]
    public static void ComputeApproxGenBytes(
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates,
        out ulong gen0Bytes,
        out ulong gen1Bytes,
        out ulong gen2Bytes)
    {
        gen0Bytes = 0;
        gen1Bytes = 0;
        gen2Bytes = 0;

        foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
        {
            TypeAggregateIndexEntry e = kv.Value;
            long nonLohCount = e.Count - e.LohCount;
            if (nonLohCount <= 0) continue;
            ulong nonLohSize = e.TotalSize >= e.LohSize ? e.TotalSize - e.LohSize : 0;
            if (nonLohSize == 0) continue;
            ulong avgSize = nonLohSize / (ulong)nonLohCount;
            gen0Bytes += (ulong)e.Gen0Count * avgSize;
            gen1Bytes += (ulong)e.Gen1Count * avgSize;
            gen2Bytes += (ulong)e.Gen2Count * avgSize;
        }
    }
}
