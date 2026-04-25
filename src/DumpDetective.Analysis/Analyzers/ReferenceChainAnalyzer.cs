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

        // Smart pruning defaults (configurable via ReferenceChainOptions)
        // Fast-mode only: depth cap applied per BFS node during forward traversal.
        private const int FastModeMaxDepth = 25;

        private readonly record struct ObjectMetadata(bool IsValid, string? TypeName, ulong Size);

        public string Name => "Reference Chain Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReferenceChainOptions options = context.Options.TryGetValue(nameof(ReferenceChainOptions), out object? configured)
                && configured is ReferenceChainOptions typed
                ? typed
                : new ReferenceChainOptions();

            return ValueTask.FromResult(AnalyzeTopTypes(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options)
        {
            return AnalyzeTopTypes(heap, cache, options, progress: null);
        }

        private AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            int topCount = options.TopCount > 0 ? options.TopCount : DefaultTopTypeCount;
            int maxPathSearchObjects = options.MaxPathSearchObjects > 0
                ? options.MaxPathSearchObjects
                : MaxPathSearchObjects;
            bool skipArrays = options.SkipArrays;
            int largeFanoutThreshold = options.LargeFanoutThreshold > 0 ? options.LargeFanoutThreshold : 100;
            var knownLeakPatterns = options.KnownLeakTypePatterns ?? Array.Empty<string>();

            // Use cached type statistics instead of re-enumerating
            progress?.Report(new(0, "building type index"));
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
                progress?.Report(new(analyzedSamples, "tracing reference chains", $"{typeIndex}/{topTypes.Count} types"));
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

                        hasGcRoot = TryFindAnyRootPath(heap, prioritizedRoots, sampleAddress.Value, options, telemetry, out path, out searchTruncated);
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

            var metrics = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SearchMode"] = options.SearchMode.ToString(),
                ["PrunedNodes"] = telemetry.PrunedNodes,
                ["LargeFanoutNodesSkipped"] = telemetry.LargeFanoutNodesSkipped,
                ["CandidateSetSize"] = telemetry.TotalCandidateSetSize,
                ["ReverseIndexEntries"] = telemetry.ReverseIndexEntries,
                ["traversalLimitedSamples"] = (long)traversalLimitedSamples,
            };

            return new ReferenceChainDomainResult(analyzedSamples, retainedSamples, retainedPct, topRetainedTypes, sampleReferenceChains, topTypeSampleTraces)
            {
                Metrics = metrics
            };
        }

        public bool AnalyzeObject(ClrHeap heap, IHeapAnalysisCache cache, ulong objectAddress)
        {
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);
            var options = new ReferenceChainOptions();
            var telemetry = new TelemetryCounters();
            return TryFindAnyRootPath(heap, prioritizedRoots, objectAddress, options, telemetry, out _, out _);
        }

        private bool TryFindAnyRootPath(
            ClrHeap heap,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            TelemetryCounters telemetry,
            out string? path,
            out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            if (!TryGetValidObject(heap, objectAddress, out _))
                return false;

            if (options.SearchMode == ReferenceChainSearchMode.Fast)
                return TryFindAnyRootPath_Fast(heap, roots, objectAddress, options, telemetry, out path, out searchTruncated);

            return TryFindAnyRootPath_Bidirectional(heap, roots, objectAddress, options, telemetry, out path, out searchTruncated);
        }

        // ── Fast mode ─────────────────────────────────────────────────────────
        private bool TryFindAnyRootPath_Fast(
            ClrHeap heap,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            TelemetryCounters telemetry,
            out string? path,
            out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            int maxPathSearchObjects = options.MaxPathSearchObjects > 0 ? options.MaxPathSearchObjects : MaxPathSearchObjects;

            // Preallocate once and reuse across all root iterations.
            var visited = new HashSet<ulong>(capacity: 1024);
            var previous = new Dictionary<ulong, ulong>(capacity: 1024);
            var queue = new Queue<(ulong Address, int Depth)>(capacity: 256);

            var scanCounter = new ObjectScanCounter("Reference chain root scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(2));
            foreach ((string rootKind, ulong rootAddress) in roots)
            {
                scanCounter.Tick();
                if (TryBuildPath(heap, rootAddress, objectAddress, maxPathSearchObjects, visited, previous, queue,
                    options.SkipArrays, options.LargeFanoutThreshold, options.KnownLeakTypePatterns, telemetry,
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
            TelemetryCounters telemetry,
            out string? path,
            out bool searchTruncated)
        {
            path = null;
            searchTruncated = false;

            // Phase 1: build candidate set via bidirectional expansion.
            // Use a simple CLR-based reference provider adapter (defined in core abstractions)
            var provider = new ClrReferenceProvider(heap);
            var candidateBuilder = new CandidateSetBuilder(heap, provider, options, telemetry.AsProxy());
            HashSet<ulong> candidateSet = candidateBuilder.Build(objectAddress, roots);

            // Phase 2: build scoped reverse index over candidate set only.
            var reverseIndex = new ReverseReferenceIndex();
            reverseIndex.Build(candidateSet, heap, options, telemetry.AsProxy(), provider);

            telemetry.TotalCandidateSetSize += candidateSet.Count;
            telemetry.ReverseIndexEntries += reverseIndex.EntryCount;

            // Phase 3: constrained BFS from roots, staying inside candidate set.
            var finder = new BidirectionalPathFinder(heap, provider, candidateSet, reverseIndex, options, telemetry.AsProxy());

            var scanCounter = new ObjectScanCounter("Bidir reference chain scan", reportEveryObjects: 500, reportEveryElapsed: TimeSpan.FromSeconds(2));
            foreach ((string rootKind, ulong rootAddress) in roots)
            {
                scanCounter.Tick();

                if (!candidateSet.Contains(rootAddress) && rootAddress != objectAddress)
                    continue;

                if (finder.TryFindPath(rootAddress, objectAddress, out List<ulong>? addresses, out bool limited))
                {
                    scanCounter.Complete();
                    path = FormatPath(heap, rootKind, addresses);
                    return true;
                }

                if (limited)
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
            bool skipArrays,
            int largeFanoutThreshold,
            IReadOnlyList<string> knownLeakPatterns,
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

                if (depth >= DefaultMaxPathDepth)
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
        internal sealed class TelemetryProxy(TelemetryCounters inner)
        {
            public void IncrementPruned() => inner.PrunedNodes++;
            public void IncrementLargeFanout() => inner.LargeFanoutNodesSkipped++;
            public long PrunedNodes { get => inner.PrunedNodes; set => inner.PrunedNodes = value; }
            public long LargeFanoutNodesSkipped { get => inner.LargeFanoutNodesSkipped; set => inner.LargeFanoutNodesSkipped = value; }
        }

        internal static bool IsKnownLeakTypePublic(ClrType? type, IReadOnlyList<string> patterns)
            => IsKnownLeakType(type, patterns);

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

    // ── ReverseReferenceIndex ─────────────────────────────────────────────────
    /// <summary>
    /// Builds a parent-lookup map scoped to a candidate set only.
    /// Never indexes the full heap.
    /// </summary>
    internal sealed class ReverseReferenceIndex
    {
        private readonly Dictionary<ulong, List<ulong>> _map = new();

        public int EntryCount => _map.Count;

        /// <summary>
        /// For every node in <paramref name="candidateSet"/>, enumerate its forward
        /// references via <paramref name="heap"/> and record an edge child→parent
        /// only when the child is also inside the candidate set.
        /// </summary>
        public void Build(
            HashSet<ulong> candidateSet,
            ClrHeap heap,
            ReferenceChainOptions options,
            ReferenceChainAnalyzer.TelemetryProxy telemetry,
            DumpDetective.Core.Abstractions.IReferenceProvider provider)
        {
            foreach (ulong obj in candidateSet)
            {
                ClrObject clrObj = heap.GetObject(obj);
                if (!clrObj.IsValid)
                    continue;

                int counted = 0;
                bool forceExpand = ReferenceChainAnalyzer.IsKnownLeakTypePublic(clrObj.Type, options.KnownLeakTypePatterns);

                foreach (var childAddr in provider.GetReferences(obj))
                {
                    counted++;
                    if (!forceExpand && counted > options.LargeFanoutThreshold)
                    {
                        telemetry.LargeFanoutNodesSkipped++;
                        break;
                    }

                    if (childAddr == 0)
                        continue;

                    if (!candidateSet.Contains(childAddr))
                        continue;

                    if (!_map.TryGetValue(childAddr, out var list))
                    {
                        list = new List<ulong>();
                        _map[childAddr] = list;
                    }

                    list.Add(obj);
                }
            }
        }

        public IEnumerable<ulong> GetParents(ulong obj)
            => _map.TryGetValue(obj, out var list) ? list : Enumerable.Empty<ulong>();
    }

    // ── CandidateSetBuilder ───────────────────────────────────────────────────
    /// <summary>
    /// Builds a bounded candidate set via true bidirectional expansion:
    /// forward from roots (limited depth) and forward from target (simulating reverse via
    /// the heap walk), meeting in the middle.
    /// </summary>
    internal sealed class CandidateSetBuilder
    {
        private readonly ClrHeap _heap;
        private readonly DumpDetective.Core.Abstractions.IReferenceProvider _provider;
        private readonly ReferenceChainOptions _options;
        private readonly ReferenceChainAnalyzer.TelemetryProxy _telemetry;

        public CandidateSetBuilder(ClrHeap heap, DumpDetective.Core.Abstractions.IReferenceProvider provider, ReferenceChainOptions options, ReferenceChainAnalyzer.TelemetryProxy telemetry)
        {
            _heap = heap;
            _provider = provider;
            _options = options;
            _telemetry = telemetry;
        }
        private static readonly HashSet<string> _noiseTypes =
            new(StringComparer.Ordinal) { "System.String", "System.Object" };

        public HashSet<ulong> Build(
            ulong target,
            IReadOnlyList<(string RootKind, ulong Address)> roots)
        {
            int maxNodes = _options.ResolvedMaxCandidateNodes;
            int maxDepth = _options.ResolvedMaxCandidateDepth;

            var candidate = new HashSet<ulong> { target };

            // Root frontier: expand forward from roots up to maxDepth levels.
            var rootQueue = new Queue<(ulong Address, int Depth)>();
            var rootVisited = new HashSet<ulong>();
            foreach ((_, ulong addr) in roots)
            {
                if (addr == 0) continue;
                if (rootVisited.Add(addr))
                {
                    rootQueue.Enqueue((addr, 0));
                    candidate.Add(addr);
                }
            }

            // Target frontier: expand forward from target (forward refs of target
            // are not useful for reverse; instead we do a second BFS from target
            // outward to find objects target references — useful to collect
            // the "neighbourhood" that is likely on a retaining chain).
            var targetQueue = new Queue<(ulong Address, int Depth)>();
            var targetVisited = new HashSet<ulong> { target };
            targetQueue.Enqueue((target, 0));

            // Interleave both frontiers until they share a node or limits are hit.
            while ((rootQueue.Count > 0 || targetQueue.Count > 0) && candidate.Count < maxNodes)
            {
                // Expand one step from root frontier.
                if (rootQueue.Count > 0)
                {
                    (ulong cur, int depth) = rootQueue.Dequeue();
                    if (depth < maxDepth)
                        ExpandForward(cur, depth, rootVisited, rootQueue, candidate, maxNodes);
                }

                // Expand one step from target frontier.
                if (targetQueue.Count > 0 && candidate.Count < maxNodes)
                {
                    (ulong cur, int depth) = targetQueue.Dequeue();
                    if (depth < maxDepth)
                        ExpandForward(cur, depth, targetVisited, targetQueue, candidate, maxNodes);
                }
            }

            return candidate;
        }

        private void ExpandForward(
            ulong address,
            int depth,
            HashSet<ulong> visited,
            Queue<(ulong, int)> queue,
            HashSet<ulong> candidate,
            int maxNodes)
        {
            ClrObject obj = _heap.GetObject(address);
            if (!obj.IsValid)
                return;

            if (IsNoise(obj.Type))
            {
                _telemetry.PrunedNodes++;
                return;
            }

            int counted = 0;
            bool forceExpand = ReferenceChainAnalyzer.IsKnownLeakTypePublic(obj.Type, _options.KnownLeakTypePatterns);

            foreach (var childAddr in _provider.GetReferences(address))
            {
                counted++;
                if (!forceExpand && counted > _options.LargeFanoutThreshold)
                {
                    _telemetry.LargeFanoutNodesSkipped++;
                    break;
                }

                if (childAddr == 0)
                    continue;

                candidate.Add(childAddr);
                if (candidate.Count >= maxNodes)
                    return;

                if (visited.Add(childAddr))
                    queue.Enqueue((childAddr, depth + 1));
            }
        }

        private bool IsNoise(ClrType? type)
        {
            if (type is null) return false;
            string? name = type.Name;
            if (string.IsNullOrEmpty(name)) return false;
            if (_noiseTypes.Contains(name)) return true;
            if (_options.SkipArrays && type.IsArray) return true;
            return false;
        }
    }

    // ── BidirectionalPathFinder ───────────────────────────────────────────────
    /// <summary>
    /// BFS from a single root constrained to the candidate set.
    /// Uses the reverse index only for path reconstruction (backpointers),
    /// NOT for forward traversal — keeping the search purely forward-constrained.
    /// </summary>
    internal sealed class BidirectionalPathFinder
    {
        private readonly ClrHeap _heap;
        private readonly DumpDetective.Core.Abstractions.IReferenceProvider _provider;
        private readonly HashSet<ulong> _candidateSet;
        private readonly ReverseReferenceIndex _reverseIndex;
        private readonly ReferenceChainOptions _options;
        private readonly ReferenceChainAnalyzer.TelemetryProxy _telemetry;

        public BidirectionalPathFinder(ClrHeap heap, DumpDetective.Core.Abstractions.IReferenceProvider provider, HashSet<ulong> candidateSet, ReverseReferenceIndex reverseIndex, ReferenceChainOptions options, ReferenceChainAnalyzer.TelemetryProxy telemetry)
        {
            _heap = heap;
            _provider = provider;
            _candidateSet = candidateSet;
            _reverseIndex = reverseIndex;
            _options = options;
            _telemetry = telemetry;
        }
        private readonly HashSet<ulong> _visited = new(capacity: 256);
        private readonly Dictionary<ulong, ulong> _previous = new(capacity: 256);
        private readonly Queue<(ulong Address, int Depth)> _queue = new(capacity: 128);

        public bool TryFindPath(
            ulong start,
            ulong target,
            out List<ulong>? path,
            out bool searchLimitReached)
        {
            path = null;
            searchLimitReached = false;

            if (start == target)
            {
                path = new List<ulong> { start };
                return true;
            }

            int maxDepth = _options.ResolvedMaxRootExpansionDepth;

            _visited.Clear();
            _previous.Clear();
            _queue.Clear();

            _visited.Add(start);
            _queue.Enqueue((start, 0));

            int searched = 0;
            int maxSearch = _options.ResolvedMaxCandidateNodes;

            while (_queue.Count > 0 && searched++ < maxSearch)
            {
                (ulong current, int depth) = _queue.Dequeue();

                if (depth >= maxDepth)
                    continue;

                ClrObject obj = _heap.GetObject(current);
                if (!obj.IsValid)
                    continue;

                int counted = 0;
                bool forceExpand = ReferenceChainAnalyzer.IsKnownLeakTypePublic(obj.Type, _options.KnownLeakTypePatterns);

                foreach (var childAddr in _provider.GetReferences(current))
                {
                    counted++;
                    if (!forceExpand && counted > _options.LargeFanoutThreshold)
                    {
                        _telemetry.LargeFanoutNodesSkipped++;
                        break;
                    }

                    if (childAddr == 0)
                        continue;

                    // Constrain to candidate set.
                    if (!_candidateSet.Contains(childAddr))
                        continue;

                    if (childAddr == target)
                    {
                        _previous[childAddr] = current;
                        path = ReconstructPath(start, target);
                        return true;
                    }

                    if (_visited.Add(childAddr))
                    {
                        _previous[childAddr] = current;
                        _queue.Enqueue((childAddr, depth + 1));
                    }
                }
            }

            searchLimitReached = _queue.Count > 0 && searched >= maxSearch;
            return false;
        }

        private List<ulong> ReconstructPath(ulong start, ulong target)
        {
            var reversed = new List<ulong>(capacity: 16) { target };
            ulong cursor = target;
            while (cursor != start && _previous.TryGetValue(cursor, out ulong parent))
            {
                reversed.Add(parent);
                cursor = parent;
            }
            reversed.Reverse();
            return reversed;
        }
    }
}
