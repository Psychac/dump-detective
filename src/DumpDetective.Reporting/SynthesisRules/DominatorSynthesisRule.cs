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
/// The highly-referenced-objects finding's confidence comes from the top highly-referenced
/// object's sampled GC root path (<see cref="RootPathEvidenceStrength"/>), carried as raw facts on
/// <c>type.retention-population</c> — the same object the old generator's
/// <c>EvidenceConfidence.Compute</c> read.
/// </para>
/// </remarks>
internal sealed class DominatorSynthesisRule : ISynthesisRule
{
    private const ulong TopSuspectCriticalBytes = 500UL * 1024 * 1024;
    private const ulong TopSuspectWarningBytes = 100UL * 1024 * 1024;
    private const int HighlyReferencedCriticalThreshold = 10;
    private const long TypeHotspotWarningThreshold = 1_000;
    private const long TypeHotspotCriticalThreshold = 10_000;

    private const double LargeExternalGraphRatio = 3.0;
    private const double SomeExternalReferencesRatio = 1.2;
    private const string AnalyzerName = "Dominator Analysis";

    public string RuleId => "dominator.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.retention", "type.retention-population", "type.retention-hotspot"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? topRetention = null;
        Observation? population = null;
        Observation? topHotspot = null;

        Observation? topLongLived = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.retention":
                    topRetention ??= observation;
                    if (IsMoreLongLived(observation, topLongLived))
                        topLongLived = observation;
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

        var findings = new List<Claim>(4);

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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("top-dominator-type"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = severity,
                Title = $"Dominant retention suspect: {typeName}",
                Narrative = $"{typeName} estimates {FormatHelper.FormatBytes(estimatedRetainedBytes)} retained across {instanceCount:N0} instances."
                    + DescribeRetentionShape(topLongLived),
                Recommendation = "Use the retention section and root paths to confirm why this type remains live.",
                Tags = ["dominator", "retained-bytes", typeName],
                MetricValue = estimatedRetainedBytes,
                MetricUnit = "bytes",
                DerivedFrom = [topRetention.Id],
                Confidence = EstimatedRetainedBytesConfidence,
            });
        }

        // ── Highly referenced objects ────────────────────────────────────────────
        if (population is not null)
        {
            long highlyReferencedCount = (long)population.Measures["highly-referenced.count"].Value;

            if (highlyReferencedCount > 0)
            {
                findings.Add(new Claim
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
                    Confidence = WithReferenceCountingLimits(RootPathEvidenceStrength.FromMeasures(population.Measures, "top-highly-referenced."), population),
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

                findings.Add(new Claim
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
                    Confidence = WithReferenceCountingLimits(ConfidenceBreakdown.MeasurementCertain, population),
                });
            }
        }

        // ── Reference tracking capped ────────────────────────────────────────────
        if (population is not null)
        {
            long approximatedReferenceAddresses = (long)population.Measures["approximated-reference-addresses.count"].Value;

            if (approximatedReferenceAddresses > 0)
            {
                findings.Add(new Claim
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    // The Gen2/LOH type the section's long-lived table leads with: most Gen2 instances, then most
    // LOH bytes; ties keep emission order (TopDominatorTypes order).
    // The top-dominator finding always reads estimated-retained.bytes, the bounded-BFS estimate,
    // even when an exact dominator-tree value exists — so its figure is an estimate either way.
    private static readonly ConfidenceBreakdown EstimatedRetainedBytesConfidence = ConfidenceBreakdown.MeasurementCertain with
    {
        Composite = 0.7,
        EvidenceStrength = 0.7,
        LimitingFactors = ["Retained bytes are bounded BFS estimates, not a true Lengauer-Tarjan dominator tree."],
    };

    // Incoming-reference counts back the highly-referenced and hotspot findings, so every way the
    // counting pass was bounded lowers those two, and only those two.
    private static ConfidenceBreakdown WithReferenceCountingLimits(ConfidenceBreakdown confidence, Observation? population)
    {
        if (population is null)
            return confidence;

        long approximated = (long)population.Measures["approximated-reference-addresses.count"].Value;
        return DataQualityPenalties.Apply(confidence,
            new DataQualityPenalty(population.Measures["is-object-scan-capped"].Value != 0, 0.15, "Object scan was capped; retention counts may be partial."),
            new DataQualityPenalty(population.Measures["is-reference-counting-skipped"].Value != 0, 0.20, "Reference counting was skipped; results are estimated."),
            new DataQualityPenalty(approximated > 0, 0.10, $"{approximated:N0} reference addresses have approximated (bounded-error) counts."));
    }

    private static bool IsMoreLongLived(Observation candidate, Observation? current)
    {
        double gen2 = candidate.Measures["gen2.count"].Value;
        double loh = candidate.Measures["loh.bytes"].Value;
        if (gen2 <= 0 && loh <= 0)
            return false;
        if (current is null)
            return true;

        double currentGen2 = current.Measures["gen2.count"].Value;
        return gen2 > currentGen2 || (gen2 == currentGen2 && loh > current.Measures["loh.bytes"].Value);
    }

    // Whether the top long-lived type mostly holds other objects alive or mostly is its own bytes.
    private static string DescribeRetentionShape(Observation? topLongLived)
    {
        if (topLongLived is null)
            return string.Empty;

        string typeName = ((TypeRef)topLongLived.Subjects[0]).CanonicalName;
        ulong retained = (ulong)topLongLived.Measures["retained.bytes"].Value;
        ulong shallow = (ulong)topLongLived.Measures["shallow.bytes"].Value;
        double ratio = shallow == 0 ? 0.0 : (double)retained / shallow;
        string ratioText = shallow == 0 ? "—" : $"{ratio:F2}x";

        string shape = ratio >= LargeExternalGraphRatio
            ? $"retained ≫ shallow ({ratioText}) — holds a large external graph."
            : ratio >= SomeExternalReferencesRatio
                ? $"retained > shallow ({ratioText}) — holds some external references."
                : $"retained ≈ shallow ({ratioText}) — largely self-contained.";
        return $" Top long-lived (Gen2/LOH) type {typeName}: {shape}";
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
