using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectLohFragmentation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Named distinctly from <c>LohFragmentationSynthesisRule</c> (which already replaced
/// <c>LohFragmentationFindingGenerator</c> in step 2/3 — a different rule, different observation
/// type, different finding). This one reads the new whole-run <c>heap.loh-fragmentation-summary</c>
/// observation added specifically for this migration
/// (<c>LohFragmentationAnalyzer.EmitObservationsAsync</c>) — the pre-existing per-kind
/// <c>heap.loh-fragmentation</c> rows don't carry a <c>free-block.count</c> measure at all, and
/// splitting LOH from POH there doesn't match this rule's whole-LOH-aggregate semantics.
/// </remarks>
internal sealed class LohFragmentationInsightRule : ISynthesisRule
{
    private const double WarningPct = 30.0;
    private const double CriticalPct = 60.0;

    public string RuleId => "insight.loh-fragmentation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["heap.loh-fragmentation-summary"] };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        Observation summary = matched.Matched[0];
        double fragmentationPercent = summary.Measures["fragmentation.percent"].Value;

        if (fragmentationPercent < WarningPct)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        Severity severity = fragmentationPercent >= CriticalPct ? Severity.Critical : Severity.Warning;
        ulong freeBytes = (ulong)summary.Measures["free.bytes"].Value;
        int freeBlockCount = (int)summary.Measures["free-block.count"].Value;
        ulong largestFreeBlock = (ulong)summary.Measures["largest-free-block.bytes"].Value;

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::loh-fragmentation",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "LOH fragmentation is high",
                Narrative = $"LOH fragmentation: {fragmentationPercent:F1}% " +
                            $"({InsightByteFormat.FormatBytes(freeBytes)} free in {freeBlockCount} blocks, " +
                            $"largest free block: {InsightByteFormat.FormatBytes(largestFreeBlock)}).",
                Recommendation = "Enable LOH compaction via GCSettings.LargeObjectHeapCompactionMode, " +
                                 "reduce LOH allocations, or upgrade to .NET 6+ with improved LOH handling.",
                Tags = ["loh", "fragmentation", "gc"],
                DerivedFrom = [summary.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
