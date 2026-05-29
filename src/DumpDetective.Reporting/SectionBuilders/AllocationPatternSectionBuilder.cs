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

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("GC pressure",        d.GCPressure.ToString(),           (double)d.GCPressure),
            KM("Promotion pressure", $"{d.PromotionPressureScore:F1}",   d.PromotionPressureScore),
            KM("Profile",            d.Profile.ToString()),
            KM("Gen0 count %",       $"{d.Gen0CountPct:F1}%",            d.Gen0CountPct),
            KM("Gen1 count %",       $"{d.Gen1CountPct:F1}%",            d.Gen1CountPct),
            KM("Gen2 count %",       $"{d.Gen2CountPct:F1}%",            d.Gen2CountPct),
            KM("LOH count %",        $"{d.LohCountPct:F1}%",             d.LohCountPct),
            KM("Gen0 size %",        $"{d.Gen0SizePct:F1}%",             d.Gen0SizePct),
            KM("Gen1 size %",        $"{d.Gen1SizePct:F1}%",             d.Gen1SizePct),
            KM("Gen2 size %",        $"{d.Gen2SizePct:F1}%",             d.Gen2SizePct),
            KM("LOH size %",         $"{d.LohSizePct:F1}%",              d.LohSizePct),
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
