using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Text.Json;
using System.Linq;
using DumpDetective.Core.Enums;

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
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["memory_pressure_score"] = new NumericMetricValue(d.MemoryPressureScore, MetricUnit.Custom, d.MemoryPressureScore.ToString("F1")),
            ["total_bytes"] = new NumericMetricValue((double)d.TotalBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalBytes)),
            ["total_objects"] = new NumericMetricValue(d.TotalObjects, MetricUnit.Count),
            ["unique_types"] = new NumericMetricValue(d.UniqueTypes, MetricUnit.Count),
            ["loh_bytes"] = new NumericMetricValue((double)d.LohBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.LohBytes)),
            ["loh_pct"] = new NumericMetricValue(d.LohPercent, MetricUnit.Percent, $"{d.LohPercent:F1}%"),
            ["top5_share"] = new NumericMetricValue(d.Top5BytesPercent, MetricUnit.Percent, $"{d.Top5BytesPercent:F1}%"),
            ["small_object_pct"] = new NumericMetricValue(d.SmallObjectCountPercent, MetricUnit.Percent, $"{d.SmallObjectCountPercent:F1}%"),
            ["objects_per_mb"] = new NumericMetricValue(d.ObjectsPerMb, MetricUnit.Custom, d.ObjectsPerMb.ToString("F1")),
        };

        string pressureBand = d.MemoryPressureScore >= 75 ? "High"
            : d.MemoryPressureScore >= 50 ? "Medium"
            : "Low";
        blocks.Add(T($"Memory pressure score: {d.MemoryPressureScore:F1}/100 ({pressureBand})."));

        if (d.TopTypes.Count > 0)
        {
            blocks.Add(T("Note: 'Est. Retained' represents approximate retained size from a single-sample bounded breadth-first search and may vary significantly from actual retained size for structurally complex types (e.g., containers, caches)."));
        }

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
                    compactTables.Add(STCompact("Top types", new[] { CH("Type"), CH("Count","number"), CH("Total Bytes","bytes"), CH("LOH Bytes","bytes"), CH("Avg Size","bytes"), CH("Est. Retained","bytes"), CH("Sample Addr"), CH("Module") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Object size histogram", new[] { CH("Range"), CH("Objects","number"), CH("% Objects", "number", "percent"), CH("Cumulative %", "number", "percent"), CH("Total Bytes","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }





        return new AnalyzerDetailSection(
            AnalyzerName: "Memory Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
