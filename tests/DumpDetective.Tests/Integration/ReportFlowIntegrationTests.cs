using DumpDetective.Cli.Services;
using DumpDetective.Core.Configuration;
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

        InsightFinding findingA = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "UNIQUE_DUP_TITLE",
            Evidence: "Evidence A",
            Recommendation: "Remediation A",
            Tags: ["dup"],
            Fingerprint: "same-key");

        InsightFinding findingB = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Critical,
            Title: "UNIQUE_DUP_TITLE",
            Evidence: "Evidence B",
            Recommendation: "Remediation B",
            Tags: ["dup"],
            Fingerprint: "same-key");

        AnalyzerRunResult runA = CreateRun("RetentionAnalyzer", findingA);
        AnalyzerRunResult runB = CreateRun("RetentionAnalyzer", findingB);

        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer([], new ReportSerializer()));

        string output = facade.BuildRenderedReport(
            dumpPath: "C:/dumps/int-test.dmp",
            format: format,
            audience: ReportAudience.All,
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1),
            cancellationToken: CancellationToken.None);

        output.Should().Contain("UNIQUE_DUP_TITLE");
        output.Should().Contain("Evidence A");
        output.Should().Contain("Evidence B");
        output.Should().Contain("Remediation A");
        output.Should().Contain("Remediation B");
    }

    [Fact]
    public void BuildRenderedReport_ShouldHonorCancellationToken()
    {
        InsightFinding finding = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Canceled",
            Evidence: "Should not render",
            Recommendation: "n/a",
            Tags: ["cancel"],
            Fingerprint: "cancel");

        AnalyzerRunResult run = CreateRun("RetentionAnalyzer", finding);
        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer([], new ReportSerializer()));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Action act = () => facade.BuildRenderedReport(
            dumpPath: "C:/dumps/int-test.dmp",
            format: ReportFormat.Text,
            audience: ReportAudience.All,
            runs: [run],
            elapsed: TimeSpan.FromSeconds(1),
            cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void BuildRenderedTrendReport_ShouldIncludeTrendComparisonContent()
    {
        InsightFinding finding = new(
            Analyzer: "CrashAnalyzer",
            Category: "Crash",
            Severity: FindingSeverity.Warning,
            Title: "Current snapshot finding",
            Evidence: "Unhandled exception",
            Recommendation: "Inspect crash thread",
            Tags: ["crash"],
            Fingerprint: "crash-1");

        AnalyzerRunResult currentRun = CreateRun("CrashAnalyzer", finding);

        AnalysisSnapshot baseline = new(
            Index: 0,
            DumpPath: "C:/dumps/base.dmp",
            Findings: [],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal),
            GeneratedAtUtc: DateTime.UtcNow.AddMinutes(-5));

        AnalysisSnapshot current = new(
            Index: 1,
            DumpPath: "C:/dumps/current.dmp",
            Findings: [finding],
            DomainResults: new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal),
            GeneratedAtUtc: DateTime.UtcNow);

        TrendReportData trendData = new(
            Steps: [],
            Overall: [],
            Timeline: [],
            Snapshots: [baseline, current],
            NewFindings: [finding],
            PersistentFindings: [],
            ResolvedFindings: []);

        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ],
        new DefaultSectionBuilderFactory(),
        new ReportSerializer(),
        new TrendReportComposer([], new ReportSerializer()));

        string output = facade.BuildRenderedTrendReport(
            dumpPath: "C:/dumps/current.dmp",
            format: ReportFormat.Text,
            audience: ReportAudience.All,
            currentRuns: [currentRun],
            elapsed: TimeSpan.FromSeconds(2),
            trendData: trendData,
            cancellationToken: CancellationToken.None);

        output.Should().Contain("DumpDetective Trend Analysis Report");
        output.Should().Contain("Dumps analyzed: 2");
        output.Should().Contain("Analyzed dumps:");
        output.Should().Contain("C:/dumps/base.dmp");
        output.Should().Contain("C:/dumps/current.dmp");
        output.Should().Contain("Trend metric regression summary");
        output.Should().Contain("Trend finding lifecycle summary");
        output.Should().Contain("[Dump 1 of 2: base.dmp]");
        output.Should().Contain("[Dump 2 of 2: current.dmp]");
        output.Should().Contain("DUMP SUMMARY");
        output.Should().Contain("TREND COMPARISON");

        int trendComparisonIndex = output.IndexOf("[Trend Comparison]", StringComparison.Ordinal);
        int dumpOneIndex = output.IndexOf("[Dump 1 of 2: base.dmp]", StringComparison.Ordinal);
        trendComparisonIndex.Should().BeGreaterThan(-1);
        dumpOneIndex.Should().BeGreaterThan(-1);
        trendComparisonIndex.Should().BeLessThan(dumpOneIndex);
    }

    private static AnalyzerRunResult CreateRun(string analyzerName, InsightFinding finding)
    {
        GenericAnalyzerDomainResult result = new()
        {
            AnalyzerName = analyzerName,
            Category = finding.Category,
            Metrics = new Dictionary<string, object?>(),
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
