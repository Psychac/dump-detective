using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectWastefulCollections</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class WastefulCollectionsInsightRule : ISynthesisRule
{
    public string RuleId => "insight.wasteful-collections";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["collection.waste-summary"] };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        Observation collections = matched.Matched[0];
        long wastefulCollectionCount = (long)collections.Measures["wasteful-collection.count"].Value;
        if (wastefulCollectionCount == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        ulong totalWastedMemory = (ulong)collections.Measures["total-wasted.bytes"].Value;
        long totalCollections = (long)collections.Measures["total-collection.count"].Value;

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::wasteful-collections",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Collections with significant wasted capacity found",
                Narrative = $"{wastefulCollectionCount:N0} wasteful collection(s) identified, " +
                            $"wasting {InsightByteFormat.FormatBytes(totalWastedMemory)} total. " +
                            $"Total collections scanned: {totalCollections:N0}.",
                Recommendation = "Call TrimExcess() after bulk-removing items from List<T> or Dictionary<K,V>. " +
                                 "Use initial capacity hints in collection constructors to avoid over-allocation.",
                Tags = ["collections", "capacity", "wasted-memory"],
                DerivedFrom = [collections.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
