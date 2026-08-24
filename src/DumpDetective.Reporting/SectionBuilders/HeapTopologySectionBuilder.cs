using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_segments"] = new NumericMetricValue(d.TotalSegments, MetricUnit.Count),
            ["committed_bytes"] = new NumericMetricValue((double)d.TotalCommittedBytes, MetricUnit.Bytes, FormatBytes(d.TotalCommittedBytes)),
            ["used_bytes"] = new NumericMetricValue((double)d.TotalUsedBytes, MetricUnit.Bytes, FormatBytes(d.TotalUsedBytes)),
            ["reserved_bytes"] = new NumericMetricValue((double)d.TotalReservedBytes, MetricUnit.Bytes, FormatBytes(d.TotalReservedBytes)),
            ["reservation_gap"] = new NumericMetricValue((double)d.ReservationGapBytes, MetricUnit.Bytes, FormatBytes(d.ReservationGapBytes)),
            ["gen0_bytes"] = new NumericMetricValue((double)d.Gen0Bytes, MetricUnit.Bytes, FormatBytes(d.Gen0Bytes)),
            ["gen1_bytes"] = new NumericMetricValue((double)d.Gen1Bytes, MetricUnit.Bytes, FormatBytes(d.Gen1Bytes)),
            ["gen2_bytes"] = new NumericMetricValue((double)d.Gen2Bytes, MetricUnit.Bytes, FormatBytes(d.Gen2Bytes)),
            ["soh_bytes"] = new NumericMetricValue((double)d.SohBytes, MetricUnit.Bytes, FormatBytes(d.SohBytes)),
            ["soh_fragmented"] = new NumericMetricValue((double)d.SohFragmentedBytes, MetricUnit.Bytes, FormatBytes(d.SohFragmentedBytes)),
            ["loh_bytes"] = new NumericMetricValue((double)d.LohBytes, MetricUnit.Bytes, FormatBytes(d.LohBytes)),
            ["loh_fragmented"] = new NumericMetricValue((double)d.LohFragmentedBytes, MetricUnit.Bytes, FormatBytes(d.LohFragmentedBytes)),
            ["loh_pct"] = new NumericMetricValue(d.LohPercent, MetricUnit.Percent, $"{d.LohPercent:F1}%"),
            ["poh_bytes"] = new NumericMetricValue((double)d.PohBytes, MetricUnit.Bytes, FormatBytes(d.PohBytes)),
            ["poh_fragmented"] = new NumericMetricValue((double)d.PohFragmentedBytes, MetricUnit.Bytes, FormatBytes(d.PohFragmentedBytes)),
            ["poh_pct"] = new NumericMetricValue(d.PohPercent, MetricUnit.Percent, $"{d.PohPercent:F1}%"),
            ["foh_bytes"] = new NumericMetricValue((double)d.FrozenBytes, MetricUnit.Bytes, FormatBytes(d.FrozenBytes)),
            ["foh_fragmented"] = new NumericMetricValue((double)d.FrozenFragmentedBytes, MetricUnit.Bytes, FormatBytes(d.FrozenFragmentedBytes)),
            ["foh_pct"] = new NumericMetricValue(d.FrozenPercent, MetricUnit.Percent, $"{d.FrozenPercent:F1}%"),
        };

        if (d.FrozenBytes > 100UL * 1024 * 1024)
            blocks.Add(T("Frozen object heap usage is above 100 MB; this often points to heavy immutable or interned data retention."));

        blocks.Add(T("Note: Used bytes shown above exclude SOH because SOH is never walked per-object; SOH object counts are derived exactly from Phase 1's heap-wide total instead. The Gen0/Gen1/Gen2 breakdown below accounts for all SOH allocations."));

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
                compactTables.Add(STCompact("Kind summary", new[] { CH("Kind"), CH("Segments","number"), CH("Objects"), CH("Committed","bytes"), CH("Reserved","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Per logical heap", new[] { CH("Heap"), CH("Committed Bytes","bytes"), CH("% of Total", "number", "percent"), CH("Objects","number"), CH("Segments","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                double objectDensity = seg.CommittedBytes > 0 && seg.ObjectCount > 0
                    ? (seg.ObjectCount * 1024.0 * 1024.0) / seg.CommittedBytes
                    : 0.0;
                rows.Add(Row(
                    Cell($"0x{seg.Address:X}"),
                    Cell(seg.Kind.ToString()),
                    Cell(FormatBytes(seg.Length), (long)Math.Min(seg.Length, long.MaxValue)),
                    Cell(FormatBytes(seg.CommittedBytes), (long)Math.Min(seg.CommittedBytes, long.MaxValue)),
                    Cell(FormatBytes(seg.UsedBytes), (long)Math.Min(seg.UsedBytes, long.MaxValue)),
                    Cell(FormatBytes(seg.ReservedBytes), (long)Math.Min(seg.ReservedBytes, long.MaxValue)),
                    Cell(seg.Generation.ToString("N0"), seg.Generation),
                    Cell(seg.ObjectCount.ToString("N0"), seg.ObjectCount),
                    Cell(objectDensity > 0 ? $"{objectDensity:F1}" : "—", objectDensity > 0 ? objectDensity : null)));
            }
            compactTables.Add(STCompact("Top segments by size",
                new[] { CH("Address"), CH("Kind"), CH("Length","bytes"), CH("Committed","bytes"), CH("Used","bytes"), CH("Reserved","bytes"), CH("Gen","number"), CH("Objects","number"), CH("Density","objects/MB") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("POH types", new[] { CH("Type"), CH("Count","number"), CH("Size","bytes"), CH("Avg Size","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("FOH types", new[] { CH("Type"), CH("Count","number"), CH("Size","bytes"), CH("Avg Size","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Heap Topology",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
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