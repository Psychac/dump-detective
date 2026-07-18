using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BenchmarkSuite1;

[MemoryDiagnoser]
[ShortRunJob]
public class ReportingHotspotBenchmark
{
    private IReadOnlyList<AnalyzerRunResult> _runs = null!;
    private IReadOnlyList<IAnalyzerSectionBuilder> _builders = null!;
    private ReportSerializer _serializer = null!;
    private MarkdownCanonicalReportFormatter _markdown = null!;
    private HtmlReportRenderer _html = null!;
    private TrendReportComposer _trendComposer = null!;
    private IReadOnlyList<AnalyzerRunResult> _trendCurrentRuns = null!;
    private TrendReportData _trendData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runs = BuildRuns(250);
        _builders = Array.Empty<IAnalyzerSectionBuilder>();
        _serializer = new ReportSerializer();
        _markdown = new MarkdownCanonicalReportFormatter();
        _html = new HtmlReportRenderer();
        _trendComposer = new TrendReportComposer(new CanonicalReportDocumentFactory(_serializer));
        _trendCurrentRuns = BuildRuns(120);
        _trendData = BuildTrendData(snapshotCount: 8, runsPerSnapshot: 120);
    }

    [Benchmark(Description = "ReportSerializer - serialize (duplicate heavy)")]
    public object SerializeCanonical_DuplicateHeavy()
    {
        return _serializer.Serialize("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3), _builders, Array.Empty<IReportSectionBuilder>());
    }

    [Benchmark(Description = "Formatter - markdown render large sections")]
    public string RenderMarkdown_LargeSections()
    {
        var doc = _serializer.Serialize("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3), _builders, Array.Empty<IReportSectionBuilder>());
        return _markdown.Render(doc);
    }

    [Benchmark(Description = "Formatter - html render long values")]
    public string RenderHtml_LongValues()
    {
        var doc = _serializer.Serialize("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3), _builders, Array.Empty<IReportSectionBuilder>());
        return _html.Render(doc);
    }

    [Benchmark(Description = "Trend composer - compare and compose snapshots")]
    public int ComposeTrend_ComparisonHeavy()
    {
        AnalysisReportDocument doc = _trendComposer.ComposeCanonicalTrendReport(
            _trendCurrentRuns,
            TimeSpan.FromSeconds(9),
            currentIncidentContext: null,
            builders: _builders,
            reportBuilders: Array.Empty<IReportSectionBuilder>(),
            trendData: _trendData);

        return doc.AnalyzerSections.Count + doc.Findings.Count;
    }

    private static IReadOnlyList<AnalyzerRunResult> BuildRuns(int count)
    {
        List<AnalyzerRunResult> runs = new(count);
        for (int i = 0; i < count; i++)
        {
            string fingerprint = $"dup-{i % 50}";
            string longValue = "VeryLongTypeName_" + new string('X', 120) + i;

            InsightFinding finding = new(
                Analyzer: $"Analyzer-{i % 10}",
                Category: "Leak",
                Severity: i % 7 == 0 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Duplicate candidate finding",
                Evidence: $"Evidence {i} {longValue}",
                Recommendation: "Review ownership and remove stale references.",
                    Tags: new[] { "benchmark", "duplicate" },
                Fingerprint: fingerprint);

            GenericAnalyzerDomainResult result = new()
            {
                AnalyzerName = $"Analyzer-{i % 10}",
                Category = "Leak"
            };

            runs.Add(new AnalyzerRunResult(
                AnalyzerName: result.AnalyzerName,
                Status: AnalyzerExecutionStatus.Success,
                Duration: TimeSpan.FromMilliseconds(5 + (i % 4)),
                Result: result,
                ErrorMessage: null,
                ErrorType: null,
                Findings: new[] { finding },
                FindingCount: 1,
                WarningCount: result.Warnings.Count,
                Diagnostics: new AnalyzerExecutionDiagnostics(
                    ObjectScanCount: 1000 + i,
                    CacheHits: 700 + (i % 100),
                    CacheMisses: 300 + (i % 50))));
        }

        return runs;
    }

    private static TrendReportData BuildTrendData(int snapshotCount, int runsPerSnapshot)
    {
        List<AnalysisSnapshot> snapshots = new(snapshotCount);
        List<IReadOnlyList<AnalyzerTrendResult>> steps = new(snapshotCount > 0 ? snapshotCount - 1 : 0);
        List<AnalyzerTrendResult> overall = new(10);
        Dictionary<string, IReadOnlyList<NewLeakSignal>> leakSignalsByAnalyzer = new(StringComparer.Ordinal);
        List<AnalyzerMetricTimeline> timeline = new(10);
        List<AnalyzerMetricTimeline> scopedTimeline = new(10);
        List<InsightFinding> newFindings = new();
        List<InsightFinding> persistentFindings = new();
        List<InsightFinding> resolvedFindings = new();

        for (int analyzerIndex = 0; analyzerIndex < 10; analyzerIndex++)
        {
            string analyzerName = $"Analyzer-{analyzerIndex:00}";
            List<MetricDelta> deltas = new()
            {
                new MetricDelta("objectScans", null, 1_000 + analyzerIndex, 1_020 + analyzerIndex, 20, 2.0, "objects", MetricTrendDirection.HigherIsWorse),
                new MetricDelta("cacheHits", null, 700 + analyzerIndex, 715 + analyzerIndex, 15, 2.1, "hits", MetricTrendDirection.HigherIsWorse)
            };

            overall.Add(new AnalyzerTrendResult(analyzerName, deltas));
            leakSignalsByAnalyzer[analyzerName] = Array.Empty<NewLeakSignal>();
                timeline.Add(new AnalyzerMetricTimeline(
                analyzerName,
                new[]
                {
                    new MetricTimelinePoint("objectScans", "objects", MetricTrendDirection.HigherIsWorse, Enumerable.Range(0, snapshotCount).Select(i => (double)(1_000 + analyzerIndex + i * 3)).ToArray()),
                    new MetricTimelinePoint("cacheHits", "hits", MetricTrendDirection.HigherIsWorse, Enumerable.Range(0, snapshotCount).Select(i => (double)(700 + analyzerIndex + i * 2)).ToArray())
                }));
            scopedTimeline.Add(new AnalyzerMetricTimeline(
                analyzerName,
                new[]
                {
                    new MetricTimelinePoint("type.bytes", "bytes", MetricTrendDirection.HigherIsWorse, Enumerable.Range(0, snapshotCount).Select(i => (double)(120_000 + analyzerIndex * 1_000 + i * 2_500)).ToArray(), Scope: "System.String"),
                    new MetricTimelinePoint("type.count", "objects", MetricTrendDirection.HigherIsWorse, Enumerable.Range(0, snapshotCount).Select(i => (double)(2_000 + analyzerIndex + i * 30)).ToArray(), Scope: "System.String")
                }));

            newFindings.Add(new InsightFinding(
                Analyzer: analyzerName,
                Category: "Trend",
                Severity: FindingSeverity.Warning,
                Title: $"New trend finding {analyzerIndex}",
                Evidence: "Synthetic trend evidence.",
                Recommendation: "Inspect the synthetic trend signal.",
                Tags: ["trend", "benchmark"],
                Fingerprint: $"trend-new-{analyzerIndex}"));

            persistentFindings.Add(new InsightFinding(
                Analyzer: analyzerName,
                Category: "Trend",
                Severity: FindingSeverity.Info,
                Title: $"Persistent trend finding {analyzerIndex}",
                Evidence: "Synthetic persistent evidence.",
                Recommendation: "Track the synthetic trend signal.",
                Tags: ["trend", "benchmark"],
                Fingerprint: $"trend-persistent-{analyzerIndex}"));
        }

        for (int snapshotIndex = 0; snapshotIndex < snapshotCount; snapshotIndex++)
        {
            List<AnalyzerRunResult> runs = new(runsPerSnapshot);
            for (int runIndex = 0; runIndex < runsPerSnapshot; runIndex++)
            {
                string analyzerName = $"Analyzer-{runIndex % 10:00}";
                GenericAnalyzerDomainResult result = new()
                {
                    AnalyzerName = analyzerName,
                    Category = "Trend"
                };

                runs.Add(new AnalyzerRunResult(
                    AnalyzerName: analyzerName,
                    Status: AnalyzerExecutionStatus.Success,
                    Duration: TimeSpan.FromMilliseconds(5 + (runIndex % 4)),
                    Result: result,
                    ErrorMessage: null,
                    ErrorType: null,
                    Findings: Array.Empty<InsightFinding>(),
                    FindingCount: 0,
                    WarningCount: 0,
                    Diagnostics: new AnalyzerExecutionDiagnostics(
                        ObjectScanCount: 1_000 + (snapshotIndex * 5) + runIndex,
                        CacheHits: 700 + (snapshotIndex * 3) + runIndex,
                        CacheMisses: 300 + runIndex)));
            }

            snapshots.Add(new AnalysisSnapshot(
                Index: snapshotIndex,
                DumpPath: $"C:/benchmarks/trend-{snapshotIndex:00}.dmp",
                Runs: runs,
                Findings: snapshotIndex % 2 == 0 ? newFindings : persistentFindings,
                DomainResults: new Dictionary<string, AnalyzerDomainResult>(),
                GeneratedAtUtc: DateTime.UtcNow.AddMinutes(snapshotIndex)));

            if (snapshotIndex > 0)
                steps.Add(overall);
        }

        return new TrendReportData(
            Steps: steps,
            Overall: overall,
            NewLeakSignalsByAnalyzer: leakSignalsByAnalyzer,
            Timeline: timeline,
            ScopedTimeline: scopedTimeline,
            Snapshots: snapshots,
            NewFindings: newFindings,
            PersistentFindings: persistentFindings,
            ResolvedFindings: resolvedFindings);
    }
}
