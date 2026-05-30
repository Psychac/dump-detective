using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Text.Json;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>A2 — Memory Overview. Source: <see cref="MemoryDomainResult"/>.</summary>
internal sealed class MemoryAnalysisSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Memory Analysis";
    public string DisplayTitle => "Memory Overview";
    public int SortOrder => 200;

    public bool CanHandle(AnalyzerDomainResult result) => result is MemoryDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (MemoryDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Memory pressure", d.MemoryPressureScore.ToString("F1"), d.MemoryPressureScore),
            KM("Total bytes",    FormatHelper.FormatBytes(d.TotalBytes),   (double)d.TotalBytes),
            KM("Total objects",  d.TotalObjects.ToString("N0"),             d.TotalObjects),
            KM("Unique types",   d.UniqueTypes.ToString("N0"),              d.UniqueTypes),
            KM("LOH bytes",      FormatHelper.FormatBytes(d.LohBytes),     (double)d.LohBytes),
            KM("LOH %",          $"{d.LohPercent:F1}%",                    d.LohPercent),
            KM("Top 5 share",    $"{d.Top5BytesPercent:F1}%",              d.Top5BytesPercent),
            KM("<256 B objects", $"{d.SmallObjectCountPercent:F1}%",       d.SmallObjectCountPercent),
            KM("Objects / MB",   d.ObjectsPerMb.ToString("F1"),            d.ObjectsPerMb),
        };

        string pressureBand = d.MemoryPressureScore >= 75 ? "High"
            : d.MemoryPressureScore >= 50 ? "Medium"
            : "Low";
        blocks.Add(T($"Memory pressure score: {d.MemoryPressureScore:F1}/100 ({pressureBand})."));

        if (d.TopTypes.Count > 0)
        {
            int chartLimit = Math.Min(12, d.TopTypes.Count);
            var items = new object[chartLimit];
            for (int i = 0; i < chartLimit; i++)
            {
                TypeSnapshot t = d.TopTypes[i];
                items[i] = new { label = t.TypeName, value = t.TotalBytes };
            }

            blocks.Add(Chart(
                "Top types by memory",
                "rankedbar",
                JsonSerializer.Serialize(new
                {
                    title = "Top types by memory",
                    items
                })));
        }

        if (d.SizeBucketHistogram is { Count: > 0 })
        {
            var histItems = new List<object>(d.SizeBucketHistogram.Count);
            for (int i = 0; i < d.SizeBucketHistogram.Count; i++)
            {
                SizeBucketEntry b = d.SizeBucketHistogram[i];
                if (b.ObjectCount <= 0)
                    continue;

                histItems.Add(new
                {
                    label = b.RangeLabel,
                    value = b.ObjectCount
                });
            }

            if (histItems.Count > 0)
            {
                blocks.Add(Chart(
                    "Object size histogram",
                    "histogram",
                    JsonSerializer.Serialize(new
                    {
                        title = "Object size histogram (by object count)",
                        items = histItems
                    })));
            }
        }

        if (d.TopTypes.Count > 0)
        {
            int limit = d.TopTypes.Count;
            var rows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                TypeSnapshot t = d.TopTypes[i];
                rows.Add(Row(
                    Cell(t.TypeName),
                    Cell(t.Count.ToString("N0"), t.Count),
                    Cell(FormatHelper.FormatBytes(t.TotalBytes), (long)Math.Min(t.TotalBytes, long.MaxValue)),
                    Cell(t.LohBytes > 0 ? FormatHelper.FormatBytes(t.LohBytes) : "—"),
                    Cell(t.AverageSize > 0 ? FormatHelper.FormatBytes(t.AverageSize) : "—"),
                    Cell(t.EstimatedRetainedBytes > 0 ? FormatHelper.FormatBytes(t.EstimatedRetainedBytes) : "—"),
                    Cell($"0x{t.SampleAddress:X}"),
                    Cell(t.ModuleName ?? "—")));
            }
                    tables.Add(ST("Top types",
                ["Type", "Count", "Total Bytes", "LOH Bytes", "Avg Size", "Est. Retained", "Sample Addr", "Module"],
                rows));
        }

        if (d.SizeBucketHistogram is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.SizeBucketHistogram.Count);
            long totalBucketObjects = 0;
            for (int i = 0; i < d.SizeBucketHistogram.Count; i++)
                totalBucketObjects += d.SizeBucketHistogram[i].ObjectCount;

            long runningObjectCount = 0;
            for (int i = 0; i < d.SizeBucketHistogram.Count; i++)
            {
                SizeBucketEntry b = d.SizeBucketHistogram[i];
                runningObjectCount += b.ObjectCount;
                double pctObjects = totalBucketObjects == 0 ? 0 : b.ObjectCount * 100.0 / totalBucketObjects;
                double cumulativePct = totalBucketObjects == 0 ? 0 : runningObjectCount * 100.0 / totalBucketObjects;
                rows.Add(Row(
                    Cell(b.RangeLabel),
                    Cell(b.ObjectCount.ToString("N0"), b.ObjectCount),
                    Cell($"{pctObjects:F1}%"),
                    Cell($"{cumulativePct:F1}%"),
                    Cell(FormatHelper.FormatBytes(b.TotalBytes), (long)Math.Min(b.TotalBytes, long.MaxValue))));
            }
            tables.Add(ST("Object size histogram", ["Range", "Objects", "% Objects", "Cumulative %", "Total Bytes"], rows));
        }





        return new AnalyzerDetailSection(
            AnalyzerName: "Memory Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
