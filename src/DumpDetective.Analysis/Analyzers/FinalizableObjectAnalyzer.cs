using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

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
            int gen0 = 0, gen1 = 0, gen2 = 0;

            var finalizableTypes = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>();

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
                }
            }
            else
            {
                // Fallback: scan heap directly (only used when no Phase 1 index is available)
                foreach (ClrObject obj in heap.EnumerateObjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!obj.IsValid || obj.Type is null || !obj.Type.IsFinalizable)
                        continue;
                    totalObjects++;
                    totalBytes += obj.Size;
                    int g = ResolveGeneration(heap, obj.Address);
                    if (g == 0) gen0++;
                    else if (g == 1) gen1++;
                    else if (g == 2) gen2++;
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
                string typeName = TypeAggregateNameResolver.ResolveTypeName(heap, mt, e.SampleAddress);
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

            foreach (ClrObject obj in heap.EnumerateFinalizableObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                queueCount++;
                if (queueSamples.Count < options.QueueScanLimit && obj.IsValid && obj.Type is not null)
                    queueSamples.Add((obj.Address, obj.Type.Name ?? "<unknown>", obj.Size));
            }

            // Sort by shallow size descending, analyse top N
            queueSamples.Sort(static (a, b) => b.ShallowSize.CompareTo(a.ShallowSize));

            int entryLimit = Math.Min(queueSamples.Count, options.TopQueueEntries);
            var topEntries = new List<FinalizerQueueEntry>(entryLimit);
            ulong totalQueueRetained = 0;
            bool potentialResurrection = false;

            for (int i = 0; i < entryLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ulong addr, string typeName, ulong shallowSize) = queueSamples[i];

                ClrObject obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null)
                    continue;

                bool isDisposable = IsDisposableType(obj.Type);
                bool disposedFound = false;
                bool disposedValue = false;

                ClrInstanceField? disposedField = FindDisposedField(obj.Type);
                if (disposedField is not null)
                {
                    disposedFound = true;
                    try { disposedValue = disposedField.Read<bool>(obj, interior: false); }
                    catch { /* field unreadable */ }
                }

                // Resurrection heuristic: in queue, has IDisposable, _disposed field exists but is false
                if (isDisposable && disposedFound && !disposedValue)
                    potentialResurrection = true;

                ulong retained = BfsEstimateRetained(heap, addr, options.MaxBfsNodes, options.MaxBfsDepth);
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
                FinalizerQueueCount: queueCount,
                FinalizerQueueRetainedBytes: totalQueueRetained,
                PotentialResurrectionDetected: potentialResurrection,
                TopFinalizableTypesByGen2Count: topTypesByGen2,
                TopQueueEntriesByRetainedSize: topEntries);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsDisposableType(ClrType type)
        {
            foreach (ClrInterface iface in type.EnumerateInterfaces())
            {
                if (iface.Name is "System.IDisposable")
                    return true;
            }
            return false;
        }

        private static ClrInstanceField? FindDisposedField(ClrType type)
        {
            foreach (ClrInstanceField field in type.Fields)
            {
                string? name = field.Name;
                if (name is "_disposed" or "disposed" or "m_disposed" or "_isDisposed" or "isDisposed")
                    return field;
            }
            return null;
        }

        // Uses the ClrMD 3.x public API: heap.GetSegmentByAddress → segment.GetGeneration.
        // Correctly handles Ephemeral segments where Gen0/Gen1/Gen2 share one segment.
        private static int ResolveGeneration(ClrHeap heap, ulong address)
        {
            ClrSegment? seg = heap.GetSegmentByAddress(address);
            if (seg is null) return 2;
            try { return (int)seg.GetGeneration(address); }
            catch { return 2; }
        }

        /// <summary>
        /// Bounded BFS from <paramref name="startAddr"/>; returns the sum of sizes of all
        /// reachable objects (including the start object). Capped at
        /// <paramref name="maxNodes"/> nodes and <paramref name="maxDepth"/> depth.
        /// </summary>
        private static ulong BfsEstimateRetained(ClrHeap heap, ulong startAddr, int maxNodes, int maxDepth)
        {
            if (startAddr == 0)
                return 0;

            var visited = new HashSet<ulong>(capacity: 32) { startAddr };
            var queue = new Queue<(ulong Addr, int Depth)>(capacity: 32);
            queue.Enqueue((startAddr, 0));
            ulong totalSize = 0;
            int nodesSeen = 0;

            while (queue.Count > 0)
            {
                (ulong addr, int depth) = queue.Dequeue();
                nodesSeen++;

                if (nodesSeen > maxNodes || depth >= maxDepth)
                    break;

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

            return totalSize;
        }

        public void Dispose() { }
    }
}
