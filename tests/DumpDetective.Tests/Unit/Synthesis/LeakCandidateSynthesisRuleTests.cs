using DumpDetective.Reporting.SynthesisRules;
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
/// Mirrors the four scenarios <c>LeakCandidateFindingGeneratorTests</c> covered before phase 5 step
/// 6.1 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) retired that generator —
/// same evidence/recommendation text, now built from observations instead of a
/// <c>LeakCandidateDomainResult</c> directly.
/// </summary>
public sealed class LeakCandidateSynthesisRuleTests
{
    private readonly LeakCandidateSynthesisRule _rule = new();

    [Fact]
    public async Task SynthesizeAsync_NoCandidates_ReturnsEmpty()
    {
        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(new ObservationMatchSet([]), new SynthesisContext());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_MultipleCandidates_ReturnsSingleAggregateFinding()
    {
        var matched = new ObservationMatchSet(
        [
            CandidateType("A.Type", totalSizeBytes: 120_000_000, instanceCount: 1200, gen2Pct: 75.0, gen2Count: 900, isContainer: true),
            CandidateType("B.Type", totalSizeBytes: 50_000_000, instanceCount: 600, gen2Pct: 62.0, gen2Count: 400, isContainer: true),
            CandidateType("C.Type", totalSizeBytes: 20_000_000, instanceCount: 220, gen2Pct: 54.0, gen2Count: 120, isContainer: false),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().ContainSingle();
        Claim finding = findings[0];
        finding.Title.Should().Contain("cluster");
        finding.Narrative.Should().Contain("3 leak candidate(s)");
        finding.Narrative.Should().Contain("Top examples:");
        finding.Tags.Should().Contain("aggregate");
        finding.MetricUnit.Should().Be("bytes");
        finding.DerivedFrom.Should().HaveCount(3);
    }

    [Fact]
    public async Task SynthesizeAsync_CriticalCandidate_ReturnsSeparateFindingPerCandidate()
    {
        // gen2Pct > 80 (+30) + totalSize > 100MB (+20) + isContainer (+5) + referenceFieldRatio > 0.5 (+5)
        // + isStaticRooted (+10) + isPinned (+10) => 80, still short of Critical (>= 90); push over
        // with isDependent (+10) => 90.
        var matched = new ObservationMatchSet(
        [
            CandidateType(
                "A.Type", totalSizeBytes: 120_000_000, instanceCount: 1200, gen2Pct: 85.0, gen2Count: 1200,
                isContainer: true, referenceFieldRatio: 0.8, isStaticRooted: true),
            HandlePinnedTargetType("A.Type"),
            HandleDependentTargetType("A.Type"),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().ContainSingle();
        Claim finding = findings[0];
        finding.Title.Should().Contain("Critical leak candidate: A.Type");
        finding.Severity.Should().Be(Severity.Critical);
        finding.Tags.Should().Contain("critical");
        finding.DerivedFrom.Should().ContainSingle();
    }

    [Fact]
    public async Task SynthesizeAsync_TimerWrapperTypeOverThreshold_ClassifiesAsTimerLeak()
    {
        var matched = new ObservationMatchSet(
        [
            CandidateType("System.Threading.Timer", totalSizeBytes: 1_000_000, instanceCount: 300, gen2Pct: 10.0, gen2Count: 30),
            TimerPopulation(logicalTimerCount: 150),
        ]);

        IReadOnlyList<Claim> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

        findings.Should().ContainSingle();
        findings[0].Tags.Should().Contain("TimerLeak");
        findings[0].Recommendation.Should().Contain("timers");
    }

    private static Observation CandidateType(
        string typeName,
        ulong totalSizeBytes,
        long instanceCount,
        double gen2Pct,
        long gen2Count,
        bool isContainer = false,
        bool isFinalizable = false,
        double referenceFieldRatio = 0.0,
        bool isStaticRooted = false,
        bool isDelegate = false) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "leak.candidate-type",
        Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>
        {
            ["shallow.bytes"] = new Measure(totalSizeBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["instance.count"] = new Measure(instanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["gen2.percent"] = new Measure(gen2Pct, MeasureUnit.Percent, MeasureSemantics.Ratio),
            ["gen2.count"] = new Measure(gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["reference-field.ratio"] = new Measure(referenceFieldRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
            ["is-finalizable.flag"] = new Measure(isFinalizable ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["is-container.flag"] = new Measure(isContainer ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["is-static-rooted.flag"] = new Measure(isStaticRooted ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["is-delegate.flag"] = new Measure(isDelegate ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute),
        },
        Provenance = FixedProvenance("Leak Candidate Analysis"),
        Confidence = 1.0,
    };

    private static Observation HandlePinnedTargetType(string typeName) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "handle.pinned-target-type",
        Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure> { ["instance.count"] = new Measure(1, MeasureUnit.Count, MeasureSemantics.Absolute) },
        Provenance = FixedProvenance("GC Handle Analysis"),
        Confidence = 1.0,
    };

    private static Observation HandleDependentTargetType(string typeName) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "handle.dependent-target-type",
        Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure> { ["instance.count"] = new Measure(1, MeasureUnit.Count, MeasureSemantics.Absolute) },
        Provenance = FixedProvenance("GC Handle Analysis"),
        Confidence = 1.0,
    };

    private static Observation TimerPopulation(int logicalTimerCount) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "infra.timer-population",
        Subjects = [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>
        {
            ["timer-queue.count"] = new Measure(logicalTimerCount, MeasureUnit.Count, MeasureSemantics.Absolute),
        },
        Provenance = FixedProvenance("Timer Leak Analysis"),
        Confidence = 1.0,
    };

    private static Provenance FixedProvenance(string analyzerKey) => new()
    {
        Artifact = new ArtifactId("test-artifact"),
        AnalyzerKey = analyzerKey,
        CapabilitiesUsed = new HashSet<Capability>(),
        Fidelity = FidelityLevel.Full,
    };
}
