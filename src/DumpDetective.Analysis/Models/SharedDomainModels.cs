using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Shared small types extracted from AnalyzerDomainModels.cs to reduce file size and merge conflicts

public sealed record LohSegmentSnapshot(
    ulong Address,
    ulong TotalBytes,
    double FragmentationPercent,
    ulong FreeBytes,
    ulong LargestFreeBlock);

/// <summary>One bucket in the free-gap size histogram for LOH fragmentation analysis.</summary>
/// <param name="GapSizeRange">Human-readable size range, e.g. "1 KB – 64 KB".</param>
/// <param name="GapCount">Number of free gaps whose size falls within this range.</param>
public sealed record FreeGapBucket(string GapSizeRange, int GapCount);

/// <summary>Snapshot of a single large (LOH) object captured during Phase 1 index build.</summary>
public sealed record LargeObjectSnapshot(ulong Address, string TypeName, ulong Size);

/// <summary>One bucket in the object-size histogram built from <see cref="HeapIndexBuildResult.GlobalSizeBuckets"/>.</summary>
/// <param name="RangeLabel">Human-readable range label, e.g. "64–255 B".</param>
/// <param name="ObjectCount">Total number of objects whose shallow size falls in this bucket.</param>
/// <param name="TotalBytes">Sum of shallow sizes of all objects in this bucket.</param>
public sealed record SizeBucketEntry(string RangeLabel, long ObjectCount, ulong TotalBytes);
