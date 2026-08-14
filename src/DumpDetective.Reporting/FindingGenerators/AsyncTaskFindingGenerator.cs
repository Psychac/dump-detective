using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class AsyncTaskFindingGenerator : IFindingGenerator
{
    private readonly record struct AsyncSignal(
        string Key,
        FindingSeverity Severity,
        int Priority,
        string Title,
        string Evidence,
        string Recommendation,
        string[] Tags,
        double MetricValue,
        string MetricUnit);

    public string AnalyzerName => "Async Task Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is AsyncTaskDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not AsyncTaskDomainResult r) return [];

        var signals = new List<AsyncSignal>(capacity: 7);

        // Continuation chain cycles — hard deadlock
        if (r.CycleDetected)
        {
            signals.Add(new AsyncSignal(
                Key: "cycle",
                Severity: FindingSeverity.Critical,
                Priority: 1000,
                Title: "Async deadlock detected (continuation chain cycle)",
                Evidence: "A task's continuation chain cycles back to itself, indicating a hard deadlock where the task cannot complete.",
                Recommendation: "Inspect the task's continuation chain for circular references or self-awaits. Review async method implementations for patterns that schedule continuations back onto themselves.",
                Tags: ["async", "task", "deadlock", "cycle"],
                MetricValue: 1.0,
                MetricUnit: "cycle-detected"));
        }

        // Gen2/LOH pending tasks — strong leak signal
        int pendingOldGen = r.PendingGen2 + r.PendingLOH;
        if (pendingOldGen > 0 && r.PendingTasks > 0)
        {
            double oldGenPct = pendingOldGen * 100.0 / r.PendingTasks;
            FindingSeverity severity = oldGenPct >= 50 ? FindingSeverity.Warning : FindingSeverity.Info;

            signals.Add(new AsyncSignal(
                Key: "pending-oldgen",
                Severity: severity,
                Priority: 500 + pendingOldGen,
                Title: $"Pending tasks in Gen2/LOH ({pendingOldGen:N0}, {oldGenPct:F1}%)",
                Evidence: $"{pendingOldGen:N0} of {r.PendingTasks:N0} pending tasks ({oldGenPct:F1}%) are in Gen2 (old generation) or LOH (large object heap). Gen2 residency is a strong indicator of long-lived memory retention and potential leaks.",
                Recommendation: "Inspect Gen2/LOH pending tasks for root cause. Check if tasks are waiting on external events, resources, or deadlocks. Gen2 presence suggests these tasks have survived multiple GC cycles.",
                Tags: ["async", "task", "pending", "generation", "gc", "retention"],
                MetricValue: pendingOldGen,
                MetricUnit: "pending-oldgen"));
        }

        // Unresolved TaskCompletionSource in Gen2/LOH — leaked promise signal. Gated on
        // old-generation residency, not raw unresolved count: a fresh in-flight TCS and a
        // genuinely stuck one look identical at the instant of the dump, but only a leaked one
        // survives multiple GC cycles into Gen2/LOH.
        if (r.UnresolvedTcsGen2Count > 0)
        {
            signals.Add(new AsyncSignal(
                Key: "tcs-unresolved-oldgen",
                Severity: r.UnresolvedTcsGen2Count >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
                Priority: 450 + r.UnresolvedTcsGen2Count,
                Title: $"Unresolved TaskCompletionSource instances in Gen2/LOH ({r.UnresolvedTcsGen2Count:N0})",
                Evidence: $"{r.UnresolvedTcsGen2Count:N0} of {r.UnresolvedTaskCompletionSources:N0} unresolved TaskCompletionSource instances (out of {r.TotalTaskCompletionSources:N0} total) are in Gen2 (old generation) or LOH, meaning nobody has called SetResult/SetException/SetCanceled and the promise has survived multiple GC cycles — a leaked promise, not just an in-flight one.",
                Recommendation: "Inspect the retention path for these TaskCompletionSource instances. Common causes: an event handler expected to call Set* was unsubscribed before firing, an external callback never invoked, or a timeout/cancellation path that doesn't resolve the TCS.",
                Tags: ["async", "task", "tcs", "leak", "generation", "gc"],
                MetricValue: r.UnresolvedTcsGen2Count,
                MetricUnit: "unresolved-tcs-oldgen"));
        }

        // Orphaned tasks — fire-and-forget anti-pattern or unobserved faults
        if (r.OrphanedTasks > 0)
        {
            double orphanPct = r.TotalTasks > 0 ? r.OrphanedTasks * 100.0 / r.TotalTasks : 0;
            FindingSeverity severity = r.OrphanedTasks >= 100 || orphanPct >= 20
                ? FindingSeverity.Critical
                : r.OrphanedTasks >= 10
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            signals.Add(new AsyncSignal(
                Key: "orphaned",
                Severity: severity,
                Priority: 400 + r.OrphanedTasks,
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
            signals.Add(new AsyncSignal(
                Key: "faulted",
                Severity: FindingSeverity.Warning,
                Priority: 200 + r.FaultedTasks,
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
            signals.Add(new AsyncSignal(
                Key: "continuation-depth",
                Severity: r.MaxContinuationDepth >= 15 ? FindingSeverity.Warning : FindingSeverity.Info,
                Priority: 100 + r.MaxContinuationDepth,
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
            // Escalate severity if pending tasks represent a high fraction of the total
            double pendingRate = r.TotalTasks > 0 ? r.PendingTasks * 100.0 / r.TotalTasks : 0;
            FindingSeverity pendingSeverity = r.PendingTasks > 5000 || pendingRate > 70
                ? FindingSeverity.Critical
                : pendingRate > 50
                    ? FindingSeverity.Warning
                    : r.PendingTasks > 2000
                        ? FindingSeverity.Warning
                        : FindingSeverity.Info;

            signals.Add(new AsyncSignal(
                Key: "pending",
                Severity: pendingSeverity,
                Priority: 300 + (r.PendingTasks / 10),
                Title: "High number of pending tasks",
                Evidence: $"{r.PendingTasks:N0} tasks are pending ({pendingRate:F1}% of {r.TotalTasks:N0} total). A large pending queue may indicate thread-pool starvation or awaiting blocked continuations.",
                Recommendation: "Check for synchronous blocking inside async methods (.Result / .Wait). Use ValueTask where tasks complete synchronously. Verify thread-pool sizing.",
                Tags: ["async", "task", "pending", "starvation"],
                MetricValue: r.PendingTasks,
                MetricUnit: "pending-tasks"));
        }

        if (signals.Count == 0) return [];

        AsyncSignal top = signals[0];
        FindingSeverity aggregateSeverity = top.Severity;
        for (int i = 1; i < signals.Count; i++)
        {
            AsyncSignal s = signals[i];
            if (SeverityRank(s.Severity) > SeverityRank(aggregateSeverity))
                aggregateSeverity = s.Severity;

            bool betterSeverity = SeverityRank(s.Severity) > SeverityRank(top.Severity);
            bool sameSeverityHigherPriority = SeverityRank(s.Severity) == SeverityRank(top.Severity) && s.Priority > top.Priority;
            if (betterSeverity || sameSeverityHigherPriority)
                top = s;
        }

        if (signals.Count == 1)
        {
            return
            [
                new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Async",
                    Severity: top.Severity,
                    Title: top.Title,
                    Evidence: top.Evidence,
                    Recommendation: top.Recommendation,
                    Tags: top.Tags,
                    MetricValue: top.MetricValue,
                    MetricUnit: top.MetricUnit)
            ];
        }

        var findings = new List<InsightFinding>(2)
        {
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Async",
                Severity: aggregateSeverity,
                Title: $"Async task risk cluster detected ({signals.Count:N0} signals)",
                Evidence: $"Tasks: total {r.TotalTasks:N0}, pending {r.PendingTasks:N0}, orphaned {r.OrphanedTasks:N0}, faulted {r.FaultedTasks:N0}, max continuation depth {r.MaxContinuationDepth:N0}. Highest-risk signal: {top.Key}.",
                Recommendation: "Prioritize the top async signal below, then validate completion/exception handling and continuation depth for the remaining signals.",
                Tags: ["async", "task", "aggregate", "risk-cluster"],
                MetricValue: r.TotalTasks,
                MetricUnit: "tasks"),
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Async",
                Severity: top.Severity,
                Title: top.Title,
                Evidence: top.Evidence,
                Recommendation: top.Recommendation,
                Tags: top.Tags,
                MetricValue: top.MetricValue,
                MetricUnit: top.MetricUnit)
        };

        return findings;
    }

    private static int SeverityRank(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 3,
        FindingSeverity.Warning => 2,
        _ => 1
    };
}
