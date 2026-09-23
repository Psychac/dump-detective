using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectStaticRootFinalizableCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>finalizer.queue-entry</c> (already existed, exhaustive over the finalizer queue) covers the
/// finalizer side with no new emission — grouped by type name here the same way
/// <c>DataTableLifecycleInsightRule</c> already does. The static-root side needed a new
/// <c>heap.static-root-retained-type</c> observation: <c>StaticRootAnalyzer</c>'s existing
/// <c>heap.static-root</c> subjects on the root object's own type, not its (bounded)
/// <c>TopRetainedTypes</c> breakdown. There is no <c>root</c> <c>EntityKind</c> in the SDK yet
/// (<c>ReportSystemVision.md</c> §4.1's root entity is aspirational, not shipped), so the new
/// observation subjects on the retained <c>TypeRef</c> instead and carries the root's description as
/// narrative-only <c>Evidence</c> — the same call already made for <c>thread.stack-cluster</c>'s
/// <c>Signature</c> and <c>thread.blocked</c>'s lock-object-types.
/// </remarks>
internal sealed class StaticRootFinalizableCorrelationInsightRule : ISynthesisRule
{
    public string RuleId => "insight.static-root-finalizable-correlation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.static-root-retained-type", "finalizer.queue-entry"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var queueCountByType = new Dictionary<string, int>(StringComparer.Ordinal);
        var queueEntryIdByType = new Dictionary<string, ObservationId>(StringComparer.Ordinal);
        var retainedTypeObservations = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "finalizer.queue-entry":
                {
                    string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                    queueCountByType[typeName] = queueCountByType.GetValueOrDefault(typeName) + 1;
                    queueEntryIdByType.TryAdd(typeName, observation.Id);
                    break;
                }
                case "heap.static-root-retained-type":
                    retainedTypeObservations.Add(observation);
                    break;
            }
        }

        if (queueCountByType.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        string? bestRootDescription = null;
        string? bestTypeName = null;
        int bestQueueCount = 0;
        int bestRetainedCount = 0;
        Observation? bestRetainedTypeObservation = null;

        foreach (Observation retained in retainedTypeObservations)
        {
            string typeName = ((TypeRef)retained.Subjects[0]).CanonicalName;
            if (!queueCountByType.TryGetValue(typeName, out int queueCount) || queueCount <= bestQueueCount)
                continue;

            bestQueueCount = queueCount;
            bestRootDescription = retained.Evidence.Count > 0 ? retained.Evidence[0].Description : null;
            bestTypeName = typeName;
            bestRetainedCount = (int)retained.Measures["instance.count"].Value;
            bestRetainedTypeObservation = retained;
        }

        if (bestRootDescription is null || bestRetainedTypeObservation is null || bestTypeName is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var derivedFrom = new List<ObservationId> { bestRetainedTypeObservation.Id };
        if (queueEntryIdByType.TryGetValue(bestTypeName, out ObservationId queueEntryId))
            derivedFrom.Add(queueEntryId);

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::static-root-finalizable-correlation",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Static root retains a type with objects queued for finalization",
                Narrative = $"Static root '{bestRootDescription}' retains {bestRetainedCount:N0} instance(s) of " +
                            $"{bestTypeName}, which also has {bestQueueCount:N0} instance(s) currently queued for " +
                            "finalization — this root may be delaying their collection.",
                Recommendation = "Confirm this static root's ownership of the type is intentional (e.g. a cache), " +
                                 "and that instances are removed or disposed once no longer needed so finalization " +
                                 "isn't delayed by long-lived static retention.",
                Tags = ["static-root", "finalizer", "cross-cutting"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
