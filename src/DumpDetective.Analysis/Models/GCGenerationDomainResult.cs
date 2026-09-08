using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// GC Generation

/// <summary>Generation distribution for a single type, built from Phase-1 TypeAggregates.</summary>
public sealed record TypeGenerationProfile(
    string TypeName,
    long Gen0Count,
    long Gen1Count,
    long Gen2Count,
    long LohCount,
    ulong TotalBytes = 0,
    /// <summary>
    /// Exact sum of object sizes for this type's non-LOH Gen2 instances, sourced from
    /// <see cref="DumpDetective.Analysis.Indexing.TypeAggregateIndexEntry.Gen2TotalSize"/>.
    /// Zero when the heap index predates that field (schema v3 and older).
    /// </summary>
    ulong Gen2Bytes = 0,
    bool IsFinalizable = false);

public sealed record GCGenerationDomainResult(
    ulong Gen0Bytes,
    long Gen0Objects,
    ulong Gen1Bytes,
    long Gen1Objects,
    ulong Gen2Bytes,
    long Gen2Objects,
    ulong LohBytes,
    double LohPercent,
    int TotalObjects,
    long LohObjects,
    IReadOnlyList<TypeSnapshot> TopLohTypes,
    ulong PohBytes = 0,
    long PohObjects = 0,
    double Gen2Pct = 0.0,
    IReadOnlyList<TypeGenerationProfile>? PerTypeGenerationProfiles = null,
    bool GenBytesAreApproximate = true,
    bool FallbackMode = false,
    double LohThresholdPercent = 20.0,
    double Gen0PressureThresholdPercent = 40.0,
    double PohThresholdPercent = 5.0,
    /// <summary>
    /// Instance count of Gen2 objects whose type implements a finalizer, summed from
    /// <see cref="TypeGenerationProfile.IsFinalizable"/> types in <see cref="PerTypeGenerationProfiles"/>.
    /// Cross-references <c>FinalizableObjectAnalyzer</c>'s dedicated finalizer-queue scan (its
    /// own <c>Gen2Count</c>) for the retention/queue detail this analyzer does not compute.
    /// Zero when <see cref="PerTypeGenerationProfiles"/> is unavailable (fallback mode).
    /// </summary>
    long FinalizableGen2Count = 0,
    ulong FinalizableGen2Bytes = 0) : AnalyzerDomainResult
{
    /// <summary>
    /// Small Object Heap total: Gen0 + Gen1 + Gen2 bytes. Excludes LOH and POH, which are
    /// separate heaps. Makes the LOH-vs-SOH ratio explicit without every caller re-summing
    /// three fields.
    /// </summary>
    public ulong SohTotal => Gen0Bytes + Gen1Bytes + Gen2Bytes;
}
