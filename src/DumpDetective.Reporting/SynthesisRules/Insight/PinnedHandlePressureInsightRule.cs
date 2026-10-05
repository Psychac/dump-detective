using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectPinnedHandlePressure</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class PinnedHandlePressureInsightRule : ISynthesisRule
{
    private const int WarningCount = 100;
    private const double FragWarningPct = 30.0;

    public string RuleId => "insight.pinned-handle-pressure";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["handle.population", "heap.loh-fragmentation-summary"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? handles = null, lohFrag = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "handle.population": handles = observation; break;
                case "heap.loh-fragmentation-summary": lohFrag = observation; break;
            }
        }

        if (handles is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long pinnedHandleTargets = (long)handles.Measures["pinned-target.count"].Value;
        if (pinnedHandleTargets < WarningCount)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double fragmentationPercent = lohFrag?.Measures["fragmentation.percent"].Value ?? 0.0;
        bool correlatedWithFrag = lohFrag is not null && fragmentationPercent >= FragWarningPct;
        Severity severity = correlatedWithFrag ? Severity.Critical : Severity.Warning;

        string correlation = correlatedWithFrag
            ? $" LOH fragmentation is {fragmentationPercent:F1}%, which may be caused by pinning."
            : string.Empty;

        var derivedFrom = new List<ObservationId> { handles.Id };
        if (lohFrag is not null) derivedFrom.Add(lohFrag.Id);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::pinned-handle-pressure",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "High number of pinned GC handles detected",
                Narrative = $"{pinnedHandleTargets:N0} pinned handles found.{correlation}",
                Recommendation = "Excessive pinning prevents GC from compacting memory, increasing fragmentation. " +
                                 "Use Memory<T>/Span<T> with MemoryHandle or GCHandle.Alloc(pin=false) where possible. " +
                                 "Ensure pinned buffers are released promptly.",
                Tags = ["pinning", "gc", "fragmentation", "handles"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
