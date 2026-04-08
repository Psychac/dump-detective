using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

using System.Diagnostics;

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

        public void Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("MEMORY ANALYSIS:");

            // Build type statistics on-demand
            var typeStats = BuildTypeStatistics(heap);

            PrintSummary(typeStats);
            PrintTopObjectsByCount(typeStats);
            PrintTopObjectsBySize(typeStats);
            PrintLOHUsage(typeStats);

            _writer.WriteLine($"\n{StringConstants.Equals80}");
        }

        private Dictionary<string, TypeStatistics> BuildTypeStatistics(ClrHeap heap)
        {
            var typeStats = new Dictionary<string, TypeStatistics>(capacity: 1024);

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? StringConstants.UnknownType;
                ulong size = obj.Size;
                bool isLoh = size >= 85000;

                if (!typeStats.TryGetValue(typeName, out var stats))
                {
                    stats = new TypeStatistics { TypeName = typeName };
                    typeStats[typeName] = stats;
                }

                stats.Count++;
                stats.TotalSize += size;

                if (isLoh)
                {
                    stats.LohCount++;
                    stats.LohSize += size;
                }
            }

            return typeStats;
        }

        private void PrintSummary(Dictionary<string, TypeStatistics> typeStats)
        {
            _writer.WriteLine("\nOVERALL SUMMARY:");
            _writer.WriteSeparator();

            // Calculate totals in a single pass
            int totalObjects = 0;
            ulong totalMemory = 0;
            int totalLohObjects = 0;
            ulong totalLohMemory = 0;

            foreach (var stat in typeStats.Values)
            {
                totalObjects += stat.Count;
                totalMemory += stat.TotalSize;
                totalLohObjects += stat.LohCount;
                totalLohMemory += stat.LohSize;
            }

            _writer.WriteLine($"Total Objects: {totalObjects:N0}");
            _writer.WriteLine($"Total Memory: {FormatHelper.FormatBytes(totalMemory)}");
            _writer.WriteLine($"Unique Types: {typeStats.Count:N0}");

            if (totalLohObjects > 0)
            {
                double lohPercentage = (totalLohMemory / (double)totalMemory) * 100;
                _writer.WriteLine($"LOH Objects: {totalLohObjects:N0} ({lohPercentage:F1}% of total memory)");
            }
        }

        private void PrintTopObjectsByCount(Dictionary<string, TypeStatistics> typeStats)
        {
            _writer.WriteLine("\nTOP 20 OBJECT TYPES BY COUNT:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
            _writer.WriteSeparator();

            // Manual sorting - no LINQ allocations
            var statsList = new List<TypeStatistics>(typeStats.Values);
            statsList.Sort((a, b) => b.Count.CompareTo(a.Count));

            int count = 0;
            foreach (var stat in statsList)
            {
                if (count >= 20) break;
                _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
                count++;
            }
        }

        private void PrintTopObjectsBySize(Dictionary<string, TypeStatistics> typeStats)
        {
            _writer.WriteLine("\nTOP 20 OBJECT TYPES BY MEMORY SIZE:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
            _writer.WriteSeparator();

            // Manual sorting - no LINQ allocations
            var statsList = new List<TypeStatistics>(typeStats.Values);
            statsList.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));

            int count = 0;
            foreach (var stat in statsList)
            {
                if (count >= 20) break;
                _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
                count++;
            }
        }

        private void PrintLOHUsage(Dictionary<string, TypeStatistics> typeStats)
        {
            // Calculate LOH totals and collect LOH types in single pass
            var lohTypes = new List<TypeStatistics>();
            int totalLohObjects = 0;
            ulong totalLohMemory = 0;

            foreach (var stat in typeStats.Values)
            {
                if (stat.LohCount > 0)
                {
                    totalLohObjects += stat.LohCount;
                    totalLohMemory += stat.LohSize;
                    lohTypes.Add(stat);
                }
            }

            if (totalLohObjects > 0)
            {
                _writer.WriteLine("\nLARGE OBJECT HEAP (LOH) USAGE:");
                _writer.WriteSeparator();
                _writer.WriteLine($"Total LOH Objects: {totalLohObjects:N0}");
                _writer.WriteLine($"Total LOH Size: {FormatHelper.FormatBytes(totalLohMemory)}");
                _writer.WriteLine($"\nTop LOH Object Types:");
                _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
                _writer.WriteSeparator();

                // Manual sorting - no LINQ allocations
                lohTypes.Sort((a, b) => b.LohSize.CompareTo(a.LohSize));

                int count = 0;
                foreach (var stat in lohTypes)
                {
                    if (count >= 15) break;
                    _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.LohCount,12:N0} {FormatHelper.FormatBytes(stat.LohSize),15}");
                    count++;
                }
            }
            else
            {
                _writer.WriteLine("\nLARGE OBJECT HEAP (LOH): No objects found");
            }
        }
    }
}
