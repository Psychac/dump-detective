using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectStaleWeakReferenceAccumulation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>weakreference.population</c> carried the dead-target counts already but not
/// <c>StaleWrapperCount</c>/<c>StaleWrapperCountIsExact</c> — the per-candidate
/// <c>weakreference.stale-wrapper</c> observation is bounded, so summing it wouldn't reconstruct
/// the true whole-run count. Both were added specifically for this rule
/// (<c>WeakReferenceAnalyzer.EmitObservationsAsync</c>).
/// </remarks>
internal sealed class StaleWeakReferenceAccumulationInsightRule : ISynthesisRule
{
    private const double WarningRatio = 0.50;

    public string RuleId => "insight.stale-weak-reference-accumulation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["weakreference.population"] };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Observation weakRef = matched.Matched[0];
        long totalWeakHandles = (long)weakRef.Measures["total-handles.count"].Value;
        if (totalWeakHandles == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long deadWeakTargets = (long)weakRef.Measures["dead-targets.count"].Value;
        double deadTargetRatio = (double)deadWeakTargets / totalWeakHandles;
        if (deadTargetRatio < WarningRatio)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Severity severity = deadTargetRatio >= 0.80 ? Severity.Warning : Severity.Info;

        long staleWrapperCount = (long)weakRef.Measures["stale-wrapper.count"].Value;
        bool staleWrapperCountIsExact = weakRef.Measures["stale-wrapper.is-exact"].Value != 0;

        string staleNote = staleWrapperCount > 0
            ? $" {staleWrapperCount:N0}{(staleWrapperCountIsExact ? "" : " (estimated)")} stale WeakReference<T> wrapper object(s) detected."
            : string.Empty;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::stale-weak-reference-accumulation",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "High dead-target ratio in weak GC handles",
                Narrative = $"{deadWeakTargets:N0} of {totalWeakHandles:N0} weak handles " +
                            $"({deadTargetRatio:P0}) point to already-collected objects.{staleNote}",
                Recommendation = "Review code that creates WeakReference<T> objects and cleans up stale entries. " +
                                 "ConditionalWeakTable<TKey,TValue> manages lifetime automatically; " +
                                 "custom caches using WeakReference need periodic compaction.",
                Tags = ["weak-reference", "stale", "gc-handles"],
                MetricValue = deadTargetRatio,
                MetricUnit = "ratio",
                DerivedFrom = [weakRef.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
