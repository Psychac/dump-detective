using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class MemoryLeakFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Memory Leak Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is MemoryLeakDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not MemoryLeakDomainResult r) return [];

        var findings = new List<InsightFinding>(capacity: 4);

        if (r.FinalizerQueueCount >= 1000)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Leak",
                Severity: FindingSeverity.Critical,
                Title: "Finalizer queue backlog is very high",
                Evidence: $"{r.FinalizerQueueCount:N0} objects are waiting for finalization.",
                Recommendation: "Investigate finalizers and implement IDisposable/using patterns to reduce finalizer pressure.",
                Tags: ["finalizer", "memory-leak", "gc"],
                MetricValue: r.FinalizerQueueCount,
                MetricUnit: "finalizer-objects"));
        }
        else if (r.FinalizerQueueCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Leak",
                Severity: FindingSeverity.Warning,
                Title: "Finalizer queue contains pending objects",
                Evidence: $"{r.FinalizerQueueCount:N0} objects are waiting for finalization.",
                Recommendation: "Review top finalizable types and avoid unnecessary finalizers.",
                Tags: ["finalizer", "memory"],
                MetricValue: r.FinalizerQueueCount,
                MetricUnit: "finalizer-objects"));
        }

        if (r.DuplicateStringPatternCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Optimization",
                Severity: FindingSeverity.Warning,
                Title: "High duplicate string pressure detected",
                Evidence: $"{r.DuplicateStringPatternCount:N0} duplicate string patterns with ~{FormatHelper.FormatBytes(r.DuplicateStringWastedBytes)} estimated waste.",
                Recommendation: "Consider string interning/pooling or de-duplicating repeated payloads.",
                Tags: ["string", "memory", "allocation"],
                MetricValue: r.DuplicateStringWastedBytes,
                MetricUnit: "wasted-bytes"));
        }

        if (r.HighlyReferencedObjectCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Leak",
                Severity: r.HighlyReferencedObjectCount >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title: "Highly referenced objects detected",
                Evidence: $"{r.HighlyReferencedObjectCount:N0} objects with abnormally high incoming reference counts were detected.",
                Recommendation: "Inspect root paths and long-lived graphs retaining these objects.",
                Tags: ["retention", "references", "memory-leak"],
                MetricValue: r.HighlyReferencedObjectCount,
                MetricUnit: "objects"));
        }

        if (r.SkippedReferenceAddresses > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Diagnostics",
                Severity: FindingSeverity.Info,
                Title: "Reference tracking was capped",
                Evidence: $"Skipped {r.SkippedReferenceAddresses:N0} references because the tracking limit was reached.",
                Recommendation: "Increase MaxReferenceAddressesToTrack for deeper incoming-reference coverage.",
                Tags: ["analysis-quality", "references"],
                MetricValue: r.SkippedReferenceAddresses,
                MetricUnit: "references"));
        }

        return findings;
    }
}
