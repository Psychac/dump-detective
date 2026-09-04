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

    private static readonly CompactHeader[] ShapeTableHeaders =
    [
        CH("Type"), CH("Total Fields","number"), CH("Ref Fields","number"), CH("Val Fields","number"),
        CH("Ref Ratio"), CH("Instances","number"), CH("Size (bytes)","number"), CH("GC Scan Cost","number"),
        CH("Gen2 Instances","number"), CH("Gen2 Scan Cost","number"),
        CH("Finalizable"), CH("Value Type"), CH("Array"), CH("Chain Depth","number"), CH("Interfaces","number"), CH("Category"),
    ];

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
            ["total_gen2_gc_scan_work"] = new NumericMetricValue(d.TotalGen2GcScanWork, MetricUnit.Custom, d.TotalGen2GcScanWork.ToString("N0")),
            ["reference_heavy_types"] = new NumericMetricValue(d.TopReferenceHeavyTypes.Count, MetricUnit.Count),
            ["balanced_types"] = new NumericMetricValue(d.TopBalancedTypes.Count, MetricUnit.Count),
            ["value_heavy_types"] = new NumericMetricValue(d.TopValueHeavyTypes.Count, MetricUnit.Count),
        };

        // Reference-heavy/Value-heavy/Balanced types are NOT emitted as their own tables: the
        // analyzer builds TopGen2RetainedTypes as the exact concatenation of those three lists
        // (ObjectShapeAnalyzer.cs), so emitting all four duplicated ~1.78 MB of identical rows on
        // a large dump for no additional information (docs/refactor/report-payload-size-reduction-design.md,
        // F3). The Category column below still distinguishes them; the existing per-table search
        // box already filters rows by any visible column's text, so typing e.g. "ReferenceHeavy"
        // reproduces what a dedicated table gave for free.
        if (d.TopGen2RetainedTypes.Count > 0)
        {
            compactTables.Add(STCompact(
                "Gen2-retained types (retention-adjusted GC scan cost)",
                ShapeTableHeaders,
                BuildShapeRows(d.TopGen2RetainedTypes).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        blocks.Add(T("Reference-heavy types (ratio > 0.6) are candidates for GC root retention and may inflate promotion pressure. Balanced types (ratio 0.2–0.6) are the numerically dominant heap residents. Value-heavy types (ratio < 0.2) with large struct sizes can cause excess stack pressure or LOH allocation. " +
                    "(Array analysis is handled by ArrayAnalyzer.) " +
                    "Gen2-retained types are ranked by reference fields × Gen2 instance count — GC scan cost that is paid on every Gen2 collection rather than collected away cheaply in Gen0/Gen1."));

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
            long gen2ScanCost = (long)(p.ReferenceFields * (double)p.Gen2InstanceCount);
            rows.Add(Row(
                Cell(p.TypeName),
                Cell(p.TotalFields.ToString("N0"),             p.TotalFields),
                Cell(p.ReferenceFields.ToString("N0"),         p.ReferenceFields),
                Cell(p.ValueFields.ToString("N0"),             p.ValueFields),
                Cell(p.ReferenceFieldRatio.ToString("F2")),
                Cell(p.InstanceCount.ToString("N0"),           (long)Math.Min(p.InstanceCount, long.MaxValue)),
                Cell(p.TotalSize.ToString("N0"),               (long)Math.Min(p.TotalSize, (ulong)long.MaxValue)),
                Cell(gcScanCost.ToString("N0"),                gcScanCost),
                Cell(p.Gen2InstanceCount.ToString("N0"),       (long)Math.Min(p.Gen2InstanceCount, long.MaxValue)),
                Cell(gen2ScanCost.ToString("N0"),              gen2ScanCost),
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
