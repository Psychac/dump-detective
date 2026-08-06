using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase-2 analyzer covering §25.1 (committed vs reserved), §25.2 (segment lifecycle),
/// and §25.3 (address space pressure).
///
/// Operates entirely on <see cref="ClrHeap.Segments"/> — no heap object scan.
/// Each segment contributes committed bytes (<see cref="ClrSegment.CommittedMemory"/>) and
/// reserved bytes (<see cref="ClrSegment.ReservedMemory"/>), and is classified as ephemeral
/// or non-ephemeral based on its <see cref="ClrSegment.Kind"/> string.
/// Logical heap index (<see cref="ClrSubHeap.Index"/>) enables per-CPU reservation breakdown
/// for Server GC configurations.
/// </summary>
public sealed class SegmentReservationAnalyzer : IAnalyzer
{
    // Address space pressure thresholds (§25.3).
    public void Dispose() { }
    public string Name => "Segment Reservation Analysis";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SegmentReservationAnalysisOptions options = context.AnalysisOptions.SegmentReservationAnalysis;
        return ValueTask.FromResult(Analyze(context, context.Heap, options, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(AnalysisContext context, ClrHeap heap, SegmentReservationAnalysisOptions options, CancellationToken cancellationToken)
    {
        ulong totalCommitted = 0;
        ulong totalReserved = 0;

        int ephemeralCount = 0;
        double ephemeralFillSum = 0.0;
        int nonEphemeralSohCount = 0;

        var segmentTable = new List<SegmentReservationEntry>(64);
        var reservedByHeap = new Dictionary<int, ulong>(16);
        var committedByHeap = new Dictionary<int, ulong>(16);
        var reservedByKind = new Dictionary<HeapSegmentKind, ulong>();
        var committedByKind = new Dictionary<HeapSegmentKind, ulong>();
        var segmentCountByKind = new Dictionary<HeapSegmentKind, int>();

        int totalSegmentCount = 0;
        double maxEphemeralFillPct = 0.0;

        foreach (ClrSegment segment in heap.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong committed = SegmentKindMapper.GetCommittedBytes(segment);
            ulong reserved = SegmentKindMapper.GetReservedBytes(segment);
            bool isEphemeral = SegmentKindMapper.IsEphemeral(segment);
            int logicalHeap = segment.SubHeap?.Index ?? 0;
            HeapSegmentKind kind = SegmentKindMapper.Map(segment);

            totalCommitted += committed;
            totalReserved += reserved;
            totalSegmentCount++;

            // Per-kind segment count (fragmentation proxy).
            if (segmentCountByKind.TryGetValue(kind, out int kindCount))
                segmentCountByKind[kind] = kindCount + 1;
            else
                segmentCountByKind[kind] = 1;

            // Per-logical-heap reserved bytes (Server GC per-CPU breakdown).
            if (reservedByHeap.TryGetValue(logicalHeap, out ulong existing))
                reservedByHeap[logicalHeap] = existing + reserved;
            else
                reservedByHeap[logicalHeap] = reserved;

            // Per-logical-heap committed bytes (Server GC per-CPU breakdown).
            if (committedByHeap.TryGetValue(logicalHeap, out ulong existingCommitted))
                committedByHeap[logicalHeap] = existingCommitted + committed;
            else
                committedByHeap[logicalHeap] = committed;

            // Per-kind reserved and committed bytes (segment type breakdown).
            if (reservedByKind.TryGetValue(kind, out ulong existingReserved))
                reservedByKind[kind] = existingReserved + reserved;
            else
                reservedByKind[kind] = reserved;

            if (committedByKind.TryGetValue(kind, out ulong existingKindCommitted))
                committedByKind[kind] = existingKindCommitted + committed;
            else
                committedByKind[kind] = committed;

            // Ephemeral fill % = committed / segment length (object range).
            double fillPct = 0.0;
            if (isEphemeral && segment.Length > 0)
            {
                fillPct = committed / (double)segment.Length * 100.0;
                if (fillPct > 100.0) fillPct = 100.0;
                ephemeralCount++;
                ephemeralFillSum += fillPct;
                if (fillPct > maxEphemeralFillPct) maxEphemeralFillPct = fillPct;
            }
            else if (!isEphemeral && kind == HeapSegmentKind.SmallObjectHeap)
            {
                nonEphemeralSohCount++;
            }

            segmentTable.Add(new SegmentReservationEntry(
                Address: segment.Address,
                Kind: kind,
                CommittedBytes: committed,
                ReservedBytes: reserved,
                IsEphemeral: isEphemeral,
                LogicalHeap: logicalHeap,
                FillPct: fillPct));
        }

        ulong gapBytes = totalReserved > totalCommitted ? totalReserved - totalCommitted : 0;
        double ratio = totalCommitted > 0 ? totalReserved / (double)totalCommitted : 0.0;
        double avgFill = ephemeralCount > 0 ? ephemeralFillSum / ephemeralCount : 0.0;

        // Evaluate address space pressure (§25.3).
        int dumpPointerSize = context.Runtime.DataTarget.DataReader.PointerSize;
        bool pressureRisk = false;
        string pressureReason = string.Empty;
        if (dumpPointerSize == 4 && totalReserved > options.ThirtyTwoBitPressureThresholdBytes)
        {
            pressureRisk = true;
            pressureReason = $"32-bit process has {totalReserved / (1024 * 1024):N0} MB reserved (>{options.ThirtyTwoBitPressureThresholdBytes / (1024 * 1024):N0} MB threshold).";
        }
        else if (ratio > options.RatioHighPressureThreshold)
        {
            pressureRisk = true;
            pressureReason = $"Reserved-to-committed ratio is {ratio:F1}x (>{options.RatioHighPressureThreshold:F0}x threshold). GC is holding large uncommitted reservations.";
        }

        segmentTable.Sort((a, b) => b.ReservedBytes.CompareTo(a.ReservedBytes));

        return new SegmentReservationDomainResult(
            TotalCommittedBytes: totalCommitted,
            TotalReservedBytes: totalReserved,
            ReservationGapBytes: gapBytes,
            ReservedToCommittedRatio: ratio,
            EphemeralSegmentCount: ephemeralCount,
            AvgEphemeralFillPct: avgFill,
            MaxEphemeralFillPct: maxEphemeralFillPct,
            NonEphemeralSohSegmentCount: nonEphemeralSohCount,
            TotalSegmentCount: totalSegmentCount,
            SegmentTable: segmentTable,
            ReservedByLogicalHeap: reservedByHeap,
            CommittedByLogicalHeap: committedByHeap,
            ReservedByKind: reservedByKind,
            CommittedByKind: committedByKind,
            SegmentCountByKind: segmentCountByKind,
            AddressSpacePressureRisk: pressureRisk,
            PressureRiskReason: pressureReason,
            RatioHighPressureThreshold: options.RatioHighPressureThreshold,
            RatioMediumPressureThreshold: 4.0,
            DumpPointerSize: dumpPointerSize);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the segment is an ephemeral SOH segment (contains Gen0/Gen1).
    /// Detection is based on the <c>Kind</c> string — ClrMD uses "Ephemeral" for the
    /// classic non-regions ephemeral segment; individual generation segments in a regions-based
    /// heap are classified as Small/SOH and have non-empty <c>Generation0</c> ranges.
    /// </summary>
    // Classification helpers moved to SegmentKindMapper
}
