using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Segment Reservation

internal sealed record SegmentReservationEntry(
    ulong Address,
    HeapSegmentKind Kind,
    ulong CommittedBytes,
    ulong ReservedBytes,
    bool IsEphemeral,
    int LogicalHeap,
    double FillPct);

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
    int DumpPointerSize) : AnalyzerDomainResult;
