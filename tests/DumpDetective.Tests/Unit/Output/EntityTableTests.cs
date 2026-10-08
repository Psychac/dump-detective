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
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class EntityTableTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dd-entities-{Guid.NewGuid():N}");
    private readonly ReportBuilderFacade _facade;

    public EntityTableTests()
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
    public void Build_NumbersEntitiesDenselyByKindThenJoinKey()
    {
        Observation[] observations =
        [
            MakeObservation(Thread(20), Type("System.String")),
            MakeObservation(Module("mscorlib.dll"), Type("System.Data.DataColumn"), Thread(7)),
        ];

        IReadOnlyList<EntityRecord> table = EntityTableBuilder.Build(observations, []);

        table.Select(e => (e.Id, e.Kind, e.JoinKey)).Should().Equal(
            (0, EntityKind.Type, "System.Data.DataColumn"),
            (1, EntityKind.Type, "System.String"),
            (2, EntityKind.Module, "mscorlib.dll"),
            (3, EntityKind.Thread, "20"),
            (4, EntityKind.Thread, "7"));
    }

    [Fact]
    public void Build_SameEntitiesInAnyOrder_ProduceAnIdenticalTable()
    {
        Observation[] forward =
        [
            MakeObservation(Type("A"), Thread(1)),
            MakeObservation(Type("B")),
            MakeObservation(Module("m.dll"), Type("A")),
        ];
        Observation[] reversed = [forward[2], forward[1], forward[0]];

        IReadOnlyList<EntityRecord> first = EntityTableBuilder.Build(forward, []);
        IReadOnlyList<EntityRecord> second = EntityTableBuilder.Build(reversed, []);

        second.Should().Equal(first);
    }

    [Fact]
    public void Build_SameEntityAtDifferentFidelity_KeepsTheLowestRegardlessOfOrder()
    {
        Observation exact = MakeObservation(Type("Closure", MatchFidelity.Exact));
        Observation low = MakeObservation(Type("Closure", MatchFidelity.Low));

        EntityTableBuilder.Build([exact, low], []).Should().ContainSingle().Which.Fidelity.Should().Be(MatchFidelity.Low);
        EntityTableBuilder.Build([low, exact], []).Should().ContainSingle().Which.Fidelity.Should().Be(MatchFidelity.Low);
    }

    [Fact]
    public void Build_IncludesAClaimSubjectNoObservationNames()
    {
        var claimWithSubject = new FindingRecord("f1", "Stub Analyzer", "Leak", "Warning", "title", ["evidence"], "rec", [])
        {
            Assertion = new Assertion { Predicate = AssertionPredicate.Exhibits, Subject = Module("only-in-claim.dll") },
        };

        IReadOnlyList<EntityRecord> table = EntityTableBuilder.Build([MakeObservation(Type("A"))], [claimWithSubject]);

        table.Select(e => e.JoinKey).Should().Equal("A", "only-in-claim.dll");
    }

    [Fact]
    public void Build_ObservationsWithoutSubjects_ProduceNoEntities()
    {
        EntityTableBuilder.Build([MakeObservation()], []).Should().BeEmpty();
    }

    [Fact]
    public async Task OutputWriter_WritesTheEntityTableToReportJson_AndTwoRunsAreIdentical()
    {
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/Crash.dmp", [], TimeSpan.FromSeconds(1));
        Observation[] observations = [MakeObservation(Type("B"), Thread(3)), MakeObservation(Type("A"))];
        var writer = new ReportOutputWriter([new ReportJsonSink(_facade)]);

        string first = await WriteAndReadReportJson(writer, document, observations, "First.html");
        string second = await WriteAndReadReportJson(writer, document, [observations[1], observations[0]], "Second.html");

        using JsonDocument parsed = JsonDocument.Parse(first);
        JsonElement entities = parsed.RootElement.GetProperty("entities");
        entities.GetArrayLength().Should().Be(3);
        entities[0].GetProperty("id").GetInt32().Should().Be(0);
        entities[0].GetProperty("joinKey").GetString().Should().Be("A");
        entities[0].GetProperty("kind").GetString().Should().Be("Type");
        EntitiesJson(second).Should().Be(EntitiesJson(first));
    }

    [Fact]
    public async Task OutputWriter_WithoutObservations_WritesNoEntityTable()
    {
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/Crash.dmp", [], TimeSpan.FromSeconds(1));
        var writer = new ReportOutputWriter([new ReportJsonSink(_facade)]);

        string json = await WriteAndReadReportJson(writer, document, [], "Empty.html");

        using JsonDocument parsed = JsonDocument.Parse(json);
        parsed.RootElement.TryGetProperty("entities", out _).Should().BeFalse();
    }

    [Fact]
    public void Render_EmbeddedHtmlKeepsTheEntityTable()
    {
        AnalysisReportDocument document = new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/Crash.dmp",
            Observations = new ObservationsReference("Crash.observations.json.gz", "abc", 1, Compressed: true),
            Entities = [new EntityRecord(0, EntityKind.Type, "System.Data.DataColumn", MatchFidelity.Exact)],
        };

        string html = _facade.RenderDocument(document, ReportFormat.Html);

        html.Should().Contain("\"joinKey\":\"System.Data.DataColumn\"");
        html.Should().NotContain("Crash.observations.json.gz");
    }

    [Fact]
    public void Build_ModulePathEvidence_BecomesSortedDistinctDetailsOnTheModule_NotPartOfItsIdentity()
    {
        Observation first = WithPath(MakeObservation(Module("a.dll")), @"C:\gac\v2\a.dll");
        Observation second = WithPath(MakeObservation(Module("a.dll")), @"C:\app\a.dll");
        Observation repeated = WithPath(MakeObservation(Module("a.dll")), @"C:\app\a.dll");

        IReadOnlyList<EntityRecord> forward = EntityTableBuilder.Build([first, second, repeated], []);
        IReadOnlyList<EntityRecord> reversed = EntityTableBuilder.Build([repeated, second, first], []);

        EntityRecord module = forward.Should().ContainSingle().Which;
        module.JoinKey.Should().Be("a.dll");
        module.Details.Should().Equal(@"C:\app\a.dll", @"C:\gac\v2\a.dll");
        reversed.Single().Details.Should().Equal(module.Details);
    }

    [Fact]
    public void Build_PathEvidenceOnAnythingButAModule_AndBlankPaths_AreIgnored()
    {
        Observation onType = WithPath(MakeObservation(Type("A")), @"C:\x\A.dll");
        Observation blank = WithPath(MakeObservation(Module("b.dll")), "  ");
        Observation otherEvidence = MakeObservation(Module("c.dll")) with
        {
            Evidence = [new DumpDetective.Sdk.Observations.EvidenceRef { Artifact = new ArtifactId("t"), Locator = "original-stack-trace-confidence", Description = "High" }],
        };

        IReadOnlyList<EntityRecord> table = EntityTableBuilder.Build([onType, blank, otherEvidence], []);

        table.Should().OnlyContain(e => e.Details == null);
    }

    [Fact]
    public async Task OutputWriter_WritesDetailsOnlyWhenPresent()
    {
        AnalysisReportDocument document = _facade.BuildReportDocument("C:/dumps/Crash.dmp", [], TimeSpan.FromSeconds(1));
        var writer = new ReportOutputWriter([new ReportJsonSink(_facade)]);
        Observation[] observations = [WithPath(MakeObservation(Module("a.dll")), @"C:\app\a.dll"), MakeObservation(Type("T"))];

        string json = await WriteAndReadReportJson(writer, document, observations, "Details.html");

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement entities = parsed.RootElement.GetProperty("entities");
        JsonElement module = entities.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Module");
        JsonElement type = entities.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Type");
        module.GetProperty("details")[0].GetString().Should().Be(@"C:\app\a.dll");
        type.TryGetProperty("details", out _).Should().BeFalse();
    }

    private static Observation WithPath(Observation observation, string path) => observation with
    {
        Evidence = [new DumpDetective.Sdk.Observations.EvidenceRef { Artifact = new ArtifactId("test-artifact"), Locator = EntityDetailLocators.ModulePath, Description = path }],
    };

    private async Task<string> WriteAndReadReportJson(ReportOutputWriter writer, AnalysisReportDocument document, Observation[] observations, string outputName)
    {
        string outputPath = Path.Combine(_tempDir, outputName);
        var resolved = ResolvedExecutionOptionsFactory.Create(outputPath) with { Report = new ReportOptions { Format = ReportFormat.Html } };

        await writer.WriteAsync(resolved, document, observations, artifacts: null, CancellationToken.None);

        return File.ReadAllText(Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(outputName) + ".report.json"));
    }

    private static string EntitiesJson(string reportJson)
    {
        using JsonDocument parsed = JsonDocument.Parse(reportJson);
        return parsed.RootElement.GetProperty("entities").GetRawText();
    }

    private static TypeRef Type(string name, MatchFidelity fidelity = MatchFidelity.Exact) => new() { CanonicalName = name, Fidelity = fidelity };

    private static ModuleRef Module(string name) => new() { SimpleName = name, Fidelity = MatchFidelity.Exact };

    private static ThreadRef Thread(uint osThreadId) => new() { OsThreadId = osThreadId, Fidelity = MatchFidelity.Exact };

    private static Observation MakeObservation(params EntityRef[] subjects) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "stub.type",
        Subjects = subjects,
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
}
