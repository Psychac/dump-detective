using System.Text.Json;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ReportSegmenterTests
{
    private static CompactTable TableOfRows(string title, int rowCount)
    {
        var rows = new List<CompactRow>(rowCount);
        for (int row = 0; row < rowCount; row++)
            rows.Add(new CompactRow([row, (long)row * 10, new UnknownCell(UnknownReason.NotApplicable)]));

        return new CompactTable(
            title,
            [new CompactHeader("Name", "string"), new CompactHeader("Size", "bytes"), new CompactHeader("Disposed")],
            rows);
    }

    private static AnalyzerDetailSection Section(string sectionId, params CompactTable[] tables) =>
        new("Analyzer " + sectionId, "Title " + sectionId, 0, [new TextBlock("narrative")], sectionId) { CompactTables = tables };

    private static SingleDumpReportDocument Document(params AnalyzerDetailSection[] sections) =>
        new()
        {
            DumpPath = "C:/dumps/Crash.dmp",
            Domains = [new ReportDomainSection("Memory", null, sections, [])],
        };

    private static JsonElement Parse(ReportSegment segment) => JsonDocument.Parse(segment.Json).RootElement;

    [Fact]
    public void Segment_SectionWithTables_MovesTheBodyOutAndKeepsTheHeaderInTheCore()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", TableOfRows("Small", 3))));

        AnalyzerDetailSection core = segmented.Core.Domains![0].Sections[0];
        core.SegmentId.Should().Be("s-A1");
        core.CompactTables.Should().BeNull();
        core.SectionId.Should().Be("A1");
        core.DisplayTitle.Should().Be("Title A1");
        core.Blocks.Should().ContainSingle().Which.Should().Be(new TextBlock("narrative"));
        segmented.Segments.Should().ContainSingle().Which.Kind.Should().Be(ReportSegmentKind.Section);
    }

    [Fact]
    public void Segment_SmallTable_TravelsInsideItsSectionSegment()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", TableOfRows("Small", ReportSegmenter.LazyTableRowThreshold))));

        JsonElement table = Parse(segmented.Segments.Single(segment => segment.Id == "s-A1")).GetProperty("compactTables")[0];
        table.GetProperty("rows").GetArrayLength().Should().Be(ReportSegmenter.LazyTableRowThreshold);
        table.TryGetProperty("segmentId", out _).Should().BeFalse();
        segmented.Segments.Should().NotContain(segment => segment.Kind == ReportSegmentKind.Table);
    }

    [Fact]
    public void Segment_TableOverTheThreshold_BecomesAColumnSegmentAndLeavesAStubInTheSection()
    {
        int rowCount = ReportSegmenter.LazyTableRowThreshold + 1;
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", TableOfRows("Small", 2), TableOfRows("Large", rowCount))));

        JsonElement stub = Parse(segmented.Segments.Single(segment => segment.Id == "s-A1")).GetProperty("compactTables")[1];
        stub.GetProperty("rowCount").GetInt32().Should().Be(rowCount);
        stub.GetProperty("segmentId").GetString().Should().Be("t-A1-1");
        stub.GetProperty("rows").GetArrayLength().Should().Be(0);

        JsonElement table = Parse(segmented.Segments.Single(segment => segment.Id == "t-A1-1"));
        table.GetProperty("rows").GetInt32().Should().Be(rowCount);
        JsonElement size = table.GetProperty("cols")[1];
        size.GetProperty("name").GetString().Should().Be("Size");
        size.GetProperty("values").GetArrayLength().Should().Be(rowCount);
        size.GetProperty("values")[5].GetInt64().Should().Be(50);
        size.GetProperty("stats").GetProperty("max").GetDouble().Should().Be((rowCount - 1) * 10);
        table.GetProperty("cols")[2].GetProperty("nullReason").GetString().Should().Be("na");
    }

    [Fact]
    public void Segment_ColumnSegment_WritesAMixedReasonColumnAsAPerRowMap()
    {
        var rows = new List<CompactRow>();
        for (int row = 0; row <= ReportSegmenter.LazyTableRowThreshold; row++)
            rows.Add(new CompactRow([row % 3 == 0 ? new UnknownCell(UnknownReason.Ambiguous) : row % 3 == 1 ? new UnknownCell(UnknownReason.NotApplicable) : "yes"]));

        SegmentedReport segmented = ReportSegmenter.Segment(
            Document(Section("A1", new CompactTable("Mixed", [new CompactHeader("Flag")], rows))));

        JsonElement column = Parse(segmented.Segments.Single(segment => segment.Kind == ReportSegmentKind.Table)).GetProperty("cols")[0];
        column.TryGetProperty("nullReason", out _).Should().BeFalse();
        column.GetProperty("nulls").GetProperty("0").GetString().Should().Be("amb");
        column.GetProperty("values")[0].ValueKind.Should().Be(JsonValueKind.Null);
        column.GetProperty("nulls").GetProperty("1").GetString().Should().Be("na");
        column.GetProperty("values")[2].GetString().Should().Be("yes");
    }

    [Fact]
    public void Segment_SectionWithNothingLazy_StaysInTheCoreUntouched()
    {
        AnalyzerDetailSection bare = new("Analyzer", "Title", 0, [new TextBlock("only prose")], "A9");

        SegmentedReport segmented = ReportSegmenter.Segment(Document(bare));

        segmented.Core.Domains![0].Sections[0].Should().BeSameAs(bare);
        segmented.Segments.Should().BeEmpty();
    }

    [Fact]
    public void Segment_EntityData_MovesIntoItsOwnSegmentsAndOutOfTheCore()
    {
        SingleDumpReportDocument document = Document(Section("A1", TableOfRows("T", 2))) with
        {
            Entities = [new EntityRecord(0, EntityKind.Type, "System.String", MatchFidelity.Exact)],
        };

        SegmentedReport segmented = ReportSegmenter.Segment(document);

        segmented.Core.Entities.Should().BeNull();
        JsonElement entities = Parse(segmented.Segments.Single(segment => segment.Id == ReportSegmenter.EntitiesSegmentId));
        entities.GetArrayLength().Should().Be(1);
        entities[0].GetProperty("joinKey").GetString().Should().Be("System.String");
        segmented.Segments.Should().NotContain(segment => segment.Id == ReportSegmenter.EntityDossierSegmentId);
    }

    [Fact]
    public void Segment_ReportWithoutEntities_AddsNoEntitySegments()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", TableOfRows("T", 2))));

        segmented.Segments.Should().NotContain(segment => segment.Kind == ReportSegmentKind.Entities || segment.Kind == ReportSegmentKind.EntityDossier);
    }

    [Fact]
    public void Segment_TwoSectionsSharingAnId_GetDistinctSegmentIds()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", TableOfRows("T", 2)), Section("A1", TableOfRows("T", 2))));

        segmented.Segments.Select(segment => segment.Id).Should().Equal("s-A1", "s-A1~2");
    }

    [Fact]
    public void Segment_SectionWithoutAnId_GetsAPositionalFallbackId()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("", TableOfRows("T", 2))));

        segmented.Core.Domains![0].Sections[0].SegmentId.Should().Be("s-section");
    }

    [Fact]
    public void Segment_SameReportTwice_ProducesIdenticalSegments()
    {
        SingleDumpReportDocument document = Document(Section("A1", TableOfRows("Large", 300)), Section("B2", TableOfRows("Small", 4)));

        IReadOnlyList<ReportSegment> first = ReportSegmenter.Segment(document).Segments;
        IReadOnlyList<ReportSegment> second = ReportSegmenter.Segment(document).Segments;

        second.Should().Equal(first);
    }

    [Fact]
    public void Segment_DoesNotMutateTheInputDocument()
    {
        SingleDumpReportDocument document = Document(Section("A1", TableOfRows("Large", 300)));
        AnalyzerDetailSection original = document.Domains![0].Sections[0];

        ReportSegmenter.Segment(document);

        document.Domains![0].Sections[0].Should().BeSameAs(original);
        original.CompactTables.Should().ContainSingle().Which.Rows.Should().HaveCount(300);
        original.SegmentId.Should().BeNull();
    }

    private static TrendReportDocument TrendWithATimelineSection(int rows)
    {
        var tableBlock = new TableBlock("Metric timeline", ["Metric", "Dump 1"], Enumerable.Range(0, rows).Select(i => new TableRow([new TableCell($"m{i}"), new TableCell("1", 1)])).ToArray());
        var section = new AnalyzerDetailSection("Trend", "Metric Timeline", 0, [new TextBlock("intro"), new HeadingBlock("Heading", 0), tableBlock], "T4");
        return new TrendReportDocument { TrendAnalyzerSections = [section] };
    }

    [Fact]
    public void Segment_TrendReportWithNothingToSegment_HasNoSegments()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(new TrendReportDocument());

        segmented.Core.Should().BeOfType<TrendReportDocument>();
        segmented.Segments.Should().BeEmpty();
    }

    [Fact]
    public void Segment_TrendSection_MovesTableBlockRowsOutAndKeepsTheBlocksInTheCore()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(TrendWithATimelineSection(rows: 500));

        AnalyzerDetailSection core = ((TrendReportDocument)segmented.Core).TrendAnalyzerSections.Single();
        core.SegmentId.Should().Be("s-T4");
        core.Blocks.Should().HaveCount(3);
        core.Blocks[1].Should().Be(new HeadingBlock("Heading", 0));
        ((TableBlock)core.Blocks[2]).Rows.Should().BeEmpty();
        ((TableBlock)core.Blocks[2]).Headers.Should().Equal("Metric", "Dump 1");

        JsonElement payload = Parse(segmented.Segments.Single(segment => segment.Id == "s-T4"));
        JsonElement entry = payload.GetProperty("blockTableRows")[0];
        entry.GetProperty("blockIndex").GetInt32().Should().Be(2);
        entry.GetProperty("rows").GetArrayLength().Should().Be(500);
        entry.GetProperty("rows")[3].GetProperty("cells")[0].GetProperty("display").GetString().Should().Be("m3");
    }

    [Fact]
    public void Segment_TrendSectionWithoutTableBlocks_StaysInTheCore()
    {
        var section = new AnalyzerDetailSection("Trend", "Overview", 0, [new TextBlock("only prose")], "T5");
        SegmentedReport segmented = ReportSegmenter.Segment(new TrendReportDocument { TrendAnalyzerSections = [section] });

        ((TrendReportDocument)segmented.Core).TrendAnalyzerSections.Single().Should().BeSameAs(section);
        segmented.Segments.Should().BeEmpty();
    }

    [Fact]
    public void Segment_TrendReport_DoesNotMutateTheInputSection()
    {
        TrendReportDocument trend = TrendWithATimelineSection(rows: 5);
        TableBlock original = (TableBlock)trend.TrendAnalyzerSections[0].Blocks[2];

        ReportSegmenter.Segment(trend);

        original.Rows.Should().HaveCount(5);
        trend.TrendAnalyzerSections[0].SegmentId.Should().BeNull();
    }

    [Fact]
    public void ColumnSegmentCells_AreWrittenWithTheSameBytesAsRowCells()
    {
        var entity = new EntityCell(4, "Exact.Name");
        var rows = new List<CompactRow>();
        for (int row = 0; row <= ReportSegmenter.LazyTableRowThreshold; row++)
            rows.Add(new CompactRow([entity, 83.11189368948767]));

        SegmentedReport segmented = ReportSegmenter.Segment(
            Document(Section("A1", new CompactTable("T", [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("Pct", "percent")], rows))));

        JsonElement cols = Parse(segmented.Segments.Single(segment => segment.Kind == ReportSegmentKind.Table)).GetProperty("cols");
        cols[0].GetProperty("values")[0][0].GetInt32().Should().Be(4);
        cols[0].GetProperty("values")[0][1].GetString().Should().Be("Exact.Name");
        cols[1].GetProperty("values")[0].GetDouble().Should().Be(83.112);
    }

    [Fact]
    public void Segment_LargeTableWithATotal_KeepsTheTotalAndSampledFlagOnItsStub()
    {
        CompactTable big = TableOfRows("Large", ReportSegmenter.LazyTableRowThreshold + 1) with { TotalRows = 9_999, Sampled = true };

        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", big)));

        JsonElement stub = Parse(segmented.Segments.Single(segment => segment.Id == "s-A1")).GetProperty("compactTables")[0];
        stub.GetProperty("totalRows").GetInt32().Should().Be(9_999);
        stub.GetProperty("sampled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Segment_TableWithoutATotal_WritesNeitherField()
    {
        SegmentedReport segmented = ReportSegmenter.Segment(Document(Section("A1", TableOfRows("Small", 3))));

        JsonElement table = Parse(segmented.Segments.Single(segment => segment.Id == "s-A1")).GetProperty("compactTables")[0];
        table.TryGetProperty("totalRows", out _).Should().BeFalse();
        table.TryGetProperty("sampled", out _).Should().BeFalse();
    }

    private static TrendReportDocument TrendWithDumps(params SingleDumpReportDocument[] dumps) =>
        new() { PerDumpDocuments = dumps };

    [Fact]
    public void Segment_PerDumpDocuments_AreSegmentedWithTheDumpIndexInEveryId()
    {
        var trend = TrendWithDumps(Document(Section("A1", TableOfRows("T", 300))), Document(Section("A1", TableOfRows("T", 300))));

        SegmentedReport segmented = ReportSegmenter.Segment(trend);

        segmented.Segments.Select(segment => segment.Id).Should().BeEquivalentTo("s-d0-A1", "t-d0-A1-0", "s-d1-A1", "t-d1-A1-0");
        var dumps = ((TrendReportDocument)segmented.Core).PerDumpDocuments;
        dumps.Select(dump => dump.Domains![0].Sections[0].SegmentId).Should().Equal("s-d0-A1", "s-d1-A1");
        dumps[0].Domains![0].Sections[0].CompactTables.Should().BeNull();
    }

    [Fact]
    public void Segment_PerDumpDocument_KeepsItsHeaderFieldsInTheCore()
    {
        var dump = Document(Section("A1", TableOfRows("T", 3))) with { DumpPath = "C:/dumps/a.dmp" };

        var core = (TrendReportDocument)ReportSegmenter.Segment(TrendWithDumps(dump)).Core;

        ((SingleDumpReportDocument)core.PerDumpDocuments[0]).DumpPath.Should().Be("C:/dumps/a.dmp");
        core.PerDumpDocuments[0].Domains![0].Sections[0].DisplayTitle.Should().Be("Title A1");
    }

    [Fact]
    public void Segment_PerDumpDocumentWithoutDomains_PassesThrough()
    {
        var dump = new SingleDumpReportDocument { DumpPath = "C:/dumps/a.dmp" };

        var core = (TrendReportDocument)ReportSegmenter.Segment(TrendWithDumps(dump)).Core;

        core.PerDumpDocuments[0].Should().BeSameAs(dump);
    }

    [Fact]
    public void Segment_TrendSectionAndPerDumpSectionWithTheSameId_GetDistinctSegmentIds()
    {
        var trendSection = new AnalyzerDetailSection("Trend", "Timeline", 0, [new TableBlock("t", ["a"], [new TableRow([new TableCell("x")])])], "A1");
        var trend = new TrendReportDocument { TrendAnalyzerSections = [trendSection], PerDumpDocuments = [Document(Section("A1", TableOfRows("T", 3)))] };

        SegmentedReport segmented = ReportSegmenter.Segment(trend);

        segmented.Segments.Select(segment => segment.Id).Should().OnlyHaveUniqueItems();
    }
}
