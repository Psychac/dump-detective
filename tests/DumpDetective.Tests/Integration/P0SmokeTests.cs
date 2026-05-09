using System.Text.Json;
using System.Text.RegularExpressions;

using DumpDetective.Cli.Services;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration;

/// <summary>
/// Smoke tests for P0 items:
///   P0.1 — JSON export uses the rendered document.
///   P0.2 — AnalyzerRunStatuses schema round-trips correctly.
///   P0.3 — Report quality panel data is populated per analyzer.
/// </summary>
public sealed class P0SmokeTests
{
    // ── helpers ────────────────────────────────────────────────────────────

    private static ReportBuilderFacade BuildFacade() => new(
    [
        new TextCanonicalReportFormatter(),
        new MarkdownCanonicalReportFormatter(),
        new HtmlCanonicalReportFormatter(),
        new HtmlReportRenderer(),
    ],
    new DefaultSectionBuilderFactory(),
    new ReportSerializer(),
    new TrendReportComposer([], new ReportSerializer()));

    private static AnalyzerRunResult MakeRun(string name, FindingSeverity sev, string title, AnalyzerExecutionStatus status = AnalyzerExecutionStatus.Success)
    {
        InsightFinding f = new(
            Analyzer: name,
            Category: "Leak",
            Severity: sev,
            Title: title,
            Evidence: $"Evidence from {name}",
            Recommendation: $"Fix from {name}",
            Tags: [],
            Fingerprint: $"{name}-{title}");

        GenericAnalyzerDomainResult result = new()
        {
            AnalyzerName = name,
            Category = "Leak",
            Metrics = new Dictionary<string, object?>(),
            Warnings = []
        };

        return new AnalyzerRunResult(
            name,
            status,
            TimeSpan.FromMilliseconds(42),
            result,
            status == AnalyzerExecutionStatus.Failed ? "Simulated failure" : null,
            null,
            Findings: [f]);
    }

    // ── P0.1: HTML report embeds JSON matching the serialized document ──────

    [Fact]
    public void P0_1_HtmlReport_EmbeddedJsonMatchesRenderedDocument()
    {
        AnalyzerRunResult run = MakeRun("LeakAnalyzer", FindingSeverity.Critical, "BigLeak");
        ReportBuilderFacade facade = BuildFacade();

        // Get the canonical document first via the ReportSerializer
        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run],
            TimeSpan.FromSeconds(5),
            new DefaultSectionBuilderFactory().CreateBuilders());

        // Render HTML
        HtmlReportRenderer renderer = new();
        string html = renderer.Render(doc);

        // Extract embedded JSON from <script id="report-json">
        Match m = Regex.Match(html,
            @"<script\b[^>]*\bid\s*=\s*(['""])(report-json)\1[^>]*>([\s\S]*?)</script>",
            RegexOptions.IgnoreCase);

        m.Success.Should().BeTrue("HTML must contain <script id=\"report-json\">");
        string embeddedJson = m.Groups[3].Value;
        embeddedJson.Should().NotBeNullOrWhiteSpace();

        // Round-trip: the embedded JSON should deserialize back to an equivalent document
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(
            embeddedJson, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        restored!.DumpPath.Should().Be("C:/dumps/smoke.dmp");
        restored.Findings.Should().HaveCount(doc.Findings.Count);
        restored.Findings[0].Title.Should().Be("BigLeak");
    }

    // ── P0.2: Separate-JSON regex extracts the same content ────────────────

    [Fact]
    public void P0_1_SeparateJsonRegex_ExtractsEmbeddedContent()
    {
        // Simulate an HTML file with the embedded JSON block
        const string fakeJson = """{"schemaVersion":"2.1","dumpPath":"test.dmp"}""";
        string html = $"""<html><body><script id="report-json" type="application/json">{fakeJson}</script></body></html>""";

        var pattern = @"<script\b[^>]*\bid\s*=\s*(['""])(report-json)\1[^>]*>([\s\S]*?)</script>";
        Match m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);

        m.Success.Should().BeTrue();
        m.Groups[3].Value.Should().Be(fakeJson);
    }

    [Fact]
    public void P0_1_SeparateJsonRegex_HandlesAlternateAttributeOrdering()
    {
        // id attribute comes after type
        const string fakeJson = """{"schemaVersion":"2.1"}""";
        string html = $"""<script type="application/json" id="report-json">{fakeJson}</script>""";

        var pattern = @"<script\b[^>]*\bid\s*=\s*(['""])(report-json)\1[^>]*>([\s\S]*?)</script>";
        Match m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);

        m.Success.Should().BeTrue();
        m.Groups[3].Value.Should().Be(fakeJson);
    }

    // ── P0.3: AnalyzerRunStatuses populated and round-trips ────────────────

    [Fact]
    public void P0_3_ReportDocument_PopulatesAnalyzerRunStatuses()
    {
        AnalyzerRunResult run1 = MakeRun("LeakAnalyzer", FindingSeverity.Critical, "Leak1");
        AnalyzerRunResult run2 = MakeRun("ThreadAnalyzer", FindingSeverity.Warning, "ThreadBlock");
        AnalyzerRunResult run3 = MakeRun("FailedAnalyzer", FindingSeverity.Info, "NotUsed", AnalyzerExecutionStatus.Failed);

        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run1, run2, run3],
            TimeSpan.FromSeconds(1),
            new DefaultSectionBuilderFactory().CreateBuilders());

        doc.AnalyzerRunStatuses.Should().HaveCount(3);

        var leak = doc.AnalyzerRunStatuses.First(s => s.AnalyzerName == "LeakAnalyzer");
        leak.Status.Should().Be("Success");
        leak.DurationMs.Should().BeApproximately(42, 5);

        var failed = doc.AnalyzerRunStatuses.First(s => s.AnalyzerName == "FailedAnalyzer");
        failed.Status.Should().Be("Failed");
        failed.ErrorMessage.Should().Be("Simulated failure");
    }

    [Fact]
    public void P0_3_AnalyzerRunStatusRecord_SerializesAndDeserializes()
    {
        AnalyzerRunResult run = MakeRun("LeakAnalyzer", FindingSeverity.Critical, "Leak");
        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run],
            TimeSpan.FromSeconds(2),
            new DefaultSectionBuilderFactory().CreateBuilders());

        // Round-trip via source-gen serializer
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        restored!.AnalyzerRunStatuses.Should().HaveCount(1);
        restored.AnalyzerRunStatuses[0].AnalyzerName.Should().Be("LeakAnalyzer");
        restored.AnalyzerRunStatuses[0].Status.Should().Be("Success");
    }

    [Fact]
    public void P0_3_HtmlReport_ContainsAnalyzerStatusData()
    {
        AnalyzerRunResult run1 = MakeRun("LeakAnalyzer", FindingSeverity.Critical, "Leak1");
        AnalyzerRunResult run2 = MakeRun("FailedAnalyzer", FindingSeverity.Info, "X", AnalyzerExecutionStatus.Failed);

        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run1, run2],
            TimeSpan.FromSeconds(1),
            new DefaultSectionBuilderFactory().CreateBuilders());

        HtmlReportRenderer renderer = new();
        string html = renderer.Render(doc);

        // The embedded JSON must contain analyzerRunStatuses
        Match m = Regex.Match(html,
            @"<script\b[^>]*\bid\s*=\s*(['""])(report-json)\1[^>]*>([\s\S]*?)</script>",
            RegexOptions.IgnoreCase);
        m.Success.Should().BeTrue();

        string embeddedJson = m.Groups[3].Value;
        embeddedJson.Should().Contain("analyzerRunStatuses");
        embeddedJson.Should().Contain("LeakAnalyzer");
        embeddedJson.Should().Contain("FailedAnalyzer");
        embeddedJson.Should().Contain("Failed");
        embeddedJson.Should().Contain("Simulated failure");
    }
}
