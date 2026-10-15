using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class HtmlSegmentWiringTests
{
    private static SingleDumpReportDocument DocumentWithATable()
    {
        var table = new CompactTable("T", [new CompactHeader("Size", "bytes")], [new CompactRow([1L]), new CompactRow([2L])]);
        var section = new AnalyzerDetailSection("Analyzer", "Title", 0, [], "A1") { CompactTables = [table] };
        return new SingleDumpReportDocument { DumpPath = "C:/dumps/Crash.dmp", Domains = [new ReportDomainSection("Memory", null, [section], [])] };
    }

    [Fact]
    public void Render_ClientReport_EmitsAManifestAndOneElementPerSegment()
    {
        string html = new HtmlReportRenderer().Render(DocumentWithATable());

        html.Should().Contain("id=\"dd-manifest\"").And.Contain("id=\"dd-seg-s-A1\"");
        html.IndexOf("id=\"report-json\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("id=\"dd-manifest\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ClientReport_LeavesTheSectionBodyOutOfTheCore()
    {
        string html = new HtmlReportRenderer().Render(DocumentWithATable());

        int coreStart = html.IndexOf("id=\"report-json\"", StringComparison.Ordinal);
        string core = html.Substring(coreStart, html.IndexOf("</script>", coreStart, StringComparison.Ordinal) - coreStart);
        core.Should().Contain("\"segmentId\":\"s-A1\"").And.NotContain("compactTables");
    }

    [Fact]
    public void Render_ReportWithNothingLazy_EmitsNoSegmentElements()
    {
        var document = new SingleDumpReportDocument { DumpPath = "C:/dumps/Crash.dmp" };

        new HtmlReportRenderer().Render(document).Should().NotContain("<script id=\"dd-manifest\" type=");
    }
}
