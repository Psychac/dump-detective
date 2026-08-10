using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>C2 — Object Shape Analysis. Source: <see cref="ObjectShapeAnalyzerDomainResult"/>.</summary>
internal sealed class ObjectShapeSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Object Shape Analysis";
    public string DisplayTitle => "Object Shape Analysis";
    public int SortOrder => 200; // §C2 — after C1 TypeSystem (100)

    public bool CanHandle(AnalyzerDomainResult result) => result is ObjectShapeAnalyzerDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ObjectShapeAnalyzerDomainResult)result;

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_types_analyzed"] = new NumericMetricValue(d.TotalTypesAnalyzed, MetricUnit.Count),
            ["avg_ref_fields_per_type"] = new NumericMetricValue(d.AvgRefFieldsPerType, MetricUnit.Custom, $"{d.AvgRefFieldsPerType:F1}"),
            ["total_gc_scan_work"] = new NumericMetricValue(d.TotalGcScanWork, MetricUnit.Custom, d.TotalGcScanWork.ToString("N0")),
            ["reference_heavy_types"] = new NumericMetricValue(d.TopReferenceHeavyTypes.Count, MetricUnit.Count),
            ["balanced_types"] = new NumericMetricValue(d.TopBalancedTypes.Count, MetricUnit.Count),
            ["array_types"] = new NumericMetricValue(d.TopArrayTypes.Count, MetricUnit.Count),
            ["value_heavy_types"] = new NumericMetricValue(d.TopValueHeavyTypes.Count, MetricUnit.Count),
        };

        if (d.TopReferenceHeavyTypes.Count > 0)
        {
            compactTables.Add(STCompact(
                "Reference-heavy types",
                new[] { CH("Type"), CH("Total Fields","number"), CH("Ref Fields","number"), CH("Val Fields","number"), CH("Ref Ratio"), CH("Instances","number"), CH("Size (bytes)","number"), CH("GC Scan Cost","number"), CH("Finalizable"), CH("Value Type"), CH("Array"), CH("Chain Depth","number"), CH("Interfaces","number"), CH("Category") },
                BuildShapeRows(d.TopReferenceHeavyTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopValueHeavyTypes.Count > 0)
        {
            compactTables.Add(STCompact(
                "Value-heavy types",
                new[] { CH("Type"), CH("Total Fields","number"), CH("Ref Fields","number"), CH("Val Fields","number"), CH("Ref Ratio"), CH("Instances","number"), CH("Size (bytes)","number"), CH("GC Scan Cost","number"), CH("Finalizable"), CH("Value Type"), CH("Array"), CH("Chain Depth","number"), CH("Interfaces","number"), CH("Category") },
                BuildShapeRows(d.TopValueHeavyTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopBalancedTypes.Count > 0)
        {
            compactTables.Add(STCompact(
                "Balanced types",
                new[] { CH("Type"), CH("Total Fields","number"), CH("Ref Fields","number"), CH("Val Fields","number"), CH("Ref Ratio"), CH("Instances","number"), CH("Size (bytes)","number"), CH("GC Scan Cost","number"), CH("Finalizable"), CH("Value Type"), CH("Array"), CH("Chain Depth","number"), CH("Interfaces","number"), CH("Category") },
                BuildShapeRows(d.TopBalancedTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopArrayTypes.Count > 0)
        {
            compactTables.Add(STCompact(
                "Array types (reference)",
                new[] { CH("Type"), CH("Total Fields","number"), CH("Ref Fields","number"), CH("Val Fields","number"), CH("Ref Ratio"), CH("Instances","number"), CH("Size (bytes)","number"), CH("GC Scan Cost","number"), CH("Finalizable"), CH("Value Type"), CH("Array"), CH("Chain Depth","number"), CH("Interfaces","number"), CH("Category") },
                BuildShapeRows(d.TopArrayTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        blocks.Add(T("Reference-heavy types (ratio > 0.6) are candidates for GC root retention and may inflate promotion pressure. Balanced types (ratio 0.2–0.6) are the numerically dominant heap residents. Array types (reference element types) impose high single-object GC scan cost. Value-heavy types (ratio < 0.2) with large struct sizes can cause excess stack pressure or LOH allocation. " +
                    $"(Avg ref fields is computed over at most {d.InstanceCountCap:N0} types by instance count.)"));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static List<TableRow> BuildShapeRows(IReadOnlyList<TypeShapeProfile> types)
    {
        var rows = new List<TableRow>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            TypeShapeProfile p = types[i];
            long gcScanCost = (long)(p.ReferenceFields * (double)p.InstanceCount);
            rows.Add(Row(
                Cell(p.TypeName),
                Cell(p.TotalFields.ToString("N0"),             p.TotalFields),
                Cell(p.ReferenceFields.ToString("N0"),         p.ReferenceFields),
                Cell(p.ValueFields.ToString("N0"),             p.ValueFields),
                Cell(p.ReferenceFieldRatio.ToString("F2")),
                Cell(p.InstanceCount.ToString("N0"),           (long)Math.Min(p.InstanceCount, long.MaxValue)),
                Cell(p.TotalSize.ToString("N0"),               (long)Math.Min(p.TotalSize, (ulong)long.MaxValue)),
                Cell(gcScanCost.ToString("N0"),                gcScanCost),
                Cell(p.IsFinalizable ? "Yes" : "No"),
                Cell(p.IsValueType  ? "Yes" : "No"),
                Cell(p.IsArray      ? "Yes" : "No"),
                Cell(p.BaseTypeChainDepth.ToString("N0"),      p.BaseTypeChainDepth),
                Cell(p.InterfaceCount.ToString("N0"),          p.InterfaceCount),
                Cell(p.Category.ToString())));
        }
        return rows;
    }
}
