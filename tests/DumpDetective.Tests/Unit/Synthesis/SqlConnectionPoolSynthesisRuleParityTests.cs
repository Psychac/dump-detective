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
/// Field-for-field parity between <see cref="SqlConnectionPoolFindingGenerator"/> (old) and
/// <see cref="SqlConnectionPoolSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class SqlConnectionPoolSynthesisRuleParityTests
{
    private readonly SqlConnectionPoolFindingGenerator _oldGenerator = new();
    private readonly SqlConnectionPoolSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoPoolsFound_ReturnsEmpty() => AssertParity([]);

    [Fact]
    public Task PoolsFoundButNoneNearCapacity_ReturnsEmpty() => AssertParity(
        [Pool(current: 5, max: 100, label: "Server=a;Database=b")]);

    [Fact]
    public Task SinglePoolAtWarningThreshold() => AssertParity(
        [Pool(current: 85, max: 100, label: "Server=a;Database=b")]);

    [Fact]
    public Task SinglePoolAtCriticalThreshold() => AssertParity(
        [Pool(current: 96, max: 100, label: "Server=a;Database=b")]);

    [Fact]
    public Task PicksHighestUtilisationPoolAmongMany() => AssertParity(
    [
        Pool(current: 82, max: 100, label: "Server=low;Database=low"),
        Pool(current: 97, max: 100, label: "Server=high;Database=high"),
        Pool(current: 10, max: 100, label: "Server=idle;Database=idle"),
    ]);

    [Fact]
    public Task PoolWithoutMaxSize_ExcludedFromWorstSelection() => AssertParity(
    [
        Pool(current: 50, max: null, label: "Server=unknown;Database=unknown"),
        Pool(current: 90, max: 100, label: "Server=known;Database=known"),
    ]);

    [Fact]
    public Task PoolWithoutLabel_UsesUnknownPlaceholder() => AssertParity(
        [Pool(current: 90, max: 100, label: null)]);

    [Fact]
    public Task CountsNearCapacityAcrossMultiplePools() => AssertParity(
    [
        Pool(current: 82, max: 100, label: "Server=a;Database=a"),
        Pool(current: 90, max: 100, label: "Server=b;Database=b"),
        Pool(current: 10, max: 100, label: "Server=c;Database=c"),
    ]);

    private async Task AssertParity(IReadOnlyList<SqlConnectionPoolSnapshot> pools)
    {
        SqlConnectionPoolDomainResult result = BuildResult(pools);
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(pools);
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

    private static SqlConnectionPoolSnapshot Pool(int current, int? max, string? label) => new(
        TypeName: "System.Data.ProviderBase.DbConnectionPool",
        Address: 0x1000,
        CurrentSize: current,
        MaxPoolSize: max ?? -1,
        MinPoolSize: -1,
        AnonymisedConnectionString: label);

    private static IReadOnlyList<Observation> ToObservations(IReadOnlyList<SqlConnectionPoolSnapshot> pools)
    {
        var observations = new List<Observation>();
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };

        foreach (SqlConnectionPoolSnapshot pool in pools)
        {
            var measures = new Dictionary<string, Measure>
            {
                ["current.size"] = new Measure(pool.CurrentSize, MeasureUnit.Count, MeasureSemantics.Absolute),
            };
            if (pool.MaxPoolSize >= 0)
                measures["max.size"] = new Measure(pool.MaxPoolSize, MeasureUnit.Count, MeasureSemantics.Absolute);
            if (pool.MinPoolSize >= 0)
                measures["min.size"] = new Measure(pool.MinPoolSize, MeasureUnit.Count, MeasureSemantics.Absolute);

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.sql-connection-pool",
                Subjects = [new TypeRef { CanonicalName = pool.TypeName, Fidelity = MatchFidelity.Exact }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = measures,
                Provenance = new Provenance
                {
                    Artifact = TestArtifact,
                    AnalyzerKey = "SQL Connection Pool Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
                Evidence = [new EvidenceRef
                {
                    Artifact = TestArtifact,
                    Locator = pool.Address.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                    Description = pool.AnonymisedConnectionString,
                }],
            });
        }

        return observations;
    }

    private static SqlConnectionPoolDomainResult BuildResult(IReadOnlyList<SqlConnectionPoolSnapshot> pools)
    {
        int nearCapacity = 0;
        foreach (SqlConnectionPoolSnapshot pool in pools)
        {
            if (UtilizationPercent(pool) >= 80.0) nearCapacity++;
        }

        return new SqlConnectionPoolDomainResult(
            PoolsFound: pools.Count > 0,
            TotalPools: pools.Count,
            PoolsNearCapacity: nearCapacity,
            Pools: pools);
    }

    private static double UtilizationPercent(SqlConnectionPoolSnapshot pool) =>
        pool.MaxPoolSize > 0 ? 100.0 * pool.CurrentSize / pool.MaxPoolSize : -1;
}
