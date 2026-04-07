using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class GCGenerationAnalyzer
    {
        private readonly OutputWriter _writer;
        private const int LOH_THRESHOLD = 85000;

        public GCGenerationAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("GC GENERATIONS BREAKDOWN:");

            // Use cached type statistics instead of re-enumerating
            var cachedStats = cache.GetOrBuildTypeStatistics(heap);
            var stats = ConvertToGenerationStatistics(cachedStats);

            PrintSummary(stats);
            PrintTopTypes(stats.Gen2Stats);

            _writer.WriteLine($"\n{StringConstants.Equals80}");
        }

        private GenerationStatistics ConvertToGenerationStatistics(Dictionary<string, TypeStatistics> cachedStats)
        {
            var gen2Stats = new Dictionary<string, GenStats>(capacity: 512);
            var lohStats = new Dictionary<string, GenStats>(capacity: 128);

            int gen2Count = 0;
            int lohCount = 0;
            ulong gen2Size = 0;
            ulong lohSize = 0;

            foreach (var kvp in cachedStats)
            {
                var stat = kvp.Value;

                // Add to gen2 stats (all objects < LOH threshold)
                if (stat.Count > stat.LohCount)
                {
                    gen2Stats[kvp.Key] = new GenStats
                    {
                        TypeName = stat.TypeName,
                        Count = stat.Count - stat.LohCount,
                        TotalSize = stat.TotalSize - stat.LohSize
                    };
                    gen2Count += stat.Count - stat.LohCount;
                    gen2Size += stat.TotalSize - stat.LohSize;
                }

                // Add to LOH stats if applicable
                if (stat.LohCount > 0)
                {
                    lohStats[kvp.Key] = new GenStats
                    {
                        TypeName = stat.TypeName,
                        Count = stat.LohCount,
                        TotalSize = stat.LohSize
                    };
                    lohCount += stat.LohCount;
                    lohSize += stat.LohSize;
                }
            }

            return new GenerationStatistics
            {
                Gen2Stats = gen2Stats,
                LohStats = lohStats,
                TotalGen2Count = gen2Count,
                TotalLohCount = lohCount,
                TotalGen2Size = gen2Size,
                TotalLohSize = lohSize
            };
        }

        private void PrintSummary(GenerationStatistics stats)
        {
            _writer.WriteLine("\nHeap Summary:");
            _writer.WriteLine($"  Small/Medium Objects (< 85KB): {stats.TotalGen2Count,12:N0} objects  {FormatHelper.FormatBytes(stats.TotalGen2Size),12}");
            _writer.WriteLine($"  Large Objects (LOH >= 85KB):   {stats.TotalLohCount,12:N0} objects  {FormatHelper.FormatBytes(stats.TotalLohSize),12}");
            _writer.WriteLine($"  Total:                          {stats.TotalGen2Count + stats.TotalLohCount,12:N0} objects  {FormatHelper.FormatBytes(stats.TotalGen2Size + stats.TotalLohSize),12}");

            if (stats.TotalLohCount > 0)
            {
                double lohPercentage = (stats.TotalLohSize / (double)(stats.TotalGen2Size + stats.TotalLohSize)) * 100;
                _writer.WriteLine($"  LOH Percentage:                  {lohPercentage,11:F1}%");
            }
        }

        private void PrintTopTypes(Dictionary<string, GenStats> gen2Stats)
        {
            if (gen2Stats.Count > 0)
            {
                _writer.WriteLine("\nTop 15 Object Types by Count (potential leak sources if excessive):");
                _writer.WriteLine($"{"Type",-60} {"Count",12} {"Size",12}");
                _writer.WriteSeparator();

                foreach (var stat in gen2Stats.Values.OrderByDescending(s => s.Count).Take(15))
                {
                    _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),12}");
                }
            }
        }
    }

    internal class GenerationStatistics
    {
        public Dictionary<string, GenStats> Gen2Stats { get; set; } = new();
        public Dictionary<string, GenStats> LohStats { get; set; } = new();
        public int TotalGen2Count { get; set; }
        public int TotalLohCount { get; set; }
        public ulong TotalGen2Size { get; set; }
        public ulong TotalLohSize { get; set; }
    }
}
