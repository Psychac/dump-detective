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

        // Finalizer-related findings are emitted by FinalizableObjectFindingGenerator.

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
