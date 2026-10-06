using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectLohArrayPressure</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>type.array-population</c> carried <c>loh.bytes</c> already but not <c>loh.count</c> — added
/// specifically for this rule (<c>ArrayAnalyzer.EmitObservationsAsync</c>).
/// </remarks>
internal sealed class LohArrayPressureInsightRule : ISynthesisRule
{
    private const ulong PressureThresholdBytes = 256UL * 1024 * 1024; // 256 MB
    private const double FragWarningPct = 30.0;

    public string RuleId => "insight.loh-array-pressure";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.array-population", "heap.loh-fragmentation-summary"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? arrays = null, lohFrag = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.array-population": arrays = observation; break;
                case "heap.loh-fragmentation-summary": lohFrag = observation; break;
            }
        }

        if (arrays is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong lohArrayBytes = (ulong)arrays.Measures["loh.bytes"].Value;
        if (lohArrayBytes < PressureThresholdBytes)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long lohArrayCount = (long)arrays.Measures["loh.count"].Value;
        double fragmentationPercent = lohFrag?.Measures["fragmentation.percent"].Value ?? 0.0;
        bool fragCorrelation = lohFrag is not null && fragmentationPercent >= FragWarningPct;

        Severity severity = fragCorrelation ? Severity.Warning : Severity.Info;

        string fragNote = fragCorrelation
            ? $" Combined with LOH fragmentation of {fragmentationPercent:F1}%, " +
              "this indicates compounding heap pressure."
            : string.Empty;

        var derivedFrom = new List<ObservationId> { arrays.Id };
        if (lohFrag is not null) derivedFrom.Add(lohFrag.Id);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::loh-array-pressure",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Bytes(lohArrayBytes),
                    Reference = ClaimParts.Budget(ClaimParts.Bytes(PressureThresholdBytes), "LOH array threshold"),
                    Qualifiers = new AssertionQualifiers
                    {
                        Location = "large arrays on the Large Object Heap",
                        Mechanism = fragCorrelation ? $"LOH fragmentation of {fragmentationPercent:F1}%" : null,
                    },
                },
                Support =
                [
                    new EvidenceStatement([arrays.Id], $"{lohArrayCount:N0} large arrays hold {InsightByteFormat.FormatBytes(lohArrayBytes)} on the LOH."),
                    .. lohFrag is not null
                        ? [new EvidenceStatement([lohFrag.Id], $"LOH fragmentation is {fragmentationPercent:F1}%, read alongside the array footprint.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("Large arrays that die young cost fragmentation, not growth; their lifetime is not observed."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -stat -min 85000",
                        Test = $"If the arrays of 85,000 bytes or more total far less than {InsightByteFormat.FormatBytes(lohArrayBytes)}, the LOH array figure is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the large arrays are different ones and the total is flat, they are churn, not retention.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "Large arrays are a major LOH contributor",
                Narrative = $"{InsightByteFormat.FormatBytes(lohArrayBytes)} of LOH memory is held by " +
                            $"{lohArrayCount:N0} large array object(s).{fragNote}",
                Recommendation = "Rent large arrays from ArrayPool<T> instead of allocating them directly. " +
                                 "Pool buffers prevent LOH growth and associated fragmentation.",
                Tags = ["loh", "arrays", "pooling"],
                MetricValue = (double)lohArrayBytes,
                MetricUnit = "bytes",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
