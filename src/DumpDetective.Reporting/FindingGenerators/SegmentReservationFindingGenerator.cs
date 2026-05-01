using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
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
            bool is32Bit = IntPtr.Size == 4;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: is32Bit ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title: "Address space pressure risk detected",
                Evidence: r.PressureRiskReason,
                Recommendation: is32Bit
                    ? "Migrate to a 64-bit process to avoid virtual address exhaustion."
                    : "Investigate GC segment reservation policy; consider COMPLUS_GCSegmentSize tuning.",
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
