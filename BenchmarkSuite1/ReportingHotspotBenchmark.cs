using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;
using System;
using System.Collections.Generic;

namespace BenchmarkSuite1;

[MemoryDiagnoser]
[ShortRunJob]
public class ReportingHotspotBenchmark
{
    private IReadOnlyList<AnalyzerRunResult> _runs = null!;
    private MarkdownCanonicalReportFormatter _markdown = null!;
    private HtmlCanonicalReportFormatter _html = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runs = BuildRuns(250);
        _markdown = new MarkdownCanonicalReportFormatter();
        _html = new HtmlCanonicalReportFormatter();
    }

    [Benchmark(Description = "ReportBuilder - compose canonical report (duplicate heavy)")]
    public object ComposeCanonical_DuplicateHeavy()
    {
        return ReportBuilder.ComposeCanonicalReport("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3));
    }

    [Benchmark(Description = "Formatter - markdown render large sections")]
    public string RenderMarkdown_LargeSections()
    {
        var report = ReportBuilder.ComposeCanonicalReport("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3));
        return _markdown.Render(report);
    }

    [Benchmark(Description = "Formatter - html render long values")]
    public string RenderHtml_LongValues()
    {
        var report = ReportBuilder.ComposeCanonicalReport("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3));
        return _html.Render(report);
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
                Tags: ["benchmark", "duplicate"],
                Fingerprint: fingerprint);

            GenericAnalyzerDomainResult result = new()
            {
                AnalyzerName = $"Analyzer-{i % 10}",
                Category = "Leak",
                Findings = [finding],
                Metrics = new Dictionary<string, object?>
                {
                    ["objectScans"] = 1000 + i,
                    ["cacheHits"] = 700 + (i % 100),
                    ["cacheMisses"] = 300 + (i % 50)
                }
            };

            runs.Add(new AnalyzerRunResult(
                AnalyzerName: result.AnalyzerName,
                Status: AnalyzerExecutionStatus.Success,
                Duration: TimeSpan.FromMilliseconds(5 + (i % 4)),
                Result: result,
                ErrorMessage: null,
                ErrorType: null,
                FindingCount: result.Findings.Count,
                WarningCount: result.Warnings.Count,
                ObjectScanCount: 1000 + i,
                CacheHits: 700 + (i % 100),
                CacheMisses: 300 + (i % 50)));
        }

        return runs;
    }
}
