using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["dependent_handles"] = new NumericMetricValue(d.DependentHandleCount, MetricUnit.Count),
            ["resolved_edges"] = new NumericMetricValue(d.ResolvedEdgeCount, MetricUnit.Count),
            ["unresolved_targets"] = new NumericMetricValue(d.UnresolvedTargetCount, MetricUnit.Count),
            ["unresolved_targets_pct"] = new NumericMetricValue(d.UnresolvedPercent, MetricUnit.Percent),
        };
        var compactTables = new List<CompactTable>();

        var sourceTypes = d.TopSourceTypes ?? [];
        if (sourceTypes.Count > 0)
        {
            var stRows = new List<TableRow>(sourceTypes.Count);
            for (int i = 0; i < sourceTypes.Count; i++)
                stRows.Add(new TableRow([Cell(sourceTypes[i].Name), Cell($"{sourceTypes[i].Count:N0}", sourceTypes[i].Count)]));
            compactTables.Add(STCompact("Source type distribution", new[] { CH("Type"), CH("Count","number") }, stRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var targetTypes = d.TopTargetTypes ?? [];
        if (targetTypes.Count > 0)
        {
            var ttRows = new List<TableRow>(targetTypes.Count);
            for (int i = 0; i < targetTypes.Count; i++)
                ttRows.Add(new TableRow([Cell(targetTypes[i].Name), Cell($"{targetTypes[i].Count:N0}", targetTypes[i].Count)]));
            compactTables.Add(STCompact("Target type distribution", new[] { CH("Type"), CH("Count","number") }, ttRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var sourceTargetEdges = d.TopSourceTargetEdges;
        if (sourceTargetEdges != null && sourceTargetEdges.Count > 0)
        {
            var edgeRows = new List<TableRow>(sourceTargetEdges.Count);
            for (int i = 0; i < sourceTargetEdges.Count; i++)
                edgeRows.Add(new TableRow(new[] { Cell(sourceTargetEdges[i].Name), Cell(sourceTargetEdges[i].Count.ToString("N0"), sourceTargetEdges[i].Count) }));
            compactTables.Add(STCompact("Source to target pairs", new[] { CH("Pair"), CH("Count","number") }, edgeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, AnalyzerName, SortOrder, [],
                KeyMetrics: keyMetrics,
                CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
