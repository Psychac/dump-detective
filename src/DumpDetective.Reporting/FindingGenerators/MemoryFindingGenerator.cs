using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class MemoryFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Memory Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is MemoryDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not MemoryDomainResult r) return [];

        var findings = new List<InsightFinding>();

        // Finding 1: High memory pressure score (≥ 70)
        if (r.MemoryPressureScore >= 70)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "High memory pressure score",
                Evidence: $"Memory pressure score: {r.MemoryPressureScore:F1}/100. Total heap: {FormatBytes(r.TotalBytes)}; LOH share: {r.LohPercent:F1}%; top-5 concentration: {r.Top5BytesPercent:F1}%.",
                Recommendation: "Heap shows elevated memory pressure from multiple dimensions. Review dominant type retention patterns and consider heap size baseline.",
                Tags: ["memory", "heap", "pressure"],
                MetricValue: r.MemoryPressureScore,
                MetricUnit: "pressure score"));
        }

        // Finding 2: High type concentration (top-5 > 80%)
        if (r.Top5BytesPercent > 80)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "High memory concentration in few types",
                Evidence: $"Top-5 types consume {r.Top5BytesPercent:F1}% of heap ({FormatBytes(r.TotalBytes * (ulong)(r.Top5BytesPercent / 100))}). Top type alone: {r.Top1BytesPercent:F1}%.",
                Recommendation: "Heap memory is concentrated in few types. Verify whether top allocators are short-lived or retained unnecessarily.",
                Tags: ["memory", "heap", "concentration"],
                MetricValue: r.Top5BytesPercent,
                MetricUnit: "% top-5"));
        }

        // Finding 3: High small-object pressure (>85% by count, indicates GC throughput risk)
        if (r.SmallObjectCountPercent > 85)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "High small-object allocation density",
                Evidence: $"{r.SmallObjectCountPercent:F1}% of heap objects are <85 bytes; these account for {r.SmallObjectBytesPercent:F1}% of heap bytes.",
                Recommendation: "High small-object density may impact GC throughput. Review allocation hot paths and consider object pooling or region-based allocation.",
                Tags: ["memory", "gc", "allocation"],
                MetricValue: r.SmallObjectCountPercent,
                MetricUnit: "% small objects"));
        }

        // Finding 4: High LOH percentage (existing finding, kept for baseline context)
        if (r.LohPercent >= 40)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Elevated Large Object Heap (LOH) usage",
                Evidence: $"LOH share: {r.LohPercent:F1}% ({FormatBytes(r.LohBytes)} of {FormatBytes(r.TotalBytes)}); {r.LohObjects:N0} large objects.",
                Recommendation: "LOH share is elevated. Review large-object allocations, retention patterns, and LOH fragmentation risk.",
                Tags: ["memory", "heap", "loh"],
                MetricValue: r.LohPercent,
                MetricUnit: "% loh"));
        }

        // If no specific findings triggered, emit baseline snapshot
        if (findings.Count == 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "Managed heap memory snapshot",
                Evidence: $"Total heap: {FormatBytes(r.TotalBytes)}; LOH: {r.LohPercent:F1}% ({FormatBytes(r.LohBytes)}); unique types: {r.UniqueTypes:N0}; pressure score: {r.MemoryPressureScore:F1}/100.",
                Recommendation: "Heap composition appears within expected range for this snapshot.",
                Tags: ["memory", "heap"],
                MetricValue: r.MemoryPressureScore,
                MetricUnit: "pressure score"));
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F2} GB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB"
        : bytes >= 1_024 ? $"{bytes / 1_024.0:F1} KB"
        : $"{bytes} B";
}
