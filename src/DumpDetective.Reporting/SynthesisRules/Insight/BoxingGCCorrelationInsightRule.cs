using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectBoxingGCCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class BoxingGCCorrelationInsightRule : ISynthesisRule
{
    private const int BoxedEnumThreshold = 10_000;
    private const double HighGen0Pct = 40.0;

    public string RuleId => "insight.boxing-gc-correlation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.boxing-pressure", "gc.generation-composition"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? boxing = null, gcGen = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.boxing-pressure": boxing = observation; break;
                case "gc.generation-composition": gcGen = observation; break;
            }
        }

        if (boxing is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long totalBoxedObjects = (long)boxing.Measures["total-boxed.count"].Value;
        if (totalBoxedObjects == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long boxedEnumCount = (long)boxing.Measures["boxed-enum.count"].Value;
        bool highBoxedEnums = boxedEnumCount > BoxedEnumThreshold;

        double gen0Fraction = 0.0;
        bool highGen0Pct = false;
        if (gcGen is not null)
        {
            long totalObjects = (long)gcGen.Measures["total.count"].Value;
            if (totalObjects > 0)
            {
                gen0Fraction = gcGen.Measures["gen0.count"].Value * 100.0 / totalObjects;
                highGen0Pct = gen0Fraction >= HighGen0Pct;
            }
        }

        if (!highBoxedEnums && !highGen0Pct)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        string gen0Note = highGen0Pct
            ? $" Gen0 holds {gen0Fraction:F1}% of managed heap objects — consistent with high transient boxing."
            : string.Empty;

        string enumNote = highBoxedEnums
            ? $" {boxedEnumCount:N0} boxed enum instances found."
            : string.Empty;

        ulong totalBoxedBytes = (ulong)boxing.Measures["total-boxed.bytes"].Value;

        var derivedFrom = gcGen is not null ? new[] { boxing.Id, gcGen.Id } : [boxing.Id];

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::boxing-gc-correlation",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Boxing pressure correlates with elevated Gen0 churn",
                Narrative = $"{totalBoxedObjects:N0} boxed value type instances " +
                            $"({InsightByteFormat.FormatBytes(totalBoxedBytes)}).{enumNote}{gen0Note}",
                Recommendation = "Replace object/non-generic collection APIs with generic alternatives " +
                                 "(List<T>, Dictionary<TKey,TValue>). Use enum-typed parameters instead of object.",
                Tags = ["boxing", "gen0", "gc-pressure", "value-type"],
                MetricValue = totalBoxedObjects,
                MetricUnit = "objects",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
