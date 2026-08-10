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
    double Gen0PressureThresholdPercent = 40.0) : AnalyzerDomainResult;
