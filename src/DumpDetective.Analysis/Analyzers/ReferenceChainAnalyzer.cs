using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers
{
    public class ReferenceChainAnalyzer : IAnalyzer
    {
        private const int DefaultTopTypeCount = 10;
        private const int MaxPathSearchObjects = 5000;
        private const int DefaultMaxPathDepth = 25;

        private readonly record struct ObjectMetadata(bool IsValid, string? TypeName, ulong Size);

        public string Name => "Reference Chain Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReferenceChainOptions options = context.Options.TryGetValue(nameof(ReferenceChainOptions), out object? configured)
                && configured is ReferenceChainOptions typed
                ? typed
                : new ReferenceChainOptions();

            AnalyzerExecutionResult executionResult = AnalyzeTopTypes(context.Heap, context.Cache, options);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options)
        {
            int topCount = options.TopCount > 0 ? options.TopCount : DefaultTopTypeCount;
            int maxPathSearchObjects = options.MaxPathSearchObjects > 0
                ? options.MaxPathSearchObjects
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
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            // Sort retaining roots by likelihood of early hit (Stack first) and drop weak/dependent roots
            // that can never prevent GC collection. Sorted once, reused for all top-N type samples.
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);

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
                    ObjectMetadata sampleMetadata = GetObjectMetadata(heap, sampleAddress.Value);
                    if (sampleMetadata.IsValid)
                    {
                        analyzedSamples++;
                        sampleType = sampleMetadata.TypeName ?? StringConstants.UnknownType;
                        sampleSize = sampleMetadata.Size;

                        hasGcRoot = TryFindAnyRootPath(heap, prioritizedRoots, sampleAddress.Value, maxPathSearchObjects, out path, out searchTruncated);
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

        public bool AnalyzeObject(ClrHeap heap, IHeapAnalysisCache cache, ulong objectAddress)
        {
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);
            return TryFindAnyRootPath(heap, prioritizedRoots, objectAddress, MaxPathSearchObjects, out _, out _);
        }

        private bool TryFindAnyRootPath(ClrHeap heap, IReadOnlyList<(string RootKind, ulong Address)> roots, ulong objectAddress, int maxPathSearchObjects, out string? path, out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            if (!TryGetValidObject(heap, objectAddress, out _))
                return false;

            // Preallocate once and reuse across all root iterations to avoid repeated allocation
            // of large HashSet/Dictionary/Queue per root (N_roots × maxPathSearchObjects entries each).
            var visited = new HashSet<ulong>(capacity: 1024);
            var previous = new Dictionary<ulong, ulong>(capacity: 1024);
            var queue = new Queue<(ulong Address, int Depth)>(capacity: 256);

            var scanCounter = new ObjectScanCounter("Reference chain root scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(2));
            foreach ((string rootKind, ulong rootAddress) in roots)
            {
                scanCounter.Tick();
                if (TryBuildPath(heap, rootAddress, objectAddress, maxPathSearchObjects, visited, previous, queue, out List<ulong>? addresses, out bool pathSearchLimited))
                {
                    scanCounter.Complete();
                    path = FormatPath(heap, rootKind, addresses);
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

        private static bool TryBuildPath(
            ClrHeap heap,
            ulong startAddress,
            ulong targetAddress,
            int maxPathSearchObjects,
            HashSet<ulong> visited,
            Dictionary<ulong, ulong> previous,
            Queue<(ulong Address, int Depth)> queue,
            out List<ulong>? path,
            out bool searchLimitReached)
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

            visited.Clear();
            visited.Add(startAddress);
            previous.Clear();
            queue.Clear();
            queue.Enqueue((startAddress, 0));

            int searched = 0;

            while (queue.Count > 0 && searched++ < maxPathSearchObjects)
            {
                (ulong current, int depth) = queue.Dequeue();

                if (depth >= DefaultMaxPathDepth)
                    continue;

                foreach (ulong refAddress in EnumerateReferenceAddresses(heap, current))
                {
                    if (refAddress == targetAddress)
                    {
                        path = ReconstructPath(previous, startAddress, targetAddress, current);
                        return true;
                    }

                    if (visited.Add(refAddress))
                    {
                        previous[refAddress] = current;
                        queue.Enqueue((refAddress, depth + 1));
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
            string chain = string.Join(" -> ", addresses.Select(address => FormatNodeByAddress(heap, address)));
            return $"{rootKind}: {chain}";
        }

        private static ObjectMetadata GetObjectMetadata(ClrHeap heap, ulong address)
        {
            if (!TryGetValidObject(heap, address, out ClrObject obj))
                return new ObjectMetadata(false, null, 0);

            return new ObjectMetadata(true, obj.Type?.Name, obj.Size);
        }

        private static IEnumerable<ulong> EnumerateReferenceAddresses(ClrHeap heap, ulong sourceAddress)
        {
            if (!TryGetValidObject(heap, sourceAddress, out ClrObject sourceObject))
                yield break;

            // Use ClrMD's GC-descriptor-based reference walk — faster than field iteration for targeted BFS
            // since it jumps directly to reference slots without checking all fields.
            foreach (ClrObject reference in sourceObject.EnumerateReferences(carefully: true))
            {
                if (!reference.IsValid)
                    continue;

                ulong referenceAddress = reference.Address;
                if (referenceAddress == 0)
                    continue;

                yield return referenceAddress;
            }
        }

        private static string FormatNodeByAddress(ClrHeap heap, ulong address)
        {
            ObjectMetadata metadata = GetObjectMetadata(heap, address);
            string typeName = metadata.IsValid ? (metadata.TypeName ?? StringConstants.UnknownType) : "<invalid>";
            return $"{typeName}@0x{address:X}";
        }

        private static List<(string RootKind, ulong Address)> SortAndFilterRoots(
            IReadOnlyList<(string RootKind, ulong Address)> roots)
        {
            var result = new List<(string RootKind, ulong Address)>(roots.Count);
            foreach ((string rootKind, ulong address) in roots)
            {
                // Weak and dependent roots never prevent collection — skip them entirely.
                if (rootKind.Contains("Weak", StringComparison.OrdinalIgnoreCase) ||
                    rootKind.Contains("Dependent", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add((rootKind, address));
            }

            result.Sort(static (a, b) => GetRootSearchPriority(a.RootKind).CompareTo(GetRootSearchPriority(b.RootKind)));
            return result;
        }

        private static int GetRootSearchPriority(string rootKind) => rootKind switch
        {
            // Stack locals are the most direct retainers and resolve fastest.
            "Stack" => 0,
            // Thread-static and static variables are the next most likely long-lived retainers.
            "ThreadStaticVar" => 1,
            "StaticVar" => 2,
            // Strong and pinned GC handles are explicit long-term roots.
            "Strong" => 3,
            "Pinned" => 4,
            "AsyncPinnedHandle" => 4,
            "RefCountedHandle" => 5,
            // Finalizer roots are already reported by MemoryLeakAnalyzer — deprioritize.
            "Finalizer" => 10,
            // Unknown kinds go last.
            _ => 6
        };

        private static bool TryGetValidObject(ClrHeap heap, ulong address, out ClrObject obj)
        {
            obj = default;

            if (address == 0)
                return false;

            obj = heap.GetObject(address);
            return obj.IsValid;
        }
    }
}


