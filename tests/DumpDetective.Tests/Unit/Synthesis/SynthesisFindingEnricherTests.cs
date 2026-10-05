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
            Findings = [new InsightFinding("Stub Analyzer", "Old", DumpDetective.Core.Enums.FindingSeverity.Info, "old title", "old evidence", "old rec", [])],
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
            Findings = [new InsightFinding("Other Analyzer", "Old", DumpDetective.Core.Enums.FindingSeverity.Info, "kept title", "kept evidence", "kept rec", [])],
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
    public async Task EnrichAsync_RecordsLineageAndBreakdownPerFingerprint()
    {
        var enricher = new SynthesisFindingEnricher(new FakeRuleFactory([new StubRule("Stub Analyzer", "stub-fingerprint")]));
        AnalyzerRunResult run = new("Stub Analyzer", AnalyzerExecutionStatus.Success, TimeSpan.Zero, Result: null, ErrorMessage: null, ErrorType: null);
        Observation observation = MakeObservation();

        SynthesisEnrichment result = await enricher.EnrichAsync([run], [observation], CancellationToken.None);

        FindingLineage lineage = result.LineageByFingerprint.Should().ContainKey("stub-fingerprint").WhoseValue;
        lineage.DerivedFrom.Should().Equal(observation.Id);
        lineage.Confidence.Should().BeEquivalentTo(ConfidenceBreakdown.MeasurementCertain);
    }

    [Fact]
    public void FindingLineage_MergeWith_KeepsEveryObservationAndTheLessConfidentBreakdown()
    {
        ObservationId shared = ObservationId.NewId();
        ObservationId onlyInSecond = ObservationId.NewId();
        ConfidenceBreakdown lessConfident = new()
        {
            Composite = 0.4,
            EvidenceStrength = 1.0,
            IdentityFidelityCap = 1.0,
            TemporalAlignmentCap = 1.0,
            CapabilityFidelity = 1.0,
            ConflictPenalty = 1.0,
        };
        var first = new FindingLineage([shared], ConfidenceBreakdown.MeasurementCertain);
        var second = new FindingLineage([shared, onlyInSecond], lessConfident);

        FindingLineage merged = first.MergeWith(second);

        merged.DerivedFrom.Should().Equal(shared, onlyInSecond);
        merged.Confidence.Should().Be(lessConfident);
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

    private sealed class FakeRuleFactory : ISynthesisRuleFactory
    {
        private readonly IReadOnlyList<ISynthesisRule> _rules;
        public FakeRuleFactory(IReadOnlyList<ISynthesisRule> rules) => _rules = rules;
        public IReadOnlyList<ISynthesisRule> CreateRules() => _rules;
    }

    private sealed class StubRule : ISynthesisRule
    {
        private readonly string _analyzerName;
        private readonly string _fingerprint;

        public StubRule(string analyzerName, string fingerprint)
        {
            _analyzerName = analyzerName;
            _fingerprint = fingerprint;
            Query = new ObservationQuery { ObservationTypes = ["stub.type"] };
        }

        public string RuleId => "stub.rule";
        public int Version => 1;
        public ObservationQuery Query { get; }

        public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
            ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<Finding> findings = [new Finding
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
            }];
            return ValueTask.FromResult(findings);
        }
    }
}
