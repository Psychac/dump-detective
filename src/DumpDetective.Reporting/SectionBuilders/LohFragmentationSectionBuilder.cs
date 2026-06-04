using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Text.Json;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class LohFragmentationSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "LOH Fragmentation Analysis";
    public string DisplayTitle => "LOH Fragmentation";
    public int SortOrder => 400;

    public bool CanHandle(AnalyzerDomainResult result) => result is LohFragmentationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (LohFragmentationDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["segment_count"] = new NumericMetricValue(d.SegmentCount, MetricUnit.Count),
            ["total_loh_bytes"] = new NumericMetricValue((double)d.TotalBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalBytes)),
            ["used_bytes"] = new NumericMetricValue((double)d.UsedBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.UsedBytes)),
            ["free_bytes"] = new NumericMetricValue((double)d.FreeBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.FreeBytes)),
            ["free_blocks"] = new NumericMetricValue(d.FreeBlockCount, MetricUnit.Count),
            ["overall_fragmentation_pct"] = new NumericMetricValue(d.FragmentationPercent, MetricUnit.Percent, $"{d.FragmentationPercent:F1}%"),
            ["largest_free_block"] = new NumericMetricValue((double)d.LargestFreeBlock, MetricUnit.Bytes, FormatHelper.FormatBytes(d.LargestFreeBlock)),
            ["severity_band"] = new TextMetricValue(GetSeverityBand(d.FragmentationPercent)),
        };

        var segments = d.TopFragmentedSegments ?? [];
        if (segments.Count > 0)
        {
            var segRows = new List<TableRow>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                segRows.Add(new TableRow([
                    Cell($"0x{s.Address:x16}"),
                    Cell(FormatHelper.FormatBytes(d.TotalBytes / (ulong)Math.Max(1, d.SegmentCount))),
                    Cell($"{s.FragmentationPercent:F1}%", (long)(s.FragmentationPercent * 100)),
                    Cell(FormatHelper.FormatBytes(s.LargestFreeBlock), (long)s.LargestFreeBlock)]));
            }
            tables.Add(ST("Top fragmented segments", ["Address", "Size", "Frag %", "Largest Free Block"], segRows));

            var heatmapItems = new List<object>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                heatmapItems.Add(new { label = $"0x{s.Address:x16}", value = s.FragmentationPercent });
            }
            blocks.Add(Chart(
                "LOH fragmentation heatmap",
                "heatmap",
                JsonSerializer.Serialize(new
                {
                    title = "Top fragmented segments",
                    subtitle = "Higher values indicate more free-space fragmentation",
                    items = heatmapItems
                })));
        }

        if (d.FragmentationPercent >= 40)
            blocks.Add(T("LOH fragmentation is critically high — compaction or large-object pooling recommended."));
        else if (d.FragmentationPercent >= 20)
            blocks.Add(T("LOH fragmentation is elevated — monitor allocation patterns."));
        else
            blocks.Add(T("LOH fragmentation is within normal range."));

        var histogram = d.FreeGapHistogram ?? [];
        if (histogram.Count > 0)
        {
            var hRows = new List<TableRow>(histogram.Count);
            int totalGaps = 0;
            for (int i = 0; i < histogram.Count; i++) totalGaps += histogram[i].GapCount;
            for (int i = 0; i < histogram.Count; i++)
            {
                var bucket = histogram[i];
                double pct = totalGaps == 0 ? 0 : bucket.GapCount * 100.0 / totalGaps;
                hRows.Add(new TableRow([
                    Cell(bucket.GapSizeRange),
                    Cell($"{bucket.GapCount:N0}", bucket.GapCount),
                    Cell($"{pct:F1}%")]));
            }
            tables.Add(ST("Free-gap size distribution", ["Gap Size Range", "Count", "% of Gaps"], hRows));
        }

        var largeObjects = d.TopLargeObjects ?? [];
        if (largeObjects.Count > 0)
        {
            var loRows = new List<TableRow>(largeObjects.Count);
            for (int i = 0; i < largeObjects.Count; i++)
            {
                var lo = largeObjects[i];
                loRows.Add(new TableRow([
                    Cell(lo.TypeName),
                    Cell(FormatHelper.FormatBytes(lo.Size), (long)lo.Size),
                    Cell($"0x{lo.Address:x16}")]));
            }
            tables.Add(ST("Top large objects by size", ["Type", "Size", "Address"], loRows));
        }

        SectionLeadFinding? leadFinding = null;
        if (d.FragmentationPercent > 60)
            leadFinding = new SectionLeadFinding(
                Severity: "Critical",
                Title: $"Severe LOH fragmentation — {d.FragmentationPercent:F1}% of LOH is free space",
                    Summary: $"Total LOH: {FormatHelper.FormatBytes(d.TotalBytes)}, free: {FormatHelper.FormatBytes(d.FreeBytes)} ({d.FreeBlockCount:N0} free blocks). Largest free block: {FormatHelper.FormatBytes(d.LargestFreeBlock)}.",
                Recommendation: "Compact the LOH via GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true), or pool large objects (ArrayPool<T>/MemoryPool<T>) to reduce allocation churn.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.9,
                Caveats: []);
        else if (d.FragmentationPercent > 30)
            leadFinding = new SectionLeadFinding(
                Severity: "Warning",
                Title: $"Elevated LOH fragmentation — {d.FragmentationPercent:F1}% free-space fragmentation",
                    Summary: $"Total LOH: {FormatHelper.FormatBytes(d.TotalBytes)}, free: {FormatHelper.FormatBytes(d.FreeBytes)} ({d.FreeBlockCount:N0} free blocks). Largest free block: {FormatHelper.FormatBytes(d.LargestFreeBlock)}.",
                Recommendation: "Monitor LOH allocation patterns. Consider using ArrayPool<T> or MemoryPool<T> for large buffers to reduce fragmentation over time.",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.9,
                Caveats: []);

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static string GetSeverityBand(double fragmentationPercent)
    {
        if (fragmentationPercent > 60)
            return "Critical (> 60%)";

        if (fragmentationPercent > 30)
            return "Warning (30%\u201360%)";

        return "OK (\u2264 30%)";
    }
}
