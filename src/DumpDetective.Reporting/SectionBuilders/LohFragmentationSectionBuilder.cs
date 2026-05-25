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
    public int SortOrder => 55;

    public bool CanHandle(AnalyzerDomainResult result) => result is LohFragmentationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (LohFragmentationDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total LOH Bytes",        FormatHelper.FormatBytes(d.TotalBytes),          (double)d.TotalBytes),
            KM("Segment Count",          $"{d.SegmentCount:N0}",                          d.SegmentCount),
            KM("Overall Fragmentation",  $"{d.FragmentationPercent:F1}%",                 d.FragmentationPercent),
            KM("Free Bytes",             FormatHelper.FormatBytes(d.FreeBytes),           (double)d.FreeBytes),
            KM("Free Blocks",            $"{d.FreeBlockCount:N0}",                        d.FreeBlockCount),
            KM("Largest Free Block",     FormatHelper.FormatBytes(d.LargestFreeBlock),    (double)d.LargestFreeBlock),
            KM("Severity Band",          GetSeverityBand(d.FragmentationPercent)),
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

        return new AnalyzerDetailSection(
            AnalyzerName, AnalyzerName, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static string GetSeverityBand(double fragmentationPercent)
    {
        if (fragmentationPercent >= 40)
            return "Critical (>= 40%)";

        if (fragmentationPercent >= 20)
            return "Warning (20% to 39.9%)";

        return "OK (< 20%)";
    }
}
