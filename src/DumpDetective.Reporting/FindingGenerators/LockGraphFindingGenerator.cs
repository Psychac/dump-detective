using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class LockGraphFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Lock Graph Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is LockGraphDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not LockGraphDomainResult r) return [];

        FindingSeverity severity = r.DeadlockCandidateCount >= 2 ? FindingSeverity.Critical
            : r.ContestedLockCount > 0 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threading",
                Severity: severity,
                Title: "Lock contention and deadlock graph",
                Evidence: $"{r.TotalHeldLocks:N0} inflated monitor lock(s) held; {r.ContestedLockCount:N0} contested; {r.DeadlockCandidateCount:N0} deadlock candidate(s).",
                Recommendation: severity == FindingSeverity.Critical
                    ? "Deadlock candidates detected. Review lock acquisition order and confirm circular-wait cycle."
                    : r.ContestedLockCount > 0
                        ? "Reduce lock scope on contested objects or switch to lock-free patterns."
                        : "No lock contention detected in this snapshot.",
                Tags: ["locks", "deadlock", "contention", "monitor"],
                MetricValue: r.DeadlockCandidateCount,
                MetricUnit: "deadlock-candidates")
        ];
    }
}
