using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class ThreadStackClusterFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Thread Stack Signature Clustering";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ThreadStackClusterDomainResult r) return [];

        FindingSeverity severity = r.DiversityPercent <= 25 ? FindingSeverity.Warning : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: severity,
                Title: "Thread stack-signature diversity",
                Evidence: $"{r.UniqueClusters:N0} unique signatures across {r.AliveThreadCount:N0} alive threads ({r.DiversityPercent:F1}% diversity).",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Low diversity suggests hotspot wait/execution patterns; inspect top clusters for bottlenecks."
                    : "Thread stack diversity appears healthy for this snapshot.",
                Tags: ["thread-cluster", "hotspot", "contention"],
                MetricValue: r.DiversityPercent,
                MetricUnit: "% signature-diversity")
        ];
    }
}
