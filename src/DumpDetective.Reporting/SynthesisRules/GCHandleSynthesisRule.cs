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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

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

        var findings = new List<Claim>(capacity: 6);
        var populationIds = new List<ObservationId> { population.Id };

        // ── Summary — always emitted ────────────────────────────────────────────
        Severity severity = pinnedHandleTargets >= pinnedHandleTargetsWarningThreshold
            || totalHandles >= totalHandlesWarningThreshold
            ? Severity.Warning : Severity.Info;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("summary"),
            Assertion = BuildSummaryAssertion(
                severity, totalHandles, totalHandlesWarningThreshold, pinnedHandleTargets, pinnedHandleTargetsWarningThreshold),
            Support = [new EvidenceStatement(populationIds, $"{totalHandles:N0} GC handles, {pinnedHandleTargets:N0} of them pinning a target.")],
            Counter = Counter.None(severity == Severity.Warning
                ? "Handles held for in-flight interop and I/O come and go; one snapshot does not show they persist."
                : "This describes handle counts and asserts no problem, so there is nothing to weaken."),
            FalsifiedBy =
            [
                new FalsificationTest
                {
                    Kind = FalsificationKind.Windbg,
                    Command = "!gchandles",
                    Test = $"If !gchandles lists a total other than {totalHandles:N0}, or {pinnedHandleTargets:N0} pinned targets, the handle counts are wrong.",
                },
            ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("pinned-retained-bytes"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Retains,
                    Measure = ClaimParts.Bytes(pinnedRetainedBytes),
                    Reference = ClaimParts.Budget(ClaimParts.Bytes(pinnedRetainedBytesWarningThreshold), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "objects retained by pinned handles" },
                },
                Support = [new EvidenceStatement(populationIds, $"Pinned handles retain {FormatBytes(pinnedRetainedBytes)} across {pinnedHandleTargets:N0} targets.")],
                Counter = Counter.None("Retained size counts everything reachable from a pinned target, including objects that would be live without the pin."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <pinned target address>",
                        Test = "On a sampled pinned target: if it is also reachable from a normal root, the pin is not what keeps its graph alive.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the pinned targets are different ones, they are in-flight buffers.",
                    },
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("soh-pinned"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Blocks,
                    Measure = ClaimParts.Count(sohPinnedTargets),
                    Reference = ClaimParts.Budget(ClaimParts.Count(pinnedSohWarningThreshold), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "small-object-heap pinned targets", Mechanism = "pinned objects cannot be moved by compaction" },
                },
                Support = [new EvidenceStatement(populationIds, $"{sohPinnedTargets:N0} pinned targets are on the small object heap ({pinnedSohCount:N0} pinned, {asyncPinnedSohCount:N0} async-pinned).")],
                Counter = Counter.None("Pinned objects only block compaction while pinned; short-lived pins release as I/O completes."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type Pinned",
                        Test = $"If !gchandles shows far fewer pinned targets on the small object heap than {sohPinnedTargets:N0}, the count is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the small-heap pins are gone, they were transient.",
                    },
                ],
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("refcounted-pressure"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(refCountedCount),
                    Reference = ClaimParts.Budget(ClaimParts.Count(refCountedWarningThreshold), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "RefCounted (COM interop) handles", Mechanism = "keeps COM runtime callable wrappers alive" },
                },
                Support =
                [
                    new EvidenceStatement(populationIds, $"{refCountedCount:N0} RefCounted handles are held."),
                    .. refCountedTypeIds.Count > 0
                        ? [new EvidenceStatement(refCountedTypeIds, "Each is a target type held by RefCounted handles.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("A RefCounted handle is released when its count reaches zero; the count at capture time does not show it is leaked."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type RefCounted",
                        Test = $"If !gchandles lists a number of RefCounted handles other than {refCountedCount:N0}, the count is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the owning code calls Marshal.ReleaseComObject or disposes the wrapper deterministically, the handles are released promptly.",
                    },
                ],
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

                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("weaklong-gen2-backlog"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Exceeds,
                        Measure = ClaimParts.Percent(weakLongGen2PlusFraction),
                        Reference = ClaimParts.Budget(ClaimParts.Percent(weakLongGen2FractionWarningThreshold), "Gen2/LOH concentration threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "WeakLong handle targets in Gen2 or the LOH", Mechanism = "WeakLong clears only after finalization" },
                    },
                    Support = [new EvidenceStatement(populationIds, $"{weakLongGen2PlusCount:N0} of {weakLongResolvedCount:N0} resolved WeakLong targets are in Gen2 or the LOH; for WeakShort it is {weakShortGen2PlusCount:N0} of {weakShortResolvedCount:N0}.")],
                    Counter = Counter.None("Long-lived objects naturally sit in Gen2, so a high Gen2 share of WeakLong targets does not by itself show a finalization backlog."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!finalizequeue",
                            Test = "If !finalizequeue shows a short queue and the finalizer thread is idle, there is no finalization backlog.",
                        },
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Capture,
                            Test = "Capture a second dump later; if the Gen2 WeakLong targets are cleared, they were awaiting finalization, not leaked.",
                        },
                    ],
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("dependent-handle-summary"),
                Assertion = new Assertion
                {
                    Predicate = dependentSeverity == Severity.Warning ? AssertionPredicate.Exceeds : AssertionPredicate.Composes,
                    Measure = dependentSeverity == Severity.Warning ? ClaimParts.Percent(dependentUnresolvedPercent) : ClaimParts.Count(dependentCount),
                    Reference = dependentSeverity == Severity.Warning
                        ? ClaimParts.Budget(ClaimParts.Percent(dependentUnresolvedPercentWarningThreshold), "unresolved-target threshold")
                        : null,
                    Qualifiers = new AssertionQualifiers { Location = "dependent handles", Mechanism = "ConditionalWeakTable entries" },
                },
                Support = [new EvidenceStatement(populationIds, $"{dependentCount:N0} dependent handles, {dependentResolvedEdges:N0} resolved edges and {dependentUnresolvedTargets:N0} unresolved targets.")],
                Counter = Counter.None(dependentSeverity == Severity.Warning
                    ? "An unresolved target is one the walk could not follow; it does not show the target is retained."
                    : "This describes dependent handles and asserts no problem, so there is nothing to weaken."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type Dependent",
                        Test = $"If !gchandles lists a number of dependent handles other than {dependentCount:N0}, the count is wrong.",
                    },
                ],
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static Assertion BuildSummaryAssertion(
        Severity severity, long totalHandles, long totalHandlesWarningThreshold, long pinnedHandleTargets, long pinnedHandleTargetsWarningThreshold)
    {
        if (severity != Severity.Warning)
            return new Assertion
            {
                Predicate = AssertionPredicate.Composes,
                Measure = ClaimParts.Count(totalHandles),
                Qualifiers = new AssertionQualifiers { Location = "GC handles by kind" },
            };

        bool byPinned = pinnedHandleTargets >= pinnedHandleTargetsWarningThreshold;
        return new Assertion
        {
            Predicate = AssertionPredicate.Exceeds,
            Measure = ClaimParts.Count(byPinned ? pinnedHandleTargets : totalHandles),
            Reference = ClaimParts.Budget(
                ClaimParts.Count(byPinned ? pinnedHandleTargetsWarningThreshold : totalHandlesWarningThreshold),
                byPinned ? "pinned-target warning threshold" : "total-handle warning threshold"),
            Qualifiers = new AssertionQualifiers { Location = byPinned ? "pinned handle targets" : "GC handles" },
        };
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
