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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total boxed objects",    $"{d.TotalBoxedObjects:N0}",                         d.TotalBoxedObjects),
            KM("Total boxed bytes",      FormatHelper.FormatBytes(d.TotalBoxedBytes),          (double)d.TotalBoxedBytes),
            KM("Boxed enum instances",   $"{d.BoxedEnumCount:N0}",                            d.BoxedEnumCount),
            KM("Boxed enum bytes",       FormatHelper.FormatBytes(d.BoxedEnumBytes),           (double)d.BoxedEnumBytes),
            KM("Oversized value types",  $"{d.OversizedValueTypeCount:N0}",                   d.OversizedValueTypeCount),
        };

        if (d.TypeScanCapped)
            blocks.Add(T("⚠ Type scan was capped at 10 000 entries — totals may be underestimated."));

        if (d.TopBoxedTypes.Count > 0)
        {
            var rows = new List<TableRow>(Math.Min(d.TopBoxedTypes.Count, TopTypesToShow));
            foreach (BoxedTypeEntry e in d.TopBoxedTypes.Take(TopTypesToShow))
            {
                rows.Add(new TableRow([
                    Cell(e.ValueTypeName),
                    Cell(e.IsEnum ? "Enum" : "Struct"),
                    Cell($"{e.BoxCount:N0}",                       e.BoxCount),
                    Cell(FormatHelper.FormatBytes(e.TotalBoxBytes), (long)e.TotalBoxBytes)]));
            }
            tables.Add(ST("Top boxed types by total size", ["Type", "Kind", "Count", "Total Size"], rows));
            if (d.TopBoxedTypes.Count > TopTypesToShow)
                blocks.Add(T($"Showing top {TopTypesToShow} boxed types. {d.TopBoxedTypes.Count - TopTypesToShow} additional type(s) omitted."));
        }

        if (d.TopPaddingWasteTypes.Count > 0)
        {
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
            tables.Add(ST("Struct types with highest padding waste",
                ["Type", "Size", "Field Bytes", "Wasted", "Waste %"], padRows));
            if (d.TopPaddingWasteTypes.Count > TopPaddingToShow)
                blocks.Add(T($"Showing top {TopPaddingToShow} padding-waste types. {d.TopPaddingWasteTypes.Count - TopPaddingToShow} additional type(s) omitted."));
        }
        else
        {
            blocks.Add(T("No significant struct padding waste detected."));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, "Boxing & Value Type Pressure", SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
