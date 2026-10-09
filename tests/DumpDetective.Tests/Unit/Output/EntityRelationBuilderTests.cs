using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Identity;

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

        EntityRelations? relations = EntityRelationBuilder.Build(domains);

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

        EntityRelations? relations = EntityRelationBuilder.Build(domains);

        relations!.Pairs.Should().ContainSingle().Which.SourceIndexes.Should().Equal(0, 1);
        relations.Sources.Should().Equal("Memory Analysis / Top types", "Memory Analysis / Type table");
    }

    [Fact]
    public void Build_IgnoresTextCellsSelfPairsAndSingleEntityColumnTables()
    {
        var domains = Domains(
            Table("Mixed", [Row("plain text", new EntityCell(2, null)), Row(new EntityCell(0, null), new EntityCell(0, null))]),
            new CompactTable("One column", [new CompactHeader("Type", EntityColumn.Type)], [new CompactRow([new EntityCell(0, null)])]));

        EntityRelationBuilder.Build(domains).Should().BeNull("no row has two different entities");
    }

    [Fact]
    public void Build_IsOrderedByEntityIdsSoTwoRunsAgree()
    {
        var domains = Domains(Table("Top types", [
            Row(new EntityCell(1, null), new EntityCell(3, null)),
            Row(new EntityCell(0, null), new EntityCell(3, null)),
            Row(new EntityCell(0, null), new EntityCell(2, null)),
        ]));

        EntityRelationBuilder.Build(domains)!.Pairs.Select(p => (p.First, p.Second)).Should().Equal((0, 2), (0, 3), (1, 3));
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
