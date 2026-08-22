using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class BoxingSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
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
            ["avg_boxed_instance_bytes"] = new NumericMetricValue(d.AvgBoxedInstanceBytes, MetricUnit.Bytes, FormatHelper.FormatBytes((ulong)d.AvgBoxedInstanceBytes)),
            ["boxed_enum_instances"] = new NumericMetricValue(d.BoxedEnumCount, MetricUnit.Count),
            ["boxed_enum_bytes"] = new NumericMetricValue((double)d.BoxedEnumBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.BoxedEnumBytes)),
            ["nullable_boxed_instances"] = new NumericMetricValue(d.NullableBoxedCount, MetricUnit.Count),
            ["nullable_boxed_bytes"] = new NumericMetricValue((double)d.NullableBoxedBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.NullableBoxedBytes)),
            ["oversized_value_types"] = new NumericMetricValue(d.OversizedValueTypeInstanceCount, MetricUnit.Count),
            ["aggregate_padding_waste"] = new NumericMetricValue((double)d.AggregatePaddingWasteBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.AggregatePaddingWasteBytes)),
        };

        if (d.TopBoxedTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopBoxedTypes.Count);
            foreach (BoxedTypeEntry e in d.TopBoxedTypes)
            {
                rows.Add(new TableRow([
                    Cell(e.ValueTypeName),
                    Cell($"{e.BoxCount:N0}",                       e.BoxCount),
                    Cell(FormatHelper.FormatBytes(e.TotalBoxBytes), (long)e.TotalBoxBytes),
                    Cell(e.IsEnum ? "Yes" : "No")]));
            }
            compactTables.Add(STCompact("Top boxed types by total size", new[] { CH("Value Type"), CH("Box Count","number"), CH("Total Box Bytes","bytes"), CH("IsEnum") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopOversizedTypes.Count > 0)
        {
            var oversizedRows = new List<TableRow>(d.TopOversizedTypes.Count);
            foreach (OversizedTypeEntry e in d.TopOversizedTypes)
            {
                oversizedRows.Add(new TableRow([
                    Cell(e.TypeName),
                    Cell($"{e.StaticSize} B",  e.StaticSize),
                    Cell($"{e.Count:N0}",      e.Count)]));
            }
            compactTables.Add(STCompact("Oversized value types",
                new[] { CH("Type"), CH("StaticSize"), CH("Instance Count","number") }, oversizedRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopPaddingWasteTypes.Count > 0)
        {
            var padRows = new List<TableRow>(d.TopPaddingWasteTypes.Count);
            foreach (StructPaddingEntry e in d.TopPaddingWasteTypes)
            {
                padRows.Add(new TableRow([
                    Cell(e.TypeName),
                    Cell($"{e.StructSize} B",          e.StructSize),
                    Cell($"{e.TotalFieldBytes} B",     e.TotalFieldBytes),
                    Cell($"{e.WastedPaddingBytes} B",  e.WastedPaddingBytes),
                    Cell($"{e.WasteRatio:P0}")]));
            }
            compactTables.Add(STCompact("Struct types with highest padding waste",
                new[] { CH("Type"), CH("Size"), CH("Field Bytes"), CH("Wasted","bytes"), CH("Waste %", "number", "percent") }, padRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
