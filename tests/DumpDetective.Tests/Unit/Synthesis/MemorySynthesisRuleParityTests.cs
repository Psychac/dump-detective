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
/// Field-for-field parity between <see cref="MemoryFindingGenerator"/> (old) and
/// <see cref="MemorySynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class MemorySynthesisRuleParityTests
{
    private readonly MemoryFindingGenerator _oldGenerator = new();
    private readonly MemorySynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task Top1BytesPercentAboveThreshold_EmitsDominantTypeFinding() => AssertParity(
        BuildResult(top1BytesPercent: 55.0, top5BytesPercent: 60.0));

    [Fact]
    public Task Top1BytesPercentAtThreshold_EmitsNoDominantTypeFinding() => AssertParity(
        BuildResult(top1BytesPercent: 40.0, top5BytesPercent: 50.0));

    [Fact]
    public Task MemoryPressureScoreAtOrAboveSeventy_EmitsWarningFinding() => AssertParity(
        BuildResult(lohPercent: 35.0, top5BytesPercent: 70.0, smallObjectCountPercent: 85.0, smallObjectBytesPercent: 45.0));

    [Fact]
    public Task MemoryPressureScoreBelowSeventy_EmitsNoPressureFinding() => AssertParity(
        BuildResult(lohPercent: 35.0, top5BytesPercent: 70.0));

    [Fact]
    public Task Top5BytesPercentAboveEighty_EmitsWarningFinding() => AssertParity(
        BuildResult(top1BytesPercent: 0, top5BytesPercent: 85.0));

    [Fact]
    public Task SmallObjectCountPercentAboveEightyFive_EmitsInfoFinding() => AssertParity(
        BuildResult(smallObjectCountPercent: 90.0));

    [Fact]
    public Task LohPercentAtOrAboveForty_EmitsWarningFinding() => AssertParity(
        BuildResult(lohPercent: 45.0));

    [Fact]
    public Task NoThresholdsCrossed_EmitsBaselineInfoSnapshot() => AssertParity(BuildResult());

    [Fact]
    public Task NoTopTypes_SuppressesDominantTypeFindingEvenAboveThreshold() => AssertParity(
        BuildResult(top1BytesPercent: 55.0, top5BytesPercent: 60.0, includeTopTypes: false));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        top1BytesPercent: 55.0, top5BytesPercent: 85.0, smallObjectCountPercent: 90.0, smallObjectBytesPercent: 45.0, lohPercent: 45.0));

    private static MemoryDomainResult BuildResult(
        double top1BytesPercent = 0,
        double top5BytesPercent = 0,
        double smallObjectCountPercent = 0,
        double smallObjectBytesPercent = 0,
        double lohPercent = 0,
        bool includeTopTypes = true)
    {
        return new MemoryDomainResult(
            TotalBytes: 1_000_000_000,
            LohBytes: (ulong)(1_000_000_000 * (lohPercent / 100.0)),
            LohPercent: lohPercent,
            TotalObjects: 100_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 1,
            TopTypes: includeTopTypes
                ? [new TypeSnapshot("MyApp.Cache.Entry", 10_000, (ulong)(1_000_000_000 * (top1BytesPercent / 100.0)), 0)]
                : [],
            Top1BytesPercent: top1BytesPercent,
            Top5BytesPercent: top5BytesPercent,
            SmallObjectCountPercent: smallObjectCountPercent,
            SmallObjectBytesPercent: smallObjectBytesPercent);
    }

    private async Task AssertParity(MemoryDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(MemoryDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapTypes, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapReferences,
        };

        var measures = new Dictionary<string, Measure>
        {
            ["total.bytes"] = new Measure(result.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["loh.bytes"] = new Measure(result.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["loh.percent"] = new Measure(result.LohPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["total.count"] = new Measure(result.TotalObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["loh.count"] = new Measure(result.LohObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["unique-types.count"] = new Measure(result.UniqueTypes, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["top1.percent"] = new Measure(result.Top1BytesPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["top5.percent"] = new Measure(result.Top5BytesPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["small-object.count-percent"] = new Measure(result.SmallObjectCountPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["small-object.bytes-percent"] = new Measure(result.SmallObjectBytesPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["objects-per-mb"] = new Measure(result.ObjectsPerMb, MeasureUnit.Custom, MeasureSemantics.Absolute),
        };

        IReadOnlyList<EvidenceRef> evidence = [];
        if (result.TopTypes.Count > 0)
        {
            measures["top1-type.bytes"] = new Measure(result.TopTypes[0].TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute);
            evidence = [new EvidenceRef { Artifact = TestArtifact, Locator = "top1-type", Description = result.TopTypes[0].TypeName }];
        }

        return
        [
            new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.memory-composition",
                Subjects = [],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = measures,
                Provenance = new Provenance
                {
                    Artifact = TestArtifact,
                    AnalyzerKey = "Memory Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
                Evidence = evidence,
            },
        ];
    }
}
