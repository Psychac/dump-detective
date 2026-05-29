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
    IReadOnlyList<TypeSnapshot> TopTypes,
    IReadOnlyList<SizeBucketEntry>? SizeBucketHistogram = null,
    double Top1BytesPercent = 0,
    double Top5BytesPercent = 0,
    double Top10BytesPercent = 0,
    double SmallObjectCountPercent = 0,
    double SmallObjectBytesPercent = 0,
    double ObjectsPerMb = 0,
    double MemoryPressureScore = 0) : AnalyzerDomainResult;
