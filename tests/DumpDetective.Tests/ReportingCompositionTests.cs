using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests;

public sealed class ReportingCompositionTests
{
    [Fact]
    public void Serialize_ShouldMergeDuplicateFindings_AndPreserveEvidenceAndRemediation()
    {
        InsightFinding findingA = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Duplicate strings detected",
            Evidence: "Evidence A",
            Recommendation: "Recommendation A",
            Tags: ["memory", "string"],
            Fingerprint: "dup-key");

        InsightFinding findingB = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Critical,
            Title: "Duplicate strings detected",
            Evidence: "Evidence B",
            Recommendation: "Recommendation B",
            Tags: ["memory", "string"],
            Fingerprint: "dup-key");

        AnalyzerRunResult runA = CreateRun("RetentionAnalyzer", findingA);
        AnalyzerRunResult runB = CreateRun("RetentionAnalyzer", findingB);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/test.dmp",
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.ExecutiveSummary.Should().NotBeNull();
        doc.ExecutiveSummary!.CriticalFindings.Should().HaveCount(1);
        doc.ExecutiveSummary.WarningFindings.Should().HaveCount(1);
        doc.ExecutiveSummary.TopRecommendations.Should().HaveCount(2);
        doc.ExecutiveSummary.CriticalFindings![0].Evidence.Should().Be("Evidence B");
        doc.ExecutiveSummary.WarningFindings![0].Evidence.Should().Be("Evidence A");
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
        AnalysisReportDocument doc = new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/test2.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 2,
            Findings =
            [
                new FindingRecord(
                    Analyzer: "CrashAnalyzer",
                    Category: "Crash",
                    Severity: FindingSeverity.Warning.ToString(),
                    Title: "Crash signature",
                    Evidence: "Unhandled exception in worker",
                    Recommendation: "Inspect stack and exception roots",
                    Tags: ["crash"],
                    Fingerprint: "sec-1"),
                new FindingRecord(
                    Analyzer: "ThreadAnalyzer",
                    Category: "Threads",
                    Severity: FindingSeverity.Info.ToString(),
                    Title: "Thread pool pressure",
                    Evidence: "LongValue_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
                    Recommendation: "Review queued work items",
                    Tags: ["threads"],
                    Fingerprint: "sec-2")
            ],
            AnalyzerSections =
            [
                new AnalyzerDetailSection("Crash Analyzer", "Crash Analyzer", 0,
                [
                    new HeadingBlock("SUMMARY"),
                    new TextBlock("Crash signature"),
                    new TextBlock("Unhandled exception in worker")
                ]),
                new AnalyzerDetailSection("Thread Analyzer", "Thread Analyzer", 10,
                [
                    new HeadingBlock("SUMMARY"),
                    new TextBlock("Thread pool pressure"),
                    new TextBlock("LongValue_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX")
                ])
            ]
        };

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
    public void Serialize_ShouldStampSchemaVersion()
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

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/contract.dmp",
            runs: [CreateRun("CrashAnalyzer", finding)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.SchemaVersion.Should().Be("2.1");
    }

    [Fact]
    public void HtmlFormatter_ShouldRenderDetailedAnalyzerSections_AsCollapsibleBlocks()
    {
        SingleDumpReportDocument doc = new()
        {
            DumpPath = "C:/dumps/detailed.dmp",
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

    [Fact]
    public void MarkdownFormatter_ShouldRenderConfidenceBandBlock()
    {
        SingleDumpReportDocument doc = new()
        {
            DumpPath = "C:/dumps/confidence.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            AnalyzerSections =
            [
                new AnalyzerDetailSection("Leak Analysis", "Leak Analysis", 0,
                [
                    new HeadingBlock("LEAK CANDIDATES"),
                    new ConfidenceBandBlock("Medium", 0.55, "★★☆☆", ["Heuristic-only leak analysis"]),
                    new TextBlock("Details follow.")
                ])
            ]
        };

        string output = new MarkdownCanonicalReportFormatter().Render(doc);

        output.Should().Contain("> ★★☆☆ Medium confidence — Heuristic-only leak analysis");
        output.Should().Contain("### Leak Analysis");
        output.Should().Contain("#### LEAK CANDIDATES");
    }

    [Fact]
    public void CrossDomainInsightsSection_ShouldRenderOnlyInsightEngineFindings()
    {
        InsightFinding regularFinding = new(
            Analyzer: "MemoryAnalyzer",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Regular finding",
            Evidence: "Regular evidence",
            Recommendation: "Regular recommendation",
            Tags: ["memory"],
            Fingerprint: "regular-finding");

        InsightFinding insightFinding = new(
            Analyzer: "InsightEngine",
            Category: "CrossDomain",
            Severity: FindingSeverity.Critical,
            Title: "Cross-domain correlation",
            Evidence: "Insight evidence",
            Recommendation: "Insight recommendation",
            Tags: ["cross-analyzer"],
            Fingerprint: "insight-finding");

        AnalyzerDomainResult result = new GenericAnalyzerDomainResult
        {
            AnalyzerName = "MemoryAnalyzer",
            Category = "Memory",
            Warnings = []
        };

        AnalyzerRunResult run = new(
            AnalyzerName: "MemoryAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(5),
            Result: result,
            ErrorMessage: null,
            ErrorType: null,
            Findings: [regularFinding],
            Artifacts: [],
            Diagnostics: null);

        AnalyzerResultSet resultSet = new([run], additionalFindings: [insightFinding]);
        InsightsSectionBuilder builder = new();

        builder.CanBuild(resultSet).Should().BeTrue();

        AnalyzerDetailSection section = builder.Build(resultSet);
        section.SectionId.Should().Be("X1");
        section.Domain.Should().Be("CrossDomain");

        string output = new MarkdownCanonicalReportFormatter().Render(new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/cross-domain.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            AnalyzerSections = [section]
        });

        output.Should().Contain("Cross-domain correlation");
        output.Should().NotContain("Regular finding");
    }

    [Fact]
    public void TypeSystemSection_ShouldRenderC1TypeTableMetadata()
    {
        MemoryDomainResult memory = new(
            TotalBytes: 1024,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 10,
            LohObjects: 0,
            LohThresholdBytes: 85000,
            UniqueTypes: 1,
            TopTypes:
            [
                new TypeSnapshot("Demo.Type", 10, 1024, 0, 102, 0, 0, "Demo.Module")
            ],
            SizeBucketHistogram: []);

        GCGenerationDomainResult gcGen = new(
            Gen0Bytes: 0,
            Gen0Objects: 0,
            Gen1Bytes: 0,
            Gen1Objects: 0,
            Gen2Bytes: 0,
            Gen2Objects: 0,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 0,
            LohObjects: 0,
            TopLohTypes: [],
            Gen2Pct: 100,
            PerTypeGenerationProfiles:
            [
                new TypeGenerationProfile("Demo.Type", 1, 2, 3, 0, 1024, false)
            ]);

        ObjectShapeAnalyzerDomainResult shape = new(
            TopReferenceHeavyTypes:
            [
                new TypeShapeProfile("Demo.Type", 4, 2, 2, 0.50, 10, false, false, false, 1, 0, ObjectShapeCategory.Balanced)
            ],
            TopValueHeavyTypes:
            [
                new TypeShapeProfile("Demo.Type", 4, 2, 2, 0.50, 10, false, false, false, 1, 0, ObjectShapeCategory.Balanced)
            ],
            TotalTypesAnalyzed: 1,
            AvgRefFieldsPerType: 2);

        AnalyzerRunResult memoryRun = new(
            AnalyzerName: "MemoryAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(5),
            Result: memory,
            ErrorMessage: null,
            ErrorType: null,
            Findings: [],
            Artifacts: [],
            Diagnostics: null);

        AnalyzerRunResult gcRun = new(
            AnalyzerName: "GCGenerationAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(5),
            Result: gcGen,
            ErrorMessage: null,
            ErrorType: null,
            Findings: [],
            Artifacts: [],
            Diagnostics: null);

        AnalyzerRunResult shapeRun = new(
            AnalyzerName: "ObjectShapeAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.FromMilliseconds(5),
            Result: shape,
            ErrorMessage: null,
            ErrorType: null,
            Findings: [],
            Artifacts: [],
            Diagnostics: null);

        AnalyzerResultSet resultSet = new([memoryRun, gcRun, shapeRun]);
        TypeSystemSectionBuilder builder = new();

        builder.CanBuild(resultSet).Should().BeTrue();

        AnalyzerDetailSection section = builder.Build(resultSet);
        section.SectionId.Should().Be("C1");
        section.Domain.Should().Be("TypeSystem");
        section.DisplayTitle.Should().Be("Type Table");

        string output = new MarkdownCanonicalReportFormatter().Render(new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/type-table.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            AnalyzerSections = [section]
        });

        output.Should().Contain("Demo.Type");
        output.Should().Contain("Type table");
    }

    private static AnalyzerRunResult CreateRun(string analyzerName, InsightFinding finding)
    {
        GenericAnalyzerDomainResult result = new()
        {
            AnalyzerName = analyzerName,
            Category = finding.Category,
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
