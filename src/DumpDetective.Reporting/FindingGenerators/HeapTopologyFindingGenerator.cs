using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class HeapTopologyFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Heap Topology";
    public bool CanGenerate(AnalyzerDomainResult result) => result is HeapTopologyDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not HeapTopologyDomainResult r) return [];

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

        // ── Per-kind fragmentation analysis ──────────────────────────────────────────
        // Committed-reserved gaps are normal on large Server GC deployments where memory growth
        // outpaces usage. Focus on per-kind fragmentation where actionable.

        // LOH fragmentation: pinning and LOH compaction are relevant.
        if (r.LohBytes > 0)
        {
            double lohFragPct = r.LohFragmentedBytes * 100.0 / r.LohBytes;
            if (lohFragPct >= 30.0)
            {
                FindingSeverity lohFragSev = lohFragPct >= 50.0 ? FindingSeverity.Critical : FindingSeverity.Warning;
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: lohFragSev,
                    Title: $"LOH fragmentation {lohFragPct:F1}%",
                    Evidence: $"Large Object Heap has {FormatBytes(r.LohFragmentedBytes)} of {FormatBytes(r.LohBytes)} committed free. " +
                              $"This indicates LOH segments with reclaimed space not yet compacted.",
                    Recommendation: "Enable LOH compaction via GCSettings.LargeObjectHeapCompactionMode = " +
                                    "GCLargeObjectHeapCompactionMode.Default or Aggressive, or reduce large object allocations.",
                    Tags: ["fragmentation", "loh", "memory", "gc"],
                    MetricValue: lohFragPct,
                    MetricUnit: "%"));
            }
        }

        // POH fragmentation: pinning directly.
        if (r.PohBytes > 0 && r.PohFragmentedBytes > 0)
        {
            double pohFragPct = r.PohFragmentedBytes * 100.0 / r.PohBytes;
            if (pohFragPct >= 20.0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: FindingSeverity.Info,
                    Title: $"POH fragmentation {pohFragPct:F1}%",
                    Evidence: $"Pinned Object Heap has {FormatBytes(r.PohFragmentedBytes)} of {FormatBytes(r.PohBytes)} committed free. " +
                              $"This is typical for pinned allocations.",
                    Recommendation: "Review pinned buffer pools and consider pooling strategies to reduce allocations.",
                    Tags: ["fragmentation", "poh", "pinning", "memory"],
                    MetricValue: pohFragPct,
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
