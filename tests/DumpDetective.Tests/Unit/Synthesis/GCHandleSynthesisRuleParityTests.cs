using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="GCHandleFindingGenerator"/> (old) and
/// <see cref="GCHandleSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike the old generator's own unit tests (<c>GCHandleFindingGeneratorTests</c>), which set
/// <c>DependentUnresolvedPercent</c> as an independent field, <see cref="BuildResult"/> here always
/// derives it from <c>dependentUnresolvedTargetCount</c>/<c>dependentHandleCount</c> — the new rule
/// can only reconstruct it that way, the same real-production coupling
/// <c>GCHandleAnalyzer.Analyze</c> itself enforces.
/// </remarks>
public sealed class GCHandleSynthesisRuleParityTests
{
    private readonly GCHandleFindingGenerator _oldGenerator = new();
    private readonly GCHandleSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task HandlesBelowThresholds_InfoPressureSummaryOnly() => AssertParity(BuildResult(
        totalHandles: 100, pinnedHandleTargets: 10));

    [Fact]
    public Task TotalHandlesAboveThreshold_WarningPressureSummary() => AssertParity(BuildResult(
        totalHandles: 20_000, totalHandlesWarningThreshold: 10_000));

    [Fact]
    public Task PinnedRetainedBytesBelowThreshold_NoFinding() => AssertParity(BuildResult(
        pinnedRetainedBytes: 1024, pinnedRetainedBytesWarningThreshold: 100 * 1024 * 1024));

    [Fact]
    public Task PinnedRetainedBytesAboveThreshold_WarningFinding() => AssertParity(BuildResult(
        pinnedRetainedBytes: 200 * 1024 * 1024, pinnedRetainedBytesWarningThreshold: 100 * 1024 * 1024));

    [Fact]
    public Task SohPinnedTargetsBelowThreshold_NoFinding() => AssertParity(BuildResult(
        pinnedSohObjectCount: 10, pinnedSohObjectCountWarningThreshold: 500));

    [Fact]
    public Task SohPinnedTargetsAboveThreshold_CombinesPinnedAndAsyncPinned() => AssertParity(BuildResult(
        pinnedSohObjectCount: 300, asyncPinnedSohObjectCount: 300, pinnedNonSohObjectCount: 5,
        pinnedSohObjectCountWarningThreshold: 500));

    [Fact]
    public Task RefCountedHandlesBelowThreshold_NoFinding() => AssertParity(BuildResult(
        refCountedHandleCount: 10, refCountedHandleCountWarningThreshold: 100));

    [Fact]
    public Task RefCountedHandlesAboveThreshold_NamesDominantType() => AssertParity(BuildResult(
        refCountedHandleCount: 500, refCountedHandleCountWarningThreshold: 100,
        topRefCountedTargetTypes: [new NameCountEntry("Some.Com.Wrapper", 500)]));

    [Fact]
    public Task RefCountedHandlesAboveThreshold_PicksHighestCountType() => AssertParity(BuildResult(
        refCountedHandleCount: 500, refCountedHandleCountWarningThreshold: 100,
        // Sorted descending, matching GCHandleAnalyzer.Analyze's own ToRankedEntries — the old
        // generator trusts list order (TopRefCountedTargetTypes[0]) rather than re-ranking, the
        // same invariant ArrayFindingGenerator/ArraySynthesisRule rely on.
        topRefCountedTargetTypes: [new NameCountEntry("Big.Wrapper", 450), new NameCountEntry("Small.Wrapper", 50)]));

    [Fact]
    public Task WeakLongGen2Concentration_BelowMinimumCount_NoFinding() => AssertParity(BuildResult(
        weakLongGen2Count: 50, weakLongGen2MinimumCountThreshold: 100, weakLongGen2FractionWarningThreshold: 70.0));

    [Fact]
    public Task WeakLongGen2Concentration_AboveFractionAndCountThresholds_WarningFinding() => AssertParity(BuildResult(
        weakLongGen0Count: 10, weakLongGen1Count: 10, weakLongGen2Count: 180, weakLongLohCount: 0,
        weakLongGen2MinimumCountThreshold: 100, weakLongGen2FractionWarningThreshold: 70.0,
        weakShortGen0Count: 5, weakShortGen1Count: 5, weakShortGen2Count: 20, weakShortLohCount: 0));

    [Fact]
    public Task WeakLongGen2Concentration_AboveCountButBelowFraction_NoFinding() => AssertParity(BuildResult(
        weakLongGen0Count: 1000, weakLongGen1Count: 0, weakLongGen2Count: 150, weakLongLohCount: 0,
        weakLongGen2MinimumCountThreshold: 100, weakLongGen2FractionWarningThreshold: 70.0));

    [Fact]
    public Task NoDependentHandles_NoFinding() => AssertParity(BuildResult(dependentHandleCount: 0));

    [Fact]
    public Task DependentHandlesWithLowUnresolvedPercent_InfoFinding() => AssertParity(BuildResult(
        dependentHandleCount: 100, dependentResolvedEdgeCount: 95, dependentUnresolvedTargetCount: 5,
        dependentUnresolvedPercentWarningThreshold: 50.0));

    [Fact]
    public Task DependentHandlesWithHighUnresolvedPercent_WarningFinding() => AssertParity(BuildResult(
        dependentHandleCount: 100, dependentResolvedEdgeCount: 20, dependentUnresolvedTargetCount: 80,
        dependentUnresolvedPercentWarningThreshold: 50.0));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        totalHandles: 20_000, pinnedHandleTargets: 2_000,
        pinnedRetainedBytes: 200 * 1024 * 1024, pinnedRetainedBytesWarningThreshold: 100 * 1024 * 1024,
        pinnedSohObjectCount: 300, asyncPinnedSohObjectCount: 300, pinnedSohObjectCountWarningThreshold: 500,
        refCountedHandleCount: 500, refCountedHandleCountWarningThreshold: 100,
        topRefCountedTargetTypes: [new NameCountEntry("Some.Com.Wrapper", 500)],
        weakLongGen0Count: 10, weakLongGen1Count: 10, weakLongGen2Count: 180, weakLongLohCount: 0,
        weakLongGen2MinimumCountThreshold: 100, weakLongGen2FractionWarningThreshold: 70.0,
        dependentHandleCount: 100, dependentResolvedEdgeCount: 20, dependentUnresolvedTargetCount: 80,
        dependentUnresolvedPercentWarningThreshold: 50.0));

    private async Task AssertParity(GCHandleDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(GCHandleDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapHandles, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments,
        };

        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.population",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(result.TotalHandles, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned-target.count"] = new Measure(result.PinnedHandleTargets, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned-retained.bytes"] = new Measure(result.PinnedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["pinned-soh.count"] = new Measure(result.PinnedSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["async-pinned-soh.count"] = new Measure(result.AsyncPinnedSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned-non-soh.count"] = new Measure(result.PinnedNonSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["async-pinned-non-soh.count"] = new Measure(result.AsyncPinnedNonSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["refcounted.count"] = new Measure(result.RefCountedHandleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weakshort-gen0.count"] = new Measure(result.WeakShortGen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weakshort-gen1.count"] = new Measure(result.WeakShortGen1Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weakshort-gen2.count"] = new Measure(result.WeakShortGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weakshort-loh.count"] = new Measure(result.WeakShortLohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weaklong-gen0.count"] = new Measure(result.WeakLongGen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weaklong-gen1.count"] = new Measure(result.WeakLongGen1Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weaklong-gen2.count"] = new Measure(result.WeakLongGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weaklong-loh.count"] = new Measure(result.WeakLongLohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dependent.count"] = new Measure(result.DependentHandleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dependent-resolved-edge.count"] = new Measure(result.DependentResolvedEdgeCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dependent-unresolved-target.count"] = new Measure(result.DependentUnresolvedTargetCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total-handles-warning.threshold"] = new Measure(result.TotalHandlesWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned-handle-targets-warning.threshold"] = new Measure(result.PinnedHandleTargetsWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned-retained-bytes-warning.threshold"] = new Measure(result.PinnedRetainedBytesWarningThreshold, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["pinned-soh-warning.threshold"] = new Measure(result.PinnedSohObjectCountWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["refcounted-warning.threshold"] = new Measure(result.RefCountedHandleCountWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["weaklong-gen2-fraction-warning.threshold"] = new Measure(result.WeakLongGen2FractionWarningThreshold, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["weaklong-gen2-minimum-count.threshold"] = new Measure(result.WeakLongGen2MinimumCountThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dependent-unresolved-percent-warning.threshold"] = new Measure(result.DependentUnresolvedPercentWarningThreshold, MeasureUnit.Percent, MeasureSemantics.Ratio),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        foreach (NameCountEntry entry in result.TopRefCountedTargetTypes ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.refcounted-target",
                Subjects = [new TypeRef { CanonicalName = entry.Name, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(entry.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(new HashSet<Capability> { CapabilityVocabulary.HeapHandles }),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "GC Handle Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static GCHandleDomainResult BuildResult(
        int totalHandles = 0,
        int pinnedHandleTargets = 0,
        int totalHandlesWarningThreshold = 10_000,
        int pinnedHandleTargetsWarningThreshold = 1_000,
        ulong pinnedRetainedBytes = 0,
        ulong pinnedRetainedBytesWarningThreshold = 100 * 1024 * 1024,
        int pinnedSohObjectCount = 0,
        int pinnedNonSohObjectCount = 0,
        int asyncPinnedSohObjectCount = 0,
        int asyncPinnedNonSohObjectCount = 0,
        int pinnedSohObjectCountWarningThreshold = 500,
        int refCountedHandleCount = 0,
        int refCountedHandleCountWarningThreshold = 100,
        IReadOnlyList<NameCountEntry>? topRefCountedTargetTypes = null,
        int weakShortGen0Count = 0,
        int weakShortGen1Count = 0,
        int weakShortGen2Count = 0,
        int weakShortLohCount = 0,
        int weakLongGen0Count = 0,
        int weakLongGen1Count = 0,
        int weakLongGen2Count = 0,
        int weakLongLohCount = 0,
        double weakLongGen2FractionWarningThreshold = 70.0,
        int weakLongGen2MinimumCountThreshold = 100,
        int dependentHandleCount = 0,
        int dependentResolvedEdgeCount = 0,
        int dependentUnresolvedTargetCount = 0,
        double dependentUnresolvedPercentWarningThreshold = 50.0)
    {
        double dependentUnresolvedPercent = dependentHandleCount == 0
            ? 0.0
            : dependentUnresolvedTargetCount * 100.0 / dependentHandleCount;

        return new GCHandleDomainResult(
            TotalHandles: totalHandles,
            StrongLikeHandles: totalHandles,
            WeakLikeHandles: 0,
            PinnedHandleTargets: pinnedHandleTargets,
            PinnedRetainedBytes: pinnedRetainedBytes,
            PinnedSohObjectCount: pinnedSohObjectCount,
            PinnedNonSohObjectCount: pinnedNonSohObjectCount,
            AsyncPinnedSohObjectCount: asyncPinnedSohObjectCount,
            AsyncPinnedNonSohObjectCount: asyncPinnedNonSohObjectCount,
            RefCountedHandleCount: refCountedHandleCount,
            TopRefCountedTargetTypes: topRefCountedTargetTypes,
            WeakShortGen0Count: weakShortGen0Count,
            WeakShortGen1Count: weakShortGen1Count,
            WeakShortGen2Count: weakShortGen2Count,
            WeakShortLohCount: weakShortLohCount,
            WeakLongGen0Count: weakLongGen0Count,
            WeakLongGen1Count: weakLongGen1Count,
            WeakLongGen2Count: weakLongGen2Count,
            WeakLongLohCount: weakLongLohCount,
            DependentHandleCount: dependentHandleCount,
            DependentResolvedEdgeCount: dependentResolvedEdgeCount,
            DependentUnresolvedTargetCount: dependentUnresolvedTargetCount,
            DependentUnresolvedPercent: dependentUnresolvedPercent,
            TotalHandlesWarningThreshold: totalHandlesWarningThreshold,
            PinnedHandleTargetsWarningThreshold: pinnedHandleTargetsWarningThreshold,
            PinnedRetainedBytesWarningThreshold: pinnedRetainedBytesWarningThreshold,
            PinnedSohObjectCountWarningThreshold: pinnedSohObjectCountWarningThreshold,
            RefCountedHandleCountWarningThreshold: refCountedHandleCountWarningThreshold,
            WeakLongGen2FractionWarningThreshold: weakLongGen2FractionWarningThreshold,
            WeakLongGen2MinimumCountThreshold: weakLongGen2MinimumCountThreshold,
            DependentUnresolvedPercentWarningThreshold: dependentUnresolvedPercentWarningThreshold);
    }
}
