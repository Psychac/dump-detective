using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers
{
    internal class MemoryAnalyzer : IAnalyzer
    {
        private const ulong LohThresholdBytes = 85000;

        public string Name => "Memory Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            var typeStats = cache.GetOrBuildTypeStatistics(heap);
            return BuildDomainResult(typeStats);
        }

        private static MemoryDomainResult BuildDomainResult(Dictionary<string, CachedTypeStatistics> typeStats)
        {
            ulong totalMemory = 0;
            ulong totalLohMemory = 0;
            int totalObjects = 0;
            int lohObjects = 0;
            foreach (var stat in typeStats.Values)
            {
                totalMemory += stat.TotalSize;
                totalLohMemory += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects += stat.LohCount;
            }

            double lohPct = totalMemory == 0 ? 0 : totalLohMemory * 100.0 / totalMemory;

            var bySize = new List<CachedTypeStatistics>(typeStats.Values);
            bySize.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            var byCount = new List<CachedTypeStatistics>(typeStats.Values);
            byCount.Sort((a, b) => b.Count.CompareTo(a.Count));

            static TypeSnapshot ToSnapshot(CachedTypeStatistics s) =>
                new(s.TypeName, s.Count, s.TotalSize, s.LohSize);

            return new MemoryDomainResult(
                totalMemory,
                totalLohMemory,
                lohPct,
                totalObjects,
                lohObjects,
                LohThresholdBytes,
                typeStats.Count,
                bySize.Take(20).Select(ToSnapshot).ToList(),
                byCount.Take(20).Select(ToSnapshot).ToList());
        }

    }
}
