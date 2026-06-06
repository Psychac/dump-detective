using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Serialization;
using DumpDetective.Reporting.Models;

using FluentAssertions;
using Xunit;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests.Integration;

public sealed class HtmlRendererCssTests
{
    [Fact]
    public void Render_IncludesCss_And_ImportMap()
    {
        // Arrange: build a minimal document
        var serializer = new ReportSerializer();
        var finding = new InsightFinding(
            Analyzer: "X",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Actionable item",
            Evidence: "Evidence",
            Recommendation: "Do something",
            Tags: ["memory"],
            Fingerprint: "x-1");

        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: [finding]);

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        // Act
        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc);

        // Assert
        html.Should().Contain("<style>");
        html.Should().Contain("--bg");
        html.Should().MatchRegex("<script type=\\\"importmap\\\">|<script type=\\\"module\\\">import 'report\\.main\\.js'");
    }

    [Fact]
    public void Render_V2Style_EmbedsStyleVersion_And_RightRailHost()
    {
        var serializer = new ReportSerializer();
        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: Array.Empty<InsightFinding>());

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        html.Should().Contain("\"reportStyleVersion\":\"v2\"");
        html.Should().Contain("id=\"report-right-rail\"");
        html.Should().Contain("id=\"report-right-rail-content\"");
        html.Should().Contain("--bg-canvas");
    }

    [Fact]
    public void Render_ContainsV2AccessibilityAndPrintHooks()
    {
        var serializer = new ReportSerializer();
        var finding = new InsightFinding(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Potential retention",
            Evidence: "Object growth observed",
            Recommendation: "Investigate root path",
            Tags: ["leak"],
            Fingerprint: "retention-1");

        var fakeRun = new AnalyzerRunResult(
            "RetentionAnalyzer",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: [finding]);

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        html.Should().Contain("id=\"report-sr-summary\"");
        html.Should().Contain("href=\"#report-domains\"");
        html.Should().Contain("id=\"report-print-footer\"");
        html.Should().Contain("table-print-note");
        html.Should().Contain("@media print");
        html.Should().Contain("aria-expanded");
    }

    [Fact]
    public void Render_ContainsExpectedBootstrapOrderHooks_ForV2TriageFlow()
    {
        var serializer = new ReportSerializer();
        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: Array.Empty<InsightFinding>());

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        int headerPos = html.IndexOf("buildHeader(doc)", StringComparison.Ordinal);
        int scorecardPos = html.IndexOf("buildHealthScorecard(doc)", StringComparison.Ordinal);
        int executivePos = html.IndexOf("buildExecutiveSummary(doc)", StringComparison.Ordinal);
        int actionQueuePos = html.IndexOf("buildActionQueuePanel(doc)", StringComparison.Ordinal);

        headerPos.Should().BeGreaterThan(-1);
        scorecardPos.Should().BeGreaterThan(-1);
        executivePos.Should().BeGreaterThan(-1);
        actionQueuePos.Should().BeGreaterThan(-1);

        headerPos.Should().BeLessThan(scorecardPos);
        scorecardPos.Should().BeLessThan(executivePos);
        executivePos.Should().BeLessThan(actionQueuePos);
    }

    [Fact]
    public void Render_ContainsKeyboardAndCollapsibleAriaHooks()
    {
        var serializer = new ReportSerializer();
        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: Array.Empty<InsightFinding>());

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        html.Should().Contain("function syncCollapsibleAria");
        html.Should().Contain("setAttribute('aria-expanded'");
        html.Should().Contain("if (ev.key === 'ArrowLeft'");
        html.Should().Contain("if (ev.key === 'ArrowRight'");
        html.Should().Contain("if (ev.key === 'Enter'");
        html.Should().Contain("if (ev.key === 'Escape'");
    }

    [Fact]
    public void Render_ExposesRequiredV2SpecTokenNames()
    {
        var serializer = new ReportSerializer();
        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: Array.Empty<InsightFinding>());

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc);

        html.Should().Contain("--border-subtle:");
        html.Should().Contain("--border-strong:");
        html.Should().Contain("--space-8:");
        html.Should().Contain("--space-3:");
        html.Should().Contain("--space-24:");
        html.Should().Contain("--space-32:");
        html.Should().Contain("--radius-sm:");
        html.Should().Contain("--radius-md:");
        html.Should().Contain("--radius-lg:");
        html.Should().Contain("--shadow-1:");
        html.Should().Contain("--shadow-2:");
    }

    [Fact]
    public void Render_ContainsRequiredV2ComponentMarkers()
    {
        var serializer = new ReportSerializer();
        var fakeRun = new AnalyzerRunResult(
            "X",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: Array.Empty<InsightFinding>());

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();
        string html = renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));

        html.Should().Contain("'data-component-id', 'report-header'");
        html.Should().Contain("'data-component-id', 'health-scorecard'");
        html.Should().Contain("'data-component-id', 'executive-summary'");
        html.Should().Contain("'data-component-id', 'top-actions'");
        html.Should().Contain("'data-component-id', 'appendix'");
        html.Should().Contain("sec.id = 'report-header'");
        html.Should().Contain("sec.id = 'health-scorecard'");
        html.Should().Contain("sec.id = 'executive-summary'");
        html.Should().Contain("sec.id = 'top-actions'");
        html.Should().Contain("['Scoring model', scoringModelVersion]");
        html.Should().Contain("exec-correlation__item");
        html.Should().Contain("Correlation Signals");
        html.Should().Contain("exec-correlation__provenance-link");
        html.Should().Contain("findingAnchorId(");
        html.Should().Contain("wrapper.id = sectionAnchorId");
        html.Should().Contain("section-anchor-legacy");
    }

    [Fact]
    public void Render_V1AndV2_PreserveSemanticReportPayloadExceptStyleField()
    {
        var serializer = new ReportSerializer();
        var finding = new InsightFinding(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Potential retention",
            Evidence: "Object growth observed",
            Recommendation: "Investigate root path",
            Tags: ["leak"],
            Fingerprint: "retention-1");

        var fakeRun = new AnalyzerRunResult(
            "RetentionAnalyzer",
            AnalyzerExecutionStatus.Success,
            TimeSpan.FromMilliseconds(1),
            new GenericAnalyzerDomainResult(),
            null,
            null,
            Findings: [finding]);

        AnalysisReportDocument doc = serializer.Serialize(
            "dump.dmp",
            new[] { fakeRun },
            TimeSpan.FromSeconds(0.5),
            new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders());

        var renderer = new HtmlReportRenderer();

        JsonObject reportV1 = ExtractEmbeddedReportPayload(renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V1)));

        JsonObject reportV2 = ExtractEmbeddedReportPayload(renderer.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2)));

        reportV1.Remove("reportStyleVersion");
        reportV2.Remove("reportStyleVersion");

        reportV1.ToJsonString().Should().Be(reportV2.ToJsonString());
    }

    private static JsonObject ExtractEmbeddedReportPayload(string html)
    {
        Match match = Regex.Match(
            html,
            "<script id=\\\"report-json\\\" type=\\\"application/json\\\">([\\s\\S]*?)</script>",
            RegexOptions.IgnoreCase);

        match.Success.Should().BeTrue();

        string payload = match.Groups[1].Value;
        JsonNode? envelope = JsonNode.Parse(payload);
        envelope.Should().NotBeNull();

        JsonObject? report = envelope!["report"] as JsonObject;
        report.Should().NotBeNull();

        return report!;
    }
}
