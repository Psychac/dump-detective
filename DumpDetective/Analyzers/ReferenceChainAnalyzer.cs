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
        private const int MaxPathSearchObjects = 5000;

        private readonly record struct RootCandidate(string RootKind, ulong Address);

        public string Name => "Reference Chain Analysis";

        public ReferenceChainAnalyzer(AnalysisConfiguration config)
        {
            _config = config;
        }

        public AnalyzerExecutionResult Execute(AnalysisContext context) => AnalyzeTopTypes(context.Heap, context.Cache);

        public AnalyzerExecutionResult AnalyzeTopTypes(ClrHeap heap, HeapAnalysisCache cache)
        {
            int topCount = _config.ReferenceChainTopCount > 0 ? _config.ReferenceChainTopCount : DefaultTopTypeCount;
            int maxPathSearchObjects = _config.ReferenceChainMaxPathSearchObjects > 0
                ? _config.ReferenceChainMaxPathSearchObjects
                : MaxPathSearchObjects;

            // Use cached type statistics instead of re-enumerating
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToList();

            int retainedSamples = 0;
            int analyzedSamples = 0;
            int traversalLimitedSamples = 0;
            var retainedTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sampleReferenceChains = new List<string>(capacity: 5);
            var topTypeSampleTraces = new List<ReferenceTypeSampleSnapshot>(capacity: topTypes.Count);
            List<RootCandidate> roots = GetValidRoots(heap);

            foreach (var typeKvp in topTypes)
            {
                string typeName = typeKvp.Key;
                var stats = typeKvp.Value;

                // Use cached sample instance instead of full heap walk
                ulong? sampleAddress = cache.GetSampleInstanceAddress(typeName);
                string? sampleType = null;
                ulong sampleSize = 0;
                bool hasGcRoot = false;
                string? path = null;
                bool searchTruncated = false;

                if (sampleAddress.HasValue)
                {
                    ClrObject sampleObj = heap.GetObject(sampleAddress.Value);
                    if (sampleObj.IsValid)
                    {
                        analyzedSamples++;
                        sampleType = sampleObj.Type?.Name ?? StringConstants.UnknownType;
                        sampleSize = sampleObj.Size;

                        hasGcRoot = TryFindAnyRootPath(heap, roots, sampleAddress.Value, maxPathSearchObjects, out path, out searchTruncated);
                        if (hasGcRoot)
                        {
                            retainedSamples++;
                            if (retainedTypeCounts.TryGetValue(typeName, out int current))
                                retainedTypeCounts[typeName] = current + 1;
                            else
                                retainedTypeCounts[typeName] = 1;

                            if (!string.IsNullOrWhiteSpace(path) && sampleReferenceChains.Count < 5)
                                sampleReferenceChains.Add($"{typeName}: {path}");
                        }
                        else if (searchTruncated)
                        {
                            traversalLimitedSamples++;
                        }
                    }
                }

                topTypeSampleTraces.Add(new ReferenceTypeSampleSnapshot(
                    typeName,
                    stats.Count,
                    stats.TotalSize,
                    sampleAddress,
                    sampleType,
                    sampleSize,
                    hasGcRoot,
                    path,
                    searchTruncated));
            }

            double retainedPct = analyzedSamples == 0 ? 0 : retainedSamples * 100.0 / analyzedSamples;
            var topRetainedTypes = retainedTypeCounts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(kvp => new NameCountEntry(kvp.Key, kvp.Value))
                .ToList();

            var findings = new List<InsightFinding>(capacity: 2)
            {
                CreateFinding(analyzedSamples, retainedSamples)
            };

            if (traversalLimitedSamples > 0)
            {
                findings.Add(CreateTraversalLimitFinding(analyzedSamples, traversalLimitedSamples));
            }

            return new AnalyzerExecutionResult(
                findings,
                new ReferenceChainDomainResult(analyzedSamples, retainedSamples, retainedPct, topRetainedTypes, sampleReferenceChains, topTypeSampleTraces));
        }

        public bool AnalyzeObject(ClrHeap heap, ulong objectAddress)
        {
            List<RootCandidate> roots = GetValidRoots(heap);
            int maxPathSearchObjects = _config.ReferenceChainMaxPathSearchObjects > 0
                ? _config.ReferenceChainMaxPathSearchObjects
                : MaxPathSearchObjects;
            return TryFindAnyRootPath(heap, roots, objectAddress, maxPathSearchObjects, out _, out _);
        }

        private static List<RootCandidate> GetValidRoots(ClrHeap heap)
        {
            var roots = new List<RootCandidate>(capacity: 1024);
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                ulong rootAddress = root.Object.Address;
                if (rootAddress == 0)
                    continue;

                roots.Add(new RootCandidate(root.RootKind.ToString(), rootAddress));
            }

            return roots;
        }

        private bool TryFindAnyRootPath(ClrHeap heap, IReadOnlyList<RootCandidate> roots, ulong objectAddress, int maxPathSearchObjects, out string? path, out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            ClrObject obj = heap.GetObject(objectAddress);
            if (!obj.IsValid)
                return false;

            var scanCounter = new ObjectScanCounter("Reference chain root scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(2));
            foreach (RootCandidate root in roots)
            {
                scanCounter.Tick();
                if (TryBuildPath(heap, root.Address, objectAddress, maxPathSearchObjects, out List<ulong>? addresses, out bool pathSearchLimited))
                {
                    scanCounter.Complete();
                    path = FormatPath(heap, root.RootKind, addresses);
                    return true;
                }

                if (pathSearchLimited)
                    searchTruncated = true;
            }

            scanCounter.Complete();
            return false;
        }

        private static InsightFinding CreateTraversalLimitFinding(int analyzedSamples, int traversalLimitedSamples)
        {
            double limitedPct = analyzedSamples == 0 ? 0 : traversalLimitedSamples * 100.0 / analyzedSamples;
            return new InsightFinding(
                Analyzer: nameof(ReferenceChainAnalyzer),
                Category: "Retention",
                Severity: limitedPct >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Reference-chain traversal limit reached",
                Evidence: $"{traversalLimitedSamples:N0}/{analyzedSamples:N0} sampled type(s) hit traversal limits before a conclusive root-path result ({limitedPct:F1}%).",
                Recommendation: "Increase sampling depth/path budget for inconclusive types and validate with targeted object tracing.",
                Tags: ["reference-chain", "traversal-limit", "retention"],
                MetricValue: limitedPct,
                MetricUnit: "% traversal-limited-samples");
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

        private static bool TryBuildPath(ClrHeap heap, ulong startAddress, ulong targetAddress, int maxPathSearchObjects, out List<ulong>? path, out bool searchLimitReached)
        {
            path = null;
            searchLimitReached = false;

            if (startAddress == targetAddress)
            {
                path = [startAddress];
                return true;
            }

            if (startAddress == 0 || targetAddress == 0)
                return false;

            var visited = new HashSet<ulong>(capacity: 1024) { startAddress };
            var previous = new Dictionary<ulong, ulong>(capacity: 1024);
            var queue = new Queue<ulong>(capacity: 256);
            queue.Enqueue(startAddress);

            int searched = 0;

            while (queue.Count > 0 && searched++ < maxPathSearchObjects)
            {
                ulong current = queue.Dequeue();
                ClrObject currentObj = heap.GetObject(current);
                if (!currentObj.IsValid)
                    continue;

                foreach (ClrObject reference in currentObj.EnumerateReferences(carefully: true))
                {
                    if (!reference.IsValid)
                        continue;

                    ulong refAddress = reference.Address;
                    if (refAddress == targetAddress)
                    {
                        path = ReconstructPath(previous, startAddress, targetAddress, current);
                        return true;
                    }

                    if (visited.Add(refAddress))
                    {
                        previous[refAddress] = current;
                        queue.Enqueue(refAddress);
                    }
                }
            }

            searchLimitReached = queue.Count > 0 && searched >= maxPathSearchObjects;

            return false;
        }

        private static List<ulong> ReconstructPath(Dictionary<ulong, ulong> previous, ulong startAddress, ulong targetAddress, ulong? targetParent = null)
        {
            var reversed = new List<ulong>(capacity: 16) { targetAddress };

            ulong cursor = targetAddress;
            if (targetParent.HasValue)
            {
                reversed.Add(targetParent.Value);
                cursor = targetParent.Value;
            }

            while (cursor != startAddress && previous.TryGetValue(cursor, out ulong parent))
            {
                reversed.Add(parent);
                cursor = parent;
            }

            reversed.Reverse();
            return reversed;
        }

        private static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong> addresses)
        {
            static string FormatNode(ClrHeap heap, ulong address)
            {
                ClrObject obj = heap.GetObject(address);
                string typeName = obj.IsValid ? (obj.Type?.Name ?? StringConstants.UnknownType) : "<invalid>";
                return $"{typeName}@0x{address:X}";
            }

            string chain = string.Join(" -> ", addresses.Select(a => FormatNode(heap, a)));
            return $"{rootKind}: {chain}";
        }
    }
}
