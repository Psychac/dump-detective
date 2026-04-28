using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class DependentHandleSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Dependent Handle Analysis";
    public int SortOrder => 46;

    public bool CanHandle(AnalyzerDomainResult result) => result is DependentHandleDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (DependentHandleDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("DEPENDENT HANDLE SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Dependent Handles",       $"{d.DependentHandleCount:N0}",   d.DependentHandleCount));
        blocks.Add(M("Resolved Edges",          $"{d.ResolvedEdgeCount:N0}",      d.ResolvedEdgeCount));
        blocks.Add(M("Unresolved Targets",      $"{d.UnresolvedTargetCount:N0}  ({d.UnresolvedPercent:F1}%)", d.UnresolvedTargetCount));

        var sourceTypes = d.TopSourceTypes ?? [];
        if (sourceTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("SOURCE TYPE DISTRIBUTION"));
            blocks.Add(Divider());

            var stRows = new List<TableRow>(sourceTypes.Count);
            for (int i = 0; i < sourceTypes.Count; i++)
                stRows.Add(new TableRow([Cell(sourceTypes[i].Name), Cell($"{sourceTypes[i].Count:N0}", sourceTypes[i].Count)]));
            blocks.Add(new TableBlock("Source type distribution", ["Type", "Count"], stRows));
        }

        var targetTypes = d.TopTargetTypes ?? [];
        if (targetTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TARGET TYPE DISTRIBUTION"));
            blocks.Add(Divider());

            var ttRows = new List<TableRow>(targetTypes.Count);
            for (int i = 0; i < targetTypes.Count; i++)
                ttRows.Add(new TableRow([Cell(targetTypes[i].Name), Cell($"{targetTypes[i].Count:N0}", targetTypes[i].Count)]));
            blocks.Add(new TableBlock("Target type distribution", ["Type", "Count"], ttRows));
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
