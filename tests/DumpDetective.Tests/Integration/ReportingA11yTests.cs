using FluentAssertions;

using Xunit;

using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests.Integration;

public sealed class ReportingA11yTests
{
    [Fact]
    public void Render_IncludesDeltaChip_WithAriaAndRole()
    {
        var serializer = new DumpDetective.Reporting.Services.ReportSerializer();
        var renderer = new HtmlReportRenderer();
        var doc = serializer.Serialize("dump.dmp", System.Array.Empty<DumpDetective.Core.Models.AnalyzerRunResult>(), System.TimeSpan.FromSeconds(0.5), new DumpDetective.Reporting.Services.DefaultSectionBuilderFactory().CreateAnalyzerBuilders(), new DumpDetective.Reporting.Services.DefaultSectionBuilderFactory().CreateReportBuilders());

        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        html.Should().Contain("timeline-delta-chip");
        html.Should().Contain("aria-label");
        html.Should().Contain("role=\"status\"");
    }

    [Fact]
    public void Render_IncludesCorrelationTimelineLane_And_FocusableEvents()
    {
        var serializer = new DumpDetective.Reporting.Services.ReportSerializer();
        var renderer = new HtmlReportRenderer();
        var doc = serializer.Serialize("dump.dmp", System.Array.Empty<DumpDetective.Core.Models.AnalyzerRunResult>(), System.TimeSpan.FromSeconds(0.5), new DumpDetective.Reporting.Services.DefaultSectionBuilderFactory().CreateAnalyzerBuilders(), new DumpDetective.Reporting.Services.DefaultSectionBuilderFactory().CreateReportBuilders());

        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        html.Should().Contain("trend-correlation-timeline__lane");
        html.Should().Contain("timeline-event");
        // Expect timeline events to be focusable (tabindex present in rendering JS)
        html.Should().Contain("tabindex");
    }
}
