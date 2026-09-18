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
/// Field-for-field parity between <see cref="DbConnectionFindingGenerator"/> (old) and
/// <see cref="DbConnectionSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <see cref="BuildResult"/> derives every aggregate from a single list of open-connection entries
/// (generation + optional root path) plus a list of per-type totals — the same "keep fixtures
/// consistent with real production coupling" principle earlier playbook entries use, since the new
/// rule can only reconstruct <c>OpenConnections</c>/<c>Gen2OpenConnections</c> by counting/filtering
/// per-connection observations.
/// </remarks>
public sealed class DbConnectionSynthesisRuleParityTests
{
    private readonly DbConnectionFindingGenerator _oldGenerator = new();
    private readonly DbConnectionSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoConnectionsFound_ReturnsEmpty() => AssertParity(BuildResult());

    [Fact]
    public Task TotalConnectionsAtWarningThreshold() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 55, generation: 1)));

    [Fact]
    public Task TotalConnectionsAtCriticalThreshold() => AssertParity(BuildResult(
        closed: 250, openEntries: OpenEntries(count: 10, generation: 1)));

    [Fact]
    public Task TotalConnectionsBelowThreshold_NoFinding() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 10, generation: 1)));

    [Fact]
    public Task OpenConnectionsAtWarningThreshold() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 20, generation: 1)));

    [Fact]
    public Task OpenConnectionsBelowThreshold_NoFinding() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 15, generation: 1)));

    [Fact]
    public Task BrokenConnectionsAtWarningThreshold() => AssertParity(BuildResult(broken: 5));

    [Fact]
    public Task BrokenConnectionsBelowThreshold_NoFinding() => AssertParity(BuildResult(broken: 3));

    [Fact]
    public Task Gen2OpenConnectionsAtThreshold_NoRootPath() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 5, generation: 2)));

    [Fact]
    public Task Gen2OpenConnectionsAtThreshold_BelowThreshold_NoFinding() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 3, generation: 2)));

    [Fact]
    public Task Gen2OpenConnections_PicksLargestRootPath() => AssertParity(BuildResult(
        openEntries:
        [
            .. OpenEntries(count: 5, generation: 2),
            new OpenEntry(Generation: 2, RetainedBytes: 1_000, RootPath: "StaticVar: MyApp.Cache@0x1000 -> SqlConnection@0x2000"),
            new OpenEntry(Generation: 2, RetainedBytes: 5_000, RootPath: "StaticVar: MyApp.Pool@0x3000 -> SqlConnection@0x4000"),
        ]));

    [Fact]
    public Task TypeBreakdown_TrustsEmissionOrderTruncatesAtThree() => AssertParity(BuildResult(
        openEntries: OpenEntries(count: 60, generation: 1),
        typeCounts: [("MyApp.Ns.TypeA", 30), ("MyApp.Ns.TypeB", 20), ("MyApp.Ns.TypeC", 15), ("MyApp.Ns.TypeD", 5)]));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        closed: 100, broken: 10,
        openEntries:
        [
            .. OpenEntries(count: 20, generation: 1),
            .. OpenEntries(count: 5, generation: 2),
        ],
        typeCounts: [("MyApp.Ns.TypeA", 15), ("MyApp.Ns.TypeB", 10)]));

    private static IReadOnlyList<OpenEntry> OpenEntries(int count, int generation)
    {
        var list = new List<OpenEntry>(count);
        for (int i = 0; i < count; i++) list.Add(new OpenEntry(Generation: generation, RetainedBytes: null, RootPath: null));
        return list;
    }

    private sealed record OpenEntry(int Generation, ulong? RetainedBytes, string? RootPath);

    private async Task AssertParity((DbConnectionDomainResult Result, IReadOnlyList<OpenEntry> OpenEntries, IReadOnlyList<(string TypeName, long Count)> TypeCounts) fixture)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(fixture.Result);
        IReadOnlyList<Observation> observations = ToObservations(fixture.OpenEntries, fixture.TypeCounts, fixture.Result);
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

    private static IReadOnlyList<Observation> ToObservations(
        IReadOnlyList<OpenEntry> openEntries, IReadOnlyList<(string TypeName, long Count)> typeCounts, DbConnectionDomainResult result)
    {
        if (result.TotalConnections == 0) return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments };
        var observations = new List<Observation>();

        foreach (OpenEntry entry in openEntries)
        {
            var measures = new Dictionary<string, Measure>
            {
                ["generation.ordinal"] = new Measure(entry.Generation, MeasureUnit.Custom, MeasureSemantics.Absolute),
            };
            if (entry.RetainedBytes is ulong retained)
                measures["retained.bytes"] = new Measure(retained, MeasureUnit.Bytes, MeasureSemantics.Absolute);

            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.db-connection",
                Subjects = [new TypeRef { CanonicalName = "System.Data.SqlClient.SqlConnection", Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = measures,
                Provenance = Provenance(capabilitiesUsed),
                Confidence = entry.RetainedBytes is not null ? 1.0 : 0.7,
                Evidence = entry.RootPath is not null
                    ? [new EvidenceRef { Artifact = TestArtifact, Locator = "0x1000", Description = entry.RootPath }]
                    : [],
            });
        }

        foreach ((string typeName, long count) in typeCounts)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.db-connection-type-population",
                Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects }),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.db-connection-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(result.TotalConnections, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["closed.count"] = new Measure(result.ClosedConnections, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["broken.count"] = new Measure(result.BrokenConnections, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["other.count"] = new Measure(result.OtherConnections, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["unknown-state.count"] = new Measure(result.UnknownStateConnections, MeasureUnit.Count, MeasureSemantics.Absolute),
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
        AnalyzerKey = "DB Connection Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static (DbConnectionDomainResult, IReadOnlyList<OpenEntry>, IReadOnlyList<(string TypeName, long Count)>) BuildResult(
        int closed = 0,
        int broken = 0,
        int other = 0,
        int unknownState = 0,
        IReadOnlyList<OpenEntry>? openEntries = null,
        IReadOnlyList<(string TypeName, long Count)>? typeCounts = null)
    {
        openEntries ??= [];
        typeCounts ??= [];

        int open = openEntries.Count;
        int gen2Open = 0, gen0Open = 0;
        foreach (OpenEntry entry in openEntries)
        {
            if (entry.Generation == 2) gen2Open++;
            else if (entry.Generation == 0) gen0Open++;
        }

        int total = open + closed + broken + other + unknownState;

        var byType = new List<DbConnectionTypeSummary>();
        foreach ((string typeName, long count) in typeCounts)
            byType.Add(new DbConnectionTypeSummary(typeName, (int)count, 0, 0, 0, 0, 0, 0));

        var result = new DbConnectionDomainResult(
            ConnectionsFound: total > 0,
            TotalConnections: total,
            OpenConnections: open,
            ClosedConnections: closed,
            BrokenConnections: broken,
            OtherConnections: other,
            UnknownStateConnections: unknownState,
            Gen2OpenConnections: gen2Open,
            Gen0OpenConnections: gen0Open,
            ByType: byType,
            TopOpenConnections: BuildSnapshots(openEntries),
            TopPools: []);

        return (result, openEntries, typeCounts);
    }

    private static IReadOnlyList<DbConnectionSnapshot> BuildSnapshots(IReadOnlyList<OpenEntry> openEntries)
    {
        var list = new List<DbConnectionSnapshot>(openEntries.Count);
        for (int i = 0; i < openEntries.Count; i++)
        {
            OpenEntry entry = openEntries[i];
            list.Add(new DbConnectionSnapshot(
                TypeName: "System.Data.SqlClient.SqlConnection",
                Address: (ulong)(0x1000 + i),
                StateLabel: "Open",
                StateValue: 1,
                Generation: (sbyte)entry.Generation,
                RetainedBytes: entry.RetainedBytes,
                RootPath: entry.RootPath));
        }
        return list;
    }
}
