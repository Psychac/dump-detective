using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Mirrors the three scenarios <c>InsightEngineTests</c> covered before phase 5 step 6.2
/// (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) migrated
/// <c>DetectReferenceChainDominatorCorrelation</c> out of <c>InsightEngine</c> — minus the evidence
/// table, dropped deliberately (see the rule's own remarks).
/// </summary>
public sealed class ReferenceChainDominatorCorrelationInsightRuleTests
{
    private readonly ReferenceChainDominatorCorrelationInsightRule _rule = new();

    [Fact]
    public async Task BelowHeapFractionThreshold_ReturnsEmpty()
    {
        var matched = new ObservationMatchSet(
        [
            RetentionPopulation(totalHeapBytes: 1_000_000),
            TypeSample("App.SmallCache", retainedBytes: 5_000),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AboveWarningThreshold_EmitsWarning()
    {
        var matched = new ObservationMatchSet(
        [
            RetentionPopulation(totalHeapBytes: 1_000_000),
            TypeSample("App.BigCache", retainedBytes: 150_000),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().ContainSingle(f => f.Title == "Reference-chain type dominates a large heap fraction");
        Claim finding = findings[0];
        finding.Severity.Should().Be(Severity.Warning);
        finding.Narrative.Should().Contain("App.BigCache");
    }

    [Fact]
    public async Task AboveCriticalThreshold_EmitsCritical()
    {
        var matched = new ObservationMatchSet(
        [
            RetentionPopulation(totalHeapBytes: 1_000_000),
            TypeSample("App.HugeCache", retainedBytes: 300_000),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().ContainSingle(f => f.Title == "Reference-chain type dominates a large heap fraction")
            .Which.Severity.Should().Be(Severity.Critical);
    }

    [Fact]
    public async Task MultipleOffenders_AllCitedInDerivedFrom()
    {
        var matched = new ObservationMatchSet(
        [
            RetentionPopulation(totalHeapBytes: 1_000_000),
            TypeSample("App.BigCache", retainedBytes: 150_000),
            TypeSample("App.HugeCache", retainedBytes: 300_000),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().ContainSingle();
        // 1 retention-population observation + 2 offending type-sample observations.
        findings[0].DerivedFrom.Should().HaveCount(3);
        findings[0].Narrative.Should().Contain("App.HugeCache"); // the larger offender is named first
    }

    private static Observation RetentionPopulation(ulong totalHeapBytes) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "type.retention-population",
        Subjects = [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure> { ["total.bytes"] = new(totalHeapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute) },
        Provenance = FixedProvenance(),
        Confidence = 1.0,
    };

    private static Observation TypeSample(string typeName, ulong retainedBytes) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "reference-chain.type-sample",
        Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure> { ["retained.bytes"] = new(retainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute) },
        Provenance = FixedProvenance(),
        Confidence = 1.0,
    };

    private static Provenance FixedProvenance() => new()
    {
        Artifact = new ArtifactId("test-artifact"),
        AnalyzerKey = "TestAnalyzer",
        CapabilitiesUsed = new HashSet<Capability>(),
        Fidelity = FidelityLevel.Full,
    };
}
