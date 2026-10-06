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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

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

        var findings = new List<Claim>(4);

        // ── POH share finding ────────────────────────────────────────────────────
        double pohPct = totalObjects == 0 ? 0.0 : pohObjects * 100.0 / totalObjects;
        if (pohBytes > 0 && pohPct >= pohThresholdPercent)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("poh-share"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(pohPct),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(pohThresholdPercent), "POH share threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the Pinned Object Heap", Mechanism = "pinned objects" },
                },
                Support = [new EvidenceStatement([composition.Id], $"The POH holds {pohObjects:N0} of {totalObjects:N0} objects, {FormatBytes(pohBytes)}.")],
                Counter = Counter.None("Objects pinned for short I/O leave the POH quickly; one snapshot does not show they persist."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If the POH size in !eeheap -gc is far from {FormatBytes(pohBytes)}, the POH share is wrong.",
                    },
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("loh-share"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(lohPercent),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(lohThresholdPercent), "LOH share threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the Large Object Heap" },
                },
                Support = [new EvidenceStatement([composition.Id], $"The LOH holds {FormatBytes(lohBytes)}, {lohPercent:F1}% of the managed heap.")],
                Counter = Counter.None("Large objects that die young cost fragmentation, not growth; their lifetime is not observed."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If the LOH size in !eeheap -gc is far from {FormatBytes(lohBytes)}, the LOH share is wrong.",
                    },
                ],
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("gen0-pressure"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(gen0Pct),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(gen0PressureThresholdPercent), "Gen0 pressure threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "objects in Generation 0" },
                },
                Support =
                [
                    new EvidenceStatement([composition.Id], $"Gen0 holds {gen0Objects:N0} of {totalObjects:N0} objects, {FormatBytes(gen0Bytes)}."),
                    .. topAllocators.Count > 0
                        ? [new EvidenceStatement(ClaimParts.IdsOf(topAllocators), "Each is one of the types allocating most of Gen0.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("A high Gen0 share is normal for request-processing code; Gen0 is cheap to collect, and survival is not measured here."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If Gen0 in !eeheap -gc holds far fewer than the {gen0Objects:N0} objects counted, the share is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if Gen0 is a small share after a collection, the first reading was the point before a GC.",
                    },
                ],
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("gen2-pressure"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(gen2Pct),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Percent(sev == Severity.Critical ? 75.0 : 50.0),
                        sev == Severity.Critical ? "critical Gen2 threshold" : "warning Gen2 threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "objects in Generation 2", Mechanism = "promotion of long-lived objects" },
                },
                Support =
                [
                    new EvidenceStatement([composition.Id], $"Gen2 holds {gen2Objects:N0} of {totalObjects:N0} objects, {FormatBytes(gen2Bytes)}."),
                    .. topAccumulators.Count > 0
                        ? [new EvidenceStatement(ClaimParts.IdsOf(topAccumulators), "Each is one of the types accumulating most in Gen2.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = fallbackMode
                    ? Counter.Of(new EvidenceStatement([composition.Id], "Gen0 and Gen1 are unknown in fallback mode, so the Gen2 percentage is unreliable and may be a false positive."))
                    : genBytesAreApproximate
                        ? Counter.Of(new EvidenceStatement([composition.Id], "Byte values are approximate, so the per-generation split may not be exact."))
                        : Counter.None("A large Gen2 share is normal for a service holding a warm cache; the share alone does not show it is growing."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If Gen2 in !eeheap -gc is far from {FormatBytes(gen2Bytes)}, the Gen2 share is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if Gen2 does not grow, it is a stable working set, not accumulation.",
                    },
                ],
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
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
