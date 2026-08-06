using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        SectionLeadFinding? leadFinding = null;
        if (d.ReservedToCommittedRatio > d.RatioHighPressureThreshold || (d.AddressSpacePressureRisk && d.ReservedToCommittedRatio > d.RatioMediumPressureThreshold))
        {
            bool critical = d.ReservedToCommittedRatio > d.RatioHighPressureThreshold;
            string reason = d.AddressSpacePressureRisk && !string.IsNullOrWhiteSpace(d.PressureRiskReason)
                ? d.PressureRiskReason
                : $"Reserved/committed ratio is {d.ReservedToCommittedRatio:F1}\u00d7.";
            leadFinding = new SectionLeadFinding(
                Severity: critical ? "Critical" : "Warning",
                Title: $"Address space pressure \u2014 reserved/committed ratio {d.ReservedToCommittedRatio:F1}\u00d7",
                Summary: reason,
                Recommendation: "Review segment reservation settings. On Server GC, consider reducing MaxHeapSize or enabling DATAS. On Workstation GC, check for LOH fragmentation or large pinned regions.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.85,
                Caveats: []);
        }
        else if (d.ReservedToCommittedRatio > d.RatioMediumPressureThreshold)
        {
            string reason = d.AddressSpacePressureRisk && !string.IsNullOrWhiteSpace(d.PressureRiskReason)
                ? d.PressureRiskReason
                : $"Reserved/committed ratio is {d.ReservedToCommittedRatio:F1}\u00d7 (threshold: {d.RatioMediumPressureThreshold:F0}\u00d7).";
            leadFinding = new SectionLeadFinding(
                Severity: "Warning",
                Title: $"Elevated segment reservation \u2014 ratio {d.ReservedToCommittedRatio:F1}\u00d7",
                Summary: reason,
                Recommendation: "Monitor heap reservation growth. Reduce MaxHeapSize or consolidate heap segments if address space is constrained.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.85,
                Caveats: []);
        }

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_committed_bytes"] = new NumericMetricValue((double)d.TotalCommittedBytes, MetricUnit.Bytes),
            ["total_reserved_bytes"] = new NumericMetricValue((double)d.TotalReservedBytes, MetricUnit.Bytes),
            ["reservation_gap_bytes"] = new NumericMetricValue((double)d.ReservationGapBytes, MetricUnit.Bytes),
            ["reserved_committed_ratio"] = new NumericMetricValue(d.ReservedToCommittedRatio, MetricUnit.Ratio),
            ["ephemeral_segments"] = new NumericMetricValue(d.EphemeralSegmentCount, MetricUnit.Count),
            ["avg_ephemeral_fill_pct"] = new NumericMetricValue(d.AvgEphemeralFillPct, MetricUnit.Percent),
            ["non_ephemeral_soh_segs"] = new NumericMetricValue(d.NonEphemeralSohSegmentCount, MetricUnit.Count),
            ["address_space_pressure"] = new TextMetricValue(d.AddressSpacePressureRisk ? "Yes" : "No"),
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
                string fillPctDisplay = seg.IsEphemeral ? $"{seg.FillPct:F1}%" : "—";
                rows.Add(Row(
                    Cell($"0x{seg.Address:X}"),
                    Cell(seg.Kind.ToString()),
                    Cell(FormatBytes(seg.CommittedBytes), (long)Math.Min(seg.CommittedBytes, long.MaxValue)),
                    Cell(FormatBytes(seg.ReservedBytes),  (long)Math.Min(seg.ReservedBytes,  long.MaxValue)),
                    Cell(seg.IsEphemeral ? "Yes" : "No"),
                    Cell(seg.LogicalHeap.ToString("N0"), seg.LogicalHeap),
                    Cell(fillPctDisplay)));
            }
            compactTables.Add(STCompact("Segment table",
                new[] { CH("Address"), CH("Kind"), CH("Committed","bytes"), CH("Reserved","bytes"), CH("Ephemeral"), CH("Logical Heap","number"), CH("Fill %", "number", "percent") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ReservedByLogicalHeap.Count > 0)
        {
            var heapRows = new List<TableRow>(d.ReservedByLogicalHeap.Count);
            foreach (KeyValuePair<int, ulong> kvp in d.ReservedByLogicalHeap.OrderBy(kvp => kvp.Key))
                heapRows.Add(Row(
                    Cell(kvp.Key.ToString("N0"), kvp.Key),
                    Cell(FormatBytes(kvp.Value), (long)Math.Min(kvp.Value, long.MaxValue))));
            compactTables.Add(STCompact("Reserved by logical heap", new[] { CH("Logical Heap","number"), CH("Reserved Bytes","bytes") }, heapRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }
        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = new[] { "B", "KB", "MB", "GB", "TB" };
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1) { bytes /= 1024; unitIndex++; }
        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}
