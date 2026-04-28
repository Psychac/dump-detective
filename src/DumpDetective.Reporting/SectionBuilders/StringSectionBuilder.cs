using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StringSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName  => "String Analysis";
    public string DisplayTitle  => "String & Duplicate Analysis";
    public int    SortOrder     => 26;

    public bool CanHandle(AnalyzerDomainResult result) => result is StringDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StringDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ──────────────────────────────────────────────────────────
        blocks.Add(H("SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Strings",           $"{d.TotalStrings:N0}",                                  d.TotalStrings));
        blocks.Add(M("Total String Memory",     FormatHelper.FormatBytes(d.TotalStringMemoryBytes),       (double)d.TotalStringMemoryBytes));
        blocks.Add(M("% of Managed Heap",       $"{d.PctOfManagedHeap:F2}%",                             d.PctOfManagedHeap));
        blocks.Add(M("Unique Strings",          $"{d.UniqueStrings:N0}",                                 d.UniqueStrings));
        blocks.Add(M("Duplication Ratio",       $"{d.DuplicationRatio:P1}",                              d.DuplicationRatio));
        blocks.Add(M("Duplicate Waste",         FormatHelper.FormatBytes(d.DuplicateWastedBytes),         (double)d.DuplicateWastedBytes));
        blocks.Add(M("LOH String Bytes",        FormatHelper.FormatBytes(d.LohStringBytes),               (double)d.LohStringBytes));
        blocks.Add(M("Gen2 String Count",       $"{d.Gen2StringCount:N0}",                               d.Gen2StringCount));
        blocks.Add(M("Interned Strings (FOH)",  $"{d.InternedStringCount:N0} ({FormatHelper.FormatBytes(d.InternedStringBytes)})", d.InternedStringCount));

        // ── Top Duplicates by Waste ───────────────────────────────────────────
        if (d.TopDuplicatesByWaste.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP DUPLICATES BY WASTED BYTES"));
            blocks.Add(CollapseBegin("Top duplicate strings by memory waste"));
            var rows = new List<TableRow>(d.TopDuplicatesByWaste.Count);
            for (int i = 0; i < d.TopDuplicatesByWaste.Count; i++)
            {
                var dup = d.TopDuplicatesByWaste[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(dup.Preview, 80)),
                    Cell($"{dup.Count:N0}", dup.Count),
                    Cell(FormatHelper.FormatBytes(dup.WastedBytes), (long)dup.WastedBytes)));
            }
            blocks.Add(new TableBlock("Duplicates ranked by wasted bytes", ["String Preview", "Count", "Wasted"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── Top Duplicates by Count ───────────────────────────────────────────
        if (d.TopDuplicatesByCount.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP DUPLICATES BY COUNT"));
            blocks.Add(CollapseBegin("Top duplicate strings by occurrence count"));
            var rows = new List<TableRow>(d.TopDuplicatesByCount.Count);
            for (int i = 0; i < d.TopDuplicatesByCount.Count; i++)
            {
                var dup = d.TopDuplicatesByCount[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(dup.Preview, 80)),
                    Cell($"{dup.Count:N0}", dup.Count),
                    Cell(FormatHelper.FormatBytes(dup.WastedBytes), (long)dup.WastedBytes)));
            }
            blocks.Add(new TableBlock("Duplicates ranked by count", ["String Preview", "Count", "Wasted"], rows));
            blocks.Add(CollapseEnd());
        }

        // ── Very Long Strings (LOH residents) ────────────────────────────────
        if (d.VeryLongStrings.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("VERY LONG STRINGS (> 85 KB)"));
            blocks.Add(CollapseBegin("Very long strings on LOH"));
            var rows = new List<TableRow>(d.VeryLongStrings.Count);
            for (int i = 0; i < d.VeryLongStrings.Count; i++)
            {
                var s = d.VeryLongStrings[i];
                rows.Add(Row(
                    Cell($"0x{s.Address:X}"),
                    Cell($"{s.CharLength:N0} chars", s.CharLength),
                    Cell(FormatHelper.FormatBytes(s.SizeBytes), (long)s.SizeBytes)));
            }
            blocks.Add(new TableBlock("Strings exceeding LOH threshold", ["Address", "Char Length", "Size"], rows));
            blocks.Add(CollapseEnd());
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks);
    }
}
