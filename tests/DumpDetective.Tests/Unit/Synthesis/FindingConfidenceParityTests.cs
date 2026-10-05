using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

using Severity = DumpDetective.Sdk.Synthesis.Severity;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Phase 5 step 7's parity half (docs/refactor/modularity/phase-5-observations-synthesis.md): the
/// report-visible confidence of every rule-produced finding must match what the pre-Phase-5
/// generators reported. Expected values are written as the retired generators' own formulas
/// (<c>EvidenceConfidence.Compute</c>, <c>CrashFindingGenerator.ComputeCandidateConfidence</c>), so
/// exact double equality is the assertion, not an approximation.
/// </summary>
public sealed class FindingConfidenceParityTests
{
    private static readonly ArtifactId Artifact = new("finding-confidence-parity-test");

    [Fact]
    public void Adapter_CertainBreakdown_FallsBackToSeverityDefault()
    {
        InsightFinding critical = FindingSynthesisAdapter.ToInsightFinding(MakeFinding(Severity.Critical, ConfidenceBreakdown.MeasurementCertain));
        InsightFinding warning = FindingSynthesisAdapter.ToInsightFinding(MakeFinding(Severity.Warning, ConfidenceBreakdown.MeasurementCertain));
        InsightFinding info = FindingSynthesisAdapter.ToInsightFinding(MakeFinding(Severity.Info, ConfidenceBreakdown.MeasurementCertain));

        critical.ConfidenceScore.Should().Be(0.9);
        warning.ConfidenceScore.Should().Be(0.7);
        info.ConfidenceScore.Should().Be(0.5);
    }

    [Fact]
    public void Adapter_LimitedBreakdown_PassesCompositeThrough()
    {
        ConfidenceBreakdown limited = ConfidenceBreakdown.MeasurementCertain with { Composite = 0.62, EvidenceStrength = 0.62 };

        InsightFinding finding = FindingSynthesisAdapter.ToInsightFinding(MakeFinding(Severity.Critical, limited));

        finding.ConfidenceScore.Should().Be(0.62);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 5)]
    [InlineData(true, true, 0)]
    public void RootPathEvidenceStrength_MatchesRetiredEvidenceConfidence(bool hasRootPath, bool searchTruncated, int signalCount)
    {
        var evidence = new Evidence(0, hasRootPath ? "static -> Owner -> Target" : null, searchTruncated, MakeSignals(signalCount));
        var measures = new Dictionary<string, Measure>();
        RootPathEvidenceMeasures.Add(measures, "top.", evidence);

        ConfidenceBreakdown confidence = RootPathEvidenceStrength.FromMeasures(measures, "top.");

        confidence.Composite.Should().Be(RetiredEvidenceConfidence(evidence));
        confidence.LimitingFactors.Should().ContainSingle();
    }

    [Fact]
    public void RootPathEvidenceMeasures_NoEvidence_ScoresAsNoRootPath()
    {
        var measures = new Dictionary<string, Measure>();
        RootPathEvidenceMeasures.Add(measures, "top.", (Evidence?)null);

        RootPathEvidenceStrength.FromMeasures(measures, "top.").Composite.Should().Be(0.4);
    }

    [Fact]
    public async Task TimerLeakRule_LogicalTimersFindingUsesTopTypeRootPath_QueuePressureStaysSeverityDefault()
    {
        var evidence = new Evidence(0, "static -> TimerQueue -> TimerQueueTimer", RootPathSearchTruncated: true, MakeSignals(1));
        var populationMeasures = new Dictionary<string, Measure>();
        RootPathEvidenceMeasures.Add(populationMeasures, "top-type.", evidence);

        IReadOnlyList<Claim> findings = await new TimerLeakSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("infra.timer", "Timer Leak Analysis", new() { ["instance.count"] = Count(300) }, "System.Threading.TimerQueueTimer"),
                MakeObservation("infra.timer-population", "Timer Leak Analysis", populationMeasures),
            ]),
            new SynthesisContext());

        findings.Should().HaveCount(2);
        findings.Single(f => f.Fingerprint.EndsWith("logical-timers", StringComparison.Ordinal))
            .Confidence.Composite.Should().Be(RetiredEvidenceConfidence(evidence));
        findings.Single(f => f.Fingerprint.EndsWith("queue-pressure", StringComparison.Ordinal))
            .Confidence.Should().Be(ConfidenceBreakdown.MeasurementCertain);
    }

    [Fact]
    public async Task EventLeakRule_EachAggregateFindingUsesItsOwnKindsTopPublisher()
    {
        var staticEvidence = new EventLeakEvidence(1, "static -> Publisher", null, SearchTruncated: false, MakeSignals(2));
        var populationMeasures = new Dictionary<string, Measure>
        {
            ["total.count"] = Count(2),
            ["instance-leak.count"] = Count(1),
            ["static-leak.count"] = Count(1),
        };
        RootPathEvidenceMeasures.Add(populationMeasures, "top-static-publisher.", staticEvidence);
        RootPathEvidenceMeasures.Add(populationMeasures, "top-instance-publisher.", (EventLeakEvidence?)null);

        IReadOnlyList<Claim> findings = await new EventLeakSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("event.publisher-leak-population", "Event Leak Analysis", populationMeasures),
                MakeLeakGroup(isStatic: true),
                MakeLeakGroup(isStatic: false),
            ]),
            new SynthesisContext());

        findings.Single(f => f.Fingerprint.EndsWith("static-retention", StringComparison.Ordinal))
            .Confidence.Composite.Should().Be(0.8 + Math.Min(2, 3) * 0.02);
        findings.Single(f => f.Fingerprint.EndsWith("instance-retention", StringComparison.Ordinal))
            .Confidence.Composite.Should().Be(0.4);
    }

    [Fact]
    public async Task CrashRule_ActiveExceptionsFinding_WeightsTiersByActiveExceptionCount_AndSummarizesThem()
    {
        IReadOnlyList<Claim> findings = await new CrashSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeCrashPopulation(total: 5, active: 4),
                MakeCrashCandidate(activeExceptions: 3, tier: "Exact"),
                MakeCrashCandidate(activeExceptions: 0, tier: "ThreadId"),
                MakeCrashCandidate(activeExceptions: 1, tier: "Exact"),
            ]),
            new SynthesisContext());

        Claim finding = findings.Should().ContainSingle().Subject;
        double weightedScore = 0.95 * 3 + 0.65 * 1 + 0.95 * 1;
        finding.Confidence.Composite.Should().Be(weightedScore / 5);
        finding.Caveats.Should().Equal(
            "Active exception count is based on measured runtime objects and thread snapshots.",
            "Original stack trace confidence: 2 Exact, 1 ThreadId.");
    }

    [Fact]
    public async Task CrashRule_ActiveExceptionsWithoutCandidates_ScoresNoCandidateStrength()
    {
        IReadOnlyList<Claim> findings = await new CrashSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([MakeCrashPopulation(total: 1, active: 1)]),
            new SynthesisContext());

        Claim finding = findings.Should().ContainSingle().Subject;
        finding.Confidence.Composite.Should().Be(0.5);
        finding.Caveats.Should().Equal("Active exception count is based on measured runtime objects and thread snapshots.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizableObjectRule_OnlyTheRetainedBytesFindingReflectsEstimatedRetainedSizes(bool retainedIsExact)
    {
        IReadOnlyList<Claim> findings = await new FinalizableObjectSynthesisRule().SynthesizeAsync(
            new ObservationMatchSet([
                MakeObservation("finalizer.queue-entry", "Finalizable Object Analysis", new()
                {
                    ["retained.bytes"] = new Measure(200_000_000, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(64, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["disposed-but-live"] = Count(0),
                    ["is-critical-finalizer"] = Count(0),
                    ["is-retained-exact"] = Count(retainedIsExact ? 1 : 0),
                }, "App.Resource"),
            ]),
            new SynthesisContext());

        Claim retained = findings.Single(f => f.Fingerprint.EndsWith("queue-retained-bytes", StringComparison.Ordinal));
        retained.Confidence.Composite.Should().Be(retainedIsExact ? 1.0 : 0.7);
        retained.Narrative.Should().Contain(retainedIsExact ? "(exact, from the dominator tree)" : "shallow size used instead");
    }

    private static double RetiredEvidenceConfidence(Evidence evidence)
    {
        if (string.IsNullOrEmpty(evidence.SampleRootPath))
            return 0.4;

        double score = evidence.RootPathSearchTruncated ? 0.6 : 0.8;
        score += Math.Min(evidence.ContributingSignals.Count, 3) * 0.02;
        return Math.Min(score, 0.9);
    }

    private static List<EvidenceSignal> MakeSignals(int count)
    {
        var signals = new List<EvidenceSignal>(count);
        for (int i = 0; i < count; i++)
            signals.Add(new EvidenceSignal($"Signal{i}", "test signal"));
        return signals;
    }

    private static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Absolute);

    private static Claim MakeFinding(Severity severity, ConfidenceBreakdown confidence) => new()
    {
        Fingerprint = "test::finding",
        Analyzer = "Test Analyzer",
        Category = "Test",
        Severity = severity,
        Title = "title",
        Narrative = "narrative",
        Recommendation = "recommendation",
        DerivedFrom = [ObservationId.NewId()],
        Confidence = confidence,
    };

    private static Observation MakeLeakGroup(bool isStatic) => MakeObservation(
        "event.publisher-leak-group",
        "Event Leak Analysis",
        new()
        {
            ["is-static"] = new Measure(isStatic ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
            ["instance.count"] = Count(1),
            ["subscriber.count"] = Count(50),
            ["max-subscribers-per-instance.count"] = Count(50),
            ["max-severity.score"] = new Measure(40, MeasureUnit.Custom, MeasureSemantics.Absolute),
            ["is-timer-event"] = new Measure(0, MeasureUnit.Custom, MeasureSemantics.Absolute),
            ["is-property-changed-event"] = new Measure(0, MeasureUnit.Custom, MeasureSemantics.Absolute),
        },
        "App.Publisher",
        [new EvidenceRef { Artifact = Artifact, Locator = "App.Publisher.Changed", Description = "Changed" }]);

    private static Observation MakeCrashPopulation(long total, long active) => MakeObservation(
        "runtime.crash-population",
        "Crash Analysis",
        new()
        {
            ["total.count"] = Count(total),
            ["active.count"] = Count(active),
            ["unique-types.count"] = Count(1),
        });

    private static Observation MakeCrashCandidate(long activeExceptions, string tier) => MakeObservation(
        "runtime.crash-thread-candidate",
        "Crash Analysis",
        new() { ["active-exception.count"] = Count(activeExceptions) },
        "System.InvalidOperationException",
        [new EvidenceRef { Artifact = Artifact, Locator = "original-stack-trace-confidence", Description = tier }]);

    private static Observation MakeObservation(
        string observationType,
        string analyzerKey,
        Dictionary<string, Measure> measures,
        string? subjectType = null,
        IReadOnlyList<EvidenceRef>? evidence = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = subjectType is null ? [] : [new TypeRef { CanonicalName = subjectType, Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = Artifact,
            AnalyzerKey = analyzerKey,
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
        Evidence = evidence ?? [],
    };
}
