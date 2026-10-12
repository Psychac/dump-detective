using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>GCRootFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Trusts <c>heap.root</c> emission order for the static-root finding's "top suspect" text — the
/// old generator reads <c>TopRootsBySeverity[0]</c>, a list already severity-sorted before
/// <c>GCRootAnalyzer.EmitObservationsAsync</c>'s emission loop runs over it, the same "already
/// sorted before a separate, later emission pass" structure <c>ArraySynthesisRule</c> relies on
/// (not <c>AsyncStateMachineSynthesisRule</c>'s inline-per-type, pre-sort case).
/// </remarks>
internal sealed class GCRootSynthesisRule : ISynthesisRule
{
    private const ulong StaticRootWarningBytes = 10_000_000UL; // 10 MB
    private const ulong StaticRootCriticalBytes = 100_000_000UL; // 100 MB
    private const int FinalizerQueueInfoThreshold = 500;
    private const int FinalizerQueueWarningThreshold = 5_000;
    private const int PinnedHandleInfoThreshold = 100;
    private const int PinnedHandleWarningThreshold = 500;

    private const string AnalyzerName = "GC Root Analysis";

    public string RuleId => "gc-root.finding-parity";
    public int Version => 2;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.root", "root.kind-summary", "root.population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        Observation? topRoot = null;
        ulong staticBytes = 0;
        bool staticBytesAreExact = true;
        int staticCount = 0;
        var staticIds = new List<ObservationId>();
        int finalizerCount = 0;
        var finalizerIds = new List<ObservationId>();
        int pinnedCount = 0;
        int asyncPinnedCount = 0;
        var pinnedIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "heap.root":
                    topRoot ??= observation;
                    break;
                case "root.population":
                    population = observation;
                    break;
                case "root.kind-summary":
                {
                    var kind = (HeapRootKind)(int)observation.Measures["kind.ordinal"].Value;
                    switch (kind)
                    {
                        case HeapRootKind.StrongHandle:
                            staticBytes = (ulong)observation.Measures["retained.bytes"].Value;
                            staticBytesAreExact = observation.Measures["is-retained-exact"].Value != 0;
                            staticCount = (int)observation.Measures["count"].Value;
                            staticIds.Add(observation.Id);
                            break;
                        case HeapRootKind.FinalizerQueue:
                            finalizerCount = (int)observation.Measures["count"].Value;
                            finalizerIds.Add(observation.Id);
                            break;
                        case HeapRootKind.PinnedHandle:
                            pinnedCount = (int)observation.Measures["count"].Value;
                            pinnedIds.Add(observation.Id);
                            break;
                        case HeapRootKind.AsyncPinnedHandle:
                            asyncPinnedCount = (int)observation.Measures["count"].Value;
                            pinnedIds.Add(observation.Id);
                            break;
                    }
                    break;
                }
            }
        }

        var findings = new List<Claim>(capacity: 4);

        // ── Large static/strong root retention ──────────────────────────────────
        if (staticBytes >= StaticRootWarningBytes)
        {
            long totalRoots = population is not null ? (long)population.Measures["total.count"].Value : 0;
            string topSuspect = topRoot is not null ? ((TypeRef)topRoot.Subjects[0]).CanonicalName : "N/A";

            var derivedFrom = new List<ObservationId>(staticIds);
            if (topRoot is not null) derivedFrom.Add(topRoot.Id);

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("static-root-retention"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Retains,
                    Measure = ClaimParts.Bytes(staticBytes),
                    Reference = staticBytes >= StaticRootCriticalBytes
                        ? ClaimParts.Budget(ClaimParts.Bytes(StaticRootCriticalBytes), "critical threshold")
                        : ClaimParts.Budget(ClaimParts.Bytes(StaticRootWarningBytes), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "strong-handle (static) roots", Mechanism = topSuspect == "N/A" ? null : $"top suspect {topSuspect}" },
                },
                Support =
                [
                    new EvidenceStatement(staticIds, $"{staticCount:N0} strong-handle roots are estimated to retain {FormatBytes(staticBytes)}."),
                    .. topRoot is not null
                        ? [new EvidenceStatement([topRoot.Id], "The highest-ranked root, the top suspect for this retention.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = staticBytesAreExact
                    ? Counter.None("Strong handles include runtime-internal handles as well as application statics, and retained size alone does not separate them.")
                    : Counter.Of(new EvidenceStatement(staticIds, "The retained size is an estimate, not an exact dominator-tree figure, so the real retention may be smaller or larger.")),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type Strong",
                        Test = $"If !gchandles lists a number of strong handles other than {staticCount:N0}, the root count is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <object address>",
                        Test = "On an instance of the top suspect: if its path does not run through a strong handle, the retention is attributed to the wrong root.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = staticBytes >= StaticRootCriticalBytes ? Severity.Critical : Severity.Warning,
                Title = $"Large static root retention: {FormatBytes(staticBytes)} via {staticCount:N0} strong handles",
                Narrative = $"{staticCount:N0} strong-handle (static) roots estimated to retain {FormatBytes(staticBytes)}. " +
                            $"Total roots: {totalRoots:N0}. Top suspect: {topSuspect}.",
                Recommendation = "Inspect static fields and GC handles retaining large object graphs. " +
                                  "Consider weak references, clearing stale caches, or scoping long-lived state.",
                Tags = ["gc", "roots", "static", "retention", "leak"],
                MetricValue = staticBytes,
                MetricUnit = "bytes",
                DerivedFrom = derivedFrom,
                Confidence = StaticRetentionConfidence(staticBytesAreExact, population),
            });
        }

        // ── Finalizer queue pressure ─────────────────────────────────────────────
        if (finalizerCount >= FinalizerQueueInfoThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("finalizer-queue-pressure"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(finalizerCount),
                    Reference = finalizerCount >= FinalizerQueueWarningThreshold
                        ? ClaimParts.Budget(ClaimParts.Count(FinalizerQueueWarningThreshold), "warning threshold")
                        : ClaimParts.Budget(ClaimParts.Count(FinalizerQueueInfoThreshold), "notice threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "objects rooted by the finalizer queue" },
                },
                Support = [new EvidenceStatement(finalizerIds, $"{finalizerCount:N0} objects are rooted through the finalizer queue.")],
                Counter = Counter.None("The queue fills briefly after a burst of finalizable allocations and then drains; one snapshot does not show it is stuck."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!finalizequeue",
                        Test = $"If !finalizequeue reports far fewer than {finalizerCount:N0} objects ready for finalization, the pressure is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump a few minutes later; if the queue has drained, it was a transient burst.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = finalizerCount >= FinalizerQueueWarningThreshold ? Severity.Warning : Severity.Info,
                Title = $"Finalizer queue pressure: {finalizerCount:N0} finalizable roots",
                Narrative = $"{finalizerCount:N0} objects are rooted via the finalizer queue. " +
                            "These objects require an extra GC cycle to be freed.",
                Recommendation = "Ensure finalizable objects call Dispose() promptly. " +
                                  "Consider suppressing finalization after disposal via GC.SuppressFinalize().",
                Tags = ["gc", "roots", "finalizer", "pressure"],
                MetricValue = finalizerCount,
                MetricUnit = "objects",
                DerivedFrom = finalizerIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Pinned handle accumulation & LOH fragmentation risk ──────────────────
        int totalPinned = pinnedCount + asyncPinnedCount;
        if (totalPinned >= PinnedHandleInfoThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("pinned-handle-accumulation"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(totalPinned),
                    Reference = totalPinned >= PinnedHandleWarningThreshold
                        ? ClaimParts.Budget(ClaimParts.Count(PinnedHandleWarningThreshold), "warning threshold")
                        : ClaimParts.Budget(ClaimParts.Count(PinnedHandleInfoThreshold), "notice threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "pinned and async-pinned GC handles", Mechanism = "pinned objects block heap compaction" },
                },
                Support = [new EvidenceStatement(pinnedIds, $"{pinnedCount:N0} pinned and {asyncPinnedCount:N0} async-pinned handles total {totalPinned:N0}.")],
                Counter = Counter.None("Handles pinned for short-lived I/O buffers come and go; one snapshot does not show whether they persist."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type Pinned",
                        Test = $"If !gchandles lists a number of pinned handles other than {totalPinned:N0}, the count is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the same handles are no longer pinned, they were in-flight I/O, not accumulation.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = totalPinned >= PinnedHandleWarningThreshold ? Severity.Warning : Severity.Info,
                Title = $"Pinned handle accumulation: {totalPinned:N0} pinned objects",
                Narrative = $"{pinnedCount:N0} pinned handles + {asyncPinnedCount:N0} async pinned handles = {totalPinned:N0} total. " +
                            "Pinned objects prevent heap compaction and fragment both SOH (Small Object Heap) and LOH (Large Object Heap).",
                Recommendation = "Review pinned handle sources and consider: (1) Unpin objects after marshaling completes, " +
                                  "(2) Use GCHandle.Alloc(obj, GCHandleType.Weak) for weak references instead of pinning, " +
                                  "(3) Consolidate pinned allocations to reduce fragmentation, (4) Monitor pinned handle growth across dumps.",
                Tags = ["gc", "roots", "pinned", "fragmentation", "loh"],
                MetricValue = totalPinned,
                MetricUnit = "handles",
                DerivedFrom = pinnedIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // The subgraph walk cap is coverage, not a claim about the dump: see GCRootCoverageRule.

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    // Only the static-root finding reads retained bytes. Without the dominator tree they're the
    // bounded subgraph-walk estimate, and a capped walk undercounts that estimate further.
    private static ConfidenceBreakdown StaticRetentionConfidence(bool retainedBytesAreExact, Observation? population)
    {
        bool walkCapped = population is not null && population.Measures["subgraph-walk-capped"].Value != 0;
        long cappedCount = population is not null ? (long)population.Measures["subgraph-walk-capped.count"].Value : 0;
        return DataQualityPenalties.Apply(ConfidenceBreakdown.MeasurementCertain,
            new DataQualityPenalty(!retainedBytesAreExact, 0.3, "Average retained bytes are heuristic estimates."),
            new DataQualityPenalty(!retainedBytesAreExact && walkCapped, 0.20, $"Root-owned subgraph walk was capped ({cappedCount:N0} subgraph(s) truncated)."));
    }


    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
