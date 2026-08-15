using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class DominatorAnalyzer : IAnalyzer
{
    public string Name => "Dominator Analysis";
    public string Category => "Memory";
    public int Order => 110;

    // Fan-in (incoming-reference-count) histogram bucket boundaries (minCount inclusive, maxCount exclusive).
    private static readonly (int Min, int Max, string Label)[] s_fanInBuckets =
    [
        (0,   10,           "0 – 10"),
        (10,  50,           "10 – 50"),
        (50,  200,          "50 – 200"),
        (200, int.MaxValue, "≥ 200"),
    ];

    private static void AddToFanInHistogram(int[] fanInCounts, int incomingReferences)
    {
        for (int b = 0; b < s_fanInBuckets.Length; b++)
        {
            if (incomingReferences >= s_fanInBuckets[b].Min && incomingReferences < s_fanInBuckets[b].Max)
            {
                fanInCounts[b]++;
                return;
            }
        }
    }

    private static List<FanInBucket> BuildFanInHistogram(int[] fanInCounts)
    {
        var result = new List<FanInBucket>(s_fanInBuckets.Length);
        for (int b = 0; b < s_fanInBuckets.Length; b++)
            if (fanInCounts[b] > 0)
                result.Add(new FanInBucket(s_fanInBuckets[b].Label, fanInCounts[b]));
        return result;
    }

    /// <summary>
    /// Builds the "highly referenced object" leak signal, preferring the disk-backed
    /// reverse-edge index (built during Phase 1 — see
    /// docs/analysis/phase1-redesigns/full-reverse-index-plan.md) over a live per-object
    /// reference count. The index already recorded every object's incoming-reference count as
    /// part of the one-time Phase 1 edge extraction, so reading it here is a single sequential,
    /// allocation-light pass with no ClrMD field walks — this analyzer no longer needs to
    /// participate in <see cref="Pipeline.HeapIndexScanDispatcher"/>'s shared heap-index scan
    /// for this signal at all (it previously did, as an <c>IParallelHeapIndexScanParticipant</c>,
    /// live-counting incoming references by walking every referencing object's fields — see git
    /// history for that implementation). Falls back to <see cref="AnalyzeObjectsPass"/>'s live
    /// scan only when no reverse index is available (in-memory mode, disabled via
    /// <c>DD_SKIP_REVERSE_INDEX_BUILD=1</c>, or a pre-v4 cache.bin).
    /// </summary>
    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RetentionOptions options = context.AnalysisOptions.MemoryLeak;
        ExecutionPolicy policy = context.AnalysisOptions.ExecutionPolicy;

        IBackwardReferenceProvider? reverseIndex = context.Cache.TryGetReverseIndexProvider();
        LeakSignals signals = reverseIndex is not null
            ? BuildLeakSignalsFromReverseIndex(context.Heap, context.Cache, reverseIndex, options, context.Progress)
            : AnalyzeObjectsPass(context.Heap, context.Cache, options, policy, context.Progress);

        AnalyzerDomainResult result = Analyze(context.Heap, context.Cache, options, signals, cancellationToken).Stamp(this);

        return ValueTask.FromResult(result);
    }

    private static LeakSignals BuildLeakSignalsFromReverseIndex(
        ClrHeap heap,
        IHeapAnalysisCache cache,
        IBackwardReferenceProvider reverseIndex,
        RetentionOptions options,
        IProgress<AnalyzerProgressReport>? progress)
    {
        int threshold = options.HighReferenceThreshold;
        int topN = Math.Max(1, options.TopHighlyReferencedObjectsToShow);

        var scanCounter = new ObjectScanCounter("scanning reverse index", progress);
        int highlyReferencedCount = 0;
        var topHeap = new PriorityQueue<(ulong Address, int Count), int>(topN + 1);
        int[] fanInCounts = new int[s_fanInBuckets.Length];

        reverseIndex.EnumerateChildCounts((child, count, _) =>
        {
            scanCounter.Tick();
            AddToFanInHistogram(fanInCounts, count);

            if (count <= threshold)
                return;

            highlyReferencedCount++;

            topHeap.Enqueue((child, count), count);
            if (topHeap.Count > topN)
                topHeap.Dequeue(); // evict smallest
        });

        scanCounter.Complete();
        progress?.Report(new(scanCounter.Scanned, "building leak signals"));

        // Ascending by count out of the min-heap; reverse below for descending display order.
        var buffer = new List<(ulong Address, int Count)>(topHeap.Count);
        while (topHeap.Count > 0)
            buffer.Add(topHeap.Dequeue());

        var results = new List<HighlyReferencedObjectSnapshot>(buffer.Count);
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(heap, cache, buffer[i].Address, buffer[i].Count);
            if (snapshot is not null)
                results.Add(snapshot);
        }

        // Unlike the live-scan fallback, there's no fixed-size accumulation dictionary here
        // (no MaxReferenceAddresses cap applies), so no addresses are ever skipped; and there's
        // no per-object ClrMD work to budget via MaxLeakScanObjects, since the whole pass is
        // sequential index reads, not live heap resolution — so the scan is exhaustive over
        // every recorded child, by construction, never capped.
        return new LeakSignals(highlyReferencedCount, SkippedReferenceAddresses: 0, results, ObjectScanCapped: false, FanInHistogram: BuildFanInHistogram(fanInCounts));
    }

    private static DominatorDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache cache,
        RetentionOptions options,
        LeakSignals signals,
        CancellationToken cancellationToken)
    {
        Dictionary<string, CachedTypeStatistics> typeStats = cache.GetOrBuildTypeStatistics(heap);

        // Calculate total heap size from all types
        ulong totalHeapBytes = 0;
        foreach (var stat in typeStats.Values)
        {
            totalHeapBytes += stat.TotalSize;
        }

        if (typeStats.Count == 0)
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>(), MaxTopDominatorTypesToShow: options.TopHighlyReferencedObjectsToShow, TotalHeapBytes: totalHeapBytes, FanInHistogram: signals.FanInHistogram);

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
        PopulateRetainedBytes(heap, cache, topHighlyReferencedObjects, options);
        PopulateEvidence(heap, cache, topHighlyReferencedObjects, options);
        IReadOnlyList<RetentionTypeSnapshot> topRetentionTypes = BuildTopRetentionTypes(topHighlyReferencedObjects);
        ulong topHighlyReferencedTotalBytes = SumTopHighlyReferencedBytes(topHighlyReferencedObjects);

        if (candidates.Count == 0)
        {
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>())
            {
                MaxBreadth = options.MaxLeakScanObjects,
                MaxDepth = 20,
                HighlyReferencedObjectCount = signals.HighlyReferencedObjectCount,
                SkippedReferenceAddresses = signals.SkippedReferenceAddresses,
                TopHighlyReferencedObjects = topHighlyReferencedObjects,
                ObjectScanCapped = signals.ObjectScanCapped,
                TopRetentionTypes = topRetentionTypes,
                TopHighlyReferencedTotalBytes = topHighlyReferencedTotalBytes,
                MaxTopDominatorTypesToShow = options.TopHighlyReferencedObjectsToShow,
                TotalHeapBytes = totalHeapBytes,
                FanInHistogram = signals.FanInHistogram
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

        int maxBreadth = options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000;
        const int MaxDepth = 20;

        // Use a shared visited set for all top-K types to produce exclusive (non-overlapping) retained-size semantics,
        // matching the semantics of PopulateRetainedBytes. This ensures the two retained-byte metrics are comparable.
        var visited = new HashSet<ulong>(capacity: Math.Min(topCount * 256, 4096));

        var walkCandidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>(topCount);
        for (int i = 0; i < topCount; i++)
        {
            ulong sampleAddress = candidates[i].SampleAddress;
            ClrObject root = heap.GetObject(sampleAddress);
            if (!root.IsValid || root.Type is null)
                continue;

            // ShallowSize here is the single sample object's own size (what a skipped-walk result
            // would fall back to), not the type-aggregate TotalSize — this walk estimates one
            // sample instance's reachable graph, not the whole type's footprint.
            walkCandidates.Add((sampleAddress, root.Type.MethodTable, root.Size));
        }

        IReadOnlyList<RetainedSizeResult> retainedResults = RetainedSizeCandidateSelector.SelectAndCompute(
            walkCandidates, heap, cache, visited, maxCandidatesToWalk: topCount, maxBreadth, MaxDepth, cancellationToken);

        var retainedByAddress = new Dictionary<ulong, ulong>(retainedResults.Count);
        foreach (RetainedSizeResult r in retainedResults)
            retainedByAddress[r.Address] = r.RetainedSize;

        ulong totalEstimatedRetainedBytes = 0;
        for (int i = 0; i < topCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string typeName, ulong sampleAddress, int count, ulong totalSize, ulong lohSize, long gen2Count, _) = candidates[i];
            if (!retainedByAddress.TryGetValue(sampleAddress, out ulong retainedBytes))
                continue;

            totalEstimatedRetainedBytes += retainedBytes;

            ulong averageSize = count > 0 ? totalSize / (ulong)count : 0;
            // TODO: Track WasCapped from RetainedSizeCandidateSelector when it exceeds breadth/depth limits.
            // For now, always false — future enhancement to propagate cap indicator from BFS traversal.
            topTypes.Add(new TypeSnapshot(
                typeName,
                count,
                totalSize,
                lohSize,
                AverageSize: averageSize,
                EstimatedRetainedBytes: retainedBytes,
                SampleAddress: sampleAddress,
                Gen2Count: gen2Count,
                WasCapped: false));
        }

        topTypes.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        return new DominatorDomainResult(
            candidates.Count,
            topTypes.Count,
            totalEstimatedRetainedBytes,
            topTypes,
            MaxBreadth: maxBreadth,
            MaxDepth: MaxDepth,
            HighlyReferencedObjectCount: signals.HighlyReferencedObjectCount,
            SkippedReferenceAddresses: signals.SkippedReferenceAddresses,
            TopHighlyReferencedObjects: topHighlyReferencedObjects,
            ObjectScanCapped: signals.ObjectScanCapped,
            TopRetentionTypes: topRetentionTypes,
            TopHighlyReferencedTotalBytes: topHighlyReferencedTotalBytes,
            MaxTopDominatorTypesToShow: options.TopHighlyReferencedObjectsToShow,
            TotalHeapBytes: totalHeapBytes,
            FanInHistogram: signals.FanInHistogram);
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

        IReadOnlyList<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = ExtractHighlyReferencedObjects(heap, cache, referenceCount, options);
        int highlyReferencedCount = CountHighlyReferencedObjects(referenceCount, options, out List<FanInBucket> fanInHistogram);

        return new LeakSignals(
            highlyReferencedCount,
            skippedReferenceAddresses,
            topHighlyReferencedObjects,
            objectScanCapped,
            FanInHistogram: fanInHistogram);
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

    private static int CountHighlyReferencedObjects(Dictionary<ulong, int> referenceCount, RetentionOptions options, out List<FanInBucket> fanInHistogram)
    {
        // OPT-#8: Replace LINQ .Count(predicate) with a plain foreach to avoid boxed IEnumerator allocation.
        // Fan-in histogram is built in the same pass to avoid a second full-dictionary iteration.
        int threshold = options.HighReferenceThreshold;
        int count = 0;
        int[] fanInCounts = new int[s_fanInBuckets.Length];
        foreach (KeyValuePair<ulong, int> kvp in referenceCount)
        {
            if (kvp.Value > threshold)
                count++;
            AddToFanInHistogram(fanInCounts, kvp.Value);
        }
        fanInHistogram = BuildFanInHistogram(fanInCounts);
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

    private static IReadOnlyList<HighlyReferencedObjectSnapshot> ExtractHighlyReferencedObjects(ClrHeap heap, IHeapAnalysisCache? cache, Dictionary<ulong, int> referenceCount, RetentionOptions options)
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
                HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(heap, cache, top.Key, top.Value);
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
            HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(heap, cache, kvp.Key, kvp.Value);
            if (snapshot is null)
                continue;

            final.Add(snapshot);
        }

        return final;
    }

    private static HighlyReferencedObjectSnapshot? CreateHighlyReferencedObjectSnapshot(ClrHeap heap, IHeapAnalysisCache? cache, ulong objectAddress, int incomingReferences)
    {
        if (objectAddress == 0)
            return null;

        // OPT (docs/cache/cache-architecture.md Phase 6): objectAddress comes from the
        // reverse-index-driven incoming-reference count, not a live traversal — when a cache is
        // available, delegate to it fully (it already handles disk-vs-in-memory internally per its
        // own contract) rather than adding a second heap.GetObject fallback here, which would
        // reintroduce the redundant-resolution pattern this index exists to remove.
        if (cache is not null)
        {
            if (!cache.TryGetObjectMetadata(heap, objectAddress, out ulong methodTable, out ulong size))
                return null;

            string typeName = heap.GetTypeByMethodTable(methodTable)?.Name ?? StringConstants.UnknownType;
            return new HighlyReferencedObjectSnapshot(objectAddress, typeName, size, incomingReferences);
        }

        ClrObject obj = heap.GetObject(objectAddress);
        if (!obj.IsValid)
            return null;

        return new HighlyReferencedObjectSnapshot(
            objectAddress,
            obj.Type?.Name ?? StringConstants.UnknownType,
            obj.Size,
            incomingReferences);
    }

    private static void PopulateRetainedBytes(ClrHeap heap, IHeapAnalysisCache cache, List<HighlyReferencedObjectSnapshot> objects, RetentionOptions options)
    {
        if (objects.Count == 0)
            return;

        var candidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>(objects.Count);
        foreach (HighlyReferencedObjectSnapshot snapshot in objects)
        {
            ClrObject root = heap.GetObject(snapshot.Address);
            if (root.IsValid && root.Type is not null)
                candidates.Add((snapshot.Address, root.Type.MethodTable, snapshot.Size));
        }

        var visited = new HashSet<ulong>(capacity: Math.Min(objects.Count * 4, 256));
        int maxBreadth = options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000;
        IReadOnlyList<RetainedSizeResult> results = RetainedSizeCandidateSelector.SelectAndCompute(
            candidates, heap, cache, visited, maxCandidatesToWalk: candidates.Count, maxBreadth, maxDepth: 20);

        var retainedByAddress = new Dictionary<ulong, ulong>(results.Count);
        foreach (RetainedSizeResult r in results)
            retainedByAddress[r.Address] = r.RetainedSize;

        for (int i = 0; i < objects.Count; i++)
        {
            if (retainedByAddress.TryGetValue(objects[i].Address, out ulong retained))
                objects[i] = objects[i] with { EstimatedRetainedBytes = retained };
        }
    }

    private static void PopulateEvidence(ClrHeap heap, IHeapAnalysisCache cache, List<HighlyReferencedObjectSnapshot> objects, RetentionOptions options)
    {
        if (objects.Count == 0)
            return;

        IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);

        var provider = new ReferenceGraph(heap);
        var limits = new RootPathSearchLimits
        {
            MaxCandidateNodes = options.MaxRootPathCandidateNodes,
            MaxCandidateDepth = options.MaxRootPathCandidateDepth,
            MaxRootExpansionDepth = options.MaxRootPathExpansionDepth,
            LargeFanoutThreshold = options.RootPathLargeFanoutThreshold,
        };
        var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false, cache.TryGetReverseIndexProvider(), cache);

        for (int i = 0; i < objects.Count; i++)
        {
            HighlyReferencedObjectSnapshot snapshot = objects[i];
            bool found = finder.TryFindAnyRootPath(snapshot.Address, roots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses, cache) : null;

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

    private readonly record struct LeakSignals(
        int HighlyReferencedObjectCount,
        long SkippedReferenceAddresses,
        IReadOnlyList<HighlyReferencedObjectSnapshot> TopHighlyReferencedObjects,
        bool ObjectScanCapped = false,
        bool ReferenceCountingSkipped = false,
        IReadOnlyList<FanInBucket>? FanInHistogram = null);

    private struct RetentionTypeAccumulator
    {
        public int ObjectCount;
        public ulong TotalBytes;
        public long TotalIncomingReferences;
        public int MaxIncomingReferences;
        public ulong EstimatedRetainedBytes;
    }
}
