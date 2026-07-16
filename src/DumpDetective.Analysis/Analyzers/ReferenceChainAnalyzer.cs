using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Analyzers
{
    public class ReferenceChainAnalyzer : IAnalyzer
    {
        private readonly record struct ObjectMetadata(bool IsValid, string? TypeName, ulong Size);

        public string Name => "Reference Chain Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReferenceChainOptions options = context.AnalysisOptions.ReferenceChain;
            ExecutionPolicy policy = context.AnalysisOptions.ExecutionPolicy;

            return ValueTask.FromResult(AnalyzeTopTypes(context.Heap, context.Cache, options, policy, context.Progress).Stamp(this));
        }

        internal AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options)
        {
            return AnalyzeTopTypes(heap, cache, options, ExecutionPolicy.Default, progress: null);
        }

        private AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options, ExecutionPolicy policy, IProgress<AnalyzerProgressReport>? progress)
        {
            int topCount = options.TopCount > 0 ? options.TopCount : options.FallbackTopCount;
            int maxPathSearchObjects = policy.ReferenceChainMaxPathSearchObjects > 0
                ? policy.ReferenceChainMaxPathSearchObjects
                : options.FallbackMaxPathSearchObjects;
            bool skipArrays = options.SkipArrays;
            int largeFanoutThreshold = options.LargeFanoutThreshold > 0 ? options.LargeFanoutThreshold : 100;
            var knownLeakPatterns = options.KnownLeakTypePatterns ?? Array.Empty<string>();

            // Use cached type statistics instead of re-enumerating
            progress?.Report(new(0, "building type index"));
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToArray();

            int retainedSamples = 0;
            int analyzedSamples = 0;
            int traversalLimitedSamples = 0;
            var retainedTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sampleReferenceChains = new List<string>(capacity: 5);
            var topTypeSampleTraces = new List<ReferenceTypeSampleSnapshot>(capacity: topTypes.Length);
            progress?.Report(new(0, "loading root list"));
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            // Sort retaining roots by likelihood of early hit (Stack first) and drop weak/dependent roots
            // that can never prevent GC collection. Sorted once, reused for all top-N type samples.
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);

            var telemetry = new TelemetryCounters();
            int typeIndex = 0;

            foreach (var typeKvp in topTypes)
            {
                typeIndex++;
                progress?.Report(new(analyzedSamples, "tracing reference chains", $"{typeIndex}/{topTypes.Length} types"));
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

                        hasGcRoot = TryFindAnyRootPath(heap, prioritizedRoots, sampleAddress.Value, options, policy, telemetry, out path, out searchTruncated);
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
                .ToArray();

            return new ReferenceChainDomainResult(
                analyzedSamples,
                retainedSamples,
                retainedPct,
                topRetainedTypes,
                sampleReferenceChains,
                topTypeSampleTraces,
                traversalLimitedSamples);
        }

        internal bool AnalyzeObject(ClrHeap heap, IHeapAnalysisCache cache, ulong objectAddress)
        {
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);
            var options = new ReferenceChainOptions();
            var telemetry = new TelemetryCounters();
            return TryFindAnyRootPath(heap, prioritizedRoots, objectAddress, options, ExecutionPolicy.Default, telemetry, out _, out _);
        }

        private bool TryFindAnyRootPath(
            ClrHeap heap,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            ExecutionPolicy policy,
            TelemetryCounters telemetry,
            out string? path,
            out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            if (!TryGetValidObject(heap, objectAddress, out _))
                return false;

            if (options.SearchMode == ReferenceChainSearchMode.Fast)
                return TryFindAnyRootPath_Fast(heap, roots, objectAddress, options, policy, telemetry, out path, out searchTruncated);

            return TryFindAnyRootPath_Bidirectional(heap, roots, objectAddress, options, policy, telemetry, out path, out searchTruncated);
        }

        // ── Fast mode ─────────────────────────────────────────────────────────
        private bool TryFindAnyRootPath_Fast(
            ClrHeap heap,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            ExecutionPolicy policy,
            TelemetryCounters telemetry,
            out string? path,
            out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            int maxPathSearchObjects = policy.ReferenceChainMaxPathSearchObjects > 0 ? policy.ReferenceChainMaxPathSearchObjects : options.FallbackMaxPathSearchObjects;

            // Preallocate once and reuse across all root iterations.
            var visited = new HashSet<ulong>(capacity: 1024);
            var previous = new Dictionary<ulong, ulong>(capacity: 1024);
            var queue = new Queue<(ulong Address, int Depth)>(capacity: 256);

            var scanCounter = new ObjectScanCounter("Reference chain root scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(2));
            foreach ((string rootKind, ulong rootAddress) in roots)
            {
                scanCounter.Tick();
                if (TryBuildPath(heap, rootAddress, objectAddress, maxPathSearchObjects, visited, previous, queue,
                    options.SkipArrays, options.LargeFanoutThreshold, options.KnownLeakTypePatterns, policy.ReferenceChainMaxPathDepth, telemetry,
                    out List<ulong>? addresses, out bool pathSearchLimited))
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

        // ── Balanced / Deep mode ──────────────────────────────────────────────
        private bool TryFindAnyRootPath_Bidirectional(
            ClrHeap heap,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            ExecutionPolicy policy,
            TelemetryCounters telemetry,
            out string? path,
            out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            // Use ReferenceGraph as the reference provider — it caches edges, reducing re-fetching
            // across the finder's internal phases (candidate set, reverse index, constrained BFS).
            var provider = new ReferenceGraph(heap);
            var limits = new RootPathSearchLimits
            {
                MaxCandidateNodes = options.ResolvedMaxCandidateNodes,
                MaxCandidateDepth = options.ResolvedMaxCandidateDepth,
                MaxRootExpansionDepth = options.ResolvedMaxRootExpansionDepth,
                LargeFanoutThreshold = options.LargeFanoutThreshold,
            };

            var finder = new RootPathFinder(
                heap,
                provider,
                limits,
                telemetry.AsProxy(),
                type => IsNoisyType(type, options.SkipArrays),
                type => IsKnownLeakType(type, options.KnownLeakTypePatterns));

            bool found = finder.TryFindAnyRootPath(
                objectAddress,
                roots,
                out string? rootKind,
                out List<ulong>? addresses,
                out searchTruncated,
                out int candidateSetSize,
                out int reverseIndexEntryCount);

            telemetry.TotalCandidateSetSize += candidateSetSize;
            telemetry.ReverseIndexEntries += reverseIndexEntryCount;

            if (found)
            {
                path = FormatPath(heap, rootKind!, addresses);
                return true;
            }

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
            bool skipArrays,
            int largeFanoutThreshold,
            IReadOnlyList<string> knownLeakPatterns,
            int maxPathDepth,
            TelemetryCounters telemetry,
            out List<ulong>? path,
            out bool searchLimitReached)
        {
            path = null;
            searchLimitReached = false;

            if (startAddress == targetAddress)
            {
                path = new List<ulong> { startAddress };
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

                if (depth >= maxPathDepth)
                    continue;

                foreach (ulong refAddress in EnumerateReferenceAddresses(heap, current, skipArrays, largeFanoutThreshold, knownLeakPatterns, telemetry))
                {
                    if (refAddress == targetAddress)
                    {
                        path = ReconstructPath(previous, startAddress, targetAddress, current);
                        return true;
                    }

                    if (visited.Add(refAddress))
                    {
                        // increment telemetry if we detect a pruned node marker? (EnumerateReferenceAddresses will maintain counts via telemetry callbacks)
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

        private static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong>? addresses)
        {
            if (addresses is null || addresses.Count == 0)
                return $"{rootKind}: <no path>";

            var parts = new List<string>(addresses.Count);
            for (int i = 0; i < addresses.Count; i++)
            {
                parts.Add(FormatNodeByAddress(heap, addresses[i]));
            }
            string chain = string.Join(" -> ", parts);
            return $"{rootKind}: {chain}";
        }

        private static ObjectMetadata GetObjectMetadata(ClrHeap heap, ulong address)
        {
            if (!TryGetValidObject(heap, address, out ClrObject obj))
                return new ObjectMetadata(false, null, 0);

            return new ObjectMetadata(true, obj.Type?.Name, obj.Size);
        }

        private static IEnumerable<ulong> EnumerateReferenceAddresses(ClrHeap heap, ulong sourceAddress, bool skipArrays, int largeFanoutThreshold, IReadOnlyList<string> knownLeakPatterns, TelemetryCounters telemetry)
        {
            if (!TryGetValidObject(heap, sourceAddress, out ClrObject sourceObject))
                yield break;

            var sourceType = sourceObject.Type;
            // If the source type looks noisy, skip expanding it.
            if (IsNoisyType(sourceType, skipArrays))
            {
                telemetry.PrunedNodes++;
                yield break;
            }

            // Enumerate and enforce a large-fanout threshold; if exceeded, treat node as noisy.
            int counted = 0;
            bool forceExpand = IsKnownLeakType(sourceType, knownLeakPatterns);

            foreach (ClrObject reference in sourceObject.EnumerateReferences(carefully: true))
            {
                // count for fanout detection
                counted++;
                if (!forceExpand && counted > largeFanoutThreshold)
                {
                    // Too many children: skip expanding this node (pruning).
                    telemetry.LargeFanoutNodesSkipped++;
                    yield break;
                }

                if (!reference.IsValid)
                    continue;

                ulong referenceAddress = reference.Address;
                if (referenceAddress == 0)
                    continue;

                yield return referenceAddress;
            }
        }

        private static bool IsNoisyType(ClrType? type, bool skipArrays)
        {
            if (type is null)
                return false;

            string? name = type.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            // Skip System.String and System.Object
            if (name == "System.String" || name == "System.Object")
                return true;

            // Optionally skip arrays
            if (skipArrays && type.IsArray)
                return true;

            return false;
        }

        private static bool IsKnownLeakType(ClrType? type, IReadOnlyList<string> knownLeakPatterns)
        {
            if (type is null)
                return false;

            string? name = type.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var pattern in knownLeakPatterns)
            {
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string FormatNodeByAddress(ClrHeap heap, ulong address)
        {
            ObjectMetadata metadata = GetObjectMetadata(heap, address);
            string typeName = metadata.IsValid ? (metadata.TypeName ?? StringConstants.UnknownType) : "<invalid>";
            return $"{typeName}@0x{address:X}";
        }

        // Simple telemetry counters used during a single AnalyzeTopTypes invocation
        internal sealed class TelemetryCounters
        {
            public long PrunedNodes { get; set; }
            public long LargeFanoutNodesSkipped { get; set; }
            public long TotalCandidateSetSize { get; set; }
            public long ReverseIndexEntries { get; set; }

            // Returns a lightweight proxy that the helper classes (outside this class) can write through.
            public TelemetryProxy AsProxy() => new(this);
        }

        /// <summary>
        /// Lightweight ref-struct-like proxy so nested helper classes (declared outside
        /// <see cref="ReferenceChainAnalyzer"/>) can update telemetry without exposing the full counter object.
        /// </summary>
        internal sealed class TelemetryProxy(TelemetryCounters inner) : IPathSearchTelemetry
        {
            public void IncrementPruned() => inner.PrunedNodes++;
            public void IncrementLargeFanout() => inner.LargeFanoutNodesSkipped++;
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
            // Finalizer roots are already reported by FinalizableObjectAnalyzer — deprioritize.
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
        public void Dispose() { }

    }

}
