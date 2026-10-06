using DumpDetective.Sdk.Synthesis;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using DumpDetective.Cli.Output;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;
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
///   P0.2 — Appendix schema round-trips correctly.
///   P0.3 — Modern report projections are populated per analyzer.
/// </summary>
public sealed class P0SmokeTests
{
    // ── helpers ────────────────────────────────────────────────────────────

    private static ReportBuilderFacade BuildFacade() => new(
    [
        new TextCanonicalReportFormatter(),
        new MarkdownCanonicalReportFormatter(),
        new HtmlReportRenderer(),
    ],
    new DefaultSectionBuilderFactory(),
    new ReportSerializer(),
    new TrendReportComposer(new ReportSerializer()));

    private static AnalyzerRunResult MakeRun(string name, Severity sev, string title, AnalyzerExecutionStatus status = AnalyzerExecutionStatus.Success)
    {
        Claim f = ClaimFactory.Create(
            analyzer: name,
            category: "Leak",
            severity: sev,
            title: title,
            narrative: $"Evidence from {name}",
            recommendation: $"Fix from {name}",
            tags: [],
            fingerprint: $"{name}-{title}");

        GenericAnalyzerDomainResult result = new()
        {
            AnalyzerName = name,
            Category = "Leak",
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
        AnalyzerRunResult run = MakeRun("LeakAnalyzer", Severity.Critical, "BigLeak");

        // Get the canonical document first via the ReportSerializer
        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run],
            TimeSpan.FromSeconds(5),
            analyzerBuilders: new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            reportBuilders: new DefaultSectionBuilderFactory().CreateReportBuilders());

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

        using JsonDocument payload = JsonDocument.Parse(embeddedJson);
        payload.RootElement.TryGetProperty("report", out JsonElement reportElement).Should().BeTrue();
        payload.RootElement.TryGetProperty("perDumpDocs", out JsonElement perDumpElement).Should().BeTrue();
        perDumpElement.ValueKind.Should().Be(JsonValueKind.Array);

        string reportJson = reportElement.GetRawText();

        // Round-trip: the embedded JSON should deserialize back to an equivalent document
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(
            reportJson, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        ((SingleDumpReportDocument)restored!).DumpPath.Should().Be("C:/dumps/smoke.dmp");
        restored.Domains.Should().NotBeNull();
        restored.Appendix.Should().NotBeNull();
        restored.CrossDomainInsights.Should().NotBeNull();
    }

    // ── P0.3: Appendix populated and round-trips ───────────────────────────

    [Fact]
    public void P0_3_ReportDocument_PopulatesAppendix()
    {
        AnalyzerRunResult run1 = MakeRun("LeakAnalyzer", Severity.Critical, "Leak1");
        AnalyzerRunResult run2 = MakeRun("ThreadAnalyzer", Severity.Warning, "ThreadBlock");
        AnalyzerRunResult run3 = MakeRun("FailedAnalyzer", Severity.Info, "NotUsed", AnalyzerExecutionStatus.Failed);

        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run1, run2, run3],
            TimeSpan.FromSeconds(1),
            analyzerBuilders: new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            reportBuilders: new DefaultSectionBuilderFactory().CreateReportBuilders());

        doc.Appendix.Should().NotBeNull();
        doc.Appendix!.AnalyzerRunSummary.Should().HaveCount(3);

        var leak = doc.Appendix.AnalyzerRunSummary.First(s => s.AnalyzerName == "LeakAnalyzer");
        leak.Status.Should().Be("Completed");
        leak.DurationMs.Should().BeApproximately(42, 5);

        var failed = doc.Appendix.AnalyzerRunSummary.First(s => s.AnalyzerName == "FailedAnalyzer");
        failed.Status.Should().Be("Failed");
        failed.ErrorMessage.Should().Be("Simulated failure");
    }

    [Fact]
    public void P0_3_AnalyzerRunStatusRecord_SerializesAndDeserializes()
    {
        AnalyzerRunResult run = MakeRun("LeakAnalyzer", Severity.Critical, "Leak");
        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run],
            TimeSpan.FromSeconds(2),
            analyzerBuilders: new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            reportBuilders: new DefaultSectionBuilderFactory().CreateReportBuilders());

        // Round-trip via source-gen serializer
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        AnalysisReportDocument? restored = JsonSerializer.Deserialize(json, ReportJsonContext.Default.AnalysisReportDocument);

        restored.Should().NotBeNull();
        restored!.Appendix.Should().NotBeNull();
        restored.Appendix!.AnalyzerRunSummary.Should().HaveCount(1);
        restored.Appendix.AnalyzerRunSummary[0].AnalyzerName.Should().Be("LeakAnalyzer");
        restored.Appendix.AnalyzerRunSummary[0].Status.Should().Be("Completed");
    }

    [Fact]
    public void P0_3_HtmlReport_ContainsAnalyzerStatusData()
    {
        AnalyzerRunResult run1 = MakeRun("LeakAnalyzer", Severity.Critical, "Leak1");
        AnalyzerRunResult run2 = MakeRun("FailedAnalyzer", Severity.Info, "X", AnalyzerExecutionStatus.Failed);

        ReportSerializer serializer = new();
        AnalysisReportDocument doc = serializer.Serialize(
            "C:/dumps/smoke.dmp",
            [run1, run2],
            TimeSpan.FromSeconds(1),
            analyzerBuilders: new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            reportBuilders: new DefaultSectionBuilderFactory().CreateReportBuilders());

        HtmlReportRenderer renderer = new();
        string html = renderer.Render(doc);

        // The embedded JSON must contain appendix data
        Match m = Regex.Match(html,
            @"<script\b[^>]*\bid\s*=\s*(['""])(report-json)\1[^>]*>([\s\S]*?)</script>",
            RegexOptions.IgnoreCase);
        m.Success.Should().BeTrue();

        string embeddedJson = m.Groups[3].Value;
        embeddedJson.Should().Contain("appendix");
        embeddedJson.Should().Contain("LeakAnalyzer");
        embeddedJson.Should().Contain("FailedAnalyzer");
        embeddedJson.Should().Contain("Failed");
        embeddedJson.Should().Contain("Simulated failure");
    }

    // ── P0.4: Trend HTML contains per-dump-json with full single-dump documents ──

    [Fact]
    public void TrendHtml_PerDumpJson_ContainsFullSingleDumpDocuments()
    {
        Claim finding = ClaimFactory.Create(
            analyzer: "LeakAnalyzer",
            category: "Leaks",
            severity: Severity.Warning,
            title: "Potential leak in ServiceProvider",
            narrative: "50MB retained",
            recommendation: "Investigate retention path",
            tags: ["leaks"],
            fingerprint: "leak-sp-1");

        AnalyzerRunResult run = MakeRun("LeakAnalyzer", Severity.Warning, "Potential leak");

        AnalysisSnapshot baseline = new(
            Index: 0, DumpPath: "C:/dumps/baseline.dmp", Runs: [run],
            Findings: [finding],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal),
            GeneratedAtUtc: DateTime.UtcNow.AddMinutes(-10));

        AnalysisSnapshot current = new(
            Index: 1, DumpPath: "C:/dumps/current.dmp", Runs: [run],
            Findings: [finding],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal),
            GeneratedAtUtc: DateTime.UtcNow);

        TrendReportData trendData = new(
            Steps: [], Overall: [],
            Timeline: [], ScopedTimeline: [], Snapshots: [baseline, current],
            NewFindings: [finding], PersistentFindings: [], ResolvedFindings: [],
            SeverityChanges: []);

        TrendReportComposer composer = new(new ReportSerializer());
        AnalysisReportDocument trendDoc = composer.ComposeCanonicalTrendReport(
            [run], TimeSpan.FromSeconds(2),
            null, new DefaultSectionBuilderFactory().CreateAnalyzerBuilders(),
            new DefaultSectionBuilderFactory().CreateReportBuilders(), trendData);

        HtmlReportRenderer renderer = new();
        string html = renderer.Render(trendDoc);

        // Extract report-json payload and read perDumpDocs envelope member.
        Match m = Regex.Match(html,
            @"<script\b[^>]*\bid\s*=\s*(['""])(report-json)\1[^>]*>([\s\S]*?)</script>",
            RegexOptions.IgnoreCase);
        m.Success.Should().BeTrue("HTML must contain <script id=\"report-json\">");

        string payloadJson = m.Groups[3].Value;
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        payload.RootElement.TryGetProperty("perDumpDocs", out JsonElement perDumpArray).Should().BeTrue();

        perDumpArray.ValueKind.Should().Be(JsonValueKind.Array);
        perDumpArray.GetArrayLength().Should().Be(2);

        // Parse as JSON array
        // Each per-dump document should have the same structure as a single-dump report
        JsonElement firstDoc = perDumpArray[0];
        firstDoc.GetProperty("dumpPath").GetString().Should().Be("C:/dumps/baseline.dmp");
        firstDoc.TryGetProperty("domains", out _).Should().BeTrue("per-dump doc should have domains");
        firstDoc.TryGetProperty("healthScorecard", out _).Should().BeTrue("per-dump doc should have health scorecard");
        firstDoc.TryGetProperty("appendix", out _).Should().BeTrue("per-dump doc should have appendix");
    }
}
