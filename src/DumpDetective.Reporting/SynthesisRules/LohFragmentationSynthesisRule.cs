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
    private const double SmallGapDominancePercent = 80.0;

    private const string AnalyzerName = "LOH & POH Fragmentation Analysis";

    public string RuleId => "loh-fragmentation.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.loh-fragmentation", "heap.loh-fragmentation-summary"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long segmentCount = 0;
        ulong totalBytes = 0;
        ulong freeBytes = 0;
        Observation? summary = null;
        var derivedFrom = new List<ObservationId>(matched.Matched.Count);
        var segmentRowIds = new List<ObservationId>(2);

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
            segmentRowIds.Add(observation.Id);
        }

        double fragmentationPercent = totalBytes > 0 ? freeBytes * 100.0 / totalBytes : 0.0;
        double subKbGapPercent = SubKbGapPercent(summary);

        Severity severity = fragmentationPercent >= CriticalThreshold ? Severity.Critical
            : fragmentationPercent >= WarningThreshold ? Severity.Warning
            : Severity.Info;

        Claim finding = new()
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
            Assertion = BuildAssertion(severity, fragmentationPercent),
            Support = BuildSupport(segmentRowIds, summary),
            Counter = BuildCounter(severity, summary, subKbGapPercent),
            FalsifiedBy = BuildFalsifiedBy(severity, fragmentationPercent, freeBytes),
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([finding]);
    }

    private static double SubKbGapPercent(Observation? summary)
    {
        if (summary is null)
            return 0;

        long freeGapCount = (long)summary.Measures["free-gap.count"].Value;
        return freeGapCount == 0 ? 0 : summary.Measures["sub-kb-free-gap.count"].Value * 100.0 / freeGapCount;
    }

    private static Assertion BuildAssertion(Severity severity, double fragmentationPercent)
    {
        double budget = severity == Severity.Critical ? CriticalThreshold : WarningThreshold;
        string budgetName = severity == Severity.Critical ? "LOH/POH fragmentation critical threshold" : "LOH/POH fragmentation warning threshold";

        return new Assertion
        {
            Predicate = severity == Severity.Info ? AssertionPredicate.Normal : AssertionPredicate.Fragments,
            Measure = new Measure(fragmentationPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
            Reference = new Reference { Value = new Measure(budget, MeasureUnit.Percent, MeasureSemantics.Ratio), Basis = budgetName },
            Qualifiers = new AssertionQualifiers { Location = "LOH/POH", Scope = "all LOH and POH segments" },
        };
    }

    private static IReadOnlyList<EvidenceStatement> BuildSupport(IReadOnlyList<ObservationId> segmentRowIds, Observation? summary)
    {
        var support = new List<EvidenceStatement>(2)
        {
            new(segmentRowIds, "Free bytes divided by total bytes, summed across these segment rows, gives the fragmentation percentage."),
        };

        if (summary is not null)
            support.Add(new([summary.Id], "Supplies the free-block count, largest free block and free-gap size distribution."));

        return support;
    }

    // Counter-evidence points away from whatever the claim says: against "fragmented" it is reusable-size gaps,
    // against "normal" it is sliver-dominated free space that a low percentage hides.
    private static Counter BuildCounter(Severity severity, Observation? summary, double subKbGapPercent)
    {
        if (summary is null)
            return Counter.None("No free-block distribution was available to judge whether the free space is reusable.");

        bool sliverDominated = subKbGapPercent > SmallGapDominancePercent;

        if (severity == Severity.Info)
        {
            return sliverDominated
                ? Counter.Of(new EvidenceStatement(
                    [summary.Id],
                    $"{subKbGapPercent:F0}% of free gaps are under 1 KB, so a low free percentage may still leave too little usable space for LOH/POH allocations."))
                : Counter.None("Free gaps are mostly large enough to be reused, so nothing in the distribution contradicts the low percentage.");
        }

        return sliverDominated
            ? Counter.None("Free space is dominated by sub-KB slivers, so nothing in the distribution suggests it is reusable.")
            : Counter.Of(new EvidenceStatement(
                [summary.Id],
                $"Only {subKbGapPercent:F0}% of free gaps are under 1 KB, so a meaningful share of the free space may be reusable without compaction."));
    }

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(Severity severity, double fragmentationPercent, ulong freeBytes)
    {
        var windbgTest = new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!dumpheap -type Free -stat",
            Test = $"If the free bytes on LOH/POH segments differ materially from the reported {FormatHelper.FormatBytes(freeBytes)}, the measurement is wrong.",
        };

        if (severity == Severity.Info)
            return [windbgTest];

        return
        [
            windbgTest,
            new FalsificationTest
            {
                Kind = FalsificationKind.Capture,
                Test = $"Capture a second dump after the workload quiesces; if free space falls below {WarningThreshold:F0}% without a compacting GC, the {fragmentationPercent:F1}% was transient, not fragmentation.",
            },
        ];
    }

    private static string BuildNarrative(ulong totalBytes, ulong freeBytes, long segmentCount, Observation? summary)
    {
        string narrative = $"Total LOH/POH: {FormatHelper.FormatBytes(totalBytes)}, free: {FormatHelper.FormatBytes(freeBytes)} across {segmentCount:N0} segment(s).";
        if (summary is null)
            return narrative;

        long freeBlockCount = (long)summary.Measures["free-block.count"].Value;
        ulong largestFreeBlock = (ulong)summary.Measures["largest-free-block.bytes"].Value;
        narrative += $" {freeBlockCount:N0} free blocks; largest free block: {FormatHelper.FormatBytes(largestFreeBlock)}.";

        long freeGapCount = (long)summary.Measures["free-gap.count"].Value;
        double subKbPercent = freeGapCount == 0 ? 0 : summary.Measures["sub-kb-free-gap.count"].Value * 100.0 / freeGapCount;
        if (subKbPercent > SmallGapDominancePercent)
            narrative += $" {subKbPercent:F0}% of free gaps are under 1 KB — the free space is fragmented into slivers too small to satisfy any meaningful LOH/POH allocation without compaction.";

        return narrative;
    }
}
