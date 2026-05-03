using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Options;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class MemoryLeakAnalyzer : IAnalyzer
    {
        private const int TopFinalizerTypesToShow = 10;
        private const int TopHighlyReferencedObjectsToShow = 15;

        public string Name => "Memory Leak Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MemoryLeakOptions options = context.GetOption<MemoryLeakOptions>();

            return ValueTask.FromResult(Analyze(context.Heap, context.Runtime, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, ClrRuntime runtime, MemoryLeakOptions options)
        {
            return Analyze(heap, runtime, cache: null, options, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache? cache, MemoryLeakOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            FinalizerQueueResult finalizerResult = AnalyzeFinalizerQueue(heap, progress);
            LeakSignals signals = AnalyzeObjectsPass(heap, cache, options, progress);

            return new MemoryLeakDomainResult(
                    finalizerResult.TotalCount,
                    signals.HighlyReferencedObjectCount,
                    signals.SkippedReferenceAddresses,
                    finalizerResult.TopTypes,
                    signals.TopHighlyReferencedObjects,
                    signals.ObjectScanCapped);
        }

        private FinalizerQueueResult AnalyzeFinalizerQueue(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress)
        {
            int finalizerCount = 0;
            var topTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var scanCounter = new ObjectScanCounter("scanning finalizer queue", progress, reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (var obj in heap.EnumerateFinalizableObjects())
            {
                scanCounter.Tick();
                finalizerCount++;

                string typeName = obj.Type?.Name ?? StringConstants.UnknownType;
                topTypes.TryGetValue(typeName, out int count);
                topTypes[typeName] = count + 1;
            }

            scanCounter.Complete();

            return new FinalizerQueueResult(
                finalizerCount,
                topTypes
                    .OrderByDescending(k => k.Value)
                    .Take(TopFinalizerTypesToShow)
                    .Select(k => new NameCountEntry(k.Key, k.Value))
                    .ToList());
        }

        private LeakSignals AnalyzeObjectsPass(ClrHeap heap, IHeapAnalysisCache? cache, MemoryLeakOptions options, IProgress<AnalyzerProgressReport>? progress)
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
            int maxScan = options.MaxLeakScanObjects;
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

                    CountIncomingReferencesByAddress(heap, objectAddress, referenceCount, options.MaxReferenceAddresses, ref skippedReferenceAddresses);
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

                    CountIncomingReferencesByAddress(heap, objectAddress, referenceCount, options.MaxReferenceAddresses, ref skippedReferenceAddresses);
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

        private static int CountHighlyReferencedObjects(Dictionary<ulong, int> referenceCount, MemoryLeakOptions options)
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

        private void AddFindings(List<InsightFinding> findings, int finalizerCount, LeakSignals signals, MemoryLeakOptions options)
        {
            if (finalizerCount >= 1000)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Leak",
                    Severity: FindingSeverity.Critical,
                    Title: "Finalizer queue backlog is very high",
                    Evidence: $"{finalizerCount:N0} objects are waiting for finalization.",
                    Recommendation: "Investigate finalizers and implement IDisposable/using patterns to reduce finalizer pressure.",
                    Tags: ["finalizer", "memory-leak", "gc"],
                    MetricValue: finalizerCount,
                    MetricUnit: "finalizer-objects"));
            }
            else if (finalizerCount > 0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Leak",
                    Severity: FindingSeverity.Warning,
                    Title: "Finalizer queue contains pending objects",
                    Evidence: $"{finalizerCount:N0} objects are waiting for finalization.",
                    Recommendation: "Review top finalizable types and avoid unnecessary finalizers.",
                    Tags: ["finalizer", "memory"],
                    MetricValue: finalizerCount,
                    MetricUnit: "finalizer-objects"));
            }

            if (signals.HighlyReferencedObjectCount > 0)
            {
                var severity = signals.HighlyReferencedObjectCount >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning;
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Leak",
                    Severity: severity,
                    Title: "Highly referenced objects detected",
                    Evidence: $"{signals.HighlyReferencedObjectCount:N0} objects exceeded {options.HighReferenceThreshold:N0} incoming references.",
                    Recommendation: "Inspect root paths and long-lived graphs retaining these objects.",
                    Tags: ["retention", "references", "memory-leak"],
                    MetricValue: signals.HighlyReferencedObjectCount,
                    MetricUnit: "objects"));
            }

            if (signals.SkippedReferenceAddresses > 0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Diagnostics",
                    Severity: FindingSeverity.Info,
                    Title: "Reference tracking was capped",
                    Evidence: $"Skipped {signals.SkippedReferenceAddresses:N0} references after hitting {options.MaxReferenceAddresses:N0} tracked addresses.",
                    Recommendation: "Increase MaxReferenceAddressesToTrack for deeper incoming-reference coverage.",
                    Tags: ["analysis-quality", "references"],
                    MetricValue: signals.SkippedReferenceAddresses,
                    MetricUnit: "references"));
            }

            if (signals.ObjectScanCapped)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Diagnostics",
                    Severity: FindingSeverity.Info,
                    Title: "Leak scan was limited by object count cap",
                    Evidence: $"Reference-field enumeration stopped after {options.MaxLeakScanObjects:N0} objects. Highly-referenced-object results may be incomplete.",
                    Recommendation: "Set MaxLeakScanObjects = 0 in options to disable the cap (use only on small dumps).",
                    Tags: ["analysis-quality", "scan-cap"],
                    MetricValue: options.MaxLeakScanObjects,
                    MetricUnit: "objects"));
            }
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

        private IReadOnlyList<HighlyReferencedObjectSnapshot> ExtractHighlyReferencedObjects(ClrHeap heap, Dictionary<ulong, int> referenceCount, MemoryLeakOptions options)
        {
            int threshold = options.HighReferenceThreshold;
            // Heuristic: for small dictionaries the LINQ-based path is faster (no heap overhead).
            const int LinqFastPathThreshold = 50_000;
            if (referenceCount.Count <= LinqFastPathThreshold)
            {
                var topAddresses = referenceCount
                    .Where(kvp => kvp.Value > threshold)
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(TopHighlyReferencedObjectsToShow)
                    .ToList();

                if (topAddresses.Count == 0)
                    return Array.Empty<HighlyReferencedObjectSnapshot>();

                var results = new List<HighlyReferencedObjectSnapshot>(topAddresses.Count);
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
            var pq = new PriorityQueue<KeyValuePair<ulong, int>, int>(TopHighlyReferencedObjectsToShow + 1);

            foreach (KeyValuePair<ulong, int> kvp in referenceCount)
            {
                if (kvp.Value <= threshold)
                    continue;

                pq.Enqueue(kvp, kvp.Value);
                if (pq.Count > TopHighlyReferencedObjectsToShow)
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
        private readonly record struct FinalizerQueueResult(int TotalCount, IReadOnlyList<NameCountEntry> TopTypes);
        
        public void Dispose() { }
    }
}
