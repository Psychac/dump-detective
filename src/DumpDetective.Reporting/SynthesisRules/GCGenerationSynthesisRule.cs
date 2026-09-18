using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>GCGenerationFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>gc.generation-top-allocator</c>/<c>gc.generation-top-accumulator</c> already carry the
/// analyzer's own final selection (≤3 types, framework-namespace-deprioritized) — the rule only
/// formats and joins them, trusting emission order, the same already-sorted-before-emission
/// structure <c>ArraySynthesisRule</c> relies on. It never sees the full, unbounded
/// <c>PerTypeGenerationProfiles</c> population the old generator's own selection logic
/// (<c>BuildTopTypeEvidence</c>/<c>IsFrameworkType</c>) ran over — that logic was ported into the
/// analyzer instead (<c>GCGenerationAnalyzer.SelectTopTypes</c>), specifically so the selection, not
/// just the formatting, stays out of this rule.
/// <para>
/// <c>FallbackMode</c>/<c>GenBytesAreApproximate</c> are read from
/// <see cref="Provenance.Fidelity"/>/<see cref="Observation.Confidence"/> rather than dedicated
/// measures — <c>gc.generation-composition</c>'s own emission already encodes them there.
/// </para>
/// <para>
/// <c>FormatBytes</c> is duplicated from the old generator verbatim (GB/MB at two decimal places,
/// KB at one) rather than reusing <c>FormatHelper.FormatBytes</c> — a different formatter with
/// different precision; swapping it would be a silent text change, not a refactor.
/// </para>
/// </remarks>
internal sealed class GCGenerationSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "GC Generation Analysis";

    public string RuleId => "gc-generation.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["gc.generation-composition", "gc.generation-top-allocator", "gc.generation-top-accumulator"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? composition = null;
        var topAllocators = new List<Observation>();
        var topAccumulators = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "gc.generation-composition":
                    composition = observation;
                    break;
                case "gc.generation-top-allocator":
                    topAllocators.Add(observation);
                    break;
                case "gc.generation-top-accumulator":
                    topAccumulators.Add(observation);
                    break;
            }
        }

        if (composition is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long totalObjects = (long)composition.Measures["total.count"].Value;
        long pohObjects = (long)composition.Measures["poh.count"].Value;
        ulong pohBytes = (ulong)composition.Measures["poh.bytes"].Value;
        double pohThresholdPercent = composition.Measures["poh-pressure.threshold"].Value;
        double lohPercent = composition.Measures["loh.percent"].Value;
        double lohThresholdPercent = composition.Measures["loh-pressure.threshold"].Value;
        long gen0Objects = (long)composition.Measures["gen0.count"].Value;
        ulong gen0Bytes = (ulong)composition.Measures["gen0.bytes"].Value;
        double gen0PressureThresholdPercent = composition.Measures["gen0-pressure.threshold"].Value;
        double gen2Pct = composition.Measures["gen2.percent"].Value;
        long gen2Objects = (long)composition.Measures["gen2.count"].Value;
        ulong gen2Bytes = (ulong)composition.Measures["gen2.bytes"].Value;
        ulong gen1Bytes = (ulong)composition.Measures["gen1.bytes"].Value;
        ulong lohBytes = (ulong)composition.Measures["loh.bytes"].Value;
        long finalizableGen2Count = (long)composition.Measures["finalizable-gen2.count"].Value;
        ulong finalizableGen2Bytes = (ulong)composition.Measures["finalizable-gen2.bytes"].Value;
        bool fallbackMode = composition.Provenance.Fidelity == FidelityLevel.Degraded;
        bool genBytesAreApproximate = composition.Confidence < 1.0;

        var findings = new List<Finding>(4);

        // ── POH share finding ────────────────────────────────────────────────────
        double pohPct = totalObjects == 0 ? 0.0 : pohObjects * 100.0 / totalObjects;
        if (pohBytes > 0 && pohPct >= pohThresholdPercent)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("poh-share"),
                Analyzer = AnalyzerName,
                Category = "GC",
                Severity = pohPct >= 20 ? Severity.Warning : Severity.Info,
                Title = "Pinned Object Heap (POH) share",
                Narrative = $"POH holds {pohPct:F1}% of objects ({FormatBytes(pohBytes)}, {pohObjects:N0} objects).",
                Recommendation = pohPct >= 20
                    ? "Investigate sources of object pinning (interop buffers, sockets, Span<T>/Memory<T> usage). Excess pinning fragments the heap and can inflate GC pause times."
                    : "POH share is within expected range for this dump.",
                Tags = ["gc", "poh", "pinning"],
                MetricValue = pohPct,
                MetricUnit = "%",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── LOH share finding ────────────────────────────────────────────────────
        if (lohPercent >= lohThresholdPercent)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("loh-share"),
                Analyzer = AnalyzerName,
                Category = "GC",
                Severity = lohPercent >= 35 ? Severity.Warning : Severity.Info,
                Title = "GC generation footprint snapshot",
                Narrative = $"LOH memory share is {lohPercent:F1}% of managed heap.",
                Recommendation = lohPercent >= 35
                    ? "Inspect large object churn and promotion patterns."
                    : "Generation split appears within expected range for this dump.",
                Tags = ["gc", "generations", "loh"],
                MetricValue = lohPercent,
                MetricUnit = "%",
                DerivedFrom = [composition.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Gen0 allocation pressure finding ────────────────────────────────────
        double gen0Pct = totalObjects == 0 ? 0.0 : gen0Objects * 100.0 / totalObjects;
        if (gen0Pct >= gen0PressureThresholdPercent)
        {
            string topGen0Evidence = "";
            if (topAllocators.Count > 0)
            {
                string top = FormatTopTypes(topAllocators, static o =>
                    $"{((TypeRef)o.Subjects[0]).CanonicalName} ×{(long)o.Measures["gen0.count"].Value:N0}");
                topGen0Evidence = $" Top allocating types: {top}.";
            }

            var derivedFrom = new List<ObservationId>(1 + topAllocators.Count) { composition.Id };
            foreach (Observation o in topAllocators) derivedFrom.Add(o.Id);

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("gen0-pressure"),
                Analyzer = AnalyzerName,
                Category = "GC",
                Severity = Severity.Warning,
                Title = $"High Gen0 allocation pressure: {gen0Pct:F1}% of objects in Gen0",
                Narrative = $"Gen0: {gen0Objects:N0} objects, {FormatBytes(gen0Bytes)}. " +
                            $"Total objects: {totalObjects:N0}.{topGen0Evidence}",
                Recommendation = "Review allocation patterns and object lifetime. High Gen0 activity indicates frequent " +
                                  "short-lived object creation; consider object pooling, lazy initialization, or batch processing " +
                                  "to reduce allocation pressure and improve GC throughput.",
                Tags = ["gc", "gen0", "allocation-pressure", "throughput"],
                MetricValue = gen0Pct,
                MetricUnit = "%",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Gen2 pressure finding ────────────────────────────────────────────────
        if (gen2Pct >= 50.0)
        {
            Severity sev = gen2Pct >= 75.0 ? Severity.Critical : Severity.Warning;

            string topTypeEvidence = "";
            if (topAccumulators.Count > 0)
            {
                bool hasGen2Bytes = composition.Measures["has-gen2-bytes"].Value >= 1.0;
                string top = hasGen2Bytes
                    ? FormatTopTypes(topAccumulators, static o =>
                        $"{((TypeRef)o.Subjects[0]).CanonicalName} ({FormatBytes((ulong)o.Measures["gen2.bytes"].Value)})")
                    : FormatTopTypes(topAccumulators, static o =>
                        $"{((TypeRef)o.Subjects[0]).CanonicalName} ×{(long)o.Measures["gen2.count"].Value:N0}");
                topTypeEvidence = $" Top accumulating types: {top}.";
            }

            string qualityNote = fallbackMode
                ? " ⚠⚠ FALLBACK MODE: Gen0/Gen1 values unknown (set to 0). Gen2% is unreliable — this finding may be a false positive."
                : genBytesAreApproximate
                    ? " ⚠ Byte values are approximate and may not reflect exact per-generation distribution for high-variance types."
                    : "";

            string finalizableNote = finalizableGen2Count > 0
                ? $" {finalizableGen2Count:N0} of these objects ({FormatBytes(finalizableGen2Bytes)}) are of finalizable types — see Finalizable Object Analysis."
                : "";

            var derivedFrom = new List<ObservationId>(1 + topAccumulators.Count) { composition.Id };
            foreach (Observation o in topAccumulators) derivedFrom.Add(o.Id);

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("gen2-pressure"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = sev,
                Title = $"Gen2 holds {gen2Pct:F1}% of managed heap ({FormatBytes(gen2Bytes)})",
                Narrative = $"Gen2: {gen2Objects:N0} objects, {FormatBytes(gen2Bytes)}. " +
                            $"Gen0: {FormatBytes(gen0Bytes)} | Gen1: {FormatBytes(gen1Bytes)} | " +
                            $"LOH: {FormatBytes(lohBytes)}.{topTypeEvidence}{finalizableNote}{qualityNote}",
                Recommendation = "Run: memory-leak <dump> for full Gen2/LOH breakdown and GC root chains. " +
                                  "High Gen2 indicates chronic object promotion — review long-lived caches, " +
                                  "static collections, and event subscriptions that keep objects alive.",
                Tags = ["gc", "gen2", "memory-pressure", "promotion"],
                MetricValue = gen2Pct,
                MetricUnit = "%",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    /// <summary>Formats and joins already-selected, already-ordered observations with "; " — the
    /// selection itself happened analyzer-side (<c>GCGenerationAnalyzer.SelectTopTypes</c>).</summary>
    private static string FormatTopTypes(IReadOnlyList<Observation> types, Func<Observation, string> formatEntry)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < types.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(formatEntry(types[i]));
        }
        return sb.ToString();
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024UL) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
