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
            bool isExpectedPattern = dominantCluster.FrameworkPattern != null;
            FindingSeverity dominantSeverity = dominantPercent >= 50 && !isExpectedPattern ? FindingSeverity.Warning : FindingSeverity.Info;

            string recommendation;
            if (isExpectedPattern)
                recommendation = $"The largest cluster matches a known framework idle pattern ({dominantCluster.FrameworkPattern}); this is expected and not indicative of application-level contention.";
            else if (dominantSeverity == FindingSeverity.Warning)
                recommendation = "More than 50% of threads share the same call stack; this typically indicates a hotspot, resource contention, or coordinated wait. Investigate the signature frames for blocking points.";
            else
                recommendation = "The largest cluster is below 50% of threads; thread activity appears distributed.";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: dominantSeverity,
                Title: "Dominant thread cluster",
                Evidence: $"{dominantCluster.Count} of {r.AliveThreadCount} threads ({dominantPercent:F1}%) blocked in: {dominantCluster.Signature}",
                Recommendation: recommendation,
                Tags: isExpectedPattern
                    ? ["thread-cluster", "dominant-cluster", "framework-pattern"]
                    : ["thread-cluster", "dominant-cluster", "hotspot"],
                MetricValue: dominantPercent,
                MetricUnit: "% dominant-cluster"));
        }

        return findings;
    }
}
