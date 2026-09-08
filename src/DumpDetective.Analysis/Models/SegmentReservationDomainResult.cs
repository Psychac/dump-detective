using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Models;

// Segment Reservation

internal sealed record SegmentReservationEntry(
    ulong Address,
    ulong EndAddress,
    HeapSegmentKind Kind,
    ulong CommittedBytes,
    ulong ReservedBytes,
    bool IsEphemeral,
    int LogicalHeap,
    double FillPct);

/// <summary>
/// GC region generation, per <see cref="ClrSegment.Kind" /> — distinct from <see cref="HeapSegmentKind"/>,
/// which collapses Gen0/Gen1/Gen2/Ephemeral into a single SmallObjectHeap bucket. Region-based stats need
/// the per-generation split since each generation is its own population of regions in regions-based GC.
/// </summary>
internal enum RegionGenerationKind { Generation0, Generation1, Generation2, Large, Pinned, Frozen, Ephemeral }

/// <summary>Aggregate stats for one <see cref="RegionGenerationKind"/> bucket, populated only for regions-based heaps.</summary>
internal sealed record RegionGenerationStats(
    RegionGenerationKind Kind,
    int Count,
    ulong TotalReservedBytes,
    ulong TotalCommittedBytes,
    ulong MinReservedBytes,
    ulong MaxReservedBytes,
    int NearEmptyCount,
    ulong NearEmptyCommittedBytes);

internal sealed record SegmentReservationDomainResult(
    ulong TotalCommittedBytes,
    ulong TotalReservedBytes,
    ulong ReservationGapBytes,
    double ReservedToCommittedRatio,
    int EphemeralSegmentCount,
    double AvgEphemeralFillPct,
    double MaxEphemeralFillPct,
    int NonEphemeralSohSegmentCount,
    int TotalSegmentCount,
    IReadOnlyList<SegmentReservationEntry> SegmentTable,
    IReadOnlyDictionary<int, ulong> ReservedByLogicalHeap,
    IReadOnlyDictionary<int, ulong> CommittedByLogicalHeap,
    IReadOnlyDictionary<HeapSegmentKind, ulong> ReservedByKind,
    IReadOnlyDictionary<HeapSegmentKind, ulong> CommittedByKind,
    IReadOnlyDictionary<HeapSegmentKind, int> SegmentCountByKind,
    bool AddressSpacePressureRisk,
    string PressureRiskReason,
    double RatioHighPressureThreshold,
    double RatioMediumPressureThreshold,
    int DumpPointerSize,
    bool IsServerGc,
    int LogicalHeapCount,
    bool IsRegionsBased,
    IReadOnlyList<RegionGenerationStats> RegionStats,
    int NearEmptyRegionCount,
    ulong NearEmptyRegionCommittedBytes,
    double NearEmptyRegionFillPctThreshold) : AnalyzerDomainResult;
