using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
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
    private IReadOnlyList<IAnalyzerSectionBuilder> _builders = null!;
    private ReportSerializer _serializer = null!;
    private MarkdownCanonicalReportFormatter _markdown = null!;
    private HtmlCanonicalReportFormatter _html = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runs = BuildRuns(250);
        _builders = [];
        _serializer = new ReportSerializer();
        _markdown = new MarkdownCanonicalReportFormatter();
        _html = new HtmlCanonicalReportFormatter();
    }

    [Benchmark(Description = "ReportSerializer - serialize (duplicate heavy)")]
    public object SerializeCanonical_DuplicateHeavy()
    {
        return _serializer.Serialize("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3), _builders);
    }

    [Benchmark(Description = "Formatter - markdown render large sections")]
    public string RenderMarkdown_LargeSections()
    {
        var doc = _serializer.Serialize("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3), _builders);
        return _markdown.Render(doc);
    }

    [Benchmark(Description = "Formatter - html render long values")]
    public string RenderHtml_LongValues()
    {
        var doc = _serializer.Serialize("C:/benchmarks/duplicate-heavy.dmp", _runs, TimeSpan.FromSeconds(3), _builders);
        return _html.Render(doc);
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
                Findings: [finding],
                FindingCount: 1,
                WarningCount: result.Warnings.Count,
                ObjectScanCount: 1000 + i,
                CacheHits: 700 + (i % 100),
                CacheMisses: 300 + (i % 50)));
        }

        return runs;
    }
}
