using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using System.Linq;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Enums;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>B2 — Allocation Patterns. Source: <see cref="AllocationPatternDomainResult"/>.</summary>
internal sealed class AllocationPatternSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Allocation Pattern Analysis";
    public string DisplayTitle => "Allocation Patterns";
    public int SortOrder => 200;

    public bool CanHandle(AnalyzerDomainResult result) => result is AllocationPatternDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AllocationPatternDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["gc_pressure"] = new EnumMetricValue(d.GCPressure.ToString(), nameof(GCPressureLevel)),
            ["promotion_pressure"] = new NumericMetricValue(d.PromotionPressureScore, MetricUnit.Custom, $"{d.PromotionPressureScore:F1}"),
            ["profile"] = new EnumMetricValue(d.Profile.ToString(), nameof(AllocationProfile)),
            ["total_managed_bytes"] = new NumericMetricValue(d.TotalManagedBytes, MetricUnit.Bytes, FormatBytes(d.TotalManagedBytes)),
            ["gen0_count_pct"] = new NumericMetricValue(d.Gen0CountPct, MetricUnit.Percent, $"{d.Gen0CountPct:F1}%"),
            ["gen1_count_pct"] = new NumericMetricValue(d.Gen1CountPct, MetricUnit.Percent, $"{d.Gen1CountPct:F1}%"),
            ["gen2_count_pct"] = new NumericMetricValue(d.Gen2CountPct, MetricUnit.Percent, $"{d.Gen2CountPct:F1}%"),
            ["loh_count_pct"] = new NumericMetricValue(d.LohCountPct, MetricUnit.Percent, $"{d.LohCountPct:F1}%"),
            ["gen0_size_pct"] = new NumericMetricValue(d.Gen0SizePct, MetricUnit.Percent, $"{d.Gen0SizePct:F1}%"),
            ["gen0_bytes"] = new NumericMetricValue(d.Gen0Bytes, MetricUnit.Bytes, FormatBytes(d.Gen0Bytes)),
            ["gen1_size_pct"] = new NumericMetricValue(d.Gen1SizePct, MetricUnit.Percent, $"{d.Gen1SizePct:F1}%"),
            ["gen1_bytes"] = new NumericMetricValue(d.Gen1Bytes, MetricUnit.Bytes, FormatBytes(d.Gen1Bytes)),
            ["gen2_size_pct"] = new NumericMetricValue(d.Gen2SizePct, MetricUnit.Percent, $"{d.Gen2SizePct:F1}%"),
            ["gen2_bytes"] = new NumericMetricValue(d.Gen2Bytes, MetricUnit.Bytes, FormatBytes(d.Gen2Bytes)),
            ["loh_size_pct"] = new NumericMetricValue(d.LohSizePct, MetricUnit.Percent, $"{d.LohSizePct:F1}%"),
            ["loh_bytes"] = new NumericMetricValue(d.LohBytes, MetricUnit.Bytes, FormatBytes(d.LohBytes)),
        };

        blocks.Add(T(d.GCPressure switch
        {
            GCPressureLevel.Critical => "Critical GC pressure. Large Gen2/LOH retention is already present.",
            GCPressureLevel.High     => "High GC pressure. Gen2 retention is elevated and should be investigated.",
            GCPressureLevel.Moderate => "Moderate GC pressure. Monitor for continued growth.",
            _                        => "GC pressure is within normal bounds."
        }));

        blocks.Add(T("Allocation-site precision is ETW-dependent; these signals summarize heap pressure from the dump state only."));

        compactTables.Add(STCompact(
            "Classification summary",
            new[] { CH("Signal"), CH("Value") },
            new[] { R("Allocation profile", d.Profile.ToString()), R("GC pressure level", d.GCPressure.ToString()) }));

        if (d.TopTransientTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact("Top transient types",
                new[] { CH("Type"), CH("Gen0 Count","number"), CH("Gen1 Count","number"), CH("Gen2 Count","number"), CH("Long-lived Ratio", "number", "percent"), CH("Total Size", "number"), CH("Profile") },
                BuildRows(d.TopTransientTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopShortishTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact("Top medium-lived types",
                new[] { CH("Type"), CH("Gen0 Count","number"), CH("Gen1 Count","number"), CH("Gen2 Count","number"), CH("Long-lived Ratio", "number", "percent"), CH("Total Size", "number"), CH("Profile") },
                BuildRows(d.TopShortishTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopLongLivedTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact("Top long-lived types",
                new[] { CH("Type"), CH("Gen0 Count","number"), CH("Gen1 Count","number"), CH("Gen2 Count","number"), CH("Long-lived Ratio", "number", "percent"), CH("Total Size", "number"), CH("Profile") },
                BuildRows(d.TopLongLivedTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopHighGen1SurvivorTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact("Types with high Gen1 survival rate",
                new[] { CH("Type"), CH("Gen0 Count","number"), CH("Gen1 Count","number"), CH("Gen1 Survival Rate", "number", "percent"), CH("Total Size", "number") },
                BuildGen1SurvivorRows(d.TopHighGen1SurvivorTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Allocation Pattern Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static List<TableRow> BuildRows(IReadOnlyList<TypeAllocationProfile> types)
    {
        var rows = new List<TableRow>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            TypeAllocationProfile p = types[i];
            rows.Add(Row(
                Cell(p.TypeName),
                Cell(p.Gen0Count.ToString("N0"), p.Gen0Count),
                Cell(p.Gen1Count.ToString("N0"), p.Gen1Count),
                Cell(p.Gen2Count.ToString("N0"), p.Gen2Count),
                Cell(p.LongLivedRatio.ToString("P1"), p.LongLivedRatio),
                Cell(FormatBytes(p.TotalSize), p.TotalSize),
                Cell(p.Profile.ToString())));
        }
        return rows;
    }

    private static List<TableRow> BuildGen1SurvivorRows(IReadOnlyList<TypeAllocationProfile> types)
    {
        var rows = new List<TableRow>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            TypeAllocationProfile p = types[i];
            rows.Add(Row(
                Cell(p.TypeName),
                Cell(p.Gen0Count.ToString("N0"), p.Gen0Count),
                Cell(p.Gen1Count.ToString("N0"), p.Gen1Count),
                Cell(p.Gen1SurvivalRate.ToString("P1"), p.Gen1SurvivalRate),
                Cell(FormatBytes(p.TotalSize), p.TotalSize)));
        }
        return rows;
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0) return "0 B";
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:F2} {sizes[order]}";
    }

}
