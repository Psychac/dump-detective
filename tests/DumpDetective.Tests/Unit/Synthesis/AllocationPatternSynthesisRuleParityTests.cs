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
/// Field-for-field parity between <see cref="AllocationPatternFindingGenerator"/> (old) and
/// <see cref="AllocationPatternSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike the old finding generator's own unit tests (which hard-code
/// <c>Profile: AllocationProfile.Mixed</c> for every pressure fixture, including ones whose
/// Gen0/Gen2 percentages would never actually classify as Mixed in production), these fixtures set
/// <c>Profile</c> consistent with what <c>AllocationPatternAnalyzer.ClassifyProfile</c> would really
/// produce from the same Gen0CountPct/Gen2CountPct — the rule re-derives Profile from those same raw
/// percentages, so an inconsistent fixture would make the two sides disagree on text neither
/// implementation is actually wrong about. Same "rebuild fixtures consistent with real production
/// coupling" principle <c>AsyncStateMachineSynthesisRuleParityTests</c> used.
/// </remarks>
public sealed class AllocationPatternSynthesisRuleParityTests
{
    private readonly AllocationPatternFindingGenerator _oldGenerator = new();
    private readonly AllocationPatternSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");
    private const double DefaultTransientThreshold = 70.0;

    [Fact]
    public Task LowPressure_EmitsNoFindings() => AssertParity(BuildResult(gen0CountPct: 100.0, gen2CountPct: 0.0, lohSizePct: 0.0));

    [Fact]
    public Task ModeratePressure_EmitsInfoFinding() => AssertParity(BuildResult(gen0CountPct: 40.0, gen2CountPct: 30.0, lohSizePct: 10.0));

    [Fact]
    public Task HighPressure_EmitsWarningFinding() => AssertParity(BuildResult(gen0CountPct: 0.0, gen2CountPct: 60.0, lohSizePct: 30.0));

    [Fact]
    public Task CriticalPressure_EmitsCriticalFinding() => AssertParity(BuildResult(gen0CountPct: 0.0, gen2CountPct: 80.0, lohSizePct: 50.0));

    [Fact]
    public Task WithFinalizableTypes_IncludesFinalizableCountInEvidence() => AssertParity(BuildResult(
        gen0CountPct: 0.0, gen2CountPct: 80.0, lohSizePct: 50.0, finalizableTypeCount: 12, finalizableBytes: 4096));

    [Fact]
    public Task WithTopLongLivedType_IncludesTypeNameInEvidence() => AssertParity(BuildResult(
        gen0CountPct: 0.0, gen2CountPct: 80.0, lohSizePct: 50.0,
        topLongLivedTypes: [new TypeAllocationProfile("MyApp.Cache.Node", 0, 0, 500, 1.0, TypeProfile.Retained, 100_000, 1.0)]));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        gen0CountPct: 0.0, gen2CountPct: 80.0, lohSizePct: 50.0,
        finalizableTypeCount: 12, finalizableBytes: 4096,
        topLongLivedTypes: [new TypeAllocationProfile("MyApp.Cache.Node", 0, 0, 500, 1.0, TypeProfile.Retained, 100_000, 1.0)]));

    private static AllocationProfile ClassifyProfile(double gen0Pct, double gen2Pct, double transientThreshold)
    {
        if (gen0Pct > transientThreshold) return AllocationProfile.Transient;
        if (gen2Pct > 50.0) return AllocationProfile.Retained;
        if (gen0Pct > 50.0) return AllocationProfile.Steady;
        return AllocationProfile.Mixed;
    }

    private static AllocationPatternDomainResult BuildResult(
        double gen0CountPct,
        double gen2CountPct,
        double lohSizePct,
        int finalizableTypeCount = 0,
        ulong finalizableBytes = 0,
        IReadOnlyList<TypeAllocationProfile>? topLongLivedTypes = null)
    {
        AllocationProfile profile = ClassifyProfile(gen0CountPct, gen2CountPct, DefaultTransientThreshold);

        return new AllocationPatternDomainResult(
            Gen0CountPct: gen0CountPct, Gen1CountPct: 20, Gen2CountPct: gen2CountPct, LohCountPct: 10,
            Gen0SizePct: 30, Gen1SizePct: 20, Gen2SizePct: 40, LohSizePct: lohSizePct,
            TotalManagedBytes: 1_000_000_000,
            Gen0Bytes: 300_000_000, Gen1Bytes: 200_000_000, Gen2Bytes: 400_000_000, LohBytes: 100_000_000,
            Profile: profile,
            TopTransientTypes: [],
            TopShortishTypes: [],
            TopLongLivedTypes: topLongLivedTypes ?? [],
            TopHighGen1SurvivorTypes: [],
            FinalizableTypeCount: finalizableTypeCount,
            FinalizableBytes: finalizableBytes);
    }

    private async Task AssertParity(AllocationPatternDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(AllocationPatternDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };

        return
        [
            new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.allocation-composition",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["gen0.count-fraction"] = new Measure(result.Gen0CountPct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen1.count-fraction"] = new Measure(result.Gen1CountPct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen2.count-fraction"] = new Measure(result.Gen2CountPct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["loh.count-fraction"] = new Measure(result.LohCountPct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen0.size-fraction"] = new Measure(result.Gen0SizePct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen2.size-fraction"] = new Measure(result.Gen2SizePct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["loh.size-fraction"] = new Measure(result.LohSizePct / 100.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["finalizable.type-count"] = new Measure(result.FinalizableTypeCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["finalizable.bytes"] = new Measure(result.FinalizableBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["transient-classification.threshold"] = new Measure(DefaultTransientThreshold, MeasureUnit.Ratio, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = TestArtifact,
                    AnalyzerKey = "Allocation Pattern Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
                Evidence = result.TopLongLivedTypes is { Count: > 0 }
                    ? [new EvidenceRef { Artifact = TestArtifact, Locator = "top-long-lived-type", Description = result.TopLongLivedTypes[0].TypeName }]
                    : [],
            },
        ];
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };
}
