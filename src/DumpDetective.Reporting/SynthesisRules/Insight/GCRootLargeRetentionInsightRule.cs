using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectGCRootLargeRetention</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>heap.root</c> never carried per-root <c>RootKind</c> — deliberately, per its own remarks
/// ("revisit if a synthesis rule actually needs to group by it"). This rule does, so
/// <c>GCRootAnalyzer</c> now emits a <c>root-kind.ordinal</c> measure alongside it, the same
/// "ordinal, no live enum instance left by the time <c>Analyze</c> returns" shape
/// <c>root.kind-summary</c> already used for its own <c>kind.ordinal</c>.
/// </remarks>
internal sealed class GCRootLargeRetentionInsightRule : ISynthesisRule
{
    private const ulong LargeRetentionThresholdBytes = 50UL * 1024 * 1024; // 50 MB

    public string RuleId => "insight.gcroot-large-retention";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["heap.root", "root.kind-summary"] };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var roots = new List<Observation>();
        var kindSummaries = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "heap.root")
                roots.Add(observation);
            else if (observation.ObservationType == "root.kind-summary")
                kindSummaries.Add(observation);
        }

        if (roots.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        // Find the most impactful root overall
        ulong maxRetained = 0;
        string? rootKind = null;
        string? targetType = null;
        Observation? maxRoot = null;

        foreach (Observation root in roots)
        {
            ulong retained = (ulong)root.Measures["retained.bytes"].Value;
            if (retained > maxRetained)
            {
                maxRetained = retained;
                rootKind = ((HeapRootKind)(int)root.Measures["root-kind.ordinal"].Value).ToString();
                targetType = ((TypeRef)root.Subjects[0]).CanonicalName;
                maxRoot = root;
            }
        }

        if (maxRetained < LargeRetentionThresholdBytes)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        // Also count how many roots exceed the threshold
        int largeRootCount = 0;
        foreach (Observation root in roots)
        {
            if ((ulong)root.Measures["retained.bytes"].Value >= LargeRetentionThresholdBytes)
                largeRootCount++;
        }

        // Check if any by-kind summary shows a dominant large root kind
        string? dominantKind = null;
        ulong dominantKindBytes = 0;
        foreach (Observation kindSummary in kindSummaries)
        {
            ulong retained = (ulong)kindSummary.Measures["retained.bytes"].Value;
            if (retained > dominantKindBytes)
            {
                dominantKindBytes = retained;
                dominantKind = ((HeapRootKind)(int)kindSummary.Measures["kind.ordinal"].Value).ToString();
            }
        }

        string kindNote = dominantKind is not null
            ? $" Dominant root kind: {dominantKind} ({InsightByteFormat.FormatBytes(dominantKindBytes)} retained)."
            : string.Empty;

        var derivedFrom = new List<ObservationId> { maxRoot!.Id };
        foreach (Observation kindSummary in kindSummaries)
            derivedFrom.Add(kindSummary.Id);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::gcroot-large-retention",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "GC roots retaining large object sub-graphs detected",
                Narrative = $"{largeRootCount:N0} root(s) each retain ≥ {InsightByteFormat.FormatBytes(LargeRetentionThresholdBytes)}. " +
                            $"Largest root ({rootKind ?? "unknown"} → {targetType ?? "?"}) retains {InsightByteFormat.FormatBytes(maxRetained)}.{kindNote}",
                Recommendation = "Review the GC Root analyzer findings for specific retention paths. " +
                                 "Look for static fields, GC handles, or thread locals holding large collections.",
                Tags = ["gc-root", "retention", "memory-leak"],
                MetricValue = (double)maxRetained,
                MetricUnit = "bytes",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
