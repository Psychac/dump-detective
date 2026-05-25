using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>B5 — Segment Reservation &amp; Virtual Memory. Source: <see cref="SegmentReservationDomainResult"/>.</summary>
internal sealed class SegmentReservationSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Segment Reservation Analysis";
    public string DisplayTitle => "Segment Reservation & Virtual Memory";
    public int SortOrder => 500; // §B5

    public bool CanHandle(AnalyzerDomainResult result) => result is SegmentReservationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (SegmentReservationDomainResult)result;

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        SectionLeadFinding? leadFinding = null;
        if (d.ReservedToCommittedRatio > 10.0 || (d.AddressSpacePressureRisk && d.ReservedToCommittedRatio > 4.0))
        {
            bool critical = d.ReservedToCommittedRatio > 10.0;
            string reason = d.AddressSpacePressureRisk && !string.IsNullOrWhiteSpace(d.PressureRiskReason)
                ? d.PressureRiskReason
                : $"Reserved/committed ratio is {d.ReservedToCommittedRatio:F1}\u00d7.";
            leadFinding = new SectionLeadFinding(
                Severity: critical ? "Critical" : "Warning",
                Title: $"Address space pressure \u2014 reserved/committed ratio {d.ReservedToCommittedRatio:F1}\u00d7",
                Evidence: reason,
                Recommendation: "Review segment reservation settings. On Server GC, consider reducing MaxHeapSize or enabling DATAS. On Workstation GC, check for LOH fragmentation or large pinned regions.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.85,
                Caveats: []);
        }
        else if (d.ReservedToCommittedRatio > 4.0)
        {
            string reason = d.AddressSpacePressureRisk && !string.IsNullOrWhiteSpace(d.PressureRiskReason)
                ? d.PressureRiskReason
                : $"Reserved/committed ratio is {d.ReservedToCommittedRatio:F1}\u00d7 (threshold: 4\u00d7).";
            leadFinding = new SectionLeadFinding(
                Severity: "Warning",
                Title: $"Elevated segment reservation \u2014 ratio {d.ReservedToCommittedRatio:F1}\u00d7",
                Evidence: reason,
                Recommendation: "Monitor heap reservation growth. Reduce MaxHeapSize or consolidate heap segments if address space is constrained.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.85,
                Caveats: []);
        }

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total committed",         FormatBytes(d.TotalCommittedBytes),     (double)d.TotalCommittedBytes),
            KM("Total reserved",          FormatBytes(d.TotalReservedBytes),      (double)d.TotalReservedBytes),
            KM("Reservation gap",         FormatBytes(d.ReservationGapBytes),     (double)d.ReservationGapBytes),
            KM("Reserved / committed",    $"{d.ReservedToCommittedRatio:F2}×",   d.ReservedToCommittedRatio),
            KM("Ephemeral segments",      d.EphemeralSegmentCount.ToString("N0"), d.EphemeralSegmentCount),
            KM("Avg ephemeral fill",      $"{d.AvgEphemeralFillPct:F1}%",        d.AvgEphemeralFillPct),
            KM("Non-ephemeral SOH segs",  d.NonEphemeralSohSegmentCount.ToString("N0"), d.NonEphemeralSohSegmentCount),
            KM("Address space pressure",  d.AddressSpacePressureRisk ? "Yes" : "No", d.AddressSpacePressureRisk ? 1.0 : 0.0),
        };

        if (d.AddressSpacePressureRisk && !string.IsNullOrWhiteSpace(d.PressureRiskReason))
            blocks.Add(T($"Pressure reason: {d.PressureRiskReason}"));

        if (d.SegmentTable.Count > 0)
        {
            int limit = Math.Min(d.SegmentTable.Count, 30);
            var rows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                SegmentReservationEntry seg = d.SegmentTable[i];
                rows.Add(Row(
                    Cell($"0x{seg.Address:X}"),
                    Cell(seg.Kind.ToString()),
                    Cell(FormatBytes(seg.CommittedBytes), (long)Math.Min(seg.CommittedBytes, long.MaxValue)),
                    Cell(FormatBytes(seg.ReservedBytes),  (long)Math.Min(seg.ReservedBytes,  long.MaxValue)),
                    Cell(seg.IsEphemeral ? "Yes" : "No"),
                    Cell(seg.LogicalHeap.ToString("N0"), seg.LogicalHeap),
                    Cell($"{seg.FillPct:F1}%")));
            }
            tables.Add(ST("Segment table",
                ["Address", "Kind", "Committed", "Reserved", "Ephemeral", "Logical Heap", "Fill %"],
                rows));
        }

        if (d.ReservedByLogicalHeap.Count > 0)
        {
            var heapRows = new List<TableRow>(d.ReservedByLogicalHeap.Count);
            foreach (KeyValuePair<int, ulong> kvp in d.ReservedByLogicalHeap.OrderBy(kvp => kvp.Key))
                heapRows.Add(Row(
                    Cell(kvp.Key.ToString("N0"), kvp.Key),
                    Cell(FormatBytes(kvp.Value), (long)Math.Min(kvp.Value, long.MaxValue))));
            tables.Add(ST("Reserved by logical heap", ["Logical Heap", "Reserved Bytes"], heapRows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1) { bytes /= 1024; unitIndex++; }
        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}
