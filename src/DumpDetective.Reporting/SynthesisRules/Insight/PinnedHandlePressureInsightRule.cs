using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectPinnedHandlePressure</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class PinnedHandlePressureInsightRule : ISynthesisRule
{
    private const int WarningCount = 100;
    private const double FragWarningPct = 30.0;

    public string RuleId => "insight.pinned-handle-pressure";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["handle.population", "heap.loh-fragmentation-summary"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? handles = null, lohFrag = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "handle.population": handles = observation; break;
                case "heap.loh-fragmentation-summary": lohFrag = observation; break;
            }
        }

        if (handles is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long pinnedHandleTargets = (long)handles.Measures["pinned-target.count"].Value;
        if (pinnedHandleTargets < WarningCount)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        double fragmentationPercent = lohFrag?.Measures["fragmentation.percent"].Value ?? 0.0;
        bool correlatedWithFrag = lohFrag is not null && fragmentationPercent >= FragWarningPct;
        Severity severity = correlatedWithFrag ? Severity.Critical : Severity.Warning;

        string correlation = correlatedWithFrag
            ? $" LOH fragmentation is {fragmentationPercent:F1}%, which may be caused by pinning."
            : string.Empty;

        var derivedFrom = new List<ObservationId> { handles.Id };
        if (lohFrag is not null) derivedFrom.Add(lohFrag.Id);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::pinned-handle-pressure",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(pinnedHandleTargets),
                    Reference = ClaimParts.Budget(ClaimParts.Count(WarningCount), "warning threshold"),
                    Qualifiers = new AssertionQualifiers
                    {
                        Location = "pinned GC handle targets",
                        Mechanism = correlatedWithFrag ? $"LOH fragmentation of {fragmentationPercent:F1}%" : null,
                    },
                },
                Support =
                [
                    new EvidenceStatement([handles.Id], $"{pinnedHandleTargets:N0} pinned handle targets exist."),
                    .. lohFrag is not null
                        ? [new EvidenceStatement([lohFrag.Id], $"LOH fragmentation is {fragmentationPercent:F1}%, read alongside the pin count.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = correlatedWithFrag
                    ? Counter.Of(new EvidenceStatement([lohFrag!.Id], "Fragmentation can come from short-lived large allocations as well as pinning, so the two co-occurring does not show pinning is the cause."))
                    : Counter.None("Handles pinned for short-lived I/O come and go; one snapshot does not show they persist."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type Pinned",
                        Test = $"If !gchandles lists far fewer pinned targets than {pinnedHandleTargets:N0}, the count is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the pinned targets are different ones, they are in-flight buffers.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "High number of pinned GC handles detected",
                Narrative = $"{pinnedHandleTargets:N0} pinned handles found.{correlation}",
                Recommendation = "Excessive pinning prevents GC from compacting memory, increasing fragmentation. " +
                                 "Use Memory<T>/Span<T> with MemoryHandle or GCHandle.Alloc(pin=false) where possible. " +
                                 "Ensure pinned buffers are released promptly.",
                Tags = ["pinning", "gc", "fragmentation", "handles"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
