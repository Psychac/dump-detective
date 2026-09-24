using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>LohFragmentationFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>heap.loh-fragmentation</c> is emitted per segment-kind breakdown row (Large/Pinned, at most
/// two), each with its own <c>fragmentation.percent</c> for that kind alone — never the old
/// generator's <c>LohFragmentationDomainResult.FragmentationPercent</c>, which is the
/// combined-across-both-kinds total. Reconstructing it means summing <c>total.bytes</c>/
/// <c>free.bytes</c>/<c>segment.count</c> across every matched row and recomputing the percentage
/// from those sums — never averaging the per-kind percentages, which would silently misweight a
/// case where one kind has far more segments than the other.
/// <c>heap.loh-fragmentation-summary</c> is read only for the free-block count and largest free
/// block, which the per-kind rows don't carry as a combined figure.
/// </remarks>
internal sealed class LohFragmentationSynthesisRule : ISynthesisRule
{
    private const double CriticalThreshold = 30.0;
    private const double WarningThreshold = 15.0;

    private const string AnalyzerName = "LOH & POH Fragmentation Analysis";

    public string RuleId => "loh-fragmentation.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.loh-fragmentation", "heap.loh-fragmentation-summary"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long segmentCount = 0;
        ulong totalBytes = 0;
        ulong freeBytes = 0;
        Observation? summary = null;
        var derivedFrom = new List<ObservationId>(matched.Matched.Count);

        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "heap.loh-fragmentation-summary")
            {
                summary = observation;
                derivedFrom.Add(observation.Id);
                continue;
            }

            segmentCount += (long)observation.Measures["segment.count"].Value;
            totalBytes += (ulong)observation.Measures["total.bytes"].Value;
            freeBytes += (ulong)observation.Measures["free.bytes"].Value;
            derivedFrom.Add(observation.Id);
        }

        double fragmentationPercent = totalBytes > 0 ? freeBytes * 100.0 / totalBytes : 0.0;

        Severity severity = fragmentationPercent >= CriticalThreshold ? Severity.Critical
            : fragmentationPercent >= WarningThreshold ? Severity.Warning
            : Severity.Info;

        Finding finding = new()
        {
            Fingerprint = $"{AnalyzerName}::loh-fragmentation-assessment".ToLowerInvariant(),
            Analyzer = AnalyzerName,
            Category = "Fragmentation",
            Severity = severity,
            Title = severity switch
            {
                Severity.Critical => $"Critical LOH/POH fragmentation — {fragmentationPercent:F1}% of LOH/POH is free space",
                Severity.Warning => $"Elevated LOH/POH fragmentation — {fragmentationPercent:F1}% free-space fragmentation",
                _ => $"LOH/POH fragmentation within acceptable range — {fragmentationPercent:F1}%",
            },
            Narrative = BuildNarrative(totalBytes, freeBytes, segmentCount, summary),
            Recommendation = severity switch
            {
                Severity.Critical => "Compact the LOH via GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true), or pool large objects (ArrayPool<T>/MemoryPool<T>) to reduce allocation churn.",
                Severity.Warning => "Monitor LOH allocation patterns. Consider using ArrayPool<T> or MemoryPool<T> for large buffers to reduce fragmentation over time.",
                _ => "LOH fragmentation is currently within acceptable range.",
            },
            Tags = ["loh", "fragmentation", "memory"],
            MetricValue = fragmentationPercent,
            MetricUnit = "% fragmentation",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        };

        return ValueTask.FromResult<IReadOnlyList<Finding>>([finding]);
    }

    private static string BuildNarrative(ulong totalBytes, ulong freeBytes, long segmentCount, Observation? summary)
    {
        string narrative = $"Total LOH/POH: {FormatHelper.FormatBytes(totalBytes)}, free: {FormatHelper.FormatBytes(freeBytes)} across {segmentCount:N0} segment(s).";
        if (summary is null)
            return narrative;

        long freeBlockCount = (long)summary.Measures["free-block.count"].Value;
        ulong largestFreeBlock = (ulong)summary.Measures["largest-free-block.bytes"].Value;
        return narrative + $" {freeBlockCount:N0} free blocks; largest free block: {FormatHelper.FormatBytes(largestFreeBlock)}.";
    }
}
