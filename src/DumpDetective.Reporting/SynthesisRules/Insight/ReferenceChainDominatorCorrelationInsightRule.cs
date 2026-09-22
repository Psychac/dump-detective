using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectReferenceChainDominatorCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <para>
/// The original attached a <c>FindingEvidenceTable</c> listing every offending type (name,
/// retained bytes, % of heap). <c>Sdk.Synthesis.Finding</c> has no equivalent field, deliberately —
/// see docs/ReportStructure/ReportSystemVision.md § 6.2, which already decided not to pre-add
/// <c>Claim</c>-shaped/report-rendering fields to <c>Finding</c> ahead of the report phase, and
/// Appendix C, which lists <c>CompactTable</c> (what the old table became) as a shape the future
/// <c>dd-table</c> widget replaces — rendered live from cited observations, not carried as a blob
/// on the finding. Adding a table field to <c>Finding</c> now would mean building exactly the
/// structure that decision already deferred. So this rule keeps the headline (offender count, the
/// single worst offender's name/bytes/%) in <see cref="Finding.Narrative"/> — unchanged from the
/// original's own <c>Evidence</c> text — and drops the supplementary full-list table. Nothing is
/// lost from the observation layer: every offending type is still an individual
/// <c>reference-chain.type-sample</c> observation, all cited via <see cref="Finding.DerivedFrom"/>,
/// so a future <c>dd-table</c> can render the exact same ranked list directly from those
/// observations without this rule changing at all.
/// </para>
/// <para>
/// <c>type.retention-population</c> gained a <c>total.bytes</c> measure and
/// <c>reference-chain.type-sample</c> gained an optional <c>retained.bytes</c> measure specifically
/// for this rule (both added alongside the <c>EvidenceTables</c> investigation, phase 5 step 6.2) —
/// real, tested groundwork kept even though the rule itself stayed held until now.
/// </para>
/// </remarks>
internal sealed class ReferenceChainDominatorCorrelationInsightRule : ISynthesisRule
{
    private const double WarningPct = 10.0;
    private const double CriticalPct = 25.0;

    public string RuleId => "insight.reference-chain-dominator-correlation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.retention-population", "reference-chain.type-sample"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? retention = null;
        var typeSamples = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "type.retention-population")
                retention = observation;
            else if (observation.ObservationType == "reference-chain.type-sample")
                typeSamples.Add(observation);
        }

        ulong totalHeapBytes = (ulong)(retention?.Measures["total.bytes"].Value ?? 0);
        if (retention is null || totalHeapBytes == 0 || typeSamples.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var offenders = new List<(string TypeName, ulong RetainedBytes, double Pct, ObservationId Id)>();
        bool anyCritical = false;

        foreach (Observation sample in typeSamples)
        {
            if (!sample.Measures.TryGetValue("retained.bytes", out Measure retainedBytesMeasure))
                continue;

            ulong retainedBytes = (ulong)retainedBytesMeasure.Value;
            double pct = retainedBytes * 100.0 / totalHeapBytes;
            if (pct < WarningPct)
                continue;

            string typeName = ((TypeRef)sample.Subjects[0]).CanonicalName;
            offenders.Add((typeName, retainedBytes, pct, sample.Id));
            anyCritical |= pct >= CriticalPct;
        }

        if (offenders.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        offenders.Sort(static (a, b) => b.Pct.CompareTo(a.Pct));
        (string TypeName, ulong RetainedBytes, double Pct, ObservationId Id) top = offenders[0];

        var derivedFrom = new List<ObservationId> { retention.Id };
        foreach (var offender in offenders)
            derivedFrom.Add(offender.Id);

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::reference-chain-dominator-correlation",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = anyCritical ? Severity.Critical : Severity.Warning,
                Title = "Reference-chain type dominates a large heap fraction",
                Narrative = $"{offenders.Count:N0} traced type(s) retain ≥ {WarningPct:F0}% of the heap. " +
                            $"Largest: {top.TypeName} retains {InsightByteFormat.FormatBytes(top.RetainedBytes)} " +
                            $"({top.Pct:F1}% of {InsightByteFormat.FormatBytes(totalHeapBytes)} total heap).",
                Recommendation = "A single type's representative instance dominating this much of the heap indicates a small " +
                                 "number of objects (often a cache, collection, or singleton) are pinning a disproportionate " +
                                 "amount of memory. Inspect the reference-chain root path for these types to identify the owner.",
                Tags = ["reference-chain", "dominator", "memory-leak", "cross-analyzer"],
                MetricValue = top.Pct,
                MetricUnit = "% of heap",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
