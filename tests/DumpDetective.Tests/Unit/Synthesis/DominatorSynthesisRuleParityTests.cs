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
/// Field-for-field parity between <see cref="DominatorFindingGenerator"/> (old) and
/// <see cref="DominatorSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// The old generator's <c>ConfidenceScore</c> override on the highly-referenced-objects finding
/// (via <c>EvidenceConfidence.Compute</c>) is deliberately not reproduced — same choice
/// <c>TimerLeakSynthesisRule</c>'s own playbook entry made, since no Title/Narrative/Recommendation/
/// Tags/MetricValue/MetricUnit text reads it.
/// </remarks>
public sealed class DominatorSynthesisRuleParityTests
{
    private readonly DominatorFindingGenerator _oldGenerator = new();
    private readonly DominatorSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoTopTypes_ReturnsEmpty() => AssertParity(BuildResult());

    [Theory]
    [InlineData(80UL * 1024 * 1024)]
    [InlineData(100UL * 1024 * 1024)]
    [InlineData(700UL * 1024 * 1024)]
    public Task TopDominatorType_MapsSeverity_FromEstimatedRetainedBytes(ulong retainedBytes) =>
        AssertParity(BuildResult(topTypes: [Type("MyApp.BigGraph", retainedBytes, count: 12)]));

    [Fact]
    public Task TopDominatorType_UsesHeuristicEstimate_NotExactOverlay() => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", estimatedRetainedBytes: 50UL * 1024 * 1024, count: 12)],
        exactRetainedBytesByTypeName: new Dictionary<string, ulong> { ["MyApp.BigGraph"] = 900UL * 1024 * 1024 }));

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    public Task HighlyReferencedObjects_MapsSeverity_FromCount(int count) => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", 1UL, count: 1)],
        highlyReferencedObjectCount: count));

    [Fact]
    public Task HighlyReferencedObjects_ZeroCount_NoFinding() => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", 1UL, count: 1)],
        highlyReferencedObjectCount: 0));

    [Theory]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(9_999)]
    [InlineData(10_000)]
    public Task TypeHotspot_MapsThresholdAndSeverity_FromTotalIncomingReferences(long totalIncomingReferences) =>
        AssertParity(BuildResult(
            topTypes: [Type("MyApp.BigGraph", 1UL, count: 1)],
            retentionTypes: [RetentionType("MyApp.Hotspot", totalIncomingReferences, objectCount: 3, totalBytes: 4_096)]));

    [Fact]
    public Task TypeHotspot_PicksMaxByTotalIncomingReferences_NotFirstEntry() => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", 1UL, count: 1)],
        retentionTypes:
        [
            RetentionType("MyApp.FirstButSmaller", totalIncomingReferences: 1_500, objectCount: 1, totalBytes: 100),
            RetentionType("MyApp.SecondButLarger", totalIncomingReferences: 12_000, objectCount: 2, totalBytes: 200),
        ]));

    [Fact]
    public Task ApproximatedReferenceAddresses_GreaterThanZero_EmitsFinding() => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", 1UL, count: 1)],
        approximatedReferenceAddresses: 42));

    [Fact]
    public Task ApproximatedReferenceAddresses_Zero_NoFinding() => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", 1UL, count: 1)],
        approximatedReferenceAddresses: 0));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        topTypes: [Type("MyApp.BigGraph", 700UL * 1024 * 1024, count: 12)],
        highlyReferencedObjectCount: 15,
        retentionTypes: [RetentionType("MyApp.Hotspot", totalIncomingReferences: 12_000, objectCount: 5, totalBytes: 8_192)],
        approximatedReferenceAddresses: 99));

    private static TypeSnapshot Type(string typeName, ulong estimatedRetainedBytes, int count) => new(
        TypeName: typeName,
        Count: count,
        TotalBytes: 24_000_000,
        LohBytes: 0,
        AverageSize: 2_000_000,
        EstimatedRetainedBytes: estimatedRetainedBytes,
        SampleAddress: 0x1234UL,
        ModuleName: "MyApp.Core");

    private static RetentionTypeSnapshot RetentionType(string typeName, long totalIncomingReferences, int objectCount, ulong totalBytes) => new(
        TypeName: typeName,
        ObjectCount: objectCount,
        TotalBytes: totalBytes,
        TotalIncomingReferences: totalIncomingReferences,
        MaxIncomingReferences: (int)totalIncomingReferences,
        EstimatedRetainedBytes: totalBytes);

    private static DominatorDomainResult BuildResult(
        IReadOnlyList<TypeSnapshot>? topTypes = null,
        int highlyReferencedObjectCount = 0,
        IReadOnlyList<RetentionTypeSnapshot>? retentionTypes = null,
        long approximatedReferenceAddresses = 0,
        IReadOnlyDictionary<string, ulong>? exactRetainedBytesByTypeName = null)
    {
        topTypes ??= [];
        return new DominatorDomainResult(
            CandidateCount: topTypes.Count,
            AnalyzedCount: topTypes.Count,
            TotalEstimatedRetainedBytes: 0,
            TopDominatorTypes: topTypes,
            HighlyReferencedObjectCount: highlyReferencedObjectCount,
            ApproximatedReferenceAddresses: approximatedReferenceAddresses,
            TopRetentionTypes: retentionTypes ?? [],
            ExactRetainedBytesByTypeName: exactRetainedBytesByTypeName);
    }

    private async Task AssertParity(DominatorDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(DominatorDomainResult result)
    {
        if (result.TopDominatorTypes.Count == 0) return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapReferences, CapabilityVocabulary.HeapTypes };
        var observations = new List<Observation>();

        foreach (TypeSnapshot snapshot in result.TopDominatorTypes)
        {
            bool isExact = result.ExactRetainedBytesByTypeName is not null
                && result.ExactRetainedBytesByTypeName.TryGetValue(snapshot.TypeName, out ulong exactRetainedBytes);
            ulong retainedBytes = isExact ? result.ExactRetainedBytesByTypeName![snapshot.TypeName] : snapshot.EstimatedRetainedBytes;

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.retention",
                Subjects = [new TypeRef { CanonicalName = snapshot.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(snapshot.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(snapshot.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["retained.bytes"] = new Measure(retainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["estimated-retained.bytes"] = new Measure(snapshot.EstimatedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(snapshot.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = isExact ? 1.0 : 0.7,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "type.retention-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["highly-referenced.count"] = new Measure(result.HighlyReferencedObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["approximated-reference-addresses.count"] = new Measure(result.ApproximatedReferenceAddresses, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(capabilitiesUsed),
            Confidence = 1.0,
        });

        foreach (RetentionTypeSnapshot type in result.TopRetentionTypes ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.retention-hotspot",
                Subjects = [new TypeRef { CanonicalName = type.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["object.count"] = new Measure(type.ObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total.bytes"] = new Measure(type.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["total-incoming-references.count"] = new Measure(type.TotalIncomingReferences, MeasureUnit.Count, MeasureSemantics.Absolute),
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
        AnalyzerKey = "Dominator Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
