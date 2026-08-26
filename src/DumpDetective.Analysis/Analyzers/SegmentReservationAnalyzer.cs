using DumpDetective.Analysis.Cache;
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
        return ValueTask.FromResult(Analyze(context, context.Heap, context.Progress, options, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(AnalysisContext context, ClrHeap heap, IProgress<AnalyzerProgressReport>? progress, SegmentReservationAnalysisOptions options, CancellationToken cancellationToken)
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
        var regionBuckets = new Dictionary<RegionGenerationKind, RegionBucketAccumulator>(8);

        int totalSegmentCount = 0;
        double maxEphemeralFillPct = 0.0;
        bool isRegionsBased = false;
        const int ProgressReportInterval = 128;

        // Shared with HeapTopologyAnalyzer — see docs/refactor/heap-segment-shared-pass-plan.md.
        // Falls back to a local classification pass when the cache isn't the concrete
        // HeapAnalysisCache (e.g. a bare IHeapAnalysisCache test double).
        IReadOnlyList<SegmentSummary> summaries = context.Cache is HeapAnalysisCache heapCache
            ? heapCache.GetOrBuildSegmentSummaries(heap)
            : SegmentSummaryCache.Build(heap);

        for (int summaryIndex = 0; summaryIndex < summaries.Count; summaryIndex++)
        {
            SegmentSummary summary = summaries[summaryIndex];
            ClrSegment segment = summary.Segment;

            // Mid-loop cancellation check and progress reporting (every 128 segments).
            if ((totalSegmentCount % ProgressReportInterval) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(totalSegmentCount, "analyzing segment reservation", $"{totalSegmentCount} segments processed"));
            }

            ulong committed = summary.CommittedBytes;
            ulong reserved = summary.ReservedBytes;
            bool isEphemeral = summary.IsEphemeral;
            int logicalHeap = summary.LogicalHeapIndex >= 0 ? summary.LogicalHeapIndex : 0;
            HeapSegmentKind kind = summary.Kind;
            RegionGenerationKind regionKind = summary.RegionKind;
            if (regionKind is RegionGenerationKind.Generation0 or RegionGenerationKind.Generation1)
                isRegionsBased = true;

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

            // Fill % = committed / segment length (object range). Computed for every segment so
            // it can feed both the ephemeral-only aggregate below and the per-region bucket stats
            // (regions-based GC benefits from a fill % on non-ephemeral kinds too, since Gen2/LOH
            // regions are small individually and a near-empty one is a real decommit candidate).
            double fillPct = 0.0;
            if (segment.Length > 0)
            {
                fillPct = committed / (double)segment.Length * 100.0;
                if (fillPct > 100.0) fillPct = 100.0;
            }

            if (isEphemeral)
            {
                ephemeralCount++;
                ephemeralFillSum += fillPct;
                if (fillPct > maxEphemeralFillPct) maxEphemeralFillPct = fillPct;
            }
            else if (kind == HeapSegmentKind.SmallObjectHeap)
            {
                nonEphemeralSohCount++;
            }

            if (!regionBuckets.TryGetValue(regionKind, out RegionBucketAccumulator? bucket))
            {
                bucket = new RegionBucketAccumulator();
                regionBuckets[regionKind] = bucket;
            }
            bucket.Add(reserved, committed, fillPct <= options.NearEmptyRegionFillPctThreshold);

            segmentTable.Add(new SegmentReservationEntry(
                Address: segment.Address,
                EndAddress: segment.End,
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

        var regionStats = new List<RegionGenerationStats>(isRegionsBased ? regionBuckets.Count : 0);
        int nearEmptyRegionCount = 0;
        ulong nearEmptyRegionCommittedBytes = 0;
        if (isRegionsBased)
        {
            foreach (KeyValuePair<RegionGenerationKind, RegionBucketAccumulator> kvp in regionBuckets)
            {
                RegionBucketAccumulator b = kvp.Value;
                regionStats.Add(new RegionGenerationStats(
                    Kind: kvp.Key,
                    Count: b.Count,
                    TotalReservedBytes: b.TotalReservedBytes,
                    TotalCommittedBytes: b.TotalCommittedBytes,
                    MinReservedBytes: b.Count > 0 ? b.MinReservedBytes : 0,
                    MaxReservedBytes: b.MaxReservedBytes,
                    NearEmptyCount: b.NearEmptyCount,
                    NearEmptyCommittedBytes: b.NearEmptyCommittedBytes));
                nearEmptyRegionCount += b.NearEmptyCount;
                nearEmptyRegionCommittedBytes += b.NearEmptyCommittedBytes;
            }
            regionStats.Sort((a, b) => a.Kind.CompareTo(b.Kind));
        }

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
            DumpPointerSize: dumpPointerSize,
            IsServerGc: heap.IsServer,
            LogicalHeapCount: reservedByHeap.Count,
            IsRegionsBased: isRegionsBased,
            RegionStats: regionStats,
            NearEmptyRegionCount: nearEmptyRegionCount,
            NearEmptyRegionCommittedBytes: nearEmptyRegionCommittedBytes,
            NearEmptyRegionFillPctThreshold: options.NearEmptyRegionFillPctThreshold);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the segment is an ephemeral SOH segment (contains Gen0/Gen1).
    /// Detection is based on the <c>Kind</c> string — ClrMD uses "Ephemeral" for the
    /// classic non-regions ephemeral segment; individual generation segments in a regions-based
    /// heap are classified as Small/SOH and have non-empty <c>Generation0</c> ranges.
    /// </summary>
    // Classification helpers moved to SegmentKindMapper

    /// <summary>Mutable per-<see cref="RegionGenerationKind"/> accumulator — at most 7 live instances (one per bucket).</summary>
    private sealed class RegionBucketAccumulator
    {
        public int Count;
        public ulong TotalReservedBytes;
        public ulong TotalCommittedBytes;
        public ulong MinReservedBytes = ulong.MaxValue;
        public ulong MaxReservedBytes;
        public int NearEmptyCount;
        public ulong NearEmptyCommittedBytes;

        public void Add(ulong reserved, ulong committed, bool isNearEmpty)
        {
            Count++;
            TotalReservedBytes += reserved;
            TotalCommittedBytes += committed;
            if (reserved < MinReservedBytes) MinReservedBytes = reserved;
            if (reserved > MaxReservedBytes) MaxReservedBytes = reserved;
            if (isNearEmpty)
            {
                NearEmptyCount++;
                NearEmptyCommittedBytes += committed;
            }
        }
    }
}
