using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryAnalyzer
    {
        private readonly OutputWriter _writer;
        private const ulong LohThresholdBytes = 85_000;
        private const int TopTypeCount = 20;
        private const int TopLohTypeCount = 15;

        public MemoryAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public IReadOnlyList<InsightFinding> Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("MEMORY ANALYSIS:");

            // Reuse prebuilt type statistics cache to avoid an extra full heap pass.
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            PrintSummary(typeStats);
            PrintTopObjectsByCount(typeStats);
            PrintTopObjectsBySize(typeStats);
            PrintLOHUsage(typeStats);
            var findings = new List<InsightFinding>(capacity: 1)
            {
                CreateFinding(typeStats)
            };

            _writer.WriteLine($"\n{StringConstants.Equals80}");
            return findings;
        }

        private static InsightFinding CreateFinding(Dictionary<string, TypeStatistics> typeStats)
        {
            ulong totalMemory = 0;
            ulong totalLohMemory = 0;
            foreach (var stat in typeStats.Values)
            {
                totalMemory += stat.TotalSize;
                totalLohMemory += stat.LohSize;
            }

            double lohPct = totalMemory == 0 ? 0 : totalLohMemory * 100.0 / totalMemory;
            FindingSeverity severity = lohPct >= 40 ? FindingSeverity.Warning : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(MemoryAnalyzer),
                Category: "Memory",
                Severity: severity,
                Title: "Heap composition overview",
                Evidence: $"{typeStats.Count:N0} unique types, {FormatHelper.FormatBytes(totalMemory)} total memory, LOH share {lohPct:F1}%.",
                Recommendation: lohPct >= 40
                    ? "Review large-object allocation patterns and retention lifetimes."
                    : "Use top types by size/count as primary triage anchors.",
                Tags: ["heap", "composition", "loh"]);
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
                _writer.WriteLine($"LOH Threshold: {LohThresholdBytes:N0} bytes");
            }
        }

        private void PrintTopObjectsByCount(Dictionary<string, TypeStatistics> typeStats)
        {
            _writer.WriteLine($"\nTOP {TopTypeCount} OBJECT TYPES BY COUNT:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
            _writer.WriteSeparator();

            // Manual sorting - no LINQ allocations
            var statsList = new List<TypeStatistics>(typeStats.Count);
            statsList.AddRange(typeStats.Values);
            statsList.Sort((a, b) => b.Count.CompareTo(a.Count));

            int count = 0;
            foreach (var stat in statsList)
            {
                if (count >= TopTypeCount) break;
                _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
                count++;
            }
        }

        private void PrintTopObjectsBySize(Dictionary<string, TypeStatistics> typeStats)
        {
            _writer.WriteLine($"\nTOP {TopTypeCount} OBJECT TYPES BY MEMORY SIZE:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Type",-60} {"Count",12} {"Total Size",15}");
            _writer.WriteSeparator();

            // Manual sorting - no LINQ allocations
            var statsList = new List<TypeStatistics>(typeStats.Count);
            statsList.AddRange(typeStats.Values);
            statsList.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));

            int count = 0;
            foreach (var stat in statsList)
            {
                if (count >= TopTypeCount) break;
                _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),15}");
                count++;
            }
        }

        private void PrintLOHUsage(Dictionary<string, TypeStatistics> typeStats)
        {
            // Calculate LOH totals and collect LOH types in single pass
            var lohTypes = new List<TypeStatistics>(typeStats.Count);
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
                    if (count >= TopLohTypeCount) break;
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
