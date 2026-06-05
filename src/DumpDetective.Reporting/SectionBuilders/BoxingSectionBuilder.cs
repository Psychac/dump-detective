using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class BoxingSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypesToShow = 15;
    private const int TopPaddingToShow = 10;

    public string AnalyzerName => "Boxing Analysis";
    public string DisplayTitle => "Boxing & Value Type Pressure";
    public int SortOrder => 500; // §20 — after WeakReferenceSectionBuilder (49)

    public bool CanHandle(AnalyzerDomainResult result) => result is BoxingDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (BoxingDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_boxed_objects"] = new NumericMetricValue(d.TotalBoxedObjects, MetricUnit.Count),
            ["total_boxed_bytes"] = new NumericMetricValue((double)d.TotalBoxedBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalBoxedBytes)),
            ["boxed_enum_instances"] = new NumericMetricValue(d.BoxedEnumCount, MetricUnit.Count),
            ["boxed_enum_bytes"] = new NumericMetricValue((double)d.BoxedEnumBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.BoxedEnumBytes)),
            ["oversized_value_types"] = new NumericMetricValue(d.OversizedValueTypeCount, MetricUnit.Count),
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
                    Cell($"{e.BoxCount:N0}",                       e.BoxCount),
                    Cell(FormatHelper.FormatBytes(e.TotalBoxBytes), (long)e.TotalBoxBytes),
                    Cell(e.IsEnum ? "Yes" : "No")]));
            }
            compactTables.Add(STCompact("Top boxed types by total size", new[] { CH("Value Type"), CH("Box Count","number"), CH("Total Box Bytes","bytes"), CH("IsEnum") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Struct types with highest padding waste",
                new[] { CH("Type"), CH("Size"), CH("Field Bytes"), CH("Wasted","bytes"), CH("Waste %") }, padRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
