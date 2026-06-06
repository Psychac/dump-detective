using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class ThreadFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Thread Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ThreadDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ThreadDomainResult r) return [];

        FindingSeverity severity = r.ThreadsWithActiveExceptionsCount > 0 || r.BlockedThreadCount >= 10
            ? FindingSeverity.Warning : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: severity,
                Title: "Thread-state triage summary",
                Evidence: $"Alive threads: {r.AliveThreadCount:N0}; blocked-pattern threads: {r.BlockedThreadCount:N0}; lock-holding threads: {r.LockHoldingThreadCount:N0}; active thread exceptions: {r.ThreadsWithActiveExceptionsCount:N0}.",
                Recommendation: "Correlate blocked groups with lock owners and hotspot frames to isolate contention/deadlock candidates.",
                Tags: ["threads", "locks", "blocked", "exceptions"],
                MetricValue: r.BlockedThreadCount,
                MetricUnit: "blocked-threads")
        ];
    }
}
