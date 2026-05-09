using System.Text.Json;
using System.Text.RegularExpressions;

using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Serialization;
using DumpDetective.Reporting.Models;

using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration;

public sealed class HtmlRendererCssTests
{
    [Fact]
    public void Render_IncludesCss_And_ImportMap()
    {
        // Arrange: build a minimal document
        var serializer = new ReportSerializer();
        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: new[] { });

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DumpDetective.Cli.Services.DefaultSectionBuilderFactory().CreateBuilders());

        // Act
        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc);

        // Assert
        html.Should().Contain("<style>");
        html.Should().Contain("--bg");
        html.Should().Contain("<script type=\"importmap\">").OrContain("<script type=\"module\">import 'report.main.js'", "import map or module bootstrap must be present");
    }
}
