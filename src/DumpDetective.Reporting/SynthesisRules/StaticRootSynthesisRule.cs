using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>StaticRootFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <b>Deliberately preserves <c>StaticRootAnalyzer</c>'s own known, documented bug</b> (its class
/// remarks: <c>HeapRootKind.StaticVar</c> never fires on ClrMD 4, so this analyzer produces zero
/// findings on every real dump) — same "mechanically port consumption logic, don't fix the
/// upstream bug" choice the analyzer's own retyping made, and the same choice
/// <c>StaticRootLeakDetector</c>'s retyping made per user call. This rule is correct for when the
/// upstream fix lands, not exercised on real dumps today.
/// <para>
/// The severity check's "largest root's memory impact" needs no dedicated observation: it's the
/// max <c>heap.static-root</c> <c>retained.bytes</c> across every analyzed root (significant or
/// not) by construction — a root large enough to be the overall max is always
/// >= <c>SignificantMemoryThresholdBytes</c>, hence always significant, so the global max and the
/// old generator's <c>topRoots[0].TotalMemoryImpact</c> (computed over the same unfiltered,
/// size-descending-sorted population) are always the same value.
/// </para>
/// <para>
/// The retention finding's confidence comes from the largest root's sampled GC root path
/// (<see cref="RootPathEvidenceStrength"/>), carried as raw facts on
/// <c>static-root.population</c> — the same root the old generator's
/// <c>EvidenceConfidence.Compute(topEvidence)</c> read.
/// </para>
/// </remarks>
internal sealed class StaticRootSynthesisRule : ISynthesisRule
{
    private const int RootCountCriticalThreshold = 10;
    private const ulong CriticalByteThreshold = 100 * 1_024 * 1_024;

    private const string AnalyzerName = "Static Root Leak Detection";

    public string RuleId => "static-root.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.static-root", "static-root.population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        ulong maxRetainedBytes = 0;
        var rootIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "static-root.population":
                    population = observation;
                    break;
                case "heap.static-root":
                    rootIds.Add(observation.Id);
                    ulong retainedBytes = (ulong)observation.Measures["retained.bytes"].Value;
                    if (retainedBytes > maxRetainedBytes) maxRetainedBytes = retainedBytes;
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long rootCount = (long)population.Measures["root.count"].Value;

        if (rootCount == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = Fingerprint("no-candidates"),
                Analyzer = AnalyzerName,
                Category = "Leak",
                Severity = Severity.Info,
                Title = "No static-root retention candidates detected",
                Narrative = "No static roots with significant retained object graphs were found.",
                Recommendation = "No static-root action required for this snapshot.",
                Tags = ["static-root", "retention"],
                MetricValue = 0,
                MetricUnit = "retained-bytes",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }]);
        }

        ulong totalRetainedBytes = (ulong)population.Measures["retained.bytes"].Value;

        Severity severity = rootCount >= RootCountCriticalThreshold || maxRetainedBytes > CriticalByteThreshold
            ? Severity.Critical
            : Severity.Warning;

        return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
        {
            Fingerprint = Fingerprint("retention-candidates"),
            Analyzer = AnalyzerName,
            Category = "Leak",
            Severity = severity,
            Title = "Static-root retention candidates detected",
            Narrative = $"{rootCount:N0} root(s) retain ~{FormatHelper.FormatBytes(totalRetainedBytes)} cumulative memory.",
            Recommendation = "Audit static ownership and clear or weaken references for expired object graphs.",
            Tags = ["static-root", "retention", "memory-leak"],
            MetricValue = totalRetainedBytes,
            MetricUnit = "retained-bytes",
            DerivedFrom = rootIds.Count > 0 ? [population.Id, .. rootIds] : [population.Id],
            Confidence = RootPathEvidenceStrength.FromMeasures(population.Measures, "top-root."),
        }]);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
