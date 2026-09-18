using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>MemoryFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>MemoryPressureClassifier.ComputeMemoryPressureScore</c> (<c>MemoryAnalyzer</c>'s own
/// sole-owner classifier, per the Smell A audit) is <c>internal</c> to
/// <c>DumpDetective.Plugins.Memory</c>, invisible to <c>DumpDetective.Reporting</c> — its formula is
/// duplicated verbatim below, the same "re-derive rather than reach into another assembly's
/// internals" choice <c>AllocationPatternSynthesisRule</c> made for its own classifier. The finding
/// generator only ever calls the composite score, never the four individual component scores or
/// <c>ClassifyPressureBand</c>, so only the composite formula is ported.
/// <para>
/// <c>TopTypes</c> (the full, unbounded per-type population) is not observed per-entry — only the
/// single top-1 type's identity (name + bytes) the dominant-type finding reads, carried on
/// <c>heap.memory-composition</c> itself via <c>top1-type.bytes</c> and an
/// <see cref="EvidenceRef"/>.
/// </para>
/// </remarks>
internal sealed class MemorySynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Memory Analysis";

    public string RuleId => "memory.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.memory-composition"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = null;
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "heap.memory-composition")
            {
                composition = observation;
                break;
            }
        }

        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        ulong totalBytes = (ulong)composition.Measures["total.bytes"].Value;
        ulong lohBytes = (ulong)composition.Measures["loh.bytes"].Value;
        double lohPercent = composition.Measures["loh.percent"].Value;
        long lohObjects = (long)composition.Measures["loh.count"].Value;
        int uniqueTypes = (int)composition.Measures["unique-types.count"].Value;
        double top1Percent = composition.Measures["top1.percent"].Value;
        double top5Percent = composition.Measures["top5.percent"].Value;
        double smallObjectCountPercent = composition.Measures["small-object.count-percent"].Value;
        double smallObjectBytesPercent = composition.Measures["small-object.bytes-percent"].Value;
        double objectsPerMb = composition.Measures["objects-per-mb"].Value;

        double memoryPressureScore = ComputeMemoryPressureScore(lohPercent, top5Percent, smallObjectCountPercent, smallObjectBytesPercent, objectsPerMb);

        var findings = new List<Finding>();

        // ── Finding 1: High memory pressure score (>= 70) ───────────────────────────
        if (memoryPressureScore >= 70)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("pressure-score"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "High memory pressure score",
                Narrative = $"Memory pressure score: {memoryPressureScore:F1}/100. Total heap: {FormatBytes(totalBytes)}; LOH share: {lohPercent:F1}%; top-5 concentration: {top5Percent:F1}%.",
                Recommendation = "Heap shows elevated memory pressure from multiple dimensions. Review dominant type retention patterns and consider heap size baseline.",
                Tags = ["memory", "heap", "pressure"],
                MetricValue = memoryPressureScore,
                MetricUnit = "pressure score",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Finding 2: High type concentration (top-5 > 80%) ────────────────────────
        if (top5Percent > 80)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("concentration"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "High memory concentration in few types",
                Narrative = $"Top-5 types consume {top5Percent:F1}% of heap ({FormatBytes(totalBytes * (ulong)(top5Percent / 100))}). Top type alone: {top1Percent:F1}%.",
                Recommendation = "Heap memory is concentrated in few types. Verify whether top allocators are short-lived or retained unnecessarily.",
                Tags = ["memory", "heap", "concentration"],
                MetricValue = top5Percent,
                MetricUnit = "% top-5",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Finding 2b: Single type dominates the heap (> 40% of total bytes) ───────
        if (top1Percent > 40 && composition.Evidence.Count > 0)
        {
            string dominantTypeName = composition.Evidence[0].Description ?? "";
            ulong dominantTypeBytes = composition.Measures.TryGetValue("top1-type.bytes", out Measure m) ? (ulong)m.Value : 0;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("dominant-type"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "A single type dominates the managed heap",
                Narrative = $"'{dominantTypeName}' alone accounts for {top1Percent:F1}% of total heap bytes " +
                            $"({FormatBytes(dominantTypeBytes)} of {FormatBytes(totalBytes)}).",
                Recommendation = "A single dominant type is a common leak signature. Cross-check this type name against " +
                                  "the Dominator/GC Root analyzer findings to identify its retaining reference chain.",
                Tags = ["memory", "heap", "concentration", "dominant-type"],
                MetricValue = top1Percent,
                MetricUnit = "% top-1",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Finding 3: High small-object pressure (>85% by count) ──────────────────
        if (smallObjectCountPercent > 85)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("small-object-density"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "High small-object allocation density",
                Narrative = $"{smallObjectCountPercent:F1}% of heap objects are <85 bytes; these account for {smallObjectBytesPercent:F1}% of heap bytes.",
                Recommendation = "High small-object density may impact GC throughput. Review allocation hot paths and consider object pooling or region-based allocation.",
                Tags = ["memory", "gc", "allocation"],
                MetricValue = smallObjectCountPercent,
                MetricUnit = "% small objects",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Finding 4: High LOH percentage ──────────────────────────────────────────
        if (lohPercent >= 40)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("loh-usage"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Elevated Large Object Heap (LOH) usage",
                Narrative = $"LOH share: {lohPercent:F1}% ({FormatBytes(lohBytes)} of {FormatBytes(totalBytes)}); {lohObjects:N0} large objects.",
                Recommendation = "LOH share is elevated. Review large-object allocations, retention patterns, and LOH fragmentation risk.",
                Tags = ["memory", "heap", "loh"],
                MetricValue = lohPercent,
                MetricUnit = "% loh",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Baseline snapshot when nothing else triggered ───────────────────────────
        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("baseline-snapshot"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Managed heap memory snapshot",
                Narrative = $"Total heap: {FormatBytes(totalBytes)}; LOH: {lohPercent:F1}% ({FormatBytes(lohBytes)}); unique types: {uniqueTypes:N0}; pressure score: {memoryPressureScore:F1}/100.",
                Recommendation = "Heap composition appears within expected range for this snapshot.",
                Tags = ["memory", "heap"],
                MetricValue = memoryPressureScore,
                MetricUnit = "pressure score",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    // Duplicated from MemoryPressureClassifier (internal to a different assembly) — see remarks.
    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static double ComputeMemoryPressureScore(
        double lohPercent, double top5Percent, double smallObjectCountPercent, double smallObjectBytesPercent, double objectsPerMb) =>
        Math.Round(
            (Clamp01(lohPercent / 35.0) * 0.35
           + Clamp01(top5Percent / 70.0) * 0.30
           + Clamp01((0.7 * (smallObjectCountPercent / 85.0)) + (0.3 * (smallObjectBytesPercent / 45.0))) * 0.20
           + Clamp01(objectsPerMb / 12_000.0) * 0.15) * 100.0,
            1);

    private static string FormatBytes(ulong bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F2} GB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB"
        : bytes >= 1_024 ? $"{bytes / 1_024.0:F1} KB"
        : $"{bytes} B";

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
