using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectPinnedStringLeak</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>handle.pinned-target-type</c> (<c>GCHandleAnalyzer</c>, already existed) carries per-type
/// pinned counts but not bytes. <c>GCHandleAnalyzer</c> now also emits
/// <c>handle.pinned-target-bytes</c> — sourced from <c>TopPinnedObjectsBySize</c>/
/// <c>TopAsyncPinnedObjectsBySize</c>, its own independent top-N-by-bytes rankings, not summed
/// from the per-address <c>handle.pinned</c> observation (that one's capped to
/// <c>GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow</c> addresses, so a per-type sum over
/// it would silently undercount) and not index-zipped against <c>handle.pinned-target-type</c>
/// (the original's own <c>FindBytes</c>/<c>FindCount</c> helpers already treated the two rankings
/// as independent, exact-name lookups — preserved here).
/// </remarks>
internal sealed class PinnedStringLeakInsightRule : ISynthesisRule
{
    private const ulong MinBytes = 1UL * 1024 * 1024;      // 1 MB
    private const ulong CriticalBytes = 20UL * 1024 * 1024; // 20 MB
    private const string TargetTypeName = "System.String";

    public string RuleId => "insight.pinned-string-leak";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["handle.pinned-target-bytes", "handle.pinned-target-type"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        ulong pinnedBytes = 0;
        int pinnedCount = 0;
        var derivedFrom = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
            if (!string.Equals(typeName, TargetTypeName, StringComparison.Ordinal))
                continue;

            switch (observation.ObservationType)
            {
                case "handle.pinned-target-bytes":
                    pinnedBytes += (ulong)observation.Measures["retained.bytes"].Value;
                    derivedFrom.Add(observation.Id);
                    break;
                case "handle.pinned-target-type":
                    pinnedCount = (int)observation.Measures["instance.count"].Value;
                    derivedFrom.Add(observation.Id);
                    break;
            }
        }

        if (pinnedBytes < MinBytes)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        Severity severity = pinnedBytes >= CriticalBytes ? Severity.Critical : Severity.Warning;

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::pinned-string-leak",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = severity,
                Title = "Pinned strings detected — blocking GC compaction",
                Narrative = $"{pinnedCount:N0} pinned System.String handle target(s) totaling {InsightByteFormat.FormatBytes(pinnedBytes)}.",
                Recommendation = "Pinning managed strings (e.g. via GCHandle.Alloc(pin: true) for P/Invoke marshalling) " +
                                 "prevents the GC from compacting the small object heap around them. Free pinned string " +
                                 "handles as soon as the interop call returns, or use Marshal.StringToHGlobalAnsi/Unicode " +
                                 "(which copies into unmanaged memory) instead of pinning the managed string itself.",
                Tags = ["strings", "pinning", "gc", "handles", "cross-analyzer"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
