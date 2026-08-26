using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class GCHandleFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "GC Handle Analysis";

    public bool CanGenerate(AnalyzerDomainResult result) => result is GCHandleDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not GCHandleDomainResult r) return [];

        FindingSeverity severity = r.PinnedHandleTargets >= r.PinnedHandleTargetsWarningThreshold
            || r.TotalHandles >= r.TotalHandlesWarningThreshold
            ? FindingSeverity.Warning : FindingSeverity.Info;

        var findings = new List<InsightFinding>(capacity: 6)
        {
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: severity,
                Title: "GC handle pressure summary",
                Evidence: $"Total handles: {r.TotalHandles:N0}; pinned-handle target count: {r.PinnedHandleTargets:N0}.",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Inspect pinned-handle-heavy types and reduce long-lived pinning where possible."
                    : "Handle distribution appears within expected bounds for this snapshot.",
                Tags: ["gc-handle", "pinning", "retention"],
                MetricValue: r.TotalHandles,
                MetricUnit: "total-handles")
        };

        // P1-1: Add PinnedRetainedBytes threshold finding
        if (r.PinnedRetainedBytes > 0)
        {
            FindingSeverity pinnedBytesSeverity = r.PinnedRetainedBytes >= r.PinnedRetainedBytesWarningThreshold
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            if (pinnedBytesSeverity == FindingSeverity.Warning)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "GC",
                    Severity: pinnedBytesSeverity,
                    Title: "High pinned retained bytes",
                    Evidence: $"Total pinned retained bytes: {FormatBytes(r.PinnedRetainedBytes)}.",
                    Recommendation: "Reduce pinned object lifetime or avoid pinning large objects to decrease heap fragmentation.",
                    Tags: ["gc-handle", "pinning", "memory-pressure"],
                    MetricValue: (double)r.PinnedRetainedBytes,
                    MetricUnit: "bytes"));
            }
        }

        // P2-1: SOH-pinned targets block GC compaction; LOH/POH/Frozen-pinned targets don't
        // (LOH is never compacted, POH objects are already pinned by construction), so flag only
        // the SOH count as an actionable compaction-barrier signal.
        int sohPinnedTargets = r.PinnedSohObjectCount + r.AsyncPinnedSohObjectCount;
        if (sohPinnedTargets > 0)
        {
            FindingSeverity sohSeverity = sohPinnedTargets >= r.PinnedSohObjectCountWarningThreshold
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            if (sohSeverity == FindingSeverity.Warning)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "GC",
                    Severity: sohSeverity,
                    Title: "SOH-pinned objects blocking GC compaction",
                    Evidence: $"Small-object-heap pinned targets: {sohPinnedTargets:N0} (Pinned: {r.PinnedSohObjectCount:N0}, AsyncPinned: {r.AsyncPinnedSohObjectCount:N0}); non-SOH pinned targets (LOH/POH/Frozen, no compaction impact): {r.PinnedNonSohObjectCount + r.AsyncPinnedNonSohObjectCount:N0}.",
                    Recommendation: "SOH-pinned objects block GC compaction of surrounding memory. Shorten pin lifetimes or move long-lived pinned buffers to pre-allocated/native memory where possible.",
                    Tags: ["gc-handle", "pinning", "compaction", "soh"],
                    MetricValue: sohPinnedTargets,
                    MetricUnit: "soh-pinned-targets"));
            }
        }

        // P2-2: RefCounted handles back COM interop RCW lifetime — an accumulating count usually
        // means COM objects (or Marshal.ReleaseComObject calls) aren't being released.
        if (r.RefCountedHandleCount > 0)
        {
            FindingSeverity refCountedSeverity = r.RefCountedHandleCount >= r.RefCountedHandleCountWarningThreshold
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            if (refCountedSeverity == FindingSeverity.Warning)
            {
                string topType = r.TopRefCountedTargetTypes is { Count: > 0 } topTypes
                    ? $" Dominant type: {topTypes[0].Name} ({topTypes[0].Count:N0})."
                    : string.Empty;

                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "GC",
                    Severity: refCountedSeverity,
                    Title: "COM interop (RefCounted) handle pressure",
                    Evidence: $"RefCounted handles: {r.RefCountedHandleCount:N0}.{topType}",
                    Recommendation: "RefCounted handles keep COM RCWs alive. Ensure COM objects are released (Marshal.ReleaseComObject / using) rather than left for finalization.",
                    Tags: ["gc-handle", "com-interop", "refcounted"],
                    MetricValue: r.RefCountedHandleCount,
                    MetricUnit: "refcounted-handles"));
            }
        }

        // P3-2: WeakLong clears only after finalization completes, so a population concentrated
        // in Gen2/LOH can indicate a finalization backlog (targets lingering, weakly-referenced,
        // waiting for their finalizer to run). Gated by an absolute-count minimum to avoid noise
        // on small weak-handle populations.
        int weakLongGen2PlusCount = r.WeakLongGen2Count + r.WeakLongLohCount;
        int weakLongResolvedCount = r.WeakLongGen0Count + r.WeakLongGen1Count + weakLongGen2PlusCount;
        if (weakLongGen2PlusCount >= r.WeakLongGen2MinimumCountThreshold && weakLongResolvedCount > 0)
        {
            double weakLongGen2PlusFraction = weakLongGen2PlusCount * 100.0 / weakLongResolvedCount;
            if (weakLongGen2PlusFraction >= r.WeakLongGen2FractionWarningThreshold)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "GC",
                    Severity: FindingSeverity.Warning,
                    Title: "WeakLong handles concentrated in Gen2/LOH — possible finalization backlog",
                    Evidence: $"WeakLong targets in Gen2/LOH: {weakLongGen2PlusCount:N0} of {weakLongResolvedCount:N0} resolved ({weakLongGen2PlusFraction:F1}%). WeakShort comparison: Gen2/LOH {r.WeakShortGen2Count + r.WeakShortLohCount:N0} of {r.WeakShortGen0Count + r.WeakShortGen1Count + r.WeakShortGen2Count + r.WeakShortLohCount:N0} resolved.",
                    Recommendation: "WeakLong clears only after finalization completes. A large Gen2/LOH-concentrated WeakLong population can indicate finalized objects lingering — cross-check with the Finalizable Object Analysis section for finalization queue depth.",
                    Tags: ["gc-handle", "weak-handle", "finalization"],
                    MetricValue: weakLongGen2PlusFraction,
                    MetricUnit: "% weaklong-gen2-plus"));
            }
        }

        if (r.DependentHandleCount > 0)
        {
            FindingSeverity dependentSeverity = r.DependentUnresolvedPercent >= r.DependentUnresolvedPercentWarningThreshold
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: dependentSeverity,
                Title: "Dependent-handle retention summary",
                Evidence: $"Dependent handles: {r.DependentHandleCount:N0}; resolved source->target edges: {r.DependentResolvedEdgeCount:N0}; unresolved targets: {r.DependentUnresolvedTargetCount:N0} ({r.DependentUnresolvedPercent:F1}%).",
                Recommendation: "Inspect dominant dependent-handle source/target pairs to identify hidden retention relationships.",
                Tags: ["dependent-handle", "retention", "conditionalweaktable"],
                MetricValue: r.DependentUnresolvedPercent,
                MetricUnit: "% unresolved-targets"));
        }

        return findings;
    }

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
