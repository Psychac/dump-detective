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
/// Field-for-field parity between <see cref="BoxingFindingGenerator"/> (old) and
/// <see cref="BoxingSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// The old generator's own tests set <c>TotalBoxedObjects</c>/<c>BoxedEnumCount</c>/
/// <c>NullableBoxedCount</c>/etc. as independent scalars, decoupled from <c>TopBoxedTypes</c> — real
/// production always derives them from the same per-type population (<c>Analyze</c>'s scan). Since
/// <c>BuildFromEntries</c> feeds the *same* <see cref="BoxingDomainResult"/> to both the old
/// generator and <c>ToObservations</c>, every fixture here builds the per-type entry lists first and
/// derives the aggregate scalars from them — the same "rebuild fixtures consistent with real
/// production coupling" fix <c>AllocationPatternSynthesisRuleParityTests</c> established.
/// </remarks>
public sealed class BoxingSynthesisRuleParityTests
{
    private readonly BoxingFindingGenerator _oldGenerator = new();
    private readonly BoxingSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task BoxedEnumCountAboveOneMillion_EmitsCriticalFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [EnumEntry("MyApp.Status", 2_000_000)]));

    [Fact]
    public Task BoxedEnumCountAboveFiftyThousand_EmitsWarningFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [EnumEntry("MyApp.Status", 60_000)]));

    [Fact]
    public Task BoxedEnumCountAboveOneThousand_EmitsInfoFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [EnumEntry("MyApp.Status", 5_000)]));

    [Fact]
    public Task NullableBoxedCountAboveTenThousand_EmitsWarningFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [NullableEntry(15_000)]));

    [Fact]
    public Task NullableBoxedCountAboveOneHundred_EmitsInfoFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [NullableEntry(500)]));

    [Fact]
    public Task StructPaddingWasteRatioAboveQuarter_EmitsInfoFinding() => AssertParity(BuildFromEntries(
        paddingTypes: [new StructPaddingEntry(TypeName: "MyApp.Point3D", TotalFieldBytes: 12, StructSize: 24, WastedPaddingBytes: 12, WasteRatio: 0.5)]));

    [Fact]
    public Task OversizedValueTypeCountAboveFiveHundredThousand_EmitsCriticalFinding() => AssertParity(BuildFromEntries(
        oversizedTypes: [new OversizedTypeEntry("MyApp.BigStruct", 128, 600_000)]));

    [Fact]
    public Task OversizedValueTypeCountAboveOneHundredThousand_EmitsWarningFinding() => AssertParity(BuildFromEntries(
        oversizedTypes: [new OversizedTypeEntry("MyApp.BigStruct", 128, 150_000)]));

    [Fact]
    public Task OversizedValueTypeCountAboveOneHundred_EmitsInfoFinding() => AssertParity(BuildFromEntries(
        oversizedTypes: [new OversizedTypeEntry("MyApp.BigStruct", 128, 500)]));

    [Fact]
    public Task Gen2FractionAboveEightyPercent_EmitsWarningFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [PlainEntry("MyApp.Foo", 2000, gen2Count: 1900, hasIEquatable: true)]));

    [Fact]
    public Task Gen2FractionBetweenFiftyAndEightyPercent_EmitsInfoFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [PlainEntry("MyApp.Foo", 2000, gen2Count: 1200, hasIEquatable: true)]));

    [Fact]
    public Task Gen2FractionAtOrBelowFiftyPercent_EmitsNoRetainedFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [PlainEntry("MyApp.Foo", 2000, gen2Count: 900, hasIEquatable: true)]));

    [Fact]
    public Task MissingEquatableInstancesAboveOneThousand_EmitsInfoFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [new BoxedTypeEntry("MyApp.Money", 2_000, 32_000, IsEnum: false, HasReferenceFields: false, Gen0Count: 1000, Gen2Count: 100, Gen2Fraction: 0.05, HasIEquatable: false)]));

    [Fact]
    public Task AllBoxedTypesHaveIEquatable_EmitsNoEqualityFinding() => AssertParity(BuildFromEntries(
        boxedTypes: [new BoxedTypeEntry("MyApp.Money", 2_000, 32_000, IsEnum: false, HasReferenceFields: false, Gen0Count: 1000, Gen2Count: 100, Gen2Fraction: 0.05, HasIEquatable: true)]));

    [Fact]
    public Task TotalBoxedObjectsAboveOneMillion_SummaryIsCritical() => AssertParity(BuildFromEntries(
        boxedTypes: [PlainEntry("MyApp.Foo", 1_500_000, gen2Count: 0, hasIEquatable: true)]));

    [Fact]
    public Task TotalBoxedObjectsAboveFiveHundredThousand_SummaryIsWarning() => AssertParity(BuildFromEntries(
        boxedTypes: [PlainEntry("MyApp.Foo", 600_000, gen2Count: 0, hasIEquatable: true)]));

    [Fact]
    public Task AllBelowThresholds_EmitsOnlyInfoSummaryFinding() => AssertParity(BuildFromEntries());

    [Fact]
    public Task AllZero_NoPerTypeObservationsAtAll_StillEmitsSummaryFinding() => AssertParity(
        new BoxingDomainResult(0, 0, [], 0, 0, 0, 0, 0, [], [], 0, 0.0, 0));

    private static BoxedTypeEntry EnumEntry(string name, int count) =>
        new(name, count, (ulong)count * 16, IsEnum: true, HasReferenceFields: false, Gen0Count: count, Gen2Count: 0, Gen2Fraction: 0.0, HasIEquatable: true);

    private static BoxedTypeEntry NullableEntry(int count) =>
        new($"System.Nullable<System.Int32>", count, (ulong)count * 16, IsEnum: false, HasReferenceFields: false, Gen0Count: count, Gen2Count: 0, Gen2Fraction: 0.0, HasIEquatable: true);

    private static BoxedTypeEntry PlainEntry(string name, int count, int gen2Count, bool hasIEquatable) =>
        new(name, count, (ulong)count * 16, IsEnum: false, HasReferenceFields: false, Gen0Count: count - gen2Count, Gen2Count: gen2Count,
            Gen2Fraction: count > 0 ? (double)gen2Count / count : 0.0, HasIEquatable: hasIEquatable);

    private static BoxingDomainResult BuildFromEntries(
        IReadOnlyList<BoxedTypeEntry>? boxedTypes = null,
        IReadOnlyList<OversizedTypeEntry>? oversizedTypes = null,
        IReadOnlyList<StructPaddingEntry>? paddingTypes = null)
    {
        boxedTypes ??= [];
        oversizedTypes ??= [];
        paddingTypes ??= [];

        var sortedBoxedTypes = boxedTypes.OrderByDescending(t => t.TotalBoxBytes).ToList();
        var sortedOversizedTypes = oversizedTypes.OrderByDescending(t => t.Count).ToList();
        var sortedPaddingTypes = paddingTypes.OrderByDescending(t => t.WastedPaddingBytes).ToList();

        long totalBoxedObjects = sortedBoxedTypes.Sum(t => (long)t.BoxCount);
        ulong totalBoxedBytes = sortedBoxedTypes.Aggregate(0UL, (acc, t) => acc + t.TotalBoxBytes);
        long boxedEnumCount = sortedBoxedTypes.Where(t => t.IsEnum).Sum(t => (long)t.BoxCount);
        ulong boxedEnumBytes = sortedBoxedTypes.Where(t => t.IsEnum).Aggregate(0UL, (acc, t) => acc + t.TotalBoxBytes);
        long nullableBoxedCount = sortedBoxedTypes.Where(t => t.ValueTypeName.StartsWith("System.Nullable<", StringComparison.Ordinal)).Sum(t => (long)t.BoxCount);
        ulong nullableBoxedBytes = sortedBoxedTypes.Where(t => t.ValueTypeName.StartsWith("System.Nullable<", StringComparison.Ordinal)).Aggregate(0UL, (acc, t) => acc + t.TotalBoxBytes);
        long totalGen2BoxedCount = sortedBoxedTypes.Sum(t => t.Gen2Count);
        long oversizedValueTypeInstanceCount = sortedOversizedTypes.Sum(t => (long)t.Count);

        return new BoxingDomainResult(
            TotalBoxedObjects: totalBoxedObjects,
            TotalBoxedBytes: totalBoxedBytes,
            TopBoxedTypes: sortedBoxedTypes,
            BoxedEnumCount: boxedEnumCount,
            BoxedEnumBytes: boxedEnumBytes,
            NullableBoxedCount: nullableBoxedCount,
            NullableBoxedBytes: nullableBoxedBytes,
            OversizedValueTypeInstanceCount: oversizedValueTypeInstanceCount,
            TopOversizedTypes: sortedOversizedTypes,
            TopPaddingWasteTypes: sortedPaddingTypes,
            AggregatePaddingWasteBytes: 0,
            AvgBoxedInstanceBytes: totalBoxedObjects > 0 ? (double)totalBoxedBytes / totalBoxedObjects : 0.0,
            TotalGen2BoxedCount: totalGen2BoxedCount);
    }

    private async Task AssertParity(BoxingDomainResult result)
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

    // ── Mirrors BoxingAnalyzer.EmitObservationsAsync ─────────────────────────────────────────────

    private static IReadOnlyList<Observation> ToObservations(BoxingDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.boxing-pressure",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total-boxed.count"] = new Measure(result.TotalBoxedObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total-boxed.bytes"] = new Measure(result.TotalBoxedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["boxed-enum.count"] = new Measure(result.BoxedEnumCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["boxed-enum.bytes"] = new Measure(result.BoxedEnumBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["nullable-boxed.count"] = new Measure(result.NullableBoxedCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["nullable-boxed.bytes"] = new Measure(result.NullableBoxedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["oversized-value.count"] = new Measure(result.OversizedValueTypeInstanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["padding-waste-type.count"] = new Measure(result.TopPaddingWasteTypes.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2-boxed.count"] = new Measure(result.TotalGen2BoxedCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        foreach (BoxedTypeEntry entry in result.TopBoxedTypes)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.boxing",
                Subjects = [new TypeRef { CanonicalName = entry.ValueTypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(entry.BoxCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(entry.TotalBoxBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen0.count"] = new Measure(entry.Gen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(entry.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2.fraction"] = new Measure(entry.Gen2Fraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["is-enum"] = new Measure(entry.IsEnum ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["has-reference-fields"] = new Measure(entry.HasReferenceFields ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["has-iequatable"] = new Measure(entry.HasIEquatable ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["is-nullable"] = new Measure(entry.ValueTypeName.StartsWith("System.Nullable<", StringComparison.Ordinal) ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (OversizedTypeEntry entry in result.TopOversizedTypes)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.oversized-value",
                Subjects = [new TypeRef { CanonicalName = entry.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["static-size.bytes"] = new Measure(entry.StaticSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["instance.count"] = new Measure(entry.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (StructPaddingEntry entry in result.TopPaddingWasteTypes)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.struct-padding-waste",
                Subjects = [new TypeRef { CanonicalName = entry.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["field.bytes"] = new Measure(entry.TotalFieldBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["struct-size.bytes"] = new Measure(entry.StructSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["wasted-padding.bytes"] = new Measure(entry.WastedPaddingBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["waste.ratio"] = new Measure(entry.WasteRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
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
        AnalyzerKey = "Boxing Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
