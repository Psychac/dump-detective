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
/// Field-for-field parity between <see cref="ObjectShapeFindingGenerator"/> (old) and
/// <see cref="ObjectShapeSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ToObservations</c> mirrors <c>ObjectShapeAnalyzer.EmitObservationAsync</c>: one whole-run
/// <c>type.shape-composition</c> (carrying the exact reference-heavy/value-heavy population
/// totals, since the per-type observations below are capped to a display-only top 3/top 1), up to
/// 3 <c>type.reference-heavy</c>, at most 1 <c>type.value-heavy</c>, and every
/// <c>type.finalizable-reference-heavy</c> match (filtered, not capped).
/// </remarks>
public sealed class ObjectShapeSynthesisRuleParityTests
{
    private readonly ObjectShapeFindingGenerator _oldGenerator = new();
    private readonly ObjectShapeSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    private static TypeShapeProfile RefHeavyType(string name = "Demo.Widget", ulong instanceCount = 1_000, bool isFinalizable = false) =>
        new(name, TotalFields: 10, ReferenceFields: 9, ValueFields: 1, ReferenceFieldRatio: 0.9,
            InstanceCount: instanceCount, TotalSize: 100_000, IsFinalizable: isFinalizable, IsValueType: false, IsArray: false,
            BaseTypeChainDepth: 1, InterfaceCount: 0, Category: ObjectShapeCategory.ReferenceHeavy);

    private static ObjectShapeAnalyzerDomainResult BuildResult(
        double avgRefFieldsPerType,
        long totalGcScanWork,
        IReadOnlyList<TypeShapeProfile>? topReferenceHeavyTypes = null,
        IReadOnlyList<TypeShapeProfile>? topValueHeavyTypes = null) =>
        new(
            TopReferenceHeavyTypes: topReferenceHeavyTypes ?? [RefHeavyType()],
            TopValueHeavyTypes: topValueHeavyTypes ?? [],
            TopBalancedTypes: [],
            TotalTypesAnalyzed: 5,
            AvgRefFieldsPerType: avgRefFieldsPerType,
            TotalGcScanWork: totalGcScanWork,
            TopGen2RetainedTypes: [],
            TotalGen2GcScanWork: 0);

    [Fact]
    public Task LowAvgRefFields_NoDensityFinding() => AssertParity(BuildResult(avgRefFieldsPerType: 2.0, totalGcScanWork: 1_000));

    [Fact]
    public Task ModerateAvgRefFields_DensityFindingIsInfo() => AssertParity(BuildResult(avgRefFieldsPerType: 5.0, totalGcScanWork: 1_000));

    [Fact]
    public Task HighAvgRefFields_DensityFindingIsWarning() => AssertParity(BuildResult(avgRefFieldsPerType: 9.0, totalGcScanWork: 1_000));

    [Fact]
    public Task TotalGcScanWorkExceedsCriticalThreshold_DensityFindingIsCritical() => AssertParity(
        BuildResult(avgRefFieldsPerType: 5.0, totalGcScanWork: 300_000_000L));

    [Fact]
    public Task TotalGcScanWorkBelowCriticalThreshold_HighAvgRefFieldsStaysWarning() => AssertParity(
        BuildResult(avgRefFieldsPerType: 9.0, totalGcScanWork: 1_000));

    [Fact]
    public Task NoReferenceHeavyTypes_DensityFindingShowsNone() => AssertParity(
        BuildResult(avgRefFieldsPerType: 5.0, totalGcScanWork: 1_000, topReferenceHeavyTypes: []));

    [Fact]
    public Task MoreThanThreeReferenceHeavyTypes_EvidenceCountUsesFullTotalNotJustTop3() => AssertParity(
        BuildResult(avgRefFieldsPerType: 5.0, totalGcScanWork: 1_000, topReferenceHeavyTypes:
        [
            RefHeavyType("Demo.A"), RefHeavyType("Demo.B"), RefHeavyType("Demo.C"), RefHeavyType("Demo.D"), RefHeavyType("Demo.E"),
        ]));

    [Fact]
    public Task ValueHeavyTypeAboveTenThousandInstances_EmitsInfoFinding() => AssertParity(
        BuildResult(avgRefFieldsPerType: 1.0, totalGcScanWork: 0, topReferenceHeavyTypes: [], topValueHeavyTypes:
        [
            new TypeShapeProfile("Demo.Coord", TotalFields: 3, ReferenceFields: 0, ValueFields: 3, ReferenceFieldRatio: 0.0,
                InstanceCount: 20_000, TotalSize: 500_000, IsFinalizable: false, IsValueType: true, IsArray: false,
                BaseTypeChainDepth: 1, InterfaceCount: 0, Category: ObjectShapeCategory.ValueHeavy),
        ]));

    [Fact]
    public Task ValueHeavyTypeBelowTenThousandInstances_NoValueHeavyFinding() => AssertParity(
        BuildResult(avgRefFieldsPerType: 1.0, totalGcScanWork: 0, topReferenceHeavyTypes: [], topValueHeavyTypes:
        [
            new TypeShapeProfile("Demo.Coord", TotalFields: 3, ReferenceFields: 0, ValueFields: 3, ReferenceFieldRatio: 0.0,
                InstanceCount: 500, TotalSize: 500_000, IsFinalizable: false, IsValueType: true, IsArray: false,
                BaseTypeChainDepth: 1, InterfaceCount: 0, Category: ObjectShapeCategory.ValueHeavy),
        ]));

    [Fact]
    public Task FinalizableReferenceHeavyTypeAboveTenThousandInstances_EmitsWarningFinding() => AssertParity(
        BuildResult(avgRefFieldsPerType: 1.0, totalGcScanWork: 0, topReferenceHeavyTypes:
        [
            RefHeavyType("Demo.FinalizerHeavy", instanceCount: 15_000, isFinalizable: true),
        ]));

    [Fact]
    public Task FinalizableReferenceHeavyTypeBelowTenThousandInstances_NoFinalizableFinding() => AssertParity(
        BuildResult(avgRefFieldsPerType: 1.0, totalGcScanWork: 0, topReferenceHeavyTypes:
        [
            RefHeavyType("Demo.FinalizerLight", instanceCount: 500, isFinalizable: true),
        ]));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(new ObjectShapeAnalyzerDomainResult(
        TopReferenceHeavyTypes:
        [
            RefHeavyType("Demo.FinalizerHeavy", instanceCount: 15_000, isFinalizable: true),
            RefHeavyType("Demo.A"),
            RefHeavyType("Demo.B"),
            RefHeavyType("Demo.C"),
        ],
        TopValueHeavyTypes:
        [
            new TypeShapeProfile("Demo.Coord", TotalFields: 3, ReferenceFields: 0, ValueFields: 3, ReferenceFieldRatio: 0.0,
                InstanceCount: 20_000, TotalSize: 500_000, IsFinalizable: false, IsValueType: true, IsArray: false,
                BaseTypeChainDepth: 1, InterfaceCount: 0, Category: ObjectShapeCategory.ValueHeavy),
        ],
        TopBalancedTypes: [],
        TotalTypesAnalyzed: 10,
        AvgRefFieldsPerType: 9.0,
        TotalGcScanWork: 300_000_000L,
        TopGen2RetainedTypes: [],
        TotalGen2GcScanWork: 0));

    private async Task AssertParity(ObjectShapeAnalyzerDomainResult result)
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

    // ── Mirrors ObjectShapeAnalyzer.EmitObservationAsync ─────────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(ObjectShapeAnalyzerDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.shape-composition",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["type.count"] = new Measure(result.TotalTypesAnalyzed, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["avg-reference-fields"] = new Measure(result.AvgRefFieldsPerType, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gc-scan-work"] = new Measure(result.TotalGcScanWork, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2-gc-scan-work"] = new Measure(result.TotalGen2GcScanWork, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["reference-heavy-type.count"] = new Measure(result.TopReferenceHeavyTypes.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["value-heavy-type.count"] = new Measure(result.TopValueHeavyTypes.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        foreach (TypeShapeProfile profile in result.TopReferenceHeavyTypes.Take(3))
            observations.Add(ToTypeShapeObservation("type.reference-heavy", profile, capabilitiesUsed));

        if (result.TopValueHeavyTypes.Count > 0)
            observations.Add(ToTypeShapeObservation("type.value-heavy", result.TopValueHeavyTypes[0], capabilitiesUsed));

        foreach (TypeShapeProfile profile in result.TopReferenceHeavyTypes)
        {
            if (profile.IsFinalizable && profile.InstanceCount >= 10_000)
                observations.Add(ToTypeShapeObservation("type.finalizable-reference-heavy", profile, capabilitiesUsed));
        }

        return observations;
    }

    private static Observation ToTypeShapeObservation(string observationType, TypeShapeProfile profile, HashSet<Capability> capabilitiesUsed) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = [new TypeRef { CanonicalName = profile.TypeName, Fidelity = MatchFidelity.Exact }],
        When = Point(),
        Measures = new Dictionary<string, Measure>
        {
            ["reference-field.count"] = new Measure(profile.ReferenceFields, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["total-field.count"] = new Measure(profile.TotalFields, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["instance.count"] = new Measure(profile.InstanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
        },
        Provenance = Provenance(capabilitiesUsed),
        Confidence = 1.0,
    };

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Object Shape Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
