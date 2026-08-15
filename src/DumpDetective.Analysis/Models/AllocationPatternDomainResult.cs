using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Allocation Pattern

public enum AllocationProfile { Transient, Steady, Retained, Mixed }
public enum TypeProfile { Transient, Retained, Mixed }
public enum GCPressureLevel { Low, Moderate, High, Critical }

public sealed record TypeAllocationProfile(
    string TypeName,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    double LongLivedRatio,
    TypeProfile Profile,
    ulong TotalSize,
    double Gen1SurvivalRate,
    bool IsFinalizable = false);

internal sealed record AllocationPatternDomainResult(
    // Object-count percentages (objects in generation / total objects)
    double Gen0CountPct,
    double Gen1CountPct,
    double Gen2CountPct,
    double LohCountPct,
    // Byte-size percentages (bytes in generation / total managed bytes)
    double Gen0SizePct,
    double Gen1SizePct,
    double Gen2SizePct,
    double LohSizePct,
    // Absolute byte totals (enables severity assessment without percentages alone)
    ulong TotalManagedBytes,
    ulong Gen0Bytes,
    ulong Gen1Bytes,
    ulong Gen2Bytes,
    ulong LohBytes,
    AllocationProfile Profile,
    GCPressureLevel GCPressure,
    double PromotionPressureScore,
    IReadOnlyList<TypeAllocationProfile> TopTransientTypes,
    IReadOnlyList<TypeAllocationProfile> TopShortishTypes,
    IReadOnlyList<TypeAllocationProfile> TopLongLivedTypes,
    IReadOnlyList<TypeAllocationProfile> TopHighGen1SurvivorTypes,
    // LOH size-band distribution (85 KB–1 MB, 1 MB–10 MB, ≥10 MB) sourced from the Phase 1
    // global size histogram — object counts are exact, byte totals are approximated from
    // each type's average object size (same approximation MemoryAnalyzer already uses).
    IReadOnlyList<SizeBucketEntry>? LohSizeBands = null,
    // Aggregated across all types (not just the top-K profiled sample), since finalizable
    // retention is a heap-wide GC-pressure signal, not a per-type classification detail.
    int FinalizableTypeCount = 0,
    ulong FinalizableBytes = 0) : AnalyzerDomainResult;
