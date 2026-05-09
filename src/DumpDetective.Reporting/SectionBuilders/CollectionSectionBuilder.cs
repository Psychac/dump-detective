using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class CollectionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Collection Analysis";
    public int SortOrder => 50;

    public bool CanHandle(AnalyzerDomainResult result) => result is CollectionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (CollectionDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("COLLECTION SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Collections", $"{d.TotalCollections:N0}", d.TotalCollections));
        blocks.Add(M("Dictionaries", $"{d.Dictionaries:N0}", d.Dictionaries, indent: 1));
        blocks.Add(M("Lists", $"{d.Lists:N0}", d.Lists, indent: 1));
        blocks.Add(M("ArrayLists", $"{d.ArrayLists:N0}", d.ArrayLists, indent: 1));
        blocks.Add(M("Stacks", $"{d.Stacks:N0}", d.Stacks, indent: 1));
        blocks.Add(M("SortedLists", $"{d.SortedLists:N0}", d.SortedLists, indent: 1));
        blocks.Add(M("SortedSets", $"{d.SortedSets:N0}", d.SortedSets, indent: 1));
        blocks.Add(M("HashSets", $"{d.HashSets:N0}", d.HashSets, indent: 1));
        blocks.Add(M("Queues", $"{d.Queues:N0}", d.Queues, indent: 1));
        blocks.Add(Blank());
        blocks.Add(M("Wasteful Collections", $"{d.WastefulCollectionCount:N0}", d.WastefulCollectionCount));
        blocks.Add(M("Total Wasted Memory", FormatHelper.FormatBytes(d.TotalWastedMemory), (double)d.TotalWastedMemory));

        var topWasteful = d.TopWastefulCollections ?? [];
        if (topWasteful.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("MOST WASTEFUL COLLECTIONS (Top 15)"));
            blocks.Add(Divider());

            var wcRows = new List<TableRow>(Math.Min(topWasteful.Count, 15));
            int limit = Math.Min(topWasteful.Count, 15);
            for (int i = 0; i < limit; i++)
            {
                var c = topWasteful[i];
                wcRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(c.Type, 60)),
                    Cell($"{c.Count:N0}", c.Count),
                    Cell($"{c.Capacity:N0}", c.Capacity),
                    Cell($"{c.FillRate:F1}%",   (long)(c.FillRate * 100)),
                    Cell(FormatHelper.FormatBytes(c.WastedMemory), (long)c.WastedMemory)]));
            }
            blocks.Add(new TableBlock("Wasteful collections", ["Type", "Count", "Capacity", "Fill Rate", "Wasted"], wcRows));
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
