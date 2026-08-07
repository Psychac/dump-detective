using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class StringFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "String Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is StringDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not StringDomainResult r) return [];

        var findings = new List<InsightFinding>(capacity: 4);

        if (r.DuplicationRatio > 0.5 && r.DuplicatePatternCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Optimization",
                Severity: FindingSeverity.Critical,
                Title: "Extreme string duplication detected",
                Evidence: $"{r.DuplicationRatio:P0} of all string objects are duplicates — {r.DuplicatePatternCount:N0} patterns wasting ~{FormatHelper.FormatBytes(r.DuplicateWastedBytes)}.",
                Recommendation: "Use string interning (string.Intern), pooling, or de-duplication at ingestion points to eliminate redundant allocations.",
                Tags: ["string", "memory", "allocation"],
                MetricValue: r.DuplicateWastedBytes,
                MetricUnit: "wasted-bytes"));
        }
        else if (r.DuplicatePatternCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Optimization",
                Severity: FindingSeverity.Warning,
                Title: "High duplicate string pressure detected",
                Evidence: $"{r.DuplicatePatternCount:N0} duplicate string patterns with ~{FormatHelper.FormatBytes(r.DuplicateWastedBytes)} estimated waste ({r.DuplicationRatio:P0} duplication ratio).",
                Recommendation: "Consider string interning or pooling for frequently repeated payloads.",
                Tags: ["string", "memory", "allocation"],
                MetricValue: r.DuplicateWastedBytes,
                MetricUnit: "wasted-bytes"));
        }

        if (r.LohStringBytes > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Large strings allocated on LOH",
                Evidence: $"{FormatHelper.FormatBytes(r.LohStringBytes)} of string data resides on the Large Object Heap (individual strings > 85 KB).",
                Recommendation: "Avoid creating very long strings; split or stream large text data to prevent LOH fragmentation.",
                Tags: ["string", "loh", "memory"],
                MetricValue: r.LohStringBytes,
                MetricUnit: "bytes"));
        }

        if (r.VeryLongStrings?.Count > 0)
        {
            ulong veryLongTotalSize = 0;
            foreach (var entry in r.VeryLongStrings)
                veryLongTotalSize += entry.SizeBytes;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "Very long strings detected",
                Evidence: $"{r.VeryLongStrings.Count:N0} very long string(s) totaling {FormatHelper.FormatBytes(veryLongTotalSize)} (individual threshold > 85 KB). These block GC compaction and fragment the Large Object Heap.",
                Recommendation: "Refactor to avoid allocating extremely long strings; use ReadOnlySpan<char>, StringBuilder with limited buffering, or streaming APIs.",
                Tags: ["string", "loh", "fragmentation", "memory"],
                MetricValue: veryLongTotalSize,
                MetricUnit: "bytes"));
        }

        if (r.PctOfManagedHeap > 20.0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Strings dominate managed heap",
                Evidence: $"Strings consume {r.PctOfManagedHeap:F1}% of total managed heap ({FormatHelper.FormatBytes(r.TotalStringMemoryBytes)}).",
                Recommendation: "Review string retention roots; consider ReadOnlySpan<char>, StringPool, or lazy string resolution.",
                Tags: ["string", "memory"],
                MetricValue: r.TotalStringMemoryBytes,
                MetricUnit: "bytes"));
        }

        if (!r.DeduplicationSkipped && r.SamplingCoverage < 0.05 && r.SamplingCoverage > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Analysis",
                Severity: FindingSeverity.Info,
                Title: "Low sampling coverage on deduplication analysis",
                Evidence: $"String deduplication was performed on a sample covering only {r.SamplingCoverage * 100.0:F1}% of all strings ({r.StringsSampled:N0} sampled out of ~{(int)(r.StringsSampled / Math.Max(r.SamplingCoverage, 0.01)):N0} total). Results may not be representative of heap-wide patterns.",
                Recommendation: "Increase sampling limits or re-run with Full dedup mode to validate findings. Current results should be treated as indicative, not definitive.",
                Tags: ["sampling", "coverage", "deduplication"],
                MetricValue: r.SamplingCoverage * 100.0,
                MetricUnit: "percent"));
        }

        return findings;
    }
}
