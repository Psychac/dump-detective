using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using System.Reflection;
using System;
using System.Collections.Generic;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using Microsoft.Extensions.Logging;
using DumpDetective.Core.Options;
using DumpDetective.Core.Enums;
using DumpDetective.Analysis.Pipeline;

namespace DumpDetective.Analysis.Analyzers
{
    // TODO: Need to check why root stack frames are not showing in deep mode.
    // Suspect a bug in ReferenceChainAnalyzer's root description logic where it doesn't populate descriptions for certain stack roots.
    // This would impact the root hints shown for wasteful collections that are rooted in stacks.
    // Also, need to refactor this class. It's currently doing too much (identification, waste analysis, root description) and could be split into multiple focused classes or methods for clarity and maintainability.
    // Need to revisit the logic once again.
    public class CollectionAnalyzer : IAnalyzer, IHeapIndexScanParticipant
    {
        // Cache of resolved interesting instance fields per MethodTable to avoid
        // repeatedly enumerating ClrType.Fields for every object of the same type.
        private static readonly ConcurrentDictionary<ulong, FieldLayout> s_fieldLayoutCache = new(concurrencyLevel: 4, capacity: 256);

        private readonly struct FieldLayout
        {
            public readonly ClrInstanceField? SizeField;
            public readonly ClrInstanceField? CountField;
            public readonly ClrInstanceField? ItemsField;
            public readonly ClrInstanceField? EntriesField;
            public readonly ClrInstanceField? ArrayField;
            public readonly ClrInstanceField? HeadField;
            public readonly ClrInstanceField? TailField;
            public readonly ClrInstanceField? AnyIntField;
            public readonly ClrType? ComponentType;
            public readonly int ComponentStaticSize;
            public readonly ulong ComputedElementSize;

            public FieldLayout(ClrInstanceField? sizeField, ClrInstanceField? countField, ClrInstanceField? itemsField, ClrInstanceField? entriesField, ClrInstanceField? arrayField, ClrInstanceField? headField, ClrInstanceField? tailField, ClrInstanceField? anyIntField, ClrType? componentType, int componentStaticSize, ulong computedElementSize)
            {
                SizeField = sizeField;
                CountField = countField;
                ItemsField = itemsField;
                EntriesField = entriesField;
                ArrayField = arrayField;
                HeadField = headField;
                TailField = tailField;
                AnyIntField = anyIntField;
                ComponentType = componentType;
                ComponentStaticSize = componentStaticSize;
                ComputedElementSize = computedElementSize;
            }
        }
        private CollectionAnalysisOptions _options;
        private readonly ILogger<CollectionAnalyzer>? _logger;

        // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
        // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
        // OnHeapEntry; consumed by BuildStatsFromParticipantState once the shared index scan
        // has completed. Mirrors the locals of the old AnalyzeCollectionsSequentialDisk.
        private ClrHeap? _heap;
        private IHeapAnalysisCache? _cache;
        private CollectionStatistics? _stats;
        private List<WastefulCollection>? _wasteful;
        private Dictionary<ulong, CollectionKind>? _methodTableKinds;
        private Dictionary<CollectionKind, int[]>? _generationCounts;
        private PropertyInfo? _generationProperty;
        private MethodInfo? _getGenerationMethod;
        private ObjectScanCounter? _scanCounter;
        private IProgress<AnalyzerProgressReport>? _progress;
        private int _topCapacity;
        private int _wastefulCount;
        private ulong _totalWasted;
        // Set by OnHeapIndexScanCompleted — the single source of truth for whether the
        // participant-accumulated state above is trustworthy. Avoids re-deriving "did the
        // shared scan run" from a second cache.TryGetHeapIndex call in AnalyzeCollections.
        private bool _participantScanSucceeded;

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
            _options = context.AnalysisOptions.Collection;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, cancellationToken).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap)
        {
            return Analyze(heap, cache: null, progress: null, cancellationToken: CancellationToken.None);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var collectionStats = AnalyzeCollections(heap, cache, progress, cancellationToken);
            int topToShow = Math.Min(_options.TopWastefulCollectionsToShow, collectionStats.WastefulCollections.Count);
            var topSnapshots = new List<WastefulCollectionSnapshot>(topToShow);
            for (int i = 0; i < topToShow; i++)
            {
                WastefulCollection w = collectionStats.WastefulCollections[i];
                topSnapshots.Add(new WastefulCollectionSnapshot(
                    w.Type,
                    (CollectionKind)w.Kind,
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
                    w.RootDescription));
            }

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
                collectionStats.WastefulCollectionCount,
                topSnapshots,
                collectionStats.WasteCountsByKind,
                collectionStats.GenerationBreakdown);

            if (collectionStats.TotalCollections == 0)
            {
                return domainResult;
            }

            return domainResult;
        }

        private CollectionStatistics AnalyzeCollections(ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            if (_participantScanSucceeded)
                return BuildStatsFromParticipantState(heap, cache);

            // No cache, or shared scan unavailable/failed: parallel over GC segments
            return RunParallelCollectionAnalysis(heap, inMemoryEntries: null, progress: progress, cancellationToken: cancellationToken, cache: cache);
        }

        /// <summary>
        /// Resets the accumulator state for the shared heap-index scan pass, mirroring the setup
        /// AnalyzeCollectionsSequentialDisk used to do before its loop.
        /// </summary>
        public void BeforeHeapIndexScan(AnalysisContext context)
        {
            _options = context.AnalysisOptions.Collection;
            _heap = context.Heap;
            _cache = context.Cache;
            _progress = context.Progress;

            _stats = new CollectionStatistics();
            _topCapacity = Math.Max(1, Math.Max(_options.TopWastefulCollectionsToShow, _options.PathAnalysisTopN));
            _wasteful = new List<WastefulCollection>(_topCapacity);
            _methodTableKinds = new Dictionary<ulong, CollectionKind>(capacity: 64);
            _generationProperty = typeof(ClrObject).GetProperty("Generation");
            _getGenerationMethod = typeof(ClrHeap).GetMethod("GetGeneration", new[] { typeof(ulong) });

            _generationCounts = new Dictionary<CollectionKind, int[]>(capacity: 16);
            foreach (CollectionKind k in Enum.GetValues(typeof(CollectionKind)))
                _generationCounts[k] = new int[4];

            _scanCounter = new ObjectScanCounter("scanning collections", _progress);
            _wastefulCount = 0;
            _totalWasted = 0;
        }

        /// <summary>
        /// Called once per disk-backed index entry, in address order, during the shared heap-index
        /// scan pass. Mirrors the old AnalyzeCollectionsSequentialDisk loop body, operating on
        /// instance fields. Per-entry cancellation checks and heap-access locking are dropped: the
        /// dispatcher already throws on cancellation per entry, and the shared pass is single-threaded.
        /// </summary>
        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

        public void OnHeapIndexScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

        private void OnHeapEntry(in HeapEntry entry)
        {
            ClrHeap heap = _heap!;
            CollectionStatistics stats = _stats!;
            List<WastefulCollection> wasteful = _wasteful!;
            Dictionary<ulong, CollectionKind> methodTableKinds = _methodTableKinds!;
            Dictionary<CollectionKind, int[]> generationCounts = _generationCounts!;

            if (_scanCounter!.ShouldReport())
                _scanCounter.Report(wasteful.Count > 0 ? $"{wasteful.Count} wasteful" : null);

            ulong objectAddress = entry.Address;
            if (objectAddress == 0)
                return;

            CollectionKind kind = ResolveCollectionKind(heap, entry, methodTableKinds);

            if (kind == CollectionKind.Dictionary)
            {
                stats.TotalCollections++;
                stats.Dictionaries++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.Dictionary][idx]++;
                }
                catch { }
                var waste = AnalyzeDictionary(heap, objectAddress);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = CollectionKind.Dictionary;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.List)
            {
                stats.TotalCollections++;
                stats.Lists++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.List][idx]++;
                }
                catch { }
                var waste = AnalyzeList(heap, objectAddress);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = CollectionKind.List;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.HashSet)
            {
                stats.TotalCollections++;
                stats.HashSets++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.HashSet][idx]++;
                }
                catch { }
                var waste = AnalyzeHashSet(heap, objectAddress);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = CollectionKind.HashSet;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.Queue)
            {
                stats.TotalCollections++;
                stats.Queues++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.Queue][idx]++;
                }
                catch { }
                var qWaste = AnalyzeQueue(heap, objectAddress);
                if (qWaste != null && qWaste.WastedMemory > _options.WasteThresholdBytes)
                {
                    qWaste.Kind = CollectionKind.Queue;
                    _wastefulCount++;
                    _totalWasted += qWaste.WastedMemory;
                    AddToTopWasteful(wasteful, qWaste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.ArrayList)
            {
                stats.TotalCollections++;
                stats.ArrayLists++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.ArrayList][idx]++;
                }
                catch { }
                var waste = AnalyzeArrayBackedCollection(heap, objectAddress, kind);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = kind;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.Stack)
            {
                stats.TotalCollections++;
                stats.Stacks++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.Stack][idx]++;
                }
                catch { }
                var waste = AnalyzeArrayBackedCollection(heap, objectAddress, kind);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = kind;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.SortedList)
            {
                stats.TotalCollections++;
                stats.SortedLists++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.SortedList][idx]++;
                }
                catch { }
                var waste = AnalyzeArrayBackedCollection(heap, objectAddress, kind);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = kind;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
            else if (kind == CollectionKind.SortedSet)
            {
                stats.TotalCollections++;
                stats.SortedSets++;
                try
                {
                    int gen = ResolveGeneration(heap, objectAddress, _generationProperty, _getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    generationCounts[CollectionKind.SortedSet][idx]++;
                }
                catch { }
                var waste = AnalyzeArrayBackedCollection(heap, objectAddress, kind);
                if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                {
                    waste.Kind = kind;
                    _wastefulCount++;
                    _totalWasted += waste.WastedMemory;
                    AddToTopWasteful(wasteful, waste, _topCapacity);
                }
            }
        }

        /// <summary>
        /// Reads back the accumulator state populated by BeforeHeapIndexScan/OnHeapEntry once the
        /// shared dispatcher pass has completed. Replaces the tail of the old
        /// AnalyzeCollectionsSequentialDisk (everything after its loop).
        /// </summary>
        private CollectionStatistics BuildStatsFromParticipantState(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            CollectionStatistics stats = _stats!;
            List<WastefulCollection> wasteful = _wasteful!;

            _scanCounter!.Complete(wasteful.Count > 0 ? $"{wasteful.Count} wasteful" : null);
            _progress?.Report(new(_scanCounter.Scanned, "aggregating results"));

            wasteful.Sort(static (a, b) => b.WastedMemory.CompareTo(a.WastedMemory));

            stats.WastefulCollections = wasteful;
            stats.WastefulCollectionCount = _wastefulCount;
            stats.TotalWastedMemory = _totalWasted;

            // populate generation breakdown for disk path
            try
            {
                var genList = new List<CollectionGenerationStats>();
                foreach (var kv in _generationCounts!)
                {
                    var a = kv.Value;
                    genList.Add(new CollectionGenerationStats(kv.Key, a[0], a[1], a[2], a[3]));
                }
                stats.GenerationBreakdown = genList;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error computing generation breakdown (disk path)");
            }

            // Aggregate a typed per-kind breakdown for reporting.
            try
            {
                int wasteCount = stats.WastefulCollectionCount;
                if (wasteCount > 0)
                {
                    var wasteCountsByKind = new Dictionary<CollectionKind, int>(8)
                    {
                        [CollectionKind.Dictionary] = stats.Dictionaries,
                        [CollectionKind.List] = stats.Lists,
                        [CollectionKind.ArrayList] = stats.ArrayLists,
                        [CollectionKind.Stack] = stats.Stacks,
                        [CollectionKind.SortedList] = stats.SortedLists,
                        [CollectionKind.SortedSet] = stats.SortedSets,
                        [CollectionKind.HashSet] = stats.HashSets,
                        [CollectionKind.Queue] = stats.Queues,
                    };
                    stats.WasteCountsByKind = wasteCountsByKind;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error computing waste metrics (disk path)");
            }

            // Post-scan root descriptions for top-N — never per-item during the scan.
            PopulateRootDescriptions(heap, cache, stats.WastefulCollections, _options);

            return stats;
        }

        // Unified parallel analysis — drives either a flat in-memory HeapEntry[] (cache path)
        // or a per-segment ClrObject walk (no-cache path) using the same concurrent accumulation logic.
        private CollectionStatistics RunParallelCollectionAnalysis(ClrHeap heap, HeapEntry[]? inMemoryEntries, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken, IHeapAnalysisCache? cache = null)
        {
            // Cache reflection handles for generation resolution (compatible across ClrMD versions)
            PropertyInfo? generationProperty = typeof(ClrObject).GetProperty("Generation");
            MethodInfo? getGenerationMethod = typeof(ClrHeap).GetMethod("GetGeneration", new[] { typeof(ulong) });

            // Per-kind generation counts: index 0=Gen0,1=Gen1,2=Gen2,3=LOH/large
            var generationCounts = new ConcurrentDictionary<CollectionKind, int[]>(concurrencyLevel: Math.Max(1, _options.MaxDegreeOfParallelism), capacity: 16);
            foreach (CollectionKind k in Enum.GetValues(typeof(CollectionKind)))
                generationCounts.TryAdd(k, new int[4]);

            var methodTableKinds = new ConcurrentDictionary<ulong, CollectionKind>(
                concurrencyLevel: Math.Max(1, _options.MaxDegreeOfParallelism), capacity: 64);
            int topCapacity = Math.Max(1, Math.Max(_options.TopWastefulCollectionsToShow, _options.PathAnalysisTopN));
            int kindCount = Enum.GetValues(typeof(CollectionKind)).Length;
            var localWaste = new ThreadLocal<LocalWasteAccumulator>(() => new LocalWasteAccumulator(topCapacity, kindCount), trackAllValues: true);
            int totalCollections = 0, dictionaries = 0, lists = 0, arrayLists = 0, stacks = 0, sortedLists = 0, sortedSets = 0, hashSets = 0, queues = 0;
            int skippedDictionaries = 0, skippedHashSets = 0, skippedQueues = 0, skippedLists = 0, skippedArrayLists = 0, skippedStacks = 0, skippedSortedLists = 0, skippedSortedSets = 0;
            int wastefulCount = 0;
            ulong totalWastedMemory = 0;
            int wasteUnder1Kb = 0, waste1To10Kb = 0, waste10To100Kb = 0, waste100KbTo1Mb = 0, wasteAtLeast1Mb = 0;
            int[] wasteCountByKind = new int[kindCount];
            ulong[] wasteBytesByKind = new ulong[kindCount];
            long scanned = 0;
            const long progressInterval = 50_000;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism), CancellationToken = cancellationToken };
            // ClrMD heap/field reads are not reliably thread-safe under this analyzer's
            // parallel per-entry execution, so we always serialize the critical heap reads.
            var heapLock = new object();

            void TrackWasteful(WastefulCollection waste)
            {
                LocalWasteAccumulator? local = localWaste.Value;
                if (local == null)
                    return;

                ulong wasteBytes = waste.WastedMemory;
                local.WastefulCount++;
                local.TotalWastedMemory += wasteBytes;

                if (wasteBytes < 1 * 1024)
                    local.WasteUnder1Kb++;
                else if (wasteBytes < 10 * 1024)
                    local.Waste1To10Kb++;
                else if (wasteBytes < 100 * 1024)
                    local.Waste10To100Kb++;
                else if (wasteBytes < 1024 * 1024)
                    local.Waste100KbTo1Mb++;
                else
                    local.WasteAtLeast1Mb++;

                int kindIndex = (int)waste.Kind;
                if ((uint)kindIndex < (uint)local.WasteCountByKind.Length)
                {
                    local.WasteCountByKind[kindIndex]++;
                    local.WasteBytesByKind[kindIndex] += wasteBytes;
                }

                AddToTopWasteful(local.TopWasteful, waste, topCapacity);
            }

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

                // determine generation and increment per-kind generation counter
                try
                {
                    int gen;
                    lock (heapLock)
                        gen = ResolveGeneration(heap, address, generationProperty, getGenerationMethod);
                    int idx = gen >= 3 ? 3 : Math.Max(0, gen);
                    var arr = generationCounts.GetOrAdd(kind, _ => new int[4]);
                    Interlocked.Increment(ref arr[idx]);
                }
                catch { /* best-effort, ignore generation failures */ }

                Interlocked.Increment(ref totalCollections);

                if (kind == CollectionKind.Dictionary)
                {
                    Interlocked.Increment(ref dictionaries);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeDictionary(heap, address); }
                    }
                    else
                    {
                        waste = AnalyzeDictionary(heap, address);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.Dictionary;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedDictionaries);
                }
                else if (kind == CollectionKind.List)
                {
                    Interlocked.Increment(ref lists);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeList(heap, address); }
                    }
                    else
                    {
                        waste = AnalyzeList(heap, address);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.List;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedLists);
                }
                else if (kind == CollectionKind.HashSet)
                {
                    Interlocked.Increment(ref hashSets);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeHashSet(heap, address); }
                    }
                    else
                    {
                        waste = AnalyzeHashSet(heap, address);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = CollectionKind.HashSet;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedHashSets);
                }
                else if (kind == CollectionKind.ArrayList)
                {
                    Interlocked.Increment(ref arrayLists);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeArrayBackedCollection(heap, address, kind); }
                    }
                    else
                    {
                        waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedArrayLists);
                }
                else if (kind == CollectionKind.Stack)
                {
                    Interlocked.Increment(ref stacks);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeArrayBackedCollection(heap, address, kind); }
                    }
                    else
                    {
                        waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedStacks);
                }
                else if (kind == CollectionKind.SortedList)
                {
                    Interlocked.Increment(ref sortedLists);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeArrayBackedCollection(heap, address, kind); }
                    }
                    else
                    {
                        waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedSortedLists);
                }
                else if (kind == CollectionKind.SortedSet)
                {
                    Interlocked.Increment(ref sortedSets);
                    WastefulCollection? waste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { waste = AnalyzeArrayBackedCollection(heap, address, kind); }
                    }
                    else
                    {
                        waste = AnalyzeArrayBackedCollection(heap, address, kind);
                    }
                    if (waste != null && waste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        waste.Kind = kind;
                        TrackWasteful(waste);
                    }
                    else if (waste == null) Interlocked.Increment(ref skippedSortedSets);
                }
                else if (kind == CollectionKind.Queue)
                {
                    Interlocked.Increment(ref queues);
                    WastefulCollection? qWaste;
                    if (heapLock is object)
                    {
                        lock (heapLock) { qWaste = AnalyzeQueue(heap, address); }
                    }
                    else
                    {
                        qWaste = AnalyzeQueue(heap, address);
                    }
                    if (qWaste != null && qWaste.WastedMemory > _options.WasteThresholdBytes)
                    {
                        qWaste.Kind = CollectionKind.Queue;
                        TrackWasteful(qWaste);
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
                            parallelOptions.CancellationToken.ThrowIfCancellationRequested();
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
            var wastefulList = new List<WastefulCollection>(topCapacity);
            foreach (LocalWasteAccumulator local in localWaste.Values)
            {
                wastefulCount += local.WastefulCount;
                totalWastedMemory += local.TotalWastedMemory;
                wasteUnder1Kb += local.WasteUnder1Kb;
                waste1To10Kb += local.Waste1To10Kb;
                waste10To100Kb += local.Waste10To100Kb;
                waste100KbTo1Mb += local.Waste100KbTo1Mb;
                wasteAtLeast1Mb += local.WasteAtLeast1Mb;

                for (int i = 0; i < kindCount; i++)
                {
                    wasteCountByKind[i] += local.WasteCountByKind[i];
                    wasteBytesByKind[i] += local.WasteBytesByKind[i];
                }

                for (int i = 0; i < local.TopWasteful.Count; i++)
                    AddToTopWasteful(wastefulList, local.TopWasteful[i], topCapacity);
            }
            wastefulList.Sort(static (a, b) => b.WastedMemory.CompareTo(a.WastedMemory));
            localWaste.Dispose();

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
                WastefulCollectionCount = wastefulCount,
                TotalWastedMemory = totalWastedMemory
            };

            // materialize generation breakdown
            try
            {
                var genList = new List<CollectionGenerationStats>();
                foreach (var kv in generationCounts)
                {
                    var a = kv.Value;
                    genList.Add(new CollectionGenerationStats(kv.Key, a[0], a[1], a[2], a[3]));
                }
                stats.GenerationBreakdown = genList;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error computing generation breakdown");
            }

            // Post-scan: populate root descriptions for top-N only — never during the scan loop.
            // Balanced/Deep only: run ReferenceChainAnalyzer for items without a description.
            PopulateRootDescriptions(heap, cache, wastefulList, _options);

            // Reporting-level summary warnings are handled by the findings generator.
            // Log total wasted memory at debug level for diagnostic purposes.
            _logger?.LogDebug("Total wasted memory from collections: {TotalWastedBytes}", stats.TotalWastedMemory);
            _logger?.LogDebug(
                "Collection scan summary — recognized: Dict={Dicts}, List={Lists}, HashSet={HashSets}, Queue={Queues} | " +
                "probeNull (no fields/no waste): Dict={SkipD}, List={SkipL}, HashSet={SkipH}, Queue={SkipQ} | wasteful total={Wasteful}",
                dictionaries, lists, hashSets, queues,
                skippedDictionaries, skippedLists, skippedHashSets, skippedQueues,
                stats.WastefulCollectionCount);

            // Aggregate a typed per-kind breakdown for reporting.
            try
            {
                int wasteCount = stats.WastefulCollectionCount;
                if (wasteCount > 0)
                {
                    var wasteCountsByKind = new Dictionary<CollectionKind, int>(8)
                    {
                        [CollectionKind.Dictionary] = dictionaries,
                        [CollectionKind.List] = lists,
                        [CollectionKind.ArrayList] = arrayLists,
                        [CollectionKind.Stack] = stacks,
                        [CollectionKind.SortedList] = sortedLists,
                        [CollectionKind.SortedSet] = sortedSets,
                        [CollectionKind.HashSet] = hashSets,
                        [CollectionKind.Queue] = queues,
                    };
                    stats.WasteCountsByKind = wasteCountsByKind;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error computing waste metrics");
            }

            return stats;
        }

        public void Dispose() { }

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
                else if (string.Equals(shortName, "SortedSet", StringComparison.OrdinalIgnoreCase))
                    resolved = CollectionKind.SortedSet;
            }

            methodTableKinds[entry.MethodTable] = resolved;
            return resolved;
        }

        private static void AddToTopWasteful(List<WastefulCollection> topList, WastefulCollection candidate, int capacity)
        {
            if (topList.Count < capacity)
            {
                topList.Add(candidate);
                return;
            }

            int minIndex = 0;
            ulong minWaste = topList[0].WastedMemory;
            for (int i = 1; i < topList.Count; i++)
            {
                ulong current = topList[i].WastedMemory;
                if (current < minWaste)
                {
                    minWaste = current;
                    minIndex = i;
                }
            }

            if (candidate.WastedMemory <= minWaste)
                return;

            topList[minIndex] = candidate;
        }

        private static ClrInstanceField? FindFirstArrayField(ClrType? type)
        {
            if (type == null)
                return null;

            foreach (ClrInstanceField field in type.Fields)
            {
                if (field.Type?.IsArray == true)
                    return field;
            }

            return null;
        }

        private static ClrInstanceField? FindFirstInt32Field(ClrType? type)
        {
            if (type == null)
                return null;

            foreach (ClrInstanceField field in type.Fields)
            {
                if (field.ElementType == ClrElementType.Int32)
                    return field;
            }

            return null;
        }

        private static ClrInstanceField? FindFieldByNameContains(ClrType? type, string token)
        {
            if (type == null)
                return null;

            foreach (ClrInstanceField field in type.Fields)
            {
                string? name = field.Name;
                if (name != null && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return field;
            }

            return null;
        }

        private static ClrInstanceField? FindFieldByNameContainsAny(ClrType? type, string tokenA, string tokenB)
        {
            if (type == null)
                return null;

            foreach (ClrInstanceField field in type.Fields)
            {
                string? name = field.Name;
                if (name == null)
                    continue;

                if (name.IndexOf(tokenA, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf(tokenB, StringComparison.OrdinalIgnoreCase) >= 0)
                    return field;
            }

            return null;
        }

        private static FieldLayout GetOrBuildFieldLayout(ClrType? type)
        {
            if (type == null)
                return default;

            ulong mt = type.MethodTable;
            return s_fieldLayoutCache.GetOrAdd(mt, _ =>
            {
                // Prefer well-known field names first (fast path), then fall back to a single
                // enumeration over fields to find reasonable candidates.
                ClrInstanceField? sizeField = type.GetFieldByName("_size") ?? type.GetFieldByName("_count") ?? type.GetFieldByName("m_count");
                ClrInstanceField? countField = type.GetFieldByName("_count") ?? type.GetFieldByName("m_count") ?? type.GetFieldByName("count");
                ClrInstanceField? itemsField = type.GetFieldByName("_items") ?? type.GetFieldByName("_entries") ?? type.GetFieldByName("_array");
                ClrInstanceField? entriesField = type.GetFieldByName("_entries");
                ClrInstanceField? arrayField = type.GetFieldByName("_array");
                ClrInstanceField? headField = type.GetFieldByName("_head");
                ClrInstanceField? tailField = type.GetFieldByName("_tail");
                ClrInstanceField? anyInt = null;
                ClrType? compType = type.ComponentType;
                int compStaticSize = compType != null ? compType.StaticSize : 0;
                ulong computedElementSize = 0;
                if (compType != null)
                {
                    computedElementSize = compType.IsValueType ? (ulong)compType.StaticSize : (ulong)IntPtr.Size;
                }

                if (sizeField == null || itemsField == null || entriesField == null || arrayField == null || headField == null || tailField == null || anyInt == null)
                {
                    foreach (ClrInstanceField f in type.Fields)
                    {
                        if (anyInt == null && f.ElementType == ClrElementType.Int32)
                            anyInt = f;

                        if (arrayField == null && f.Type?.IsArray == true)
                            arrayField = f;

                        string? name = f.Name;
                        if (name == null)
                            continue;

                        if (itemsField == null && (name.IndexOf("items", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("entries", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("array", StringComparison.OrdinalIgnoreCase) >= 0))
                            itemsField = f;

                        if (entriesField == null && name.IndexOf("entries", StringComparison.OrdinalIgnoreCase) >= 0)
                            entriesField = f;

                        if (headField == null && name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0)
                            headField = f;

                        if (tailField == null && name.IndexOf("tail", StringComparison.OrdinalIgnoreCase) >= 0)
                            tailField = f;

                        if (sizeField == null && (name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0))
                            sizeField = sizeField ?? f;
                    }
                }

                // Normalize fallbacks: prefer itemsField -> arrayField; entriesField -> itemsField -> arrayField
                ClrInstanceField? resolvedItems = itemsField ?? arrayField;
                ClrInstanceField? resolvedEntries = entriesField ?? itemsField ?? arrayField;
                ClrInstanceField? resolvedSize = sizeField ?? countField ?? anyInt;
                ClrInstanceField? resolvedCount = countField ?? sizeField ?? anyInt;

                return new FieldLayout(resolvedSize, resolvedCount, resolvedItems, resolvedEntries, arrayField, headField, tailField, anyInt, compType, compStaticSize, computedElementSize);
            });
        }

        private static void TrySetComputedElementSize(ulong methodTable, ClrType? compType, ulong computedSize)
        {
            if (computedSize == 0)
                return;

            while (true)
            {
                if (!s_fieldLayoutCache.TryGetValue(methodTable, out var existing))
                {
                    var newLayout = new FieldLayout(existing.SizeField, existing.CountField, existing.ItemsField, existing.EntriesField, existing.ArrayField, existing.HeadField, existing.TailField, existing.AnyIntField, compType, compType != null ? compType.StaticSize : 0, computedSize);
                    if (s_fieldLayoutCache.TryAdd(methodTable, newLayout))
                        return;
                    continue;
                }

                if (existing.ComputedElementSize != 0)
                    return;

                var updated = new FieldLayout(existing.SizeField, existing.CountField, existing.ItemsField, existing.EntriesField, existing.ArrayField, existing.HeadField, existing.TailField, existing.AnyIntField, existing.ComponentType ?? compType, existing.ComponentStaticSize != 0 ? existing.ComponentStaticSize : (compType != null ? compType.StaticSize : 0), computedSize);
                if (s_fieldLayoutCache.TryUpdate(methodTable, updated, existing))
                    return;
            }
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
        // Balanced/Deep only: runs ReferenceChainAnalyzer BFS for items still missing a description.
        private void PopulateRootDescriptions(ClrHeap heap, IHeapAnalysisCache? cache, List<WastefulCollection> wastefulList, CollectionAnalysisOptions options)
        {
            if (wastefulList.Count == 0)
                return;

            int topN = Math.Min(options.PathAnalysisTopN, wastefulList.Count);

            // Balanced/Deep only: BFS path search for items still missing a description.
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

                var layout = GetOrBuildFieldLayout(obj.Type);
                var sizeField = layout.SizeField ?? layout.AnyIntField;
                var itemsField = layout.ItemsField ?? layout.EntriesField ?? layout.ArrayField;

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
                // Prefer cached computed size from the layout if available.
                var layoutMt = obj.Type?.MethodTable ?? 0UL;
                if (layout.ComputedElementSize != 0)
                {
                    elementSize = layout.ComputedElementSize;
                }
                else if (compType != null)
                {
                    elementSize = compType.IsValueType ? (ulong)compType.StaticSize : (ulong)IntPtr.Size;
                    // Try to cache this for future objects of the same MethodTable
                    TrySetComputedElementSize(layoutMt, compType, elementSize);
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

                var layout = GetOrBuildFieldLayout(dictObj.Type);
                var countField = layout.CountField ?? layout.SizeField ?? layout.AnyIntField;
                var entriesField = layout.EntriesField ?? layout.ItemsField ?? layout.ArrayField;

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
                var layoutMt = dictObj.Type?.MethodTable ?? 0UL;
                var compType = entriesObj.Type?.ComponentType ?? dictObj.Type?.ComponentType ?? layout.ComponentType;
                if (layout.ComputedElementSize != 0)
                {
                    elementSize = layout.ComputedElementSize;
                }
                else if (compType != null)
                {
                    elementSize = compType.IsValueType ? (ulong)compType.StaticSize : (ulong)(IntPtr.Size);
                    TrySetComputedElementSize(layoutMt, compType, elementSize);
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
                var layout = GetOrBuildFieldLayout(queueObj.Type);
                var arrayField = layout.ArrayField ?? layout.ItemsField ?? layout.EntriesField ?? FindFirstArrayField(queueObj.Type);
                var headField = layout.HeadField ?? FindFieldByNameContains(queueObj.Type, "head");
                var tailField = layout.TailField ?? FindFieldByNameContains(queueObj.Type, "tail");
                var sizeField = layout.SizeField ?? layout.CountField ?? layout.AnyIntField ?? FindFieldByNameContainsAny(queueObj.Type, "size", "count");

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
                var layoutMt = queueObj.Type?.MethodTable ?? 0UL;
                var compType = arrayObj.Type?.ComponentType ?? queueObj.Type?.ComponentType ?? layout.ComponentType;
                if (layout.ComputedElementSize != 0)
                {
                    elementSize = layout.ComputedElementSize;
                }
                else if (compType != null)
                {
                    elementSize = compType.IsValueType ? (ulong)compType.StaticSize : (ulong)(IntPtr.Size);
                    TrySetComputedElementSize(layoutMt, compType, elementSize);
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

                var layout = GetOrBuildFieldLayout(listObj.Type);
                var sizeField = layout.SizeField ?? layout.AnyIntField;
                var itemsField = layout.ItemsField ?? layout.EntriesField ?? layout.ArrayField;

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
                var layoutMt = listObj.Type?.MethodTable ?? 0UL;
                var compType = itemsObj.Type?.ComponentType ?? listObj.Type?.ComponentType ?? layout.ComponentType;
                if (layout.ComputedElementSize != 0)
                {
                    elementSize = layout.ComputedElementSize;
                }
                else if (compType != null)
                {
                    elementSize = compType.IsValueType ? (ulong)compType.StaticSize : (ulong)(IntPtr.Size);
                    TrySetComputedElementSize(layoutMt, compType, elementSize);
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

                var layout = GetOrBuildFieldLayout(hashSetObj.Type);
                var countField = layout.CountField ?? layout.SizeField ?? layout.AnyIntField;
                var entriesField = layout.EntriesField ?? layout.ItemsField ?? layout.ArrayField ?? FindFirstArrayField(hashSetObj.Type);

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
                var layoutMt = hashSetObj.Type?.MethodTable ?? 0UL;
                var compType = entriesObj.Type?.ComponentType ?? hashSetObj.Type?.ComponentType ?? layout.ComponentType;
                if (layout.ComputedElementSize != 0)
                {
                    elementSize = layout.ComputedElementSize;
                }
                else if (compType != null)
                {
                    elementSize = compType.IsValueType ? (ulong)compType.StaticSize : (ulong)(IntPtr.Size);
                    TrySetComputedElementSize(layoutMt, compType, elementSize);
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
            }

            return null;
        }

        private static int ResolveGeneration(ClrHeap heap, ulong address, PropertyInfo? generationProperty, MethodInfo? getGenerationMethod)
        {
            try
            {
                if (getGenerationMethod != null)
                {
                    object? val = getGenerationMethod.Invoke(heap, new object[] { address });
                    if (val is int gi) return gi;
                    if (val is uint gu) return (int)gu;
                }

                if (generationProperty != null)
                {
                    ClrObject obj = heap.GetObject(address);
                    object boxed = obj;
                    object? value = generationProperty.GetValue(boxed);
                    if (value is int g) return g;
                    if (value is uint ug) return (int)ug;
                }
            }
            catch { }

            // Fallback to Gen2 when uncertain
            return 2;
        }

        private sealed class LocalWasteAccumulator
        {
            public readonly List<WastefulCollection> TopWasteful;
            public readonly int[] WasteCountByKind;
            public readonly ulong[] WasteBytesByKind;
            public int WastefulCount;
            public ulong TotalWastedMemory;
            public int WasteUnder1Kb;
            public int Waste1To10Kb;
            public int Waste10To100Kb;
            public int Waste100KbTo1Mb;
            public int WasteAtLeast1Mb;

            public LocalWasteAccumulator(int topCapacity, int kindCount)
            {
                TopWasteful = new List<WastefulCollection>(topCapacity);
                WasteCountByKind = new int[kindCount];
                WasteBytesByKind = new ulong[kindCount];
            }
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
        public int WastefulCollectionCount { get; set; }
        public List<WastefulCollection> WastefulCollections { get; set; } = new();
        public IReadOnlyDictionary<CollectionKind, int> WasteCountsByKind { get; set; } = new Dictionary<CollectionKind, int>();
        public IReadOnlyList<CollectionGenerationStats>? GenerationBreakdown { get; set; }
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


