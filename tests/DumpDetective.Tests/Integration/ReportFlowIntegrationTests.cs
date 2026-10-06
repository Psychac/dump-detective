using DumpDetective.Sdk.Synthesis;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration;

public sealed class ReportFlowIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BuildRenderedReport_ShouldUseComposedDedupedSections_ForAllFormats(int formatCode)
    {
        ReportFormat format = formatCode switch
        {
            0 => ReportFormat.Text,
            1 => ReportFormat.Markdown,
            2 => ReportFormat.Html,
            _ => throw new ArgumentOutOfRangeException(nameof(formatCode))
        };

        Claim findingA = ClaimFactory.Create(
            analyzer: "RetentionAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "UNIQUE_DUP_TITLE",
            narrative: "Evidence A",
            recommendation: "Remediation A",
            tags: ["dup"],
            fingerprint: "same-key");

        Claim findingB = ClaimFactory.Create(
            analyzer: "RetentionAnalyzer",
            category: "Leak",
            severity: Severity.Critical,
            title: "UNIQUE_DUP_TITLE",
            narrative: "Evidence B",
            recommendation: "Remediation B",
            tags: ["dup"],
            fingerprint: "same-key");

        AnalyzerRunResult runA = CreateRun("RetentionAnalyzer", findingA);
        AnalyzerRunResult runB = CreateRun("RetentionAnalyzer", findingB);

        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlReportRenderer()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer(new ReportSerializer()));

        string output = facade.BuildRenderedReport(
            dumpPath: "C:/dumps/int-test.dmp",
            format: format,
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1),
            cancellationToken: CancellationToken.None);

        output.Should().Contain("UNIQUE_DUP_TITLE");
        output.Should().Contain("Remediation A");
        output.Should().Contain("Remediation B");
    }

    [Fact]
    public void BuildRenderedReport_ShouldHonorCancellationToken()
    {
        Claim finding = ClaimFactory.Create(
            analyzer: "RetentionAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Canceled",
            narrative: "Should not render",
            recommendation: "n/a",
            tags: ["cancel"],
            fingerprint: "cancel");

        AnalyzerRunResult run = CreateRun("RetentionAnalyzer", finding);
        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlReportRenderer()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer(new ReportSerializer()));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Action act = () => facade.BuildRenderedReport(
            dumpPath: "C:/dumps/int-test.dmp",
            format: ReportFormat.Text,
            runs: [run],
            elapsed: TimeSpan.FromSeconds(1),
            cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void BuildRenderedTrendReport_ShouldIncludeTrendComparisonContent()
    {
        Claim finding = ClaimFactory.Create(
            analyzer: "CrashAnalyzer",
            category: "Crash",
            severity: Severity.Warning,
            title: "Current snapshot finding",
            narrative: "Unhandled exception",
            recommendation: "Inspect crash thread",
            tags: ["crash"],
            fingerprint: "crash-1");

        AnalyzerRunResult currentRun = CreateRun("CrashAnalyzer", finding);

        AnalysisSnapshot baseline = new(
            Index: 0,
            DumpPath: "C:/dumps/base.dmp",
            Runs: [],
            Findings: [],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal),
            GeneratedAtUtc: DateTime.UtcNow.AddMinutes(-5));

        AnalysisSnapshot current = new(
            Index: 1,
            DumpPath: "C:/dumps/current.dmp",
            Runs: [currentRun],
            Findings: [finding],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal),
            GeneratedAtUtc: DateTime.UtcNow);

        TrendReportData trendData = new(
            Steps: [],
            Overall: [],
            Timeline: [],
            ScopedTimeline: [],
            Snapshots: [baseline, current],
            NewFindings: [finding],
            PersistentFindings: [],
            ResolvedFindings: [],
            SeverityChanges: []);

        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlReportRenderer()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer(new ReportSerializer()));

        string output = facade.BuildRenderedTrendReport(
            format: ReportFormat.Text,
            currentRuns: [currentRun],
            elapsed: TimeSpan.FromSeconds(2),
            trendData: trendData,
            cancellationToken: CancellationToken.None);

        output.Should().Contain("DumpDetective Trend Analysis Report");
        output.Should().Contain("Dumps analyzed: 2");
        output.Should().Contain("Analyzed dumps:");
        output.Should().Contain("C:/dumps/base.dmp");
        output.Should().Contain("C:/dumps/current.dmp");
        output.Should().Contain("Finding lifecycle:");
        output.Should().Contain("New=1");
        output.Should().Contain("[Dump 1 of 2: base.dmp]");
        output.Should().Contain("[Dump 2 of 2: current.dmp]");
        output.Should().Contain("DUMP SUMMARY");
        output.Should().Contain("Regression Dashboard");

        int regressionDashboardIndex = output.IndexOf("Regression Dashboard", StringComparison.Ordinal);
        int dumpOneIndex = output.IndexOf("[Dump 1 of 2: base.dmp]", StringComparison.Ordinal);
        regressionDashboardIndex.Should().BeGreaterThan(-1);
        dumpOneIndex.Should().BeGreaterThan(-1);
        regressionDashboardIndex.Should().BeLessThan(dumpOneIndex);

        // Verify HTML format contains perDumpDocuments in the embedded JSON
        string htmlOutput = facade.BuildRenderedTrendReport(
            format: ReportFormat.Html,
            currentRuns: [currentRun],
            elapsed: TimeSpan.FromSeconds(2),
            trendData: trendData,
            cancellationToken: CancellationToken.None);

        // HtmlReportRenderer embeds report JSON with the document; verify it includes trend data
        htmlOutput.Should().Contain("report-json");
        htmlOutput.Should().Contain("trendAnalyzerSections");
    }

    private static AnalyzerRunResult CreateRun(string analyzerName, Claim finding)
    {
        GenericAnalyzerDomainResult result = new()
        {
            AnalyzerName = analyzerName,
            Category = finding.Category,
            Warnings = []
        };

        return new AnalyzerRunResult(analyzerName, AnalyzerExecutionStatus.Success, TimeSpan.FromMilliseconds(1), result, null, null, Findings: [finding]);
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
