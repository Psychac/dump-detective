using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>DominatorFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Trusts <c>type.retention</c> emission order for the top-dominator-type finding: the real
/// analyzer's <c>TopDominatorTypes</c> is already sorted descending by
/// <c>TypeSnapshot.EstimatedRetainedBytes</c> before <c>EmitRetentionObservationsAsync</c>'s
/// emission loop runs over it — the same already-sorted-before-emission structure
/// <c>ArraySynthesisRule</c> relies on. Reads <c>estimated-retained.bytes</c> (always the heuristic
/// BFS estimate), not <c>retained.bytes</c> (exact when the §D9 overlay succeeded) — the old
/// generator's top-suspect finding reads <c>TypeSnapshot.EstimatedRetainedBytes</c> unconditionally.
/// <para>
/// Unlike <c>type.retention</c>'s emission order, <c>type.retention-hotspot</c> observations
/// (backing the type-hotspot finding) cannot be trusted by order: the real analyzer's
/// <c>TopRetentionTypes</c> is sorted by estimated-retained bytes, not by incoming-reference count,
/// so this rule scans every matched instance for the highest <c>total-incoming-references.count</c>
/// — the same "old generator's own scan, not the emission order" situation
/// <c>AsyncStateMachineSynthesisRule</c> hit for its per-type case.
/// </para>
/// <para>
/// The old generator's second finding (highly-referenced objects) also computes an evidence-based
/// <c>Finding.ConfidenceScore</c> override via <c>EvidenceConfidence.Compute</c> — not read by any
/// Title/Narrative/Recommendation/Tags/MetricValue/MetricUnit text, and, like every prior rule in
/// this playbook (<c>TimerLeakSynthesisRule</c>'s own notable-findings entry), left as
/// <see cref="ConfidenceBreakdown.MeasurementCertain"/> rather than reconstructed — no observation
/// exists solely to feed a value this pass doesn't reproduce for any other rule either.
/// </para>
/// </remarks>
internal sealed class DominatorSynthesisRule : ISynthesisRule
{
    private const ulong TopSuspectCriticalBytes = 500UL * 1024 * 1024;
    private const ulong TopSuspectWarningBytes = 100UL * 1024 * 1024;
    private const int HighlyReferencedCriticalThreshold = 10;
    private const long TypeHotspotWarningThreshold = 1_000;
    private const long TypeHotspotCriticalThreshold = 10_000;

    private const string AnalyzerName = "Dominator Analysis";

    public string RuleId => "dominator.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.retention", "type.retention-population", "type.retention-hotspot"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? topRetention = null;
        Observation? population = null;
        Observation? topHotspot = null;

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.retention":
                    topRetention ??= observation;
                    break;
                case "type.retention-population":
                    population = observation;
                    break;
                case "type.retention-hotspot":
                    if (topHotspot is null
                        || observation.Measures["total-incoming-references.count"].Value
                            > topHotspot.Measures["total-incoming-references.count"].Value)
                        topHotspot = observation;
                    break;
            }
        }

        var findings = new List<Finding>(4);

        // ── Top dominator type ──────────────────────────────────────────────────
        if (topRetention is not null)
        {
            ulong estimatedRetainedBytes = (ulong)topRetention.Measures["estimated-retained.bytes"].Value;
            long instanceCount = (long)topRetention.Measures["instance.count"].Value;
            string typeName = ((TypeRef)topRetention.Subjects[0]).CanonicalName;

            Severity severity = estimatedRetainedBytes >= TopSuspectCriticalBytes
                ? Severity.Critical
                : estimatedRetainedBytes >= TopSuspectWarningBytes
                    ? Severity.Warning
                    : Severity.Info;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("top-dominator-type"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = severity,
                Title = $"Dominant retention suspect: {typeName}",
                Narrative = $"{typeName} estimates {FormatHelper.FormatBytes(estimatedRetainedBytes)} retained across {instanceCount:N0} instances.",
                Recommendation = "Use the retention section and root paths to confirm why this type remains live.",
                Tags = ["dominator", "retained-bytes", typeName],
                MetricValue = estimatedRetainedBytes,
                MetricUnit = "bytes",
                DerivedFrom = [topRetention.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Highly referenced objects ────────────────────────────────────────────
        if (population is not null)
        {
            long highlyReferencedCount = (long)population.Measures["highly-referenced.count"].Value;

            if (highlyReferencedCount > 0)
            {
                findings.Add(new Finding
                {
                    Fingerprint = Fingerprint("highly-referenced-objects"),
                    Analyzer = AnalyzerName,
                    Category = "Retention",
                    Severity = highlyReferencedCount >= HighlyReferencedCriticalThreshold ? Severity.Critical : Severity.Warning,
                    Title = "Highly referenced objects indicate retention hotspots",
                    Narrative = $"{highlyReferencedCount:N0} objects with abnormally high incoming reference counts were detected.",
                    Recommendation = "Inspect root paths and long-lived graphs retaining these objects.",
                    Tags = ["retention", "references", "memory-leak"],
                    MetricValue = highlyReferencedCount,
                    MetricUnit = "objects",
                    DerivedFrom = [population.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── Dominant retained type hotspot ───────────────────────────────────────
        if (topHotspot is not null)
        {
            long totalIncomingReferences = (long)topHotspot.Measures["total-incoming-references.count"].Value;

            if (totalIncomingReferences >= TypeHotspotWarningThreshold)
            {
                long objectCount = (long)topHotspot.Measures["object.count"].Value;
                ulong totalBytes = (ulong)topHotspot.Measures["total.bytes"].Value;
                string typeName = ((TypeRef)topHotspot.Subjects[0]).CanonicalName;

                findings.Add(new Finding
                {
                    Fingerprint = Fingerprint("dominant-retained-type-hotspot"),
                    Analyzer = AnalyzerName,
                    Category = "Retention",
                    Severity = totalIncomingReferences >= TypeHotspotCriticalThreshold ? Severity.Critical : Severity.Warning,
                    Title = "Dominant retained type hotspot detected",
                    Narrative = $"Type '{typeName}' contributes {totalIncomingReferences:N0} incoming references across " +
                                $"{objectCount:N0} top hotspot object(s), footprint {FormatHelper.FormatBytes(totalBytes)}.",
                    Recommendation = "Inspect this type's root paths and owners, then reduce long-lived references or partition caches by scope.",
                    Tags = ["retention", "type-hotspot", "references"],
                    MetricValue = totalIncomingReferences,
                    MetricUnit = "references",
                    DerivedFrom = [topHotspot.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── Reference tracking capped ────────────────────────────────────────────
        if (population is not null)
        {
            long approximatedReferenceAddresses = (long)population.Measures["approximated-reference-addresses.count"].Value;

            if (approximatedReferenceAddresses > 0)
            {
                findings.Add(new Finding
                {
                    Fingerprint = Fingerprint("reference-tracking-capped"),
                    Analyzer = AnalyzerName,
                    Category = "Diagnostics",
                    Severity = Severity.Info,
                    Title = "Reference tracking was capped",
                    Narrative = $"{approximatedReferenceAddresses:N0} references have approximated, bounded-error counts because the " +
                                "tracking limit was reached (Space-Saving admission — never dropped entirely).",
                    Recommendation = "Increase MaxReferenceAddresses for exact incoming-reference coverage on more addresses.",
                    Tags = ["analysis-quality", "references"],
                    MetricValue = approximatedReferenceAddresses,
                    MetricUnit = "references",
                    DerivedFrom = [population.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
