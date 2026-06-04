using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["gc_pressure"] = new EnumMetricValue(d.GCPressure.ToString(), nameof(GCPressureLevel)),
            ["promotion_pressure"] = new NumericMetricValue(d.PromotionPressureScore, MetricUnit.Custom, $"{d.PromotionPressureScore:F1}"),
            ["profile"] = new EnumMetricValue(d.Profile.ToString(), nameof(AllocationProfile)),
            ["gen0_count_pct"] = new NumericMetricValue(d.Gen0CountPct, MetricUnit.Percent, $"{d.Gen0CountPct:F1}%"),
            ["gen1_count_pct"] = new NumericMetricValue(d.Gen1CountPct, MetricUnit.Percent, $"{d.Gen1CountPct:F1}%"),
            ["gen2_count_pct"] = new NumericMetricValue(d.Gen2CountPct, MetricUnit.Percent, $"{d.Gen2CountPct:F1}%"),
            ["loh_count_pct"] = new NumericMetricValue(d.LohCountPct, MetricUnit.Percent, $"{d.LohCountPct:F1}%"),
            ["gen0_size_pct"] = new NumericMetricValue(d.Gen0SizePct, MetricUnit.Percent, $"{d.Gen0SizePct:F1}%"),
            ["gen1_size_pct"] = new NumericMetricValue(d.Gen1SizePct, MetricUnit.Percent, $"{d.Gen1SizePct:F1}%"),
            ["gen2_size_pct"] = new NumericMetricValue(d.Gen2SizePct, MetricUnit.Percent, $"{d.Gen2SizePct:F1}%"),
            ["loh_size_pct"] = new NumericMetricValue(d.LohSizePct, MetricUnit.Percent, $"{d.LohSizePct:F1}%"),
        };

        blocks.Add(T(d.GCPressure switch
        {
            GCPressureLevel.Critical => "Critical GC pressure. Large Gen2/LOH retention is already present.",
            GCPressureLevel.High     => "High GC pressure. Gen2 retention is elevated and should be investigated.",
            GCPressureLevel.Moderate => "Moderate GC pressure. Monitor for continued growth.",
            _                        => "GC pressure is within normal bounds."
        }));

        blocks.Add(T("Allocation-site precision is ETW-dependent; these signals summarize heap pressure from the dump state only."));

        tables.Add(ST(
            "Classification summary",
            ["Signal", "Value"],
            [
                Row(Cell("Allocation profile"), Cell(d.Profile.ToString())),
                Row(Cell("GC pressure level"),  Cell(d.GCPressure.ToString())),
            ]));

        if (d.TopTransientTypes is { Count: > 0 })
            tables.Add(ST("Top transient types",
                ["Type", "Gen0 Count", "Gen1 Count", "Gen2 Count", "Long-lived Ratio", "Profile"],
                BuildRows(d.TopTransientTypes)));

        if (d.TopShortishTypes is { Count: > 0 })
            tables.Add(ST("Top medium-lived types",
                ["Type", "Gen0 Count", "Gen1 Count", "Gen2 Count", "Long-lived Ratio", "Profile"],
                BuildRows(d.TopShortishTypes)));

        if (d.TopLongLivedTypes is { Count: > 0 })
            tables.Add(ST("Top long-lived types",
                ["Type", "Gen0 Count", "Gen1 Count", "Gen2 Count", "Long-lived Ratio", "Profile"],
                BuildRows(d.TopLongLivedTypes)));

        return new AnalyzerDetailSection(
            AnalyzerName: "Allocation Pattern Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
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
                Cell($"{p.LongLivedRatio:P1}"),
                Cell(p.Profile.ToString())));
        }
        return rows;
    }
}
