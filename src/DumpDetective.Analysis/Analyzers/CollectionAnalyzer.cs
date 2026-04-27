using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Linq;
using System.Collections.Generic;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using Microsoft.Extensions.Logging;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers
{
    // TODO: Need to check why root stack frames are not showing in deep mode.
    // Suspect a bug in ReferenceChainAnalyzer's root description logic where it doesn't populate descriptions for certain stack roots.
    // This would impact the root hints shown for wasteful collections that are rooted in stacks.
    // Also, need to refactor this class. It's currently doing too much (identification, waste analysis, root description) and could be split into multiple focused classes or methods for clarity and maintainability.
    // Need to revisit the logic once again.
    public class CollectionAnalyzer : IAnalyzer
    {
        private CollectionAnalysisOptions _options;
        private readonly ILogger<CollectionAnalyzer>? _logger;

        public string Name => "Collection Analysis";
        public string Category => "Memory";

        public CollectionAnalyzer()
            : this(CollectionAnalysisOptions.Default, logger: null)
        {
        }

        /// <summary>Constructor for DI/factory use — options are read from the analysis context at run time.</summary>
        public CollectionAnalyzer(ILogger<CollectionAnalyzer>? logger)
            : this(CollectionAnalysisOptions.Default, logger)
        {
        }

        public CollectionAnalyzer(CollectionAnalysisOptions options, ILogger<CollectionAnalyzer>? logger = null)
        {
            _options = options ?? CollectionAnalysisOptions.Default;
            _logger = logger;
        }

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _options = context.GetOption<CollectionAnalysisOptions>();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, cancellationToken).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap)
        {
            return Analyze(heap, cache: null, progress: null, cancellationToken: CancellationToken.None);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var collectionStats = AnalyzeCollections(heap, cache, progress, cancellationToken);
            var domainResult = new CollectionDomainResult(
                collectionStats.TotalCollections,
                collectionStats.Dictionaries,
                collectionStats.Lists,
                collectionStats.ArrayLists,
                collectionStats.Stacks,
                collectionStats.SortedLists,
                collectionStats.SortedSets,
                collectionStats.HashSets,
                collectionStats.Queues,
                collectionStats.TotalWastedMemory,
                collectionStats.WastefulCollections.Count,
                collectionStats.WastefulCollections
                    .Take(_options.TopWastefulCollectionsToShow)
                .Select(w => new WastefulCollectionSnapshot(
                        w.Type,
                        (DumpDetective.Core.Models.CollectionKind)w.Kind,
                        w.Count,
                        w.Capacity,
                        w.FillRate,
                        w.WastedMemory,
                        w.Address,
                        w.Head,
                        w.Tail,
                        w.LargestContiguousFreeSegmentBytes,
                        w.FreeSegmentCount,
                        w.ElementSize,
                        w.ElementType,
                        w.SizeEstimateConfidence,
                        w.DetectionMethod,
                        w.RootDescription))
                    .ToList());

            // Copy metrics into domain result
            if (collectionStats.Metrics != null && collectionStats.Metrics.Count > 0)
            {
                domainResult = domainResult with { Metrics = collectionStats.Metrics };
            }

            if (collectionStats.TotalCollections == 0)
            {
                return domainResult;
            }

            return domainResult;
        }

        private CollectionStatistics AnalyzeCollections(ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var heapIdx))
            {
                // In-memory index: parallel over the flat entry array
                if (heapIdx.StorageKind == HeapIndexStorageKind.Memory && heapIdx.InMemoryEntries is { } entries)
                    return RunParallelCollectionAnalysis(heap, inMemoryEntries: entries, progress: progress, cancellationToken: cancellationToken, cache: cache);

                // Disk-backed index: sequential (I/O bound; parallel won't help)
                return AnalyzeCollectionsSequentialDisk(heap, heapCache, progress, cancellationToken);
            }

            // No cache: parallel over GC segments
            return RunParallelCollectionAnalysis(heap, inMemoryEntries: null, progress: progress, cancellationToken: cancellationToken, cache: cache);
        }

        // Unified parallel analysis — drives either a flat in-memory HeapEntry[] (cache path)
        // or a per-segment ClrObject walk (no-cache path) using the same concurrent accumulation logic.
        private CollectionStatistics RunParallelCollectionAnalysis(ClrHeap heap, HeapEntry[]? inMemoryEntries, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken, IHeapAnalysisCache? cache = null)
        {
            var methodTableKinds = new ConcurrentDictionary<ulong, CollectionKind>(
                concurrencyLevel: Math.Max(1, _options.MaxDegreeOfParallelism), capacity: 64);
            var concurrentWasteful = new ConcurrentBag<WastefulCollection>();
            int totalCollections = 0, dictionaries = 0, lists = 0, arrayLists = 0, stacks = 0, sortedLists = 0, sortedSets = 0, hashSets = 0, queues = 0;
            int skippedDictionaries = 0, skippedHashSets = 0, skippedQueues = 0, skippedLists = 0, skippedArrayLists = 0, skippedStacks = 0, skippedSortedLists = 0, skippedSortedSets = 0;
            long scanned = 0;
            const long progressInterval = 50_000;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism), CancellationToken = cancellationToken };
            var heapLock = _options.SerializeHeapAccess ? new object() : null;

            void ProcessEntry(ulong address, ulong mt)
            {
                long s = Interlocked.Increment(ref scanned);
                if (s % progressInterval == 0)
                    progress?.Report(new(s, "scanning collections"));
                CollectionKind kind;
                if (heapLock is object)
                {
                    lock (heapLock)
                        kind = ResolveCollectionKindConcurrent(heap, address, mt, methodTableKinds);
                }
                else
                {
                    kind = ResolveCollectionKindConcurrent(heap, address, mt, methodTableKinds);
                }
                if (kind == CollectionKind.None)
                    return;

                Interlocked.Increment(ref totalCollections);

                if (kind == CollectionKind.Dictionary)
                {
                    Interlocked.Increment(ref dictionaries);
                    var waste = AnalyzeDictionary(heap, address);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.Dictionary;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedDictionaries);
                }
                else if (kind == CollectionKind.List)
                {
                    Interlocked.Increment(ref lists);
                    var waste = AnalyzeList(heap, address);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.List;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedLists);
                }
                else if (kind == CollectionKind.HashSet)
                {
                    Interlocked.Increment(ref hashSets);
                    var waste = AnalyzeHashSet(heap, address);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.HashSet;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedHashSets);
                }
                else if (kind == CollectionKind.ArrayList)
                {
                    Interlocked.Increment(ref arrayLists);
                    var waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedArrayLists);
                }
                else if (kind == CollectionKind.Stack)
                {
                    Interlocked.Increment(ref stacks);
                    var waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedStacks);
                }
                else if (kind == CollectionKind.SortedList)
                {
                    Interlocked.Increment(ref sortedLists);
                    var waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedSortedLists);
                }
                else if (kind == CollectionKind.SortedSet)
                {
                    Interlocked.Increment(ref sortedSets);
                    var waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        concurrentWasteful.Add(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedSortedSets);
                }
                else if (kind == CollectionKind.Queue)
                {
                    Interlocked.Increment(ref queues);
                    var qWaste = AnalyzeQueue(heap, address);
                    if (qWaste != null && qWaste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        qWaste.Kind = CollectionKind.Queue;
                        concurrentWasteful.Add(qWaste);
                    }
                    else if (qWaste == null) Interlocked.Increment(ref skippedQueues);
                }

            }

            try
            {
                if (inMemoryEntries != null)
                {
                    Parallel.ForEach(inMemoryEntries, parallelOptions, entry =>
                    {
                        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
                        if (entry.Address == 0 || entry.MethodTable == 0)
                            return;
                        ProcessEntry(entry.Address, entry.MethodTable);
                    });
                }
                else
                {
                    Parallel.ForEach(heap.Segments, parallelOptions, segment =>
                    {
                        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
                        foreach (ClrObject obj in segment.EnumerateObjects())
                        {
                            if (!obj.IsValid || obj.Type is null)
                                continue;
                            ulong mt = obj.Type.MethodTable;
                            if (mt == 0)
                                continue;
                            ProcessEntry(obj.Address, mt);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Collection analysis cancelled by user.");
            }

            progress?.Report(new(scanned, "aggregating results"));
            var wastefulList = concurrentWasteful.OrderByDescending(w => w.WastedMemory).ToList();
            var stats = new CollectionStatistics
            {
                TotalCollections = totalCollections,
                Dictionaries = dictionaries,
                Lists = lists,
                ArrayLists = arrayLists,
                Stacks = stacks,
                SortedLists = sortedLists,
                SortedSets = sortedSets,
                HashSets = hashSets,
                Queues = queues,
                WastefulCollections = wastefulList,
                TotalWastedMemory = wastefulList.Aggregate(0UL, (acc, w) => acc + w.WastedMemory)
            };

            // Post-scan: populate root descriptions for top-N only — never during the scan loop.
            // Fast profile: use cheap cache.GetRootDescription only.
            // Balanced/Deep: additionally run ReferenceChainAnalyzer for items without a description.
            PopulateRootDescriptions(heap, cache, wastefulList, _options);

            // Reporting-level summary warnings are handled by the findings generator.
            // Log total wasted memory at debug level for diagnostic purposes.
            _logger?.LogDebug("Total wasted memory from collections: {TotalWastedBytes}", stats.TotalWastedMemory);
            _logger?.LogDebug(
                "Collection scan summary — recognized: Dict={Dicts}, List={Lists}, HashSet={HashSets}, Queue={Queues} | " +
                "probeNull (no fields/no waste): Dict={SkipD}, List={SkipL}, HashSet={SkipH}, Queue={SkipQ} | wasteful total={Wasteful}",
                dictionaries, lists, hashSets, queues,
                skippedDictionaries, skippedLists, skippedHashSets, skippedQueues,
                wastefulList.Count);

            // Aggregate metrics and simple percentiles for distributions
            try
            {
                var wastes = wastefulList.Select(w => (double)w.WastedMemory).OrderByDescending(v => v).ToArray();
                if (wastes.Length > 0)
                {
                    double avg = wastes.Average();
                    double median = wastes[wastes.Length / 2];
                    double p75 = wastes[(int)Math.Ceiling(wastes.Length * 0.75) - 1];
                    double p90 = wastes[(int)Math.Ceiling(wastes.Length * 0.90) - 1];
                    stats.TotalWastedMemory = wastefulList.Aggregate(0UL, (acc, w) => acc + w.WastedMemory);

                    var metrics = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Waste.AvgBytes"] = avg,
                        ["Waste.MedianBytes"] = median,
                        ["Waste.P75Bytes"] = p75,
                        ["Waste.P90Bytes"] = p90,
                        ["Waste.TotalBytes"] = stats.TotalWastedMemory,
                        ["Waste.Count"] = wastes.Length
                    };

                    // Per-kind counts for reporting
                    var perKindCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        [DumpDetective.Core.Models.CollectionKind.Dictionary.ToString()] = dictionaries,
                        [DumpDetective.Core.Models.CollectionKind.List.ToString()] = lists,
                        [DumpDetective.Core.Models.CollectionKind.ArrayList.ToString()] = arrayLists,
                        [DumpDetective.Core.Models.CollectionKind.Stack.ToString()] = stacks,
                        [DumpDetective.Core.Models.CollectionKind.SortedList.ToString()] = sortedLists,
                        [DumpDetective.Core.Models.CollectionKind.SortedSet.ToString()] = sortedSets,
                        [DumpDetective.Core.Models.CollectionKind.HashSet.ToString()] = hashSets,
                        [DumpDetective.Core.Models.CollectionKind.Queue.ToString()] = queues,
                    };

                    metrics["Waste.Counts.ByKind"] = perKindCounts;

                    // Histogram buckets (overall)
                    var buckets = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["<1KB"] = 0,
                        ["1KB-10KB"] = 0,
                        ["10KB-100KB"] = 0,
                        ["100KB-1MB"] = 0,
                        [">=1MB"] = 0
                    };

                    // Per-kind buckets
                    var kindBuckets = new Dictionary<CollectionKind, List<double>>();
                    foreach (CollectionKind k in Enum.GetValues(typeof(CollectionKind)))
                        kindBuckets[k] = new List<double>();

                    foreach (var w in wastefulList)
                    {
                        double v = w.WastedMemory;
                        if (v < 1 * 1024) buckets["<1KB"]++;
                        else if (v < 10 * 1024) buckets["1KB-10KB"]++;
                        else if (v < 100 * 1024) buckets["10KB-100KB"]++;
                        else if (v < 1024 * 1024) buckets["100KB-1MB"]++;
                        else buckets[">=1MB"]++;

                        if (kindBuckets.TryGetValue(w.Kind, out var kb))
                        {
                            kb.Add(v);
                        }
                    }

                    metrics["Waste.Histogram"] = buckets;
                    // Add per-kind histograms and percentiles
                    var perKindMetrics = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var kv in kindBuckets)
                    {
                        var arr = kv.Value.OrderByDescending(x => x).ToArray();
                        if (arr.Length == 0)
                        {
                            perKindMetrics[kv.Key.ToString()] = new Dictionary<string, object?>
                            {
                                ["Count"] = 0
                            };
                            continue;
                        }

                        double kAvg = arr.Average();
                        double kMedian = arr[arr.Length / 2];
                        double kP75 = arr[(int)Math.Ceiling(arr.Length * 0.75) - 1];
                        double kP90 = arr[(int)Math.Ceiling(arr.Length * 0.90) - 1];

                        perKindMetrics[kv.Key.ToString()] = new Dictionary<string, object?>
                        {
                            ["Count"] = arr.Length,
                            ["AvgBytes"] = kAvg,
                            ["MedianBytes"] = kMedian,
                            ["P75Bytes"] = kP75,
                            ["P90Bytes"] = kP90
                        };
                    }

                    metrics["Waste.Histogram.ByKind"] = perKindMetrics;
                    stats.Metrics = metrics;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error computing waste metrics");
            }

            return stats;
        }

        private CollectionStatistics AnalyzeCollectionsSequentialDisk(ClrHeap heap, HeapAnalysisCache heapCache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var stats = new CollectionStatistics();
            var wasteful = new List<WastefulCollection>();
            var methodTableKinds = new Dictionary<ulong, CollectionKind>(capacity: 64);
            var scanCounter = new ObjectScanCounter("scanning collections", progress);
            var heapLock = _options.SerializeHeapAccess ? new object() : null;

            foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogInformation("Collection analysis cancelled (disk-backed path).");
                    break;
                }
                scanCounter.Tick(wasteful.Count > 0 ? $"{wasteful.Count} wasteful" : null);

                ulong objectAddress = entry.Address;
                if (objectAddress == 0)
                    continue;

                CollectionKind kind;
                if (heapLock is object)
                {
                    lock (heapLock)
                        kind = ResolveCollectionKind(heap, entry, methodTableKinds);
                }
                else
                {
                    kind = ResolveCollectionKind(heap, entry, methodTableKinds);
                }

                if (kind == CollectionKind.Dictionary)
                {
                    stats.TotalCollections++;
                    stats.Dictionaries++;
                    stats.TotalCollections++;
                    stats.Dictionaries++;
                    var waste = AnalyzeDictionary(heap, objectAddress);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.Dictionary;
                        wasteful.Add(waste);
                    }
                }
                else if (kind == CollectionKind.List)
                {
                    stats.TotalCollections++;
                    stats.Lists++;
                    stats.TotalCollections++;
                    stats.Lists++;
                    var waste = AnalyzeList(heap, objectAddress);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.List;
                        wasteful.Add(waste);
                    }
                }
                else if (kind == CollectionKind.HashSet)
                {
                    stats.TotalCollections++;
                    stats.HashSets++;
                    stats.TotalCollections++;
                    stats.HashSets++;
                    var waste = AnalyzeHashSet(heap, objectAddress);
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.HashSet;
                        wasteful.Add(waste);
                    }
                }
                else if (kind == CollectionKind.Queue)
                {
                    stats.TotalCollections++;
                    stats.Queues++;
                    stats.TotalCollections++;
                    stats.Queues++;
                    var qWaste = AnalyzeQueue(heap, objectAddress);
                    if (qWaste != null && qWaste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        qWaste.Kind = CollectionKind.Queue;
                        wasteful.Add(qWaste);
                    }
                }
            }

            scanCounter.Complete(wasteful.Count > 0 ? $"{wasteful.Count} wasteful" : null);
            progress?.Report(new(scanCounter.Scanned, "aggregating results"));

            stats.WastefulCollections = wasteful.OrderByDescending(w => w.WastedMemory).ToList();
            stats.TotalWastedMemory = wasteful.Aggregate(0UL, (acc, w) => acc + w.WastedMemory);

            // Post-scan root descriptions for top-N — never per-item during the scan.
            PopulateRootDescriptions(heap, heapCache, stats.WastefulCollections, _options);

            return stats;
        }

        private static IEnumerable<HeapEntry> EnumerateCollectionEntries(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
            {
                foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                    yield return entry;

                yield break;
            }

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null)
                    continue;

                ulong methodTable = obj.Type.MethodTable;
                if (methodTable == 0)
                    continue;

                yield return new HeapEntry(obj.Address, methodTable, obj.Size);
            }
        }

        private static CollectionKind ResolveCollectionKind(ClrHeap heap, in HeapEntry entry, Dictionary<ulong, CollectionKind> methodTableKinds)
        {
            if (entry.MethodTable == 0)
                return CollectionKind.None;

            if (methodTableKinds.TryGetValue(entry.MethodTable, out CollectionKind existing))
                return existing;

            ClrObject obj = heap.GetObject(entry.Address);
            string typeName = obj.IsValid ? (obj.Type?.Name ?? string.Empty) : string.Empty;

            // Skip array objects (e.g. Dictionary<...>[]). We only analyze instance objects that
            // represent collection types themselves (List<>, Dictionary<>, HashSet<>, Queue<>).
            if (obj.IsValid && obj.Type?.IsArray == true)
            {
                methodTableKinds[entry.MethodTable] = CollectionKind.None;
                return CollectionKind.None;
            }

            CollectionKind resolved = CollectionKind.None;
            // Skip nested/inner types (e.g. ConcurrentDictionary+Node, Dictionary+Entry).
            // These are implementation details and have no backing capacity to analyze.
            if (typeName.Contains('+'))
            {
                methodTableKinds[entry.MethodTable] = resolved;
                return resolved;
            }

            // Match only well-known BCL collection namespaces to avoid false positives
            // from arbitrary application types whose names happen to contain these words.
            bool isBcl = typeName.StartsWith("System.Collections.", StringComparison.Ordinal)
                      || typeName.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                      || typeName.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal);

            if (isBcl)
            {
                // normalize to the outer (non-generic) type name to avoid matching nested generic args
                string outer = typeName;
                int cut = outer.IndexOfAny(new char[] { '`', '[', '<', '+' });
                if (cut >= 0) outer = outer.Substring(0, cut);
                int lastDot = outer.LastIndexOf('.');
                string shortName = lastDot >= 0 ? outer.Substring(lastDot + 1) : outer;

                // Exclude concurrent/non-array-backed variants explicitly
                if (shortName.StartsWith("Concurrent", StringComparison.OrdinalIgnoreCase) ||
                    shortName.IndexOf("BlockingCollection", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    methodTableKinds[entry.MethodTable] = CollectionKind.None;
                    return CollectionKind.None;
                }

                if (string.Equals(shortName, "Dictionary", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.Dictionary;
                else if (string.Equals(shortName, "List", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.List;
                else if (string.Equals(shortName, "HashSet", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.HashSet;
                else if (string.Equals(shortName, "Queue", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.Queue;
                else if (string.Equals(shortName, "ArrayList", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.ArrayList;
                else if (string.Equals(shortName, "Stack", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.Stack;
                else if (string.Equals(shortName, "SortedList", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.SortedList;
                else if (string.Equals(shortName, "ArrayList", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.ArrayList;
                else if (string.Equals(shortName, "Stack", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.Stack;
                else if (string.Equals(shortName, "SortedList", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.SortedList;
                else if (string.Equals(shortName, "SortedSet", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.SortedSet;
            }

            methodTableKinds[entry.MethodTable] = resolved;
            return resolved;
        }

        private static readonly char[] s_typeNameCutChars = ['`', '[', '<', '+'];

        private static CollectionKind ResolveCollectionKindConcurrent(
            ClrHeap heap, ulong address, ulong methodTable,
            ConcurrentDictionary<ulong, CollectionKind> methodTableKinds)
        {
            return methodTableKinds.GetOrAdd(methodTable, static (mt, state) =>
            {
                ClrObject obj = state.heap.GetObject(state.address);
                string typeName = obj.IsValid ? (obj.Type?.Name ?? string.Empty) : string.Empty;

                // Skip arrays (e.g. Dictionary<...>[]). Only classify actual collection instances.
                if (obj.IsValid && obj.Type?.IsArray == true)
                    return CollectionKind.None;

                // Skip nested/inner types (e.g. ConcurrentDictionary+Node).
                if (typeName.Contains('+'))
                    return CollectionKind.None;

                bool isBcl = typeName.StartsWith("System.Collections.", StringComparison.Ordinal)
                          || typeName.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                          || typeName.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal);

                if (!isBcl) return CollectionKind.None;

                string outer = typeName;
                int cut = outer.IndexOfAny(s_typeNameCutChars);
                if (cut >= 0) outer = outer.Substring(0, cut);
                int lastDot = outer.LastIndexOf('.');
                string shortName = lastDot >= 0 ? outer.Substring(lastDot + 1) : outer;

                // Exclude concurrent/non-array-backed variants explicitly
                if (shortName.StartsWith("Concurrent", StringComparison.OrdinalIgnoreCase) ||
                    shortName.IndexOf("BlockingCollection", StringComparison.OrdinalIgnoreCase) >= 0)
                    return CollectionKind.None;

                if (string.Equals(shortName, "Dictionary", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.Dictionary;
                if (string.Equals(shortName, "List", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.List;
                if (string.Equals(shortName, "HashSet", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.HashSet;
                if (string.Equals(shortName, "Queue", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.Queue;
                if (string.Equals(shortName, "ArrayList", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.ArrayList;
                if (string.Equals(shortName, "Stack", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.Stack;
                if (string.Equals(shortName, "SortedList", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.SortedList;
                if (string.Equals(shortName, "SortedSet", StringComparison.OrdinalIgnoreCase))
                    return CollectionKind.SortedSet;

                return CollectionKind.None;
            }, (heap, address));
        }



        // Populate root descriptions for the top-N items only, after the scan is complete.
        // This avoids the catastrophic O(n * heap-walk) cost of doing it per item during scanning.
        // Fast profile: cache.GetRootDescription only (O(1) lookup).
        // Balanced/Deep: additionally runs ReferenceChainAnalyzer BFS for items still missing a description.
        private void PopulateRootDescriptions(ClrHeap heap, IHeapAnalysisCache? cache, List<WastefulCollection> wastefulList, CollectionAnalysisOptions options)
        {
            if (wastefulList.Count == 0)
                return;

            int topN = Math.Min(options.PathAnalysisTopN, wastefulList.Count);

            // Phase 1 (all profiles): cheap cache lookup — O(1) per item.
            if (cache is not null)
            {
                try
                {
                    for (int i = 0; i < topN; i++)
                    {
                        var item = wastefulList[i];
                        item.RootDescription = cache.GetRootDescription(item.Address);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error during cheap root description lookup for top-N collections");
                }
            }

            // Phase 2 (Balanced/Deep only): BFS path search for items still missing a description.
            if (options.Profile != AnalysisProfile.Fast && cache is not null)
            {
                try
                {
                    var chainAnalyzer = new ReferenceChainAnalyzer();
                    for (int i = 0; i < topN; i++)
                    {
                        var item = wastefulList[i];
                        if (!string.IsNullOrEmpty(item.RootDescription))
                            continue;
                        bool retained = chainAnalyzer.AnalyzeObject(heap, cache, item.Address);
                        item.RootDescription = retained ? "Retained (reference path found)" : "No root path found (within budget)";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error during targeted reference-path analysis for collections");
                }
            }
        }

        // Generic handler for array-backed collection types with a count/size field and an items/entries array.
        private WastefulCollection? AnalyzeArrayBackedCollection(ClrHeap heap, ulong address, CollectionKind kind)
        {
            try
            {
                ClrObject obj = heap.GetObject(address);
                if (!obj.IsValid || obj.Type == null)
                    return null;

                var sizeField = obj.Type.GetFieldByName("_size") ?? obj.Type.GetFieldByName("_count") ?? obj.Type.Fields.FirstOrDefault(f => f.ElementType == Microsoft.Diagnostics.Runtime.ClrElementType.Int32);
                var itemsField = obj.Type.GetFieldByName("_items") ?? obj.Type.GetFieldByName("_entries") ?? obj.Type.Fields.FirstOrDefault(f => f.Type?.IsArray == true);

                if (sizeField == null || itemsField == null)
                    return null;

                int count = Math.Max(0, sizeField.Read<int>(obj, interior: false));
                var itemsObj = itemsField.ReadObject(obj, interior: false);
                if (!itemsObj.IsValid || !itemsObj.IsArray)
                    return null;

                int capacity = itemsObj.AsArray().Length;
                if (capacity <= 0 || count >= capacity)
                    return null;

                double fillRate = (count / (double)capacity) * 100;
                var compType = itemsObj.Type?.ComponentType ?? obj.Type?.ComponentType;
                ulong elementSize = 0;
                if (compType != null)
                {
                    if (compType.IsValueType)
                        elementSize = (ulong)compType.StaticSize;
                    else
                        elementSize = (ulong)IntPtr.Size;
                }
                else
                {
                    elementSize = itemsObj.Size / (ulong)capacity;
                }

                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                return new WastefulCollection
                {
                    Address = obj.Address,
                    Type = obj.Type?.Name ?? "Collection",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory,
                    ElementSize = elementSize,
                    ElementType = compType?.Name ?? string.Empty,
                    SizeEstimateConfidence = compType != null ? "High" : "Low",
                    DetectionMethod = itemsField?.Name ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                if (_options.SurfaceProbingExceptions)
                    _logger?.LogError(ex, "Error analyzing collection at {Address}", address);
                else
                    _logger?.LogDebug(ex, "Ignored error analyzing collection at {Address}", address);
#if DEBUG
                throw;
#endif
            }

            return null;
        }

        private WastefulCollection? AnalyzeDictionary(ClrHeap heap, ulong dictionaryAddress)
        {
            try
            {
                ClrObject dictObj = heap.GetObject(dictionaryAddress);
                if (!dictObj.IsValid || dictObj.Type == null)
                {
                    _logger?.LogDebug("[Dictionary] 0x{Address:X} invalid or no type", dictionaryAddress);
                    return null;
                }

                var countField = dictObj.Type?.GetFieldByName("_count")
                    ?? dictObj.Type?.GetFieldByName("count")     // .NET Framework
                    ?? dictObj.Type?.GetFieldByName("m_count");  // some custom runtimes
                var entriesField = dictObj.Type?.GetFieldByName("_entries");

                // Fallback: look for any instance field that is an array if named field not found
                if (entriesField == null)
                    entriesField = dictObj.Type.Fields.FirstOrDefault(f => f.Type?.IsArray == true);

                if (countField == null)
                {
                    _logger?.LogDebug("[Dictionary] 0x{Address:X} type={Type}: _count field not found", dictionaryAddress, dictObj.Type?.Name);
                    return null;
                }
                if (entriesField == null)
                {
                    _logger?.LogDebug("[Dictionary] 0x{Address:X} type={Type}: no backing array field found (tried _entries + fallback)", dictionaryAddress, dictObj.Type?.Name);
                    return null;
                }

                int count = Math.Max(0, countField.Read<int>(dictObj, interior: false));
                var entriesObj = entriesField.ReadObject(dictObj, interior: false);

                if (!entriesObj.IsValid || !entriesObj.IsArray)
                {
                    // Null/uninitialized entries = empty dict never populated, no waste.
                    return null;
                }

                int capacity = entriesObj.AsArray().Length;

                // No waste if fully packed or empty
                if (capacity <= 0 || count >= capacity)
                {
                    // suppressed debug: no actionable waste detected for this collection
                    return null;
                }

                double fillRate = (count / (double)capacity) * 100;
                // Prefer component type size when available. For value types use StaticSize; for references use pointer size.
                ulong elementSize = 0;
                var compType = entriesObj.Type?.ComponentType ?? dictObj.Type?.ComponentType;
                if (compType != null)
                {
                    if (compType.IsValueType)
                        elementSize = (ulong)compType.StaticSize;
                    else
                        elementSize = (ulong)(IntPtr.Size);
                }
                else
                {
                    elementSize = entriesObj.Size / (ulong)capacity; // fallback
                }
                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                // Root descriptions are populated post-scan for the top-N only (see PopulateRootDescriptions).
                return new WastefulCollection
                {
                    Address = dictObj.Address,
                    Type = dictObj.Type?.Name ?? "Dictionary",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory,
                    ElementSize = elementSize,
                    ElementType = compType?.Name ?? string.Empty,
                    SizeEstimateConfidence = compType != null ? "High" : "Low",
                    DetectionMethod = entriesField?.Name ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                if (_options.SurfaceProbingExceptions)
                    _logger?.LogError(ex, "Error analyzing Dictionary at {Address}", dictionaryAddress);
                else
                    _logger?.LogDebug(ex, "Ignored error analyzing Dictionary at {Address}", dictionaryAddress);
#if DEBUG
                throw;
#endif
            }

            return null;
        }

        private WastefulCollection? AnalyzeQueue(ClrHeap heap, ulong queueAddress)
        {
            try
            {
                ClrObject queueObj = heap.GetObject(queueAddress);
                if (!queueObj.IsValid || queueObj.Type == null)
                    return null;

                // Common field names in BCL: _array, _head, _tail, _size (.NET Core/Framework varies)
                var arrayField = queueObj.Type.GetFieldByName("_array") ?? queueObj.Type.Fields.FirstOrDefault(f => f.Type?.IsArray == true);
                // head/tail are optional — used for contiguous-free-segment diagnostics only, not required for waste detection
                var headField = queueObj.Type.GetFieldByName("_head") ?? queueObj.Type.Fields.FirstOrDefault(f => f.Name?.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0);
                var tailField = queueObj.Type.GetFieldByName("_tail") ?? queueObj.Type.Fields.FirstOrDefault(f => f.Name?.IndexOf("tail", StringComparison.OrdinalIgnoreCase) >= 0);
                var sizeField = queueObj.Type.GetFieldByName("_size") ?? queueObj.Type.GetFieldByName("_count") ?? queueObj.Type.Fields.FirstOrDefault(f => f.Name?.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name?.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0);

                // Only array + size are required to compute waste; head/tail are best-effort for diagnostics.
                if (arrayField == null)
                {
                    _logger?.LogDebug("[Queue] 0x{Address:X} type={Type}: no backing array field found", queueAddress, queueObj.Type?.Name);
                    return null;
                }
                if (sizeField == null)
                {
                    _logger?.LogDebug("[Queue] 0x{Address:X} type={Type}: no size/count field found", queueAddress, queueObj.Type?.Name);
                    return null;
                }

                var arrayObj = arrayField.ReadObject(queueObj, interior: false);
                if (!arrayObj.IsValid || !arrayObj.IsArray)
                {
                    _logger?.LogDebug("[Queue] 0x{Address:X} type={Type}: backing array object invalid or not array", queueAddress, queueObj.Type?.Name);
                    return null;
                }

                int capacity = arrayObj.AsArray().Length;
                int size = Math.Max(0, sizeField.Read<int>(queueObj, interior: false));

                // read head/tail if available to compute contiguous free segments
                int? head = null;
                int? tail = null;
                try
                {
                    if (headField != null)
                        head = headField.Read<int>(queueObj, interior: false);
                }
                catch { head = null; }
                try
                {
                    if (tailField != null)
                        tail = tailField.Read<int>(queueObj, interior: false);
                }
                catch { tail = null; }

                // No waste if full or empty
                if (capacity <= 0 || size >= capacity)
                {
                    // suppressed debug: no actionable waste detected for this collection
                    return null;
                }

                // compute fill rate and approximate element size similar to lists
                double fillRate = (size / (double)capacity) * 100;
                ulong elementSize = 0;
                var compType = arrayObj.Type?.ComponentType ?? queueObj.Type?.ComponentType;
                if (compType != null)
                {
                    if (compType.IsValueType)
                        elementSize = (ulong)compType.StaticSize;
                    else
                        elementSize = (ulong)(IntPtr.Size);
                }
                else
                {
                    elementSize = arrayObj.Size / (ulong)capacity;
                }

                // compute wasted memory from slots and contiguous free-segment metrics using helper
                ulong wastedMemory = CollectionAnalysisHelpers.ComputeWastedMemoryFromSlots(capacity, size, elementSize);
                var (freeSegments, largestFreeSlots) = CollectionAnalysisHelpers.ComputeQueueFreeSegments(capacity, size, head);
                ulong largestFreeBytes = (ulong)largestFreeSlots * elementSize;

                return new WastefulCollection
                {
                    Address = queueObj.Address,
                    Type = queueObj.Type?.Name ?? "Queue",
                    Count = size,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory,
                    ElementSize = elementSize,
                    ElementType = compType?.Name ?? string.Empty,
                    SizeEstimateConfidence = compType != null ? "High" : "Low",
                    DetectionMethod = arrayField?.Name ?? string.Empty,
                    Head = head,
                    Tail = tail,
                    FreeSegmentCount = freeSegments,
                    LargestContiguousFreeSegmentBytes = largestFreeBytes
                };
            }
            catch (Exception ex)
            {
                if (_options.SurfaceProbingExceptions)
                    _logger?.LogError(ex, "Error analyzing Queue at {Address}", queueAddress);
                else
                    _logger?.LogDebug(ex, "Ignored error analyzing Queue at {Address}", queueAddress);
#if DEBUG
                throw;
#endif
            }

            return null;
        }

        private WastefulCollection? AnalyzeList(ClrHeap heap, ulong listAddress)
        {
            try
            {
                ClrObject listObj = heap.GetObject(listAddress);
                if (!listObj.IsValid || listObj.Type == null)
                    return null;

                var sizeField = listObj.Type?.GetFieldByName("_size");
                var itemsField = listObj.Type?.GetFieldByName("_items");

                if (sizeField == null || itemsField == null)
                    return null;

                int count = Math.Max(0, sizeField.Read<int>(listObj, interior: false));
                var itemsObj = itemsField.ReadObject(listObj, interior: false);

                if (!itemsObj.IsValid || !itemsObj.IsArray)
                    return null;

                int capacity = itemsObj.AsArray().Length;

                // No waste if fully packed or empty
                if (capacity <= 0 || count >= capacity)
                    return null;

                double fillRate = (count / (double)capacity) * 100;
                // Prefer component type size when available. For value types use StaticSize; for references use pointer size.
                ulong elementSize = 0;
                var compType = itemsObj.Type?.ComponentType ?? listObj.Type?.ComponentType;
                if (compType != null)
                {
                    if (compType.IsValueType)
                        elementSize = (ulong)compType.StaticSize;
                    else
                        elementSize = (ulong)(IntPtr.Size);
                }
                else
                {
                    elementSize = itemsObj.Size / (ulong)capacity;
                }
                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                // Root descriptions are populated post-scan for the top-N only (see PopulateRootDescriptions).
                return new WastefulCollection
                {
                    Address = listObj.Address,
                    Type = listObj.Type?.Name ?? "List",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory,
                    ElementSize = elementSize,
                    ElementType = compType?.Name ?? string.Empty,
                    SizeEstimateConfidence = compType != null ? "High" : "Low",
                    DetectionMethod = itemsField?.Name ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                if (_options.SurfaceProbingExceptions)
                    _logger?.LogError(ex, "Error analyzing List at {Address}", listAddress);
                else
                    _logger?.LogDebug(ex, "Ignored error analyzing List at {Address}", listAddress);
#if DEBUG
                throw;
#endif
            }

            return null;
        }

        private WastefulCollection? AnalyzeHashSet(ClrHeap heap, ulong hashSetAddress)
        {
            try
            {
                ClrObject hashSetObj = heap.GetObject(hashSetAddress);
                if (!hashSetObj.IsValid || hashSetObj.Type == null)
                {
                    _logger?.LogDebug("[HashSet] 0x{Address:X} invalid or no type", hashSetAddress);
                    return null;
                }

                var countField = hashSetObj.Type?.GetFieldByName("_count")
                    ?? hashSetObj.Type?.GetFieldByName("count")     // .NET Framework
                    ?? hashSetObj.Type?.GetFieldByName("m_count");  // some custom runtimes
                var entriesField = hashSetObj.Type?.GetFieldByName("_entries");

                // Fallback: look for any instance field that is an array if named field not found
                if (entriesField == null)
                    entriesField = hashSetObj.Type.Fields.FirstOrDefault(f => f.Type?.IsArray == true);

                if (countField == null)
                {
                    _logger?.LogDebug("[HashSet] 0x{Address:X} type={Type}: _count field not found", hashSetAddress, hashSetObj.Type?.Name);
                    return null;
                }
                if (entriesField == null)
                {
                    _logger?.LogDebug("[HashSet] 0x{Address:X} type={Type}: no backing array field found (tried _entries + fallback)", hashSetAddress, hashSetObj.Type?.Name);
                    return null;
                }

                int count = Math.Max(0, countField.Read<int>(hashSetObj, interior: false));
                var entriesObj = entriesField.ReadObject(hashSetObj, interior: false);

                if (!entriesObj.IsValid || !entriesObj.IsArray)
                {
                    // Null/uninitialized entries = empty set never populated, no waste.
                    return null;
                }

                int capacity = entriesObj.AsArray().Length;

                // No waste if fully packed or empty
                if (capacity <= 0 || count >= capacity)
                {
                    // suppressed debug: no actionable waste detected for this collection
                    return null;
                }

                double fillRate = (count / (double)capacity) * 100;
                // Prefer component type size when available. For value types use StaticSize; for references use pointer size.
                ulong elementSize = 0;
                var compType = entriesObj.Type?.ComponentType ?? hashSetObj.Type?.ComponentType;
                if (compType != null)
                {
                    if (compType.IsValueType)
                        elementSize = (ulong)compType.StaticSize;
                    else
                        elementSize = (ulong)(IntPtr.Size);
                }
                else
                {
                    elementSize = entriesObj.Size / (ulong)capacity;
                }
                ulong wastedSlots = (ulong)(capacity - count);
                ulong wastedMemory = wastedSlots * elementSize;

                return new WastefulCollection
                {
                    Address = hashSetObj.Address,
                    Type = hashSetObj.Type?.Name ?? "HashSet",
                    Count = count,
                    Capacity = capacity,
                    FillRate = fillRate,
                    WastedMemory = wastedMemory,
                    ElementSize = elementSize,
                    ElementType = compType?.Name ?? string.Empty,
                    SizeEstimateConfidence = compType != null ? "High" : "Low",
                    DetectionMethod = entriesField?.Name ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                if (_options.SurfaceProbingExceptions)
                    _logger?.LogError(ex, "Error analyzing HashSet at {Address}", hashSetAddress);
                else
                    _logger?.LogDebug(ex, "Ignored error analyzing HashSet at {Address}", hashSetAddress);
#if DEBUG
                throw;
#endif
            }

            return null;
        }

    }

    internal class CollectionStatistics
    {
        public int TotalCollections { get; set; }
        public int Dictionaries { get; set; }
        public int Lists { get; set; }
        public int ArrayLists { get; set; }
        public int Stacks { get; set; }
        public int SortedLists { get; set; }
        public int SortedSets { get; set; }
        public int HashSets { get; set; }
        public int Queues { get; set; }
        public ulong TotalWastedMemory { get; set; }
        public List<WastefulCollection> WastefulCollections { get; set; } = new();
        public IReadOnlyDictionary<string, object?> Metrics { get; set; } = new Dictionary<string, object?>();
    }

    internal class WastefulCollection
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
        public CollectionKind Kind { get; set; }
        public int Count { get; set; }
        public int Capacity { get; set; }
        public double FillRate { get; set; }
        public ulong WastedMemory { get; set; }
        public ulong ElementSize { get; set; }
        public string ElementType { get; set; } = string.Empty;
        public string SizeEstimateConfidence { get; set; } = "Unknown";
        public string DetectionMethod { get; set; } = string.Empty;
        public string? RootDescription { get; set; }
        // Queue-specific diagnostics
        public int? Head { get; set; }
        public int? Tail { get; set; }
        public ulong? LargestContiguousFreeSegmentBytes { get; set; }
        public int? FreeSegmentCount { get; set; }
    }
}


