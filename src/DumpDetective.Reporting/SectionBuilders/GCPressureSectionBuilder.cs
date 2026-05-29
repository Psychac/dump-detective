using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Gen0 bytes",     FormatBytes(d.Gen0Bytes),              (double)d.Gen0Bytes),
            KM("Gen0 objects",   d.Gen0Objects.ToString("N0"),           d.Gen0Objects),
            KM("Gen1 bytes",     FormatBytes(d.Gen1Bytes),              (double)d.Gen1Bytes),
            KM("Gen1 objects",   d.Gen1Objects.ToString("N0"),           d.Gen1Objects),
            KM("Gen2 bytes",     FormatBytes(d.Gen2Bytes),              (double)d.Gen2Bytes),
            KM("Gen2 objects",   d.Gen2Objects.ToString("N0"),           d.Gen2Objects),
            KM("LOH bytes",      FormatBytes(d.LohBytes),              (double)d.LohBytes),
            KM("LOH objects",    d.LohObjects.ToString("N0"),            d.LohObjects),
            KM("Total objects",  d.TotalObjects.ToString("N0"),          d.TotalObjects),
            KM("Gen2 %",         $"{d.Gen2Pct:F1}%",                    d.Gen2Pct),
            KM("LOH %",          $"{d.LohPercent:F1}%",                 d.LohPercent),
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
                tables.Add(ST("Top LOH types", ["Type", "Gen0", "Gen1", "Gen2", "LOH Count", "Total Bytes"], rows));
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
            tables.Add(ST("Top LOH types", ["Type", "Count", "Total Bytes", "LOH Bytes"], rows));
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
            tables.Add(ST("Per-type generation profiles",
                ["Type", "Gen0", "Gen1", "Gen2", "LOH", "Total Bytes", "Gen2%", "Survival Ratio", "Finalizable"],
                rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "GC Generation Analysis",
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