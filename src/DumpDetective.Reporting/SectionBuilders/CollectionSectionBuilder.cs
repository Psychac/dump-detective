using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;
using DumpDetective.Core.Enums;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class CollectionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Collection Analysis";
    public string DisplayTitle => "Collection Health";
    public int SortOrder => 300;

    public bool CanHandle(AnalyzerDomainResult result) => result is CollectionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (CollectionDomainResult)result;
        var compactTables = new List<CompactTable>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_collections"] = new NumericMetricValue(d.TotalCollections, MetricUnit.Count),
            ["dictionaries"] = new NumericMetricValue(d.Dictionaries, MetricUnit.Count),
            ["lists"] = new NumericMetricValue(d.Lists, MetricUnit.Count),
            ["hashsets"] = new NumericMetricValue(d.HashSets, MetricUnit.Count),
            ["queues"] = new NumericMetricValue(d.Queues, MetricUnit.Count),
            ["stacks"] = new NumericMetricValue(d.Stacks, MetricUnit.Count),
            ["arraylists"] = new NumericMetricValue(d.ArrayLists, MetricUnit.Count),
            ["sortedlists"] = new NumericMetricValue(d.SortedLists, MetricUnit.Count),
            ["sortedsets"] = new NumericMetricValue(d.SortedSets, MetricUnit.Count),
            ["immutable_arrays"] = new NumericMetricValue(d.ImmutableArrays, MetricUnit.Count),
            ["immutable_array_builders"] = new NumericMetricValue(d.ImmutableArrayBuilders, MetricUnit.Count),
            ["wasteful_collections"] = new NumericMetricValue(d.WastefulCollectionCount, MetricUnit.Count),
            ["total_wasted_memory"] = new NumericMetricValue((double)d.TotalWastedMemory, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalWastedMemory)),
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
            new([Cell("ArrayList"),   Cell($"{d.ArrayLists:N0}",   d.ArrayLists)]),
            new([Cell("ImmutableArray"),         Cell($"{d.ImmutableArrays:N0}",        d.ImmutableArrays)]),
            new([Cell("ImmutableArray.Builder"), Cell($"{d.ImmutableArrayBuilders:N0}", d.ImmutableArrayBuilders)])
        };
        compactTables.Add(STCompact("Collection inventory", new[] { CH("Kind"), CH("Count","number") }, inventoryRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        if (d.WasteCountsByKind is { Count: > 0 })
        {
            var wasteBytesByKind = d.WasteBytesByKind ?? new Dictionary<CollectionKind, ulong>();
            var wasteKinds = new List<CollectionKind>(d.WasteCountsByKind.Keys);
            wasteKinds.Sort((a, b) =>
            {
                wasteBytesByKind.TryGetValue(a, out ulong aBytes);
                wasteBytesByKind.TryGetValue(b, out ulong bBytes);
                int byBytes = bBytes.CompareTo(aBytes);
                return byBytes != 0 ? byBytes : string.CompareOrdinal(a.ToString(), b.ToString());
            });

            var wasteKindRows = new List<TableRow>(wasteKinds.Count);
            foreach (var kind in wasteKinds)
            {
                int count = d.WasteCountsByKind[kind];
                wasteBytesByKind.TryGetValue(kind, out ulong bytes);
                double shareOfTotal = d.TotalWastedMemory > 0 ? (double)bytes / d.TotalWastedMemory * 100.0 : 0.0;
                wasteKindRows.Add(new TableRow([
                    Cell(kind.ToString()),
                    Cell($"{count:N0}", count),
                    Cell(FormatHelper.FormatBytes(bytes), (long)Math.Min(bytes, (ulong)long.MaxValue)),
                    Cell($"{shareOfTotal:F1}%", shareOfTotal)]));
            }
            compactTables.Add(STCompact("Wasteful collections by kind",
                new[] { CH("Kind"), CH("Wasteful Count","number"), CH("Wasted","bytes"), CH("Share of Waste") },
                wasteKindRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                    Cell($"{c.FillRate:F1}%",   c.FillRate),
                    Cell(FormatHelper.FormatBytes(c.WastedMemory), (long)c.WastedMemory),
                    Cell(c.Head.HasValue ? c.Head.Value.ToString("N0") : "—", c.Head.HasValue ? c.Head.Value : null),
                    Cell(c.Tail.HasValue ? c.Tail.Value.ToString("N0") : "—", c.Tail.HasValue ? c.Tail.Value : null),
                    Cell(c.LargestContiguousFreeSegmentBytes.HasValue ? FormatHelper.FormatBytes(c.LargestContiguousFreeSegmentBytes.Value) : "—", c.LargestContiguousFreeSegmentBytes.HasValue ? (long)Math.Min(c.LargestContiguousFreeSegmentBytes.Value, (ulong)long.MaxValue) : null),
                    Cell(c.FreeSegmentCount.HasValue ? c.FreeSegmentCount.Value.ToString("N0") : "—", c.FreeSegmentCount.HasValue ? c.FreeSegmentCount.Value : null),
                    Cell(c.ElementType),
                    Cell(c.ElementSize > 0 ? FormatHelper.FormatBytes(c.ElementSize) : "—", c.ElementSize > 0 ? (long)Math.Min(c.ElementSize, (ulong)long.MaxValue) : null),
                    Cell(c.SizeEstimateConfidence),
                    Cell(c.DetectionMethod),
                    Cell(c.RootDescription ?? "—"),
                    Cell(c.Recommendation)]));
            }
            compactTables.Add(STCompact("Wasteful collections",
                new[] { CH("Type"), CH("Kind"), CH("Count","number"), CH("Capacity","number"), CH("Fill Rate"), CH("Wasted","bytes"), CH("Head","number"), CH("Tail","number"), CH("Largest Free Gap"), CH("Free Segments","number"), CH("Element Type"), CH("Element Size","bytes"), CH("Confidence"), CH("Method"), CH("Root"), CH("Recommendation") },
                wcRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            if (topWasteful.Count > limit)
            {
                // note will be shown in narrative blocks
            }
        }

        if (d.GenerationBreakdown is { Count: > 0 })
        {
            var genRows = new List<TableRow>(d.GenerationBreakdown.Count);
            foreach (var s in d.GenerationBreakdown)
            {
                genRows.Add(new TableRow([
                    Cell(s.Kind.ToString()),
                    Cell($"{s.Gen0Count:N0}", s.Gen0Count),
                    Cell($"{s.Gen1Count:N0}", s.Gen1Count),
                    Cell($"{s.Gen2Count:N0}", s.Gen2Count),
                    Cell($"{s.LohCount:N0}", s.LohCount)
                ]));
            }
            compactTables.Add(STCompact("Collections by GC generation", new[] { CH("Type"), CH("Gen0","number"), CH("Gen1","number"), CH("Gen2","number"), CH("LOH","number") }, genRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, [],
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
