using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectLohPressure</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Same three-source fallback chain as the original (<c>memory ?? gcGen ?? segments</c>) — all
/// three already carry <c>loh.percent</c>/<c>loh.bytes</c> as raw measures
/// (<c>heap.memory-composition</c>, <c>gc.generation-composition</c>, <c>heap.topology-population</c>
/// respectively), so no new emission was needed for this one.
/// </remarks>
internal sealed class LohPressureInsightRule : ISynthesisRule
{
    private const double WarningPct = 25.0;
    private const double CriticalPct = 40.0;

    public string RuleId => "insight.loh-pressure";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.memory-composition", "gc.generation-composition", "heap.topology-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? memory = null, gcGen = null, segments = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "heap.memory-composition": memory = observation; break;
                case "gc.generation-composition": gcGen = observation; break;
                case "heap.topology-population": segments = observation; break;
            }
        }

        double lohPct = memory?.Measures["loh.percent"].Value
            ?? gcGen?.Measures["loh.percent"].Value
            ?? segments?.Measures["loh.percent"].Value
            ?? -1;

        if (lohPct < WarningPct)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Severity severity = lohPct >= CriticalPct ? Severity.Critical : Severity.Warning;

        ulong lohBytes = (ulong)(memory?.Measures["loh.bytes"].Value
            ?? gcGen?.Measures["loh.bytes"].Value
            ?? segments?.Measures["loh.bytes"].Value
            ?? 0);

        Observation source = memory ?? gcGen ?? segments!;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::loh-pressure",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(lohPct),
                    Reference = severity == Severity.Critical
                        ? ClaimParts.Budget(ClaimParts.Percent(CriticalPct), "critical threshold")
                        : ClaimParts.Budget(ClaimParts.Percent(WarningPct), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the Large Object Heap's share of the heap" },
                },
                Support = [new EvidenceStatement([source.Id], $"The LOH holds {InsightByteFormat.FormatBytes(lohBytes)}, {lohPct:F1}% of the heap.")],
                Counter = Counter.None("Large objects that die young cost fragmentation, not growth; their lifetime is not observed."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If the LOH size in !eeheap -gc is far from {InsightByteFormat.FormatBytes(lohBytes)}, the LOH share is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the LOH share does not grow, it is a stable buffer working set.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "Large Object Heap (LOH) pressure detected",
                Narrative = $"LOH is {lohPct:F1}% of total heap ({InsightByteFormat.FormatBytes(lohBytes)}). " +
                            $"Threshold: Warning ≥ {WarningPct}%, Critical ≥ {CriticalPct}%.",
                Recommendation = "Review allocations ≥ 85 KB. Consider pooling large arrays (ArrayPool<T>), " +
                                 "chunking large collections, or enabling Server GC with LOH compaction.",
                Tags = ["loh", "gc", "memory-pressure"],
                DerivedFrom = [source.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
