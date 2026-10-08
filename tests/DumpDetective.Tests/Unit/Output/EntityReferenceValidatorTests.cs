using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class EntityReferenceValidatorTests
{
    private static readonly EntityRecord[] Entities =
    [
        new(0, EntityKind.Type, "System.Data.DataColumn", MatchFidelity.Exact),
        new(1, EntityKind.Thread, "24088", MatchFidelity.Exact),
    ];

    [Fact]
    public void Validate_ConsistentDocument_HasNoProblems()
    {
        AnalysisReportDocument document = Document(typeCell: "System.Data.DataColumn", threadCell: 24088d, summary: GoodSummary());

        EntityReferenceValidator.Validate(document).Should().BeEmpty();
    }

    [Fact]
    public void Validate_DocumentWithoutEntities_HasNothingToCheck()
    {
        EntityReferenceValidator.Validate(new SingleDumpReportDocument { DumpPath = "x" }).Should().BeEmpty();
    }

    [Fact]
    public void Validate_NamesWithNoEntityAndMissingCells_AreNotProblems()
    {
        EntityReferenceValidator.Validate(Document(typeCell: "SqlCommand", threadCell: "—", summary: GoodSummary())).Should().BeEmpty();
        EntityReferenceValidator.Validate(Document(typeCell: null, threadCell: null, summary: GoodSummary())).Should().BeEmpty();
    }

    [Fact]
    public void Validate_EntityTableWithWrongIds_IsReported()
    {
        AnalysisReportDocument document = Document("System.Data.DataColumn", 24088d, GoodSummary()) with
        {
            Entities = [new EntityRecord(0, EntityKind.Type, "A", MatchFidelity.Exact), new EntityRecord(5, EntityKind.Type, "B", MatchFidelity.Exact)],
        };

        EntityReferenceValidator.Validate(document).Should().Contain(p => p.Contains("row 1") && p.Contains("id 5"));
    }

    [Fact]
    public void Validate_SummaryPointingOutsideItsLists_IsReportedPerKindOfReference()
    {
        EntitySummary broken = new(
            [new FactKindRecord("a", "A", "Other")],
            [new MeasureRecord("m", "Count")],
            [
                new EntityFacts(0, [new FactGroup(9, 1, [new MeasureStat(7, 1, 1, 1)])], [4]),
                new EntityFacts(99, [], []),
            ],
            ["claim-1", "not-a-finding"]);

        IReadOnlyList<string> problems = EntityReferenceValidator.Validate(Document("System.Data.DataColumn", 24088d, broken));

        problems.Should().Contain(p => p.Contains("kind index 9"));
        problems.Should().Contain(p => p.Contains("measure index 7"));
        problems.Should().Contain(p => p.Contains("claim index 4"));
        problems.Should().Contain(p => p.Contains("entity 99"));
        problems.Should().Contain(p => p.Contains("'not-a-finding'"));
        problems.Should().NotContain(p => p.Contains("'claim-1'"), "claim-1 is a finding in the document");
    }

    [Fact]
    public void Validate_TableCellPointingAtTheWrongKindOrOutsideTheTable_IsReported()
    {
        AnalysisReportDocument wrongKind = Document(new EntityCell(1, null), threadCell: 24088d, summary: GoodSummary());
        AnalysisReportDocument outside = Document(new EntityCell(50, null), threadCell: 24088d, summary: GoodSummary());

        EntityReferenceValidator.Validate(wrongKind).Should().Contain(p => p.Contains("column holds Type entities") && p.Contains("Thread '24088'"));
        EntityReferenceValidator.Validate(outside).Should().Contain(p => p.Contains("entity 50") && p.Contains("'Top types'") && p.Contains("row 0"));
    }

    [Fact]
    public void Validate_BareNumberLeftInAnEntityColumn_IsReported()
    {
        EntityReferenceValidator.Validate(Document(typeCell: 7L, threadCell: 24088d, summary: GoodSummary()))
            .Should().Contain(p => p.Contains("bare Int64"));
    }

    [Fact]
    public void Validate_TwoEntitiesSharingAnAddress_IsReported()
    {
        AnalysisReportDocument document = Document("System.Core.dll", 24088d, GoodSummary()) with
        {
            Entities =
            [
                new EntityRecord(0, EntityKind.Module, "System.Core.dll", MatchFidelity.Exact),
                new EntityRecord(1, EntityKind.Module, "System.Core.dll", MatchFidelity.Exact),
                new EntityRecord(2, EntityKind.Type, "System.Core.dll", MatchFidelity.Exact),
            ],
        };

        IReadOnlyList<string> problems = EntityReferenceValidator.Validate(document);

        problems.Should().ContainSingle(p => p.Contains("Entities 0 and 1") && p.Contains("#entity/module/System.Core.dll"));
    }

    [Fact]
    public void Validate_EntityWithNoNameAndNoCellLinkingIt_IsNotAProblem()
    {
        AnalysisReportDocument document = Document("System.Data.DataColumn", 24088d, GoodSummary()) with
        {
            Entities = [.. Entities, new EntityRecord(2, EntityKind.Module, "", MatchFidelity.Exact)],
        };

        EntityReferenceValidator.Validate(document).Should().BeEmpty();
    }

    [Fact]
    public void Validate_CellLinkingAnEntityWithNoName_IsReported()
    {
        AnalysisReportDocument document = Document(new EntityCell(0, null), 24088d, GoodSummary()) with
        {
            Entities = [new EntityRecord(0, EntityKind.Type, "", MatchFidelity.Exact), Entities[1]],
        };

        EntityReferenceValidator.Validate(document).Should().Contain(p => p.Contains("no name to address") && p.Contains("row 0"));
    }

    [Fact]
    public void Validate_MethodColumnWithoutADeclaringTypeColumn_IsReported()
    {
        AnalysisReportDocument document = Document("System.Data.DataColumn", 24088d, GoodSummary());
        CompactTable table = new("Methods", [new CompactHeader("Method Name", EntityColumn.Method)], [new CompactRow(["OnTick"])]);
        AnalyzerDetailSection section = document.Domains![0].Sections[0] with { CompactTables = [table] };

        IReadOnlyList<string> problems = EntityReferenceValidator.Validate(document with { Domains = [document.Domains[0] with { Sections = [section] }] });

        problems.Should().ContainSingle(p => p.Contains("'Method Name'") && p.Contains("Declaring Type"));
    }

    private static EntitySummary GoodSummary() => new(
        [new FactKindRecord("leak.candidate-type", "Leak Candidate Analysis", "Size and population")],
        [new MeasureRecord("instance.count", "Count")],
        [new EntityFacts(0, [new FactGroup(0, 1, [new MeasureStat(0, 5, 5, 5)])], [0])],
        ["claim-1"]);

    private static AnalysisReportDocument Document(object? typeCell, object? threadCell, EntitySummary summary)
    {
        var table = new CompactTable(
            "Top types",
            [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("OS Thread", EntityColumn.Thread)],
            [new CompactRow([typeCell, threadCell])]);
        var section = new AnalyzerDetailSection("Memory Analysis", "Memory Analysis", 0, []) { CompactTables = [table] };
        var claim = new FindingRecord("claim-1", "Stub Analyzer", "Leak", "Warning", "title", ["evidence"], "rec", []);
        return new SingleDumpReportDocument
        {
            DumpPath = "x",
            Domains = [new ReportDomainSection("Memory", null, [section], [])],
            Findings = [claim],
            Entities = Entities,
            EntitySummary = summary,
        };
    }
}
