using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ObjectShapeSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 20;

    public string AnalyzerName => "Object Shape Analysis";
    public int SortOrder => 33;

    public bool CanHandle(AnalyzerDomainResult result) => result is ObjectShapeAnalyzerDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ObjectShapeAnalyzerDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ───────────────────────────────────────────────────────────
        blocks.Add(H("OBJECT SHAPE SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Types Analyzed",        $"{d.TotalTypesAnalyzed:N0}",     d.TotalTypesAnalyzed));
        blocks.Add(M("Avg Ref Fields / Type",  $"{d.AvgRefFieldsPerType:F2}",   d.AvgRefFieldsPerType));
        blocks.Add(M("Reference-Heavy Types",  $"{d.TopReferenceHeavyTypes.Count:N0}", d.TopReferenceHeavyTypes.Count));
        blocks.Add(M("Value-Heavy Types",      $"{d.TopValueHeavyTypes.Count:N0}",     d.TopValueHeavyTypes.Count));

        // ── Reference-heavy types ─────────────────────────────────────────────
        if (d.TopReferenceHeavyTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP REFERENCE-HEAVY TYPES (HIGH GC SCAN COST)"));
            blocks.Add(new TableBlock(
                Caption: "Top reference-heavy types",
                Headers: ["Type", "Instances", "Ref Fields", "Val Fields", "Ref Ratio", "Finalizable", "Base Depth"],
                Rows: BuildShapeRows(d.TopReferenceHeavyTypes)));
        }

        // ── Value-heavy types ─────────────────────────────────────────────────
        if (d.TopValueHeavyTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP VALUE-HEAVY TYPES (LOW GC SCAN COST)"));
            blocks.Add(new TableBlock(
                Caption: "Top value-heavy types",
                Headers: ["Type", "Instances", "Ref Fields", "Val Fields", "Ref Ratio", "IsValueType", "Interfaces"],
                Rows: BuildShapeRows(d.TopValueHeavyTypes, valueMode: true)));
        }

        return new AnalyzerDetailSection(AnalyzerName, "Object Shape Analysis", SortOrder, blocks);
    }

    private static List<TableRow> BuildShapeRows(IReadOnlyList<TypeShapeProfile> types, bool valueMode = false)
    {
        int limit = Math.Min(types.Count, TopTypeRows);
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            TypeShapeProfile t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.TypeName, 70)),
                Cell($"{t.InstanceCount:N0}", (long)Math.Min(t.InstanceCount, long.MaxValue)),
                Cell($"{t.ReferenceFields:N0}", t.ReferenceFields),
                Cell($"{t.ValueFields:N0}",     t.ValueFields),
                Cell($"{t.ReferenceFieldRatio:P0}"),
                valueMode
                    ? Cell(t.IsValueType    ? "yes" : "no")
                    : Cell(t.IsFinalizable  ? "yes" : "no"),
                valueMode
                    ? Cell($"{t.InterfaceCount:N0}", t.InterfaceCount)
                    : Cell($"{t.BaseTypeChainDepth:N0}", t.BaseTypeChainDepth),
            ]));
        }
        return rows;
    }
}
