using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

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
    private const ulong ThirtyTwoBitPressureThreshold = 1_500_000_000UL; // 1.5 GB
    private const double RatioHighPressureThreshold    = 10.0;

    public string Name     => "Segment Reservation Analysis";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(ClrHeap heap, CancellationToken cancellationToken)
    {
        ulong totalCommitted = 0;
        ulong totalReserved  = 0;

        int ephemeralCount         = 0;
        double ephemeralFillSum    = 0.0;
        int nonEphemeralSohCount   = 0;

        var segmentTable = new List<SegmentReservationEntry>(64);
        var reservedByHeap = new Dictionary<int, ulong>(16);

        foreach (ClrSegment segment in heap.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong committed = GetLength(segment.CommittedMemory);
            ulong reserved  = GetLength(segment.ReservedMemory);
            bool isEphemeral = SegmentKindMapper.IsEphemeral(segment);
            int logicalHeap  = segment.SubHeap?.Index ?? 0;
            HeapSegmentKind kind = SegmentKindMapper.Map(segment);

            totalCommitted += committed;
            totalReserved  += reserved;

            // Per-logical-heap reserved bytes (Server GC per-CPU breakdown).
            if (reservedByHeap.TryGetValue(logicalHeap, out ulong existing))
                reservedByHeap[logicalHeap] = existing + reserved;
            else
                reservedByHeap[logicalHeap] = reserved;

            // Ephemeral fill % = committed / segment length (object range).
            double fillPct = 0.0;
            if (isEphemeral && segment.Length > 0)
            {
                fillPct = committed / (double)segment.Length * 100.0;
                if (fillPct > 100.0) fillPct = 100.0;
                ephemeralCount++;
                ephemeralFillSum += fillPct;
            }
            else if (!isEphemeral && kind == HeapSegmentKind.SmallObjectHeap)
            {
                nonEphemeralSohCount++;
            }

            segmentTable.Add(new SegmentReservationEntry(
                Address:       segment.Address,
                Kind:          kind,
                CommittedBytes: committed,
                ReservedBytes:  reserved,
                IsEphemeral:    isEphemeral,
                LogicalHeap:    logicalHeap,
                FillPct:        fillPct));
        }

        ulong gapBytes = totalReserved > totalCommitted ? totalReserved - totalCommitted : 0;
        double ratio = totalCommitted > 0 ? totalReserved / (double)totalCommitted : 0.0;
        double avgFill = ephemeralCount > 0 ? ephemeralFillSum / ephemeralCount : 0.0;

        // Evaluate address space pressure (§25.3).
        bool pressureRisk = false;
        string pressureReason = string.Empty;
        if (IntPtr.Size == 4 && totalReserved > ThirtyTwoBitPressureThreshold)
        {
            pressureRisk   = true;
            pressureReason = $"32-bit process has {totalReserved / (1024 * 1024):N0} MB reserved (>{ThirtyTwoBitPressureThreshold / (1024 * 1024):N0} MB threshold).";
        }
        else if (ratio > RatioHighPressureThreshold)
        {
            pressureRisk   = true;
            pressureReason = $"Reserved-to-committed ratio is {ratio:F1}x (>{RatioHighPressureThreshold:F0}x threshold). GC is holding large uncommitted reservations.";
        }

        return new SegmentReservationDomainResult(
            TotalCommittedBytes:       totalCommitted,
            TotalReservedBytes:        totalReserved,
            ReservationGapBytes:       gapBytes,
            ReservedToCommittedRatio:  ratio,
            EphemeralSegmentCount:     ephemeralCount,
            AvgEphemeralFillPct:       avgFill,
            NonEphemeralSohSegmentCount: nonEphemeralSohCount,
            SegmentTable:              segmentTable,
            ReservedByLogicalHeap:     reservedByHeap,
            AddressSpacePressureRisk:  pressureRisk,
            PressureRiskReason:        pressureReason);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Returns the byte length of a <see cref="MemoryRange"/> (End − Start).</summary>
    private static ulong GetLength(MemoryRange range) =>
        range.End >= range.Start ? range.End - range.Start : 0;

    /// <summary>
    /// Returns true when the segment is an ephemeral SOH segment (contains Gen0/Gen1).
    /// Detection is based on the <c>Kind</c> string — ClrMD uses "Ephemeral" for the
    /// classic non-regions ephemeral segment; individual generation segments in a regions-based
    /// heap are classified as Small/SOH and have non-empty <c>Generation0</c> ranges.
    /// </summary>
    // Classification helpers moved to SegmentKindMapper
}
