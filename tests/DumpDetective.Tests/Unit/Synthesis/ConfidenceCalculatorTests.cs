using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

public sealed class ConfidenceCalculatorTests
{
    private static readonly ConfidenceBreakdown RootPathSupported = ConfidenceBreakdown.MeasurementCertain with
    {
        Composite = 0.82,
        EvidenceStrength = 0.82,
        LimitingFactors = ["Retention is supported by one sampled GC root path."],
    };

    [Fact]
    public void Apply_EmptyLineage_ReturnsRuleConfidenceUnchanged()
    {
        ConfidenceCalculator.Apply(RootPathSupported, []).Should().BeSameAs(RootPathSupported);
    }

    [Fact]
    public void Apply_FullyConfidentSingleArtifactLineage_LeavesRuleConfidenceUntouched()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            RootPathSupported,
            [MakeObservation("Dominator Analysis"), MakeObservation("Static Root Leak Detection")]);

        result.Should().BeEquivalentTo(RootPathSupported);
    }

    [Fact]
    public void Apply_DegradedObservation_ScalesByCapabilityFidelity_NotEvidenceStrength()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [MakeObservation("Static Root Leak Detection", confidence: 0.5, fidelity: FidelityLevel.Degraded)]);

        result.Composite.Should().Be(0.5);
        result.CapabilityFidelity.Should().Be(0.5);
        result.EvidenceStrength.Should().Be(1.0);
        result.LimitingFactors.Should().ContainSingle().Which.Should().Contain("Static Root Leak Detection ran without an optional capability");
    }

    [Fact]
    public void Apply_SameSourceTwice_CountsItOnce_AtItsWeakestMeasurement()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [MakeObservation("Dominator Analysis", confidence: 0.7), MakeObservation("Dominator Analysis", confidence: 0.7)]);

        result.Composite.Should().BeApproximately(0.7, 1e-12);
        result.LimitingFactors.Should().ContainSingle();
    }

    [Fact]
    public void Apply_IndependentSources_CorroborateByNoisyOr()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [MakeObservation("Dominator Analysis", confidence: 0.7), MakeObservation("GC Root Analysis", confidence: 0.6)]);

        result.Composite.Should().BeApproximately(1 - (0.3 * 0.4), 1e-12);
        result.EvidenceStrength.Should().BeApproximately(1 - (0.3 * 0.4), 1e-12);
        result.LimitingFactors.Should().HaveCount(2);
    }

    [Fact]
    public void Apply_FullyConfidentSourceCorroboratesAWeakOne_AndNothingIsReportedAsLimiting()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [MakeObservation("Dominator Analysis", confidence: 0.7), MakeObservation("GC Root Analysis")]);

        result.Composite.Should().Be(1.0);
        result.LimitingFactors.Should().BeEmpty();
    }

    [Fact]
    public void Apply_SingleArtifact_IgnoresIdentityFidelityAndTimeAnchors()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [MakeObservation("Async State Machine Analysis", identity: MatchFidelity.Low, anchor: AnchorConfidence.Unknown)]);

        result.Composite.Should().Be(1.0);
        result.IdentityFidelityCap.Should().Be(1.0);
        result.TemporalAlignmentCap.Should().Be(1.0);
    }

    [Fact]
    public void Apply_AcrossArtifacts_CapsAtWeakestIdentityMatch()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [
                MakeObservation("Dominator Analysis", artifact: "dump", identity: MatchFidelity.Exact, anchor: AnchorConfidence.Exact),
                MakeObservation("Allocation Hotspots", artifact: "trace", identity: MatchFidelity.Low, anchor: AnchorConfidence.Exact),
            ]);

        result.IdentityFidelityCap.Should().Be(0.4);
        result.TemporalAlignmentCap.Should().Be(1.0);
        result.Composite.Should().Be(0.4);
        result.LimitingFactors.Should().ContainSingle().Which.Should().Contain("Low-fidelity identity match");
    }

    [Fact]
    public void Apply_AcrossArtifacts_CapsAtWeakestTimeAnchor()
    {
        ConfidenceBreakdown result = ConfidenceCalculator.Apply(
            ConfidenceBreakdown.MeasurementCertain,
            [
                MakeObservation("Dominator Analysis", artifact: "dump", anchor: AnchorConfidence.Unknown),
                MakeObservation("Allocation Hotspots", artifact: "trace", anchor: AnchorConfidence.Approximate),
            ]);

        result.TemporalAlignmentCap.Should().Be(0.4);
        result.Composite.Should().Be(0.4);
        result.LimitingFactors.Should().ContainSingle().Which.Should().Contain("Unknown time alignment");
    }

    [Fact]
    public async Task SynthesisEngine_AppliesLineageConfidenceToEveryRuleFinding()
    {
        Observation degraded = MakeObservation("Static Root Leak Detection", confidence: 0.5, fidelity: FidelityLevel.Degraded);
        var engine = new SynthesisEngine([new DerivedFromRule(degraded.Id, RootPathSupported)]);

        IReadOnlyList<Claim> findings = await engine.SynthesizeAsync([degraded], new SynthesisContext());

        Claim finding = findings.Should().ContainSingle().Subject;
        finding.Confidence.Composite.Should().Be(0.82 * 0.5);
        finding.Confidence.LimitingFactors.Should().HaveCount(2);
    }

    private static Observation MakeObservation(
        string analyzerKey,
        double confidence = 1.0,
        FidelityLevel fidelity = FidelityLevel.Full,
        string artifact = "dump",
        MatchFidelity identity = MatchFidelity.Exact,
        AnchorConfidence anchor = AnchorConfidence.Unknown) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "test.observation",
        Subjects = [new TypeRef { CanonicalName = "App.Retained", Fidelity = identity }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = anchor } },
        Measures = new Dictionary<string, Measure>(),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId(artifact),
            AnalyzerKey = analyzerKey,
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = fidelity,
        },
        Confidence = confidence,
    };

    private sealed class DerivedFromRule(ObservationId derivedFrom, ConfidenceBreakdown confidence) : ISynthesisRule
    {
        public string RuleId => "test.derived-from";
        public int Version => 1;

        public ObservationQuery Query { get; } = new() { ObservationTypes = ["test.observation"] };

        public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
            ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = "test::derived-from",
                Analyzer = "Test Analyzer",
                Category = "Test",
                Severity = Severity.Warning,
                Title = "test",
                Narrative = "test",
                Recommendation = "test",
                DerivedFrom = [derivedFrom],
                Confidence = confidence,
            }]);
    }
}
