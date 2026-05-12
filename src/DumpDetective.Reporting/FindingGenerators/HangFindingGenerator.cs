using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class HangFindingGenerator : IFindingGenerator
{
    private const int HighThreadPoolThreshold = 500;

    public string AnalyzerName => "Hang Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is HangDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not HangDomainResult r) return [];

        FindingSeverity severity = r.WaitingPercent >= 80 ? FindingSeverity.Critical
            : r.WaitingPercent >= 50 || r.QueuedWorkItems > HighThreadPoolThreshold ? FindingSeverity.Warning
            : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Hang",
                Severity: severity,
                Title: "Hang-risk assessment",
                Evidence: $"Waiting threads: {r.WaitingThreadCount:N0}/{r.TotalAliveThreads:N0} ({r.WaitingPercent:F1}%); queued work items: {r.QueuedWorkItems:N0}; health score: {r.HealthScore}/100.",
                Recommendation: severity == FindingSeverity.Critical
                    ? "Investigate wait groups and lock owners immediately for deadlock/contention storms."
                    : "Review waiting-thread categories and thread-pool saturation indicators.",
                Tags: ["hang", "deadlock", "threadpool", "waits"],
                MetricValue: r.WaitingPercent,
                MetricUnit: "% waiting threads")
        ];
    }
}
