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

        // Logical-heap skew: promoted from an inline report text block to a real InsightFinding
        // (see docs/analysis/phase1/heap-topology-analyzer-audit.md P3 item #13) so it carries
        // severity/tags/MetricValue and is trend-tracked and ranked alongside other findings.
        if (r.PerLogicalHeapSummaries.Count > 1)
        {
            ulong maxHeapBytes = 0, minHeapBytes = ulong.MaxValue;
            for (int i = 0; i < r.PerLogicalHeapSummaries.Count; i++)
            {
                ulong bytes = r.PerLogicalHeapSummaries[i].Bytes;
                if (bytes > maxHeapBytes) maxHeapBytes = bytes;
                if (bytes < minHeapBytes) minHeapBytes = bytes;
            }

            if (minHeapBytes > 0 && maxHeapBytes > minHeapBytes * 2)
            {
                double skewRatio = maxHeapBytes / (double)minHeapBytes;
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: FindingSeverity.Warning,
                    Title: $"Logical heaps are skewed ({skewRatio:F1}x)",
                    Evidence: $"Largest logical heap ({FormatBytes(maxHeapBytes)}) is {skewRatio:F1}x the smallest " +
                              $"({FormatBytes(minHeapBytes)}) across {r.PerLogicalHeapSummaries.Count} logical heaps" +
                              (r.IsServerGc ? " (Server GC)." : "."),
                    Recommendation: r.IsServerGc
                        ? "Uneven per-CPU heap sizes on Server GC usually indicate skewed allocation patterns " +
                          "across threads/cores. Review thread-affinity of large allocators."
                        : "Uneven logical heap sizes are unexpected outside Server GC; verify GC mode and segment classification.",
                    Tags: ["segments", "skew", "server-gc", "memory"],
                    MetricValue: skewRatio,
                    MetricUnit: "x"));
            }
        }

        // Unrecognized segment kind: SegmentKindMapper no longer silently folds an unrecognized
        // ClrSegment.Kind into SOH (see docs/analysis/phase1/heap-topology-analyzer-audit.md P3
        // item #16); surface it here since it usually signals a corrupted dump or a ClrMD version
        // this analyzer hasn't been updated for.
        for (int i = 0; i < r.KindSummaries.Count; i++)
        {
            SegmentKindSummary kindSummary = r.KindSummaries[i];
            if (kindSummary.Kind != HeapSegmentKind.Unknown || kindSummary.SegmentCount == 0)
                continue;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"{kindSummary.SegmentCount} segment(s) with an unrecognized kind",
                Evidence: $"{kindSummary.SegmentCount} segment(s) totaling {FormatBytes(kindSummary.TotalBytes)} did not match any " +
                          "known GC segment kind (SOH/LOH/POH/Frozen).",
                Recommendation: "This usually indicates a corrupted dump or a ClrMD version this analyzer hasn't been " +
                                "updated for. Verify the dump captured cleanly and cross-check with `!eeheap -gc` in WinDbg.",
                Tags: ["segments", "unknown", "corruption", "memory"],
                MetricValue: kindSummary.SegmentCount,
                MetricUnit: "segments"));
            break;
        }

        // ── Per-kind fragmentation analysis ──────────────────────────────────────────
        // Committed-reserved gaps are normal on large Server GC deployments where memory growth
        // outpaces usage. Focus on per-kind fragmentation where actionable, with attribution
        // specific to each segment kind's collection behavior.

        // SOH fragmentation: unlike LOH, the GC compacts SOH during Gen2 collections, so
        // persistent free space usually means pinned handles are blocking compaction rather than
        // requiring a manual compaction trigger.
        if (r.SohBytes > 0 && r.SohFragmentedBytes > 0)
        {
            double sohFragPct = r.SohFragmentedBytes * 100.0 / r.SohBytes;
            if (sohFragPct >= 25.0)
            {
                FindingSeverity sohFragSev = sohFragPct >= 45.0 ? FindingSeverity.Warning : FindingSeverity.Info;
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: sohFragSev,
                    Title: $"SOH fragmentation {sohFragPct:F1}%",
                    Evidence: $"Small Object Heap has {FormatBytes(r.SohFragmentedBytes)} of {FormatBytes(r.SohBytes)} committed free.",
                    Recommendation: "SOH is compacted automatically during Gen2 collections, so persistent free space " +
                                    "usually indicates pinned handles blocking compaction. Review GCHandle.Alloc(Pinned) " +
                                    "usage and interop buffers rather than expecting manual compaction to help.",
                    Tags: ["fragmentation", "soh", "memory", "gc"],
                    MetricValue: sohFragPct,
                    MetricUnit: "%"));
            }
        }

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

        // Frozen fragmentation: frozen segments hold read-only/immutable data (interned strings,
        // precompiled metadata) that is never collected, so free space here is not reclaimable
        // garbage — it is informational address-space overhead, not a leak signal.
        if (r.FrozenBytes > 0 && r.FrozenFragmentedBytes > 0)
        {
            double frozenFragPct = r.FrozenFragmentedBytes * 100.0 / r.FrozenBytes;
            if (frozenFragPct >= 30.0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: FindingSeverity.Info,
                    Title: $"Frozen heap fragmentation {frozenFragPct:F1}%",
                    Evidence: $"Frozen Object Heap has {FormatBytes(r.FrozenFragmentedBytes)} of {FormatBytes(r.FrozenBytes)} committed free.",
                    Recommendation: "Frozen segments hold immutable data that is never collected; this is address-space " +
                                    "overhead rather than reclaimable fragmentation and typically requires no action.",
                    Tags: ["fragmentation", "frozen", "memory"],
                    MetricValue: frozenFragPct,
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
