using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AllocationPatternSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 15;

    public string AnalyzerName => "Allocation Pattern Analysis";
    public int SortOrder => 32;

    public bool CanHandle(AnalyzerDomainResult result) => result is AllocationPatternDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AllocationPatternDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ───────────────────────────────────────────────────────────
        blocks.Add(H("ALLOCATION PATTERN SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Allocation Profile",       d.Profile.ToString(),            (double)d.Profile));
        blocks.Add(M("GC Pressure Level",        d.GCPressure.ToString(),         (double)d.GCPressure));
        blocks.Add(M("Promotion Pressure Score", $"{d.PromotionPressureScore:F1}", d.PromotionPressureScore));

        // ── Generation distribution table ─────────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("GENERATION DISTRIBUTION"));
        blocks.Add(new TableBlock(
            Caption: "Generation distribution",
            Headers: ["Generation", "Objects %", "Size %"],
            Rows:
            [
                new([ Cell("Gen0"), Cell($"{d.Gen0CountPct:F1}%"), Cell($"{d.Gen0SizePct:F1}%") ]),
                new([ Cell("Gen1"), Cell($"{d.Gen1CountPct:F1}%"), Cell($"{d.Gen1SizePct:F1}%") ]),
                new([ Cell("Gen2"), Cell($"{d.Gen2CountPct:F1}%"), Cell($"{d.Gen2SizePct:F1}%") ]),
                new([ Cell("LOH"),  Cell($"{d.LohCountPct:F1}%"),  Cell($"{d.LohSizePct:F1}%")  ]),
            ]));

        // ── Pressure signal ───────────────────────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("GC PRESSURE SIGNAL"));
        blocks.Add(Divider());
        blocks.Add(d.GCPressure switch
        {
            GCPressureLevel.Critical =>
                T("Critical GC pressure. Large Gen2/LOH accumulation detected. Investigate long-lived objects, caches, and finalizable types."),
            GCPressureLevel.High =>
                T("High GC pressure. Reduce Gen2 retention — review static fields and event subscriptions."),
            GCPressureLevel.Moderate =>
                T("Moderate GC pressure. Monitor Gen2 growth across snapshots."),
            _ =>
                T("GC pressure is within normal bounds.")
        });

        // ── Short-lived types ─────────────────────────────────────────────────
        var shortLived = d.TopShortLivedTypes ?? [];
        if (shortLived.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP SHORT-LIVED TYPES (HIGH GEN0 RATIO)"));
            blocks.Add(new TableBlock(
                Caption: "Top short-lived types",
                Headers: ["Type", "Gen0", "Gen1", "Gen2", "Long-Lived Ratio"],
                Rows: BuildTypeRows(shortLived)));
        }

        // ── Long-lived types ──────────────────────────────────────────────────
        var longLived = d.TopLongLivedTypes ?? [];
        if (longLived.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP LONG-LIVED TYPES (HIGH GEN2/LOH RATIO)"));
            blocks.Add(new TableBlock(
                Caption: "Top long-lived types",
                Headers: ["Type", "Gen0", "Gen1", "Gen2", "Long-Lived Ratio"],
                Rows: BuildTypeRows(longLived)));
        }

        return new AnalyzerDetailSection(AnalyzerName, "GC & Allocation Patterns", SortOrder, blocks);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<TypeAllocationProfile> types)
    {
        int limit = Math.Min(types.Count, TopTypeRows);
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            var t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.TypeName, 70)),
                Cell($"{t.Gen0Count:N0}", t.Gen0Count),
                Cell($"{t.Gen1Count:N0}", t.Gen1Count),
                Cell($"{t.Gen2Count:N0}", t.Gen2Count),
                Cell($"{t.LongLivedRatio:P1}")]));
        }
        return rows;
    }
}
