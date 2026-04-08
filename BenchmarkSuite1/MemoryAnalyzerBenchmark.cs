using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using DumpDetective.Analyzers;
using DumpDetective.Utilities;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VSDiagnostics;

namespace DumpDetective.Benchmarks
{
    [MemoryDiagnoser]
    [CPUUsageDiagnoser]
    public class MemoryAnalyzerBenchmark
    {
        private Dictionary<string, TypeStatistics> _typeStats = null!;
        private StreamWriter _streamWriter = null!;
        private OutputWriter _outputWriter = null!;

        [GlobalSetup]
        public void Setup()
        {
            // Simulate a large dump with 100,000 unique types
            _typeStats = new Dictionary<string, TypeStatistics>(100000);

            for (int i = 0; i < 100000; i++)
            {
                var typeName = $"TestNamespace.Type{i}";
                _typeStats[typeName] = new TypeStatistics
                {
                    TypeName = typeName,
                    Count = 10 + (i % 1000),
                    TotalSize = (ulong)(1000 + (i % 100000)),
                    LohCount = i % 10 == 0 ? 1 : 0,
                    LohSize = i % 10 == 0 ? 90000ul : 0ul
                };
            }

            var memoryStream = new MemoryStream();
            _streamWriter = new StreamWriter(memoryStream);
            _outputWriter = new OutputWriter(_streamWriter);
        }

        [Benchmark(Description = "MemoryAnalyzer - OPTIMIZED (direct TypeStatistics + manual sorting)")]
        public void MemoryAnalyzerProcessingOptimized()
        {
            // Work directly with TypeStatistics - no dictionary copy
            var statsList = new List<TypeStatistics>(_typeStats.Values);

            // Manual sorting by count
            statsList.Sort((a, b) => b.Count.CompareTo(a.Count));
            int countByCount = 0;
            foreach (var stat in statsList)
            {
                if (countByCount >= 20) break;
                // Simulate output without actually writing
                var _ = stat.TypeName + stat.Count;
                countByCount++;
            }

            // Manual sorting by size
            statsList.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            int countBySize = 0;
            foreach (var stat in statsList)
            {
                if (countBySize >= 20) break;
                var _ = stat.TypeName + stat.TotalSize;
                countBySize++;
            }

            // LOH filtering and sorting
            var lohTypes = new List<TypeStatistics>();
            foreach (var stat in _typeStats.Values)
            {
                if (stat.LohCount > 0)
                {
                    lohTypes.Add(stat);
                }
            }
            lohTypes.Sort((a, b) => b.LohSize.CompareTo(a.LohSize));
            int countLoh = 0;
            foreach (var stat in lohTypes)
            {
                if (countLoh >= 15) break;
                var _ = stat.TypeName + stat.LohSize;
                countLoh++;
            }
        }

        [Benchmark(Description = "GCGenerationAnalyzer - OPTIMIZED (direct TypeStatistics + manual sorting)")]
        public void GCGenerationAnalyzerProcessingOptimized()
        {
            // Create list of gen2 types without dictionary copy
            var gen2Types = new List<TypeStatistics>();
            foreach (var stat in _typeStats.Values)
            {
                if (stat.Count > stat.LohCount)
                {
                    gen2Types.Add(stat);
                }
            }

            // Manual sorting by gen2 count
            gen2Types.Sort((a, b) =>
            {
                int countA = a.Count - a.LohCount;
                int countB = b.Count - b.LohCount;
                return countB.CompareTo(countA);
            });

            int count = 0;
            foreach (var stat in gen2Types)
            {
                if (count >= 15) break;
                int gen2Count = stat.Count - stat.LohCount;
                var _ = stat.TypeName + gen2Count;
                count++;
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _streamWriter?.Dispose();
        }
    }
}