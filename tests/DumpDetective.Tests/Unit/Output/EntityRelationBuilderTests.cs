using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class EntityRelationBuilderTests
{
    private static readonly EntityRecord[] Entities =
    [
        new(0, EntityKind.Type, "System.String", MatchFidelity.Exact),
        new(1, EntityKind.Type, "System.Data.DataColumn", MatchFidelity.Exact),
        new(2, EntityKind.Module, "mscorlib.dll", MatchFidelity.Exact),
        new(3, EntityKind.Module, "System.Data.dll", MatchFidelity.Exact),
        new(4, EntityKind.Thread, "24088", MatchFidelity.Exact),
    ];

    [Fact]
    public void Build_TwoEntityCellsInOneRow_AreStoredOnceLowerIdFirst()
    {
        var domains = Domains(Table("Top types", [Row(new EntityCell(2, null), new EntityCell(0, null))]));

        EntityRelations? relations = EntityRelationBuilder.FromTables(domains);

        EntityRelation pair = relations!.Pairs.Should().ContainSingle().Which;
        (pair.First, pair.Second).Should().Be((0, 2), "the pair is stored lower id first");
        pair.SourceIndexes.Should().Equal(0);
        relations.Sources.Should().Equal("Memory Analysis / Top types");
    }

    [Fact]
    public void Build_SamePairInSeveralRowsAndTables_IsOnePairWithEverySource()
    {
        var domains = Domains(
            Table("Top types", [Row(new EntityCell(0, null), new EntityCell(2, null)), Row(new EntityCell(0, null), new EntityCell(2, null))]),
            Table("Type table", [Row(new EntityCell(0, null), new EntityCell(2, null))]));

        EntityRelations? relations = EntityRelationBuilder.FromTables(domains);

        relations!.Pairs.Should().ContainSingle().Which.SourceIndexes.Should().Equal(0, 1);
        relations.Sources.Should().Equal("Memory Analysis / Top types", "Memory Analysis / Type table");
    }

    [Fact]
    public void Build_IgnoresTextCellsSelfPairsAndSingleEntityColumnTables()
    {
        var domains = Domains(
            Table("Mixed", [Row("plain text", new EntityCell(2, null)), Row(new EntityCell(0, null), new EntityCell(0, null))]),
            new CompactTable("One column", [new CompactHeader("Type", EntityColumn.Type)], [new CompactRow([new EntityCell(0, null)])]));

        EntityRelationBuilder.FromTables(domains).Should().BeNull("no row has two different entities");
    }

    [Fact]
    public void Build_IsOrderedByEntityIdsSoTwoRunsAgree()
    {
        var domains = Domains(Table("Top types", [
            Row(new EntityCell(1, null), new EntityCell(3, null)),
            Row(new EntityCell(0, null), new EntityCell(3, null)),
            Row(new EntityCell(0, null), new EntityCell(2, null)),
        ]));

        EntityRelationBuilder.FromTables(domains)!.Pairs.Select(p => (p.First, p.Second)).Should().Equal((0, 2), (0, 3), (1, 3));
    }

    [Fact]
    public void Apply_AttachesTheRelationsReadFromTheRewrittenCells()
    {
        var table = new CompactTable(
            "Top types",
            [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("Module", EntityColumn.Module)],
            [new CompactRow(["System.String", "mscorlib.dll"])]);
        AnalysisReportDocument document = Document(Domains(table));

        AnalysisReportDocument rewritten = EntityCellPass.Apply(document);

        EntityRelation pair = rewritten.Relations!.Pairs.Should().ContainSingle().Which;
        (pair.First, pair.Second).Should().Be((0, 2));
        pair.SourceIndexes.Should().Equal(0);
        document.Relations.Should().BeNull("the in-memory document is not changed");
    }

    [Fact]
    public void Validate_RelationsOutsideTheTableUnorderedOrWithoutASource_AreReported()
    {
        var good = new EntityRelations(["Memory Analysis / Top types"], [new EntityRelation(0, 2, [0])]);
        EntityReferenceValidator.ValidateRelations(good, Entities).Should().BeEmpty();
        EntityReferenceValidator.ValidateRelations(null, Entities).Should().BeEmpty();

        var broken = new EntityRelations(
            ["only"],
            [new EntityRelation(0, 99, [0]), new EntityRelation(3, 2, [0]), new EntityRelation(1, 2, []), new EntityRelation(0, 1, [5])]);
        IReadOnlyList<string> problems = EntityReferenceValidator.ValidateRelations(broken, Entities);

        problems.Should().Contain(p => p.Contains("Relation 0-99") && p.Contains("outside the entity table"));
        problems.Should().Contain(p => p.Contains("Relation 3-2") && p.Contains("lower id first"));
        problems.Should().Contain(p => p.Contains("Relation 1-2") && p.Contains("does not say where"));
        problems.Should().Contain(p => p.Contains("Relation 0-1") && p.Contains("source 5"));
    }

    [Fact]
    public void FromFacts_TwoSubjectsOfOneFact_AreAPairNamedByTheFactKind()
    {
        Observation[] observations = [Fact("infra.sql-transaction", Entity(0), Entity(2))];

        EntityRelations? relations = EntityRelationBuilder.FromFacts(observations, Entities);

        EntityRelation pair = relations!.Pairs.Should().ContainSingle().Which;
        (pair.First, pair.Second).Should().Be((0, 2));
        relations.Sources.Should().Equal("fact: infra.sql-transaction");
    }

    [Fact]
    public void FromFacts_IgnoresFactsWithFewerThanTwoKnownDistinctSubjects()
    {
        var unknown = new ModuleRef { SimpleName = "not-in-the-table.dll", Fidelity = MatchFidelity.Exact };
        Observation[] observations =
        [
            Fact("a.none"),
            Fact("a.one", Entity(0)),
            Fact("a.same-twice", Entity(0), Entity(0)),
            Fact("a.one-unknown", Entity(0), unknown),
        ];

        EntityRelationBuilder.FromFacts(observations, Entities).Should().BeNull();
    }

    [Fact]
    public void FromFacts_ThreeSubjects_RelateEveryTwo()
    {
        EntityRelations? relations = EntityRelationBuilder.FromFacts([Fact("a.three", Entity(0), Entity(2), Entity(4))], Entities);

        relations!.Pairs.Select(p => (p.First, p.Second)).Should().Equal((0, 2), (0, 4), (2, 4));
    }

    [Fact]
    public void FromFacts_SourcesAreSortedSoArrivalOrderDoesNotMatter()
    {
        Observation first = Fact("z.fact", Entity(0), Entity(2));
        Observation second = Fact("a.fact", Entity(1), Entity(3));

        EntityRelations forward = EntityRelationBuilder.FromFacts([first, second], Entities)!;
        EntityRelations backward = EntityRelationBuilder.FromFacts([second, first], Entities)!;

        forward.Sources.Should().Equal("fact: a.fact", "fact: z.fact");
        backward.Sources.Should().Equal(forward.Sources);
        backward.Pairs.Select(p => (p.First, p.Second, p.SourceIndexes[0])).Should().Equal(forward.Pairs.Select(p => (p.First, p.Second, p.SourceIndexes[0])));
    }

    [Fact]
    public void Merge_APairSeenBothWays_KeepsOnlyItsFactSource()
    {
        EntityRelations facts = new(["fact: type.async-state-machine"], [new EntityRelation(0, 2, [0])]);
        EntityRelations tables = new(["Async / Top"], [new EntityRelation(0, 2, [0]), new EntityRelation(1, 3, [0])]);

        EntityRelations merged = EntityRelationBuilder.Merge(facts, tables)!;

        merged.Sources.Should().Equal("Async / Top", "fact: type.async-state-machine");
        merged.Pairs.Select(p => (p.First, p.Second, string.Join(",", p.SourceIndexes.Select(i => merged.Sources[i]))))
            .Should().Equal((0, 2, "fact: type.async-state-machine"), (1, 3, "Async / Top"));
    }

    [Fact]
    public void Merge_AlreadyMergedInput_GivesTheSameResult()
    {
        EntityRelations facts = new(["fact: a.fact"], [new EntityRelation(0, 2, [0])]);
        EntityRelations tables = new(["Async / Top"], [new EntityRelation(1, 3, [0])]);

        EntityRelations once = EntityRelationBuilder.Merge(facts, tables)!;
        EntityRelations twice = EntityRelationBuilder.Merge(once, tables)!;

        twice.Sources.Should().Equal(once.Sources);
        twice.Pairs.Select(p => (p.First, p.Second, string.Join(",", p.SourceIndexes))).Should()
            .Equal(once.Pairs.Select(p => (p.First, p.Second, string.Join(",", p.SourceIndexes))));
    }

    [Fact]
    public void Merge_WithNothingOnEitherSide_IsNull()
    {
        EntityRelationBuilder.Merge(null, null).Should().BeNull();
        EntityRelationBuilder.Merge(new EntityRelations(["Async / Top"], [new EntityRelation(0, 2, [0])]), null).Should().BeNull("table sources in the existing list are not facts");
    }

    [Fact]
    public void Apply_KeepsTheFactPairsTheWriterSet_AndAddsTheRowPairs()
    {
        var table = new CompactTable(
            "Top types",
            [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("Module", EntityColumn.Module)],
            [new CompactRow(["System.String", "mscorlib.dll"])]);
        var facts = new EntityRelations(["fact: a.fact"], [new EntityRelation(1, 4, [0])]);
        AnalysisReportDocument document = Document(Domains(table), facts);

        EntityRelations rewritten = EntityCellPass.Apply(document).Relations!;

        rewritten.Pairs.Select(p => (p.First, p.Second, string.Join(",", p.SourceIndexes.Select(i => rewritten.Sources[i]))))
            .Should().Equal((0, 2, "Memory Analysis / Top types"), (1, 4, "fact: a.fact"));
    }

    [Fact]
    public void FromFacts_DirectedFact_NamesTheSourceReversedOnlyWhenTheFirstSubjectHasTheHigherId()
    {
        Observation forward = Fact("event.subscription", Entity(0), Entity(1));
        Observation reversed = Fact("event.subscription", Entity(3), Entity(2));

        EntityRelations relations = EntityRelationBuilder.FromFacts([forward, reversed], Entities)!;

        relations.Pairs.Select(p => (p.First, p.Second, string.Join(",", p.SourceIndexes.Select(i => relations.Sources[i]))))
            .Should().Equal((0, 1, "fact: event.subscription"), (2, 3, "fact: event.subscription (reversed)"));
    }

    [Fact]
    public void FromFacts_SameTypePairInBothDirectionsOnDifferentEvents_KeepsBothSources()
    {
        Observation ab = Fact("event.subscription", Entity(0), Entity(1));
        Observation ba = Fact("event.subscription", Entity(1), Entity(0));

        EntityRelation pair = EntityRelationBuilder.FromFacts([ab, ba], Entities)!.Pairs.Should().ContainSingle().Which;

        pair.SourceIndexes.Should().HaveCount(2);
    }

    private static EntityRef Entity(int id) => Entities[id].Kind switch
    {
        EntityKind.Type => new TypeRef { CanonicalName = Entities[id].JoinKey, Fidelity = MatchFidelity.Exact },
        EntityKind.Module => new ModuleRef { SimpleName = Entities[id].JoinKey, Fidelity = MatchFidelity.Exact },
        _ => new ThreadRef { OsThreadId = uint.Parse(Entities[id].JoinKey), Fidelity = MatchFidelity.Exact },
    };

    private static Observation Fact(string type, params EntityRef[] subjects) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = type,
        Subjects = subjects,
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = new Dictionary<string, Measure>(),
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test"),
            AnalyzerKey = "Test",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static CompactRow Row(params object?[] cells) => new(cells);

    private static CompactTable Table(string title, IReadOnlyList<CompactRow> rows) =>
        new(title, [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("Module", EntityColumn.Module)], rows);

    private static IReadOnlyList<ReportDomainSection> Domains(params CompactTable[] tables)
    {
        var section = new AnalyzerDetailSection("Memory Analysis", "Memory", 0, []) { CompactTables = tables };
        return [new ReportDomainSection("Memory", null, [section], [])];
    }

    private static AnalysisReportDocument Document(IReadOnlyList<ReportDomainSection> domains, EntityRelations? relations = null) =>
        new SingleDumpReportDocument { DumpPath = "x", Domains = domains, Entities = Entities, Relations = relations };
}
