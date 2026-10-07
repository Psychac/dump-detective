using System.Text.Json;
using DumpDetective.Cli.Output;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class EntitySummaryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dd-summary-{Guid.NewGuid():N}");
    private readonly ReportBuilderFacade _facade;

    public EntitySummaryTests()
    {
        Directory.CreateDirectory(_tempDir);
        _facade = new ReportBuilderFacade(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlReportRenderer(),
            new JsonCanonicalReportFormatter()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer(new ReportSerializer()));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Build_GroupsFactsByKindAndCountsThem()
    {
        TypeRef column = Type("System.Data.DataColumn");
        Observation[] observations =
        [
            Fact("leak.candidate-type", "Leak Candidate Analysis", [column], ("instance.count", 54068, MeasureUnit.Count)),
            Fact("finalizer.queue-entry", "Finalizable Object Analysis", [column], ("shallow.bytes", 216, MeasureUnit.Bytes)),
            Fact("finalizer.queue-entry", "Finalizable Object Analysis", [column], ("shallow.bytes", 216, MeasureUnit.Bytes)),
        ];

        (EntitySummary summary, FactGroup[] groups) = BuildFor(observations, column);

        summary.FactKinds.Select(k => (k.Name, k.Analyzer, k.Panel)).Should().Equal(
            ("finalizer.queue-entry", "Finalizable Object Analysis", "Lifecycle"),
            ("leak.candidate-type", "Leak Candidate Analysis", "Size and population"));
        groups.Select(g => (summary.FactKinds[g.FactKind].Name, g.Count)).Should().Equal(
            ("finalizer.queue-entry", 2), ("leak.candidate-type", 1));
    }

    [Fact]
    public void Build_SingleFact_GivesItsValueAsSumMinAndMax()
    {
        TypeRef column = Type("System.Data.DataColumn");
        Observation[] observations = [Fact("leak.candidate-type", "A", [column], ("instance.count", 54068, MeasureUnit.Count))];

        (_, FactGroup[] groups) = BuildFor(observations, column);

        groups.Single().Stats.Single().Should().Be(new MeasureStat(0, 54068, 54068, 54068));
    }

    [Fact]
    public void Build_SeveralFacts_SumsAllowListedMeasures_ButNeverRetainedBytes()
    {
        TypeRef column = Type("System.Data.DataColumn");
        Observation[] observations =
        [
            Fact("finalizer.queue-entry", "A", [column], ("shallow.bytes", 200, MeasureUnit.Bytes), ("retained.bytes", 1000, MeasureUnit.Bytes)),
            Fact("finalizer.queue-entry", "A", [column], ("shallow.bytes", 300, MeasureUnit.Bytes), ("retained.bytes", 5000, MeasureUnit.Bytes)),
        ];

        (EntitySummary summary, FactGroup[] groups) = BuildFor(observations, column);

        MeasureStat shallow = groups.Single().Stats.Single(s => summary.Measures[s.Measure].Name == "shallow.bytes");
        MeasureStat retained = groups.Single().Stats.Single(s => summary.Measures[s.Measure].Name == "retained.bytes");
        shallow.Should().Be(new MeasureStat(shallow.Measure, 500, 200, 300));
        retained.Should().Be(new MeasureStat(retained.Measure, null, 1000, 5000));
    }

    [Fact]
    public void Build_AnUnreviewedMeasure_IsNeverSummed()
    {
        TypeRef column = Type("System.Data.DataColumn");
        Observation[] observations =
        [
            Fact("type.boxing", "A", [column], ("some.new-average", 10, MeasureUnit.Count)),
            Fact("type.boxing", "A", [column], ("some.new-average", 30, MeasureUnit.Count)),
        ];

        (_, FactGroup[] groups) = BuildFor(observations, column);

        groups.Single().Stats.Single().Sum.Should().BeNull();
    }

    [Fact]
    public void Build_UnmappedFactKind_LandsInOtherUnderItsOwnName()
    {
        TypeRef column = Type("System.Data.DataColumn");
        Observation[] observations = [Fact("brand.new-kind", "New Analyzer", [column], ("instance.count", 1, MeasureUnit.Count))];

        (EntitySummary summary, _) = BuildFor(observations, column);

        summary.FactKinds.Should().ContainSingle().Which.Should().Be(new FactKindRecord("brand.new-kind", "New Analyzer", "Other"));
    }

    [Fact]
    public void Build_ObservationNamingTwoEntities_CountsForBoth()
    {
        TypeRef machine = Type("App.Worker+<Run>d__");
        MethodRef method = new() { DeclaringType = Type("App.Worker"), Name = "Run", NormalizedSignature = "()", Fidelity = MatchFidelity.High };
        Observation[] observations = [Fact("type.async-state-machine", "A", [machine, method], ("instance.count", 7, MeasureUnit.Count))];
        IReadOnlyList<EntityRecord> entities = EntityTableBuilder.Build(observations, []);

        EntitySummary summary = EntitySummaryBuilder.Build(observations, entities);

        summary.Entities.Select(e => e.EntityId).Should().BeEquivalentTo(entities.Select(e => e.Id));
        summary.Entities.Should().OnlyContain(e => e.Groups.Single().Count == 1);
    }

    [Fact]
    public void Build_ObservationWithoutSubjects_ContributesNothing()
    {
        Observation[] observations = [Fact("heap.memory-composition", "A", [], ("total.bytes", 5, MeasureUnit.Bytes))];

        EntitySummary summary = EntitySummaryBuilder.Build(observations, []);

        summary.Entities.Should().BeEmpty();
        summary.FactKinds.Should().BeEmpty();
    }

    [Fact]
    public void Build_SameFactsInAnyOrder_ProduceIdenticalJson()
    {
        TypeRef a = Type("A");
        TypeRef b = Type("B");
        Observation[] forward =
        [
            Fact("leak.candidate-type", "X", [a], ("instance.count", 1, MeasureUnit.Count), ("shallow.bytes", 10, MeasureUnit.Bytes)),
            Fact("type.boxing", "Y", [a, b], ("instance.count", 2, MeasureUnit.Count)),
            Fact("type.boxing", "Y", [b], ("instance.count", 3, MeasureUnit.Count)),
            Fact("finalizer.queue-entry", "Z", [b], ("retained.bytes", 7, MeasureUnit.Bytes)),
        ];
        Observation[] reversed = [forward[3], forward[2], forward[1], forward[0]];

        string first = Serialize(forward);
        string second = Serialize(reversed);

        second.Should().Be(first);
    }

    [Fact]
    public void EntityFacts_AreWrittenAsNestedArrays()
    {
        TypeRef column = Type("A");
        Observation[] observations = [Fact("leak.candidate-type", "X", [column], ("instance.count", 5, MeasureUnit.Count))];

        using JsonDocument parsed = JsonDocument.Parse(Serialize(observations));

        JsonElement entity = parsed.RootElement.GetProperty("entities")[0];
        entity[0].GetInt32().Should().Be(0);
        JsonElement group = entity[1][0];
        group[0].GetInt32().Should().Be(0);
        group[1].GetInt32().Should().Be(1);
        JsonElement stat = group[2][0];
        stat.EnumerateArray().Select(v => v.GetDouble()).Should().Equal(0, 5, 5, 5);
        parsed.RootElement.GetProperty("factKinds")[0].GetProperty("panel").GetString().Should().Be("Size and population");
        parsed.RootElement.GetProperty("measures")[0].GetProperty("name").GetString().Should().Be("instance.count");
    }

    [Fact]
    public async Task OutputWriter_WritesTheSummaryToReportJson_AndTheEmbeddedHtmlKeepsIt_AndNoObservationsMeansNoSummary()
    {
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/Crash.dmp", [], TimeSpan.FromSeconds(1));
        var writer = new ReportOutputWriter([new FileReportSink(_facade), new ReportJsonSink(_facade)]);
        TypeRef column = Type("System.Data.DataColumn");
        var resolved = ResolvedExecutionOptionsFactory.Create(Path.Combine(_tempDir, "Crash.html")) with { Report = new ReportOptions { Format = ReportFormat.Html } };

        await writer.WriteAsync(resolved, document, [Fact("leak.candidate-type", "X", [column], ("instance.count", 5, MeasureUnit.Count))], artifacts: null, CancellationToken.None);

        using JsonDocument reportJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "Crash.report.json")));
        reportJson.RootElement.GetProperty("entitySummary").GetProperty("entities").GetArrayLength().Should().Be(1);
        string html = _facade.RenderDocument(document with { EntitySummary = EntitySummaryBuilder.Build([], []) with { FactKinds = [new FactKindRecord("embedded.marker", "A", "Other")] } }, ReportFormat.Html);
        html.Should().Contain("embedded.marker");

        var emptyResolved = ResolvedExecutionOptionsFactory.Create(Path.Combine(_tempDir, "Empty.html")) with { Report = new ReportOptions { Format = ReportFormat.Html } };
        await writer.WriteAsync(emptyResolved, document, [], artifacts: null, CancellationToken.None);
        using JsonDocument emptyJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "Empty.report.json")));
        emptyJson.RootElement.TryGetProperty("entitySummary", out _).Should().BeFalse();
    }

    private static string Serialize(Observation[] observations)
    {
        IReadOnlyList<EntityRecord> entities = EntityTableBuilder.Build(observations, []);
        EntitySummary summary = EntitySummaryBuilder.Build(observations, entities);
        string reportJson = new JsonCanonicalReportFormatter().Render(new SingleDumpReportDocument { DumpPath = "x", EntitySummary = summary });
        using JsonDocument parsed = JsonDocument.Parse(reportJson);
        return parsed.RootElement.GetProperty("entitySummary").GetRawText();
    }

    private static (EntitySummary Summary, FactGroup[] Groups) BuildFor(Observation[] observations, EntityRef entity)
    {
        IReadOnlyList<EntityRecord> entities = EntityTableBuilder.Build(observations, []);
        EntitySummary summary = EntitySummaryBuilder.Build(observations, entities);
        int entityId = entities.Single(e => e.Kind == entity.Kind && e.JoinKey == entity.JoinKey).Id;
        return (summary, summary.Entities.Single(e => e.EntityId == entityId).Groups.ToArray());
    }

    private static TypeRef Type(string name) => new() { CanonicalName = name, Fidelity = MatchFidelity.Exact };

    private static Observation Fact(string kind, string analyzer, EntityRef[] subjects, params (string Name, double Value, MeasureUnit Unit)[] measures) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = kind,
        Subjects = subjects,
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures.ToDictionary(m => m.Name, m => new Measure(m.Value, m.Unit, MeasureSemantics.Absolute)),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = analyzer,
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };
}
