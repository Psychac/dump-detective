using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>B1 — Generation Pressure. Source: <see cref="GCGenerationDomainResult"/>.</summary>
internal sealed class GCPressureSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "GC Generation Analysis";
    public string DisplayTitle => "Generation Pressure";
    public int SortOrder => 100;

    public bool CanHandle(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (GCGenerationDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["gen0_bytes"] = new NumericMetricValue((double)d.Gen0Bytes, MetricUnit.Bytes, FormatBytes(d.Gen0Bytes)),
            ["gen0_objects"] = new NumericMetricValue(d.Gen0Objects, MetricUnit.Count),
            ["gen1_bytes"] = new NumericMetricValue((double)d.Gen1Bytes, MetricUnit.Bytes, FormatBytes(d.Gen1Bytes)),
            ["gen1_objects"] = new NumericMetricValue(d.Gen1Objects, MetricUnit.Count),
            ["gen2_bytes"] = new NumericMetricValue((double)d.Gen2Bytes, MetricUnit.Bytes, FormatBytes(d.Gen2Bytes)),
            ["gen2_objects"] = new NumericMetricValue(d.Gen2Objects, MetricUnit.Count),
            ["loh_bytes"] = new NumericMetricValue((double)d.LohBytes, MetricUnit.Bytes, FormatBytes(d.LohBytes)),
            ["loh_objects"] = new NumericMetricValue(d.LohObjects, MetricUnit.Count),
            ["total_objects"] = new NumericMetricValue(d.TotalObjects, MetricUnit.Count),
            ["gen2_pct"] = new NumericMetricValue(d.Gen2Pct, MetricUnit.Percent, $"{d.Gen2Pct:F1}%"),
            ["loh_pct"] = new NumericMetricValue(d.LohPercent, MetricUnit.Percent, $"{d.LohPercent:F1}%"),
        };

        if (d.Gen2Pct >= 40.0)
            blocks.Add(T("Gen2 dominates the heap; retention is likely becoming long-lived."));
        if (d.LohPercent >= 35.0)
            blocks.Add(T("LOH share is elevated and may be contributing to fragmentation or promotion pressure."));

        // Top LOH types — prefer PerTypeGenerationProfiles (per-gen counts) when available; fall back to TypeSnapshot
        if (d.PerTypeGenerationProfiles is { Count: > 0 })
        {
            var lohProfiles = new List<TypeGenerationProfile>(16);
            for (int i = 0; i < d.PerTypeGenerationProfiles.Count; i++)
            {
                if (d.PerTypeGenerationProfiles[i].LohCount > 0)
                    lohProfiles.Add(d.PerTypeGenerationProfiles[i]);
            }
            lohProfiles.Sort(static (a, b) => b.LohCount.CompareTo(a.LohCount));

            if (lohProfiles.Count > 0)
            {
                int limit = Math.Min(lohProfiles.Count, 15);
                var rows = new List<TableRow>(limit);
                for (int i = 0; i < limit; i++)
                {
                    TypeGenerationProfile p = lohProfiles[i];
                    rows.Add(Row(
                        Cell(p.TypeName),
                        Cell(p.Gen0Count.ToString("N0"), p.Gen0Count),
                        Cell(p.Gen1Count.ToString("N0"), p.Gen1Count),
                        Cell(p.Gen2Count.ToString("N0"), p.Gen2Count),
                        Cell(p.LohCount.ToString("N0"),  p.LohCount),
                        Cell(p.TotalBytes > 0 ? FormatBytes(p.TotalBytes) : "—")));
                }
                compactTables.Add(STCompact("Top LOH types", new[] { CH("Type"), CH("Gen0","number"), CH("Gen1","number"), CH("Gen2","number"), CH("LOH Count","number"), CH("Total Bytes","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
        }
        else if (d.TopLohTypes.Count > 0)
        {
            int limit = Math.Min(d.TopLohTypes.Count, 15);
            var rows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                TypeSnapshot t = d.TopLohTypes[i];
                rows.Add(Row(
                    Cell(t.TypeName),
                    Cell(t.Count.ToString("N0"), t.Count),
                    Cell(FormatBytes(t.TotalBytes), (long)Math.Min(t.TotalBytes, long.MaxValue)),
                    Cell(t.LohBytes > 0 ? FormatBytes(t.LohBytes) : "—")));
            }
            compactTables.Add(STCompact("Top LOH types", new[] { CH("Type"), CH("Count","number"), CH("Total Bytes","bytes"), CH("LOH Bytes","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.PerTypeGenerationProfiles is { Count: > 0 })
        {
            int limit = Math.Min(d.PerTypeGenerationProfiles.Count, 30);
            var rows = new List<TableRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                TypeGenerationProfile p = d.PerTypeGenerationProfiles[i];
                int total = p.Gen0Count + p.Gen1Count + p.Gen2Count + p.LohCount;
                double gen2Pct   = total == 0 ? 0.0 : p.Gen2Count  * 100.0 / total;
                double survivalR = total == 0 ? 0.0 : (p.Gen2Count + p.LohCount) * 1.0 / total;
                rows.Add(Row(
                    Cell(p.TypeName),
                    Cell(p.Gen0Count.ToString("N0"), p.Gen0Count),
                    Cell(p.Gen1Count.ToString("N0"), p.Gen1Count),
                    Cell(p.Gen2Count.ToString("N0"), p.Gen2Count),
                    Cell(p.LohCount.ToString("N0"),  p.LohCount),
                    Cell(p.TotalBytes > 0 ? FormatBytes(p.TotalBytes) : "-"),
                    Cell($"{gen2Pct:F1}%"),
                    Cell($"{survivalR:P1}"),
                    Cell(p.IsFinalizable ? "Yes" : "No")));
            }
            compactTables.Add(STCompact("Per-type generation profiles",
                new[] { CH("Type"), CH("Gen0","number"), CH("Gen1","number"), CH("Gen2","number"), CH("LOH","number"), CH("Total Bytes","bytes"), CH("Gen2%", "number", "percent"), CH("Survival Ratio", "number", "ratio"), CH("Finalizable") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "GC Generation Analysis",
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