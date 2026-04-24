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
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers
{
    public class MemoryLeakAnalyzer : IAnalyzer
    {
        private const int TopFinalizerTypesToShow = 10;
        private const int TopDuplicateStringsToShow = 20;
        private const int TopHighlyReferencedObjectsToShow = 15;

        public string Name => "Memory Leak Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MemoryLeakOptions options = context.Options.TryGetValue(nameof(MemoryLeakOptions), out object? configured)
                && configured is MemoryLeakOptions typed
                ? typed
                : new MemoryLeakOptions();

            AnalyzerExecutionResult executionResult = Analyze(context.Heap, context.Runtime, context.Cache, options);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult Analyze(ClrHeap heap, ClrRuntime runtime, MemoryLeakOptions options)
        {
            return Analyze(heap, runtime, cache: null, options);
        }

        private AnalyzerExecutionResult Analyze(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache? cache, MemoryLeakOptions options)
        {
            var findings = new List<InsightFinding>(capacity: 4);

            FinalizerQueueResult finalizerResult = AnalyzeFinalizerQueue(heap);
            LeakSignals signals = AnalyzeObjectsPass(heap, cache, options);

            AddFindings(findings, finalizerResult.TotalCount, signals, options);

            return new AnalyzerExecutionResult(
                findings,
                new MemoryLeakDomainResult(
                    finalizerResult.TotalCount,
                    signals.DuplicateStringCount,
                    signals.DuplicateStringWastedBytes,
                    signals.TotalStrings,
                    signals.TotalStringMemoryBytes,
                    signals.UniqueStrings,
                    signals.HighlyReferencedObjectCount,
                    signals.SkippedReferenceAddresses,
                    finalizerResult.TopTypes,
                    signals.TopDuplicateStrings,
                    signals.TopHighlyReferencedObjects));
        }

        private FinalizerQueueResult AnalyzeFinalizerQueue(ClrHeap heap)
        {
            int finalizerCount = 0;
            var topTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var scanCounter = new ObjectScanCounter("Finalizer queue scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

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

        private LeakSignals AnalyzeObjectsPass(ClrHeap heap, IHeapAnalysisCache? cache, MemoryLeakOptions options)
        {
            // Pass 1: index-driven — string dedup stats only. Non-string/ref-capable addresses are NOT
            // materialized into a list anymore (OPT-#6): pass 2 re-streams from the same index source,
            // eliminating the intermediate List<ulong> (~tens of MB on large dumps) entirely.
            var stringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
            var stringMethodTables = new Dictionary<ulong, bool>(capacity: 64);
            var methodTableHasRefs = new Dictionary<ulong, bool>(capacity: 64);
            int totalStrings = 0;
            ulong totalStringMemory = 0;
            var scanCounter = new ObjectScanCounter("Memory leak index scan");

            foreach (HeapEntry entry in EnumerateLeakEntries(heap, cache))
            {
                scanCounter.Tick();

                ulong objectAddress = entry.Address;
                if (objectAddress == 0) continue;

                if (IsStringEntry(heap, entry, stringMethodTables))
                {
                    ProcessStringObjectByAddress(heap, objectAddress, entry.Size, options, stringStats, ref totalStrings, ref totalStringMemory);
                }
            }

            scanCounter.Complete();

            // Pass 2: targeted — outgoing reference enumeration on non-string objects only.
            // Streams directly from the index using the same MethodTableHasOutgoingRefs filter,
            // avoiding the intermediate address list that pass 1 previously built.
            var referenceCount = new Dictionary<ulong, int>(capacity: 4096);
            long skippedReferenceAddresses = 0;
            var refScanCounter = new ObjectScanCounter("Memory leak reference scan");

            foreach (HeapEntry entry in EnumerateLeakEntries(heap, cache))
            {
                if (entry.Address == 0) continue;
                if (IsStringEntry(heap, entry, stringMethodTables)) continue;
                if (!MethodTableHasOutgoingRefs(heap, entry.MethodTable, methodTableHasRefs)) continue;

                refScanCounter.Tick();
                CountIncomingReferencesByAddress(heap, entry.Address, referenceCount, options.MaxReferenceAddresses, ref skippedReferenceAddresses);
            }

            refScanCounter.Complete();

            DuplicateStringResult duplicateResult = ComputeDuplicateStrings(stringStats, options);
            IReadOnlyList<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = ExtractHighlyReferencedObjects(heap, referenceCount, options);
            int highlyReferencedCount = CountHighlyReferencedObjects(referenceCount, options);

            return new LeakSignals(
                duplicateResult.DuplicateCount,
                duplicateResult.TotalWastedBytes,
                totalStrings,
                totalStringMemory,
                stringStats.Count,
                highlyReferencedCount,
                skippedReferenceAddresses,
                duplicateResult.TopDuplicates,
                topHighlyReferencedObjects);
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

        private static bool IsStringEntry(ClrHeap heap, in HeapEntry entry, Dictionary<ulong, bool> stringMethodTables)
        {
            if (entry.MethodTable == 0)
                return false;

            if (stringMethodTables.TryGetValue(entry.MethodTable, out bool isString))
                return isString;

            ClrObject obj = heap.GetObject(entry.Address);
            isString = obj.IsValid && string.Equals(obj.Type?.Name, "System.String", StringComparison.Ordinal);
            stringMethodTables[entry.MethodTable] = isString;
            return isString;
        }

        private DuplicateStringResult ComputeDuplicateStrings(Dictionary<StringFingerprint, StringLeakInfo> stringStats, MemoryLeakOptions options)
        {
            // OPT-#10: Replace full O(N log N) sort+take with O(N log K) partial extraction using
            // PriorityQueue<,> as a fixed-size min-heap. On string-heavy dumps N can be hundreds of
            // thousands of unique fingerprints while K=TopDuplicateStringsToShow=20.
            int minCount = options.MinDuplicateStringCount;
            var heap = new PriorityQueue<StringLeakInfo, ulong>(TopDuplicateStringsToShow + 1);

            foreach (StringLeakInfo info in stringStats.Values)
            {
                if (info.Count <= minCount)
                    continue;

                heap.Enqueue(info, info.TotalSize);
                if (heap.Count > TopDuplicateStringsToShow)
                    heap.Dequeue(); // evict smallest
            }

            // Drain min-heap into descending list
            var duplicates = new List<StringLeakInfo>(heap.Count);
            while (heap.Count > 0)
                duplicates.Add(heap.Dequeue());
            duplicates.Reverse(); // ascending -> descending by TotalSize

            ulong totalWastedBytes = 0;
            var topDuplicates = new List<DuplicateStringSnapshot>(duplicates.Count);
            foreach (StringLeakInfo dup in duplicates)
            {
                ulong wasted = dup.TotalSize - (dup.TotalSize / (ulong)dup.Count);
                totalWastedBytes += wasted;
                topDuplicates.Add(new DuplicateStringSnapshot(dup.Preview ?? string.Empty, dup.Count, wasted));
            }

            return new DuplicateStringResult(topDuplicates.Count, totalWastedBytes, topDuplicates);
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

            if (signals.DuplicateStringCount > 0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Optimization",
                    Severity: FindingSeverity.Warning,
                    Title: "High duplicate string pressure detected",
                    Evidence: $"{signals.DuplicateStringCount:N0} duplicate string patterns with ~{FormatHelper.FormatBytes(signals.DuplicateStringWastedBytes)} estimated waste.",
                    Recommendation: "Consider string interning/pooling or de-duplicating repeated payloads.",
                    Tags: ["string", "memory", "allocation"],
                    MetricValue: signals.DuplicateStringWastedBytes,
                    MetricUnit: "wasted-bytes"));
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
        }

        private static StringFingerprint CreateStringFingerprint(string value)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            ulong hash = fnvOffset;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return new StringFingerprint(hash, value.Length, value[0], value[^1]);
        }

        private static string CreateStringPreview(string value)
        {
            string preview = value.Length > 47 ? value.Substring(0, 47) + "..." : value;
            return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static void ProcessStringObjectByAddress(
            ClrHeap heap,
            ulong objectAddress,
            ulong objectSize,
            MemoryLeakOptions options,
            Dictionary<StringFingerprint, StringLeakInfo> stringStats,
            ref int totalStrings,
            ref ulong totalStringMemory)
        {
            if (objectAddress == 0)
                return;

            totalStrings++;
            totalStringMemory += objectSize;

            // OPT-#13: Approximate string char-length from objectSize before calling heap.GetObject.
            // .NET string layout: 8 (obj header) + 8 (MT ptr) + 4 (length) + 2*N (chars) + 2 (null) ≈ 26 + 2N
            // so N ≈ (size - 26) / 2. If the estimate already exceeds MaxDuplicateStringLength we can
            // skip the heap dereference entirely for strings that would be filtered anyway.
            if (objectSize > 26)
            {
                ulong estimatedLength = (objectSize - 26) / 2;
                if (estimatedLength >= (ulong)options.MaxDuplicateStringLength)
                    return;
            }

            ClrObject stringObject = heap.GetObject(objectAddress);
            if (!stringObject.IsValid)
                return;

            string? value = stringObject.AsString();
            if (value == null || value.Length == 0 || value.Length >= options.MaxDuplicateStringLength)
                return;

            var fingerprint = CreateStringFingerprint(value);

            // OPT-#12: StringLeakInfo is now a struct. Use GetValueRefOrAddDefault for a single
            // ref-returning probe — no copy-out, no copy-back, no heap allocation per unique string.
            ref StringLeakInfo info = ref CollectionsMarshal.GetValueRefOrAddDefault(
                stringStats, fingerprint, out bool existed);

            if (!existed)
                info.Preview = CreateStringPreview(value);

            info.Count++;
            info.TotalSize += objectSize;
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

            foreach (ulong referenceAddress in EnumerateOutgoingReferenceAddresses(sourceObject))
            {
                if (referenceAddress == 0)
                    continue;

                if (referenceCount.TryGetValue(referenceAddress, out int count))
                {
                    referenceCount[referenceAddress] = count + 1;
                }
                else if (referenceCount.Count < maxReferenceAddresses)
                {
                    referenceCount[referenceAddress] = 1;
                }
                else
                {
                    skippedReferenceAddresses++;
                }
            }
        }

        private static IEnumerable<ulong> EnumerateOutgoingReferenceAddresses(ClrObject sourceObject)
        {
            ClrType? type = sourceObject.Type;
            if (type is null)
                yield break;

            // Reference-type arrays: enumerate elements directly instead of full EnumerateReferences
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
                            yield return element.Address;
                    }
                }
                yield break;
            }

            // Regular objects: iterate reference-type fields only (lazy — skips value-type-only objects)
            foreach (ClrInstanceField field in type.Fields)
            {
                if (!field.IsObjectReference)
                    continue;

                ClrObject value = field.ReadObject(sourceObject.Address, interior: false);
                if (value.IsValid && value.Address != 0)
                    yield return value.Address;
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

            foreach (ClrInstanceField field in type.Fields)
            {
                if (field.IsObjectReference)
                    return true;
            }

            return false;
        }

        private readonly record struct StringFingerprint(ulong Hash, int Length, char FirstChar, char LastChar);
        private readonly record struct DuplicateStringResult(int DuplicateCount, ulong TotalWastedBytes, IReadOnlyList<DuplicateStringSnapshot> TopDuplicates);
        private readonly record struct LeakSignals(
            int DuplicateStringCount,
            ulong DuplicateStringWastedBytes,
            int TotalStrings,
            ulong TotalStringMemoryBytes,
            int UniqueStrings,
            int HighlyReferencedObjectCount,
            long SkippedReferenceAddresses,
            IReadOnlyList<DuplicateStringSnapshot> TopDuplicateStrings,
            IReadOnlyList<HighlyReferencedObjectSnapshot> TopHighlyReferencedObjects);
        private readonly record struct FinalizerQueueResult(int TotalCount, IReadOnlyList<NameCountEntry> TopTypes);
    }
}
