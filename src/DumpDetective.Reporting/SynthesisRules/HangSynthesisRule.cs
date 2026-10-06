using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>HangFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>HangHealthClassifier.ComputeHealthScore</c> is <c>internal</c> in
/// <c>DumpDetective.Plugins.Threads</c> and inaccessible from Reporting, so its formula is
/// duplicated here verbatim (same precedent as <c>AllocationPatternPressureClassifier</c> and
/// <c>MemoryPressureClassifier</c> in the Memory-plugin rules). The one piece the classifier reads
/// that has no direct measure — the count of "MonitorWait while holding a lock" candidates among
/// <c>TopWaitingThreads</c> — is reconstructed by summing each matched <c>thread.wait-state</c>
/// observation's <c>circular-wait-candidate.count</c>, the same raw per-thread fact
/// <c>HangAnalyzer</c> now computes at emission time instead of leaking the <c>WaitType</c> string.
/// </remarks>
internal sealed class HangSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Hang Analysis";
    private const int HighThreadPoolThreshold = 500;

    public string RuleId => "hang.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.hang-pressure", "thread.wait-state"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? pressure = null;
        int circularWaitCandidates = 0;
        var derivedFrom = new List<ObservationId>();
        var waitStateIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.hang-pressure":
                    pressure = observation;
                    break;
                case "thread.wait-state":
                    derivedFrom.Add(observation.Id);
                    waitStateIds.Add(observation.Id);
                    if (observation.Measures["circular-wait-candidate.count"].Value >= 1.0)
                        circularWaitCandidates++;
                    break;
            }
        }

        if (pressure is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        derivedFrom.Insert(0, pressure.Id);

        int waitingThreadCount = (int)pressure.Measures["waiting-thread.count"].Value;
        int totalAliveThreads = (int)pressure.Measures["alive-thread.count"].Value;
        double waitingPercent = pressure.Measures["waiting.percent"].Value;
        int queuedWorkItems = (int)pressure.Measures["queued-work-items.count"].Value;
        int pendingTasks = (int)pressure.Measures["pending-task.count"].Value;
        int faultedTasks = (int)pressure.Measures["faulted-task.count"].Value;
        int totalTaskContinuations = (int)pressure.Measures["task-continuation.count"].Value;
        int runtimeMaxThreads = (int)pressure.Measures["threadpool-max.count"].Value;
        int runtimeActiveWorkerThreads = (int)pressure.Measures["threadpool-active.count"].Value;
        int runtimeCpuUtilization = (int)pressure.Measures["threadpool-cpu.percent"].Value;
        int highThreadPoolThreshold = (int)pressure.Measures["high-threadpool-threshold.count"].Value;

        int healthScore = ComputeHealthScore(
            waitingPercent, circularWaitCandidates, runtimeMaxThreads, runtimeActiveWorkerThreads,
            runtimeCpuUtilization, pendingTasks, highThreadPoolThreshold, faultedTasks, totalTaskContinuations);

        Severity severity = waitingPercent >= 80 ? Severity.Critical
            : waitingPercent >= 50 || queuedWorkItems > HighThreadPoolThreshold ? Severity.Warning
            : Severity.Info;

        var finding = new Claim
        {
            Fingerprint = Fingerprint("hang-risk"),
            Analyzer = AnalyzerName,
            Category = "Hang",
            Severity = severity,
            Title = "Hang-risk assessment",
            Narrative = $"Waiting threads: {waitingThreadCount:N0}/{totalAliveThreads:N0} ({waitingPercent:F1}%); " +
                        $"queued work items: {queuedWorkItems:N0}; health score: {healthScore}/100.",
            Recommendation = severity == Severity.Critical
                ? "Investigate wait groups and lock owners immediately for deadlock/contention storms."
                : "Review waiting-thread categories and thread-pool saturation indicators.",
            Tags = ["hang", "deadlock", "threadpool", "waits"],
            MetricValue = waitingPercent,
            MetricUnit = "% waiting threads",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Assertion = BuildAssertion(severity, waitingPercent, queuedWorkItems),
            Support = BuildSupport(pressure, waitStateIds, waitingThreadCount, totalAliveThreads, queuedWorkItems),
            Counter = BuildCounter(severity, waitingPercent, pressure, runtimeCpuUtilization, faultedTasks),
            FalsifiedBy = BuildFalsifiedBy(severity, waitingPercent, waitingThreadCount, queuedWorkItems),
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([finding]);
    }

    private static int ComputeHealthScore(
        double waitingPercent, int circularWaitCandidates, int runtimeMaxThreads, int runtimeActiveWorkerThreads,
        int runtimeCpuUtilization, int pendingTasks, int highThreadPoolThreshold, int faultedTasks, int totalTaskContinuations)
    {
        int score = 100;

        if (waitingPercent >= 80) score -= 40;
        else if (waitingPercent >= 50) score -= 25;
        else if (waitingPercent >= 30) score -= 10;

        score -= Math.Min(circularWaitCandidates * 15, 30);

        bool saturated = runtimeMaxThreads > 0 && runtimeActiveWorkerThreads >= runtimeMaxThreads;
        bool starvation = saturated && runtimeCpuUtilization < 20;
        if (starvation) score -= 15;
        else if (saturated) score -= 10;

        if (pendingTasks > highThreadPoolThreshold) score -= 5;
        if (faultedTasks > 0) score -= 5;
        if (totalTaskContinuations > 1000) score -= 5;

        return Math.Max(0, Math.Min(100, score));
    }

    private const double CriticalWaitingPercent = 80;
    private const double WarningWaitingPercent = 50;
    private const int ThreadPoolCpuBusyPercent = 50;

    private static bool WarnedByQueueAlone(Severity severity, double waitingPercent) =>
        severity == Severity.Warning && waitingPercent < WarningWaitingPercent;

    private static Assertion BuildAssertion(Severity severity, double waitingPercent, int queuedWorkItems)
    {
        if (WarnedByQueueAlone(severity, waitingPercent))
            return new Assertion
            {
                Predicate = AssertionPredicate.Stalls,
                Measure = ClaimParts.Count(queuedWorkItems),
                Reference = ClaimParts.Budget(ClaimParts.Count(HighThreadPoolThreshold), "queued work item threshold"),
                Qualifiers = new AssertionQualifiers { Location = "thread-pool work queue" },
            };

        (double threshold, string basis) = severity switch
        {
            Severity.Critical => (CriticalWaitingPercent, "critical waiting threshold"),
            _ => (WarningWaitingPercent, "warning waiting threshold"),
        };

        return new Assertion
        {
            Predicate = severity == Severity.Info ? AssertionPredicate.Normal : AssertionPredicate.Stalls,
            Measure = ClaimParts.Percent(waitingPercent),
            Reference = ClaimParts.Budget(ClaimParts.Percent(threshold), basis),
            Qualifiers = new AssertionQualifiers { Location = "alive threads in a wait state" },
        };
    }

    private static IReadOnlyList<EvidenceStatement> BuildSupport(
        Observation pressure, List<ObservationId> waitStateIds, int waitingThreadCount, int totalAliveThreads, int queuedWorkItems)
    {
        var support = new List<EvidenceStatement>(2)
        {
            new([pressure.Id], $"{waitingThreadCount:N0} of {totalAliveThreads:N0} alive threads are waiting; {queuedWorkItems:N0} work items are queued."),
        };

        if (waitStateIds.Count > 0)
            support.Add(new(waitStateIds, "Each is a thread's recorded wait state."));

        return support;
    }

    private static Counter BuildCounter(Severity severity, double waitingPercent, Observation pressure, int threadPoolCpuPercent, int faultedTasks)
    {
        if (WarnedByQueueAlone(severity, waitingPercent))
            return Counter.None("Queue depth is a point-in-time read; nothing observed shows whether it is a burst that drains or a standing backlog.");

        if (severity == Severity.Info)
            return faultedTasks > 0
                ? Counter.Of(new EvidenceStatement([pressure.Id], $"{faultedTasks:N0} tasks are faulted, which can stall work even though few threads are waiting."))
                : Counter.None("The waiting share is below the warning threshold and no faulted tasks were observed.");

        return threadPoolCpuPercent >= ThreadPoolCpuBusyPercent
            ? Counter.Of(new EvidenceStatement([pressure.Id], $"Thread-pool CPU utilisation is {threadPoolCpuPercent}%, so much of the pool is doing work, not stalled."))
            : Counter.None("Waiting threads include idle thread-pool workers, and low pool CPU utilisation does not separate idle waits from stalled ones.");
    }

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(Severity severity, double waitingPercent, int waitingThreadCount, int queuedWorkItems)
    {
        if (WarnedByQueueAlone(severity, waitingPercent))
            return
            [
                new FalsificationTest
                {
                    Kind = FalsificationKind.Windbg,
                    Command = "!threadpool",
                    Test = $"If !threadpool reports a work-item queue far shorter than the {queuedWorkItems:N0} counted here, the queue reading is wrong.",
                },
                new FalsificationTest
                {
                    Kind = FalsificationKind.Capture,
                    Test = "Capture a second dump a minute later; if the queue is nearly empty, it was a burst that drained, not a standing backlog.",
                },
            ];

        var tests = new List<FalsificationTest>(2)
        {
            new()
            {
                Kind = FalsificationKind.Windbg,
                Command = "!threadpool",
                Test = $"If most of the {waitingThreadCount:N0} waiting threads are idle pool workers, the waiting share is spare capacity, not a stall.",
            },
        };

        if (severity != Severity.Info)
            tests.Add(new()
            {
                Kind = FalsificationKind.Capture,
                Test = "Capture a second dump a few minutes later; if different threads are waiting at different frames, the waits are short-lived churn, not a stall.",
            });

        return tests;
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
