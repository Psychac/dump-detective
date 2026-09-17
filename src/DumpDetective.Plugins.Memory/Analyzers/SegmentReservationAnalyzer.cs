using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

// DumpDetective.Analysis.Models and DumpDetective.Sdk.Analysis both declare HeapSegmentKind and
// RegionGenerationKind (deliberately identical names, see SdkSegmentKindMapper) — alias the
// dump-side ones (what this analyzer's still-unchanged *DomainResult output needs) to disambiguate.
using DumpHeapSegmentKind = DumpDetective.Analysis.Models.HeapSegmentKind;
using DumpRegionGenerationKind = DumpDetective.Analysis.Models.RegionGenerationKind;
using SdkHeapSegmentRef = DumpDetective.Sdk.Analysis.HeapSegmentRef;

namespace DumpDetective.Analysis.Analyzers.Memory;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing
/// everything through <see cref="IHeapSegmentQuery"/> (the <c>heap.segments</c> capability) instead
/// of a raw <c>ClrHeap</c>/<c>IHeapAnalysisCache</c>. Runs through the existing pipeline via
/// <see cref="SegmentReservationAnalyzerLegacyAdapter"/>.
///
/// Covers §25.1 (committed vs reserved), §25.2 (segment lifecycle), and §25.3 (address space
/// pressure). Operates entirely on segment metadata — no heap object scan.
/// </summary>
[AnalyzerModule(key: "segment-reservation", displayName: "Segment Reservation Analysis", order: 380, tags: ["gc", "segments"])]
[RequiresCapability(CapabilityVocabulary.HeapSegments)]
public sealed class SegmentReservationAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Segment Reservation Analysis";
    public string Category => "Memory";

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapSegmentQuery segmentQuery = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        SegmentReservationAnalysisOptions options = context.AnalyzerOptions as SegmentReservationAnalysisOptions ?? new SegmentReservationAnalysisOptions();

        var result = (SegmentReservationDomainResult)Analyze(segmentQuery, options, context.Progress, cancellationToken);
        LastResult = result;

        await EmitObservationsAsync(context, result, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): a single
    /// whole-heap <c>heap.segment-reservation</c> observation, plus one <c>heap.region-composition</c>
    /// observation per <see cref="SegmentReservationDomainResult.RegionStats"/> row when
    /// <see cref="SegmentReservationDomainResult.IsRegionsBased"/> (bounded to at most 7 buckets —
    /// see <see cref="RegionBucketAccumulator"/>'s own remarks). Deliberately excludes
    /// <see cref="SegmentReservationDomainResult.AddressSpacePressureRisk"/>/
    /// <see cref="SegmentReservationDomainResult.PressureRiskReason"/> — a hand-picked-threshold
    /// classification, the same Smell A shape already fixed on the other four Memory analyzers — and
    /// <see cref="SegmentReservationDomainResult.SegmentTable"/>, for the same "no <c>segment</c>
    /// entity kind yet" reason <c>MemoryAnalyzer</c>/<c>LohFragmentationAnalyzer</c> skip theirs.
    /// </summary>
    private static async ValueTask EmitObservationsAsync(AnalysisContext context, SegmentReservationDomainResult result, CancellationToken cancellationToken)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapSegments };

        await context.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "heap.segment-reservation",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["committed.bytes"] = new Measure(result.TotalCommittedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["reserved.bytes"] = new Measure(result.TotalReservedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["reservation-gap.bytes"] = new Measure(result.ReservationGapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["reserved-to-committed.ratio"] = new Measure(result.ReservedToCommittedRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                ["ephemeral.segment-count"] = new Measure(result.EphemeralSegmentCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["ephemeral.avg-fill-percent"] = new Measure(result.AvgEphemeralFillPct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                ["segment.count"] = new Measure(result.TotalSegmentCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = context.ArtifactId!.Value,
                AnalyzerKey = "Segment Reservation Analysis",
                CapabilitiesUsed = capabilitiesUsed,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        if (!result.IsRegionsBased)
            return;

        foreach (RegionGenerationStats region in result.RegionStats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.region-composition",
                Subjects = [],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["region-kind.ordinal"] = new Measure((double)region.Kind, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["region.count"] = new Measure(region.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["reserved.bytes"] = new Measure(region.TotalReservedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["committed.bytes"] = new Measure(region.TotalCommittedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["near-empty.count"] = new Measure(region.NearEmptyCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["near-empty.committed-bytes"] = new Measure(region.NearEmptyCommittedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "Segment Reservation Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }
    }

    private static AnalyzerDomainResult Analyze(
        IHeapSegmentQuery segmentQuery,
        SegmentReservationAnalysisOptions options,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        ulong totalCommitted = 0;
        ulong totalReserved = 0;

        int ephemeralCount = 0;
        double ephemeralFillSum = 0.0;
        int nonEphemeralSohCount = 0;

        var segmentTable = new List<SegmentReservationEntry>(64);
        var reservedByHeap = new Dictionary<int, ulong>(16);
        var committedByHeap = new Dictionary<int, ulong>(16);
        var reservedByKind = new Dictionary<DumpHeapSegmentKind, ulong>();
        var committedByKind = new Dictionary<DumpHeapSegmentKind, ulong>();
        var segmentCountByKind = new Dictionary<DumpHeapSegmentKind, int>();
        var regionBuckets = new Dictionary<DumpRegionGenerationKind, RegionBucketAccumulator>(8);

        int totalSegmentCount = 0;
        double maxEphemeralFillPct = 0.0;
        bool isRegionsBased = false;
        const int ProgressReportInterval = 128;

        foreach (SdkHeapSegmentRef segment in segmentQuery.EnumerateSegments())
        {
            if ((totalSegmentCount % ProgressReportInterval) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(totalSegmentCount, "analyzing segment reservation", $"{totalSegmentCount} segments processed"));
            }

            ulong committed = segment.CommittedBytes;
            ulong reserved = segment.ReservedBytes;
            bool isEphemeral = segment.IsEphemeral;
            int logicalHeap = segment.LogicalHeapIndex >= 0 ? segment.LogicalHeapIndex : 0;
            DumpHeapSegmentKind kind = SdkSegmentKindMapper.ToDump(segment.Kind);
            DumpRegionGenerationKind regionKind = SdkSegmentKindMapper.ToDump(segment.RegionKind);
            if (regionKind is DumpRegionGenerationKind.Generation0 or DumpRegionGenerationKind.Generation1)
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
            ulong length = segment.End > segment.Start ? segment.End - segment.Start : 0;
            double fillPct = 0.0;
            if (length > 0)
            {
                fillPct = committed / (double)length * 100.0;
                if (fillPct > 100.0) fillPct = 100.0;
            }

            if (isEphemeral)
            {
                ephemeralCount++;
                ephemeralFillSum += fillPct;
                if (fillPct > maxEphemeralFillPct) maxEphemeralFillPct = fillPct;
            }
            else if (kind == DumpHeapSegmentKind.SmallObjectHeap)
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
        int dumpPointerSize = segmentQuery.DumpPointerSize;
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
            foreach (KeyValuePair<DumpRegionGenerationKind, RegionBucketAccumulator> kvp in regionBuckets)
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
            IsServerGc: segmentQuery.IsServerGc,
            LogicalHeapCount: reservedByHeap.Count,
            IsRegionsBased: isRegionsBased,
            RegionStats: regionStats,
            NearEmptyRegionCount: nearEmptyRegionCount,
            NearEmptyRegionCommittedBytes: nearEmptyRegionCommittedBytes,
            NearEmptyRegionFillPctThreshold: options.NearEmptyRegionFillPctThreshold);
    }

    /// <summary>Mutable per-<see cref="DumpRegionGenerationKind"/> accumulator — at most 7 live instances (one per bucket).</summary>
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

    public void Dispose() { }
}
