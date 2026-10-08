using System.Text.Json;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class EntityCellTests
{
    private const string ClosureShown = "App.Worker+<>c__DisplayClass132_0";
    private const string ClosureEntity = "App.Worker+<>c__DisplayClass";

    private static readonly EntityRecord[] Entities =
    [
        new(0, EntityKind.Type, "System.Data.DataColumn", MatchFidelity.Exact),
        new(1, EntityKind.Type, ClosureEntity, MatchFidelity.Low),
    ];

    [Fact]
    public void Apply_ExactName_BecomesTheBareEntityId()
    {
        AnalysisReportDocument rewritten = EntityCellPass.Apply(DocumentWithRows(["System.Data.DataColumn", 5L]));

        FirstRow(rewritten).Values[0].Should().Be(new EntityCell(0, null));
        FirstRow(rewritten).Values[1].Should().Be(5L);
    }

    [Fact]
    public void Apply_MergedName_KeepsTheExactNameBesideTheId()
    {
        AnalysisReportDocument rewritten = EntityCellPass.Apply(DocumentWithRows([ClosureShown, 1L]));

        FirstRow(rewritten).Values[0].Should().Be(new EntityCell(1, ClosureShown));
    }

    [Fact]
    public void Apply_NameWithoutAnEntity_StaysALiteral()
    {
        AnalysisReportDocument rewritten = EntityCellPass.Apply(DocumentWithRows(["SqlCommand", 1L]));

        FirstRow(rewritten).Values[0].Should().Be("SqlCommand");
    }

    [Fact]
    public void Apply_NeverTouchesColumnsThatAreNotEntityColumns_OrTheOriginalDocument()
    {
        AnalysisReportDocument original = DocumentWithRows(["System.Data.DataColumn", 5L]);

        AnalysisReportDocument rewritten = EntityCellPass.Apply(original);

        FirstRow(original).Values[0].Should().Be("System.Data.DataColumn");
        FirstRow(rewritten).Values[0].Should().Be(new EntityCell(0, null));

        AnalysisReportDocument plain = DocumentWithRows(["System.Data.DataColumn", 5L], entityColumn: false);
        FirstRow(EntityCellPass.Apply(plain)).Values[0].Should().Be("System.Data.DataColumn");
    }

    [Fact]
    public void Apply_WithoutAnEntityTable_ChangesNothing()
    {
        AnalysisReportDocument withoutEntities = DocumentWithRows(["System.Data.DataColumn", 5L]) with { Entities = null };

        EntityCellPass.Apply(withoutEntities).Should().BeSameAs(withoutEntities);
    }

    [Fact]
    public void EntityCell_WritesABareIdOrAnIdAndNamePair_AndReadsBothBack()
    {
        JsonSerializer.Serialize(new EntityCell(7, null)).Should().Be("7");
        JsonSerializer.Serialize(new EntityCell(7, "Exact")).Should().Be("[7,\"Exact\"]");
        JsonSerializer.Deserialize<EntityCell>("7").Should().Be(new EntityCell(7, null));
        JsonSerializer.Deserialize<EntityCell>("[7,\"Exact\"]").Should().Be(new EntityCell(7, "Exact"));
    }

    [Fact]
    public void RenderedReportJson_CarriesEntityCellsInTheTable_ButMarkdownStillShowsNames()
    {
        AnalysisReportDocument document = DocumentWithRows(["System.Data.DataColumn", 5L], [ClosureShown, 6L]);

        string json = new JsonCanonicalReportFormatter().Render(document);
        AnalyzerDetailSection inMemorySection = document.Domains![0].Sections[0];
        string markdown = new MarkdownCanonicalReportFormatter().Render(document with { Domains = null, AnalyzerSections = [inMemorySection] });

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement rows = parsed.RootElement.GetProperty("domains")[0].GetProperty("sections")[0].GetProperty("compactTables")[0].GetProperty("rows");
        rows[0][0].GetInt32().Should().Be(0);
        rows[1][0][0].GetInt32().Should().Be(1);
        rows[1][0][1].GetString().Should().Be(ClosureShown);
        markdown.Should().Contain("System.Data.DataColumn").And.Contain(ClosureShown);
    }

    [Fact]
    public void StringPool_NeverPoolsALiteralInAnEntityColumn_ButStillPoolsTheSameTextElsewhere()
    {
        string repeated = "SqlCommand-with-a-long-enough-name-to-be-worth-pooling";
        var table = new CompactTable(
            "t",
            [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("Other")],
            [new CompactRow([repeated, repeated]), new CompactRow([repeated, repeated]), new CompactRow([repeated, repeated])]);
        var section = new AnalyzerDetailSection("A", "A", 0, []) { CompactTables = [table] };
        var domains = new[] { new ReportDomainSection("Memory", null, [section], []) };

        (IReadOnlyList<ReportDomainSection>? pooledDomains, _, IReadOnlyList<string>? pool) = ReportStringPool.Apply(domains, null);

        pool.Should().ContainSingle().Which.Should().Be(repeated);
        CompactRow row = pooledDomains![0].Sections[0].CompactTables![0].Rows[0];
        row.Values[0].Should().Be(repeated);
        row.Values[1].Should().Be(0);
    }

    [Fact]
    public void RenderedHtml_BundlesTheEntityPanelAndStartsIt()
    {
        string html = new HtmlReportRenderer().Render(DocumentWithRows(["System.Data.DataColumn", 5L]));

        html.Should().Contain("function setupEntityPanel").And.Contain("function openEntityPanel")
            .And.Contain("function decorateEntityCell").And.Contain("setupEntityPanel();")
            .And.Contain(".entity-panel");
    }

    [Fact]
    public void Apply_ThreadColumn_TurnsAnOsThreadNumberIntoTheThreadEntityId()
    {
        FirstRow(EntityCellPass.Apply(ThreadDocument(24088u, 5L))).Values[0].Should().Be(new EntityCell(2, null));
        FirstRow(EntityCellPass.Apply(ThreadDocument(24088d, 5L))).Values[0].Should().Be(new EntityCell(2, null), "the section builders hand over doubles");
        AnalysisReportDocument rewritten = EntityCellPass.Apply(ThreadDocument(24088u, 5L));

        FirstRow(rewritten).Values[1].Should().Be(5L);
    }

    [Fact]
    public void Apply_ThreadColumn_LeavesUnknownThreadsAndPlaceholdersAsTheyWere()
    {
        FirstRow(EntityCellPass.Apply(ThreadDocument(999d, 5L))).Values[0].Should().Be("999", "a bare number in an entity column would be read as an entity id");
        FirstRow(EntityCellPass.Apply(ThreadDocument("—", 5L))).Values[0].Should().Be("—");
        FirstRow(EntityCellPass.Apply(ThreadDocument(null, 5L))).Values[0].Should().BeNull();
    }

    [Fact]
    public void Apply_ThreadIdsAreNotMatchedAgainstTypeEntities()
    {
        // A type whose name is all digits must not capture a thread cell.
        EntityRecord[] entities = [new(0, EntityKind.Type, "24088", MatchFidelity.Exact)];

        AnalysisReportDocument rewritten = EntityCellPass.Apply(ThreadDocument(24088u, 5L) with { Entities = entities });

        FirstRow(rewritten).Values[0].Should().Be("24088");
    }

    [Fact]
    public void Apply_ModuleColumn_MatchesAFileNameDirectly()
    {
        FirstRow(EntityCellPass.Apply(ModuleDocument("mscorlib.dll"))).Values[0].Should().Be(new EntityCell(3, null));
    }

    [Fact]
    public void Apply_ModuleColumn_ShowingAFullPath_MatchesOnTheFileNameAndKeepsThePathAsTheExactText()
    {
        const string path = @"C:\Windows\Microsoft.Net\assembly\GAC_64\mscorlib\v4.0\mscorlib.dll";

        FirstRow(EntityCellPass.Apply(ModuleDocument(path))).Values[0].Should().Be(new EntityCell(3, path));
        FirstRow(EntityCellPass.Apply(ModuleDocument("C:/unix/style/mscorlib.dll"))).Values[0].Should().Be(new EntityCell(3, "C:/unix/style/mscorlib.dll"));
    }

    [Fact]
    public void Apply_ModuleColumn_LeavesTruncatedUnknownAndPlaceholderCellsAsText()
    {
        foreach (string shown in new[] { @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Temp...", "unknown.dll", "\u2014", "", @"C:\folder\" })
            FirstRow(EntityCellPass.Apply(ModuleDocument(shown))).Values[0].Should().Be(shown);
    }

    [Fact]
    public void Apply_ModuleColumn_EmptyCellStaysTextEvenWhenAnUnnamedModuleEntityExists()
    {
        EntityRecord[] entities = [.. ModuleDocument("").Entities!, new EntityRecord(4, EntityKind.Module, "", MatchFidelity.Exact)];

        FirstRow(EntityCellPass.Apply(ModuleDocument("") with { Entities = entities })).Values[0].Should().Be("");
    }

    [Fact]
    public void Apply_ModuleColumn_DoesNotMatchATypeOrAThreadWithTheSameText()
    {
        EntityRecord[] entities = [new(0, EntityKind.Type, "mscorlib.dll", MatchFidelity.Exact), new(1, EntityKind.Thread, "mscorlib.dll", MatchFidelity.Exact)];

        FirstRow(EntityCellPass.Apply(ModuleDocument("mscorlib.dll") with { Entities = entities })).Values[0].Should().Be("mscorlib.dll");
    }

    [Fact]
    public void Apply_MethodColumn_MatchesOnTheDeclaringTypeInTheSameRow_AndKeepsTheShownMethodName()
    {
        AnalysisReportDocument rewritten = EntityCellPass.Apply(MethodDocument("Serilog.PortableTimer", "OnTick"));

        object?[] row = FirstRow(rewritten).Values;
        row[0].Should().Be(new EntityCell(2, "OnTick"));
        row[1].Should().Be(new EntityCell(0, null));
    }

    [Fact]
    public void Apply_MethodColumn_SameNameOnAnotherType_MatchesThatTypesMethod()
    {
        FirstRow(EntityCellPass.Apply(MethodDocument("Serilog.PeriodicBatchingSink", "OnTick"))).Values[0].Should().Be(new EntityCell(3, "OnTick"));
    }

    [Fact]
    public void Apply_MethodColumn_LeavesUnknownMissingAndCutOffCellsAsText()
    {
        FirstRow(EntityCellPass.Apply(MethodDocument("Serilog.PortableTimer", "Elsewhere"))).Values[0].Should().Be("Elsewhere");
        FirstRow(EntityCellPass.Apply(MethodDocument("Serilog.Periodic...", "OnTick"))).Values[0].Should().Be("OnTick");
        FirstRow(EntityCellPass.Apply(MethodDocument("", "OnTick"))).Values[0].Should().Be("OnTick");
        FirstRow(EntityCellPass.Apply(MethodDocument("Serilog.PortableTimer", ""))).Values[0].Should().Be("");
    }

    [Fact]
    public void Apply_MethodColumn_WithoutADeclaringTypeColumn_StaysText()
    {
        AnalysisReportDocument document = MethodDocument("Serilog.PortableTimer", "OnTick");
        CompactTable table = document.Domains![0].Sections[0].CompactTables![0];
        CompactTable onlyMethod = table with { Headers = [table.Headers[0]], Rows = [new CompactRow(["OnTick"])] };
        AnalyzerDetailSection section = document.Domains[0].Sections[0] with { CompactTables = [onlyMethod] };

        FirstRow(EntityCellPass.Apply(document with { Domains = [document.Domains[0] with { Sections = [section] }] })).Values[0].Should().Be("OnTick");
    }

    [Fact]
    public void Apply_ObjectColumn_MatchesOnTheAddressAlone_AndKeepsTheShownAddress()
    {
        FirstRow(EntityCellPass.Apply(ObjectDocument("0x1E101A03098"))).Values[0].Should().Be(new EntityCell(0, "0x1E101A03098"));
        FirstRow(EntityCellPass.Apply(ObjectDocument("0x1e101a03098"))).Values[0].Should().Be(new EntityCell(0, "0x1e101a03098"));
    }

    [Fact]
    public void Apply_ObjectColumn_LeavesUnknownAndPlaceholderCellsAsText()
    {
        foreach (string shown in new[] { "0x1E101A03099", "(unknown)", "", "1E101A03098" })
            FirstRow(EntityCellPass.Apply(ObjectDocument(shown))).Values[0].Should().Be(shown);
    }

    [Fact]
    public void Apply_ObjectColumn_AddressSharedByObjectsOfDifferentDumps_IsNotLinked()
    {
        EntityRecord[] entities =
        [
            new(0, EntityKind.Object, @"D:.dmp:0x1E101A03098", MatchFidelity.Exact),
            new(1, EntityKind.Object, @"D:.dmp:0x1E101A03098", MatchFidelity.Exact),
        ];

        FirstRow(EntityCellPass.Apply(ObjectDocument("0x1E101A03098") with { Entities = entities })).Values[0].Should().Be("0x1E101A03098");
    }

    private static AnalysisReportDocument ObjectDocument(string shown)
    {
        var table = new CompactTable(
            "Active transactions",
            [new CompactHeader("Connection Address", EntityColumn.Object)],
            [new CompactRow([shown])]);
        var section = new AnalyzerDetailSection("SQL Transaction Analysis", "SQL", 0, []) { CompactTables = [table] };
        return new SingleDumpReportDocument
        {
            DumpPath = @"D:.dmp",
            Domains = [new ReportDomainSection("Infra", null, [section], [])],
            Entities = [new EntityRecord(0, EntityKind.Object, @"D:.dmp:0x1E101A03098", MatchFidelity.Exact)],
        };
    }

    private static AnalysisReportDocument MethodDocument(string declaringTypeShown, string methodShown)
    {
        var table = new CompactTable(
            "Suspended async methods",
            [new CompactHeader("Method Name", EntityColumn.Method), new CompactHeader("Declaring Type", EntityColumn.Type)],
            [new CompactRow([methodShown, declaringTypeShown])]);
        var section = new AnalyzerDetailSection("Async State Machine Analysis", "Async", 0, []) { CompactTables = [table] };
        return new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/Crash.dmp",
            Domains = [new ReportDomainSection("Async", null, [section], [])],
            Entities =
            [
                new EntityRecord(0, EntityKind.Type, "Serilog.PortableTimer", MatchFidelity.Exact),
                new EntityRecord(1, EntityKind.Type, "Serilog.PeriodicBatchingSink", MatchFidelity.Exact),
                new EntityRecord(2, EntityKind.Method, "Serilog.PortableTimer::OnTick", MatchFidelity.Low),
                new EntityRecord(3, EntityKind.Method, "Serilog.PeriodicBatchingSink::OnTick", MatchFidelity.Low),
            ],
        };
    }

    private static AnalysisReportDocument ModuleDocument(string shown)
    {
        var table = new CompactTable(
            "Modules by heap footprint",
            [new CompactHeader("Module", EntityColumn.Module), new CompactHeader("Objects", "number")],
            [new CompactRow([shown, 5L])]);
        var section = new AnalyzerDetailSection("Module Analysis", "Module Analysis", 0, []) { CompactTables = [table] };
        return new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/Crash.dmp",
            Domains = [new ReportDomainSection("Runtime", null, [section], [])],
            Entities =
            [
                new EntityRecord(0, EntityKind.Type, "System.Data.DataColumn", MatchFidelity.Exact),
                new EntityRecord(1, EntityKind.Thread, "24088", MatchFidelity.Exact),
                new EntityRecord(2, EntityKind.Module, "System.dll", MatchFidelity.Exact),
                new EntityRecord(3, EntityKind.Module, "mscorlib.dll", MatchFidelity.Exact),
            ],
        };
    }

    private static AnalysisReportDocument ThreadDocument(object? osThread, long count)
    {
        var table = new CompactTable(
            "Top blocked threads",
            [new CompactHeader("OS Thread", EntityColumn.Thread), new CompactHeader("Count", "number")],
            [new CompactRow([osThread, count])]);
        var section = new AnalyzerDetailSection("Thread Analysis", "Thread Analysis", 0, []) { CompactTables = [table] };
        return new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/Crash.dmp",
            Domains = [new ReportDomainSection("Threads", null, [section], [])],
            Entities =
            [
                new EntityRecord(0, EntityKind.Type, "System.Data.DataColumn", MatchFidelity.Exact),
                new EntityRecord(1, EntityKind.Thread, "11132", MatchFidelity.Exact),
                new EntityRecord(2, EntityKind.Thread, "24088", MatchFidelity.Exact),
            ],
        };
    }

    private static CompactRow FirstRow(AnalysisReportDocument document) => document.Domains![0].Sections[0].CompactTables![0].Rows[0];

    private static AnalysisReportDocument DocumentWithRows(object?[] firstRow, object?[]? secondRow = null, bool entityColumn = true)
    {
        var rows = new List<CompactRow> { new(firstRow) };
        if (secondRow is not null)
            rows.Add(new CompactRow(secondRow));

        var table = new CompactTable(
            "Top types",
            [new CompactHeader("Type", entityColumn ? EntityColumn.Type : "string"), new CompactHeader("Count", "number")],
            rows);
        var section = new AnalyzerDetailSection("Memory Analysis", "Memory Analysis", 0, []) { CompactTables = [table] };
        return new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/Crash.dmp",
            Domains = [new ReportDomainSection("Memory", null, [section], [])],
            Entities = Entities,
        };
    }
}
