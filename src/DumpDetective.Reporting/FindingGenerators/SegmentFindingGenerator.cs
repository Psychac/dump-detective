using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class SegmentFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Segment Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is SegmentAnalysisDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not SegmentAnalysisDomainResult r) return [];

        var findings = new List<InsightFinding>();

        FindingSeverity lohSeverity = r.LohPercent >= 40 ? FindingSeverity.Critical
            : r.LohPercent >= 25 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Memory",
            Severity: lohSeverity,
            Title: "Heap segment distribution snapshot",
            Evidence: $"Total committed: {r.TotalCommittedBytes / (1024 * 1024):N0} MB across {r.TotalSegments} segments. " +
                      $"SOH: {r.SohBytes / (1024 * 1024):N0} MB ({r.SohSegmentCount} segs), " +
                      $"LOH: {r.LohBytes / (1024 * 1024):N0} MB ({r.LohSegmentCount} segs, {r.LohPercent:F1}%), " +
                      $"POH: {r.PohBytes / (1024 * 1024):N0} MB ({r.PohSegmentCount} segs, {r.PohPercent:F1}%).",
            Recommendation: lohSeverity >= FindingSeverity.Warning
                ? "LOH is large relative to total heap. Investigate large object allocations and LOH fragmentation."
                : "Segment distribution appears within expected range.",
            Tags: ["segments", "loh", "poh", "soh", "memory"],
            MetricValue: r.LohPercent,
            MetricUnit: "%"));

        if (r.PohPercent >= 10)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Elevated Pinned Object Heap (POH) usage",
                Evidence: $"POH occupies {r.PohPercent:F1}% ({r.PohBytes / (1024 * 1024):N0} MB) of committed heap across {r.PohSegmentCount} segments.",
                Recommendation: "High POH usage can increase GC pause times. Review pinned buffer pools and interop code.",
                Tags: ["segments", "poh", "pinned", "gc"],
                MetricValue: r.PohPercent,
                MetricUnit: "%"));
        }

        // ── Total heap fragmentation ──────────────────────────────────────────
        // Free space dispersed across segments prevents GC from reclaiming it in aggregate.
        if (r.TotalCommittedBytes > 0 && r.TotalUsedBytes < r.TotalCommittedBytes)
        {
            double heapFragPct = (r.TotalCommittedBytes - r.TotalUsedBytes) * 100.0 / r.TotalCommittedBytes;
            if (heapFragPct >= 30.0)
            {
                FindingSeverity fragSev = heapFragPct >= 50.0 ? FindingSeverity.Critical : FindingSeverity.Warning;
                ulong freeBytes = r.TotalCommittedBytes - r.TotalUsedBytes;

                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: fragSev,
                    Title: $"Heap fragmentation {heapFragPct:F1}%",
                    Evidence: $"{FormatBytes(freeBytes)} free but fragmented across segments " +
                              $"(committed: {FormatBytes(r.TotalCommittedBytes)}, " +
                              $"used: {FormatBytes(r.TotalUsedBytes)}). " +
                              $"Gen0: {FormatBytes(r.SohBytes)} SOH | LOH: {FormatBytes(r.LohBytes)}.",
                    Recommendation: "Reduce GCHandle.Alloc(Pinned) and use Memory<T> for I/O buffers. " +
                                    "Pinned objects prevent GC from compacting segments and are the primary " +
                                    "cause of total heap fragmentation. Enable LOH compaction via " +
                                    "GCSettings.LargeObjectHeapCompactionMode for LOH free-space.",
                    Tags: ["fragmentation", "memory", "gc", "pinning"],
                    MetricValue: heapFragPct,
                    MetricUnit: "%"));
            }
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024UL) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
