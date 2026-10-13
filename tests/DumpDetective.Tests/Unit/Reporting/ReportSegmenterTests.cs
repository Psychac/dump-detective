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

    [Fact]
    public void Segment_TrendReport_IsReturnedUnchanged()
    {
        var trend = new TrendReportDocument();

        SegmentedReport segmented = ReportSegmenter.Segment(trend);

        segmented.Core.Should().BeSameAs(trend);
        segmented.Segments.Should().BeEmpty();
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
}
