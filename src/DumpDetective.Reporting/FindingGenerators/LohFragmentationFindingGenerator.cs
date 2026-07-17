using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class LohFragmentationFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "LOH Fragmentation Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is LohFragmentationDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not LohFragmentationDomainResult r) return [];

        FindingSeverity severity = r.FragmentationPercent >= 30 ? FindingSeverity.Critical
            : r.FragmentationPercent >= 15 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Fragmentation",
                Severity: severity,
                Title: "LOH fragmentation assessment",
                Evidence: $"{r.FragmentationPercent:F1}% overall free-space fragmentation across {r.SegmentCount:N0} LOH segment(s).",
                Recommendation: severity == FindingSeverity.Critical
                    ? "Investigate large object allocation churn and retention; consider compaction strategies and pooling."
                    : severity == FindingSeverity.Warning
                        ? "Monitor LOH allocation patterns and reduce churn from short-lived large allocations."
                        : "LOH fragmentation is currently within acceptable range.",
                Tags: ["loh", "fragmentation", "memory"],
                MetricValue: r.FragmentationPercent,
                MetricUnit: "% fragmentation")
        ];
    }
}
