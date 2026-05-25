using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total types analyzed",    d.TotalTypesAnalyzed.ToString("N0"),  d.TotalTypesAnalyzed),
            KM("Avg ref fields per type", $"{d.AvgRefFieldsPerType:F1}",        d.AvgRefFieldsPerType),
            KM("Reference-heavy types",  d.TopReferenceHeavyTypes.Count.ToString("N0"), d.TopReferenceHeavyTypes.Count),
            KM("Value-heavy types",      d.TopValueHeavyTypes.Count.ToString("N0"),     d.TopValueHeavyTypes.Count),
        };

        if (d.TopReferenceHeavyTypes.Count > 0)
            tables.Add(ST(
                "Reference-heavy types",
                ["Type", "Total Fields", "Ref Fields", "Val Fields", "Ref Ratio", "Instances", "Finalizable", "Value Type", "Array", "Chain Depth", "Interfaces", "Category"],
                BuildShapeRows(d.TopReferenceHeavyTypes)));

        if (d.TopValueHeavyTypes.Count > 0)
            tables.Add(ST(
                "Value-heavy types",
                ["Type", "Total Fields", "Ref Fields", "Val Fields", "Ref Ratio", "Instances", "Finalizable", "Value Type", "Array", "Chain Depth", "Interfaces", "Category"],
                BuildShapeRows(d.TopValueHeavyTypes)));

        blocks.Add(T("Reference-heavy types (ratio > 0.6) are candidates for GC root retention and may inflate promotion pressure. Value-heavy types (ratio < 0.2) with large struct sizes can cause excess stack pressure or LOH allocation."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static List<TableRow> BuildShapeRows(IReadOnlyList<TypeShapeProfile> types)
    {
        var rows = new List<TableRow>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            TypeShapeProfile p = types[i];
            rows.Add(Row(
                Cell(p.TypeName),
                Cell(p.TotalFields.ToString("N0"),             p.TotalFields),
                Cell(p.ReferenceFields.ToString("N0"),         p.ReferenceFields),
                Cell(p.ValueFields.ToString("N0"),             p.ValueFields),
                Cell(p.ReferenceFieldRatio.ToString("F2")),
                Cell(p.InstanceCount.ToString("N0"),           (long)Math.Min(p.InstanceCount, long.MaxValue)),
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
