using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectPohGrowth</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class PohGrowthInsightRule : ISynthesisRule
{
    private const double WarningPct = 10.0;

    public string RuleId => "insight.poh-growth";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["heap.topology-population"] };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Observation segments = matched.Matched[0];
        double pohPercent = segments.Measures["poh.percent"].Value;
        if (pohPercent < WarningPct)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong pohBytes = (ulong)segments.Measures["poh.bytes"].Value;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::poh-growth",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(pohPercent),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(WarningPct), "POH share threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the Pinned Object Heap's share of the heap" },
                },
                Support = [new EvidenceStatement([segments.Id], $"The POH is {pohPercent:F1}% of the heap, {InsightByteFormat.FormatBytes(pohBytes)}.")],
                Counter = Counter.None("The POH holds buffers pinned on purpose for I/O; a large share does not by itself show pinning is excessive."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If the POH size in !eeheap -gc is far from {InsightByteFormat.FormatBytes(pohBytes)}, the POH share is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the POH share does not grow, it is a stable buffer pool, not growth.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Pinned Object Heap (POH) occupies significant heap space",
                Narrative = $"POH is {pohPercent:F1}% of total heap ({InsightByteFormat.FormatBytes(pohBytes)}).",
                Recommendation = "Audit use of GC.AllocateArray with pinned=true and unsafe pinned buffers. " +
                                 "Excessive POH usage can fragment address space and increase GC pause times.",
                Tags = ["poh", "pinning", "gc"],
                DerivedFrom = [segments.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
