using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Options;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class RetentionAnalyzer : IAnalyzer
    {
        public string Name => "Retention Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

                    RetentionOptions options = context.AnalysisOptions.MemoryLeak;
                    ExecutionPolicy policy = context.AnalysisOptions.ExecutionPolicy;

            return ValueTask.FromResult(Analyze(context.Heap, context.Runtime, context.Cache, options, policy, context.Progress).Stamp(this));
        }

        internal AnalyzerDomainResult Analyze(ClrHeap heap, ClrRuntime runtime, RetentionOptions options)
        {
            return Analyze(heap, runtime, cache: null, options, ExecutionPolicy.Default, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache? cache, RetentionOptions options, ExecutionPolicy policy, IProgress<AnalyzerProgressReport>? progress)
        {
            // Finalizer queue analysis has been moved to FinalizableObjectAnalyzer.
            // Keep RetentionAnalyzer focused on incoming-reference retention signals only.
            LeakSignals signals = AnalyzeObjectsPass(heap, cache, options, policy, progress);
            List<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = signals.TopHighlyReferencedObjects as List<HighlyReferencedObjectSnapshot>
                ?? new List<HighlyReferencedObjectSnapshot>(signals.TopHighlyReferencedObjects);
            PopulateRetainedBytes(heap, topHighlyReferencedObjects, options);
            IReadOnlyList<RetentionTypeSnapshot> topRetentionTypes = BuildTopRetentionTypes(topHighlyReferencedObjects);
            ulong topHighlyReferencedTotalBytes = SumTopHighlyReferencedBytes(topHighlyReferencedObjects);

            return new RetentionDomainResult(
                    FinalizerQueueCount: 0,
                    HighlyReferencedObjectCount: signals.HighlyReferencedObjectCount,
                    SkippedReferenceAddresses: signals.SkippedReferenceAddresses,
                    TopFinalizerTypes: null,
                    TopHighlyReferencedObjects: topHighlyReferencedObjects,
                ObjectScanCapped: signals.ObjectScanCapped,
                TopRetentionTypes: topRetentionTypes,
                TopHighlyReferencedTotalBytes: topHighlyReferencedTotalBytes);
        }

        // Finalizer queue analysis removed — handled by FinalizableObjectAnalyzer.

        private LeakSignals AnalyzeObjectsPass(ClrHeap heap, IHeapAnalysisCache? cache, RetentionOptions options, ExecutionPolicy policy, IProgress<AnalyzerProgressReport>? progress)
        {
            // Single-pass: enumerate the index (or heap) once, counting incoming references.
            // String analysis is handled by StringAnalyzer.
            Dictionary<ulong, bool>? methodTableHasRefs = cache is not null
                ? null
                : new Dictionary<ulong, bool>(capacity: 64);

            var referenceCount = new Dictionary<ulong, int>(capacity: 4096);
            long skippedReferenceAddresses = 0;
            bool objectScanCapped = false;

            // MaxLeakScanObjects caps the number of heap.GetObject() + field-walk calls, which are
            // the primary bottleneck on multi-GB dumps (each call reads object data from the dump file).
            // 0 = unlimited. The cap applies to both disk and memory index paths.
            int maxScan = policy.MaxLeakScanObjects;
            long objectsTraced = 0;

            var scanCounter = new ObjectScanCounter("scanning heap objects", progress);

            // Use heap index tuples when available for slightly cheaper enumeration path.
            if (cache is HeapAnalysisCache concreteCache && concreteCache.TryGetHeapIndex(out _))
            {
                foreach (var tuple in concreteCache.EnumerateIndexedEntriesAsTuples())
                {
                    scanCounter.Tick();

                    ulong objectAddress = tuple.Address;
                    if (objectAddress == 0) continue;

                    bool hasRefs = cache.MethodTableHasOutgoingRefs(heap, tuple.MethodTable);
                    if (!hasRefs)
                        continue;

                    if (maxScan > 0 && objectsTraced >= maxScan)
                    {
                        objectScanCapped = true;
                        break;
                    }

                    CountIncomingReferencesByAddress(heap, objectAddress, referenceCount, policy.MaxReferenceAddresses, ref skippedReferenceAddresses);
                    objectsTraced++;
                }
            }
            else
            {
                foreach (HeapEntry entry in EnumerateLeakEntries(heap, cache))
                {
                    scanCounter.Tick();

                    ulong objectAddress = entry.Address;
                    if (objectAddress == 0) continue;

                    if (cache is not null)
                    {
                        if (!cache.MethodTableHasOutgoingRefs(heap, entry.MethodTable))
                            continue;
                    }
                    else
                    {
                        if (!MethodTableHasOutgoingRefs(heap, entry.MethodTable, methodTableHasRefs!))
                            continue;
                    }

                    if (maxScan > 0 && objectsTraced >= maxScan)
                    {
                        objectScanCapped = true;
                        break;
                    }

                    CountIncomingReferencesByAddress(heap, objectAddress, referenceCount, policy.MaxReferenceAddresses, ref skippedReferenceAddresses);
                    objectsTraced++;
                }
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

        private IReadOnlyList<HighlyReferencedObjectSnapshot> ExtractHighlyReferencedObjects(ClrHeap heap, Dictionary<ulong, int> referenceCount, RetentionOptions options)
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

                ulong retained = BoundedRetainedSizeBfs.ComputeExclusiveRetained(root, heap, visited, maxBreadth: options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000, maxDepth: 20);
                objects[i] = snapshot with { EstimatedRetainedBytes = retained };
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

        private static bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable, Dictionary<ulong, bool> cache)
        {
            if (methodTable == 0)
                return false;

            if (cache.TryGetValue(methodTable, out bool cached))
                return cached;

            bool result = TypeHasOutgoingRefs(heap.GetTypeByMethodTable(methodTable));
            cache[methodTable] = result;
            return result;
        }

        private static bool TypeHasOutgoingRefs(ClrType? type)
        {
            if (type is null)
                return false;

            if (type.IsArray)
                return type.ComponentType?.IsObjectReference == true;

            // FIX-2: indexed for loop — same SZGenericArrayEnumerator boxing fix as in CountIncomingReferencesByAddress.
            IReadOnlyList<ClrInstanceField> fields = type.Fields;
            int count = fields.Count;
            for (int i = 0; i < count; i++)
            {
                if (fields[i].IsObjectReference)
                    return true;
            }

            return false;
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

        public void Dispose() { }
    }
}
