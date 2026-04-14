using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class CollectionAnalyzer
    {
        private const ulong WasteThresholdBytes = 10 * 1024;           // 10 KB per collection
        private const ulong SummaryWarnThresholdBytes = 10 * 1024 * 1024; // 10 MB total
        private const int TopWastefulCount = 15;

        private readonly OutputWriter _writer;

        public CollectionAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("COLLECTION EFFICIENCY ANALYSIS:");

            var collectionStats = AnalyzeCollections(heap);
            var domainResult = new CollectionDomainResult(
                collectionStats.TotalCollections,
                collectionStats.Dictionaries,
                collectionStats.Lists,
                collectionStats.HashSets,
                collectionStats.TotalWastedMemory,
                collectionStats.WastefulCollections.Count);

            if (collectionStats.TotalCollections == 0)
            {
                _writer.WriteLine("No collections found for analysis.");
                _writer.WriteLine(StringConstants.Equals80);
                return new AnalyzerOutput(
                    [new InsightFinding(
                        Analyzer: nameof(CollectionAnalyzer),
                        Category: "Memory",
                        Severity: FindingSeverity.Info,
                        Title: "No generic collections detected",
                        Evidence: "Collection analyzer did not find list/dictionary/hashset instances for evaluation.",
                        Recommendation: "No collection-sizing action required from this snapshot.",
                        Tags: ["collections", "capacity"],
                        MetricValue: 0,
                        MetricUnit: "wasted-bytes")],
                    domainResult);
            }

            PrintCollectionSummary(collectionStats);
            PrintWastefulCollections(collectionStats);

            _writer.WriteLine(StringConstants.Equals80);
            return new AnalyzerOutput([CreateFinding(collectionStats)], domainResult);
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

        private CollectionStatistics AnalyzeCollections(ClrHeap heap)
        {
            var stats = new CollectionStatistics();
            var wasteful = new List<WastefulCollection>();
            var scanCounter = new ObjectScanCounter("Collection scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();

                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "";

                // Analyze Dictionary
                if (typeName.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal))
                {
                    stats.TotalCollections++;
                    stats.Dictionaries++;

                    var waste = AnalyzeDictionary(obj);
                    if (waste != null && waste.WastedMemory > WasteThresholdBytes)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze List
                else if (typeName.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal))
                {
                    stats.TotalCollections++;
                    stats.Lists++;
                    
                    var waste = AnalyzeList(obj);
                    if (waste != null && waste.WastedMemory > WasteThresholdBytes)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze HashSet
                else if (typeName.StartsWith("System.Collections.Generic.HashSet", StringComparison.Ordinal))
                {
                    stats.TotalCollections++;
                    stats.HashSets++;

                    var waste = AnalyzeHashSet(obj);
                    if (waste != null && waste.WastedMemory > WasteThresholdBytes)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze Queue
                else if (typeName.StartsWith("System.Collections.Generic.Queue", StringComparison.Ordinal))
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

        private WastefulCollection? AnalyzeDictionary(ClrObject dictObj)
        {
            try
            {
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

        private WastefulCollection? AnalyzeList(ClrObject listObj)
        {
            try
            {
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

        private WastefulCollection? AnalyzeHashSet(ClrObject hashSetObj)
        {
            try
            {
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

        private void PrintCollectionSummary(CollectionStatistics stats)
        {
            _writer.WriteLine("COLLECTION SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Collections: {stats.TotalCollections:N0}");
            _writer.WriteLine($"  Dictionaries: {stats.Dictionaries:N0}");
            _writer.WriteLine($"  Lists: {stats.Lists:N0}");
            _writer.WriteLine($"  HashSets: {stats.HashSets:N0}");
            _writer.WriteLine($"  Queues: {stats.Queues:N0}");
            _writer.WriteLine($"\nTotal Wasted Memory: {FormatHelper.FormatBytes(stats.TotalWastedMemory)}");

            if (stats.TotalWastedMemory > SummaryWarnThresholdBytes)
            {
                _writer.WriteLine($"⚠️  Over 10 MB wasted in under-filled collections!");
            }
        }

        private void PrintWastefulCollections(CollectionStatistics stats)
        {
            if (stats.WastefulCollections.Count == 0)
            {
                _writer.WriteLine("\n✅ No significantly wasteful collections detected.");
                return;
            }

            _writer.WriteLine($"\n\nMOST WASTEFUL COLLECTIONS (Top {TopWastefulCount}):");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-50} {"Count/Capacity",-15} {"Fill Rate",10} {"Wasted",12}");
            _writer.WriteSeparator();

            foreach (var waste in stats.WastefulCollections.Take(TopWastefulCount))
            {
                string countCapacity = $"{waste.Count}/{waste.Capacity}";
                _writer.WriteLine($"{FormatHelper.TruncateString(waste.Type, 50),-50} {countCapacity,-15} {waste.FillRate,9:F1}% {FormatHelper.FormatBytes(waste.WastedMemory),12}");
                _writer.WriteLine($"  Address: 0x{waste.Address:X}");
                
                if (waste.FillRate < 25)
                {
                    _writer.WriteLine($"  ⚠️  Very low fill rate - consider using TrimExcess() or right-sizing capacity");
                }
                _writer.WriteLine(string.Empty);
            }

            _writer.WriteLine("💡 OPTIMIZATION TIPS:");
            _writer.WriteLine("   - Use collection.TrimExcess() to reclaim unused capacity");
            _writer.WriteLine("   - Initialize collections with appropriate capacity");
            _writer.WriteLine("   - For dictionaries: new Dictionary<>(expectedCount)");
            _writer.WriteLine("   - For lists: new List<>(expectedCount)");
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
