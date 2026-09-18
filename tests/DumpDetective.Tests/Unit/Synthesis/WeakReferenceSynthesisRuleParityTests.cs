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
/// Field-for-field parity between <see cref="WeakReferenceFindingGenerator"/> (old) and
/// <see cref="WeakReferenceSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike the old generator's own unit tests (<c>WeakReferenceFindingGeneratorTests</c>), which set
/// <c>AliveWeakTargets</c>/<c>DeadTargetRatio</c>/<c>StaleWrapperCount</c> as independent fields,
/// <see cref="BuildResult"/> here always derives <c>alive = total - dead</c> and
/// <c>ratio = dead / total</c> — the new rule can only reconstruct them that way (by counting
/// <c>weakhandle.target</c> observations and reading <c>weakreference.population</c>'s own
/// <c>dead-targets.count</c>/<c>total-handles.count</c> measures).
/// </remarks>
public sealed class WeakReferenceSynthesisRuleParityTests
{
    private readonly WeakReferenceFindingGenerator _oldGenerator = new();
    private readonly WeakReferenceSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoSignals_OverviewOnly() => AssertParity(BuildResult(total: 1000, dead: 300, staleWrapperCount: 20));

    [Fact]
    public Task HighDeadRatioAtWarningThreshold() => AssertParity(BuildResult(total: 1000, dead: 500));

    [Fact]
    public Task HighDeadRatioAtCriticalThreshold() => AssertParity(BuildResult(total: 1000, dead: 850));

    [Fact]
    public Task HighDeadRatioBelowThreshold_NoFinding() => AssertParity(BuildResult(total: 1000, dead: 400));

    [Fact]
    public Task ZeroTotalHandles_NoRatioFinding() => AssertParity(BuildResult(total: 0, dead: 0));

    [Fact]
    public Task HighAbsoluteDeadCountAtWarningThreshold() => AssertParity(BuildResult(total: 100_000, dead: 15_000));

    [Fact]
    public Task HighAbsoluteDeadCountAtCriticalThreshold() => AssertParity(BuildResult(total: 500_000, dead: 150_000));

    [Fact]
    public Task DeadCountAtOrBelowThreshold_NoFinding() => AssertParity(BuildResult(total: 20_000, dead: 10_000));

    [Fact]
    public Task DependentDeadKeys() => AssertParity(BuildResult(total: 1000, dead: 300, dependentDeadKeyCount: 25));

    [Fact]
    public Task NoDependentDeadKeys_NoFinding() => AssertParity(BuildResult(total: 1000, dead: 300, dependentDeadKeyCount: 0));

    [Fact]
    public Task HeldOnlyViaWeakReference_WithTopType() => AssertParity(BuildResult(
        total: 1000, dead: 300, heldOnlyDetectionAvailable: true,
        heldOnlyByType: [("MyApp.Cache", 40), ("MyApp.Other", 5)]));

    [Fact]
    public Task HeldOnlyViaWeakReference_DetectionUnavailable_NoFinding() => AssertParity(BuildResult(
        total: 1000, dead: 300, heldOnlyDetectionAvailable: false,
        heldOnlyByType: [("MyApp.Cache", 40)]));

    [Fact]
    public Task HeldOnlyViaWeakReference_ZeroCount_NoFinding() => AssertParity(BuildResult(
        total: 1000, dead: 300, heldOnlyDetectionAvailable: true, heldOnlyByType: []));

    [Fact]
    public Task MultipleSignals_ReturnsAllSorted() => AssertParity(BuildResult(
        total: 55_000, dead: 50_000, staleWrapperCount: 900, dependentDeadKeyCount: 25));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        total: 200_000, dead: 180_000, staleWrapperCount: 900, dependentDeadKeyCount: 25,
        heldOnlyDetectionAvailable: true, heldOnlyByType: [("MyApp.Cache", 40)]));

    private async Task AssertParity(WeakReferenceDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(WeakReferenceDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapHandles, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.DumpObjectFields,
        };

        var observations = new List<Observation>();

        int heldOnlyEmitted = 0;
        var heldOnlyByType = new List<(string TypeName, int Count)>();
        foreach (NameCountEntry entry in result.HeldOnlyViaWeakReferenceTopTypes ?? [])
            heldOnlyByType.Add((entry.Name, entry.Count));

        for (int i = 0; i < result.AliveWeakTargets; i++)
        {
            bool isHeldOnly = false;
            string typeName = "MyApp.AliveType";
            if (heldOnlyEmitted < result.HeldOnlyViaWeakReferenceCount)
            {
                isHeldOnly = true;
                // Attribute this held-only instance to a type from heldOnlyByType, cycling through
                // entries so their relative counts stay proportionally represented.
                int cumulative = 0;
                foreach ((string TypeName, int Count) entry in heldOnlyByType)
                {
                    cumulative += entry.Count;
                    if (heldOnlyEmitted < cumulative)
                    {
                        typeName = entry.TypeName;
                        break;
                    }
                }
                heldOnlyEmitted++;
            }

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "weakhandle.target",
                Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["held-only-via-weak-reference"] = new Measure(isHeldOnly ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        for (int i = 0; i < result.StaleWrapperCount; i++)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "weakreference.stale-wrapper",
                Subjects = [new TypeRef { CanonicalName = "MyApp.StaleHolder", Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["shallow.bytes"] = new Measure(64, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "weakreference.population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["total-handles.count"] = new Measure(result.TotalWeakHandles, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["dead-targets.count"] = new Measure(result.DeadWeakTargets, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["object.count"] = new Measure(result.WeakReferenceObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["object.bytes"] = new Measure(result.WeakReferenceObjectBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["dependent-dead-key.count"] = new Measure(result.DependentHandleDeadKeyCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["held-only-detection-available"] = new Measure(result.HeldOnlyViaWeakReferenceDetectionAvailable ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
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
        AnalyzerKey = "Weak Reference Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static WeakReferenceDomainResult BuildResult(
        int total,
        int dead,
        int staleWrapperCount = 0,
        int dependentDeadKeyCount = 0,
        bool heldOnlyDetectionAvailable = false,
        IReadOnlyList<(string TypeName, int Count)>? heldOnlyByType = null)
    {
        int alive = total - dead;
        heldOnlyByType ??= [];
        int heldOnlyCount = 0;
        foreach ((string _, int count) in heldOnlyByType) heldOnlyCount += count;

        var heldOnlyTopTypes = heldOnlyByType.Count == 0
            ? (IReadOnlyList<NameCountEntry>?)null
            : heldOnlyByType.OrderByDescending(t => t.Count).Select(t => new NameCountEntry(t.TypeName, t.Count)).ToList();

        return new WeakReferenceDomainResult(
            TotalWeakHandles: total,
            AliveWeakTargets: alive,
            DeadWeakTargets: dead,
            DeadTargetRatio: total == 0 ? 0.0 : (double)dead / total,
            WeakHandleKinds: [],
            WeakReferenceObjectCount: total,
            WeakReferenceObjectBytes: (ulong)(total * 64),
            StaleWrapperCount: staleWrapperCount,
            TopWeakTargetTypes: [],
            TopStaleWrapperHolderTypes: [],
            DependentHandleDeadKeyCount: dependentDeadKeyCount,
            PhaseBFallbackUsed: false,
            PhaseBSkipped: false,
            HeldOnlyViaWeakReferenceCount: heldOnlyCount,
            HeldOnlyViaWeakReferenceTopTypes: heldOnlyTopTypes,
            HeldOnlyViaWeakReferenceDetectionAvailable: heldOnlyDetectionAvailable,
            StaleWrapperCountIsExact: true);
    }
}
