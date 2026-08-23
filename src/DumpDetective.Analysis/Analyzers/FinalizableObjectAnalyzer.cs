using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

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
    /// Queue analysis (§21.2) calls <c>heap.EnumerateFinalizableObjects()</c> exhaustively (no
    /// row cap); per-entry retained bytes come from the exact dominator tree
    /// (<see cref="IDominatorTreeProvider.TryGetRetainedBytes"/>) when available, falling back
    /// to shallow size otherwise — no bounded BFS estimator remains in this analyzer.
    /// </summary>
    public sealed class FinalizableObjectAnalyzer : IAnalyzer, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
    {
        public string Name => "Finalizable Object Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
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
            var topTypesByGen2 = new List<TypeGenerationProfile>(finalizableTypes.Count);
            for (int i = 0; i < finalizableTypes.Count; i++)
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
            // §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): null
            // when Stage B wasn't built for this run — retained-bytes below degrades to shallow
            // size in that case (IsRetainedBytesExact = false on the affected entries).
            IDominatorTreeProvider? treeProvider = cache.TryGetDominatorTreeProvider();

            int queueCount = 0;
            var queueSamples = new List<(ClrObject Obj, string TypeName)>(128);
            var queueTypeCountMap = new Dictionary<string, int>();

            foreach (ClrObject obj in heap.EnumerateFinalizableObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                queueCount++;

                string typeName = obj.IsValid && obj.Type is not null ? (obj.Type.Name ?? "<unknown>") : "<unknown>";
                if (!queueTypeCountMap.ContainsKey(typeName))
                    queueTypeCountMap[typeName] = 0;
                queueTypeCountMap[typeName]++;

                if (obj.IsValid && obj.Type is not null)
                    queueSamples.Add((obj, typeName));
            }

            // Build queue types by count, full list — no LINQ, manual sort
            var topQueueTypes = new List<QueueTypeStatistic>(queueTypeCountMap.Count);
            foreach (KeyValuePair<string, int> kv in queueTypeCountMap)
                topQueueTypes.Add(new QueueTypeStatistic(kv.Key, kv.Value));
            topQueueTypes.Sort(static (a, b) => b.QueueCount.CompareTo(a.QueueCount));

            // Sort by shallow size descending
            queueSamples.Sort(static (a, b) => b.Obj.Size.CompareTo(a.Obj.Size));

            var topEntries = new List<FinalizerQueueEntry>(queueSamples.Count);
            ulong totalQueueRetained = 0;
            bool hasUndisposedDisposable = false;
            bool isRetainedEstimatePartial = false;

            var isDisposableCache = new Dictionary<ulong, bool>();
            var disposedFieldCache = new Dictionary<ulong, ClrInstanceField?>();

            for (int i = 0; i < queueSamples.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ClrObject obj, string typeName) = queueSamples[i];

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

                ulong retained = 0;
                bool retainedIsExact = treeProvider is not null && treeProvider.TryGetRetainedBytes(obj.Address, out retained);
                if (!retainedIsExact)
                {
                    retained = obj.Size;
                    isRetainedEstimatePartial = true;
                }
                totalQueueRetained += retained;

                topEntries.Add(new FinalizerQueueEntry(
                    Address: obj.Address,
                    TypeName: typeName,
                    ShallowSize: obj.Size,
                    EstimatedRetainedBytes: retained,
                    IsDisposableType: isDisposable,
                    DisposedFieldFound: disposedFound,
                    DisposedFieldValue: disposedValue,
                    RetainedBytesIsExact: retainedIsExact));
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

        public void Dispose() { }
    }
}
