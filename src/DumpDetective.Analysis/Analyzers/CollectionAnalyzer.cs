using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
public class CollectionAnalyzer : IAnalyzer
    {
        private const ulong WasteThresholdBytes = 10 * 1024;           // 10 KB per collection
        private const ulong SummaryWarnThresholdBytes = 10 * 1024 * 1024; // 10 MB total
        private const int TopWastefulCollectionsToShow = 15;

        public string Name => "Collection Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap)
        {
            return Analyze(heap, cache: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            var collectionStats = AnalyzeCollections(heap, cache);
            var domainResult = new CollectionDomainResult(
                collectionStats.TotalCollections,
                collectionStats.Dictionaries,
                collectionStats.Lists,
                collectionStats.HashSets,
                collectionStats.Queues,
                collectionStats.TotalWastedMemory,
                collectionStats.WastefulCollections.Count,
                collectionStats.WastefulCollections
                    .Take(TopWastefulCollectionsToShow)
                    .Select(w => new WastefulCollectionSnapshot(
                        w.Type,
                        w.Count,
                        w.Capacity,
                        w.FillRate,
                        w.WastedMemory,
                        w.Address))
                    .ToList());

            if (collectionStats.TotalCollections == 0)
            {
                return domainResult;
            }

            return domainResult;
        }

        private static InsightFinding CreateFinding(CollectionStatistics stats)
        {
            FindingSeverity severity = stats.TotalWastedMemory >= SummaryWarnThresholdBytes
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(CollectionAnalyzer),
                Category: "Memory",
                Severity: severity,
                Title: "Collection capacity efficiency",
                Evidence: $"{stats.TotalCollections:N0} collections scanned; estimated unused capacity {FormatHelper.FormatBytes(stats.TotalWastedMemory)} across {stats.WastefulCollections.Count:N0} wasteful collections.",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Trim long-lived collections and initialize with realistic capacities."
                    : "Collection sizing appears acceptable in this snapshot.",
                Tags: ["collections", "memory-waste", "capacity"],
                MetricValue: stats.TotalWastedMemory,
                MetricUnit: "wasted-bytes");
        }

        private CollectionStatistics AnalyzeCollections(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            var stats = new CollectionStatistics();
            var wasteful = new List<WastefulCollection>();
            var methodTableKinds = new Dictionary<ulong, CollectionKind>(capacity: 64);
            var scanCounter = new ObjectScanCounter("Collection scan");

            foreach (HeapEntry entry in EnumerateCollectionEntries(heap, cache))
            {
                scanCounter.Tick();

                ulong objectAddress = entry.Address;
                if (objectAddress == 0)
                    continue;

                CollectionKind kind = ResolveCollectionKind(heap, entry, methodTableKinds);

                // Analyze Dictionary
                if (kind == CollectionKind.Dictionary)
                {
                    stats.TotalCollections++;
                    stats.Dictionaries++;

                    var waste = AnalyzeDictionary(heap, objectAddress);
                    if (waste != null && waste.WastedMemory > WasteThresholdBytes)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze List
                else if (kind == CollectionKind.List)
                {
                    stats.TotalCollections++;
                    stats.Lists++;
                    
                    var waste = AnalyzeList(heap, objectAddress);
                    if (waste != null && waste.WastedMemory > WasteThresholdBytes)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze HashSet
                else if (kind == CollectionKind.HashSet)
                {
                    stats.TotalCollections++;
                    stats.HashSets++;

                    var waste = AnalyzeHashSet(heap, objectAddress);
                    if (waste != null && waste.WastedMemory > WasteThresholdBytes)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze Queue
                else if (kind == CollectionKind.Queue)
                {
                    stats.TotalCollections++;
                    stats.Queues++;
                }
            }

            scanCounter.Complete();

            stats.WastefulCollections = wasteful.OrderByDescending(w => w.WastedMemory).ToList();
            stats.TotalWastedMemory = wasteful.Aggregate(0UL, (acc, w) => acc + w.WastedMemory);

            return stats;
        }

        private static IEnumerable<HeapEntry> EnumerateCollectionEntries(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
            {
                foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                    yield return entry;

                yield break;
            }

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null)
                    continue;

                ulong methodTable = obj.Type.MethodTable;
                if (methodTable == 0)
                    continue;

                yield return new HeapEntry(obj.Address, methodTable, obj.Size);
            }
        }

        private static CollectionKind ResolveCollectionKind(ClrHeap heap, in HeapEntry entry, Dictionary<ulong, CollectionKind> methodTableKinds)
        {
            if (entry.MethodTable == 0)
                return CollectionKind.None;

            if (methodTableKinds.TryGetValue(entry.MethodTable, out CollectionKind existing))
                return existing;

            ClrObject obj = heap.GetObject(entry.Address);
            string typeName = obj.IsValid ? (obj.Type?.Name ?? string.Empty) : string.Empty;

            CollectionKind resolved = CollectionKind.None;
            if (typeName.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal))
                resolved = CollectionKind.Dictionary;
            else if (typeName.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal))
                resolved = CollectionKind.List;
            else if (typeName.StartsWith("System.Collections.Generic.HashSet", StringComparison.Ordinal))
                resolved = CollectionKind.HashSet;
            else if (typeName.StartsWith("System.Collections.Generic.Queue", StringComparison.Ordinal))
                resolved = CollectionKind.Queue;

            methodTableKinds[entry.MethodTable] = resolved;
            return resolved;
        }

        private enum CollectionKind
        {
            None,
            Dictionary,
            List,
            HashSet,
            Queue
        }

        private WastefulCollection? AnalyzeDictionary(ClrHeap heap, ulong dictionaryAddress)
        {
            try
            {
                ClrObject dictObj = heap.GetObject(dictionaryAddress);
                if (!dictObj.IsValid || dictObj.Type == null)
                    return null;

                var countField = dictObj.Type?.GetFieldByName("_count");
                var entriesField = dictObj.Type?.GetFieldByName("_entries");

                if (countField == null || entriesField == null)
                    return null;

                int count = Math.Max(0, countField.Read<int>(dictObj, interior: false));
                var entriesObj = entriesField.ReadObject(dictObj, interior: false);

                if (!entriesObj.IsValid || !entriesObj.IsArray)
                    return null;

                int capacity = entriesObj.AsArray().Length;

                // No waste if fully packed or empty
                if (capacity <= 0 || count >= capacity)
                    return null;

                double fillRate = (count / (double)capacity) * 100;
                ulong elementSize = entriesObj.Size / (ulong)capacity;
                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                return new WastefulCollection
                {
                    Address = dictObj.Address,
                    Type = dictObj.Type?.Name ?? "Dictionary",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory
                };
            }
            catch { }

            return null;
        }

        private WastefulCollection? AnalyzeList(ClrHeap heap, ulong listAddress)
        {
            try
            {
                ClrObject listObj = heap.GetObject(listAddress);
                if (!listObj.IsValid || listObj.Type == null)
                    return null;

                var sizeField = listObj.Type?.GetFieldByName("_size");
                var itemsField = listObj.Type?.GetFieldByName("_items");

                if (sizeField == null || itemsField == null)
                    return null;

                int count = Math.Max(0, sizeField.Read<int>(listObj, interior: false));
                var itemsObj = itemsField.ReadObject(listObj, interior: false);

                if (!itemsObj.IsValid || !itemsObj.IsArray)
                    return null;

                int capacity = itemsObj.AsArray().Length;

                // No waste if fully packed or empty
                if (capacity <= 0 || count >= capacity)
                    return null;

                double fillRate = (count / (double)capacity) * 100;
                ulong elementSize = itemsObj.Size / (ulong)capacity;
                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                return new WastefulCollection
                {
                    Address = listObj.Address,
                    Type = listObj.Type?.Name ?? "List",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory
                };
            }
            catch { }

            return null;
        }

        private WastefulCollection? AnalyzeHashSet(ClrHeap heap, ulong hashSetAddress)
        {
            try
            {
                ClrObject hashSetObj = heap.GetObject(hashSetAddress);
                if (!hashSetObj.IsValid || hashSetObj.Type == null)
                    return null;

                var countField = hashSetObj.Type?.GetFieldByName("_count");
                var entriesField = hashSetObj.Type?.GetFieldByName("_entries");

                if (countField == null || entriesField == null)
                    return null;

                int count = Math.Max(0, countField.Read<int>(hashSetObj, interior: false));
                var entriesObj = entriesField.ReadObject(hashSetObj, interior: false);

                if (!entriesObj.IsValid || !entriesObj.IsArray)
                    return null;

                int capacity = entriesObj.AsArray().Length;

                // No waste if fully packed or empty
                if (capacity <= 0 || count >= capacity)
                    return null;

                double fillRate = (count / (double)capacity) * 100;
                ulong elementSize = entriesObj.Size / (ulong)capacity;
                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                return new WastefulCollection
                {
                    Address = hashSetObj.Address,
                    Type = hashSetObj.Type?.Name ?? "HashSet",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory
                };
            }
            catch { }

            return null;
        }

    }

    internal class CollectionStatistics
    {
        public int TotalCollections { get; set; }
        public int Dictionaries { get; set; }
        public int Lists { get; set; }
        public int HashSets { get; set; }
        public int Queues { get; set; }
        public ulong TotalWastedMemory { get; set; }
        public List<WastefulCollection> WastefulCollections { get; set; } = new();
    }

    internal class WastefulCollection
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
        public int Count { get; set; }
        public int Capacity { get; set; }
        public double FillRate { get; set; }
        public ulong WastedMemory { get; set; }
    }
}


