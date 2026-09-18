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
/// Field-for-field parity between <see cref="LohFragmentationFindingGenerator"/> (old) and
/// <see cref="LohFragmentationSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class LohFragmentationSynthesisRuleParityTests
{
    private readonly LohFragmentationFindingGenerator _oldGenerator = new();
    private readonly LohFragmentationSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task FragmentationAtOrAboveThirty() => AssertParity(BuildResult(35.0));

    [Fact]
    public Task FragmentationAtThirtyBoundary() => AssertParity(BuildResult(30.0));

    [Fact]
    public Task FragmentationBetweenFifteenAndThirty() => AssertParity(BuildResult(20.0));

    [Fact]
    public Task FragmentationAtFifteenBoundary() => AssertParity(BuildResult(15.0));

    [Fact]
    public Task FragmentationBelowFifteen() => AssertParity(BuildResult(5.0));

    /// <summary>
    /// Two kind rows (Large + Pinned) with different per-kind fragmentation percentages —
    /// specifically exercises that the rule recombines by summing bytes, not averaging percents.
    /// A naive average of 50% and 10% would give 30% (Critical); the correct weighted combination
    /// here gives 20% (Warning), which is what the old generator's own combined total computes to.
    /// </summary>
    [Fact]
    public Task TwoKindsWithDifferentFragmentation_WeightedNotAveraged()
    {
        // Large: 300M total, 150M free (50%). Pinned: 100M total, 10M free (10%).
        // Combined: 400M total, 160M free -> 40%.
        ulong largeTotal = 300_000_000, largeFree = 150_000_000;
        ulong pinnedTotal = 100_000_000, pinnedFree = 10_000_000;
        ulong combinedTotal = largeTotal + pinnedTotal;
        ulong combinedFree = largeFree + pinnedFree;
        double combinedPercent = combinedFree * 100.0 / combinedTotal;

        LohFragmentationDomainResult result = new(
            SegmentCount: 5,
            TotalBytes: combinedTotal,
            FreeBytes: combinedFree,
            UsedBytes: combinedTotal - combinedFree,
            FreeBlockCount: 20,
            FragmentationPercent: combinedPercent,
            LargestFreeBlock: 1_000_000);

        IReadOnlyList<Observation> observations =
        [
            KindObservation(kindOrdinal: 0, segmentCount: 3, totalBytes: largeTotal, freeBytes: largeFree),
            KindObservation(kindOrdinal: 1, segmentCount: 2, totalBytes: pinnedTotal, freeBytes: pinnedFree),
        ];

        return AssertParity(result, observations);
    }

    private async Task AssertParity(LohFragmentationDomainResult result, IReadOnlyList<Observation>? observationsOverride = null)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = observationsOverride ?? [ToObservation(result)];
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title);
            newFinding.Narrative.Should().Be(oldFinding.Evidence);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString());
            newFinding.Category.Should().Be(oldFinding.Category);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer);
            newFinding.Tags.Should().Equal(oldFinding.Tags);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit);
        }
    }

    private static Observation ToObservation(LohFragmentationDomainResult result) =>
        KindObservation(kindOrdinal: 0, segmentCount: result.SegmentCount, totalBytes: result.TotalBytes, freeBytes: result.FreeBytes);

    private static Observation KindObservation(int kindOrdinal, int segmentCount, ulong totalBytes, ulong freeBytes)
    {
        ulong usedBytes = totalBytes - freeBytes;
        double fragmentationPercent = totalBytes > 0 ? freeBytes * 100.0 / totalBytes : 0.0;

        return new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "heap.loh-fragmentation",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["kind.ordinal"] = new Measure(kindOrdinal, MeasureUnit.Custom, MeasureSemantics.Absolute),
                ["segment.count"] = new Measure(segmentCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["total.bytes"] = new Measure(totalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["free.bytes"] = new Measure(freeBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["used.bytes"] = new Measure(usedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["fragmentation.percent"] = new Measure(fragmentationPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                ["largest-free-block.bytes"] = new Measure(1_000_000, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = TestArtifact,
                AnalyzerKey = "LOH & POH Fragmentation Analysis",
                CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapSegments },
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        };
    }

    private static LohFragmentationDomainResult BuildResult(double fragmentationPercent) => new(
        SegmentCount: 4,
        TotalBytes: 400_000_000,
        FreeBytes: (ulong)(400_000_000 * (fragmentationPercent / 100.0)),
        UsedBytes: 400_000_000 - (ulong)(400_000_000 * (fragmentationPercent / 100.0)),
        FreeBlockCount: 10,
        FragmentationPercent: fragmentationPercent,
        LargestFreeBlock: 1_000_000);
}
