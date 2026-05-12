using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class AsyncTaskFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Async Task Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is AsyncTaskDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not AsyncTaskDomainResult r) return [];

        var findings = new List<InsightFinding>(capacity: 4);

        // Orphaned tasks — fire-and-forget anti-pattern or unobserved faults
        if (r.OrphanedTasks > 0)
        {
            double orphanPct = r.TotalTasks > 0 ? r.OrphanedTasks * 100.0 / r.TotalTasks : 0;
            FindingSeverity severity = r.OrphanedTasks >= 100 || orphanPct >= 20
                ? FindingSeverity.Critical
                : r.OrphanedTasks >= 10
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Async",
                Severity: severity,
                Title: "Orphaned tasks detected (fire-and-forget or missing continuation)",
                Evidence: $"{r.OrphanedTasks:N0} tasks ({orphanPct:F1}% of {r.TotalTasks:N0} total) have no continuation object and are not yet completed. These may represent fire-and-forget patterns or unobserved faults.",
                Recommendation: "Await all tasks or attach a continuation to handle faults. Use Task.Run with await rather than discarding TaskCompletionSource results.",
                Tags: ["async", "task", "orphan", "fire-and-forget"],
                MetricValue: r.OrphanedTasks,
                MetricUnit: "orphaned-tasks"));
        }

        // Faulted tasks — unhandled exceptions
        if (r.FaultedTasks > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Async",
                Severity: FindingSeverity.Warning,
                Title: "Faulted tasks with unobserved exceptions",
                Evidence: $"{r.FaultedTasks:N0} tasks are in the Faulted state out of {r.TotalTasks:N0} total. Unobserved task exceptions suppress error propagation and mask failures.",
                Recommendation: "Observe task exceptions via await, Task.Wait, or Task.Exception. Add UnobservedTaskException handler for diagnostics.",
                Tags: ["async", "task", "fault", "exception"],
                MetricValue: r.FaultedTasks,
                MetricUnit: "faulted-tasks"));
        }

        // Deep continuation chains — async-over-sync or unbounded continuations
        if (r.MaxContinuationDepth >= 10)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Async",
                Severity: r.MaxContinuationDepth >= 15 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Deep async continuation chains detected",
                Evidence: $"Maximum continuation chain depth: {r.MaxContinuationDepth}, average depth: {r.AvgContinuationDepth:F1}. Deep chains indicate complex async call graphs or potential async-over-sync wrappers.",
                Recommendation: "Review deep continuation chains for async-over-sync patterns. Consider flattening with ConfigureAwait(false) or redesigning to reduce chain depth.",
                Tags: ["async", "task", "continuation", "chain-depth"],
                MetricValue: r.MaxContinuationDepth,
                MetricUnit: "chain-depth"));
        }

        // High pending task count — possible starvation
        if (r.PendingTasks > 500)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Async",
                Severity: r.PendingTasks > 5000 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title: "High number of pending tasks",
                Evidence: $"{r.PendingTasks:N0} tasks are pending ({r.TotalTasks:N0} total). A large pending queue may indicate thread-pool starvation or awaiting blocked continuations.",
                Recommendation: "Check for synchronous blocking inside async methods (.Result / .Wait). Use ValueTask where tasks complete synchronously. Verify thread-pool sizing.",
                Tags: ["async", "task", "pending", "starvation"],
                MetricValue: r.PendingTasks,
                MetricUnit: "pending-tasks"));
        }

        return findings;
    }
}
