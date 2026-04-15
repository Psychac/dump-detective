using Microsoft.Diagnostics.Runtime;
using DumpDetective.Configuration;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ReferenceChainAnalyzer : IAnalyzer
    {
        private readonly AnalysisConfiguration _config;
        private const int DefaultTopTypeCount = 10;

        public string Name => "Reference Chain Analysis";

        public ReferenceChainAnalyzer(AnalysisConfiguration config)
        {
            _config = config;
        }

        public AnalyzerExecutionResult Execute(AnalysisContext context) => AnalyzeTopTypes(context.Heap, context.Cache);

        public AnalyzerExecutionResult AnalyzeTopTypes(ClrHeap heap, HeapAnalysisCache cache)
        {
            int topCount = _config.ReferenceChainTopCount > 0 ? _config.ReferenceChainTopCount : DefaultTopTypeCount;

            // Use cached type statistics instead of re-enumerating
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToList();

            int retainedSamples = 0;
            int analyzedSamples = 0;
            var retainedTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var typeKvp in topTypes)
            {
                string typeName = typeKvp.Key;

                // Skip system types using shared utility
                if (TypeFilterHelper.IsSystemType(typeName))
                    continue;

                // Use cached sample instance instead of full heap walk
                ulong? sampleAddress = cache.GetSampleInstanceAddress(typeName);

                if (sampleAddress.HasValue)
                {
                    analyzedSamples++;
                    if (AnalyzeObject(heap, sampleAddress.Value))
                    {
                        retainedSamples++;
                        if (retainedTypeCounts.TryGetValue(typeName, out int current))
                            retainedTypeCounts[typeName] = current + 1;
                        else
                            retainedTypeCounts[typeName] = 1;
                    }
                }
            }

            double retainedPct = analyzedSamples == 0 ? 0 : retainedSamples * 100.0 / analyzedSamples;
            var topRetainedTypes = retainedTypeCounts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(kvp => new NameCountEntry(kvp.Key, kvp.Value))
                .ToList();

            return new AnalyzerExecutionResult(
                [CreateFinding(analyzedSamples, retainedSamples)],
                new ReferenceChainDomainResult(analyzedSamples, retainedSamples, retainedPct, topRetainedTypes));
        }

        public bool AnalyzeObject(ClrHeap heap, ulong objectAddress)
        {
            ClrObject obj = heap.GetObject(objectAddress);

            if (!obj.IsValid)
                return false;

            var scanCounter = new ObjectScanCounter("Reference chain root scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(2));
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                scanCounter.Tick();
                if (CanReachTarget(heap, root.Object.Address, objectAddress))
                {
                    scanCounter.Complete();
                    return true;
                }
            }

            scanCounter.Complete();
            return false;
        }

        private static InsightFinding CreateFinding(int analyzedSamples, int retainedSamples)
        {
            if (analyzedSamples == 0)
            {
                return new InsightFinding(
                    Analyzer: nameof(ReferenceChainAnalyzer),
                    Category: "Retention",
                    Severity: FindingSeverity.Info,
                    Title: "No sample instances available for reference-chain tracing",
                    Evidence: "Reference-chain analyzer could not obtain valid sample objects for configured top types.",
                    Recommendation: "Review type statistics and dump integrity; re-run with broader type coverage if needed.",
                    Tags: ["reference-chain", "roots", "retention"],
                    MetricValue: 0,
                    MetricUnit: "% retained-samples");
            }

            double retainedPct = retainedSamples * 100.0 / analyzedSamples;
            FindingSeverity severity = retainedPct >= 70 ? FindingSeverity.Warning : FindingSeverity.Info;
            return new InsightFinding(
                Analyzer: nameof(ReferenceChainAnalyzer),
                Category: "Retention",
                Severity: severity,
                Title: "Reference-chain retention coverage",
                Evidence: $"{retainedSamples:N0}/{analyzedSamples:N0} sampled top types had at least one GC-root path ({retainedPct:F1}%).",
                Recommendation: "Focus on root paths for retained top types to identify ownership leaks.",
                Tags: ["reference-chain", "gc-roots", "retention"],
                MetricValue: retainedPct,
                MetricUnit: "% retained-samples");
        }

        private static bool CanReachTarget(ClrHeap heap, ulong startAddress, ulong targetAddress)
        {
            if (startAddress == targetAddress)
                return true;

            var visited = new HashSet<ulong>(capacity: 1024) { startAddress };
            var queue = new Queue<ulong>(capacity: 256);
            queue.Enqueue(startAddress);

            int maxSearch = 5000;
            int searched = 0;

            while (queue.Count > 0 && searched++ < maxSearch)
            {
                var current = queue.Dequeue();
                var obj = heap.GetObject(current);

                if (!obj.IsValid) continue;

                foreach (var reference in obj.EnumerateReferences(carefully: true))
                {
                    if (!reference.IsValid) continue;

                    if (reference.Address == targetAddress)
                        return true;

                    if (visited.Add(reference.Address))
                    {
                        queue.Enqueue(reference.Address);
                    }
                }
            }

            return false;
        }
    }
}
