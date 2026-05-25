using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class CollectionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Collection Analysis";
    public string DisplayTitle => "Collection Health";
    public int SortOrder => 50;

    public bool CanHandle(AnalyzerDomainResult result) => result is CollectionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (CollectionDomainResult)result;
        var tables = new List<SectionTable>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total Collections",    $"{d.TotalCollections:N0}",                              d.TotalCollections),
            KM("Dictionaries",         $"{d.Dictionaries:N0}",                                 d.Dictionaries),
            KM("Lists",                $"{d.Lists:N0}",                                        d.Lists),
            KM("HashSets",             $"{d.HashSets:N0}",                                     d.HashSets),
            KM("Queues",               $"{d.Queues:N0}",                                       d.Queues),
            KM("Stacks",               $"{d.Stacks:N0}",                                       d.Stacks),
            KM("ArrayLists",           $"{d.ArrayLists:N0}",                                   d.ArrayLists),
            KM("SortedLists",          $"{d.SortedLists:N0}",                                  d.SortedLists),
            KM("SortedSets",           $"{d.SortedSets:N0}",                                   d.SortedSets),
            KM("Wasteful Collections", $"{d.WastefulCollectionCount:N0}",                      d.WastefulCollectionCount),
            KM("Total Wasted Memory",  FormatHelper.FormatBytes(d.TotalWastedMemory),          (double)d.TotalWastedMemory),
        };

        var inventoryRows = new List<TableRow>
        {
            new([Cell("Dictionary"),  Cell($"{d.Dictionaries:N0}", d.Dictionaries)]),
            new([Cell("List<T>"),     Cell($"{d.Lists:N0}",        d.Lists)]),
            new([Cell("HashSet<T>"),  Cell($"{d.HashSets:N0}",     d.HashSets)]),
            new([Cell("Queue<T>"),    Cell($"{d.Queues:N0}",       d.Queues)]),
            new([Cell("Stack<T>"),    Cell($"{d.Stacks:N0}",       d.Stacks)]),
            new([Cell("SortedList"),  Cell($"{d.SortedLists:N0}",  d.SortedLists)]),
            new([Cell("SortedSet"),   Cell($"{d.SortedSets:N0}",   d.SortedSets)]),
            new([Cell("ArrayList"),   Cell($"{d.ArrayLists:N0}",   d.ArrayLists)])
        };
        tables.Add(ST("Collection inventory", ["Kind", "Count"], inventoryRows));

        if (d.WasteCountsByKind is { Count: > 0 })
        {
            var wasteKindRows = new List<TableRow>(d.WasteCountsByKind.Count);
            foreach (var kvp in d.WasteCountsByKind)
                wasteKindRows.Add(new TableRow([Cell(kvp.Key.ToString()), Cell($"{kvp.Value:N0}", kvp.Value)]));
            tables.Add(ST("Wasteful collections by kind", ["Kind", "Wasteful Count"], wasteKindRows));
        }

        var topWasteful = d.TopWastefulCollections ?? [];
        if (topWasteful.Count > 0)
        {
            var wcRows = new List<TableRow>(Math.Min(topWasteful.Count, 15));
            int limit = Math.Min(topWasteful.Count, 15);
            for (int i = 0; i < limit; i++)
            {
                var c = topWasteful[i];
                wcRows.Add(new TableRow([
                    Cell(FormatHelper.TruncateString(c.Type, 60)),
                    Cell(c.Kind.ToString()),
                    Cell($"{c.Count:N0}", c.Count),
                    Cell($"{c.Capacity:N0}", c.Capacity),
                    Cell($"{c.FillRate:F1}%",   (long)(c.FillRate * 100)),
                    Cell(FormatHelper.FormatBytes(c.WastedMemory), (long)c.WastedMemory),
                    Cell(c.Head.HasValue ? c.Head.Value.ToString("N0") : "—", c.Head.HasValue ? c.Head.Value : null),
                    Cell(c.Tail.HasValue ? c.Tail.Value.ToString("N0") : "—", c.Tail.HasValue ? c.Tail.Value : null),
                    Cell(c.LargestContiguousFreeSegmentBytes.HasValue ? FormatHelper.FormatBytes(c.LargestContiguousFreeSegmentBytes.Value) : "—", c.LargestContiguousFreeSegmentBytes.HasValue ? (long)Math.Min(c.LargestContiguousFreeSegmentBytes.Value, (ulong)long.MaxValue) : null),
                    Cell(c.FreeSegmentCount.HasValue ? c.FreeSegmentCount.Value.ToString("N0") : "—", c.FreeSegmentCount.HasValue ? c.FreeSegmentCount.Value : null),
                    Cell(c.ElementType),
                    Cell(c.ElementSize > 0 ? FormatHelper.FormatBytes(c.ElementSize) : "—", c.ElementSize > 0 ? (long)Math.Min(c.ElementSize, (ulong)long.MaxValue) : null),
                    Cell(c.SizeEstimateConfidence),
                    Cell(c.DetectionMethod),
                    Cell(c.RootDescription ?? "—")]));
            }
            tables.Add(ST("Wasteful collections",
                ["Type", "Kind", "Count", "Capacity", "Fill Rate", "Wasted", "Head", "Tail",
                 "Largest Free Gap", "Free Segments", "Element Type", "Element Size", "Confidence", "Method", "Root"],
                wcRows));
            if (topWasteful.Count > limit)
            {
                // note will be shown in narrative blocks
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName, AnalyzerName, SortOrder, [],
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
