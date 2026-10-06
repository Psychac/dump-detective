using DumpDetective.Sdk.Identity;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectCrashModuleHotspot</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Needed two new observations, neither of which the original held-reason fully covered.
/// <c>CrashAnalyzer</c>'s per-candidate module attribution (<c>CrashThreadCandidateSnapshot.
/// TopUserFrameModule</c>) had no observation at all — its existing <c>runtime.crash-thread-candidate</c>
/// subjects on the exception *type*, resolved before module attribution happens
/// (<c>BuildCrashThreadSnapshotsImpl</c>, called after that emission). Closed with a new
/// <c>runtime.crash-thread-candidate-module</c> observation, emitted once <c>BuildDomainResult</c>
/// has run. <c>ModuleAnalyzer</c>'s on-disk module size — the gap the original held-reason did name
/// — had no observation either; <c>runtime.module-heap-footprint</c>'s <c>shallow.bytes</c> is heap
/// footprint, a different quantity. Closed with a new <c>runtime.module-disk-footprint</c>
/// observation from the already-capped <c>TopModulesBySize</c> list. The version-conflict flag
/// needed <em>no</em> new emission — <c>runtime.module-conflict-group</c> already subjects per
/// module with empty <c>Measures</c>, so its mere presence for a module already is the signal.
/// The ranked module-attribution table from the original is dropped, not ported — same
/// "supplementary, not the whole payload" call <c>ReferenceChainDominatorCorrelation</c> made for
/// its own table; the headline already states the dominant module, its share, count, size and
/// conflict status.
/// </remarks>
internal sealed class CrashModuleHotspotInsightRule : ISynthesisRule
{
    private const int MinActiveExceptions = 3;
    private const double DominantSharePct = 50.0;

    public string RuleId => "insight.crash-module-hotspot";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.crash-thread-candidate-module", "runtime.module-conflict-group", "runtime.module-disk-footprint"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var moduleActiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var moduleActiveObservations = new Dictionary<string, List<ObservationId>>(StringComparer.Ordinal);
        var conflictedModules = new Dictionary<string, ObservationId>(StringComparer.Ordinal);
        var diskFootprintByModule = new Dictionary<string, Observation>(StringComparer.Ordinal);
        int attributedActiveExceptions = 0;

        foreach (Observation observation in matched.Matched)
        {
            string moduleName = ((ModuleRef)observation.Subjects[0]).SimpleName;
            switch (observation.ObservationType)
            {
                case "runtime.crash-thread-candidate-module":
                {
                    int count = (int)observation.Measures["active-exception.count"].Value;
                    moduleActiveCounts[moduleName] = moduleActiveCounts.GetValueOrDefault(moduleName) + count;
                    attributedActiveExceptions += count;
                    if (!moduleActiveObservations.TryGetValue(moduleName, out List<ObservationId>? ids))
                        moduleActiveObservations[moduleName] = ids = [];
                    ids.Add(observation.Id);
                    break;
                }
                case "runtime.module-conflict-group":
                    conflictedModules.TryAdd(moduleName, observation.Id);
                    break;
                case "runtime.module-disk-footprint":
                    diskFootprintByModule[moduleName] = observation;
                    break;
            }
        }

        if (attributedActiveExceptions < MinActiveExceptions)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        string topModuleName = string.Empty;
        int topModuleCount = -1;
        foreach (KeyValuePair<string, int> kvp in moduleActiveCounts)
        {
            if (kvp.Value > topModuleCount)
            {
                topModuleCount = kvp.Value;
                topModuleName = kvp.Key;
            }
        }

        double topModuleSharePct = 100.0 * topModuleCount / attributedActiveExceptions;
        if (topModuleSharePct < DominantSharePct)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        bool hasSizeMatch = diskFootprintByModule.TryGetValue(topModuleName, out Observation? sizeMatch);
        bool inConflict = conflictedModules.TryGetValue(topModuleName, out ObservationId conflictObservationId);

        if (!hasSizeMatch && !inConflict)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]); // no cross-analyzer signal beyond what per-analyzer attribution already shows

        var derivedFrom = new List<ObservationId>(moduleActiveObservations[topModuleName]);
        if (hasSizeMatch)
            derivedFrom.Add(sizeMatch!.Id);
        if (inConflict)
            derivedFrom.Add(conflictObservationId);

        string sizeNote = hasSizeMatch ? $" and is {InsightByteFormat.FormatBytes((ulong)sizeMatch!.Measures["disk.bytes"].Value)} on disk" : string.Empty;
        string conflictNote = inConflict
            ? " and has a conflicting assembly version loaded — a version mismatch may be the underlying cause"
            : string.Empty;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::crash-module-hotspot",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Dominates,
                    Measure = ClaimParts.Percent(topModuleSharePct),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(DominantSharePct), "dominant-share threshold"),
                    Qualifiers = new AssertionQualifiers
                    {
                        Location = $"active exceptions attributed to module {topModuleName}",
                        Mechanism = hasSizeMatch && inConflict ? "large on disk and in a version conflict" : hasSizeMatch ? "large on disk" : "in a version conflict",
                    },
                },
                Support =
                [
                    new EvidenceStatement(moduleActiveObservations[topModuleName], $"Module '{topModuleName}' owns {topModuleCount:N0} of {attributedActiveExceptions:N0} attributed active exceptions."),
                    .. hasSizeMatch ? [new EvidenceStatement([sizeMatch!.Id], "The module's on-disk footprint.")] : Array.Empty<EvidenceStatement>(),
                    .. inConflict ? [new EvidenceStatement([conflictObservationId], "The module is part of an assembly version conflict group.")] : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("The module that hosts the failing request path owns most exceptions whatever its size or versioning; the correlation does not show either causes them."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!pe",
                        Test = $"On threads carrying the attributed exceptions: if their top frames are not in '{topModuleName}', the attribution is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = $"If '{topModuleName}' is simply the application's request-handling module, concentration there is expected.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Crash",
                Severity = inConflict ? Severity.Warning : Severity.Info,
                Title = "Active exceptions concentrated in a module flagged by module analysis",
                Narrative = $"Module '{topModuleName}' owns {topModuleSharePct:F1}% of attributed active exceptions " +
                            $"({topModuleCount:N0} of {attributedActiveExceptions:N0}){sizeNote}{conflictNote}.",
                Recommendation = inConflict
                    ? $"Check the module inventory for duplicate/conflicting assembly versions of '{topModuleName}' — " +
                      "a version mismatch loading the wrong dependency is a common cause of concentrated runtime faults."
                    : $"Correlate '{topModuleName}'s size and load context with the exception attribution table to " +
                      "prioritize investigation of this assembly's fault handling.",
                Tags = ["crash", "modules", "cross-analyzer"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
