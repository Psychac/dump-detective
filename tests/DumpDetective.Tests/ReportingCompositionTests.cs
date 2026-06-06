using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Services;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;
using DumpDetective.Core.Enums;

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
        doc.ExecutiveSummary.CriticalFindings![0].Details![0].Should().Be("Evidence B");
        doc.ExecutiveSummary.WarningFindings![0].Details![0].Should().Be("Evidence A");
    }

    [Fact]
    public void Serialize_ShouldClusterNearDuplicateTopActions()
    {
        InsightFinding leakA = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Critical,
            Title: "Event handler retention pressure",
            Evidence: "Subscribers retain roots.",
            Recommendation: "Detach leaked handlers and re-check retention.",
            Tags: ["leak", "event"],
            Fingerprint: "cluster-a");

        InsightFinding leakB = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Event handler retention pressure",
            Evidence: "Duplicate retention pattern in related type.",
            Recommendation: "Detach leaked handlers and re-check retention.",
            Tags: ["leak", "event"],
            Fingerprint: "cluster-b");

        InsightFinding threadFinding = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Thread",
            Severity: FindingSeverity.Warning,
            Title: "Blocked worker thread hotspot",
            Evidence: "Multiple workers blocked on lock.",
            Recommendation: "Inspect lock ownership and reduce contention.",
            Tags: ["thread", "lock"],
            Fingerprint: "cluster-c");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/cluster.dmp",
            runs: [
                CreateRun("RetentionAnalyzer", leakA),
                CreateRun("RetentionAnalyzer", leakB),
                CreateRun("ThreadAnalyzer", threadFinding)
            ],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.ExecutiveSummary.Should().NotBeNull();
        doc.ExecutiveSummary!.TopActions.Should().NotBeNull();
        doc.ExecutiveSummary.TopActions!.Count.Should().Be(2);
        doc.ExecutiveSummary.TopActions[0].Title.Should().Contain("related");
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
                    Id: "sec-1",
                    Analyzer: "CrashAnalyzer",
                    Category: "Crash",
                    Severity: FindingSeverity.Warning.ToString(),
                    Title: "Crash signature",
                    Details: ["Unhandled exception in worker"],
                    Recommendation: "Inspect stack and exception roots",
                    Tags: ["crash"]),
                new FindingRecord(
                    Id: "sec-2",
                    Analyzer: "ThreadAnalyzer",
                    Category: "Threads",
                    Severity: FindingSeverity.Info.ToString(),
                    Title: "Thread pool pressure",
                    Details: ["LongValue_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"],
                    Recommendation: "Review queued work items",
                    Tags: ["threads"])
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
            Tags: ["contract"]);

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

    [Fact]
    public void Serialize_ShouldMapDisplayNameFindings_ToTheirDomains()
    {
        InsightFinding memoryFinding = new(
            Analyzer: "Memory Analysis",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Memory pressure",
            Evidence: "Heap pressure observed",
            Recommendation: "Investigate retention",
            Tags: ["memory"],
            Fingerprint: "mem-1");

        InsightFinding threadFinding = new(
            Analyzer: "Thread Analysis",
            Category: "Threads",
            Severity: FindingSeverity.Warning,
            Title: "Thread contention",
            Evidence: "Blocked threads observed",
            Recommendation: "Inspect lock owners",
            Tags: ["threads"],
            Fingerprint: "thr-1");

        AnalyzerRunResult memoryRun = CreateRun("MemoryAnalyzer", memoryFinding);
        AnalyzerRunResult threadRun = CreateRun("ThreadAnalyzer", threadFinding);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/domain-map.dmp",
            runs: [memoryRun, threadRun],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders:
            [
                new StubAnalyzerSectionBuilder("MemoryAnalyzer", "Memory"),
                new StubAnalyzerSectionBuilder("ThreadAnalyzer", "Threads")
            ],
            reportBuilders: []);

        doc.Domains.Should().NotBeNull();
        doc.Domains!.Should().ContainSingle(d => d.Domain == "Memory");
        doc.Domains.Should().ContainSingle(d => d.Domain == "Threads");

        ReportDomainSection memoryDomain = doc.Domains.Single(d => d.Domain == "Memory");
        ReportDomainSection threadsDomain = doc.Domains.Single(d => d.Domain == "Threads");

        memoryDomain.DomainInsights.Should().ContainSingle(f => f.Analyzer == "Memory Analysis");
        threadsDomain.DomainInsights.Should().ContainSingle(f => f.Analyzer == "Thread Analysis");
    }

    [Fact]
    public void Serialize_ShouldExcludeInfoConfidenceAndDiagnostics_FromDomainInsights()
    {
        InsightFinding analyzerSignal = new(
            Analyzer: "Retention Analysis",
            Category: "Retention",
            Severity: FindingSeverity.Warning,
            Title: "Retention hotspot",
            Evidence: "Large retained graph detected",
            Recommendation: "Inspect root paths",
            Tags: ["retention"],
            Fingerprint: "ret-main");

        InsightFinding confidenceInfo = new(
            Analyzer: "GC Root Analysis",
            Category: "Confidence",
            Severity: FindingSeverity.Info,
            Title: "Root path search capped (10 paths truncated)",
            Evidence: "Traversal budget capped some paths.",
            Recommendation: "Treat as indicative only.",
            Tags: ["confidence", "cap"],
            Fingerprint: "conf-cap");

        InsightFinding diagnosticsInfo = new(
            Analyzer: "Retention Analysis",
            Category: "Diagnostics",
            Severity: FindingSeverity.Info,
            Title: "Reference tracking was capped",
            Evidence: "Tracking limit reached.",
            Recommendation: "Increase tracking caps.",
            Tags: ["analysis-quality"],
            Fingerprint: "diag-cap");

        AnalyzerRunResult run = CreateRun("RetentionAnalyzer", analyzerSignal);
        AnalyzerRunResult gcRun = CreateRun("GCRootAnalyzer", confidenceInfo);
        AnalyzerRunResult diagRun = CreateRun("RetentionAnalyzer", diagnosticsInfo);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/info-filter.dmp",
            runs: [run, gcRun, diagRun],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders:
            [
                new StubAnalyzerSectionBuilder("RetentionAnalyzer", "Retention"),
                new StubAnalyzerSectionBuilder("GCRootAnalyzer", "GC Roots")
            ],
            reportBuilders: []);

        doc.Domains.Should().NotBeNull();
        int insightCount = doc.Domains!
            .SelectMany(d => d.DomainInsights)
            .Count();

        insightCount.Should().Be(1);
        doc.Domains.SelectMany(d => d.DomainInsights)
            .Should().ContainSingle(f => f.Title == "Retention hotspot");
        doc.Domains.SelectMany(d => d.DomainInsights)
            .Should().NotContain(f => f.Title.Contains("capped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Serialize_ShouldEmitDeterministicTopActions_WithFactorBreakdown()
    {
        InsightFinding warningA = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Retention risk alpha",
            Evidence: "Growing retained graph",
            Recommendation: "Break retention chain",
            Tags: ["memory", "runtime"],
            Fingerprint: "same-priority-a");

        InsightFinding warningB = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Retention risk beta",
            Evidence: "Growing retained graph",
            Recommendation: "Break retention chain",
            Tags: ["memory", "runtime"],
            Fingerprint: "same-priority-b");

        AnalyzerRunResult runA = CreateRun("RetentionAnalyzer", warningA);
        AnalyzerRunResult runB = CreateRun("RetentionAnalyzer", warningB);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/top-actions.dmp",
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.ExecutiveSummary.Should().NotBeNull();
        doc.ExecutiveSummary!.TopActions.Should().NotBeNull();
        doc.ExecutiveSummary.TopActions!.Should().HaveCount(2);
        doc.ExecutiveSummary.ActionScoringModelVersion.Should().Be("v1");

        RankedActionRecord first = doc.ExecutiveSummary.TopActions[0];
        RankedActionRecord second = doc.ExecutiveSummary.TopActions[1];

        first.Priority.Should().Be(1);
        second.Priority.Should().Be(2);
        first.Factors.Should().NotBeNull();
        first.Factors!.TotalScore.Should().BeGreaterThan(0);
        first.WhyNow.Should().NotBeNullOrWhiteSpace();

        // Tie-break should be deterministic on fingerprint for same-score candidates.
        first.FindingFingerprint.Should().Be("same-priority-a");
        second.FindingFingerprint.Should().Be("same-priority-b");
    }

    [Fact]
    public void Serialize_ShouldEmitTopLevelScoringModelVersionMetadata()
    {
        InsightFinding warning = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Retention risk",
            Evidence: "Growing retained graph",
            Recommendation: "Break retention chain",
            Tags: ["memory", "runtime"],
            Fingerprint: "score-meta");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/score-meta.dmp",
            runs: [CreateRun("RetentionAnalyzer", warning)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.ScoringModelVersion.Should().Be("v1");
        doc.ExecutiveSummary.Should().NotBeNull();
        doc.ExecutiveSummary!.ActionScoringModelVersion.Should().Be("v1");
    }

    [Fact]
    public void Serialize_ShouldKeepDeterministicRankedOrdering_AcrossRepeatedRuns()
    {
        InsightFinding f1 = new(
            Analyzer: "MemoryAnalyzer",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Retention trend alpha",
            Evidence: "retained objects increasing",
            Recommendation: "inspect retention roots",
            Tags: ["pipeline-correlation", "latency-bridge"],
            Fingerprint: "det-a");

        InsightFinding f2 = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Threads",
            Severity: FindingSeverity.Warning,
            Title: "Worker saturation beta",
            Evidence: "queue latency increasing",
            Recommendation: "inspect scheduler pressure",
            Tags: ["pipeline-correlation", "latency-bridge"],
            Fingerprint: "det-b");

        InsightFinding f3 = new(
            Analyzer: "RuntimeAnalyzer",
            Category: "Runtime",
            Severity: FindingSeverity.Warning,
            Title: "Timeout pressure gamma",
            Evidence: "timeouts align with queue pressure",
            Recommendation: "inspect timeout source",
            Tags: ["pipeline-correlation"],
            Fingerprint: "det-c");

        ReportSerializer serializer = new();

        AnalysisReportDocument first = serializer.Serialize(
            dumpPath: "C:/dumps/determinism.dmp",
            runs: [CreateRun("MemoryAnalyzer", f1), CreateRun("ThreadAnalyzer", f2), CreateRun("RuntimeAnalyzer", f3)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        AnalysisReportDocument second = serializer.Serialize(
            dumpPath: "C:/dumps/determinism.dmp",
            runs: [CreateRun("MemoryAnalyzer", f1), CreateRun("ThreadAnalyzer", f2), CreateRun("RuntimeAnalyzer", f3)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        IReadOnlyList<string> firstActionOrder = first.ExecutiveSummary!.TopActions!
            .Select(a => a.FindingFingerprint)
            .ToList();
        IReadOnlyList<string> secondActionOrder = second.ExecutiveSummary!.TopActions!
            .Select(a => a.FindingFingerprint)
            .ToList();

        firstActionOrder.Should().Equal(secondActionOrder);

        IReadOnlyList<string> firstCorrelationOrder = first.CorrelationEvents!
            .Select(c => c.EventType + "|" + c.Title)
            .ToList();
        IReadOnlyList<string> secondCorrelationOrder = second.CorrelationEvents!
            .Select(c => c.EventType + "|" + c.Title)
            .ToList();

        firstCorrelationOrder.Should().Equal(secondCorrelationOrder);
    }

    [Fact]
    public void Serialize_ShouldRequireVerification_ForLowConfidenceCriticalTopAction()
    {
        InsightFinding critical = new(
            Analyzer: "CrashAnalyzer",
            Category: "Crash",
            Severity: FindingSeverity.Critical,
            Title: "Intermittent crash signature",
            Evidence: "Partial dump evidence",
            Recommendation: "Guard failing call path",
            Tags: ["runtime"],
            Fingerprint: "crit-low-conf",
            ConfidenceScore: 0.30);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/crit-low-conf.dmp",
            runs: [CreateRun("CrashAnalyzer", critical)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.ExecutiveSummary.Should().NotBeNull();
        doc.ExecutiveSummary!.TopActions.Should().NotBeNullOrEmpty();

        RankedActionRecord top = doc.ExecutiveSummary.TopActions![0];
        top.Validation.Should().NotBeNullOrWhiteSpace();
        top.WhyNow.Should().Contain("Confidence is low");
        top.Confidence.Should().NotBeNull();
        top.Confidence!.Composite.Should().BeLessThan(0.45);
    }

    [Fact]
    public void Serialize_ShouldPropagateConfidenceCaveats_InTopActions()
    {
        InsightFinding warning = new(
            Analyzer: "RetentionAnalyzer",
            Category: "Leak",
            Severity: FindingSeverity.Warning,
            Title: "Heuristic retention signature",
            Evidence: "Approximate object growth estimate",
            Recommendation: "Verify retained roots before cleanup",
            Tags: ["memory", "retention"],
            Fingerprint: "warn-caveat",
            ConfidenceScore: 0.42,
            Caveats: ["heuristic estimate", "partial evidence"]);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/warn-caveat.dmp",
            runs: [CreateRun("RetentionAnalyzer", warning)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        RankedActionRecord top = doc.ExecutiveSummary!.TopActions![0];
        top.Confidence.Should().NotBeNull();
        top.Confidence!.Caveats.Should().NotBeNullOrEmpty();
        top.Confidence.HeuristicPenalty.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void Serialize_ShouldGenerateCorrelationEvents_ForSharedCrossDomainTags()
    {
        InsightFinding memory = new(
            Analyzer: "MemoryAnalyzer",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Pinned object increase",
            Evidence: "Pinned segment occupancy rising",
            Recommendation: "Review pinning usage",
            Tags: ["runtime-coupling", "memory"],
            Fingerprint: "corr-memory");

        InsightFinding threads = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Threads",
            Severity: FindingSeverity.Warning,
            Title: "Worker starvation pattern",
            Evidence: "Thread pool starvation observed",
            Recommendation: "Tune worker scheduling",
            Tags: ["runtime-coupling", "threads"],
            Fingerprint: "corr-threads");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/correlation.dmp",
            runs: [CreateRun("MemoryAnalyzer", memory), CreateRun("ThreadAnalyzer", threads)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.CorrelationEvents.Should().NotBeNullOrEmpty();
        CorrelationEventRecord evt = doc.CorrelationEvents![0];
        evt.EventType.Should().Be("co-move");
        evt.SignalKeys.Should().Contain("tag:runtime-coupling");
        evt.Domains.Should().Contain("Memory");
        evt.Domains.Should().Contain("Threads");
    }

    [Fact]
    public void Serialize_ShouldDeterministicallyMergeSignalKeys_ForDuplicateCorrelationCandidates()
    {
        InsightFinding memory = new(
            Analyzer: "MemoryAnalyzer",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Request latency and retention increase",
            Evidence: "Latency increase observed with shared metric key",
            Recommendation: "Correlate with thread pressure",
            Tags: ["pipeline-correlation", "latency-bridge"],
            Fingerprint: "merge-memory");

        InsightFinding threads = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Threads",
            Severity: FindingSeverity.Warning,
            Title: "Request latency and thread pool saturation",
            Evidence: "Latency increase observed with shared metric key",
            Recommendation: "Correlate with retention pressure",
            Tags: ["pipeline-correlation", "latency-bridge"],
            Fingerprint: "merge-threads");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/correlation-merge.dmp",
            runs: [CreateRun("MemoryAnalyzer", memory), CreateRun("ThreadAnalyzer", threads)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.CorrelationEvents.Should().NotBeNullOrEmpty();
        CorrelationEventRecord evt = doc.CorrelationEvents![0];

        evt.SignalKeys.Should().Contain("tag:pipeline-correlation");
        evt.SignalKeys.Should().Contain("tag:latency-bridge");

        List<string> ordered = evt.SignalKeys.ToList();
        List<string> expected = ordered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        ordered.Should().Equal(expected);
    }

    [Fact]
    public void Serialize_ShouldClusterOverlappingCorrelationCandidates_BeyondExactSourceMatch()
    {
        InsightFinding memory = new(
            Analyzer: "MemoryAnalyzer",
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Latency bridge signal in memory path",
            Evidence: "Retention pressure aligns with latency increase",
            Recommendation: "Inspect retention path",
            Tags: ["alpha-bridge"],
            Fingerprint: "cluster-mem");

        InsightFinding threads = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Threads",
            Severity: FindingSeverity.Warning,
            Title: "Latency bridge signal in thread pool",
            Evidence: "Scheduling pressure aligns with latency increase",
            Recommendation: "Inspect pool saturation",
            Tags: ["alpha-bridge", "beta-bridge"],
            Fingerprint: "cluster-thr");

        InsightFinding runtime = new(
            Analyzer: "RuntimeAnalyzer",
            Category: "Runtime",
            Severity: FindingSeverity.Warning,
            Title: "Timeout bridge signal in runtime",
            Evidence: "Connection timeout pattern aligns with pressure",
            Recommendation: "Inspect timeout root cause",
            Tags: ["beta-bridge"],
            Fingerprint: "cluster-run");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/correlation-cluster.dmp",
            runs: [CreateRun("MemoryAnalyzer", memory), CreateRun("ThreadAnalyzer", threads), CreateRun("RuntimeAnalyzer", runtime)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.CorrelationEvents.Should().NotBeNullOrEmpty();

        CorrelationEventRecord? cluster = doc.CorrelationEvents!
            .FirstOrDefault(e => e.SignalKeys.Contains("tag:alpha-bridge") && e.SignalKeys.Contains("tag:beta-bridge"));

        cluster.Should().NotBeNull();
        cluster!.SourceFingerprints.Should().Contain("cluster-mem");
        cluster.SourceFingerprints.Should().Contain("cluster-thr");
        cluster.SourceFingerprints.Should().Contain("cluster-run");
        cluster.Domains.Should().Contain("Memory");
        cluster.Domains.Should().Contain("Threads");
        cluster.Domains.Should().Contain("Runtime");
    }

    [Fact]
    public void Serialize_ShouldEmitConflictCorrelationEvent_WhenSeverityDisagreesAcrossDomains()
    {
        InsightFinding critical = new(
            Analyzer: "MemoryAnalyzer",
            Category: "Memory",
            Severity: FindingSeverity.Critical,
            Title: "Pinned object surge",
            Evidence: "Pinned segment pressure rising",
            Recommendation: "Review pinning behavior",
            Tags: ["pipeline-correlation"],
            Fingerprint: "corr-critical",
            ConfidenceScore: 0.91);

        InsightFinding info = new(
            Analyzer: "ThreadAnalyzer",
            Category: "Threads",
            Severity: FindingSeverity.Info,
            Title: "Scheduler stall hint",
            Evidence: "Intermittent scheduling delays",
            Recommendation: "Validate worker distribution",
            Tags: ["pipeline-correlation"],
            Fingerprint: "corr-info",
            ConfidenceScore: 0.22);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/correlation-conflict.dmp",
            runs: [CreateRun("MemoryAnalyzer", critical), CreateRun("ThreadAnalyzer", info)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.CorrelationEvents.Should().NotBeNullOrEmpty();
        CorrelationEventRecord evt = doc.CorrelationEvents![0];
        evt.EventType.Should().Be("conflict");
        evt.Rationale.Should().Contain("require verification");
    }

    [Fact]
    public void MarkdownFormatter_ShouldRenderActionConfidenceAndCorrelationSignals()
    {
        SingleDumpReportDocument doc = new()
        {
            DumpPath = "C:/dumps/markdown-parity.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            ExecutiveSummary = new ExecutiveSummaryRecord(
                TotalManagedBytes: 123,
                LeakLikelihoodScore: 22,
                GcPressureScore: 31,
                ThreadContentionScore: 15,
                TopRecommendations: [])
            {
                TopActions =
                [
                    new RankedActionRecord(
                        Priority: 1,
                        Title: "Thread pool starvation",
                        Action: "Tune worker limits",
                        Impact: "High near-term risk",
                        WhyNow: "Warning signal with elevated risk.",
                        FindingFingerprint: "md-top-action",
                        Analyzer: "ThreadAnalyzer",
                        Validation: "Confirm with runtime counters.",
                        Confidence: new ActionConfidenceRecord(
                            EvidenceCompleteness: 0.75,
                            CrossAnalyzerConsistency: 0.60,
                            HeuristicPenalty: 0.08,
                            CoverageFreshness: 0.85,
                            Composite: 0.58,
                            Caveats: ["partial evidence"]))
                ],
                ActionScoringModelVersion = "v1"
            },
            CorrelationEvents =
            [
                new CorrelationEventRecord(
                    EventId: System.Guid.NewGuid().ToString("D"),
                    EventType: "co-move",
                    Title: "Potential cross-domain coupling on tag 'runtime-coupling'",
                    Rationale: "Signal appears across 2 domains from 2 findings.",
                    Confidence: 0.5,
                    Domains: ["Memory", "Threads"],
                    SnapshotIndices: System.Array.Empty<int>(),
                    SignalKeys: ["runtime-coupling"],
                    SourceFingerprints: ["a", "b"],
                    PrimarySnapshotIndex: null)
            ]
        };

        string output = new MarkdownCanonicalReportFormatter().Render(doc);

        output.Should().Contain("### Action Queue");
        output.Should().Contain("Confidence: 0.58");
        output.Should().Contain("Caveats: partial evidence");
        output.Should().Contain("### Cross-Domain Correlation Signals");
        output.Should().Contain("runtime-coupling");
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

    private sealed class StubAnalyzerSectionBuilder(string analyzerName, string displayTitle) : IAnalyzerSectionBuilder
    {
        public string AnalyzerName { get; } = analyzerName;
        public string DisplayTitle { get; } = displayTitle;
        public int SortOrder => 0;

        public bool CanHandle(AnalyzerDomainResult result) => true;

        public AnalyzerDetailSection Build(AnalyzerDomainResult result) =>
            new(
                AnalyzerName: AnalyzerName,
                DisplayTitle: DisplayTitle,
                SortOrder: SortOrder,
                Blocks: [new TextBlock("stub")]);
    }
}
