using DumpDetective.Cli.Services;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;

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
            Analyzer: "MemoryLeakAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "UNIQUE_DUP_TITLE",
            Evidence: "Evidence A",
            Recommendation: "Remediation A",
            Tags: ["dup"],
            Fingerprint: "same-key");

        InsightFinding findingB = new(
            Analyzer: "MemoryLeakAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Critical,
            Title: "UNIQUE_DUP_TITLE",
            Evidence: "Evidence B",
            Recommendation: "Remediation B",
            Tags: ["dup"],
            Fingerprint: "same-key");

        AnalyzerRunResult runA = CreateRun("MemoryLeakAnalyzer", findingA);
        AnalyzerRunResult runB = CreateRun("MemoryLeakAnalyzer", findingB);

        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ],
        new DefaultAnalyzerReporterFactory());

        string output = facade.BuildRenderedReport(
            dumpPath: "C:/dumps/int-test.dmp",
            format: format,
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1),
            cancellationToken: CancellationToken.None);

        CountOccurrences(output, "UNIQUE_DUP_TITLE").Should().Be(1);
        output.Should().Contain("Evidence A");
        output.Should().Contain("Evidence B");
        output.Should().Contain("Remediation A");
        output.Should().Contain("Remediation B");
    }

    [Fact]
    public void BuildRenderedReport_ShouldHonorCancellationToken()
    {
        InsightFinding finding = new(
            Analyzer: "MemoryLeakAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Canceled",
            Evidence: "Should not render",
            Recommendation: "n/a",
            Tags: ["cancel"],
            Fingerprint: "cancel");

        AnalyzerRunResult run = CreateRun("MemoryLeakAnalyzer", finding);
        ReportBuilderFacade facade = new(
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ],
        new DefaultAnalyzerReporterFactory());

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

    private static AnalyzerRunResult CreateRun(string analyzerName, InsightFinding finding)
    {
        GenericAnalyzerDomainResult result = new()
        {
            AnalyzerName = analyzerName,
            Category = finding.Category,
            Findings = [finding],
            Metrics = new Dictionary<string, object?>(),
            Warnings = []
        };

        return new AnalyzerRunResult(analyzerName, AnalyzerExecutionStatus.Success, TimeSpan.FromMilliseconds(1), result, null, null);
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
