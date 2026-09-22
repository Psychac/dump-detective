using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectOrphanedTaskAccumulation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class OrphanedTaskAccumulationInsightRule : ISynthesisRule
{
    private const double OrphanRatioThresholdPct = 30.0;
    private const int OrphanCountThreshold = 50;

    public string RuleId => "insight.orphaned-task-accumulation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["async.task-population", "runtime.finalizer-thread"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? asyncTasks = null, finalizerThread = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "async.task-population": asyncTasks = observation; break;
                case "runtime.finalizer-thread": finalizerThread = observation; break;
            }
        }

        if (asyncTasks is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var findings = new List<Finding>();

        long faultedTasks = (long)asyncTasks.Measures["faulted.count"].Value;
        bool finalizerThreadBlocked = finalizerThread is not null && finalizerThread.Measures["is-blocked"].Value != 0;

        // Cross-cutting: orphaned faulted tasks + blocked finalizer = risk of starvation
        if (faultedTasks > 0 && finalizerThreadBlocked)
        {
            findings.Add(new Finding
            {
                Fingerprint = "insightengine::orphaned-task-finalizer-blocked",
                Analyzer = "InsightEngine",
                Category = "Async",
                Severity = Severity.Warning,
                Title = "Faulted tasks combined with blocked finalizer thread",
                Narrative = $"{faultedTasks:N0} faulted tasks detected while the finalizer thread is blocked. Unobserved task exceptions may prevent finalizable resources from being reclaimed.",
                Recommendation = "Ensure task exceptions are observed (await, .Exception, or UnobservedTaskException handler). Unblock the finalizer thread to resume resource cleanup.",
                Tags = ["async", "task", "fault", "finalizer"],
                MetricValue = faultedTasks,
                MetricUnit = "faulted-tasks",
                DerivedFrom = [asyncTasks.Id, finalizerThread!.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Cross-cutting: large orphan count relative to total tasks
        long totalTasks = (long)asyncTasks.Measures["total.count"].Value;
        if (totalTasks > 0)
        {
            long orphanedTasks = (long)asyncTasks.Measures["orphaned.count"].Value;
            double orphanPct = orphanedTasks * 100.0 / totalTasks;
            if (orphanPct >= OrphanRatioThresholdPct && orphanedTasks >= OrphanCountThreshold)
            {
                findings.Add(new Finding
                {
                    Fingerprint = "insightengine::orphaned-task-ratio",
                    Analyzer = "InsightEngine",
                    Category = "Async",
                    Severity = Severity.Warning,
                    Title = "High proportion of orphaned tasks indicates systemic fire-and-forget",
                    Narrative = $"{orphanedTasks:N0} of {totalTasks:N0} tasks ({orphanPct:F1}%) have no continuation. This pattern prevents exception propagation and may mask faults.",
                    Recommendation = "Audit call sites that produce tasks without await. Use Task.WhenAll for bulk orchestration and structured concurrency to ensure tasks are always observed.",
                    Tags = ["async", "task", "orphan", "pattern"],
                    MetricValue = orphanPct,
                    MetricUnit = "% orphaned",
                    DerivedFrom = [asyncTasks.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }
}
