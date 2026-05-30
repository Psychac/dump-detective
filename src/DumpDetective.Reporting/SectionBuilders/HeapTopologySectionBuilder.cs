using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>B3 — Heap Topology. Source: <see cref="HeapTopologyDomainResult"/>.</summary>
internal sealed class HeapTopologySectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Heap Topology";
    public string DisplayTitle => "Heap Topology";
    public int SortOrder => 300;

    public bool CanHandle(AnalyzerDomainResult result) => result is HeapTopologyDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (HeapTopologyDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total segments",  d.TotalSegments.ToString("N0"),           d.TotalSegments),
            KM("Committed bytes", FormatBytes(d.TotalCommittedBytes),      (double)d.TotalCommittedBytes),
            KM("Used bytes",      FormatBytes(d.TotalUsedBytes),           (double)d.TotalUsedBytes),
            KM("Reserved bytes",  FormatBytes(d.TotalReservedBytes),       (double)d.TotalReservedBytes),
            KM("Reservation gap", FormatBytes(d.ReservationGapBytes),     (double)d.ReservationGapBytes),
            KM("SOH bytes",       FormatBytes(d.SohBytes),                (double)d.SohBytes),
            KM("LOH bytes",       FormatBytes(d.LohBytes),                (double)d.LohBytes),
            KM("LOH %",           $"{d.LohPercent:F1}%",                   d.LohPercent),
            KM("POH bytes",       FormatBytes(d.PohBytes),                (double)d.PohBytes),
            KM("POH %",           $"{d.PohPercent:F1}%",                   d.PohPercent),
            KM("FOH bytes",       FormatBytes(d.FrozenBytes),             (double)d.FrozenBytes),
            KM("FOH %",           $"{d.FrozenPercent:F1}%",               d.FrozenPercent),
        };

        if (d.FrozenBytes > 100UL * 1024 * 1024)
            blocks.Add(T("Frozen object heap usage is above 100 MB; this often points to heavy immutable or interned data retention."));

        // Kind summary
        if (d.KindSummaries is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.KindSummaries.Count);
            for (int i = 0; i < d.KindSummaries.Count; i++)
            {
                SegmentKindSummary s = d.KindSummaries[i];
                if (s.SegmentCount == 0) continue;
                rows.Add(Row(
                    Cell(s.Kind.ToString()),
                    Cell(s.SegmentCount.ToString("N0"), s.SegmentCount),
                    Cell(s.ObjectCount >= 0 ? s.ObjectCount.ToString("N0") : "N/A", s.ObjectCount >= 0 ? s.ObjectCount : null),
                    Cell(FormatBytes(s.TotalBytes), (long)Math.Min(s.TotalBytes, long.MaxValue)),
                    Cell(FormatBytes(s.ReservedBytes), (long)Math.Min(s.ReservedBytes, long.MaxValue))));
            }
            if (rows.Count > 0)
                tables.Add(ST("Kind summary", ["Kind", "Segments", "Objects", "Committed", "Reserved"], rows));
        }

        // Per-logical-heap breakdown
        if (d.PerLogicalHeapSummaries.Count > 0)
        {
            var rows = new List<TableRow>(d.PerLogicalHeapSummaries.Count);
            ulong maxBytes = 0, minBytes = ulong.MaxValue;
            for (int i = 0; i < d.PerLogicalHeapSummaries.Count; i++)
            {
                PerLogicalHeapSummary heap = d.PerLogicalHeapSummaries[i];
                double share = d.TotalCommittedBytes == 0 ? 0.0 : heap.Bytes * 100.0 / d.TotalCommittedBytes;
                if (heap.Bytes > maxBytes) maxBytes = heap.Bytes;
                if (heap.Bytes < minBytes) minBytes = heap.Bytes;
                rows.Add(Row(
                    Cell(heap.LogicalHeapIndex.ToString("N0"), heap.LogicalHeapIndex),
                    Cell(FormatBytes(heap.Bytes), (long)Math.Min(heap.Bytes, long.MaxValue)),
                    Cell($"{share:F1}%"),
                    Cell(heap.ObjectCount >= 0 ? heap.ObjectCount.ToString("N0") : "N/A", heap.ObjectCount >= 0 ? heap.ObjectCount : null),
                    Cell(heap.SegmentCount.ToString("N0"), heap.SegmentCount)));
            }
            tables.Add(ST("Per logical heap", ["Heap", "Committed Bytes", "% of Total", "Objects", "Segments"], rows));
            if (d.PerLogicalHeapSummaries.Count > 1 && minBytes > 0 && maxBytes > minBytes * 2)
                blocks.Add(T("Warning: Logical heaps are skewed: largest heap is more than 2x the smallest."));
        }

        // Top segments by size
        if (d.TopSegmentsBySize is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopSegmentsBySize.Count);
            for (int i = 0; i < d.TopSegmentsBySize.Count; i++)
            {
                HeapSegmentSnapshot seg = d.TopSegmentsBySize[i];
                rows.Add(Row(
                    Cell($"0x{seg.Address:X}"),
                    Cell(seg.Kind.ToString()),
                    Cell(FormatBytes(seg.Length), (long)Math.Min(seg.Length, long.MaxValue)),
                    Cell(FormatBytes(seg.CommittedBytes), (long)Math.Min(seg.CommittedBytes, long.MaxValue)),
                    Cell(FormatBytes(seg.UsedBytes), (long)Math.Min(seg.UsedBytes, long.MaxValue)),
                    Cell(FormatBytes(seg.ReservedBytes), (long)Math.Min(seg.ReservedBytes, long.MaxValue)),
                    Cell(seg.Generation.ToString("N0"), seg.Generation),
                    Cell(seg.ObjectCount.ToString("N0"), seg.ObjectCount)));
            }
            tables.Add(ST("Top segments by size",
                ["Address", "Kind", "Length", "Committed", "Used", "Reserved", "Gen", "Objects"], rows));
        }

        // POH types
        if (d.TopPohTypes is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopPohTypes.Count);
            for (int i = 0; i < d.TopPohTypes.Count; i++)
            {
                TypeSnapshot t = d.TopPohTypes[i];
                rows.Add(Row(Cell(t.TypeName), Cell(t.Count.ToString("N0"), t.Count),
                    Cell(FormatBytes(t.TotalBytes), (long)Math.Min(t.TotalBytes, long.MaxValue)),
                    Cell(t.AverageSize > 0 ? FormatBytes(t.AverageSize) : "—")));
            }
            tables.Add(ST("POH types", ["Type", "Count", "Size", "Avg Size"], rows));
        }

        // Frozen types
        if (d.TopFrozenTypes is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopFrozenTypes.Count);
            for (int i = 0; i < d.TopFrozenTypes.Count; i++)
            {
                TypeSnapshot t = d.TopFrozenTypes[i];
                rows.Add(Row(Cell(t.TypeName), Cell(t.Count.ToString("N0"), t.Count),
                    Cell(FormatBytes(t.TotalBytes), (long)Math.Min(t.TotalBytes, long.MaxValue)),
                    Cell(t.AverageSize > 0 ? FormatBytes(t.AverageSize) : "—")));
            }
            tables.Add(ST("FOH types", ["Type", "Count", "Size", "Avg Size"], rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Heap Topology",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
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