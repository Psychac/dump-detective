using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Memory

public sealed record MemoryDomainResult(
    ulong TotalBytes,
    ulong LohBytes,
    double LohPercent,
    int TotalObjects,
    int LohObjects,
    ulong LohThresholdBytes,
    int UniqueTypes,
    IReadOnlyList<TypeSnapshot> TopTypesBySize,
    IReadOnlyList<TypeSnapshot> TopTypesByCount,
    IReadOnlyList<SizeBucketEntry>? SizeBucketHistogram = null) : AnalyzerDomainResult;
