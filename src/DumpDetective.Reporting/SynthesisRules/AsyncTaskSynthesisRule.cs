using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>AsyncTaskFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Seven of this rule's eight signals read the single whole-run <c>async.task-population</c>
/// observation — the old generator's fields (<c>PendingGen2</c>/<c>PendingLOH</c>,
/// <c>UnresolvedTcsGen2Count</c>, <c>PendingVtsGen2Count</c>, <c>FaultedTasks</c>,
/// <c>MaxContinuationDepth</c>/<c>AvgContinuationDepth</c>, <c>PendingTasks</c>,
/// <c>CycleDetected</c>) had no observation backing at all until this step d — see the
/// playbook's per-analyzer status table. Only the eighth (<c>orphaned</c>) already had one
/// (<c>type.orphaned-task</c>, one per orphaned task); here it is reconstructed by counting the
/// matched instances rather than summing a measure.
/// </remarks>
internal sealed class AsyncTaskSynthesisRule : ISynthesisRule
{
    private readonly record struct AsyncSignal(
        string Key,
        Severity Severity,
        long Priority,
        string Title,
        string Narrative,
        string Recommendation,
        string[] Tags,
        double MetricValue,
        string MetricUnit);

    private const string AnalyzerName = "Async Task Analysis";

    public string RuleId => "async-task.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["async.task-population", "type.orphaned-task"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        long orphanedTasks = 0;
        var orphanedTaskIds = new List<ObservationId>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "async.task-population")
            {
                population = observation;
            }
            else if (observation.ObservationType == "type.orphaned-task")
            {
                orphanedTasks++;
                orphanedTaskIds.Add(observation.Id);
            }
        }

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        IReadOnlyDictionary<string, Measure> m = population.Measures;
        long totalTasks = (long)m["total.count"].Value;
        long pendingTasks = (long)m["pending.count"].Value;
        long pendingOldGen = (long)m["pending-oldgen.count"].Value;
        long faultedTasks = (long)m["faulted.count"].Value;
        bool cycleDetected = m["cycle-detected"].Value >= 1.0;
        long maxContinuationDepth = (long)m["max-continuation-depth"].Value;
        double avgContinuationDepth = m["avg-continuation-depth"].Value;
        long totalTcs = (long)m["total-tcs.count"].Value;
        long unresolvedTcs = (long)m["unresolved-tcs.count"].Value;
        long unresolvedTcsGen2 = (long)m["unresolved-tcs-oldgen.count"].Value;
        long totalVts = (long)m["total-vts.count"].Value;
        long pendingVts = (long)m["pending-vts.count"].Value;
        long pendingVtsGen2 = (long)m["pending-vts-oldgen.count"].Value;

        var signals = new List<AsyncSignal>(capacity: 8);

        // Continuation chain cycles — hard deadlock
        if (cycleDetected)
        {
            signals.Add(new AsyncSignal(
                Key: "cycle",
                Severity: Severity.Critical,
                Priority: 1000,
                Title: "Async deadlock detected (continuation chain cycle)",
                Narrative: "A task's continuation chain cycles back to itself, indicating a hard deadlock where the task cannot complete.",
                Recommendation: "Inspect the task's continuation chain for circular references or self-awaits. Review async method implementations for patterns that schedule continuations back onto themselves.",
                Tags: ["async", "task", "deadlock", "cycle"],
                MetricValue: 1.0,
                MetricUnit: "cycle-detected"));
        }

        // Gen2/LOH pending tasks — strong leak signal
        if (pendingOldGen > 0 && pendingTasks > 0)
        {
            double oldGenPct = pendingOldGen * 100.0 / pendingTasks;
            Severity severity = oldGenPct >= 50 ? Severity.Warning : Severity.Info;

            signals.Add(new AsyncSignal(
                Key: "pending-oldgen",
                Severity: severity,
                Priority: 500 + pendingOldGen,
                Title: $"Pending tasks in Gen2/LOH ({pendingOldGen:N0}, {oldGenPct:F1}%)",
                Narrative: $"{pendingOldGen:N0} of {pendingTasks:N0} pending tasks ({oldGenPct:F1}%) are in Gen2 (old generation) or LOH (large object heap). Gen2 residency is a strong indicator of long-lived memory retention and potential leaks.",
                Recommendation: "Inspect Gen2/LOH pending tasks for root cause. Check if tasks are waiting on external events, resources, or deadlocks. Gen2 presence suggests these tasks have survived multiple GC cycles.",
                Tags: ["async", "task", "pending", "generation", "gc", "retention"],
                MetricValue: pendingOldGen,
                MetricUnit: "pending-oldgen"));
        }

        // Unresolved TaskCompletionSource in Gen2/LOH — leaked promise signal
        if (unresolvedTcsGen2 > 0)
        {
            signals.Add(new AsyncSignal(
                Key: "tcs-unresolved-oldgen",
                Severity: unresolvedTcsGen2 >= 20 ? Severity.Warning : Severity.Info,
                Priority: 450 + unresolvedTcsGen2,
                Title: $"Unresolved TaskCompletionSource instances in Gen2/LOH ({unresolvedTcsGen2:N0})",
                Narrative: $"{unresolvedTcsGen2:N0} of {unresolvedTcs:N0} unresolved TaskCompletionSource instances (out of {totalTcs:N0} total) are in Gen2 (old generation) or LOH, meaning nobody has called SetResult/SetException/SetCanceled and the promise has survived multiple GC cycles — a leaked promise, not just an in-flight one.",
                Recommendation: "Inspect the retention path for these TaskCompletionSource instances. Common causes: an event handler expected to call Set* was unsubscribed before firing, an external callback never invoked, or a timeout/cancellation path that doesn't resolve the TCS.",
                Tags: ["async", "task", "tcs", "leak", "generation", "gc"],
                MetricValue: unresolvedTcsGen2,
                MetricUnit: "unresolved-tcs-oldgen"));
        }

        // Pending IValueTaskSource in Gen2/LOH — same leak-strength rationale as the TCS signal
        if (pendingVtsGen2 > 0)
        {
            signals.Add(new AsyncSignal(
                Key: "vts-pending-oldgen",
                Severity: pendingVtsGen2 >= 20 ? Severity.Warning : Severity.Info,
                Priority: 440 + pendingVtsGen2,
                Title: $"Pending IValueTaskSource instances in Gen2/LOH ({pendingVtsGen2:N0})",
                Narrative: $"{pendingVtsGen2:N0} of {pendingVts:N0} pending IValueTaskSource instances (out of {totalVts:N0} total) are in Gen2 (old generation) or LOH, meaning the underlying operation has not signaled completion and has survived multiple GC cycles — a stuck source, not just an in-flight one.",
                Recommendation: "Inspect the retention path for these ValueTaskSource-backed objects (common in Socket, System.IO.Pipelines, and pooled ASP.NET Core primitives). Check for a completion callback that never fires or a pooled instance that was never returned/reset.",
                Tags: ["async", "valuetask", "vts", "leak", "generation", "gc"],
                MetricValue: pendingVtsGen2,
                MetricUnit: "pending-vts-oldgen"));
        }

        // Orphaned tasks — fire-and-forget anti-pattern or unobserved faults
        if (orphanedTasks > 0)
        {
            double orphanPct = totalTasks > 0 ? orphanedTasks * 100.0 / totalTasks : 0;
            Severity severity = orphanedTasks >= 100 || orphanPct >= 20
                ? Severity.Critical
                : orphanedTasks >= 10
                    ? Severity.Warning
                    : Severity.Info;

            signals.Add(new AsyncSignal(
                Key: "orphaned",
                Severity: severity,
                Priority: 400 + orphanedTasks,
                Title: "Orphaned tasks detected (fire-and-forget or missing continuation)",
                Narrative: $"{orphanedTasks:N0} tasks ({orphanPct:F1}% of {totalTasks:N0} total) have no continuation object and are not yet completed. These may represent fire-and-forget patterns or unobserved faults.",
                Recommendation: "Await all tasks or attach a continuation to handle faults. Use Task.Run with await rather than discarding TaskCompletionSource results.",
                Tags: ["async", "task", "orphan", "fire-and-forget"],
                MetricValue: orphanedTasks,
                MetricUnit: "orphaned-tasks"));
        }

        // Faulted tasks — unhandled exceptions
        if (faultedTasks > 0)
        {
            signals.Add(new AsyncSignal(
                Key: "faulted",
                Severity: Severity.Warning,
                Priority: 200 + faultedTasks,
                Title: "Faulted tasks with unobserved exceptions",
                Narrative: $"{faultedTasks:N0} tasks are in the Faulted state out of {totalTasks:N0} total. Unobserved task exceptions suppress error propagation and mask failures.",
                Recommendation: "Observe task exceptions via await, Task.Wait, or Task.Exception. Add UnobservedTaskException handler for diagnostics.",
                Tags: ["async", "task", "fault", "exception"],
                MetricValue: faultedTasks,
                MetricUnit: "faulted-tasks"));
        }

        // Deep continuation chains — async-over-sync or unbounded continuations
        if (maxContinuationDepth >= 10)
        {
            signals.Add(new AsyncSignal(
                Key: "continuation-depth",
                Severity: maxContinuationDepth >= 15 ? Severity.Warning : Severity.Info,
                Priority: 100 + maxContinuationDepth,
                Title: "Deep async continuation chains detected",
                Narrative: $"Maximum continuation chain depth: {maxContinuationDepth}, average depth: {avgContinuationDepth:F1}. Deep chains indicate complex async call graphs or potential async-over-sync wrappers.",
                Recommendation: "Review deep continuation chains for async-over-sync patterns. Consider flattening with ConfigureAwait(false) or redesigning to reduce chain depth.",
                Tags: ["async", "task", "continuation", "chain-depth"],
                MetricValue: maxContinuationDepth,
                MetricUnit: "chain-depth"));
        }

        // High pending task count — possible starvation
        if (pendingTasks > 500)
        {
            double pendingRate = totalTasks > 0 ? pendingTasks * 100.0 / totalTasks : 0;
            Severity pendingSeverity = pendingTasks > 5000 || pendingRate > 70
                ? Severity.Critical
                : pendingRate > 50
                    ? Severity.Warning
                    : pendingTasks > 2000
                        ? Severity.Warning
                        : Severity.Info;

            signals.Add(new AsyncSignal(
                Key: "pending",
                Severity: pendingSeverity,
                Priority: 300 + (pendingTasks / 10),
                Title: "High number of pending tasks",
                Narrative: $"{pendingTasks:N0} tasks are pending ({pendingRate:F1}% of {totalTasks:N0} total). A large pending queue may indicate thread-pool starvation or awaiting blocked continuations.",
                Recommendation: "Check for synchronous blocking inside async methods (.Result / .Wait). Use ValueTask where tasks complete synchronously. Verify thread-pool sizing.",
                Tags: ["async", "task", "pending", "starvation"],
                MetricValue: pendingTasks,
                MetricUnit: "pending-tasks"));
        }

        if (signals.Count == 0) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        AsyncSignal top = signals[0];
        Severity aggregateSeverity = top.Severity;
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

        var derivedFrom = new List<ObservationId>(orphanedTaskIds.Count + 1) { population.Id };
        derivedFrom.AddRange(orphanedTaskIds);

        if (signals.Count == 1)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>([ToFinding(top, derivedFrom)]);
        }

        var findings = new List<Claim>(2)
        {
            new Claim
            {
                Fingerprint = Fingerprint("risk-cluster"),
                Analyzer = AnalyzerName,
                Category = "Async",
                Severity = aggregateSeverity,
                Title = $"Async task risk cluster detected ({signals.Count:N0} signals)",
                Narrative = $"Tasks: total {totalTasks:N0}, pending {pendingTasks:N0}, orphaned {orphanedTasks:N0}, faulted {faultedTasks:N0}, max continuation depth {maxContinuationDepth:N0}. Highest-risk signal: {top.Key}.",
                Recommendation = "Prioritize the top async signal below, then validate completion/exception handling and continuation depth for the remaining signals.",
                Tags = ["async", "task", "aggregate", "risk-cluster"],
                MetricValue = totalTasks,
                MetricUnit = "tasks",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
            ToFinding(top, derivedFrom),
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static Claim ToFinding(AsyncSignal signal, IReadOnlyList<ObservationId> derivedFrom) => new()
    {
        Fingerprint = Fingerprint(signal.Key),
        Analyzer = AnalyzerName,
        Category = "Async",
        Severity = signal.Severity,
        Title = signal.Title,
        Narrative = signal.Narrative,
        Recommendation = signal.Recommendation,
        Tags = signal.Tags,
        MetricValue = signal.MetricValue,
        MetricUnit = signal.MetricUnit,
        DerivedFrom = derivedFrom,
        Confidence = ConfidenceBreakdown.MeasurementCertain,
    };

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Critical => 3,
        Severity.Warning => 2,
        _ => 1,
    };

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
