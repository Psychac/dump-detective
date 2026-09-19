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
/// Field-for-field parity between <see cref="HeapTopologyFindingGenerator"/> (old) and
/// <see cref="HeapTopologySynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike the old finding generator's own unit tests (which set <c>TotalCommittedBytes</c>/
/// <c>LohPercent</c>/<c>PohPercent</c> as independent fixture fields, decoupled from
/// <c>KindSummaries</c>), these fixtures derive every whole-run scalar from the same per-kind bytes
/// the rule reconstructs its own totals from — the same "rebuild fixtures consistent with real
/// production coupling" principle <c>AllocationPatternSynthesisRuleParityTests</c> established,
/// since <c>HeapTopologyAnalyzer.Analyze</c> only ever computes these together.
/// </remarks>
public sealed class HeapTopologySynthesisRuleParityTests
{
    private readonly HeapTopologyFindingGenerator _oldGenerator = new();
    private readonly HeapTopologySynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task LohPercentAtOrAboveForty_OverviewIsCritical() => AssertParity(
        BuildResult(sohBytes: 500_000, lohBytes: 500_000));

    [Fact]
    public Task LohPercentBetweenTwentyFiveAndForty_OverviewIsWarning() => AssertParity(
        BuildResult(sohBytes: 700_000, lohBytes: 300_000));

    [Fact]
    public Task LohPercentBelowTwentyFive_OverviewIsInfo() => AssertParity(
        BuildResult(sohBytes: 900_000, lohBytes: 100_000));

    [Fact]
    public Task PohPercentAboveTen_EmitsWarningFinding() => AssertParity(
        BuildResult(sohBytes: 850_000, pohBytes: 150_000));

    [Fact]
    public Task LogicalHeapsSkewedAboveTwoX_EmitsWarningFinding() => AssertParity(BuildResult(
        perLogicalHeapSummaries:
        [
            new PerLogicalHeapSummary(LogicalHeapIndex: 0, Bytes: 100 * 1024 * 1024UL, ObjectCount: 1000, SegmentCount: 2),
            new PerLogicalHeapSummary(LogicalHeapIndex: 1, Bytes: 300 * 1024 * 1024UL, ObjectCount: 3000, SegmentCount: 2),
        ]));

    [Fact]
    public Task LogicalHeapsSkewedAboveTwoX_ServerGc_MentionsServerGc() => AssertParity(BuildResult(
        isServerGc: true,
        perLogicalHeapSummaries:
        [
            new PerLogicalHeapSummary(LogicalHeapIndex: 0, Bytes: 100 * 1024 * 1024UL, ObjectCount: 1000, SegmentCount: 2),
            new PerLogicalHeapSummary(LogicalHeapIndex: 1, Bytes: 300 * 1024 * 1024UL, ObjectCount: 3000, SegmentCount: 2),
        ]));

    [Fact]
    public Task LogicalHeapsNotSkewed_EmitsNoSkewFinding() => AssertParity(BuildResult(
        perLogicalHeapSummaries:
        [
            new PerLogicalHeapSummary(LogicalHeapIndex: 0, Bytes: 200 * 1024 * 1024UL, ObjectCount: 1000, SegmentCount: 2),
            new PerLogicalHeapSummary(LogicalHeapIndex: 1, Bytes: 300 * 1024 * 1024UL, ObjectCount: 3000, SegmentCount: 2),
        ]));

    [Fact]
    public Task SingleLogicalHeap_EmitsNoSkewFinding() => AssertParity(BuildResult(
        perLogicalHeapSummaries: [new PerLogicalHeapSummary(LogicalHeapIndex: 0, Bytes: 100 * 1024 * 1024UL, ObjectCount: 1000, SegmentCount: 2)]));

    [Fact]
    public Task UnrecognizedSegmentKind_EmitsWarningFinding() => AssertParity(
        BuildResult(sohBytes: 1_000_000, unknownSegmentCount: 2, unknownBytes: 500_000));

    [Fact]
    public Task NoUnrecognizedSegmentKind_EmitsNoUnknownFinding() => AssertParity(
        BuildResult(sohBytes: 1_000_000));

    [Fact]
    public Task SohFragmentationAtOrAboveFortyFive_EmitsWarningFinding() => AssertParity(
        BuildResult(sohBytes: 1000, sohFragmentedBytes: 460));

    [Fact]
    public Task SohFragmentationBetweenTwentyFiveAndFortyFive_EmitsInfoFinding() => AssertParity(
        BuildResult(sohBytes: 1000, sohFragmentedBytes: 300));

    [Fact]
    public Task SohFragmentationBelowTwentyFive_EmitsNoSohFragmentationFinding() => AssertParity(
        BuildResult(sohBytes: 1000, sohFragmentedBytes: 100));

    [Fact]
    public Task LohFragmentationAtOrAboveFifty_EmitsCriticalFinding() => AssertParity(
        BuildResult(lohBytes: 1000, lohFragmentedBytes: 550));

    [Fact]
    public Task LohFragmentationBetweenThirtyAndFifty_EmitsWarningFinding() => AssertParity(
        BuildResult(lohBytes: 1000, lohFragmentedBytes: 350));

    [Fact]
    public Task PohFragmentationAtOrAboveTwenty_EmitsInfoFinding() => AssertParity(
        BuildResult(pohBytes: 1000, pohFragmentedBytes: 250));

    [Fact]
    public Task FrozenFragmentationAtOrAboveThirty_EmitsInfoFinding() => AssertParity(
        BuildResult(frozenBytes: 1000, frozenFragmentedBytes: 350));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        sohBytes: 100_000, sohFragmentedBytes: 46_000,
        lohBytes: 400_000, lohFragmentedBytes: 220_000,
        pohBytes: 400_000, pohFragmentedBytes: 100_000,
        frozenBytes: 100_000, frozenFragmentedBytes: 35_000,
        unknownSegmentCount: 1, unknownBytes: 10_000,
        isServerGc: true,
        perLogicalHeapSummaries:
        [
            new PerLogicalHeapSummary(LogicalHeapIndex: 0, Bytes: 100 * 1024 * 1024UL, ObjectCount: 1000, SegmentCount: 2),
            new PerLogicalHeapSummary(LogicalHeapIndex: 1, Bytes: 300 * 1024 * 1024UL, ObjectCount: 3000, SegmentCount: 2),
        ]));

    private static HeapTopologyDomainResult BuildResult(
        ulong sohBytes = 0, int sohSegmentCount = 1, ulong sohFragmentedBytes = 0,
        ulong lohBytes = 0, int lohSegmentCount = 1, ulong lohFragmentedBytes = 0,
        ulong pohBytes = 0, int pohSegmentCount = 1, ulong pohFragmentedBytes = 0,
        ulong frozenBytes = 0, int frozenSegmentCount = 1, ulong frozenFragmentedBytes = 0,
        int unknownSegmentCount = 0, ulong unknownBytes = 0,
        IReadOnlyList<PerLogicalHeapSummary>? perLogicalHeapSummaries = null,
        bool isServerGc = false)
    {
        ulong totalCommitted = sohBytes + lohBytes + pohBytes + frozenBytes + unknownBytes;
        double lohPercent = totalCommitted == 0 ? 0.0 : lohBytes * 100.0 / totalCommitted;
        double pohPercent = totalCommitted == 0 ? 0.0 : pohBytes * 100.0 / totalCommitted;
        double frozenPercent = totalCommitted == 0 ? 0.0 : frozenBytes * 100.0 / totalCommitted;

        var kindSummaries = new List<SegmentKindSummary>
        {
            new(HeapSegmentKind.SmallObjectHeap, sohSegmentCount, 0, sohBytes, 0),
            new(HeapSegmentKind.LargeObjectHeap, lohSegmentCount, 0, lohBytes, 0),
            new(HeapSegmentKind.PinnedObjectHeap, pohSegmentCount, 0, pohBytes, 0),
            new(HeapSegmentKind.Frozen, frozenSegmentCount, 0, frozenBytes, 0),
            new(HeapSegmentKind.Unknown, unknownSegmentCount, 0, unknownBytes, 0),
        };

        IReadOnlyList<PerLogicalHeapSummary> heaps = perLogicalHeapSummaries ?? [];

        return new HeapTopologyDomainResult(
            TotalSegments: sohSegmentCount + lohSegmentCount + pohSegmentCount + frozenSegmentCount + unknownSegmentCount,
            TotalCommittedBytes: totalCommitted,
            TotalUsedBytes: 0,
            TotalReservedBytes: 0,
            ReservationGapBytes: 0,
            SohSegmentCount: sohSegmentCount,
            SohBytes: sohBytes,
            LohSegmentCount: lohSegmentCount,
            LohBytes: lohBytes,
            PohSegmentCount: pohSegmentCount,
            PohBytes: pohBytes,
            FrozenSegmentCount: frozenSegmentCount,
            FrozenBytes: frozenBytes,
            FrozenPercent: frozenPercent,
            LohPercent: lohPercent,
            PohPercent: pohPercent,
            Gen0Bytes: 0,
            Gen1Bytes: 0,
            Gen2Bytes: 0,
            SohFragmentedBytes: sohFragmentedBytes,
            LohFragmentedBytes: lohFragmentedBytes,
            PohFragmentedBytes: pohFragmentedBytes,
            FrozenFragmentedBytes: frozenFragmentedBytes,
            IsServerGc: isServerGc,
            LogicalHeapCount: heaps.Count,
            KindSummaries: kindSummaries,
            PerLogicalHeapSummaries: heaps);
    }

    private async Task AssertParity(HeapTopologyDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
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

    private static IReadOnlyList<Observation> ToObservations(HeapTopologyDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapSegments, CapabilityVocabulary.HeapTypes };
        var observations = new List<Observation>();

        foreach (SegmentKindSummary kind in result.KindSummaries)
        {
            var measures = new Dictionary<string, Measure>
            {
                ["kind.ordinal"] = new Measure((double)kind.Kind, MeasureUnit.Custom, MeasureSemantics.Absolute),
                ["segment.count"] = new Measure(kind.SegmentCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["object.count"] = new Measure(kind.ObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["committed.bytes"] = new Measure(kind.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["reserved.bytes"] = new Measure(kind.ReservedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            };

            ulong? fragmentedBytes = kind.Kind switch
            {
                HeapSegmentKind.SmallObjectHeap => result.SohFragmentedBytes,
                HeapSegmentKind.LargeObjectHeap => result.LohFragmentedBytes,
                HeapSegmentKind.PinnedObjectHeap => result.PohFragmentedBytes,
                HeapSegmentKind.Frozen => result.FrozenFragmentedBytes,
                _ => null,
            };
            if (fragmentedBytes is ulong fb)
                measures["fragmented.bytes"] = new Measure(fb, MeasureUnit.Bytes, MeasureSemantics.Absolute);

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.segment-kind-composition",
                Subjects = [],
                When = Point(),
                Measures = measures,
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        ulong maxLogicalHeapBytes = 0, minLogicalHeapBytes = 0;
        for (int i = 0; i < result.PerLogicalHeapSummaries.Count; i++)
        {
            ulong bytes = result.PerLogicalHeapSummaries[i].Bytes;
            if (i == 0 || bytes > maxLogicalHeapBytes) maxLogicalHeapBytes = bytes;
            if (i == 0 || bytes < minLogicalHeapBytes) minLogicalHeapBytes = bytes;
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "heap.topology-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["is-server-gc"] = new Measure(result.IsServerGc ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
                ["logical-heap.count"] = new Measure(result.LogicalHeapCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["logical-heap.max-bytes"] = new Measure(maxLogicalHeapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["logical-heap.min-bytes"] = new Measure(minLogicalHeapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(capabilitiesUsed),
            Confidence = 1.0,
        });

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Heap Topology",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
