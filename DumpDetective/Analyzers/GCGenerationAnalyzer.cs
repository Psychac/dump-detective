using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class GCGenerationAnalyzer
    {
        private readonly OutputWriter _writer;
        private const ulong LohThresholdBytes = 85_000;
        private const int TopTypeCount = 15;

        public GCGenerationAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public IReadOnlyList<InsightFinding> Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("GC GENERATIONS BREAKDOWN:");

            // Reuse prebuilt type statistics cache to avoid an extra full heap pass.
            var cachedStats = cache.GetOrBuildTypeStatistics(heap);

            PrintSummary(cachedStats);
            PrintTopTypes(cachedStats);
            var findings = new List<InsightFinding>(capacity: 1)
            {
                CreateFinding(cachedStats)
            };

            _writer.WriteLine($"\n{StringConstants.Equals80}");
            return findings;
        }

        private static InsightFinding CreateFinding(Dictionary<string, TypeStatistics> typeStats)
        {
            ulong total = 0;
            ulong loh = 0;
            foreach (var stat in typeStats.Values)
            {
                total += stat.TotalSize;
                loh += stat.LohSize;
            }

            double lohPct = total == 0 ? 0 : loh * 100.0 / total;
            return new InsightFinding(
                Analyzer: nameof(GCGenerationAnalyzer),
                Category: "GC",
                Severity: lohPct >= 35 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "GC generation footprint snapshot",
                Evidence: $"LOH memory share is {lohPct:F1}% of managed heap.",
                Recommendation: lohPct >= 35
                    ? "Inspect large object churn and promotion patterns."
                    : "Generation split appears within expected range for this dump.",
                Tags: ["gc", "generations", "loh"],
                MetricValue: lohPct,
                MetricUnit: "%");
        }

        private void PrintSummary(Dictionary<string, TypeStatistics> typeStats)
        {
            _writer.WriteLine("\nHeap Summary:");

            // Calculate totals in a single pass
            int totalGen2Count = 0;
            int totalLohCount = 0;
            ulong totalGen2Size = 0;
            ulong totalLohSize = 0;

            foreach (var stat in typeStats.Values)
            {
                int gen2Count = stat.Count - stat.LohCount;
                ulong gen2Size = stat.TotalSize - stat.LohSize;

                totalGen2Count += gen2Count;
                totalGen2Size += gen2Size;
                totalLohCount += stat.LohCount;
                totalLohSize += stat.LohSize;
            }

            _writer.WriteLine($"  Small/Medium Objects (< {LohThresholdBytes / 1000}KB): {totalGen2Count,12:N0} objects  {FormatHelper.FormatBytes(totalGen2Size),12}");
            _writer.WriteLine($"  Large Objects (LOH >= {LohThresholdBytes / 1000}KB):   {totalLohCount,12:N0} objects  {FormatHelper.FormatBytes(totalLohSize),12}");
            _writer.WriteLine($"  Total:                          {totalGen2Count + totalLohCount,12:N0} objects  {FormatHelper.FormatBytes(totalGen2Size + totalLohSize),12}");

            if (totalLohCount > 0)
            {
                double lohPercentage = (totalLohSize / (double)(totalGen2Size + totalLohSize)) * 100;
                _writer.WriteLine($"  LOH Percentage:                  {lohPercentage,11:F1}%");
            }
        }

        private void PrintTopTypes(Dictionary<string, TypeStatistics> typeStats)
        {
            if (typeStats.Count > 0)
            {
                _writer.WriteLine($"\nTop {TopTypeCount} Object Types by Count (potential leak sources if excessive):");
                _writer.WriteLine($"{"Type",-60} {"Count",12} {"Size",12}");
                _writer.WriteSeparator();

                // Create list of types with gen2 objects (Count > LohCount)
                var gen2Types = new List<TypeStatistics>(typeStats.Count);
                foreach (var stat in typeStats.Values)
                {
                    if (stat.Count > stat.LohCount)
                    {
                        gen2Types.Add(stat);
                    }
                }

                // Manual sorting by gen2 count - no LINQ allocations
                gen2Types.Sort((a, b) =>
                {
                    int countA = a.Count - a.LohCount;
                    int countB = b.Count - b.LohCount;
                    return countB.CompareTo(countA);
                });

                int count = 0;
                foreach (var stat in gen2Types)
                {
                    if (count >= TopTypeCount) break;
                    int gen2Count = stat.Count - stat.LohCount;
                    ulong gen2Size = stat.TotalSize - stat.LohSize;
                    _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {gen2Count,12:N0} {FormatHelper.FormatBytes(gen2Size),12}");
                    count++;
                }
            }
        }
    }
}
