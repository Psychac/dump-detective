using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// docs/analysis/phase1/dominator-analyzer-audit.md's "Shared Next steps" P3 item: a cross-section
/// link built from a stable <see cref="AnalyzerDetailSection.SectionId"/> (e.g. "#A5") only resolves
/// if the HTML anchor scheme actually uses <c>SectionId</c> — this was previously index-only
/// (<c>detail-{i}</c>), same as <c>MarkdownCanonicalReportFormatter</c> already did, but a real gap
/// in this server-side pre-render path (<see cref="ReportHtmlShared.RenderAnalyzerSections"/>).
/// </summary>
public sealed class ReportHtmlSharedAnchorTests
{
    private static AnalyzerDetailSection MakeSection(string sectionId, string displayTitle) =>
        new(AnalyzerName: displayTitle, DisplayTitle: displayTitle, SortOrder: 0, Blocks: [], SectionId: sectionId);

    [Fact]
    public void RenderAnalyzerSections_SectionHasSectionId_AnchorsBySectionId()
    {
        string html = ReportHtmlShared.RenderAnalyzerSections([MakeSection("A5", "GC Root Analysis")]);

        html.Should().Contain("id=\"A5\"");
        html.Should().Contain("href=\"#A5\"");
        html.Should().NotContain("id=\"detail-0\"");
    }

    [Fact]
    public void RenderAnalyzerSections_SectionHasNoSectionId_FallsBackToIndexAnchor()
    {
        string html = ReportHtmlShared.RenderAnalyzerSections([MakeSection("", "Some Supplementary Section")]);

        html.Should().Contain("id=\"detail-0\"");
        html.Should().Contain("href=\"#detail-0\"");
    }

    [Fact]
    public void RenderAnalyzerSections_MixOfSectionIdsAndBlanks_EachUsesItsOwnScheme()
    {
        string html = ReportHtmlShared.RenderAnalyzerSections(
        [
            MakeSection("A5", "GC Root Analysis"),
            MakeSection("", "Some Supplementary Section"),
        ]);

        html.Should().Contain("id=\"A5\"");
        html.Should().Contain("id=\"detail-1\"");
    }
}
