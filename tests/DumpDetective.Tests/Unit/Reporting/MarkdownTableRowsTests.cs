using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class MarkdownTableRowsTests
{
    private static string Render(int rowCount)
    {
        var rows = Enumerable.Range(0, rowCount).Select(i => new CompactRow([$"row{i}", (long)i])).ToArray();
        var table = new CompactTable("Sample", [new CompactHeader("Name"), new CompactHeader("Size", "bytes")], rows);
        var section = new AnalyzerDetailSection("Analyzer", "Title", 0, [], "A1") { CompactTables = [table] };
        return new MarkdownCanonicalReportFormatter().Render(new SingleDumpReportDocument { DumpPath = "C:/d.dmp", AnalyzerSections = [section] });
    }

    [Fact]
    public void Render_SmallTable_PrintsEveryRowAndNoNote()
    {
        string md = Render(100);

        md.Should().Contain("row0").And.Contain("row99");
        md.Should().NotContain("Showing");
    }

    [Fact]
    public void Render_LargeTable_PrintsTheFirstHundredRowsAndStatesTheTotal()
    {
        string md = Render(68_576);

        md.Should().Contain("row99").And.NotContain("row100 ");
        md.Should().Contain("*Showing 100 of 68,576 rows; the full table is in the HTML and JSON reports.*");
    }

    private static string RenderWithTotal(int rows, int total, bool sampled)
    {
        var table = new CompactTable("Sample", [new CompactHeader("Name")], Enumerable.Range(0, rows).Select(i => new CompactRow([$"r{i}"])).ToArray(), TotalRows: total, Sampled: sampled);
        var section = new AnalyzerDetailSection("Analyzer", "Title", 0, [], "A1") { CompactTables = [table] };
        return new MarkdownCanonicalReportFormatter().Render(new SingleDumpReportDocument { DumpPath = "C:/d.dmp", AnalyzerSections = [section] });
    }

    [Fact]
    public void Render_TableCutByTheAnalyzer_SaysNOfMShown() =>
        RenderWithTotal(15, 800, sampled: false).Should().Contain("*15 of 800 shown.*");

    [Fact]
    public void Render_SampledTable_SaysSampledFrom() =>
        RenderWithTotal(68, 5_000, sampled: true).Should().Contain("*68 rows sampled from 5,000.*");

    [Fact]
    public void Render_TableWhoseTotalEqualsItsRows_SaysNothing() =>
        RenderWithTotal(15, 15, sampled: false).Should().NotContain("shown").And.NotContain("sampled");
}
