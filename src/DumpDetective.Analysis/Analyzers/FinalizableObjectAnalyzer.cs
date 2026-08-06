using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §21.1 (finalizable object population) and §21.2
    /// (finalizer queue sub-graph retention and undisposed detection).
    ///
    /// Population sweep (§21.1) uses <c>TypeAggregates</c> from Phase 1 filtered by
    /// <see cref="TypeAggregateFlags.IsFinalizableType"/> — no full heap re-scan.
    ///
    /// Queue analysis (§21.2) calls <c>heap.EnumerateFinalizableObjects()</c>, bounded by
    /// configured options, with bounded BFS for the top entries only.
    /// </summary>
    public sealed class FinalizableObjectAnalyzer : IAnalyzer
    {
        public string Name => "Finalizable Object Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizableObjectAnalysisOptions options = context.AnalysisOptions.FinalizableObjectAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            FinalizableObjectAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            // ── Step 1: Population from TypeAggregates (Phase 1 index) ────────
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
                typeAggregates = idx.TypeAggregates;

            long totalObjects = 0;
            ulong totalBytes = 0;
            long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0;

            var finalizableTypes = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>();
            var fallbackTypeNames = new Dictionary<ulong, string>();  // For fallback path type name caching

            if (typeAggregates is not null)
            {
                foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
                {
                    TypeAggregateIndexEntry e = kv.Value;
                    if ((e.Flags & TypeAggregateFlags.IsFinalizableType) == 0)
                        continue;

                    finalizableTypes.Add((kv.Key, e));
                    totalObjects += e.Count;
                    totalBytes += e.TotalSize;
                    gen0 += e.Gen0Count;
                    gen1 += e.Gen1Count;
                    gen2 += e.Gen2Count;
                    loh += e.LohCount;
                }
            }
            else
            {
                // Fallback: scan heap directly (only used when no Phase 1 index is available)
                // Build per-type statistics to match the Phase 1 index path output.
                var typeStats = new Dictionary<ulong, (string Name, ulong Mt, long Count, ulong Bytes, long Gen0, long Gen1, long Gen2, long Loh)>();

                foreach (ClrObject obj in heap.EnumerateObjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!obj.IsValid || obj.Type is null || !obj.Type.IsFinalizable)
                        continue;

                    totalObjects++;
                    totalBytes += obj.Size;

                    int g = SegmentKindMapper.ResolveGeneration(heap, obj.Address);
                    if (g == 0) gen0++;
                    else if (g == 1) gen1++;
                    else if (g == 2) gen2++;
                    else if (g >= 3) loh++;  // LOH is typically reported as Gen3 or higher

                    // Accumulate per-type stats
                    ulong mt = obj.Type.MethodTable;
                    string typeName = obj.Type.Name ?? "<unknown>";
                    if (!typeStats.TryGetValue(mt, out var stat))
                    {
                        stat = (typeName, mt, 0, 0, 0, 0, 0, 0);
                        fallbackTypeNames[mt] = typeName;
                    }

                    stat.Count++;
                    stat.Bytes += obj.Size;
                    if (g == 0) stat.Gen0++;
                    else if (g == 1) stat.Gen1++;
                    else if (g == 2) stat.Gen2++;
                    else if (g >= 3) stat.Loh++;

                    typeStats[mt] = stat;
                }

                // Convert per-type stats to TypeAggregateIndexEntry equivalents
                foreach (var (mt, (typeName, _, count, bytes, g0, g1, g2, lohCount)) in typeStats)
                {
                    var entry = new TypeAggregateIndexEntry(
                        MethodTable: mt,
                        ModuleId: 0,  // Module ID not available in fallback path
                        Count: count,
                        TotalSize: bytes,
                        LohCount: lohCount,
                        LohSize: lohCount > 0 ? bytes : 0,  // Assume LOH objects contribute to TotalSize
                        SampleAddress: 0,  // No sample address available in fallback path
                        Gen0Count: g0,
                        Gen1Count: g1,
                        Gen2Count: g2,
                        Flags: TypeAggregateFlags.IsFinalizableType);
                    finalizableTypes.Add((mt, entry));
                }
            }

            // ── Step 2: Top finalizable types by Gen2Count ─────────────────────
            finalizableTypes.Sort(static (a, b) => b.Entry.Gen2Count.CompareTo(a.Entry.Gen2Count));
            int typeLimit = Math.Min(finalizableTypes.Count, options.TopTypeLimit);
            var topTypesByGen2 = new List<TypeGenerationProfile>(typeLimit);
            for (int i = 0; i < typeLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ulong mt, TypeAggregateIndexEntry e) = finalizableTypes[i];
                // Use cached type name from fallback path if available; otherwise resolve from sample address
                string typeName = fallbackTypeNames.TryGetValue(mt, out var cached)
                    ? cached
                    : TypeAggregateNameResolver.ResolveTypeName(heap, mt, e.SampleAddress);
                topTypesByGen2.Add(new TypeGenerationProfile(
                    TypeName: typeName,
                    Gen0Count: e.Gen0Count,
                    Gen1Count: e.Gen1Count,
                    Gen2Count: e.Gen2Count,
                    LohCount: (int)Math.Min(e.LohCount, int.MaxValue),
                    TotalBytes: e.TotalSize,
                    IsFinalizable: true));
            }

            // ── Step 3: Finalizer queue analysis ─────────────────────────────
            int queueCount = 0;
            var queueSamples = new List<(ulong Addr, string TypeName, ulong ShallowSize)>(Math.Min(options.QueueScanLimit, 128));
            var queueTypeCountMap = new Dictionary<string, int>();

            foreach (ClrObject obj in heap.EnumerateFinalizableObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                queueCount++;

                string typeName = obj.IsValid && obj.Type is not null ? (obj.Type.Name ?? "<unknown>") : "<unknown>";
                if (!queueTypeCountMap.ContainsKey(typeName))
                    queueTypeCountMap[typeName] = 0;
                queueTypeCountMap[typeName]++;

                if (queueSamples.Count < options.QueueScanLimit && obj.IsValid && obj.Type is not null)
                    queueSamples.Add((obj.Address, typeName, obj.Size));
            }

            // Build top queue types by count
            var topQueueTypes = queueTypeCountMap
                .OrderByDescending(x => x.Value)
                .Take(Math.Min(queueTypeCountMap.Count, options.TopTypeLimit))
                .Select(x => new QueueTypeStatistic(x.Key, x.Value))
                .ToList();

            // Sort by shallow size descending, analyse top N
            queueSamples.Sort(static (a, b) => b.ShallowSize.CompareTo(a.ShallowSize));

            int entryLimit = Math.Min(queueSamples.Count, options.TopQueueEntries);
            var topEntries = new List<FinalizerQueueEntry>(entryLimit);
            ulong totalQueueRetained = 0;
            bool hasUndisposedDisposable = false;
            bool isRetainedEstimatePartial = false;

            var isDisposableCache = new Dictionary<ulong, bool>();
            var disposedFieldCache = new Dictionary<ulong, ClrInstanceField?>();

            for (int i = 0; i < entryLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ulong addr, string typeName, ulong shallowSize) = queueSamples[i];

                ClrObject obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null)
                    continue;

                ulong mt = obj.Type.MethodTable;
                bool isDisposable = IsDisposableType(obj.Type, isDisposableCache, mt);
                bool disposedFound = false;
                bool disposedValue = false;

                ClrInstanceField? disposedField = FindDisposedField(obj.Type, disposedFieldCache, mt);
                if (disposedField is not null)
                {
                    disposedFound = true;
                    try { disposedValue = disposedField.Read<bool>(obj, interior: false); }
                    catch { /* field unreadable */ }
                }

                if (isDisposable && disposedFound && !disposedValue)
                    hasUndisposedDisposable = true;

                (ulong retained, bool wasCapped) = BfsEstimateRetained(heap, addr, options.MaxBfsNodes, options.MaxBfsDepth);
                if (wasCapped)
                    isRetainedEstimatePartial = true;
                totalQueueRetained += retained;

                topEntries.Add(new FinalizerQueueEntry(
                    Address: addr,
                    TypeName: typeName,
                    ShallowSize: shallowSize,
                    EstimatedRetainedBytes: retained,
                    IsDisposableType: isDisposable,
                    DisposedFieldFound: disposedFound,
                    DisposedFieldValue: disposedValue));
            }

            // Sort by estimated retained size descending
            topEntries.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

            return new FinalizableObjectDomainResult(
                TotalFinalizableObjects: (int)Math.Min(totalObjects, int.MaxValue),
                TotalFinalizableBytes: totalBytes,
                Gen0Count: gen0,
                Gen1Count: gen1,
                Gen2Count: gen2,
                LohCount: loh,
                FinalizerQueueCount: queueCount,
                FinalizerQueueRetainedBytes: totalQueueRetained,
                IsRetainedEstimatePartial: isRetainedEstimatePartial,
                HasUndisposedDisposableInQueue: hasUndisposedDisposable,
                TopFinalizableTypesByGen2Count: topTypesByGen2,
                TopQueueTypesByCount: topQueueTypes,
                TopQueueEntriesByRetainedSize: topEntries);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsDisposableType(ClrType type, Dictionary<ulong, bool> cache, ulong methodTable)
        {
            if (cache.TryGetValue(methodTable, out bool result))
                return result;

            bool isDisposable = false;
            foreach (ClrInterface iface in type.EnumerateInterfaces())
            {
                if (iface.Name is "System.IDisposable")
                {
                    isDisposable = true;
                    break;
                }
            }

            cache[methodTable] = isDisposable;
            return isDisposable;
        }

        private static ClrInstanceField? FindDisposedField(ClrType type, Dictionary<ulong, ClrInstanceField?> cache, ulong methodTable)
        {
            if (cache.TryGetValue(methodTable, out ClrInstanceField? cached))
                return cached;

            ClrInstanceField? field = null;
            foreach (ClrInstanceField f in type.Fields)
            {
                string? name = f.Name;
                if (name is "_disposed" or "disposed" or "m_disposed" or "_isDisposed" or "isDisposed")
                {
                    field = f;
                    break;
                }
            }

            cache[methodTable] = field;
            return field;
        }

        /// <summary>
        /// Bounded BFS from <paramref name="startAddr"/>; returns the sum of sizes of all
        /// reachable objects (including the start object) and a flag indicating if traversal
        /// was capped by node limit or depth. Capped at <paramref name="maxNodes"/> nodes
        /// and <paramref name="maxDepth"/> depth. When capped, returned size is a partial
        /// estimate of the true sub-graph (lower bound).
        /// </summary>
        private static (ulong RetainedSize, bool WasCapped) BfsEstimateRetained(ClrHeap heap, ulong startAddr, int maxNodes, int maxDepth)
        {
            if (startAddr == 0)
                return (0, false);

            var visited = new HashSet<ulong>(capacity: 32) { startAddr };
            var queue = new Queue<(ulong Addr, int Depth)>(capacity: 32);
            queue.Enqueue((startAddr, 0));
            ulong totalSize = 0;
            int nodesSeen = 0;
            bool wasCapped = false;

            while (queue.Count > 0)
            {
                (ulong addr, int depth) = queue.Dequeue();
                nodesSeen++;

                if (nodesSeen > maxNodes || depth >= maxDepth)
                {
                    wasCapped = true;
                    break;
                }

                ClrObject obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null)
                    continue;

                totalSize += obj.Size;

                foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
                {
                    if (child.IsValid && child.Address != 0 && visited.Add(child.Address))
                        queue.Enqueue((child.Address, depth + 1));
                }
            }

            return (totalSize, wasCapped);
        }

        public void Dispose() { }
    }
}
