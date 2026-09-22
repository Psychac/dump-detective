using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectAddressSpacePressure</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Re-derives <c>AddressSpacePressureRisk</c>/<c>PressureRiskReason</c> from raw measures rather
/// than reading a stored classification/narrative — the same choice
/// <c>SegmentReservationSynthesisRule</c> already made (<c>EvaluatePressureRisk</c> below is its
/// formula, duplicated verbatim, same as every other classifier re-derivation in this batch).
/// </remarks>
internal sealed class AddressSpacePressureInsightRule : ISynthesisRule
{
    private const double EphemeralFillCriticalPct = 90.0;

    public string RuleId => "insight.address-space-pressure";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.segment-reservation", "heap.topology-population"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? segReservation = null, segments = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "heap.segment-reservation": segReservation = observation; break;
                case "heap.topology-population": segments = observation; break;
            }
        }

        if (segReservation is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var findings = new List<Finding>();

        int dumpPointerSize = (int)segReservation.Measures["dump-pointer-size"].Value;
        ulong totalReserved = (ulong)segReservation.Measures["reserved.bytes"].Value;
        double ratio = segReservation.Measures["reserved-to-committed.ratio"].Value;
        double ratioHighThreshold = segReservation.Measures["ratio-high-pressure.threshold"].Value;
        ulong thirtyTwoBitThresholdBytes = (ulong)segReservation.Measures["thirty-two-bit-pressure.threshold-bytes"].Value;

        (bool pressureRisk, string pressureReason) = EvaluatePressureRisk(
            dumpPointerSize, totalReserved, ratio, ratioHighThreshold, thirtyTwoBitThresholdBytes);

        if (pressureRisk)
        {
            ulong totalCommitted = (ulong)segReservation.Measures["committed.bytes"].Value;

            findings.Add(new Finding
            {
                Fingerprint = "insightengine::address-space-pressure",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Managed heap virtual address space pressure",
                Narrative = $"Reserved: {InsightByteFormat.FormatBytes(totalReserved)}, " +
                            $"committed: {InsightByteFormat.FormatBytes(totalCommitted)}, " +
                            $"ratio: {ratio:F1}×. " +
                            $"Reason: {pressureReason}.",
                Recommendation = "On 32-bit processes, reserved memory > 1.5 GB risks address space exhaustion. " +
                                 "Consider migrating to 64-bit, enabling Server GC, or reducing the number of " +
                                 "GC segments via heap hard limit configuration.",
                Tags = ["segment", "address-space", "virtual-memory"],
                MetricValue = (double)totalReserved,
                MetricUnit = "bytes",
                DerivedFrom = [segReservation.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        long ephemeralSegmentCount = (long)segReservation.Measures["ephemeral.segment-count"].Value;
        double avgEphemeralFillPct = segReservation.Measures["ephemeral.avg-fill-percent"].Value;

        if (ephemeralSegmentCount > 0 && avgEphemeralFillPct >= EphemeralFillCriticalPct)
        {
            long totalSegments = (long)(segments?.Measures["segment.count"].Value ?? 0);
            string segNote = segments is not null ? $" {totalSegments} total segments across the heap." : string.Empty;

            var derivedFrom = segments is not null ? new[] { segReservation.Id, segments.Id } : [segReservation.Id];

            findings.Add(new Finding
            {
                Fingerprint = "insightengine::address-space-ephemeral-fill",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Ephemeral GC segments critically full",
                Narrative = $"Average ephemeral segment fill: {avgEphemeralFillPct:F1}% " +
                            $"across {ephemeralSegmentCount} ephemeral segment(s).{segNote} " +
                            "When ephemeral segments are nearly full, GC must commit new segments or trigger full compaction.",
                Recommendation = "Reduce Gen0/Gen1 object survival rates. " +
                                 "Review long-lived objects promoted from Gen1 to Gen2 — " +
                                 "they fragment ephemeral segments and force premature full GC.",
                Tags = ["segment", "ephemeral", "gc-pressure"],
                MetricValue = avgEphemeralFillPct,
                MetricUnit = "% full",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    // Duplicated verbatim from SegmentReservationAnalyzer.Analyze's §25.3 address space pressure
    // evaluation — same as SegmentReservationSynthesisRule's own copy.
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
            string reason = $"Reserved-to-committed ratio {ratio:F1}x (>{ratioHighPressureThreshold:F0}x threshold). GC is holding large uncommitted reservations.";
            return (true, reason);
        }

        return (false, string.Empty);
    }
}
