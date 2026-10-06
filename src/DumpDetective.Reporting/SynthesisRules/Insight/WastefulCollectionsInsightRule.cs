using DumpDetective.Reporting.SynthesisRules;
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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Observation collections = matched.Matched[0];
        long wastefulCollectionCount = (long)collections.Measures["wasteful-collection.count"].Value;
        if (wastefulCollectionCount == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong totalWastedMemory = (ulong)collections.Measures["total-wasted.bytes"].Value;
        long totalCollections = (long)collections.Measures["total-collection.count"].Value;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::wasteful-collections",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Bytes(totalWastedMemory),
                    Qualifiers = new AssertionQualifiers { Location = "unused capacity in wasteful collections" },
                },
                Support = [new EvidenceStatement([collections.Id], $"{wastefulCollectionCount:N0} of {totalCollections:N0} collections are wasteful, with {InsightByteFormat.FormatBytes(totalWastedMemory)} of unused capacity.")],
                Counter = Counter.None("Unused capacity in short-lived collections is reclaimed with them; their lifetime is not observed."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!do <collection address>",
                        Test = $"If a flagged collection's backing array is close to its element count, the estimate of {InsightByteFormat.FormatBytes(totalWastedMemory)} unused is too high.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the flagged collections are request-scoped, the capacity is reclaimed with each request, not held.",
                    },
                ],
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
