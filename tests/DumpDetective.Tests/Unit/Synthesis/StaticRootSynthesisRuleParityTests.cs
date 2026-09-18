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
/// Field-for-field parity between <see cref="StaticRootFindingGenerator"/> (old) and
/// <see cref="StaticRootSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Deliberately does not exercise <c>StaticRootAnalyzer</c>'s own known bug (it never actually
/// emits <c>heap.static-root</c>/<c>static-root.population</c> on a real dump) — these fixtures
/// stand in for the observations that would exist once the upstream fix lands, same as the
/// analyzer's own retyping did for its domain-result fixtures.
/// </remarks>
public sealed class StaticRootSynthesisRuleParityTests
{
    private readonly StaticRootFindingGenerator _oldGenerator = new();
    private readonly StaticRootSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoRoots_EmitsInfoFinding() => AssertParity(BuildResult(rootCount: 0));

    [Fact]
    public Task RootCountAtOrAboveTen_EmitsCriticalFinding() => AssertParity(BuildResult(rootCount: 15));

    [Fact]
    public Task TopRootExceedsCriticalByteThreshold_EmitsCriticalFinding() => AssertParity(BuildResult(
        rootCount: 3,
        topRoots: [new StaticRootSnapshot(RootDescription: "MyApp.Cache.Instance", TotalMemoryImpact: 200 * 1024 * 1024UL, ObjectsKeptAlive: 5000)]));

    [Fact]
    public Task BelowRootCountAndByteThresholds_EmitsWarningFinding() => AssertParity(BuildResult(
        rootCount: 3,
        topRoots: [new StaticRootSnapshot(RootDescription: "MyApp.Cache.Instance", TotalMemoryImpact: 1024, ObjectsKeptAlive: 10)]));

    /// <summary>
    /// Unlike <c>TopRootsByRetainedBytes</c> (always pre-sorted descending before the old generator
    /// ever reads it), the real analyzer emits <c>heap.static-root</c> inline during root
    /// enumeration — an arbitrary, not size-sorted, order. This exercises that the rule's max-scan
    /// over matched observations (not an index-0 trust) reconstructs the same "largest root's
    /// memory impact" the old generator's sorted-list index-0 read gives, even when observation
    /// order and the sorted-list order disagree.
    /// </summary>
    [Fact]
    public Task LargestRootObservedOutOfOrder_StillEmitsCriticalFinding()
    {
        StaticRootSnapshot small = new(RootDescription: "MyApp.Small", TotalMemoryImpact: 1024, ObjectsKeptAlive: 10);
        StaticRootSnapshot huge = new(RootDescription: "MyApp.Huge", TotalMemoryImpact: 200 * 1024 * 1024UL, ObjectsKeptAlive: 5000);

        // Sorted descending, matching what TopRootsByRetainedBytes always looks like in production.
        StaticRootDomainResult result = BuildResult(rootCount: 3, topRoots: [huge, small]);

        // Reversed relative to the sorted list — models heap.static-root's real (non-size-sorted)
        // emission order.
        return AssertParity(result, observationOrder: [small, huge]);
    }

    private static StaticRootDomainResult BuildResult(int rootCount, IReadOnlyList<StaticRootSnapshot>? topRoots = null) => new(
        RootCount: rootCount,
        TotalRetainedBytes: 1024,
        TopRootsByRetainedBytes: topRoots ?? []);

    private async Task AssertParity(StaticRootDomainResult result, IReadOnlyList<StaticRootSnapshot>? observationOrder = null)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result, observationOrder ?? result.TopRootsByRetainedBytes ?? []);
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

    private static IReadOnlyList<Observation> ToObservations(StaticRootDomainResult result, IReadOnlyList<StaticRootSnapshot> roots)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapRoots, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments };
        var observations = new List<Observation>();

        foreach (StaticRootSnapshot root in roots)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.static-root",
                Subjects = [new TypeRef { CanonicalName = root.TypeName.Length > 0 ? root.TypeName : root.RootDescription, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["retained.bytes"] = new Measure(root.TotalMemoryImpact, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["objects-kept-alive.count"] = new Measure(root.ObjectsKeptAlive, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2-or-loh-retained.fraction"] = new Measure(root.Gen2OrLohRetainedFraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "static-root.population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["root.count"] = new Measure(result.RootCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["retained.bytes"] = new Measure(result.TotalRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
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
        AnalyzerKey = "Static Root Leak Detection",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
