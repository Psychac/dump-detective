using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class BoxingSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypesToShow = 15;
    private const int TopPaddingToShow = 10;

    public string AnalyzerName => "Boxing Analysis";
    public int SortOrder => 50; // §20 — after WeakReferenceSectionBuilder (49)

    public bool CanHandle(AnalyzerDomainResult result) => result is BoxingDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (BoxingDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── §20.1 Boxed Value Type Inventory ─────────────────────────────────
        blocks.Add(H("BOXED VALUE TYPE INVENTORY"));
        blocks.Add(Divider());
        blocks.Add(M("Total boxed objects", $"{d.TotalBoxedObjects:N0}", d.TotalBoxedObjects));
        blocks.Add(M("Total boxed bytes", FormatHelper.FormatBytes(d.TotalBoxedBytes), (double)d.TotalBoxedBytes));
        blocks.Add(M("Boxed enum instances", $"{d.BoxedEnumCount:N0}", d.BoxedEnumCount));
        blocks.Add(M("Boxed enum bytes", FormatHelper.FormatBytes(d.BoxedEnumBytes), (double)d.BoxedEnumBytes));
        blocks.Add(M("Oversized value types", $"{d.OversizedValueTypeCount:N0}", d.OversizedValueTypeCount));

        if (d.TypeScanCapped)
            blocks.Add(T("⚠ Type scan was capped at 10 000 entries — totals may be underestimated."));

        // Top boxed types by total bytes
        if (d.TopBoxedTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP BOXED TYPES BY SIZE"));
            blocks.Add(Divider());

            var rows = new List<TableRow>(Math.Min(d.TopBoxedTypes.Count, TopTypesToShow));
            foreach (BoxedTypeEntry e in d.TopBoxedTypes.Take(TopTypesToShow))
            {
                rows.Add(new TableRow([
                    Cell(e.ValueTypeName),
                    Cell(e.IsEnum ? "Enum" : "Struct"),
                    Cell($"{e.BoxCount:N0}",                       e.BoxCount),
                    Cell(FormatHelper.FormatBytes(e.TotalBoxBytes), (long)e.TotalBoxBytes)]));
            }
            blocks.Add(new TableBlock("Top boxed types by total size",
                ["Type", "Kind", "Count", "Total Size"], rows));
        }

        // ── §20.2 Value Type Shape Issues ────────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("VALUE TYPE SHAPE ISSUES"));
        blocks.Add(Divider());

        if (d.TopPaddingWasteTypes.Count > 0)
        {
            blocks.Add(H("TOP STRUCT PADDING WASTE", indent: 1));
            blocks.Add(Divider());

            var padRows = new List<TableRow>(Math.Min(d.TopPaddingWasteTypes.Count, TopPaddingToShow));
            foreach (StructPaddingEntry e in d.TopPaddingWasteTypes.Take(TopPaddingToShow))
            {
                padRows.Add(new TableRow([
                    Cell(e.TypeName),
                    Cell($"{e.StructSize} B",          e.StructSize),
                    Cell($"{e.TotalFieldBytes} B",     e.TotalFieldBytes),
                    Cell($"{e.WastedPaddingBytes} B",  e.WastedPaddingBytes),
                    Cell($"{e.WasteRatio:P0}")]));
            }
            blocks.Add(new TableBlock("Struct types with highest padding waste",
                ["Type", "Size", "Field Bytes", "Wasted", "Waste %"], padRows));
        }
        else
        {
            blocks.Add(T("No significant struct padding waste detected."));
        }

        return new AnalyzerDetailSection(AnalyzerName, "Boxing & Value Type Pressure", SortOrder, blocks);
    }
}
