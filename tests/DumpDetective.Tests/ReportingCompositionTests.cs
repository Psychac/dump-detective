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

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/test2.dmp",
            runs: [CreateRun("CrashAnalyzer", finding1), CreateRun("ThreadAnalyzer", finding2)],
            elapsed: TimeSpan.FromSeconds(2),
            builders: []);

        IReportFormatter[] formatters =
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter(),
            new HtmlCanonicalReportFormatter()
        ];

        foreach (IReportFormatter formatter in formatters)
        {
            string output = formatter.Render(doc);

            output.Should().Contain("Crash signature");
            output.Should().Contain("Thread pool pressure");
            output.Should().Contain("LongValue_XXXXXXXXXXXXXXXX");
        }
    }

    [Fact]
    public void ComposeCanonicalReport_ShouldStampContractVersions()
    {
        InsightFinding finding = new(
            Analyzer: "CrashAnalyzer",
            Category: "Crash",
            Severity: FindingSeverity.Warning,
            Title: "Contract versioning",
            Evidence: "Version metadata should be stamped.",
            Recommendation: "Keep schema changes backward-compatible.",
            Tags: ["contract"],
            Fingerprint: "contract-ver");

        ComposedReport report = ReportBuilder.ComposeCanonicalReport(
            dumpPath: "C:/dumps/contract.dmp",
            runs: [CreateRun("CrashAnalyzer", finding)],
            elapsed: TimeSpan.FromSeconds(1));

        report.ReportSchemaVersion.Should().Be(ReportContractVersions.ReportSchemaV1);
        report.SectionSchemaVersion.Should().Be(ReportContractVersions.SectionSchemaV1);
    }

    [Fact]
    public void HtmlFormatter_ShouldRenderDetailedAnalyzerSections_AsCollapsibleBlocks()
    {
        AnalysisReportDocument doc = new()
        {
            DumpPath       = "C:/dumps/detailed.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            AnalyzerSections =
            [
                new AnalyzerDetailSection("Memory Leak Analyzer", "Memory Leak Analyzer", 0,
                [
                    new MetricBlock("Top type",   "System.String"),
                    new MetricBlock("Retained MB", "123")
                ]),
                new AnalyzerDetailSection("Thread Analyzer", "Thread Analyzer", 10,
                [
                    new MetricBlock("Blocked threads", "4"),
                    new MetricBlock("Wait chains",     "2")
                ])
            ]
        };

        IReportFormatter formatter = new HtmlCanonicalReportFormatter();

        string output = formatter.Render(doc);

        output.Should().Contain("<details>");
        output.Should().Contain(">Memory Leak Analyzer<");
        output.Should().Contain(">Thread Analyzer<");
        output.Should().Contain("<span class=\"detail-key\">Top type:</span>");
        output.Should().Contain("<span class=\"detail-value wrap\">System.String</span>");
        output.Should().Contain("<span class=\"detail-key\">Blocked threads:</span>");
        output.Should().Contain("<span class=\"detail-value wrap\">4</span>");
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

        return new AnalyzerRunResult(
            AnalyzerName: analyzerName,
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(10),
            Result: result,
            ErrorMessage: null,
            ErrorType: null,
            Findings: [finding]);
    }
}
