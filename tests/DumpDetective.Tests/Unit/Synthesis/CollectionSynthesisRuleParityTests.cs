using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
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
/// Field-for-field parity between <see cref="CollectionFindingGenerator"/> (old) and
/// <see cref="CollectionSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>CollectionAnalyzer.EmitObservationsAsync</c>: one whole-run
/// <c>collection.waste-summary</c>, one <c>collection.kind-waste</c> per <c>WasteCountsByKind</c>
/// entry, and one <c>collection.element-type-waste</c> per <c>WasteCountsByElementType</c> entry.
/// </remarks>
public sealed class CollectionSynthesisRuleParityTests
{
    private readonly CollectionFindingGenerator _oldGenerator = new();
    private readonly CollectionSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task TotalWastedMemoryAtOrAboveFiftyMb_EmitsWarningFinding() => AssertParity(BuildResult(
        countsByKind: null, bytesByKind: null, totalWastedMemory: 60 * 1024 * 1024UL));

    [Fact]
    public Task TotalWastedMemoryBelowFiftyMb_EmitsInfoFinding() => AssertParity(BuildResult(
        countsByKind: null, bytesByKind: null, totalWastedMemory: 1_000_000UL));

    [Fact]
    public Task Evidence_ListsKindsByWastedBytesDescending() => AssertParity(BuildResult(
        countsByKind: new() { [CollectionKind.Dictionary] = 5, [CollectionKind.Queue] = 1 },
        bytesByKind: new() { [CollectionKind.Dictionary] = 2_000, [CollectionKind.Queue] = 8_000 }));

    [Fact]
    public Task Recommendation_TargetsKindDominatingWastedBytes() => AssertParity(BuildResult(
        countsByKind: new() { [CollectionKind.List] = 50, [CollectionKind.Queue] = 1 },
        bytesByKind: new() { [CollectionKind.List] = 1_000, [CollectionKind.Queue] = 9_000 }));

    [Fact]
    public Task NoPerKindData_OmitsBreakdown() => AssertParity(BuildResult(null, null));

    [Fact]
    public Task Evidence_NamesElementTypeDominatingWastedBytes() => AssertParity(BuildResult(
        countsByKind: null,
        bytesByKind: null,
        countsByElementType: new() { ["System.String"] = 2, ["System.Int32"] = 50 },
        bytesByElementType: new() { ["System.String"] = 9_000, ["System.Int32"] = 1_000 }));

    [Fact]
    public Task NoElementTypeData_OmitsDominantElementTypeNote() => AssertParity(BuildResult(null, null));

    private static CollectionDomainResult BuildResult(
        Dictionary<CollectionKind, int>? countsByKind,
        Dictionary<CollectionKind, ulong>? bytesByKind,
        Dictionary<string, int>? countsByElementType = null,
        Dictionary<string, ulong>? bytesByElementType = null,
        ulong totalWastedMemory = 10_000) =>
        new(
            TotalCollections: 100,
            Dictionaries: 20,
            Lists: 20,
            ArrayLists: 0,
            Stacks: 0,
            SortedLists: 0,
            SortedSets: 0,
            HashSets: 0,
            Queues: 10,
            TotalWastedMemory: totalWastedMemory,
            WastefulCollectionCount: 6,
            WasteCountsByKind: countsByKind,
            WasteBytesByKind: bytesByKind,
            WasteCountsByElementType: countsByElementType,
            WasteBytesByElementType: bytesByElementType);

    private async Task AssertParity(CollectionDomainResult result)
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

    // ── Mirrors CollectionAnalyzer.EmitObservationsAsync ─────────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(CollectionDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.DumpObjectFields };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "collection.waste-summary",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total-collection.count"] = new Measure(result.TotalCollections, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total-wasted.bytes"] = new Measure(result.TotalWastedMemory, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["wasteful-collection.count"] = new Measure(result.WastefulCollectionCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        foreach (KeyValuePair<CollectionKind, int> kv in result.WasteCountsByKind ?? new Dictionary<CollectionKind, int>())
        {
            (result.WasteBytesByKind ?? new Dictionary<CollectionKind, ulong>()).TryGetValue(kv.Key, out ulong bytes);
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "collection.kind-waste",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["waste.count"] = new Measure(kv.Value, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["waste.bytes"] = new Measure(bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Evidence = [new EvidenceRef { Artifact = TestArtifact, Locator = "kind", Description = kv.Key.ToString() }],
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (KeyValuePair<string, int> kv in result.WasteCountsByElementType ?? new Dictionary<string, int>())
        {
            (result.WasteBytesByElementType ?? new Dictionary<string, ulong>()).TryGetValue(kv.Key, out ulong bytes);
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "collection.element-type-waste",
                Subjects = [new TypeRef { CanonicalName = kv.Key, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["waste.count"] = new Measure(kv.Value, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["waste.bytes"] = new Measure(bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Collection Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
