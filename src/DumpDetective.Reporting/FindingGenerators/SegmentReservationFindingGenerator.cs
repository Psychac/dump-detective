using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class SegmentReservationFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Segment Reservation Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is SegmentReservationDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not SegmentReservationDomainResult r) return [];

        var findings = new List<InsightFinding>();

        // Address space pressure.
        if (r.AddressSpacePressureRisk)
        {
            bool is32Bit = r.DumpPointerSize == 4;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: is32Bit ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title: "Address space pressure risk detected",
                Evidence: r.PressureRiskReason,
                Recommendation: is32Bit
                    ? "Migrate to a 64-bit process to avoid virtual address exhaustion."
                    : "Investigate GC segment reservation policy; consider tuning System.GC.HeapHardLimit in runtimeconfig.json.",
                Tags: ["segments", "virtual-memory", "address-space"],
                MetricValue: r.ReservedToCommittedRatio,
                MetricUnit: "ratio"));
        }

        // Ephemeral segment fill critical (> 90 %).
        if (r.AvgEphemeralFillPct > 90.0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Ephemeral segment fill critically high",
                Evidence: $"Average ephemeral fill is {r.AvgEphemeralFillPct:F1}% across {r.EphemeralSegmentCount} ephemeral segment(s). " +
                          $"New GC segments will be allocated shortly.",
                Recommendation: "Reduce Gen0/Gen1 allocation rate or increase GC collection frequency.",
                Tags: ["segments", "ephemeral", "gen0", "gc"],
                MetricValue: r.AvgEphemeralFillPct,
                MetricUnit: "%"));
        }

        // Near-empty GC regions (.NET 8+ regions-based GC) — decommit candidates.
        if (r.IsRegionsBased && r.NearEmptyRegionCount > 0)
        {
            // 50 MB — below this the near-empty committed total isn't worth flagging above Info;
            // no external standard behind this number, picked as a "worth a human looking at it" line.
            const ulong WarningReclaimableBytesThreshold = 50 * 1024 * 1024UL;
            bool warning = r.NearEmptyRegionCommittedBytes > WarningReclaimableBytesThreshold;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: warning ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Near-empty GC regions detected",
                Evidence: $"{r.NearEmptyRegionCount} region(s) below {r.NearEmptyRegionFillPctThreshold:F0}% fill, holding {FormatHelper.FormatBytes(r.NearEmptyRegionCommittedBytes)} of committed memory.",
                Recommendation: "Regions-based GC (DATAS) can decommit near-empty regions over time; if this persists across snapshots, investigate pinned or long-lived objects preventing region evacuation.",
                Tags: ["segments", "regions", "gc-regions", "decommit"],
                MetricValue: (double)r.NearEmptyRegionCommittedBytes,
                MetricUnit: "bytes"));
        }

        // Summary finding (always).
        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "Segment reservation overview",
            Evidence: $"Total committed: {FormatHelper.FormatBytes(r.TotalCommittedBytes)}, " +
                      $"reserved: {FormatHelper.FormatBytes(r.TotalReservedBytes)}, " +
                      $"gap: {FormatHelper.FormatBytes(r.ReservationGapBytes)} " +
                      $"(ratio {r.ReservedToCommittedRatio:F1}x). " +
                      $"Logical heaps: {r.ReservedByLogicalHeap.Count}.",
            Recommendation: r.ReservedToCommittedRatio > 3.0
                ? "Reserved-to-committed ratio is elevated. Monitor for virtual address fragmentation."
                : "Reservation profile is within normal range.",
            Tags: ["segments", "reserved", "committed"],
            MetricValue: (double)r.TotalReservedBytes,
            MetricUnit: "bytes"));

        return findings;
    }
}
