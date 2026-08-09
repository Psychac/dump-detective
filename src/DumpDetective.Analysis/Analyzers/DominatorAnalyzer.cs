using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class DominatorAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant
{
    public string Name => "Dominator Analysis";
    public string Category => "Memory";
    public int Order => 110;

    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
    private ClrHeap? _heap;
    private IHeapAnalysisCache? _cache;
    private Dictionary<ulong, int>? _referenceCount;
    private long _skippedReferenceAddresses;
    private bool _objectScanCapped;
    private long _objectsTraced;
    private int _maxScan;
    private int _maxReferenceAddresses;
    private ObjectScanCounter? _scanCounter;
    private IProgress<AnalyzerProgressReport>? _progress;
    // Set by OnHeapIndexScanCompleted — the single source of truth for whether the
    // participant-accumulated state above is trustworthy. Avoids re-deriving "did the
    // shared scan run" from a second cache.TryGetHeapIndex call in AnalyzeAsync.
    private bool _participantScanSucceeded;

    /// <summary>
    /// Resets the leak-signal reference-counting accumulator ahead of the shared heap-index scan.
    /// The candidate-scoring / bounded-graph-walk section of <see cref="Analyze"/> is unaffected —
    /// it never scans the full index and stays a self-contained, on-demand pass.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        _heap = context.Heap;
        _cache = context.Cache;

        ExecutionPolicy policy = context.AnalysisOptions.ExecutionPolicy;
        _maxScan = policy.MaxLeakScanObjects;
        _maxReferenceAddresses = policy.MaxReferenceAddresses;
        _progress = context.Progress;

        _referenceCount = new Dictionary<ulong, int>(capacity: 4096);
        _skippedReferenceAddresses = 0;
        _objectScanCapped = false;
        _objectsTraced = 0;
        _scanCounter = new ObjectScanCounter("scanning heap objects", context.Progress);
    }

    /// <summary>
    /// Called once per disk-backed index entry, in address order, during the shared heap-index
    /// scan pass. Ports the former index-tuple fast-path loop body (see the no-index fallback
    /// still in <see cref="AnalyzeObjectsPass"/> for the case the dispatcher didn't run).
    /// Explicit interface implementation because <see cref="HeapEntry"/> is internal and this
    /// class is public — an implicit implementation would leak the internal type as public API.
    /// </summary>
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

    public void OnHeapIndexScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new DominatorAnalyzer();

    // Sums each worker's independently-capped reference-count map by address key, subject to
    // the same _maxReferenceAddresses cap the sequential path enforces via AccumulateReference.
    // Mirrors AsyncTaskAnalyzer.MergePartial's merge-then-trim pattern; _objectsTraced is summed
    // for diagnostics only and doesn't gate anything after the scan completes.
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        Dictionary<ulong, int> referenceCount = _referenceCount!;
        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (DominatorAnalyzer)p;
            if (other._referenceCount is null)
                continue;

            foreach (KeyValuePair<ulong, int> kvp in other._referenceCount)
                MergeReferenceCount(kvp.Key, kvp.Value, referenceCount, _maxReferenceAddresses, ref _skippedReferenceAddresses);

            _skippedReferenceAddresses += other._skippedReferenceAddresses;
            _objectsTraced += other._objectsTraced;
            _objectScanCapped |= other._objectScanCapped;
        }
    }

    private void OnHeapEntry(in HeapEntry entry)
    {
        _scanCounter!.Tick();

        if (_objectScanCapped) return;

        ulong objectAddress = entry.Address;
        if (objectAddress == 0) return;

        if (!_cache!.MethodTableHasOutgoingRefs(_heap!, entry.MethodTable))
            return;

        if (_maxScan > 0 && _objectsTraced >= _maxScan)
        {
            _objectScanCapped = true;
            return;
        }

        CountIncomingReferencesByAddress(_heap!, objectAddress, _referenceCount!, _maxReferenceAddresses, ref _skippedReferenceAddresses);
        _objectsTraced++;
    }

    // Relies on the pipeline dispatcher having already called BeforeHeapIndexScan/OnHeapEntry
    // for this context (when an on-disk heap index exists) before AnalyzeAsync runs.
    private LeakSignals BuildLeakSignalsFromParticipantState(ClrHeap heap, RetentionOptions options)
    {
        _scanCounter!.Complete();
        _progress?.Report(new(_scanCounter.Scanned, "building leak signals"));

        Dictionary<ulong, int> referenceCount = _referenceCount!;
        IReadOnlyList<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = ExtractHighlyReferencedObjects(heap, referenceCount, options);
        int highlyReferencedCount = CountHighlyReferencedObjects(referenceCount, options);

        return new LeakSignals(
            highlyReferencedCount,
            _skippedReferenceAddresses,
            topHighlyReferencedObjects,
            _objectScanCapped);
    }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RetentionOptions options = context.AnalysisOptions.MemoryLeak;
        ExecutionPolicy policy = context.AnalysisOptions.ExecutionPolicy;

        LeakSignals signals = _participantScanSucceeded
            ? BuildLeakSignalsFromParticipantState(context.Heap, options)
            : AnalyzeObjectsPass(context.Heap, context.Cache, options, policy, context.Progress);

        AnalyzerDomainResult result = Analyze(context.Heap, context.Cache, options, signals, cancellationToken).Stamp(this);

        return ValueTask.FromResult(result);
    }

    private static DominatorDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache cache,
        RetentionOptions options,
        LeakSignals signals,
        CancellationToken cancellationToken)
    {
        Dictionary<string, CachedTypeStatistics> typeStats = cache.GetOrBuildTypeStatistics(heap);
        if (typeStats.Count == 0)
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>(), MaxTopDominatorTypesToShow: options.TopHighlyReferencedObjectsToShow);

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? aggregates = null;
        if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
            aggregates = heapIndex.TypeAggregates;

        var candidates = new List<(string TypeName, ulong SampleAddress, int Count, ulong TotalSize, ulong LohSize, long Gen2Count, ulong Score)>(capacity: Math.Min(32, typeStats.Count));

        foreach (KeyValuePair<string, CachedTypeStatistics> kv in typeStats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong sampleAddress = cache.GetSampleInstanceAddress(kv.Key) ?? 0;
            if (sampleAddress == 0)
                continue;

            ulong totalSize = kv.Value.TotalSize;
            ulong lohSize = kv.Value.LohSize;
            int count = kv.Value.Count;
            long gen2Count = 0;

            if (aggregates is not null)
            {
                ClrObject sample = heap.GetObject(sampleAddress);
                if (sample.IsValid && sample.Type is not null && aggregates.TryGetValue(sample.Type.MethodTable, out TypeAggregateIndexEntry aggregate))
                {
                    gen2Count = aggregate.Gen2Count;
                    totalSize = aggregate.TotalSize;
                    lohSize = aggregate.LohSize;
                    count = (int)Math.Min(int.MaxValue, aggregate.Count);
                }
            }

            ulong averageSize = count > 0 ? Math.Max(1UL, totalSize / (ulong)count) : 1;
            ulong score = totalSize + lohSize + (ulong)Math.Max(0, gen2Count) * averageSize;
            if (count >= 1_000)
                score += totalSize / 4;

            candidates.Add((kv.Key, sampleAddress, count, totalSize, lohSize, gen2Count, score));
        }

        List<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = signals.TopHighlyReferencedObjects as List<HighlyReferencedObjectSnapshot>
            ?? new List<HighlyReferencedObjectSnapshot>(signals.TopHighlyReferencedObjects);
        PopulateRetainedBytes(heap, topHighlyReferencedObjects, options);
        PopulateEvidence(heap, cache, topHighlyReferencedObjects);
        IReadOnlyList<RetentionTypeSnapshot> topRetentionTypes = BuildTopRetentionTypes(topHighlyReferencedObjects);
        ulong topHighlyReferencedTotalBytes = SumTopHighlyReferencedBytes(topHighlyReferencedObjects);

        if (candidates.Count == 0)
        {
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>())
            {
                HeuristicOnly = true,
                MaxBreadth = options.MaxLeakScanObjects,
                MaxDepth = 20,
                HighlyReferencedObjectCount = signals.HighlyReferencedObjectCount,
                SkippedReferenceAddresses = signals.SkippedReferenceAddresses,
                TopHighlyReferencedObjects = topHighlyReferencedObjects,
                ObjectScanCapped = signals.ObjectScanCapped,
                TopRetentionTypes = topRetentionTypes,
                TopHighlyReferencedTotalBytes = topHighlyReferencedTotalBytes,
                MaxTopDominatorTypesToShow = options.TopHighlyReferencedObjectsToShow
            };
        }

        candidates.Sort(static (a, b) =>
        {
            int score = b.Score.CompareTo(a.Score);
            if (score != 0)
                return score;

            int size = b.TotalSize.CompareTo(a.TotalSize);
            if (size != 0)
                return size;

            return StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
        });

        int topCount = Math.Min(options.TopHighlyReferencedObjectsToShow, candidates.Count);
        var topTypes = new List<TypeSnapshot>(topCount);
        ulong totalEstimatedRetainedBytes = 0;

        int maxBreadth = options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000;
        const int MaxDepth = 20;

        // Use a shared visited set for all top-K types to produce exclusive (non-overlapping) retained-size semantics,
        // matching the semantics of PopulateRetainedBytes. This ensures the two retained-byte metrics are comparable.
        var visited = new HashSet<ulong>(capacity: Math.Min(topCount * 256, 4096));

        for (int i = 0; i < topCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string typeName, ulong sampleAddress, int count, ulong totalSize, ulong lohSize, long gen2Count, _) = candidates[i];
            ClrObject root = heap.GetObject(sampleAddress);
            if (!root.IsValid || root.Type is null)
                continue;

            ulong retainedBytes = BoundedGraphWalk.ComputeExclusiveRetained(root, heap, visited, maxBreadth, MaxDepth);
            totalEstimatedRetainedBytes += retainedBytes;

            ulong averageSize = count > 0 ? totalSize / (ulong)count : 0;
            topTypes.Add(new TypeSnapshot(
                typeName,
                count,
                totalSize,
                lohSize,
                AverageSize: averageSize,
                EstimatedRetainedBytes: retainedBytes,
                SampleAddress: sampleAddress,
                Gen2Count: gen2Count));
        }

        topTypes.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        return new DominatorDomainResult(
            candidates.Count,
            topTypes.Count,
            totalEstimatedRetainedBytes,
            topTypes,
            HeuristicOnly: true,
            MaxBreadth: maxBreadth,
            MaxDepth: MaxDepth,
            HighlyReferencedObjectCount: signals.HighlyReferencedObjectCount,
            SkippedReferenceAddresses: signals.SkippedReferenceAddresses,
            TopHighlyReferencedObjects: topHighlyReferencedObjects,
            ObjectScanCapped: signals.ObjectScanCapped,
            TopRetentionTypes: topRetentionTypes,
            TopHighlyReferencedTotalBytes: topHighlyReferencedTotalBytes,
            MaxTopDominatorTypesToShow: options.TopHighlyReferencedObjectsToShow);
    }

    // No-index fallback: the pipeline dispatcher only calls BeforeHeapIndexScan/OnHeapEntry when
    // an on-disk heap index exists (see HeapIndexScanDispatcher.Run). When it doesn't, this method
    // runs the same reference-counting pass directly over the live heap (or an in-memory index).
    private static LeakSignals AnalyzeObjectsPass(ClrHeap heap, IHeapAnalysisCache? cache, RetentionOptions options, ExecutionPolicy policy, IProgress<AnalyzerProgressReport>? progress)
    {
        var referenceCount = new Dictionary<ulong, int>(capacity: 4096);
        long skippedReferenceAddresses = 0;
        bool objectScanCapped = false;

        // MaxLeakScanObjects caps the number of heap.GetObject() + field-walk calls, which are
        // the primary bottleneck on multi-GB dumps (each call reads object data from the dump file).
        // 0 = unlimited. The cap applies to both disk and memory index paths.
        int maxScan = policy.MaxLeakScanObjects;
        long objectsTraced = 0;

        var scanCounter = new ObjectScanCounter("scanning heap objects", progress);

        foreach (HeapEntry entry in EnumerateLeakEntries(heap, cache))
        {
            scanCounter.Tick();

            ulong objectAddress = entry.Address;
            if (objectAddress == 0) continue;

            if (cache is not null && !cache.MethodTableHasOutgoingRefs(heap, entry.MethodTable))
                continue;

            if (maxScan > 0 && objectsTraced >= maxScan)
            {
                objectScanCapped = true;
                break;
            }

            CountIncomingReferencesByAddress(heap, objectAddress, referenceCount, policy.MaxReferenceAddresses, ref skippedReferenceAddresses);
            objectsTraced++;
        }

        scanCounter.Complete();
        progress?.Report(new(scanCounter.Scanned, "building leak signals"));

        IReadOnlyList<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = ExtractHighlyReferencedObjects(heap, referenceCount, options);
        int highlyReferencedCount = CountHighlyReferencedObjects(referenceCount, options);

        return new LeakSignals(
            highlyReferencedCount,
            skippedReferenceAddresses,
            topHighlyReferencedObjects,
            objectScanCapped);
    }

    private static IEnumerable<HeapEntry> EnumerateLeakEntries(ClrHeap heap, IHeapAnalysisCache? cache)
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

    private static int CountHighlyReferencedObjects(Dictionary<ulong, int> referenceCount, RetentionOptions options)
    {
        // OPT-#8: Replace LINQ .Count(predicate) with a plain foreach to avoid boxed IEnumerator allocation.
        int threshold = options.HighReferenceThreshold;
        int count = 0;
        foreach (KeyValuePair<ulong, int> kvp in referenceCount)
        {
            if (kvp.Value > threshold)
                count++;
        }
        return count;
    }

    private static void CountIncomingReferencesByAddress(
        ClrHeap heap,
        ulong sourceAddress,
        Dictionary<ulong, int> referenceCount,
        int maxReferenceAddresses,
        ref long skippedReferenceAddresses)
    {
        if (sourceAddress == 0)
            return;

        ClrObject sourceObject = heap.GetObject(sourceAddress);
        if (!sourceObject.IsValid)
            return;

        // FIX-1: iterator state-machine eliminated — EnumerateOutgoingReferenceAddresses was a
        // yield-based IEnumerable<ulong> that heap-allocated a new state-machine object on every
        // call (4.4 M allocations / 494 MB per pipeline run).  Logic is inlined below.
        ClrType? type = sourceObject.Type;
        if (type is null)
            return;

        if (type.IsArray)
        {
            if (type.ComponentType?.IsObjectReference == true && sourceObject.AsArray().Rank == 1)
            {
                ClrArray arr = sourceObject.AsArray();
                int len = arr.Length;
                for (int i = 0; i < len; i++)
                {
                    ClrObject element = arr.GetObjectValue(i);
                    if (element.IsValid && element.Address != 0)
                        AccumulateReference(element.Address, referenceCount, maxReferenceAddresses, ref skippedReferenceAddresses);
                }
            }
            return;
        }

        // FIX-2: indexed for loop over IReadOnlyList<ClrInstanceField> instead of foreach.
        // foreach on an interface-typed variable calls IEnumerable<T>.GetEnumerator() which routes
        // through SZArrayHelper.GetEnumerator<T>() and heap-allocates a boxed SZGenericArrayEnumerator
        // (13.7 M allocations / 439 MB per pipeline run for type.Fields alone).
        IReadOnlyList<ClrInstanceField> fields = type.Fields;
        int fieldCount = fields.Count;
        for (int fi = 0; fi < fieldCount; fi++)
        {
            ClrInstanceField field = fields[fi];
            if (!field.IsObjectReference)
                continue;

            ClrObject value = field.ReadObject(sourceObject.Address, interior: false);
            if (value.IsValid && value.Address != 0)
                AccumulateReference(value.Address, referenceCount, maxReferenceAddresses, ref skippedReferenceAddresses);
        }
    }

    // Extracted to keep CountIncomingReferencesByAddress concise; the JIT inlines this at the call sites.
    private static void AccumulateReference(
        ulong address,
        Dictionary<ulong, int> referenceCount,
        int maxReferenceAddresses,
        ref long skippedReferenceAddresses)
    {
        if (referenceCount.TryGetValue(address, out int count))
        {
            referenceCount[address] = count + 1;
        }
        else if (referenceCount.Count < maxReferenceAddresses)
        {
            referenceCount[address] = 1;
        }
        else
        {
            skippedReferenceAddresses++;
        }
    }

    private static IReadOnlyList<HighlyReferencedObjectSnapshot> ExtractHighlyReferencedObjects(ClrHeap heap, Dictionary<ulong, int> referenceCount, RetentionOptions options)
    {
        int threshold = options.HighReferenceThreshold;
        // Heuristic: for small dictionaries the LINQ-based path is faster (no heap overhead).
        const int LinqFastPathThreshold = 50_000;
        if (referenceCount.Count <= LinqFastPathThreshold)
        {
            var topAddresses = referenceCount
                .Where(kvp => kvp.Value > threshold)
                .OrderByDescending(kvp => kvp.Value)
                .Take(options.TopHighlyReferencedObjectsToShow)
                .ToArray();

            if (topAddresses.Length == 0)
                return Array.Empty<HighlyReferencedObjectSnapshot>();

            var results = new List<HighlyReferencedObjectSnapshot>(topAddresses.Length);
            foreach (var top in topAddresses)
            {
                HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(heap, top.Key, top.Value);
                if (snapshot is null)
                    continue;

                results.Add(snapshot);
            }

            return results;
        }

        // Use a fixed-size min-heap (PriorityQueue) to track top K addresses by incoming reference count for large inputs.
        var pq = new PriorityQueue<KeyValuePair<ulong, int>, int>(options.TopHighlyReferencedObjectsToShow + 1);

        foreach (KeyValuePair<ulong, int> kvp in referenceCount)
        {
            if (kvp.Value <= threshold)
                continue;

            pq.Enqueue(kvp, kvp.Value);
            if (pq.Count > options.TopHighlyReferencedObjectsToShow)
                pq.Dequeue(); // evict smallest
        }

        if (pq.Count == 0)
            return Array.Empty<HighlyReferencedObjectSnapshot>();

        // Drain pq into a list (ascending), then build snapshots and reverse to descending.
        var buffer = new List<KeyValuePair<ulong, int>>(pq.Count);
        while (pq.Count > 0)
            buffer.Add(pq.Dequeue());

        // buffer currently ascending by count; iterate reverse to produce descending order.
        var final = new List<HighlyReferencedObjectSnapshot>(buffer.Count);
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            var kvp = buffer[i];
            HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(heap, kvp.Key, kvp.Value);
            if (snapshot is null)
                continue;

            final.Add(snapshot);
        }

        return final;
    }

    private static HighlyReferencedObjectSnapshot? CreateHighlyReferencedObjectSnapshot(ClrHeap heap, ulong objectAddress, int incomingReferences)
    {
        if (objectAddress == 0)
            return null;

        ClrObject obj = heap.GetObject(objectAddress);
        if (!obj.IsValid)
            return null;

        return new HighlyReferencedObjectSnapshot(
            objectAddress,
            obj.Type?.Name ?? StringConstants.UnknownType,
            obj.Size,
            incomingReferences);
    }

    private static void PopulateRetainedBytes(ClrHeap heap, List<HighlyReferencedObjectSnapshot> objects, RetentionOptions options)
    {
        if (objects.Count == 0)
            return;

        var visited = new HashSet<ulong>(capacity: Math.Min(objects.Count * 4, 256));
        for (int i = 0; i < objects.Count; i++)
        {
            HighlyReferencedObjectSnapshot snapshot = objects[i];
            ClrObject root = heap.GetObject(snapshot.Address);
            if (!root.IsValid)
                continue;

            ulong retained = BoundedGraphWalk.ComputeExclusiveRetained(root, heap, visited, maxBreadth: options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000, maxDepth: 20);
            objects[i] = snapshot with { EstimatedRetainedBytes = retained };
        }
    }

    private static void PopulateEvidence(ClrHeap heap, IHeapAnalysisCache cache, List<HighlyReferencedObjectSnapshot> objects)
    {
        if (objects.Count == 0)
            return;

        IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);

        var provider = new ReferenceGraph(heap);
        var limits = new RootPathSearchLimits
        {
            MaxCandidateNodes = 5_000,
            MaxCandidateDepth = 8,
            MaxRootExpansionDepth = 12,
            LargeFanoutThreshold = 100,
        };
        var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false);

        for (int i = 0; i < objects.Count; i++)
        {
            HighlyReferencedObjectSnapshot snapshot = objects[i];
            bool found = finder.TryFindAnyRootPath(snapshot.Address, roots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses) : null;

            objects[i] = snapshot with
            {
                Evidence = new Evidence(
                    snapshot.EstimatedRetainedBytes,
                    rootPath,
                    searchTruncated,
                    [new EvidenceSignal("IncomingReferences", "Incoming reference count", snapshot.IncomingReferences)])
            };
        }
    }

    private static ulong SumTopHighlyReferencedBytes(IReadOnlyList<HighlyReferencedObjectSnapshot> objects)
    {
        ulong total = 0;
        for (int i = 0; i < objects.Count; i++)
            total += objects[i].Size;

        return total;
    }

    private static IReadOnlyList<RetentionTypeSnapshot> BuildTopRetentionTypes(IReadOnlyList<HighlyReferencedObjectSnapshot> objects)
    {
        if (objects.Count == 0)
            return Array.Empty<RetentionTypeSnapshot>();

        var byType = new Dictionary<string, RetentionTypeAccumulator>(StringComparer.Ordinal);
        for (int i = 0; i < objects.Count; i++)
        {
            HighlyReferencedObjectSnapshot obj = objects[i];
            if (byType.TryGetValue(obj.TypeName, out RetentionTypeAccumulator acc))
            {
                acc.ObjectCount++;
                acc.TotalBytes += obj.Size;
                acc.TotalIncomingReferences += obj.IncomingReferences;
                acc.EstimatedRetainedBytes += obj.EstimatedRetainedBytes;
                if (obj.IncomingReferences > acc.MaxIncomingReferences)
                    acc.MaxIncomingReferences = obj.IncomingReferences;
                byType[obj.TypeName] = acc;
            }
            else
            {
                byType[obj.TypeName] = new RetentionTypeAccumulator
                {
                    ObjectCount = 1,
                    TotalBytes = obj.Size,
                    TotalIncomingReferences = obj.IncomingReferences,
                    MaxIncomingReferences = obj.IncomingReferences,
                    EstimatedRetainedBytes = obj.EstimatedRetainedBytes
                };
            }
        }
        return byType
            .Select(static kvp => new RetentionTypeSnapshot(
                TypeName: kvp.Key,
                ObjectCount: kvp.Value.ObjectCount,
                TotalBytes: kvp.Value.TotalBytes,
                TotalIncomingReferences: kvp.Value.TotalIncomingReferences,
                MaxIncomingReferences: kvp.Value.MaxIncomingReferences,
                EstimatedRetainedBytes: kvp.Value.EstimatedRetainedBytes))
            .OrderByDescending(static t => t.EstimatedRetainedBytes)
            .ThenByDescending(static t => t.TotalBytes)
            .ThenByDescending(static t => t.TotalIncomingReferences)
            .ToArray();
    }

    // Like AccumulateReference, but merges a worker-partial count instead of always incrementing by 1.
    private static void MergeReferenceCount(
        ulong address,
        int addCount,
        Dictionary<ulong, int> referenceCount,
        int maxReferenceAddresses,
        ref long skippedReferenceAddresses)
    {
        if (referenceCount.TryGetValue(address, out int count))
        {
            referenceCount[address] = count + addCount;
        }
        else if (referenceCount.Count < maxReferenceAddresses)
        {
            referenceCount[address] = addCount;
        }
        else
        {
            skippedReferenceAddresses++;
        }
    }

    private readonly record struct LeakSignals(
        int HighlyReferencedObjectCount,
        long SkippedReferenceAddresses,
        IReadOnlyList<HighlyReferencedObjectSnapshot> TopHighlyReferencedObjects,
        bool ObjectScanCapped = false,
        bool ReferenceCountingSkipped = false);

    private struct RetentionTypeAccumulator
    {
        public int ObjectCount;
        public ulong TotalBytes;
        public long TotalIncomingReferences;
        public int MaxIncomingReferences;
        public ulong EstimatedRetainedBytes;
    }
}
