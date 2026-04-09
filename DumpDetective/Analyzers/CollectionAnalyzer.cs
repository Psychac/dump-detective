using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class CollectionAnalyzer
    {
        private readonly OutputWriter _writer;

        public CollectionAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("COLLECTION EFFICIENCY ANALYSIS:");
            _writer.WriteLine("Analyzing dictionaries, lists, and other collections for waste...\n");

            var collectionStats = AnalyzeCollections(heap);

            if (collectionStats.TotalCollections == 0)
            {
                _writer.WriteLine("No collections found for analysis.");
                _writer.WriteLine(StringConstants.Equals80);
                return;
            }

            PrintCollectionSummary(collectionStats);
            PrintWastefulCollections(collectionStats);

            _writer.WriteLine(StringConstants.Equals80);
        }

        private CollectionStatistics AnalyzeCollections(ClrHeap heap)
        {
            var stats = new CollectionStatistics();
            var wasteful = new List<WastefulCollection>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "";

                // Analyze Dictionary
                if (typeName.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal))
                {
                    stats.TotalCollections++;
                    stats.Dictionaries++;
                    
                    var waste = AnalyzeDictionary(obj);
                    if (waste != null && waste.WastedMemory > 10 * 1024) // >10KB wasted
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
                    if (waste != null && waste.WastedMemory > 10 * 1024)
                    {
                        wasteful.Add(waste);
                    }
                }
                // Analyze HashSet
                else if (typeName.StartsWith("System.Collections.Generic.HashSet", StringComparison.Ordinal))
                {
                    stats.TotalCollections++;
                    stats.HashSets++;
                }
                // Analyze Queue
                else if (typeName.StartsWith("System.Collections.Generic.Queue", StringComparison.Ordinal))
                {
                    stats.TotalCollections++;
                    stats.Queues++;
                }
            }

            stats.WastefulCollections = wasteful.OrderByDescending(w => w.WastedMemory).ToList();
            stats.TotalWastedMemory = (ulong)wasteful.Sum(w => (long)w.WastedMemory);

            return stats;
        }

        private WastefulCollection? AnalyzeDictionary(ClrObject dictObj)
        {
            try
            {
                var countField = dictObj.Type?.GetFieldByName("_count");
                var bucketsField = dictObj.Type?.GetFieldByName("_buckets");

                if (countField == null || bucketsField == null)
                    return null;

                int count = countField.Read<int>(dictObj, interior: false);
                var bucketsObj = bucketsField.ReadObject(dictObj, interior: false);

                if (!bucketsObj.IsValid || !bucketsObj.IsArray)
                    return null;

                int capacity = bucketsObj.AsArray().Length;

                if (capacity > 0)
                {
                    double fillRate = (count / (double)capacity) * 100;
                    
                    // Calculate wasted memory
                    ulong elementSize = bucketsObj.Size / (ulong)capacity;
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

                int count = sizeField.Read<int>(listObj, interior: false);
                var itemsObj = itemsField.ReadObject(listObj, interior: false);

                if (!itemsObj.IsValid || !itemsObj.IsArray)
                    return null;

                int capacity = itemsObj.AsArray().Length;

                if (capacity > 0)
                {
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

            if (stats.TotalWastedMemory > 10 * 1024 * 1024)
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

            _writer.WriteLine($"\n\nMOST WASTEFUL COLLECTIONS (Top 15):");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-50} {"Count/Capacity",-15} {"Fill Rate",10} {"Wasted",12}");
            _writer.WriteSeparator();

            foreach (var waste in stats.WastefulCollections.Take(15))
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
