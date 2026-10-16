using System.Text.Json;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Trend;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class TrendMetricTimelineSectionBuilderTests
{
    private static AnalysisSnapshot Snapshot(int index) =>
        new(index, $"dump{index}.dmp", [], [], new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);

    private static AnalyzerMetricTimeline Timeline(string analyzer, int metrics, string keyPrefix = "m") =>
        new(analyzer, Enumerable.Range(0, metrics)
            .Select(i => new MetricTimelinePoint($"{keyPrefix}.{i}", "count", MetricTrendDirection.HigherIsWorse, [10 + i, 20 + i, 30 + i * 2]))
            .ToArray());

    private static AnalyzerDetailSection Build(IReadOnlyList<AnalyzerMetricTimeline> headline, IReadOnlyList<AnalyzerMetricTimeline> scoped)
    {
        AnalysisSnapshot[] snapshots = [Snapshot(0), Snapshot(1), Snapshot(2)];
        var data = new TrendReportData(
            Steps: [[]], Overall: [], Timeline: headline, ScopedTimeline: scoped, Snapshots: snapshots,
            NewFindings: [], PersistentFindings: [], ResolvedFindings: [], SeverityChanges: Array.Empty<FindingSeverityChange>());
        return TrendMetricTimelineSectionBuilder.Build(data, snapshots);
    }

    [Fact]
    public void Build_PutsTablesInCompactTablesAndLeavesAReferenceInTheNarrative()
    {
        AnalyzerDetailSection section = Build([Timeline("Memory Analysis", 3)], []);

        section.Blocks.OfType<TableBlock>().Should().BeEmpty();
        section.CompactTables.Should().ContainSingle();
        section.Blocks.OfType<CompactTableRefBlock>().Should().ContainSingle().Which.TableIndex.Should().Be(0);
        section.CompactTables![0].Title.Should().Be("Memory Analysis metric timeline");
    }

    [Fact]
    public void Build_KeepsEachTableBetweenItsOwnHeadings()
    {
        AnalyzerDetailSection section = Build(
            [Timeline("Memory Analysis", 2), Timeline("Thread Analysis", 2)],
            [Timeline("Memory Analysis", 2, "type.bytes")]);

        List<string> shape = section.Blocks.Select(block => block switch
        {
            HeadingBlock heading => "H:" + heading.Text,
            CompactTableRefBlock reference => "T" + reference.TableIndex,
            TextBlock text => "X:" + text.Text,
            _ => string.Empty,
        }).Where(label => label.Length > 0).ToList();

        int memoryHeading = shape.IndexOf("H:Memory Analysis");
        int threadHeading = shape.IndexOf("H:Thread Analysis");
        memoryHeading.Should().BeGreaterThanOrEqualTo(0);
        threadHeading.Should().BeGreaterThan(memoryHeading);
        shape.Skip(memoryHeading).Take(threadHeading - memoryHeading).Count(label => label.StartsWith('T')).Should().BeGreaterThan(0, "a table follows its analyzer's heading");
        shape.Where(label => label.StartsWith('T')).Select(label => int.Parse(label[1..])).Should().Equal(Enumerable.Range(0, section.CompactTables!.Count));
    }

    [Fact]
    public void Build_Headers_TypeTheCellsTheViewerSortsAndDraws()
    {
        CompactTable table = Build([Timeline("Memory Analysis", 3)], []).CompactTables!.Single();

        table.Headers.Select(h => h.Name).Should().Equal("Metric", "Trend (3)", "Dump 1", "Dump 2", "Dump 3", "Δ", "Δ%", "Pattern", "Status");
        table.Headers.Select(h => h.Type).Should().Equal("string", "string", "number", "number", "number", "number", "number", "string", "number");
        table.Headers[1].Sortable.Should().BeFalse();
        table.Headers.Where((_, i) => i != 1).Should().OnlyContain(h => h.Sortable);
    }

    [Fact]
    public void Build_Row_KeepsTheLinkSparklineAndSortValues()
    {
        CompactRow row = Build([Timeline("Memory Analysis", 1)], []).CompactTables!.Single().Rows[0];

        row.Values[0].Should().BeOfType<TableCell>().Which.LinkTarget.Should().StartWith("detail-");
        row.Values[1].Should().BeOfType<string>().Which.Should().StartWith("__SPARK__");
        row.Values[2].Should().BeOfType<TableCell>().Which.RawValue.Should().Be(10);
        row.Values[^2].Should().BeOfType<string>("a cell with only text is that text");
        row.Values[^1].Should().BeOfType<TableCell>().Which.Display.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Build_NoMetrics_HasNoTablesAndNoReferences()
    {
        AnalyzerDetailSection section = Build([], []);

        section.CompactTables.Should().BeNull();
        section.Blocks.OfType<CompactTableRefBlock>().Should().BeEmpty();
    }

    [Fact]
    public void Segment_LargeTimelineTable_BecomesAColumnSegmentAndItsReferenceStaysInTheCore()
    {
        // Scoped tables hold one metric key with a row per scope, so 300 scopes of one key make one 300-row table.
        var scoped = new AnalyzerMetricTimeline("Memory Analysis", Enumerable.Range(0, 300)
            .Select(i => new MetricTimelinePoint("type.bytes", "bytes", MetricTrendDirection.HigherIsWorse, [10 + i, 20 + i, 30 + i], Scope: $"Type{i}")).ToArray());
        AnalyzerDetailSection t4 = Build([Timeline("Memory Analysis", 3)], [scoped]);
        int bigIndex = t4.CompactTables!.ToList().FindIndex(t => t.Rows.Count == 300);
        var trend = new TrendReportDocument { TrendAnalyzerSections = [t4] };

        SegmentedReport segmented = ReportSegmenter.Segment(trend);

        AnalyzerDetailSection core = ((TrendReportDocument)segmented.Core).TrendAnalyzerSections.Single();
        core.CompactTables.Should().BeNull();
        core.Blocks.OfType<CompactTableRefBlock>().Should().HaveCount(t4.CompactTables!.Count);

        ReportSegment tableSegment = segmented.Segments.Single(s => s.Kind == ReportSegmentKind.Table);
        using JsonDocument table = JsonDocument.Parse(tableSegment.Json);
        table.RootElement.GetProperty("rows").GetInt32().Should().Be(300);
        table.RootElement.GetProperty("cols")[2].GetProperty("values")[0].GetProperty("display").GetString().Should().NotBeNullOrEmpty();

        using JsonDocument sectionPayload = JsonDocument.Parse(segmented.Segments.Single(s => s.Id == "s-T4").Json);
        sectionPayload.RootElement.GetProperty("compactTables")[bigIndex].GetProperty("segmentId").GetString().Should().Be(tableSegment.Id);
    }

    [Fact]
    public void Row_WithCellRecords_WritesAndReadsThemBack()
    {
        var row = new CompactRow(["plain", new TableCell("12 items", 12, "detail-1"), new UnknownCell(UnknownReason.NotApplicable)]);

        string json = JsonSerializer.Serialize(row, DumpDetective.Reporting.Serialization.ReportJsonContext.Default.CompactRow);
        CompactRow back = JsonSerializer.Deserialize(json, DumpDetective.Reporting.Serialization.ReportJsonContext.Default.CompactRow)!;

        back.Values[0].Should().Be("plain");
        back.Values[1].Should().Be(new TableCell("12 items", 12, "detail-1"));
        back.Values[2].Should().Be(new UnknownCell(UnknownReason.NotApplicable));
    }
}
