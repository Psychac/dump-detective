using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="SegmentReservationFindingGenerator"/> (old) and
/// <see cref="SegmentReservationSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike the old finding generator's own unit tests (which set
/// <c>AddressSpacePressureRisk</c>/<c>PressureRiskReason</c> as independent fixture fields,
/// including reason text that doesn't match the real formula's own message shape), these fixtures
/// compute both from the same <c>EvaluatePressureRisk</c> logic the rule uses — the same "rebuild
/// fixtures consistent with real production coupling" principle
/// <c>AllocationPatternSynthesisRuleParityTests</c> established, since
/// <c>ThirtyTwoBitPressureThresholdBytes</c>/ratio/pointer-size are what actually drive both fields
/// in production, never set independently.
/// </remarks>
public sealed class SegmentReservationSynthesisRuleParityTests
{
    private readonly SegmentReservationFindingGenerator _oldGenerator = new();
    private readonly SegmentReservationSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task AddressSpacePressureRisk_32Bit_EmitsCriticalFinding() => AssertParity(BuildResult(
        dumpPointerSize: 4, totalReservedBytes: 150_000_000, thirtyTwoBitPressureThresholdBytes: 100_000_000));

    [Fact]
    public Task AddressSpacePressureRisk_64Bit_EmitsWarningFinding() => AssertParity(BuildResult(
        dumpPointerSize: 8, reservedToCommittedRatio: 7.0, ratioHighPressureThreshold: 6.0, ratioMediumPressureThreshold: 3.0));

    [Fact]
    public Task RatioAboveHighThreshold_EmitsCriticalFinding() => AssertParity(BuildResult(
        reservedToCommittedRatio: 10.0, ratioHighPressureThreshold: 6.0, ratioMediumPressureThreshold: 3.0));

    [Fact]
    public Task RatioAboveMediumThreshold_BelowHighThreshold_EmitsWarningFinding() => AssertParity(BuildResult(
        reservedToCommittedRatio: 4.0, ratioHighPressureThreshold: 6.0, ratioMediumPressureThreshold: 3.0));

    [Fact]
    public Task RatioAtOrBelowMediumThreshold_EmitsNoReservationRatioFinding() => AssertParity(BuildResult(
        reservedToCommittedRatio: 3.0, ratioHighPressureThreshold: 6.0, ratioMediumPressureThreshold: 3.0));

    [Fact]
    public Task EphemeralFillAboveNinety_EmitsWarningFinding() => AssertParity(BuildResult(
        avgEphemeralFillPct: 95.0, ephemeralSegmentCount: 3));

    [Fact]
    public Task EphemeralFillAtNinety_EmitsNoEphemeralFinding() => AssertParity(BuildResult(avgEphemeralFillPct: 90.0));

    [Fact]
    public Task NearEmptyRegions_ReclaimableAboveThreshold_EmitsWarningFinding() => AssertParity(BuildResult(
        isRegionsBased: true, nearEmptyRegionBuckets: [(5, 100 * 1024 * 1024UL)]));

    [Fact]
    public Task NearEmptyRegions_ReclaimableAtOrBelowThreshold_EmitsInfoFinding() => AssertParity(BuildResult(
        isRegionsBased: true, nearEmptyRegionBuckets: [(2, 10 * 1024 * 1024UL)]));

    [Fact]
    public Task NearEmptyRegions_SummedAcrossMultipleBuckets_EmitsWarningFinding() => AssertParity(BuildResult(
        isRegionsBased: true, nearEmptyRegionBuckets: [(2, 10 * 1024 * 1024UL), (3, 60 * 1024 * 1024UL)]));

    [Fact]
    public Task RegionsBased_NoNearEmptyRegions_EmitsNoDecommitFinding() => AssertParity(BuildResult(
        isRegionsBased: true, nearEmptyRegionBuckets: [(0, 0UL)]));

    [Fact]
    public Task NotRegionsBased_EmitsNoDecommitFindingRegardlessOfCount() => AssertParity(BuildResult(isRegionsBased: false));

    [Fact]
    public Task NoTriggeredBranches_StillEmitsInfoOverviewFinding() => AssertParity(BuildResult());

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        dumpPointerSize: 4, totalReservedBytes: 150_000_000, thirtyTwoBitPressureThresholdBytes: 100_000_000,
        avgEphemeralFillPct: 95.0, ephemeralSegmentCount: 3,
        isRegionsBased: true, nearEmptyRegionBuckets: [(5, 100 * 1024 * 1024UL)]));

    private static (SegmentReservationDomainResult Result, IReadOnlyList<RegionGenerationStats> Regions, ulong ThirtyTwoBitPressureThresholdBytes) BuildResult(
        int dumpPointerSize = 8,
        ulong totalReservedBytes = 150_000_000,
        ulong thirtyTwoBitPressureThresholdBytes = 1_500_000_000,
        double reservedToCommittedRatio = 1.5,
        double ratioHighPressureThreshold = 6.0,
        double ratioMediumPressureThreshold = 3.0,
        double avgEphemeralFillPct = 50.0,
        int ephemeralSegmentCount = 2,
        bool isRegionsBased = false,
        IReadOnlyList<(int NearEmptyCount, ulong NearEmptyCommittedBytes)>? nearEmptyRegionBuckets = null)
    {
        (bool pressureRisk, string pressureReason) = EvaluatePressureRisk(
            dumpPointerSize, totalReservedBytes, reservedToCommittedRatio, ratioHighPressureThreshold, thirtyTwoBitPressureThresholdBytes);

        var regions = new List<RegionGenerationStats>();
        int nearEmptyRegionCount = 0;
        ulong nearEmptyRegionCommittedBytes = 0;
        if (isRegionsBased)
        {
            var kinds = new[] { RegionGenerationKind.Generation0, RegionGenerationKind.Generation1, RegionGenerationKind.Generation2 };
            IReadOnlyList<(int, ulong)> buckets = nearEmptyRegionBuckets ?? [(0, 0UL)];
            for (int i = 0; i < buckets.Count; i++)
            {
                regions.Add(new RegionGenerationStats(
                    Kind: kinds[i % kinds.Length],
                    Count: buckets[i].Item1 + 1,
                    TotalReservedBytes: 1_000_000,
                    TotalCommittedBytes: 500_000,
                    MinReservedBytes: 1_000,
                    MaxReservedBytes: 10_000,
                    NearEmptyCount: buckets[i].Item1,
                    NearEmptyCommittedBytes: buckets[i].Item2));
                nearEmptyRegionCount += buckets[i].Item1;
                nearEmptyRegionCommittedBytes += buckets[i].Item2;
            }
        }

        var result = new SegmentReservationDomainResult(
            TotalCommittedBytes: 100_000_000,
            TotalReservedBytes: totalReservedBytes,
            ReservationGapBytes: totalReservedBytes > 100_000_000 ? totalReservedBytes - 100_000_000 : 0,
            ReservedToCommittedRatio: reservedToCommittedRatio,
            EphemeralSegmentCount: ephemeralSegmentCount,
            AvgEphemeralFillPct: avgEphemeralFillPct,
            MaxEphemeralFillPct: avgEphemeralFillPct,
            NonEphemeralSohSegmentCount: 1,
            TotalSegmentCount: 5,
            SegmentTable: [],
            ReservedByLogicalHeap: new Dictionary<int, ulong> { [0] = totalReservedBytes },
            CommittedByLogicalHeap: new Dictionary<int, ulong> { [0] = 100_000_000 },
            ReservedByKind: new Dictionary<HeapSegmentKind, ulong>(),
            CommittedByKind: new Dictionary<HeapSegmentKind, ulong>(),
            SegmentCountByKind: new Dictionary<HeapSegmentKind, int>(),
            AddressSpacePressureRisk: pressureRisk,
            PressureRiskReason: pressureReason,
            RatioHighPressureThreshold: ratioHighPressureThreshold,
            RatioMediumPressureThreshold: ratioMediumPressureThreshold,
            DumpPointerSize: dumpPointerSize,
            IsServerGc: false,
            LogicalHeapCount: 1,
            IsRegionsBased: isRegionsBased,
            RegionStats: regions,
            NearEmptyRegionCount: nearEmptyRegionCount,
            NearEmptyRegionCommittedBytes: nearEmptyRegionCommittedBytes,
            NearEmptyRegionFillPctThreshold: 5.0);

        return (result, regions, thirtyTwoBitPressureThresholdBytes);
    }

    private static (bool PressureRisk, string PressureReason) EvaluatePressureRisk(
        int dumpPointerSize, ulong totalReserved, double ratio, double ratioHighPressureThreshold, ulong thirtyTwoBitPressureThresholdBytes)
    {
        if (dumpPointerSize == 4 && totalReserved > thirtyTwoBitPressureThresholdBytes)
            return (true, $"32-bit process has {totalReserved / (1024 * 1024):N0} MB reserved (>{thirtyTwoBitPressureThresholdBytes / (1024 * 1024):N0} MB threshold).");

        if (ratio > ratioHighPressureThreshold)
            return (true, $"Reserved-to-committed ratio is {ratio:F1}x (>{ratioHighPressureThreshold:F0}x threshold). GC is holding large uncommitted reservations.");

        return (false, string.Empty);
    }

    private async Task AssertParity((SegmentReservationDomainResult Result, IReadOnlyList<RegionGenerationStats> Regions, ulong ThirtyTwoBitPressureThresholdBytes) fixture)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(fixture.Result);
        IReadOnlyList<Observation> observations = ToObservations(fixture.Result, fixture.Regions, fixture.ThirtyTwoBitPressureThresholdBytes);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    private static IReadOnlyList<Observation> ToObservations(
        SegmentReservationDomainResult result, IReadOnlyList<RegionGenerationStats> regions, ulong thirtyTwoBitPressureThresholdBytes)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapSegments };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.segment-reservation",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["committed.bytes"] = new Measure(result.TotalCommittedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["reserved.bytes"] = new Measure(result.TotalReservedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["reservation-gap.bytes"] = new Measure(result.ReservationGapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["reserved-to-committed.ratio"] = new Measure(result.ReservedToCommittedRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["ephemeral.segment-count"] = new Measure(result.EphemeralSegmentCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["ephemeral.avg-fill-percent"] = new Measure(result.AvgEphemeralFillPct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["segment.count"] = new Measure(result.TotalSegmentCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dump-pointer-size"] = new Measure(result.DumpPointerSize, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["logical-heap.count"] = new Measure(result.LogicalHeapCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["ratio-high-pressure.threshold"] = new Measure(result.RatioHighPressureThreshold, MeasureUnit.Ratio, MeasureSemantics.Absolute),
                    ["ratio-medium-pressure.threshold"] = new Measure(result.RatioMediumPressureThreshold, MeasureUnit.Ratio, MeasureSemantics.Absolute),
                    ["thirty-two-bit-pressure.threshold-bytes"] = new Measure(thirtyTwoBitPressureThresholdBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["near-empty-region-fill-percent.threshold"] = new Measure(result.NearEmptyRegionFillPctThreshold, MeasureUnit.Percent, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        if (result.IsRegionsBased)
        {
            foreach (RegionGenerationStats region in regions)
            {
                observations.Add(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "heap.region-composition",
                    Subjects = [],
                    When = Point(),
                    Measures = new Dictionary<string, Measure>
                    {
                        ["region-kind.ordinal"] = new Measure((double)region.Kind, MeasureUnit.Custom, MeasureSemantics.Absolute),
                        ["region.count"] = new Measure(region.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["reserved.bytes"] = new Measure(region.TotalReservedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["committed.bytes"] = new Measure(region.TotalCommittedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["near-empty.count"] = new Measure(region.NearEmptyCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["near-empty.committed-bytes"] = new Measure(region.NearEmptyCommittedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    },
                    Provenance = Provenance(capabilitiesUsed),
                    Confidence = 1.0,
                });
            }
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Segment Reservation Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
