using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryAnalyzer
    {
        private readonly OutputWriter _writer;

        public MemoryAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("MEMORY ANALYSIS:");

            var typeStats = new Dictionary<string, TypeStats>();
            var lohStats = new Dictionary<string, TypeStats>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "Unknown";
                ulong size = obj.Size;
                bool isLoh = obj.Size >= 85000;

                if (!typeStats.ContainsKey(typeName))
                {
                    typeStats[typeName] = new TypeStats { TypeName = typeName };
                }
                typeStats[typeName].Count++;
                typeStats[typeName].TotalSize += size;

                if (isLoh)
                {
                    if (!lohStats.ContainsKey(typeName))
                    {
                        lohStats[typeName] = new TypeStats { TypeName = typeName };
                    }
                    lohStats[typeName].Count++;
                    lohStats[typeName].TotalSize += size;
                }
            }

            PrintTopObjectsByCount(typeStats);
            PrintTopObjectsBySize(typeStats);
            PrintLOHUsage(lohStats);

            _writer.WriteLine($"\n{new string('=', 80)}");
        }

        private void PrintTopObjectsByCount(Dictionary<string, TypeStats> typeStats)
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

        private void PrintTopObjectsBySize(Dictionary<string, TypeStats> typeStats)
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

        private void PrintLOHUsage(Dictionary<string, TypeStats> lohStats)
        {
            if (lohStats.Any())
            {
                long totalLohSize = lohStats.Values.Sum(s => (long)s.TotalSize);
                int totalLohCount = lohStats.Values.Sum(s => s.Count);

                _writer.WriteLine("\nLARGE OBJECT HEAP (LOH) USAGE:");
                _writer.WriteSeparator();
                _writer.WriteLine($"Total LOH Objects: {totalLohCount:N0}");
                _writer.WriteLine($"Total LOH Size: {FormatHelper.FormatBytes((ulong)totalLohSize)}");
                _writer.WriteLine($"\nTop LOH Object Types:");
                _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
                _writer.WriteSeparator();

                foreach (var stat in lohStats.Values.OrderByDescending(s => s.TotalSize).Take(15))
                {
                    _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
                }
            }
            else
            {
                _writer.WriteLine("\nLARGE OBJECT HEAP (LOH): No objects found");
            }
        }
    }
}
