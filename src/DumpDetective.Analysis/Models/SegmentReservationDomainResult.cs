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
    int NonEphemeralSohSegmentCount,
    IReadOnlyList<SegmentReservationEntry> SegmentTable,
    IReadOnlyDictionary<int, ulong> ReservedByLogicalHeap,
    bool AddressSpacePressureRisk,
    string PressureRiskReason,
    int DumpPointerSize) : AnalyzerDomainResult;
