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
        ObservationTypes = ["heap.loh-fragmentation"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long segmentCount = 0;
        ulong totalBytes = 0;
        ulong freeBytes = 0;
        var derivedFrom = new List<ObservationId>(matched.Matched.Count);

        foreach (Observation observation in matched.Matched)
        {
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
            Title = "LOH fragmentation assessment",
            Narrative = $"{fragmentationPercent:F1}% overall free-space fragmentation across {segmentCount:N0} LOH/POH segment(s).",
            Recommendation = severity == Severity.Critical
                ? "Investigate large object allocation churn and retention; consider compaction strategies and pooling."
                : severity == Severity.Warning
                    ? "Monitor LOH allocation patterns and reduce churn from short-lived large allocations."
                    : "LOH fragmentation is currently within acceptable range.",
            Tags = ["loh", "fragmentation", "memory"],
            MetricValue = fragmentationPercent,
            MetricUnit = "% fragmentation",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        };

        return ValueTask.FromResult<IReadOnlyList<Finding>>([finding]);
    }
}
