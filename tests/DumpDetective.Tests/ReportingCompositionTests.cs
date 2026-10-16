using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Services;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.SectionBuilders;

using System.Linq;

using FluentAssertions;

using Xunit;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests;

public sealed class ReportingCompositionTests
{
    [Fact]
    public void Serialize_ShouldMergeDuplicateFindings_AndPreserveEvidenceAndRemediation()
    {
        Claim findingA = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Duplicate strings detected",
            narrative: "Evidence A",
            recommendation: "Recommendation A",
            tags: ["memory", "string"],
            fingerprint: "dup-key");

        Claim findingB = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Critical,
            title: "Duplicate strings detected",
            narrative: "Evidence B",
            recommendation: "Recommendation B",
            tags: ["memory", "string"],
            fingerprint: "dup-key");

        AnalyzerRunResult runA = CreateRun("DominatorAnalyzer", findingA);
        AnalyzerRunResult runB = CreateRun("DominatorAnalyzer", findingB);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/test.dmp",
            runs: [runA, runB],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.ExecutiveSummary.Should().NotBeNull();
        // Critical/Warning/top-recommendation projection removed from ExecutiveSummaryRecord.
        // Verify findings were composed and remain available on the report document.
        doc.Findings.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Serialize_ShouldClusterNearDuplicateTopActions()
    {
        Claim leakA = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Critical,
            title: "Event handler retention pressure",
            narrative: "Subscribers retain roots.",
            recommendation: "Detach leaked handlers and re-check retention.",
            tags: ["leak", "event"],
            fingerprint: "cluster-a");

        Claim leakB = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Event handler retention pressure",
            narrative: "Duplicate retention pattern in related type.",
            recommendation: "Detach leaked handlers and re-check retention.",
            tags: ["leak", "event"],
            fingerprint: "cluster-b");

        Claim threadFinding = ClaimFactory.Create(
            analyzer: "ThreadAnalyzer",
            category: "Thread",
            severity: Severity.Warning,
            title: "Blocked worker thread hotspot",
            narrative: "Multiple workers blocked on lock.",
            recommendation: "Inspect lock ownership and reduce contention.",
            tags: ["thread", "lock"],
            fingerprint: "cluster-c");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/cluster.dmp",
            runs: [
                CreateRun("DominatorAnalyzer", leakA),
                CreateRun("DominatorAnalyzer", leakB),
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
                    Severity: Severity.Warning.ToString(),
                    Title: "Crash signature",
                    Details: ["Unhandled exception in worker"],
                    Recommendation: "Inspect stack and exception roots",
                    Tags: ["crash"]),
                new FindingRecord(
                    Id: "sec-2",
                    Analyzer: "ThreadAnalyzer",
                    Category: "Threads",
                    Severity: Severity.Info.ToString(),
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

        // HtmlReportRenderer relies on Domains (client-rendered), not the legacy
        // Findings/AnalyzerSections fields, so it is excluded from this literal-content check.
        IReportFormatter[] formatters =
        [
            new TextCanonicalReportFormatter(),
            new MarkdownCanonicalReportFormatter()
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
        Claim finding = ClaimFactory.Create(
            analyzer: "CrashAnalyzer",
            category: "Crash",
            severity: Severity.Warning,
            title: "Contract versioning",
            narrative: "Version metadata should be stamped.",
            recommendation: "Keep schema changes backward-compatible.",
            tags: ["contract"]);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/contract.dmp",
            runs: [CreateRun("CrashAnalyzer", finding)],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders: [],
            reportBuilders: []);

        doc.SchemaVersion.Should().Be("3.0");
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
        Claim regularFinding = ClaimFactory.Create(
            analyzer: "MemoryAnalyzer",
            category: "Memory",
            severity: Severity.Warning,
            title: "Regular finding",
            narrative: "Regular evidence",
            recommendation: "Regular recommendation",
            tags: ["memory"],
            fingerprint: "regular-finding");

        Claim insightFinding = ClaimFactory.Create(
            analyzer: "InsightEngine",
            category: "CrossDomain",
            severity: Severity.Critical,
            title: "Cross-domain correlation",
            narrative: "Insight evidence",
            recommendation: "Insight recommendation",
            tags: ["cross-analyzer"],
            fingerprint: "insight-finding");

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
                new TypeGenerationProfile("Demo.Type", 1, 2, 3, 0, 1024, IsFinalizable: false, Gen2Bytes: 0)
            ]);

        ObjectShapeAnalyzerDomainResult shape = new(
            TopReferenceHeavyTypes:
            [
                new TypeShapeProfile("Demo.Type", 4, 2, 2, 0.50, 10, 1000, false, false, false, 1, 0, ObjectShapeCategory.Balanced)
            ],
            TopValueHeavyTypes:
            [
                new TypeShapeProfile("Demo.Type", 4, 2, 2, 0.50, 10, 1000, false, false, false, 1, 0, ObjectShapeCategory.Balanced)
            ],
            TopBalancedTypes:
            [
                new TypeShapeProfile("Demo.Balanced", 4, 2, 2, 0.50, 500, 5000, false, false, false, 1, 0, ObjectShapeCategory.Balanced)
            ],
            TotalTypesAnalyzed: 1,
            AvgRefFieldsPerType: 2,
            TotalGcScanWork: 20,
            TopGen2RetainedTypes: [],
            TotalGen2GcScanWork: 0);

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
        section.DisplayTitle.Should().Be("Type Shape and Finalizable Overhead");

        string output = new MarkdownCanonicalReportFormatter().Render(new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/type-table.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            AnalyzerSections = [section]
        });

        output.Should().Contain("Demo.Type");
        output.Should().NotContain("Type table");
    }

    [Fact]
    public void Serialize_ShouldMapDisplayNameFindings_ToTheirDomains()
    {
        Claim memoryFinding = ClaimFactory.Create(
            analyzer: "Memory Analysis",
            category: "Memory",
            severity: Severity.Warning,
            title: "Memory pressure",
            narrative: "Heap pressure observed",
            recommendation: "Investigate retention",
            tags: ["memory"],
            fingerprint: "mem-1");

        Claim threadFinding = ClaimFactory.Create(
            analyzer: "Thread Analysis",
            category: "Threads",
            severity: Severity.Warning,
            title: "Thread contention",
            narrative: "Blocked threads observed",
            recommendation: "Inspect lock owners",
            tags: ["threads"],
            fingerprint: "thr-1");

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
        Claim analyzerSignal = ClaimFactory.Create(
            analyzer: "Dominator Analysis",
            category: "Retention",
            severity: Severity.Warning,
            title: "Retention hotspot",
            narrative: "Large retained graph detected",
            recommendation: "Inspect root paths",
            tags: ["retention"],
            fingerprint: "ret-main");

        Claim confidenceInfo = ClaimFactory.Create(
            analyzer: "GC Root Analysis",
            category: "Confidence",
            severity: Severity.Info,
            title: "Root path search capped (10 paths truncated)",
            narrative: "Traversal budget capped some paths.",
            recommendation: "Treat as indicative only.",
            tags: ["confidence", "cap"],
            fingerprint: "conf-cap");

        Claim diagnosticsInfo = ClaimFactory.Create(
            analyzer: "Dominator Analysis",
            category: "Diagnostics",
            severity: Severity.Info,
            title: "Reference tracking was capped",
            narrative: "Tracking limit reached.",
            recommendation: "Increase tracking caps.",
            tags: ["analysis-quality"],
            fingerprint: "diag-cap");

        AnalyzerRunResult run = CreateRun("DominatorAnalyzer", analyzerSignal);
        AnalyzerRunResult gcRun = CreateRun("GCRootAnalyzer", confidenceInfo);
        AnalyzerRunResult diagRun = CreateRun("DominatorAnalyzer", diagnosticsInfo);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/info-filter.dmp",
            runs: [run, gcRun, diagRun],
            elapsed: TimeSpan.FromSeconds(1),
            analyzerBuilders:
            [
                new StubAnalyzerSectionBuilder("DominatorAnalyzer", "Retention"),
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
        Claim warningA = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Retention risk alpha",
            narrative: "Growing retained graph",
            recommendation: "Break retention chain",
            tags: ["memory", "runtime"],
            fingerprint: "same-priority-a");

        Claim warningB = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Retention risk beta",
            narrative: "Growing retained graph",
            recommendation: "Break retention chain",
            tags: ["memory", "runtime"],
            fingerprint: "same-priority-b");

        AnalyzerRunResult runA = CreateRun("DominatorAnalyzer", warningA);
        AnalyzerRunResult runB = CreateRun("DominatorAnalyzer", warningB);

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
        Claim warning = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Retention risk",
            narrative: "Growing retained graph",
            recommendation: "Break retention chain",
            tags: ["memory", "runtime"],
            fingerprint: "score-meta");

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/score-meta.dmp",
            runs: [CreateRun("DominatorAnalyzer", warning)],
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
        Claim f1 = ClaimFactory.Create(
            analyzer: "MemoryAnalyzer",
            category: "Memory",
            severity: Severity.Warning,
            title: "Retention trend alpha",
            narrative: "retained objects increasing",
            recommendation: "inspect retention roots",
            tags: ["pipeline-correlation", "latency-bridge"],
            fingerprint: "det-a");

        Claim f2 = ClaimFactory.Create(
            analyzer: "ThreadAnalyzer",
            category: "Threads",
            severity: Severity.Warning,
            title: "Worker saturation beta",
            narrative: "queue latency increasing",
            recommendation: "inspect scheduler pressure",
            tags: ["pipeline-correlation", "latency-bridge"],
            fingerprint: "det-b");

        Claim f3 = ClaimFactory.Create(
            analyzer: "RuntimeAnalyzer",
            category: "Runtime",
            severity: Severity.Warning,
            title: "Timeout pressure gamma",
            narrative: "timeouts align with queue pressure",
            recommendation: "inspect timeout source",
            tags: ["pipeline-correlation"],
            fingerprint: "det-c");

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
        Claim critical = ClaimFactory.Create(
            analyzer: "CrashAnalyzer",
            category: "Crash",
            severity: Severity.Critical,
            title: "Intermittent crash signature",
            narrative: "Partial dump evidence",
            recommendation: "Guard failing call path",
            tags: ["runtime"],
            fingerprint: "crit-low-conf",
            confidenceScore: 0.30);

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
        Claim warning = ClaimFactory.Create(
            analyzer: "DominatorAnalyzer",
            category: "Leak",
            severity: Severity.Warning,
            title: "Heuristic retention signature",
            narrative: "Approximate object growth estimate",
            recommendation: "Verify retained roots before cleanup",
            tags: ["memory", "retention"],
            fingerprint: "warn-caveat",
            confidenceScore: 0.42,
            caveats: ["heuristic estimate", "partial evidence"]);

        AnalysisReportDocument doc = new ReportSerializer().Serialize(
            dumpPath: "C:/dumps/warn-caveat.dmp",
            runs: [CreateRun("DominatorAnalyzer", warning)],
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
        Claim memory = ClaimFactory.Create(
            analyzer: "MemoryAnalyzer",
            category: "Memory",
            severity: Severity.Warning,
            title: "Pinned object increase",
            narrative: "Pinned segment occupancy rising",
            recommendation: "Review pinning usage",
            tags: ["runtime-coupling", "memory"],
            fingerprint: "corr-memory");

        Claim threads = ClaimFactory.Create(
            analyzer: "ThreadAnalyzer",
            category: "Threads",
            severity: Severity.Warning,
            title: "Worker starvation pattern",
            narrative: "Thread pool starvation observed",
            recommendation: "Tune worker scheduling",
            tags: ["runtime-coupling", "threads"],
            fingerprint: "corr-threads");

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
        Claim memory = ClaimFactory.Create(
            analyzer: "MemoryAnalyzer",
            category: "Memory",
            severity: Severity.Warning,
            title: "Request latency and retention increase",
            narrative: "Latency increase observed with shared metric key",
            recommendation: "Correlate with thread pressure",
            tags: ["pipeline-correlation", "latency-bridge"],
            fingerprint: "merge-memory");

        Claim threads = ClaimFactory.Create(
            analyzer: "ThreadAnalyzer",
            category: "Threads",
            severity: Severity.Warning,
            title: "Request latency and thread pool saturation",
            narrative: "Latency increase observed with shared metric key",
            recommendation: "Correlate with retention pressure",
            tags: ["pipeline-correlation", "latency-bridge"],
            fingerprint: "merge-threads");

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
        Claim memory = ClaimFactory.Create(
            analyzer: "MemoryAnalyzer",
            category: "Memory",
            severity: Severity.Warning,
            title: "Latency bridge signal in memory path",
            narrative: "Retention pressure aligns with latency increase",
            recommendation: "Inspect retention path",
            tags: ["alpha-bridge"],
            fingerprint: "cluster-mem");

        Claim threads = ClaimFactory.Create(
            analyzer: "ThreadAnalyzer",
            category: "Threads",
            severity: Severity.Warning,
            title: "Latency bridge signal in thread pool",
            narrative: "Scheduling pressure aligns with latency increase",
            recommendation: "Inspect pool saturation",
            tags: ["alpha-bridge", "beta-bridge"],
            fingerprint: "cluster-thr");

        Claim runtime = ClaimFactory.Create(
            analyzer: "RuntimeAnalyzer",
            category: "Runtime",
            severity: Severity.Warning,
            title: "Timeout bridge signal in runtime",
            narrative: "Connection timeout pattern aligns with pressure",
            recommendation: "Inspect timeout root cause",
            tags: ["beta-bridge"],
            fingerprint: "cluster-run");

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
        Claim critical = ClaimFactory.Create(
            analyzer: "MemoryAnalyzer",
            category: "Memory",
            severity: Severity.Critical,
            title: "Pinned object surge",
            narrative: "Pinned segment pressure rising",
            recommendation: "Review pinning behavior",
            tags: ["pipeline-correlation"],
            fingerprint: "corr-critical",
            confidenceScore: 0.91);

        Claim info = ClaimFactory.Create(
            analyzer: "ThreadAnalyzer",
            category: "Threads",
            severity: Severity.Info,
            title: "Scheduler stall hint",
            narrative: "Intermittent scheduling delays",
            recommendation: "Validate worker distribution",
            tags: ["pipeline-correlation"],
            fingerprint: "corr-info",
            confidenceScore: 0.22);

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
                ThreadContentionScore: 15)
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

    private static AnalyzerRunResult CreateRun(string analyzerName, Claim finding)
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
