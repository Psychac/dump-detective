using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

public sealed class SynthesisFindingEnricherTests
{
    [Fact]
    public async Task EnrichAsync_OverridesFindingsForAnalyzerWithAMigratedRule()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory([new StubRule("Stub Analyzer", "stub-fingerprint")]));

        AnalyzerRunResult run = new("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null)
        {
            Findings = [ClaimFactory.Create("Stub Analyzer", "Old", DumpDetective.Sdk.Synthesis.Severity.Info, "old title", "old evidence", "old rec", [])],
        };

        IReadOnlyList<AnalyzerRunResult> result = (await enricher.EnrichAsync([run], [MakeObservation()], CancellationToken.None)).Runs;

        result.Should().ContainSingle();
        result[0].Findings.Should().ContainSingle(f => f.Fingerprint == "stub-fingerprint");
    }

    [Fact]
    public async Task EnrichAsync_LeavesUnrelatedAnalyzersUntouched()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory([new StubRule("Stub Analyzer", "stub-fingerprint")]));

        AnalyzerRunResult unrelated = new("Other Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null)
        {
            Findings = [ClaimFactory.Create("Other Analyzer", "Old", DumpDetective.Sdk.Synthesis.Severity.Info, "kept title", "kept evidence", "kept rec", [])],
        };

        IReadOnlyList<AnalyzerRunResult> result = (await enricher.EnrichAsync([unrelated], [MakeObservation()], CancellationToken.None)).Runs;

        result[0].Findings.Should().ContainSingle(f => f.Title == "kept title");
    }

    [Fact]
    public async Task EnrichAsync_KeepsFindingsWhoseAnalyzerHasNoRun()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory([new StubRule("InsightEngine", "cross-analyzer-fingerprint")]));
        AnalyzerRunResult run = new("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null);

        SynthesisEnrichment result = await enricher.EnrichAsync([run], [MakeObservation()], CancellationToken.None);

        result.UnattachedFindings.Should().ContainSingle(f => f.Fingerprint == "cross-analyzer-fingerprint");
        result.Runs.Should().ContainSingle().Which.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_AttachesLineageAndBreakdownToTheClaimItself()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory([new StubRule("Stub Analyzer", "stub-fingerprint")]));
        AnalyzerRunResult run = new("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null);
        Observation observation = MakeObservation();

        SynthesisEnrichment result = await enricher.EnrichAsync([run], [observation], CancellationToken.None);

        Claim claim = result.Runs.Should().ContainSingle().Which.Findings.Should().ContainSingle().Which;
        claim.DerivedFrom.Should().Equal(observation.Id);
        claim.Confidence.Should().BeEquivalentTo(ConfidenceBreakdown.MeasurementCertain);
    }

    [Fact]
    public async Task EnrichAsync_ReturnsRunsUnchanged_WhenNoObservations()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory([new StubRule("Stub Analyzer", "stub-fingerprint")]));
        AnalyzerRunResult run = new("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null);
        AnalyzerRunResult[] runs = [run];

        IReadOnlyList<AnalyzerRunResult> result = (await enricher.EnrichAsync(runs, [], CancellationToken.None)).Runs;

        result.Should().BeSameAs(runs);
    }

    private static Observation MakeObservation() => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "stub.type",
        Subjects = [new TypeRef { CanonicalName = "System.Object", Fidelity = MatchFidelity.Exact }],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>(),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "Stub Analyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    [Fact]
    public async Task EnrichAsync_LinksClaimsOfALimitedAnalyzerToItsCoverageRecord_AndNamesTheBoundaryInTheirConfidence()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory(
            [new StubRule("Stub Analyzer", "stub-fingerprint"), new StubRule("Other Analyzer", "other-fingerprint")],
            [new LimitedStubCoverageRule("Stub Analyzer")]));

        SynthesisEnrichment enrichment = await enricher.EnrichAsync([], [MakeObservation()], CancellationToken.None);

        CoverageRecord record = enrichment.Coverage.Should().ContainSingle().Subject;
        record.Claims.Should().Equal("stub-fingerprint");
        record.Rule.Should().Be(new RuleRef("stub.coverage", 1));

        Claim limited = enrichment.UnattachedFindings.Single(c => c.Fingerprint == "stub-fingerprint");
        limited.Confidence.LimitingFactors.Should().ContainSingle().Which.Should().Contain("7 of 10 stub items hit the stub limit");
        limited.Confidence.Composite.Should().Be(ConfidenceBreakdown.MeasurementCertain.Composite);

        enrichment.UnattachedFindings.Single(c => c.Fingerprint == "other-fingerprint").Confidence.LimitingFactors.Should().BeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_KeepsANormalClaimOfALimitedAnalyzer_BecauseNormalIsNotAlwaysAnAbsence()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory(
            [new StubRule("Stub Analyzer", "threshold-normal", AssertionPredicate.Normal)], [new LimitedStubCoverageRule("Stub Analyzer")]));

        SynthesisEnrichment enrichment = await enricher.EnrichAsync([], [MakeObservation()], CancellationToken.None);

        enrichment.UnattachedFindings.Should().ContainSingle(c => c.Fingerprint == "threshold-normal");
        enrichment.Coverage.Single().Claims.Should().Equal("threshold-normal");
    }

    private sealed class FakeRuleFactory : ISynthesisRuleFactory
    {
        private readonly IReadOnlyList<ISynthesisRule> _rules;
        private readonly IReadOnlyList<ICoverageRule> _coverageRules;

        public FakeRuleFactory(IReadOnlyList<ISynthesisRule> rules, IReadOnlyList<ICoverageRule>? coverageRules = null)
        {
            _rules = rules;
            _coverageRules = coverageRules ?? [];
        }

        public IReadOnlyList<ISynthesisRule> CreateRules() => _rules;
        public IReadOnlyList<ICoverageRule> CreateCoverageRules() => _coverageRules;
    }

    private sealed class LimitedStubCoverageRule(string analyzer) : ICoverageRule
    {
        public string RuleId => "stub.coverage";
        public int Version => 1;
        public ObservationQuery Query { get; } = new() { ObservationTypes = ["stub.type"] };

        public IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context) =>
        [
            new CoverageRecord(
                new Capability("stub.capability"), new ArtifactId("stub-artifact"), CoverageStatus.Partial,
                [new CoverageImpact(analyzer, CoverageImpactKind.Limited)], new CoverageBoundary(7, 10, "stub items", "hit the stub limit")),
        ];
    }

    private sealed class StubRule : ISynthesisRule
    {
        private readonly string _analyzerName;
        private readonly string _fingerprint;

        private readonly AssertionPredicate? _predicate;

        public StubRule(string analyzerName, string fingerprint, AssertionPredicate? predicate = null)
        {
            _analyzerName = analyzerName;
            _fingerprint = fingerprint;
            _predicate = predicate;
            Query = new ObservationQuery { ObservationTypes = ["stub.type"] };
        }

        public string RuleId => "stub.rule";
        public int Version => 1;
        public ObservationQuery Query { get; }

        public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
            ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<Claim> findings = [new Claim
            {
                Fingerprint = _fingerprint,
                Severity = Severity.Info,
                Title = "stub title",
                Narrative = "stub narrative",
                Recommendation = "stub recommendation",
                DerivedFrom = matched.Matched.Select(o => o.Id).ToArray(),
                Confidence = ConfidenceBreakdown.MeasurementCertain,
                Analyzer = _analyzerName,
                Category = "Stub",
                Assertion = _predicate is { } predicate ? new Assertion { Predicate = predicate } : null,
            }];
            return ValueTask.FromResult(findings);
        }
    }
}
