using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCGenerationSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopLohTypes = 15;

    public string AnalyzerName => "GC Generation Analysis";
    public int SortOrder => 30;

    public bool CanHandle(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (GCGenerationDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("HEAP SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Gen0 objects", $"{d.Gen0Objects:N0},  {FormatHelper.FormatBytes(d.Gen0Bytes)}", d.Gen0Objects));
        blocks.Add(M("Gen1 objects", $"{d.Gen1Objects:N0},  {FormatHelper.FormatBytes(d.Gen1Bytes)}", d.Gen1Objects));
        blocks.Add(M("Gen2 objects", $"{d.Gen2Objects:N0},  {FormatHelper.FormatBytes(d.Gen2Bytes)}", d.Gen2Objects));
        blocks.Add(M("LOH objects",  $"{d.LohObjects:N0},  {FormatHelper.FormatBytes(d.LohBytes)}",   d.LohObjects));
        blocks.Add(M("Total objects", $"{d.TotalObjects:N0}", d.TotalObjects));
        blocks.Add(M("LOH percentage", $"{d.LohPercent:F1}%", d.LohPercent));

        blocks.Add(Blank());
        blocks.Add(H("GENERATION SPLIT (bytes)"));
        blocks.Add(Divider());
        blocks.Add(M("Gen0 bytes", FormatHelper.FormatBytes(d.Gen0Bytes), (double)d.Gen0Bytes));
        blocks.Add(M("Gen1 bytes", FormatHelper.FormatBytes(d.Gen1Bytes), (double)d.Gen1Bytes));
        blocks.Add(M("Gen2 bytes", FormatHelper.FormatBytes(d.Gen2Bytes), (double)d.Gen2Bytes));
        blocks.Add(M("LOH bytes",  FormatHelper.FormatBytes(d.LohBytes),  (double)d.LohBytes));

        var lohTypes = d.TopLohTypes ?? [];
        if (lohTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP LOH OBJECT TYPES"));
            blocks.Add(new TableBlock(
                Caption: "Top LOH object types",
                Headers: ["Type", "Count", "Total Size"],
                Rows: BuildLohRows(lohTypes, TopLohTypes)));
        }

        blocks.Add(Blank());
        blocks.Add(H("LOH RISK SIGNAL"));
        blocks.Add(Divider());
        blocks.Add(d.LohPercent >= 35
            ? T("LOH footprint is elevated for this dump.")
            : T("LOH footprint is not elevated."));

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }

    private static List<TableRow> BuildLohRows(IReadOnlyList<TypeSnapshot> types, int take)
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
