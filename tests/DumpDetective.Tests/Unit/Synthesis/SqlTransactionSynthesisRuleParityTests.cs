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
/// Field-for-field parity between <see cref="SqlTransactionFindingGenerator"/> (old) and
/// <see cref="SqlTransactionSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class SqlTransactionSynthesisRuleParityTests
{
    private readonly SqlTransactionFindingGenerator _oldGenerator = new();
    private readonly SqlTransactionSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoTransactionsFound_ReturnsEmpty() => AssertParity(active: 0, total: 0, other: 0);

    [Fact]
    public Task ActiveBelowWarningThreshold_ReturnsEmpty() => AssertParity(active: 3, total: 20, other: 1);

    [Fact]
    public Task ActiveAtWarningThreshold() => AssertParity(active: 5, total: 50, other: 2);

    [Fact]
    public Task ActiveAtCriticalThreshold() => AssertParity(active: 25, total: 100, other: 5);

    [Fact]
    public Task ActiveAboveCriticalThreshold() => AssertParity(active: 40, total: 100, other: 3);

    private async Task AssertParity(int active, int total, int other)
    {
        SqlTransactionDomainResult result = BuildResult(active, total, other);
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(active, total, other);
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

    private static IReadOnlyList<Observation> ToObservations(int active, int total, int other)
    {
        if (total == 0) return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };
        var observations = new List<Observation>();

        for (int i = 0; i < active; i++)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.sql-transaction",
                Subjects = [new TypeRef { CanonicalName = "System.Data.SqlClient.SqlTransaction", Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["shallow.bytes"] = new Measure(48, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.sql-transaction-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(total, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["disposed.count"] = new Measure(total - active - other, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["other.count"] = new Measure(other, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(capabilitiesUsed),
            Confidence = 1.0,
        });

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "SQL Transaction Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static SqlTransactionDomainResult BuildResult(int active, int total, int other) => new(
        TransactionsFound: total > 0,
        TotalTransactions: total,
        DisposedCount: total - active - other,
        ActiveCount: active,
        OtherCount: other,
        ByType: [],
        TopActiveTransactions: []);
}
