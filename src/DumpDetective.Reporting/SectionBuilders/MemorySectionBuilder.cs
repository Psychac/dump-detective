using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class MemorySectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopItems = 20;

    public string AnalyzerName => "Memory Analysis";
    public int SortOrder => 20;

    public bool CanHandle(AnalyzerDomainResult result) => result is MemoryDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (MemoryDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("OVERALL SUMMARY"));
        blocks.Add(M("Total Memory",  FormatHelper.FormatBytes(d.TotalBytes),   (double)d.TotalBytes));
        blocks.Add(M("Total Objects", $"{d.TotalObjects:N0}",                   d.TotalObjects));
        blocks.Add(M("LOH Memory",    $"{FormatHelper.FormatBytes(d.LohBytes)} ({d.LohPercent:F1}%)", (double)d.LohBytes));
        blocks.Add(M("LOH Objects",   $"{d.LohObjects:N0}",                     d.LohObjects));
        blocks.Add(M("LOH Threshold", $"{d.LohThresholdBytes:N0} bytes",        (double)d.LohThresholdBytes));
        blocks.Add(M("Unique Types",  $"{d.UniqueTypes:N0}",                    d.UniqueTypes));

        blocks.Add(Blank());
        blocks.Add(H("HEAP COMPOSITION SIGNALS"));
        blocks.Add(Divider());
        blocks.Add(d.LohPercent >= 40
            ? T("LOH share is elevated; review large-object allocation and retention patterns.")
            : T("LOH share appears within expected range for this snapshot."));

        blocks.Add(Blank());
        blocks.Add(H("TOP 20 OBJECT TYPES BY MEMORY SIZE"));
        blocks.Add(Divider());
        blocks.Add(new TableBlock(
            Caption: "Top 20 object types by memory size",
            Headers: ["Type", "Count", "Total Size"],
            Rows: BuildTypeRows(d.TopTypesBySize, TopItems)));

        blocks.Add(Blank());
        blocks.Add(H("TOP 20 OBJECT TYPES BY COUNT"));
        blocks.Add(Divider());
        blocks.Add(new TableBlock(
            Caption: "Top 20 object types by count",
            Headers: ["Type", "Count", "Total Size"],
            Rows: BuildTypeRows(d.TopTypesByCount, TopItems)));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<TypeSnapshot> types, int take)
    {
        var rows = new List<TableRow>(Math.Min(types.Count, take));
        int limit = Math.Min(types.Count, take);
        for (int i = 0; i < limit; i++)
        {
            var t = types[i];
            rows.Add(new TableRow([
                new TableCell(t.TypeName),
                new TableCell($"{t.Count:N0}", t.Count),
                new TableCell(FormatHelper.FormatBytes(t.TotalBytes), (long)t.TotalBytes)]));
        }
        return rows;
    }
}
