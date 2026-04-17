using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests;

public sealed class ReportingCompositionTests
{
    [Fact]
    public void ComposeCanonicalReport_ShouldMergeDuplicateSections_AndPreserveEvidenceAndRemediation()
    {
        InsightFinding findingA = new(
            Analyzer: "MemoryLeakAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Duplicate strings detected",
            Evidence: "Evidence A",
            Recommendation: "Recommendation A",
            Tags: ["memory", "string"],
            Fingerprint: "dup-key");

        InsightFinding findingB = new(
            Analyzer: "MemoryLeakAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Critical,
            Title: "Duplicate strings detected",
            Evidence: "Evidence B",
            Recommendation: "Recommendation B",
            Tags: ["memory", "string"],
            Fingerprint: "dup-key");

        AnalyzerRunResult runA = CreateRun("MemoryLeakAnalyzer", findingA);
        AnalyzerRunResult runB = CreateRun("MemoryLeakAnalyzer", findingB);

        ComposedReport report = ReportBuilder.ComposeCanonicalReport(
            dumpPath: "C:/dumps/test.dmp",
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1));

        report.Sections.Should().HaveCount(1);
        ReportSection section = report.Sections[0];
        section.SectionKey.Should().Be("dup-key");
        section.Severity.Should().Be(FindingSeverity.Critical);
        section.NarrativeSummary.Should().Contain("Evidence A").And.Contain("Evidence B");
        section.RemediationHints.Should().Contain("Recommendation A").And.Contain("Recommendation B");
        section.EvidenceRows.Should().Contain(r => r.Label == "Evidence" && r.Value == "Evidence A");
        section.EvidenceRows.Should().Contain(r => r.Label == "Evidence" && r.Value == "Evidence B");

        report.DedupDiagnostics.DuplicateCandidates.Should().Be(1);
        report.DedupDiagnostics.MergedSections.Should().Be(1);
        report.DedupDiagnostics.EvidenceAfterMerge.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TableWrapHelper_ShouldWrapWithoutTruncatingLongValues()
    {
        const string longValue = "ThisIsAnExtremelyLongTokenWithoutSpaces_0123456789ABCDEFGHIJ";

        IReadOnlyList<string> wrapped = TableWrapHelper.Wrap(longValue, 10);

        wrapped.Should().NotBeEmpty();
        string recombined = string.Concat(wrapped);
        recombined.Should().Be(longValue);
        wrapped.Should().OnlyContain(line => line.Length <= 10);
    }

    [Fact]
    public void CanonicalFormatters_ShouldRenderAllComposedSections_AndKeepLongValues()
    {
        InsightFinding finding1 = new(
            Analyzer: "CrashAnalyzer",
            Category: "Crash",
            Severity: FindingSeverity.Warning,
            Title: "Crash signature",
            Evidence: "Unhandled exception in worker",
            Recommendation: "Inspect stack and exception roots",
            Tags: ["crash"],
            Fingerprint: "sec-1");

        InsightFinding finding2 = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Threads",
            Severity: FindingSeverity.Info,
            Title: "Thread pool pressure",
            Evidence: "LongValue_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            Recommendation: "Review queued work items",
            Tags: ["threads"],
            Fingerprint: "sec-2");

        ComposedReport report = ReportBuilder.ComposeCanonicalReport(
            dumpPath: "C:/dumps/test2.dmp",
            runs: [CreateRun("CrashAnalyzer", finding1), CreateRun("ThreadAnalyzer", finding2)],
            elapsed: TimeSpan.FromSeconds(2));

        IReportFormatter[] formatters =
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ];

        foreach (IReportFormatter formatter in formatters)
        {
            string output = formatter.Render(report);

            output.Should().Contain("Crash signature");
            output.Should().Contain("Thread pool pressure");
            output.Should().Contain("LongValue_XXXXXXXXXXXXXXXX");
            output.Should().NotContain("...");
        }
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

        return new AnalyzerRunResult(
            AnalyzerName: analyzerName,
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(10),
            Result: result,
            ErrorMessage: null,
            ErrorType: null);
    }
}
