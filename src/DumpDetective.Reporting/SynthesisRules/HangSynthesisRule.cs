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

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.hang-pressure":
                    pressure = observation;
                    break;
                case "thread.wait-state":
                    derivedFrom.Add(observation.Id);
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

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
