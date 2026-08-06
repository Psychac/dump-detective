using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ThreadStackClusterFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Thread Stack Signature Clustering";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ThreadStackClusterDomainResult r) return [];

        var findings = new List<InsightFinding>();

        FindingSeverity severity = r.DiversityPercent <= 25 ? FindingSeverity.Warning : FindingSeverity.Info;

        findings.Add(new InsightFinding(
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
            MetricUnit: "% signature-diversity"));

        if (r.TopClusters is { Count: > 0 })
        {
            var dominantCluster = r.TopClusters[0];
            double dominantPercent = r.AliveThreadCount > 0 ? dominantCluster.Count * 100.0 / r.AliveThreadCount : 0;
            FindingSeverity dominantSeverity = dominantPercent >= 50 ? FindingSeverity.Warning : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: dominantSeverity,
                Title: "Dominant thread cluster",
                Evidence: $"{dominantCluster.Count} of {r.AliveThreadCount} threads ({dominantPercent:F1}%) blocked in: {dominantCluster.Signature}",
                Recommendation: dominantSeverity == FindingSeverity.Warning
                    ? "More than 50% of threads share the same call stack; this typically indicates a hotspot, resource contention, or coordinated wait. Investigate the signature frames for blocking points."
                    : "The largest cluster is below 50% of threads; thread activity appears distributed.",
                Tags: ["thread-cluster", "dominant-cluster", "hotspot"],
                MetricValue: dominantPercent,
                MetricUnit: "% dominant-cluster"));
        }

        return findings;
    }
}
