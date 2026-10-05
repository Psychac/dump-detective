using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>SegmentReservationFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Re-derives <c>AddressSpacePressureRisk</c>/<c>PressureRiskReason</c> from raw measures rather
/// than reading a stored value — both are Stage-1 judgment (a hand-picked-threshold
/// classification, the audit's Smell A), deliberately not carried by
/// <c>heap.segment-reservation</c>. The formula (<c>EvaluatePressureRisk</c> below) is duplicated
/// verbatim from <c>SegmentReservationAnalyzer.Analyze</c>, the same "re-derive rather than reach
/// into another assembly's internals" choice <c>AllocationPatternSynthesisRule</c>/
/// <c>MemorySynthesisRule</c> made for their own classifiers.
/// <para>
/// <c>NearEmptyRegionCount</c>/<c>NearEmptyRegionCommittedBytes</c> need no dedicated measures:
/// when the dump is regions-based, they're exact sums over every matched
/// <c>heap.region-composition</c> observation's own <c>near-empty.count</c>/
/// <c>near-empty.committed-bytes</c> (emitted for every region-generation bucket, never a capped
/// sample) — the same "don't duplicate what summing already gives you" choice
/// <c>DbConnectionSynthesisRule</c> made for <c>OpenConnections</c>. <c>IsRegionsBased</c> itself is
/// reconstructed as "did any <c>heap.region-composition</c> observation arrive" — that observation
/// type is only ever emitted when the dump is regions-based.
/// </para>
/// </remarks>
internal sealed class SegmentReservationSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Segment Reservation Analysis";

    public string RuleId => "segment-reservation.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.segment-reservation", "heap.region-composition"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = null;
        var regionIds = new List<ObservationId>();
        long nearEmptyRegionCount = 0;
        ulong nearEmptyRegionCommittedBytes = 0;
        bool isRegionsBased = false;

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "heap.segment-reservation":
                    composition = observation;
                    break;
                case "heap.region-composition":
                    isRegionsBased = true;
                    regionIds.Add(observation.Id);
                    nearEmptyRegionCount += (long)observation.Measures["near-empty.count"].Value;
                    nearEmptyRegionCommittedBytes += (ulong)observation.Measures["near-empty.committed-bytes"].Value;
                    break;
            }
        }

        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong totalCommittedBytes = (ulong)composition.Measures["committed.bytes"].Value;
        ulong totalReservedBytes = (ulong)composition.Measures["reserved.bytes"].Value;
        ulong reservationGapBytes = (ulong)composition.Measures["reservation-gap.bytes"].Value;
        double ratio = composition.Measures["reserved-to-committed.ratio"].Value;
        int ephemeralSegmentCount = (int)composition.Measures["ephemeral.segment-count"].Value;
        double avgEphemeralFillPct = composition.Measures["ephemeral.avg-fill-percent"].Value;
        int dumpPointerSize = (int)composition.Measures["dump-pointer-size"].Value;
        long logicalHeapCount = (long)composition.Measures["logical-heap.count"].Value;
        double ratioHighPressureThreshold = composition.Measures["ratio-high-pressure.threshold"].Value;
        double ratioMediumPressureThreshold = composition.Measures["ratio-medium-pressure.threshold"].Value;
        ulong thirtyTwoBitPressureThresholdBytes = (ulong)composition.Measures["thirty-two-bit-pressure.threshold-bytes"].Value;
        double nearEmptyRegionFillPctThreshold = composition.Measures["near-empty-region-fill-percent.threshold"].Value;

        (bool pressureRisk, string pressureReason) = EvaluatePressureRisk(
            dumpPointerSize, totalReservedBytes, ratio, ratioHighPressureThreshold, thirtyTwoBitPressureThresholdBytes);

        var findings = new List<Claim>();

        // ── Address space pressure ──────────────────────────────────────────────
        if (pressureRisk)
        {
            bool is32Bit = dumpPointerSize == 4;
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("address-space-pressure"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = is32Bit ? Severity.Critical : Severity.Warning,
                Title = "Address space pressure risk detected",
                Narrative = pressureReason,
                Recommendation = is32Bit
                    ? "Migrate to a 64-bit process to avoid virtual address exhaustion."
                    : "Investigate GC segment reservation policy; consider tuning System.GC.HeapHardLimit in runtimeconfig.json.",
                Tags = ["segments", "virtual-memory", "address-space"],
                MetricValue = ratio,
                MetricUnit = "ratio",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Reserved/committed ratio crossing its own configured thresholds ─────
        if (ratio > ratioMediumPressureThreshold)
        {
            bool critical = ratio > ratioHighPressureThreshold;
            string reason = pressureRisk && !string.IsNullOrWhiteSpace(pressureReason)
                ? pressureReason
                : $"Reserved/committed ratio is {ratio:F1}x (threshold: {ratioMediumPressureThreshold:F0}x).";

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("reservation-ratio"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = critical ? Severity.Critical : Severity.Warning,
                Title = critical
                    ? $"Address space pressure — reserved/committed ratio {ratio:F1}x"
                    : $"Elevated segment reservation — ratio {ratio:F1}x",
                Narrative = reason,
                Recommendation = critical
                    ? "Review segment reservation settings. On Server GC, consider reducing MaxHeapSize or enabling DATAS. On Workstation GC, check for LOH fragmentation or large pinned regions."
                    : "Monitor heap reservation growth. Reduce MaxHeapSize or consolidate heap segments if address space is constrained.",
                Tags = ["segments", "virtual-memory", "reservation-ratio"],
                MetricValue = ratio,
                MetricUnit = "ratio",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Ephemeral segment fill critical (> 90%) ──────────────────────────────
        if (avgEphemeralFillPct > 90.0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("ephemeral-fill"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Ephemeral segment fill critically high",
                Narrative = $"Average ephemeral fill is {avgEphemeralFillPct:F1}% across {ephemeralSegmentCount} ephemeral segment(s). " +
                            "New GC segments will be allocated shortly.",
                Recommendation = "Reduce Gen0/Gen1 allocation rate or increase GC collection frequency.",
                Tags = ["segments", "ephemeral", "gen0", "gc"],
                MetricValue = avgEphemeralFillPct,
                MetricUnit = "%",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Near-empty GC regions (.NET 8+ regions-based GC) — decommit candidates ──
        if (isRegionsBased && nearEmptyRegionCount > 0)
        {
            const ulong WarningReclaimableBytesThreshold = 50 * 1024 * 1024UL;
            bool warning = nearEmptyRegionCommittedBytes > WarningReclaimableBytesThreshold;

            var derivedFrom = new List<ObservationId>(1 + regionIds.Count) { composition.Id };
            derivedFrom.AddRange(regionIds);

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("near-empty-regions"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = warning ? Severity.Warning : Severity.Info,
                Title = "Near-empty GC regions detected",
                Narrative = $"{nearEmptyRegionCount} region(s) below {nearEmptyRegionFillPctThreshold:F0}% fill, holding {FormatHelper.FormatBytes(nearEmptyRegionCommittedBytes)} of committed memory.",
                Recommendation = "Regions-based GC (DATAS) can decommit near-empty regions over time; if this persists across snapshots, investigate pinned or long-lived objects preventing region evacuation.",
                Tags = ["segments", "regions", "gc-regions", "decommit"],
                MetricValue = nearEmptyRegionCommittedBytes,
                MetricUnit = "bytes",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Summary finding (always) ─────────────────────────────────────────────
        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("overview"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = Severity.Info,
            Title = "Segment reservation overview",
            Narrative = $"Total committed: {FormatHelper.FormatBytes(totalCommittedBytes)}, " +
                        $"reserved: {FormatHelper.FormatBytes(totalReservedBytes)}, " +
                        $"gap: {FormatHelper.FormatBytes(reservationGapBytes)} " +
                        $"(ratio {ratio:F1}x). " +
                        $"Logical heaps: {logicalHeapCount}.",
            Recommendation = ratio > 3.0
                ? "Reserved-to-committed ratio is elevated. Monitor for virtual address fragmentation."
                : "Reservation profile is within normal range.",
            Tags = ["segments", "reserved", "committed"],
            MetricValue = totalReservedBytes,
            MetricUnit = "bytes",
            DerivedFrom = [composition.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    /// <summary>Duplicated verbatim from <c>SegmentReservationAnalyzer.Analyze</c>'s §25.3 address
    /// space pressure evaluation — see remarks.</summary>
    private static (bool PressureRisk, string PressureReason) EvaluatePressureRisk(
        int dumpPointerSize, ulong totalReserved, double ratio, double ratioHighPressureThreshold, ulong thirtyTwoBitPressureThresholdBytes)
    {
        if (dumpPointerSize == 4 && totalReserved > thirtyTwoBitPressureThresholdBytes)
        {
            string reason = $"32-bit process has {totalReserved / (1024 * 1024):N0} MB reserved (>{thirtyTwoBitPressureThresholdBytes / (1024 * 1024):N0} MB threshold).";
            return (true, reason);
        }

        if (ratio > ratioHighPressureThreshold)
        {
            string reason = $"Reserved-to-committed ratio is {ratio:F1}x (>{ratioHighPressureThreshold:F0}x threshold). GC is holding large uncommitted reservations.";
            return (true, reason);
        }

        return (false, string.Empty);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
