using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectEventLeakPattern</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>event.publisher-leak-population</c> gained a <c>subscriber.count</c> measure specifically for
/// this rule — <c>EventLeakDomainResult.TotalSubscribers</c> was already an exact field, just not
/// yet promoted to the whole-run observation (only the per-instance/per-group ones carried it).
/// </remarks>
internal sealed class EventLeakPatternInsightRule : ISynthesisRule
{
    private const double HighGen2Pct = 40.0;

    public string RuleId => "insight.event-leak-pattern";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["event.publisher-leak-population", "gc.generation-composition", "finalizer.queue-population"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? eventLeaks = null, gcGen = null, finalizerQueue = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "event.publisher-leak-population": eventLeaks = observation; break;
                case "gc.generation-composition": gcGen = observation; break;
                case "finalizer.queue-population": finalizerQueue = observation; break;
            }
        }

        long totalEventLeakInstances = (long)(eventLeaks?.Measures["total.count"].Value ?? 0);
        if (eventLeaks is null || totalEventLeakInstances == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        double gen2Pct = gcGen?.Measures["gen2.percent"].Value ?? 0.0;
        bool highGen2 = gcGen is not null && gen2Pct >= HighGen2Pct;

        long finalizerQueueCount = (long)(finalizerQueue?.Measures["queue.count"].Value ?? 0);
        bool highFinalizer = finalizerQueue is not null && finalizerQueueCount >= 1_000;

        // Only emit a cross-cutting finding when at least one other signal correlates.
        if (!highGen2 && !highFinalizer)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        string gen2Note = highGen2
            ? $" Gen2 holds {gen2Pct:F1}% of the managed heap — event subscribers may be keeping objects alive across GC generations."
            : string.Empty;

        string finNote = highFinalizer
            ? $" Finalizer queue has {finalizerQueueCount:N0} objects — some may be retained by event subscription chains."
            : string.Empty;

        long totalSubscribers = (long)eventLeaks.Measures["subscriber.count"].Value;

        var derivedFrom = new List<ObservationId> { eventLeaks.Id };
        if (gcGen is not null) derivedFrom.Add(gcGen.Id);
        if (finalizerQueue is not null) derivedFrom.Add(finalizerQueue.Id);

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::event-leak-pattern",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Event subscriptions likely amplify Gen2 and finalizer queue pressure",
                Narrative = $"{totalEventLeakInstances:N0} event-leak group(s) with " +
                            $"{totalSubscribers:N0} total subscribers detected.{gen2Note}{finNote}",
                Recommendation = "Unsubscribe event handlers in Dispose() to release publisher references. " +
                                 "Use WeakEventManager or weak-reference delegate patterns for long-lived publishers. " +
                                 "Review PropertyChanged and custom event patterns for unbounded subscription growth.",
                Tags = ["event-leak", "gen2", "finalizer", "cross-cutting"],
                MetricValue = totalSubscribers,
                MetricUnit = "subscribers",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
