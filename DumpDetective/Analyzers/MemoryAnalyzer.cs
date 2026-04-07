using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryAnalyzer
    {
        private readonly OutputWriter _writer;
        private const int LOH_THRESHOLD = 85000;

        public MemoryAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("MEMORY ANALYSIS:");

            // Use cached type statistics instead of re-enumerating
            var typeStats = cache.GetOrBuildTypeStatistics(heap);
            var stats = ConvertToMemoryStatistics(typeStats);

            PrintSummary(stats);
            PrintTopObjectsByCount(stats.TypeStats);
            PrintTopObjectsBySize(stats.TypeStats);
            PrintLOHUsage(stats);

            _writer.WriteLine($"\n{StringConstants.Equals80}");
        }

        private MemoryStatistics ConvertToMemoryStatistics(Dictionary<string, TypeStatistics> typeStats)
        {
            int totalObjects = 0;
            ulong totalMemory = 0;
            int totalLohObjects = 0;
            ulong totalLohMemory = 0;

            var enhancedStats = new Dictionary<string, EnhancedTypeStats>(typeStats.Count);

            foreach (var kvp in typeStats)
            {
                var stat = kvp.Value;
                totalObjects += stat.Count;
                totalMemory += stat.TotalSize;
                totalLohObjects += stat.LohCount;
                totalLohMemory += stat.LohSize;

                enhancedStats[kvp.Key] = new EnhancedTypeStats
                {
                    TypeName = stat.TypeName,
                    Count = stat.Count,
                    TotalSize = stat.TotalSize,
                    LohCount = stat.LohCount,
                    LohSize = stat.LohSize
                };
            }

            return new MemoryStatistics
            {
                TypeStats = enhancedStats,
                TotalObjects = totalObjects,
                TotalMemory = totalMemory,
                TotalLohObjects = totalLohObjects,
                TotalLohMemory = totalLohMemory
            };
        }

        private void PrintSummary(MemoryStatistics stats)
        {
            _writer.WriteLine("\nOVERALL SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Objects: {stats.TotalObjects:N0}");
            _writer.WriteLine($"Total Memory: {FormatHelper.FormatBytes(stats.TotalMemory)}");
            _writer.WriteLine($"Unique Types: {stats.TypeStats.Count:N0}");

            if (stats.TotalLohObjects > 0)
            {
                double lohPercentage = (stats.TotalLohMemory / (double)stats.TotalMemory) * 100;
                _writer.WriteLine($"LOH Objects: {stats.TotalLohObjects:N0} ({lohPercentage:F1}% of total memory)");
            }
        }

        private void PrintTopObjectsByCount(Dictionary<string, EnhancedTypeStats> typeStats)
        {
            _writer.WriteLine("\nTOP 20 OBJECT TYPES BY COUNT:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
            _writer.WriteSeparator();

            foreach (var stat in typeStats.Values.OrderByDescending(s => s.Count).Take(20))
            {
                _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
            }
        }

        private void PrintTopObjectsBySize(Dictionary<string, EnhancedTypeStats> typeStats)
        {
            _writer.WriteLine("\nTOP 20 OBJECT TYPES BY MEMORY SIZE:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
            _writer.WriteSeparator();

            foreach (var stat in typeStats.Values.OrderByDescending(s => s.TotalSize).Take(20))
            {
                _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
            }
        }

        private void PrintLOHUsage(MemoryStatistics stats)
        {
            if (stats.TotalLohObjects > 0)
            {
                _writer.WriteLine("\nLARGE OBJECT HEAP (LOH) USAGE:");
                _writer.WriteSeparator();
                _writer.WriteLine($"Total LOH Objects: {stats.TotalLohObjects:N0}");
                _writer.WriteLine($"Total LOH Size: {FormatHelper.FormatBytes(stats.TotalLohMemory)}");
                _writer.WriteLine($"\nTop LOH Object Types:");
                _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
                _writer.WriteSeparator();

                // Filter types with LOH objects and sort by LOH size
                var lohTypes = stats.TypeStats.Values
                    .Where(s => s.LohCount > 0)
                    .OrderByDescending(s => s.LohSize)
                    .Take(15);

                foreach (var stat in lohTypes)
                {
                    _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.LohCount,12:N0} {FormatHelper.FormatBytes(stat.LohSize),15}");
                }
            }
            else
            {
                _writer.WriteLine("\nLARGE OBJECT HEAP (LOH): No objects found");
            }
        }
    }

    internal class EnhancedTypeStats
    {
        public string TypeName { get; set; } = string.Empty;
        public int Count { get; set; }
        public ulong TotalSize { get; set; }
        public int LohCount { get; set; }
        public ulong LohSize { get; set; }
    }

    internal class MemoryStatistics
    {
        public Dictionary<string, EnhancedTypeStats> TypeStats { get; set; } = new();
        public int TotalObjects { get; set; }
        public ulong TotalMemory { get; set; }
        public int TotalLohObjects { get; set; }
        public ulong TotalLohMemory { get; set; }
    }
}
