using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// LOH + POH Fragmentation

/// <summary>Per-<see cref="HeapSegmentKind"/> subtotal (Large vs. Pinned) within the combined LOH+POH view.</summary>
public sealed record LohKindBreakdown(
    HeapSegmentKind Kind,
    int SegmentCount,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    double FragmentationPercent,
    ulong LargestFreeBlock);

internal sealed record LohFragmentationDomainResult(
    int SegmentCount,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    int FreeBlockCount,
    double FragmentationPercent,
    ulong LargestFreeBlock,
    IReadOnlyList<LohSegmentSnapshot>? TopFragmentedSegments = null,
    /// <summary>Distribution of free-gap sizes across all LOH segments.</summary>
    IReadOnlyList<FreeGapBucket>? FreeGapHistogram = null,
    /// <summary>Top large objects by size (up to 20), from Phase 1 LargeObjectIndex.bin.</summary>
    IReadOnlyList<LargeObjectSnapshot>? TopLargeObjects = null,
    /// <summary>Top types by total LOH bytes consumed (type-aggregated view of LOH consumption).</summary>
    IReadOnlyList<LohTypeProfile>? TopLargeObjectTypes = null,
    /// <summary>Large-vs-Pinned segment kind subtotals, so LOH and POH fragmentation can be told apart.</summary>
    IReadOnlyList<LohKindBreakdown>? KindBreakdown = null) : AnalyzerDomainResult;
