using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class DependentHandleSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Dependent Handle Analysis";
    public string DisplayTitle => "Dependent Handles";
    public int SortOrder => 730;

    public bool CanHandle(AnalyzerDomainResult result) => result is DependentHandleDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (DependentHandleDomainResult)result;
        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Dependent Handles", $"{d.DependentHandleCount:N0}", d.DependentHandleCount),
            KM("Resolved Edges", $"{d.ResolvedEdgeCount:N0}", d.ResolvedEdgeCount),
            KM("Unresolved Targets", $"{d.UnresolvedTargetCount:N0}  ({d.UnresolvedPercent:F1}%)", d.UnresolvedTargetCount),
        };
        var tables = new List<SectionTable>();

        var sourceTypes = d.TopSourceTypes ?? [];
        if (sourceTypes.Count > 0)
        {
            var stRows = new List<TableRow>(sourceTypes.Count);
            for (int i = 0; i < sourceTypes.Count; i++)
                stRows.Add(new TableRow([Cell(sourceTypes[i].Name), Cell($"{sourceTypes[i].Count:N0}", sourceTypes[i].Count)]));
            tables.Add(ST("Source type distribution", ["Type", "Count"], stRows));
        }

        var targetTypes = d.TopTargetTypes ?? [];
        if (targetTypes.Count > 0)
        {
            var ttRows = new List<TableRow>(targetTypes.Count);
            for (int i = 0; i < targetTypes.Count; i++)
                ttRows.Add(new TableRow([Cell(targetTypes[i].Name), Cell($"{targetTypes[i].Count:N0}", targetTypes[i].Count)]));
            tables.Add(ST("Target type distribution", ["Type", "Count"], ttRows));
        }

        var sourceTargetEdges = d.TopSourceTargetEdges;
        if (sourceTargetEdges != null && sourceTargetEdges.Count > 0)
        {
            var edgeRows = new List<TableRow>(sourceTargetEdges.Count);
            for (int i = 0; i < sourceTargetEdges.Count; i++)
                edgeRows.Add(new TableRow(new[] { Cell(sourceTargetEdges[i].Name), Cell(sourceTargetEdges[i].Count.ToString("N0"), sourceTargetEdges[i].Count) }));
            tables.Add(ST("Source to target pairs", new[] { "Pair", "Count" }, edgeRows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, AnalyzerName, SortOrder, [],
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
