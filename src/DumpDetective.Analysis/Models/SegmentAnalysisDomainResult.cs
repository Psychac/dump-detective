using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Segments

internal enum HeapSegmentKind { SmallObjectHeap, LargeObjectHeap, PinnedObjectHeap, Frozen, Unknown }

internal sealed record HeapSegmentSnapshot(
    ulong Address,
    ulong Start,
    ulong End,
    ulong Length,
    ulong CommittedBytes,
    HeapSegmentKind Kind,
    int Generation,
    int ObjectCount);

internal sealed record SegmentKindSummary(
    HeapSegmentKind Kind,
    int SegmentCount,
    int ObjectCount,
    ulong TotalBytes);

internal sealed record SegmentAnalysisDomainResult(
    int TotalSegments,
    ulong TotalCommittedBytes,
    int SohSegmentCount,
    ulong SohBytes,
    int LohSegmentCount,
    ulong LohBytes,
    int PohSegmentCount,
    ulong PohBytes,
    int FrozenSegmentCount,
    ulong FrozenBytes,
    double LohPercent,
    double PohPercent,
    IReadOnlyList<SegmentKindSummary> KindSummaries,
    IReadOnlyList<HeapSegmentSnapshot>? TopSegmentsBySize = null) : AnalyzerDomainResult;
