using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCHandleSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "GC Handle Analysis";
    public int SortOrder => 45;

    public bool CanHandle(AnalyzerDomainResult result) => result is GCHandleDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (GCHandleDomainResult)result;
        var blocks = new List<SectionBlock>();

        double strongPct  = d.TotalHandles == 0 ? 0 : d.StrongLikeHandles   * 100.0 / d.TotalHandles;
        double weakPct    = d.TotalHandles == 0 ? 0 : d.WeakLikeHandles     * 100.0 / d.TotalHandles;
        double pinnedPct  = d.TotalHandles == 0 ? 0 : d.PinnedHandleTargets * 100.0 / d.TotalHandles;

        blocks.Add(H("HANDLE SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Handles",          $"{d.TotalHandles:N0}",                              d.TotalHandles));
        blocks.Add(M("Strong-like Handles",    $"{d.StrongLikeHandles:N0}  ({strongPct:F1}%)",      d.StrongLikeHandles));
        blocks.Add(M("Weak-like Handles",      $"{d.WeakLikeHandles:N0}  ({weakPct:F1}%)",          d.WeakLikeHandles));
        blocks.Add(M("Pinned Handle Targets",  $"{d.PinnedHandleTargets:N0}  ({pinnedPct:F1}%)",    d.PinnedHandleTargets));

        var byKind = d.HandlesByKind ?? [];
        if (byKind.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("HANDLES BY KIND"));
            blocks.Add(Divider());

            var kindRows = new List<TableRow>(byKind.Count);
            for (int i = 0; i < byKind.Count; i++)
            {
                var entry = byKind[i];
                double pct = d.TotalHandles == 0 ? 0 : entry.Count * 100.0 / d.TotalHandles;
                kindRows.Add(new TableRow([
                    Cell(entry.Name),
                    Cell($"{entry.Count:N0}", entry.Count),
                    Cell($"{pct:F1}%")]));
            }
            blocks.Add(new TableBlock("Handles by kind", ["Kind", "Count", "% Total"], kindRows));
        }

        var topTargets = d.TopTargetTypes ?? [];
        if (topTargets.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP TYPES REFERENCED BY HANDLES"));
            blocks.Add(Divider());

            var ttRows = new List<TableRow>(topTargets.Count);
            for (int i = 0; i < topTargets.Count; i++)
                ttRows.Add(new TableRow([Cell(topTargets[i].Name), Cell($"{topTargets[i].Count:N0}", topTargets[i].Count)]));
            blocks.Add(new TableBlock("Top handle target types", ["Type", "Count"], ttRows));
        }

        var pinnedTypes = d.TopPinnedTargetTypes ?? [];
        if (pinnedTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP PINNED HANDLE TARGET TYPES"));
            blocks.Add(Divider());

            var ptRows = new List<TableRow>(pinnedTypes.Count);
            for (int i = 0; i < pinnedTypes.Count; i++)
                ptRows.Add(new TableRow([Cell(pinnedTypes[i].Name), Cell($"{pinnedTypes[i].Count:N0}", pinnedTypes[i].Count)]));
            blocks.Add(new TableBlock("Pinned handle target types", ["Type", "Count"], ptRows));
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
