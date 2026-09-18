using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>GCHandleFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike every prior rule in this playbook, the old generator's thresholds are not private
/// consts — they're read from <c>GCHandleDomainResult</c>'s own fields, sourced from
/// <c>GCHandleAnalysisOptions</c> (runtime-configurable via <c>AnalysisContext.AnalyzerOptions</c>,
/// not a fixed default). Hard-coding the defaults here the way other rules hard-code their
/// generator's consts would silently drop that configurability, so this rule reads every
/// threshold from <c>handle.population</c>'s own measures instead.
/// </remarks>
internal sealed class GCHandleSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "GC Handle Analysis";

    public string RuleId => "gc-handle.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["handle.population", "handle.refcounted-target"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        Observation? topRefCountedType = null;
        var refCountedTypeIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "handle.population")
            {
                population = observation;
            }
            else if (observation.ObservationType == "handle.refcounted-target")
            {
                refCountedTypeIds.Add(observation.Id);
                if (topRefCountedType is null ||
                    observation.Measures["instance.count"].Value > topRefCountedType.Measures["instance.count"].Value)
                    topRefCountedType = observation;
            }
        }

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        IReadOnlyDictionary<string, Measure> m = population.Measures;
        long totalHandles = (long)m["total.count"].Value;
        long pinnedHandleTargets = (long)m["pinned-target.count"].Value;
        ulong pinnedRetainedBytes = (ulong)m["pinned-retained.bytes"].Value;
        long pinnedSohCount = (long)m["pinned-soh.count"].Value;
        long asyncPinnedSohCount = (long)m["async-pinned-soh.count"].Value;
        long pinnedNonSohCount = (long)m["pinned-non-soh.count"].Value;
        long asyncPinnedNonSohCount = (long)m["async-pinned-non-soh.count"].Value;
        long refCountedCount = (long)m["refcounted.count"].Value;
        long weakShortGen0 = (long)m["weakshort-gen0.count"].Value;
        long weakShortGen1 = (long)m["weakshort-gen1.count"].Value;
        long weakShortGen2 = (long)m["weakshort-gen2.count"].Value;
        long weakShortLoh = (long)m["weakshort-loh.count"].Value;
        long weakLongGen0 = (long)m["weaklong-gen0.count"].Value;
        long weakLongGen1 = (long)m["weaklong-gen1.count"].Value;
        long weakLongGen2 = (long)m["weaklong-gen2.count"].Value;
        long weakLongLoh = (long)m["weaklong-loh.count"].Value;
        long dependentCount = (long)m["dependent.count"].Value;
        long dependentResolvedEdges = (long)m["dependent-resolved-edge.count"].Value;
        long dependentUnresolvedTargets = (long)m["dependent-unresolved-target.count"].Value;

        long totalHandlesWarningThreshold = (long)m["total-handles-warning.threshold"].Value;
        long pinnedHandleTargetsWarningThreshold = (long)m["pinned-handle-targets-warning.threshold"].Value;
        ulong pinnedRetainedBytesWarningThreshold = (ulong)m["pinned-retained-bytes-warning.threshold"].Value;
        long pinnedSohWarningThreshold = (long)m["pinned-soh-warning.threshold"].Value;
        long refCountedWarningThreshold = (long)m["refcounted-warning.threshold"].Value;
        double weakLongGen2FractionWarningThreshold = m["weaklong-gen2-fraction-warning.threshold"].Value;
        long weakLongGen2MinimumCountThreshold = (long)m["weaklong-gen2-minimum-count.threshold"].Value;
        double dependentUnresolvedPercentWarningThreshold = m["dependent-unresolved-percent-warning.threshold"].Value;

        var findings = new List<Finding>(capacity: 6);
        var populationIds = new List<ObservationId> { population.Id };

        // ── Summary — always emitted ────────────────────────────────────────────
        Severity severity = pinnedHandleTargets >= pinnedHandleTargetsWarningThreshold
            || totalHandles >= totalHandlesWarningThreshold
            ? Severity.Warning : Severity.Info;

        findings.Add(new Finding
        {
            Fingerprint = Fingerprint("summary"),
            Analyzer = AnalyzerName,
            Category = "GC",
            Severity = severity,
            Title = "GC handle pressure summary",
            Narrative = $"Total handles: {totalHandles:N0}; pinned-handle target count: {pinnedHandleTargets:N0}.",
            Recommendation = severity == Severity.Warning
                ? "Inspect pinned-handle-heavy types and reduce long-lived pinning where possible."
                : "Handle distribution appears within expected bounds for this snapshot.",
            Tags = ["gc-handle", "pinning", "retention"],
            MetricValue = totalHandles,
            MetricUnit = "total-handles",
            DerivedFrom = populationIds,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        // ── Pinned retained bytes — only above the warning threshold ────────────
        if (pinnedRetainedBytes > 0 && pinnedRetainedBytes >= pinnedRetainedBytesWarningThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("pinned-retained-bytes"),
                Analyzer = AnalyzerName,
                Category = "GC",
                Severity = Severity.Warning,
                Title = "High pinned retained bytes",
                Narrative = $"Total pinned retained bytes: {FormatBytes(pinnedRetainedBytes)}.",
                Recommendation = "Reduce pinned object lifetime or avoid pinning large objects to decrease heap fragmentation.",
                Tags = ["gc-handle", "pinning", "memory-pressure"],
                MetricValue = pinnedRetainedBytes,
                MetricUnit = "bytes",
                DerivedFrom = populationIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── SOH-pinned objects — only above the warning threshold ───────────────
        long sohPinnedTargets = pinnedSohCount + asyncPinnedSohCount;
        if (sohPinnedTargets > 0 && sohPinnedTargets >= pinnedSohWarningThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("soh-pinned"),
                Analyzer = AnalyzerName,
                Category = "GC",
                Severity = Severity.Warning,
                Title = "SOH-pinned objects blocking GC compaction",
                Narrative = $"Small-object-heap pinned targets: {sohPinnedTargets:N0} (Pinned: {pinnedSohCount:N0}, AsyncPinned: {asyncPinnedSohCount:N0}); " +
                            $"non-SOH pinned targets (LOH/POH/Frozen, no compaction impact): {pinnedNonSohCount + asyncPinnedNonSohCount:N0}.",
                Recommendation = "SOH-pinned objects block GC compaction of surrounding memory. Shorten pin lifetimes or move long-lived pinned buffers to pre-allocated/native memory where possible.",
                Tags = ["gc-handle", "pinning", "compaction", "soh"],
                MetricValue = sohPinnedTargets,
                MetricUnit = "soh-pinned-targets",
                DerivedFrom = populationIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── COM interop (RefCounted) — only above the warning threshold ─────────
        if (refCountedCount > 0 && refCountedCount >= refCountedWarningThreshold)
        {
            string topTypeText = topRefCountedType is not null
                ? $" Dominant type: {((TypeRef)topRefCountedType.Subjects[0]).CanonicalName} ({(long)topRefCountedType.Measures["instance.count"].Value:N0})."
                : string.Empty;

            var derivedFrom = new List<ObservationId>(populationIds);
            derivedFrom.AddRange(refCountedTypeIds);

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("refcounted-pressure"),
                Analyzer = AnalyzerName,
                Category = "GC",
                Severity = Severity.Warning,
                Title = "COM interop (RefCounted) handle pressure",
                Narrative = $"RefCounted handles: {refCountedCount:N0}.{topTypeText}",
                Recommendation = "RefCounted handles keep COM RCWs alive. Ensure COM objects are released (Marshal.ReleaseComObject / using) rather than left for finalization.",
                Tags = ["gc-handle", "com-interop", "refcounted"],
                MetricValue = refCountedCount,
                MetricUnit = "refcounted-handles",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── WeakLong handles concentrated in Gen2/LOH — only above thresholds ───
        long weakLongGen2PlusCount = weakLongGen2 + weakLongLoh;
        long weakLongResolvedCount = weakLongGen0 + weakLongGen1 + weakLongGen2PlusCount;
        if (weakLongGen2PlusCount >= weakLongGen2MinimumCountThreshold && weakLongResolvedCount > 0)
        {
            double weakLongGen2PlusFraction = weakLongGen2PlusCount * 100.0 / weakLongResolvedCount;
            if (weakLongGen2PlusFraction >= weakLongGen2FractionWarningThreshold)
            {
                long weakShortGen2PlusCount = weakShortGen2 + weakShortLoh;
                long weakShortResolvedCount = weakShortGen0 + weakShortGen1 + weakShortGen2PlusCount;

                findings.Add(new Finding
                {
                    Fingerprint = Fingerprint("weaklong-gen2-backlog"),
                    Analyzer = AnalyzerName,
                    Category = "GC",
                    Severity = Severity.Warning,
                    Title = "WeakLong handles concentrated in Gen2/LOH — possible finalization backlog",
                    Narrative = $"WeakLong targets in Gen2/LOH: {weakLongGen2PlusCount:N0} of {weakLongResolvedCount:N0} resolved ({weakLongGen2PlusFraction:F1}%). " +
                                $"WeakShort comparison: Gen2/LOH {weakShortGen2PlusCount:N0} of {weakShortResolvedCount:N0} resolved.",
                    Recommendation = "WeakLong clears only after finalization completes. A large Gen2/LOH-concentrated WeakLong population can indicate finalized objects lingering — cross-check with the Finalizable Object Analysis section for finalization queue depth.",
                    Tags = ["gc-handle", "weak-handle", "finalization"],
                    MetricValue = weakLongGen2PlusFraction,
                    MetricUnit = "% weaklong-gen2-plus",
                    DerivedFrom = populationIds,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── Dependent-handle retention summary — always emitted when any exist ──
        if (dependentCount > 0)
        {
            double dependentUnresolvedPercent = dependentCount == 0 ? 0.0 : dependentUnresolvedTargets * 100.0 / dependentCount;
            Severity dependentSeverity = dependentUnresolvedPercent >= dependentUnresolvedPercentWarningThreshold
                ? Severity.Warning
                : Severity.Info;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("dependent-handle-summary"),
                Analyzer = AnalyzerName,
                Category = "Retention",
                Severity = dependentSeverity,
                Title = "Dependent-handle retention summary",
                Narrative = $"Dependent handles: {dependentCount:N0}; resolved source->target edges: {dependentResolvedEdges:N0}; " +
                            $"unresolved targets: {dependentUnresolvedTargets:N0} ({dependentUnresolvedPercent:F1}%).",
                Recommendation = "Inspect dominant dependent-handle source/target pairs to identify hidden retention relationships.",
                Tags = ["dependent-handle", "retention", "conditionalweaktable"],
                MetricValue = dependentUnresolvedPercent,
                MetricUnit = "% unresolved-targets",
                DerivedFrom = populationIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();

    private static string FormatBytes(ulong bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:F1} {sizes[order]}";
    }
}
