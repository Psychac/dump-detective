using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectMemoryTypeGenerationCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// The original cross-joined <c>MemoryDomainResult.TopTypes</c> against
/// <c>GCGenerationDomainResult.PerTypeGenerationProfiles</c>, but both carry the same per-type
/// total from the heap index, so <c>gc.generation-type-profile</c> alone is enough — no
/// cross-analyzer join. Deliberate change from the original: every type of at least
/// <see cref="MinBytes"/> is checked, not only the top 30 by size. The original's evidence table
/// is dropped; every matching type is cited via <c>DerivedFrom</c> and the largest is named in the
/// narrative.
/// </remarks>
internal sealed class MemoryTypeGenerationInsightRule : ISynthesisRule
{
    private const ulong MinBytes = 10UL * 1024 * 1024;
    private const ulong WarningBytes = 100UL * 1024 * 1024;
    private const double Gen2FractionPct = 85.0;

    public string RuleId => "insight.memory-type-generation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["gc.generation-type-profile"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var matches = new List<(Observation Observation, string TypeName, ulong TotalBytes, double Gen2FractionPct)>();

        foreach (Observation observation in matched.Matched)
        {
            IReadOnlyDictionary<string, Measure> m = observation.Measures;
            ulong totalBytes = (ulong)m["total.bytes"].Value;
            if (totalBytes < MinBytes)
                continue;

            double gen2FractionPct = Gen2FractionPercent(m, totalBytes);
            if (gen2FractionPct >= Gen2FractionPct)
                matches.Add((observation, ((TypeRef)observation.Subjects[0]).CanonicalName, totalBytes, gen2FractionPct));
        }

        if (matches.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        matches.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

        var derivedFrom = new List<ObservationId>(matches.Count);
        foreach (var match in matches)
            derivedFrom.Add(match.Observation.Id);

        var largest = matches[0];

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::memory-type-generation",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = largest.TotalBytes >= WarningBytes ? Severity.Warning : Severity.Info,
                Title = "Large heap types are almost entirely long-lived (Gen2)",
                Narrative = $"{matches.Count:N0} type(s) of at least {InsightByteFormat.FormatBytes(MinBytes)} are ≥ " +
                            $"{Gen2FractionPct:F0}% Gen2. Largest: '{largest.TypeName}' at " +
                            $"{InsightByteFormat.FormatBytes(largest.TotalBytes)} ({largest.Gen2FractionPct:F1}% Gen2).",
                Recommendation = "High Gen2 residency for a top-size type usually means the instances are held by " +
                                 "long-lived roots (statics, caches, event subscriptions) rather than transient churn. " +
                                 "Cross-check the Dominator/GC Root analyzer findings for these type names to locate the " +
                                 "retaining reference chain.",
                Tags = ["memory", "gc-generation", "cross-analyzer", "long-lived"],
                MetricValue = largest.TotalBytes,
                MetricUnit = "bytes",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }

    // Prefers exact Gen2 bytes over instance counts: a type whose Gen2 instances are much larger
    // than its Gen0/Gen1 ones (a collection that keeps reallocating bigger backing arrays as it
    // survives) looks mild by count while actually retaining most of its bytes in Gen2. Falls back
    // to counts for heap indices written before Gen2 bytes existed.
    private static double Gen2FractionPercent(IReadOnlyDictionary<string, Measure> m, ulong totalBytes)
    {
        double gen2Bytes = m["gen2.bytes"].Value;
        if (gen2Bytes > 0 && totalBytes > 0)
            return gen2Bytes * 100.0 / totalBytes;

        double gen2Count = m["gen2.count"].Value;
        double totalCounted = m["gen0.count"].Value + m["gen1.count"].Value + gen2Count + m["loh.count"].Value;
        return totalCounted == 0 ? 0.0 : gen2Count * 100.0 / totalCounted;
    }
}
