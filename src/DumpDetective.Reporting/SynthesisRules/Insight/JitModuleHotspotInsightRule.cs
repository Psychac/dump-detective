using DumpDetective.Sdk.Identity;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectJitModuleHotspot</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Needed one new observation, <c>runtime.jit-module-frames</c> (<c>JitAnalyzer</c>'s per-module
/// frame counts had none). The module side reuses what <c>CrashModuleHotspotInsightRule</c> already
/// joins against: <c>runtime.module-disk-footprint</c> for size and
/// <c>runtime.module-conflict-group</c>'s presence for a version conflict. Module names are matched
/// case-insensitively, as the original did. The original's top-5 module table is dropped — the
/// headline already names the top module with its frame count, size and conflict status.
/// </remarks>
internal sealed class JitModuleHotspotInsightRule : ISynthesisRule
{
    private const int MinFrameHits = 50;

    public string RuleId => "insight.jit-module-hotspot";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.jit-module-frames", "runtime.module-disk-footprint", "runtime.module-conflict-group"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? topModule = null;
        var diskFootprintByModule = new Dictionary<string, Observation>(StringComparer.OrdinalIgnoreCase);
        var conflictedModules = new Dictionary<string, ObservationId>(StringComparer.OrdinalIgnoreCase);

        foreach (Observation observation in matched.Matched)
        {
            string moduleName = ((ModuleRef)observation.Subjects[0]).SimpleName;
            switch (observation.ObservationType)
            {
                case "runtime.jit-module-frames":
                    if (topModule is null || FrameCount(observation) > FrameCount(topModule))
                        topModule = observation;
                    break;
                case "runtime.module-disk-footprint":
                    diskFootprintByModule[moduleName] = observation;
                    break;
                case "runtime.module-conflict-group":
                    conflictedModules.TryAdd(moduleName, observation.Id);
                    break;
            }
        }

        if (topModule is null || FrameCount(topModule) < MinFrameHits)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        string topModuleName = ((ModuleRef)topModule.Subjects[0]).SimpleName;
        bool hasSizeMatch = diskFootprintByModule.TryGetValue(topModuleName, out Observation? sizeMatch);
        bool inConflict = conflictedModules.TryGetValue(topModuleName, out ObservationId conflictObservationId);

        if (!hasSizeMatch && !inConflict)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]); // no cross-analyzer signal beyond the JIT section's own heatmap

        var derivedFrom = new List<ObservationId> { topModule.Id };
        if (hasSizeMatch)
            derivedFrom.Add(sizeMatch!.Id);
        if (inConflict)
            derivedFrom.Add(conflictObservationId);

        int frameCount = FrameCount(topModule);
        string sizeNote = hasSizeMatch ? $" and is {InsightByteFormat.FormatBytes((ulong)sizeMatch!.Measures["disk.bytes"].Value)} on disk" : string.Empty;
        string conflictNote = inConflict ? " and is involved in an assembly version conflict" : string.Empty;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::jit-module-hotspot",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Subject = topModule.Subjects[0],
                    Measure = ClaimParts.Count(frameCount),
                    Reference = ClaimParts.Budget(ClaimParts.Count(MinFrameHits), "frame-presence threshold"),
                    Qualifiers = new AssertionQualifiers
                    {
                        Location = $"active JIT stack frames in module {topModuleName}",
                        Mechanism = hasSizeMatch && inConflict ? "large on disk and in a version conflict" : hasSizeMatch ? "large on disk" : "in a version conflict",
                    },
                },
                Support =
                [
                    new EvidenceStatement([topModule.Id], $"Module '{topModuleName}' accounts for {frameCount:N0} active JIT stack frames."),
                    .. hasSizeMatch ? [new EvidenceStatement([sizeMatch!.Id], "The module's on-disk footprint.")] : Array.Empty<EvidenceStatement>(),
                    .. inConflict ? [new EvidenceStatement([conflictObservationId], "The module is part of an assembly version conflict group.")] : Array.Empty<EvidenceStatement>(),
                ],
                Counter = Counter.None("A module that carries the application's hot path is expected to dominate stacks; its presence there does not show the size or conflict matters."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eestack",
                        Test = $"If the {frameCount:N0} frames from '{topModuleName}' are mostly parked in waits, not executing, the presence is idle, not hot.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Performance",
                Severity = Severity.Info,
                Title = "Module with heavy active JIT stack presence also flagged by module analysis",
                Narrative = $"Module '{topModuleName}' accounts for {frameCount:N0} active JIT stack frames{sizeNote}{conflictNote}.",
                Recommendation = "Correlate this module's size/version-conflict status with the JIT stack " +
                                 "heatmap to prioritize ReadyToRun/NativeAOT precompilation or dependency " +
                                 "deduplication for this assembly.",
                Tags = ["jit", "modules", "cross-analyzer"],
                MetricValue = frameCount,
                MetricUnit = "frames",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }

    private static int FrameCount(Observation observation) => (int)observation.Measures["active-frame.count"].Value;
}
