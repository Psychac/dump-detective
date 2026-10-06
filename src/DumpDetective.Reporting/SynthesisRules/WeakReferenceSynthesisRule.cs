using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>WeakReferenceFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>AliveWeakTargets</c> and <c>StaleWrapperCount</c> are reconstructed by counting matched
/// <c>weakhandle.target</c>/<c>weakreference.stale-wrapper</c> observations rather than duplicated
/// as measures on the new <c>weakreference.population</c> observation — same "don't duplicate what
/// counting already gives you" choice <c>AsyncTaskSynthesisRule</c> made for <c>OrphanedTasks</c>.
/// <c>StaleWrapperCountIsExact</c> is not read from an observation at all: the retyped analyzer's
/// own remarks say the degraded (extrapolated) case "can no longer occur by construction," and it
/// unconditionally constructs <c>StaleWrapperCountIsExact: true</c> — so the qualifier text is
/// always empty here, matching that structural guarantee rather than a measured fact.
/// </remarks>
internal sealed class WeakReferenceSynthesisRule : ISynthesisRule
{
    private const double HighDeadRatioWarningThreshold = 0.5;
    private const double HighDeadRatioCriticalThreshold = 0.8;
    private const int AbsoluteDeadCountThreshold = 10_000;
    private const int AbsoluteDeadCountCriticalThreshold = 100_000;

    private const string AnalyzerName = "Weak Reference Analysis";

    public string RuleId => "weak-reference.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["weakhandle.target", "weakreference.stale-wrapper", "weakreference.population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        int aliveWeakTargets = 0;
        int staleWrapperCount = 0;
        var weakHandleIds = new List<ObservationId>();
        var staleWrapperIds = new List<ObservationId>();
        int heldOnlyCount = 0;
        var heldOnlyIds = new List<ObservationId>();
        var heldOnlyTypeCounts = new Dictionary<string, (long Count, List<ObservationId> Ids)>(StringComparer.Ordinal);

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "weakhandle.target":
                    aliveWeakTargets++;
                    weakHandleIds.Add(observation.Id);
                    if (observation.Measures["held-only-via-weak-reference"].Value >= 1.0)
                    {
                        heldOnlyCount++;
                        heldOnlyIds.Add(observation.Id);

                        string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                        if (!heldOnlyTypeCounts.TryGetValue(typeName, out var existing))
                            existing = (0, []);
                        existing.Count++;
                        existing.Ids.Add(observation.Id);
                        heldOnlyTypeCounts[typeName] = existing;
                    }
                    break;
                case "weakreference.stale-wrapper":
                    staleWrapperCount++;
                    staleWrapperIds.Add(observation.Id);
                    break;
                case "weakreference.population":
                    population = observation;
                    break;
            }
        }

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        IReadOnlyDictionary<string, Measure> m = population.Measures;
        long totalWeakHandles = (long)m["total-handles.count"].Value;
        long deadWeakTargets = (long)m["dead-targets.count"].Value;
        long weakReferenceObjectCount = (long)m["object.count"].Value;
        ulong weakReferenceObjectBytes = (ulong)m["object.bytes"].Value;
        long dependentHandleDeadKeyCount = (long)m["dependent-dead-key.count"].Value;
        bool heldOnlyDetectionAvailable = m["held-only-detection-available"].Value >= 1.0;

        double deadTargetRatio = totalWeakHandles == 0 ? 0.0 : (double)deadWeakTargets / totalWeakHandles;
        const string staleWrapperQualifier = "";

        var signals = new List<(Claim Claim, int Priority)>(4);

        // High dead-target ratio — stale wrapper accumulation.
        if (totalWeakHandles > 0 && deadTargetRatio >= HighDeadRatioWarningThreshold)
        {
            signals.Add((new Claim
            {
                Fingerprint = Fingerprint("high-dead-ratio"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(deadTargetRatio * 100.0),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Percent((deadTargetRatio >= HighDeadRatioCriticalThreshold ? HighDeadRatioCriticalThreshold : HighDeadRatioWarningThreshold) * 100.0),
                        deadTargetRatio >= HighDeadRatioCriticalThreshold ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "weak handles whose target is already collected" },
                },
                Support =
                [
                    new EvidenceStatement([population.Id], $"{deadWeakTargets:N0} of {totalWeakHandles:N0} weak handles point at collected objects."),
                    .. staleWrapperIds.Count > 0
                        ? [new EvidenceStatement(staleWrapperIds, "Each is a WeakReference wrapper whose target is gone.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("Entries in a weak cache are expected to go stale between purges; the ratio does not show purging has stopped."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles -type WeakShort",
                        Test = $"If !gchandles shows far fewer dead weak handles than {deadWeakTargets:N0}, the dead ratio is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the owning cache purges stale entries on a schedule, the dead handles are awaiting the next sweep.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = deadTargetRatio >= HighDeadRatioCriticalThreshold ? Severity.Critical : Severity.Warning,
                Title = "High proportion of dead weak handle targets",
                Narrative = $"{deadWeakTargets:N0} of {totalWeakHandles:N0} weak handles " +
                            $"({deadTargetRatio:P1}) point to already-collected objects. " +
                            $"Stale WeakReference wrappers: {staleWrapperCount:N0}{staleWrapperQualifier}.",
                Recommendation = "Audit caches and event subscriptions that hold WeakReference objects. " +
                                  "Ensure entries are purged after the target is collected to avoid accumulation.",
                Tags = ["weak-reference", "handles", "cache", "memory-leak"],
                MetricValue = deadTargetRatio,
                MetricUnit = "ratio",
                DerivedFrom = [population.Id, .. staleWrapperIds],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }, 200));
        }

        // Absolute dead-count threshold signal (complementary to ratio).
        if (deadWeakTargets > AbsoluteDeadCountThreshold)
        {
            signals.Add((new Claim
            {
                Fingerprint = Fingerprint("high-dead-count"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(deadWeakTargets),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Count(deadWeakTargets > AbsoluteDeadCountCriticalThreshold ? AbsoluteDeadCountCriticalThreshold : AbsoluteDeadCountThreshold),
                        deadWeakTargets > AbsoluteDeadCountCriticalThreshold ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "weak handles whose target is already collected" },
                },
                Support = [new EvidenceStatement([population.Id], $"{deadWeakTargets:N0} dead weak handles have accumulated.")],
                Counter = Counter.None("In a process with millions of weak handles a large dead count can be a small fraction; the absolute count does not show unbounded growth."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the dead count does not grow, it is a steady-state backlog, not accumulation.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = deadWeakTargets > AbsoluteDeadCountCriticalThreshold ? Severity.Critical : Severity.Warning,
                Title = "High absolute count of dead weak handle targets",
                Narrative = $"{deadWeakTargets:N0} dead weak handles accumulated. " +
                            "Large-scale applications with millions of handles can have benign ratios but still accumulate significant dead count.",
                Recommendation = "Review cache retention patterns and event subscription cleanup. " +
                                  "Implement periodic purging of dead entries to prevent unbounded growth.",
                Tags = ["weak-reference", "handles", "cache", "memory-leak"],
                MetricValue = deadWeakTargets,
                MetricUnit = "handles",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }, 150));
        }

        // Dependent handle dead-key signal.
        if (dependentHandleDeadKeyCount > 0)
        {
            signals.Add((new Claim
            {
                Fingerprint = Fingerprint("dependent-dead-key"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(dependentHandleDeadKeyCount),
                    Qualifiers = new AssertionQualifiers { Location = "dependent handles with a collected primary key", Mechanism = "ConditionalWeakTable entries" },
                },
                Support = [new EvidenceStatement([population.Id], $"{dependentHandleDeadKeyCount:N0} dependent handles have a dead primary key.")],
                Counter = Counter.None("The GC removes dead-key entries on its own; their presence at capture time shows only that cleanup is pending."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump after a GC; if the dead-key entries are gone, they were pending cleanup.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Dependent handles with dead primary keys detected",
                Narrative = $"{dependentHandleDeadKeyCount:N0} dependent handle(s) have a dead primary key, " +
                            "indicating ConditionalWeakTable entries whose source object has been collected.",
                Recommendation = "Review ConditionalWeakTable usage; dead-key entries are cleaned up by the GC " +
                                  "but a high count may indicate object lifecycle issues.",
                Tags = ["dependent-handle", "conditional-weak-table"],
                MetricValue = dependentHandleDeadKeyCount,
                MetricUnit = "handles",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }, 100));
        }

        // "Held only via weak reference" signal — objects still alive but unreachable from any
        // GC root, so the weak handle is the only known reference and they'll be collected next GC.
        if (heldOnlyDetectionAvailable && heldOnlyCount > 0)
        {
            string topTypeSuffix = "";
            long topCount = -1;
            string topType = "";
            foreach ((string typeName, (long count, List<ObservationId> _)) in heldOnlyTypeCounts)
            {
                if (count > topCount)
                {
                    topCount = count;
                    topType = typeName;
                }
            }
            if (topCount > 0) topTypeSuffix = $" Most common: {topType} ({topCount:N0}).";

            signals.Add((new Claim
            {
                Fingerprint = Fingerprint("held-only-via-weak-reference"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(heldOnlyCount),
                    Qualifiers = new AssertionQualifiers { Location = "alive weak targets unreachable from any GC root", Mechanism = "pending collection" },
                },
                Support = [new EvidenceStatement(heldOnlyIds, $"{heldOnlyCount:N0} alive weak targets have no GC-root path; the weak handle is the only known reference.")],
                Counter = Counter.None("This describes objects pending collection and asserts no leak, so there is nothing to weaken."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <target address>",
                        Test = "On a sampled target: if !gcroot finds a root, it is reachable, not held only by the weak handle.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Info,
                Title = "Objects held only via weak reference",
                Narrative = $"{heldOnlyCount:N0} alive weak target(s) are unreachable from any GC root — " +
                            $"the weak handle is currently the only known reference to them.{topTypeSuffix}",
                Recommendation = "Informational: these objects are pending collection on the next GC and are not " +
                                  "themselves a leak. If this count is unexpectedly large, it may indicate churn in a " +
                                  "cache or subscription pattern that repeatedly creates short-lived weakly-referenced objects.",
                Tags = ["weak-reference", "gc-root", "diagnostic"],
                MetricValue = heldOnlyCount,
                MetricUnit = "objects",
                DerivedFrom = heldOnlyIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }, 120));
        }

        // Summary finding (always).
        Severity summarySeverity = Severity.Info;
        foreach ((Claim signal, _) in signals)
        {
            if (SeverityRank(signal.Severity) > SeverityRank(summarySeverity))
                summarySeverity = signal.Severity;
        }

        var findings = new List<Claim>(signals.Count + 1)
        {
            new Claim
            {
                Fingerprint = Fingerprint("overview"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Composes,
                    Measure = ClaimParts.Count(totalWeakHandles),
                    Qualifiers = new AssertionQualifiers { Location = "weak handles by target state" },
                },
                Support = [new EvidenceStatement([population.Id], $"{totalWeakHandles:N0} weak handles: {aliveWeakTargets:N0} alive targets and {deadWeakTargets:N0} dead.")],
                Counter = Counter.None("This describes weak handle counts and asserts no problem, so there is nothing to weaken."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gchandles",
                        Test = $"If !gchandles lists a number of weak handles other than {totalWeakHandles:N0}, the overview is wrong.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = summarySeverity,
                Title = "Weak reference overview",
                Narrative = $"Total weak handles: {totalWeakHandles:N0}. " +
                            $"Alive targets: {aliveWeakTargets:N0}, dead: {deadWeakTargets:N0} " +
                            $"({deadTargetRatio:P1}). " +
                            $"WeakReference objects: {weakReferenceObjectCount:N0} " +
                            $"({FormatHelper.FormatBytes(weakReferenceObjectBytes)}). " +
                            $"Stale wrappers: {staleWrapperCount:N0}{staleWrapperQualifier}.",
                Recommendation = signals.Count > 0
                    ? $"Review the {signals.Count} weak-reference signal(s) below."
                    : "Weak handle health is acceptable.",
                Tags = ["weak-reference", "handles", "overview"],
                MetricValue = totalWeakHandles,
                MetricUnit = "handles",
                DerivedFrom = [population.Id, .. weakHandleIds, .. staleWrapperIds],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        };

        if (signals.Count > 0)
        {
            // Sort by severity (descending) then priority (descending) — mirrors the old
            // generator's OrderByDescending(severity).ThenByDescending(priority) over its own
            // fixed-priority WeakRefSignal records (200, 150, 100, 120).
            IEnumerable<Claim> sortedSignals = signals
                .OrderByDescending(s => SeverityRank(s.Claim.Severity))
                .ThenByDescending(s => s.Priority)
                .Select(s => s.Claim);

            findings.AddRange(sortedSignals);
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Critical => 3,
        Severity.Warning => 2,
        _ => 1,
    };

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
