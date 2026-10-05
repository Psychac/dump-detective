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
