using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Segments

public enum HeapSegmentKind { SmallObjectHeap, LargeObjectHeap, PinnedObjectHeap, Frozen, Unknown }

internal sealed record HeapSegmentSnapshot(
    ulong Address,
    ulong Start,
    ulong End,
    ulong Length,
    ulong CommittedBytes,
    ulong UsedBytes,
    ulong ReservedBytes,
    HeapSegmentKind Kind,
    int Generation,
    int ObjectCount,
    ulong Gen0Bytes = 0,
    ulong Gen1Bytes = 0,
    ulong Gen2Bytes = 0);

internal sealed record SegmentKindSummary(
    HeapSegmentKind Kind,
    int SegmentCount,
    int ObjectCount,
    ulong TotalBytes,
    ulong ReservedBytes);

internal sealed record PerLogicalHeapSummary(
    int LogicalHeapIndex,
    ulong Bytes,
    int ObjectCount,
    int SegmentCount);

internal sealed record HeapTopologyDomainResult(
    int TotalSegments,
    ulong TotalCommittedBytes,
    ulong TotalUsedBytes,
    ulong TotalReservedBytes,
    ulong ReservationGapBytes,
    int SohSegmentCount,
    ulong SohBytes,
    int LohSegmentCount,
    ulong LohBytes,
    int PohSegmentCount,
    ulong PohBytes,
    int FrozenSegmentCount,
    ulong FrozenBytes,
    double FrozenPercent,
    double LohPercent,
    double PohPercent,
    ulong Gen0Bytes,
    ulong Gen1Bytes,
    ulong Gen2Bytes,
    ulong SohFragmentedBytes,
    ulong LohFragmentedBytes,
    ulong PohFragmentedBytes,
    ulong FrozenFragmentedBytes,
    IReadOnlyList<SegmentKindSummary> KindSummaries,
    IReadOnlyList<PerLogicalHeapSummary> PerLogicalHeapSummaries,
    IReadOnlyList<TypeSnapshot>? TopPohTypes = null,
    IReadOnlyList<TypeSnapshot>? TopFrozenTypes = null,
    IReadOnlyList<HeapSegmentSnapshot>? TopSegmentsBySize = null) : AnalyzerDomainResult;
