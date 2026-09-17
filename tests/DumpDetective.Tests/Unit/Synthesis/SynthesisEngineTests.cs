using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

public sealed class SynthesisEngineTests
{
    [Fact]
    public async Task SynthesizeAsync_OnlyInvokesRuleWithObservationsMatchingItsQuery()
    {
        Observation typeAObservation = MakeObservation("type.retention");
        Observation typeBObservation = MakeObservation("gc.pressure-composition");

        RecordingRule ruleForA = new("rule.a", ["type.retention"]);
        RecordingRule ruleForB = new("rule.b", ["gc.pressure-composition"]);

        SynthesisEngine engine = new([ruleForA, ruleForB]);

        await engine.SynthesizeAsync([typeAObservation, typeBObservation], new SynthesisContext());

        ruleForA.ReceivedMatches.Should().ContainSingle().Which.Should().Be(typeAObservation);
        ruleForB.ReceivedMatches.Should().ContainSingle().Which.Should().Be(typeBObservation);
    }

    [Fact]
    public async Task SynthesizeAsync_StillInvokesARuleWithZeroMatches()
    {
        RecordingRule rule = new("rule.no-match", ["some.type-nobody-emitted"]);
        SynthesisEngine engine = new([rule]);

        await engine.SynthesizeAsync([], new SynthesisContext());

        rule.WasInvoked.Should().BeTrue(
            "whether zero matches means \"nothing to report\" or an explicit no-issue finding " +
            "(ReportSystemVision.md § 7.2) is the rule's call, not the engine's");
    }

    [Fact]
    public async Task SynthesizeAsync_IsolatesAThrowingRuleWithoutLosingOtherRulesFindings()
    {
        ThrowingRule badRule = new("rule.throws");
        RecordingRule goodRule = new("rule.good", ["type.retention"], FixedFinding("z-finding"));

        SynthesisEngine engine = new([badRule, goodRule]);

        IReadOnlyList<Finding> findings = await engine.SynthesizeAsync(
            [MakeObservation("type.retention")], new SynthesisContext());

        findings.Should().ContainSingle(f => f.Fingerprint == "z-finding");
    }

    [Fact]
    public async Task SynthesizeAsync_SortsCombinedFindingsByFingerprintForDeterministicOutput()
    {
        RecordingRule ruleZ = new("rule.z", ["type.retention"], FixedFinding("z-finding"));
        RecordingRule ruleA = new("rule.a", ["type.retention"], FixedFinding("a-finding"));

        SynthesisEngine engine = new([ruleZ, ruleA]);

        IReadOnlyList<Finding> findings = await engine.SynthesizeAsync(
            [MakeObservation("type.retention")], new SynthesisContext());

        findings.Select(f => f.Fingerprint).Should().Equal("a-finding", "z-finding");
    }

    private static Observation MakeObservation(string observationType) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = [new TypeRef { CanonicalName = "System.Data.DataColumn", Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>(),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "TestAnalyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static Finding FixedFinding(string fingerprint) => new()
    {
        Fingerprint = fingerprint,
        Severity = Severity.Info,
        Title = "test",
        Narrative = "test",
        Recommendation = "test",
        DerivedFrom = [],
        Confidence = ConfidenceBreakdown.MeasurementCertain,
        Analyzer = "Test Analyzer",
        Category = "Test",
    };

    private sealed class RecordingRule : ISynthesisRule
    {
        private readonly Finding? _findingToReturn;

        public RecordingRule(string ruleId, IReadOnlyList<string> observationTypes, Finding? findingToReturn = null)
        {
            RuleId = ruleId;
            Query = new ObservationQuery { ObservationTypes = observationTypes };
            _findingToReturn = findingToReturn;
        }

        public string RuleId { get; }
        public ObservationQuery Query { get; }
        public bool WasInvoked { get; private set; }
        public IReadOnlyList<Observation> ReceivedMatches { get; private set; } = [];

        public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
            ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            ReceivedMatches = matched.Matched;
            IReadOnlyList<Finding> result = _findingToReturn is null ? [] : [_findingToReturn];
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingRule : ISynthesisRule
    {
        public ThrowingRule(string ruleId)
        {
            RuleId = ruleId;
            Query = new ObservationQuery { ObservationTypes = ["type.retention"] };
        }

        public string RuleId { get; }
        public ObservationQuery Query { get; }

        public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
            ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthesis rule under test always throws");
    }
}
