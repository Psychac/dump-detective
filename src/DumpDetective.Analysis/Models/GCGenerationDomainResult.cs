using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// GC Generation

/// <summary>Generation distribution for a single type, built from Phase-1 TypeAggregates.</summary>
public sealed record TypeGenerationProfile(
    string TypeName,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    int LohCount,
    ulong TotalBytes = 0,
    bool IsFinalizable = false);

public sealed record GCGenerationDomainResult(
    ulong Gen0Bytes,
    int Gen0Objects,
    ulong Gen1Bytes,
    int Gen1Objects,
    ulong Gen2Bytes,
    int Gen2Objects,
    ulong LohBytes,
    double LohPercent,
    int TotalObjects,
    int LohObjects,
    IReadOnlyList<TypeSnapshot> TopLohTypes,
    double Gen2Pct = 0.0,
    IReadOnlyList<TypeGenerationProfile>? PerTypeGenerationProfiles = null) : AnalyzerDomainResult;
