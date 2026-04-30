using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class GCGenerationAnalyzer : IAnalyzer
    {
        private const ulong LohThresholdBytes = 85_000;
        private const int TopLohTypeLimit = 15;
        private const int TopGenProfileLimit = 20;

        public string Name => "GC Generation Analysis";
        public string Category => "GC";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, progress: null);
        }

        private static AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "reading type aggregates"));

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIdx))
                return BuildFromIndex(heap, heapCache, heapIdx, progress);

            // No heap index available — fall back to type statistics (gen split will be Gen2-centric).
            progress?.Report(new(0, "reading type statistics (fallback)"));
            return BuildFromTypeStatistics(heap, cache.GetOrBuildTypeStatistics(heap));
        }

        // ── Fast path: TypeAggregates ──────────────────────────────────────────────

        private static GCGenerationDomainResult BuildFromIndex(
            ClrHeap heap,
            HeapAnalysisCache heapCache,
            HeapIndexBuildResult heapIdx,
            IProgress<AnalyzerProgressReport>? progress)
        {
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = heapIdx.TypeAggregates;

            ulong lohBytes = 0;
            long totalObjects = 0, lohObjects = 0;
            long gen0Objects = 0, gen1Objects = 0, gen2Objects = 0;

            var lohCandidates = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>();
            var genCandidates = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>();

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
            {
                TypeAggregateIndexEntry e = kv.Value;
                gen0Objects += e.Gen0Count;
                gen1Objects += e.Gen1Count;
                gen2Objects += e.Gen2Count;
                lohBytes    += e.LohSize;
                lohObjects  += e.LohCount;
                totalObjects += e.Count;

                if (e.LohCount > 0)
                    lohCandidates.Add((kv.Key, e));

                genCandidates.Add((kv.Key, e));
            }

            long nonLohTotal = totalObjects - lohObjects;
            long accountedGen = gen0Objects + gen1Objects + gen2Objects;

            ulong gen0Bytes, gen1Bytes, gen2Bytes;

            if (accountedGen == 0 && nonLohTotal > 0)
            {
                // Ephemeral segment (workstation GC): Phase-1 counts are all 0.
                // Run a lightweight per-object generation scan against the cached index
                // entries (HeapEntry[] already in memory) — not a fresh heap walk.
                progress?.Report(new(0, "scanning GC generations (Ephemeral fallback)"));
                (gen0Bytes, gen0Objects, gen1Bytes, gen1Objects, gen2Bytes, gen2Objects) =
                    RunGenerationScanFromIndex(heap, heapCache, progress);
            }
            else
            {
                // Server GC (dedicated per-generation segments): approximate bytes from
                // per-type avg non-LOH size × gen counts.
                gen0Bytes = 0; gen1Bytes = 0; gen2Bytes = 0;
                foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
                {
                    TypeAggregateIndexEntry e = kv.Value;
                    long nonLohCount = e.Count - e.LohCount;
                    if (nonLohCount <= 0) continue;
                    ulong nonLohSize = e.TotalSize >= e.LohSize ? e.TotalSize - e.LohSize : 0;
                    if (nonLohSize == 0) continue;
                    ulong avgSize = nonLohSize / (ulong)nonLohCount;
                    gen0Bytes += (ulong)e.Gen0Count * avgSize;
                    gen1Bytes += (ulong)e.Gen1Count * avgSize;
                    gen2Bytes += (ulong)e.Gen2Count * avgSize;
                }
            }

            ulong totalManagedBytes = gen0Bytes + gen1Bytes + gen2Bytes + lohBytes;
            double lohPct  = totalManagedBytes == 0 ? 0.0 : lohBytes   * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects      == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // Top LOH types — resolve names only for top N.
            lohCandidates.Sort(static (a, b) => b.Entry.LohSize.CompareTo(a.Entry.LohSize));
            int lohTake = Math.Min(TopLohTypeLimit, lohCandidates.Count);
            var topLohTypes = new List<TypeSnapshot>(lohTake);
            for (int i = 0; i < lohTake; i++)
            {
                (ulong mt, TypeAggregateIndexEntry e) = lohCandidates[i];
                string name = heap.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";
                topLohTypes.Add(new TypeSnapshot(name, (int)Math.Min(int.MaxValue, e.LohCount), e.LohSize, e.LohSize));
            }

            // Per-type generation profiles — only meaningful when Phase-1 gen counts are populated.
            List<TypeGenerationProfile> profiles = [];
            if (accountedGen > 0)
            {
                genCandidates.Sort(static (a, b) => b.Entry.Count.CompareTo(a.Entry.Count));
                int genTake = Math.Min(TopGenProfileLimit, genCandidates.Count);
                profiles = new List<TypeGenerationProfile>(genTake);
                for (int i = 0; i < genTake; i++)
                {
                    (ulong mt, TypeAggregateIndexEntry e) = genCandidates[i];
                    string name = heap.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";
                    profiles.Add(new TypeGenerationProfile(
                        name, e.Gen0Count, e.Gen1Count, e.Gen2Count,
                        (int)Math.Min(int.MaxValue, e.LohCount)));
                }
            }

            return new GCGenerationDomainResult(
                gen0Bytes,
                (int)Math.Min(int.MaxValue, gen0Objects),
                gen1Bytes,
                (int)Math.Min(int.MaxValue, gen1Objects),
                gen2Bytes,
                (int)Math.Min(int.MaxValue, gen2Objects),
                lohBytes,
                lohPct,
                (int)Math.Min(int.MaxValue, totalObjects),
                (int)Math.Min(int.MaxValue, lohObjects),
                topLohTypes,
                gen2Pct,
                profiles);
        }

        // ── Generation scan fallback (Ephemeral GC) ───────────────────────────────
        // Scans the cached HeapEntry[] (in-memory) or disk index — NOT the raw heap.
        // Only called when Phase-1 gen counts are all 0 (workstation / Ephemeral GC).

        private static (ulong gen0Bytes, long gen0Objects,
                        ulong gen1Bytes, long gen1Objects,
                        ulong gen2Bytes, long gen2Objects)
            RunGenerationScanFromIndex(
                ClrHeap heap,
                HeapAnalysisCache heapCache,
                IProgress<AnalyzerProgressReport>? progress)
        {
            long gen0O = 0, gen1O = 0, gen2O = 0;
            ulong gen0B = 0, gen1B = 0, gen2B = 0;
            long scanned = 0;
            const long progressInterval = 100_000;

            void Process(ulong address, ulong size)
            {
                if (address == 0 || size >= LohThresholdBytes)
                    return;

                long s = Interlocked.Increment(ref scanned);
                if (s % progressInterval == 0)
                    progress?.Report(new(s, "scanning GC generations"));

                int gen = ResolveGeneration(heap, address);

                if (gen == 0)       { Interlocked.Increment(ref gen0O); Interlocked.Add(ref gen0B, size); }
                else if (gen == 1)  { Interlocked.Increment(ref gen1O); Interlocked.Add(ref gen1B, size); }
                else                { Interlocked.Increment(ref gen2O); Interlocked.Add(ref gen2B, size); }
            }

            if (heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIdx)
                && heapIdx.StorageKind == HeapIndexStorageKind.Memory
                && heapIdx.InMemoryEntries is { } entries)
            {
                // Cap at ObjectCount: InMemoryEntries may have up to 50 000 extra uninitialized
                // slots past ObjectCount when the Phase-1 trim threshold was not reached.
                // GC.AllocateUninitializedArray means those slots hold garbage addresses/sizes.
                int safeCount = (int)Math.Min(heapIdx.ObjectCount, entries.Length);
                Parallel.For(0, safeCount, i => Process(entries[i].Address, entries[i].Size));
            }
            else
            {
                foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                    Process(entry.Address, entry.Size);
            }

            return (gen0B, gen0O, gen1B, gen1O, gen2B, gen2O);
        }

        // Uses the ClrMD 3.x public API: heap.GetSegmentByAddress → segment.GetGeneration.
        // This works correctly for Ephemeral segments (workstation GC) where all generations
        // share a single segment. No reflection required.
        private static int ResolveGeneration(ClrHeap heap, ulong address)
        {
            ClrSegment? seg = heap.GetSegmentByAddress(address);
            if (seg is null)
                return 2;

            try
            {
                return (int)seg.GetGeneration(address);
            }
            catch
            {
                return 2;
            }
        }

        // ── Slow / fallback path (no heap index) ──────────────────────────────────

        private static GCGenerationDomainResult BuildFromTypeStatistics(
            ClrHeap heap,
            Dictionary<string, CachedTypeStatistics> typeStats)
        {
            ulong lohBytes = 0;
            int totalObjects = 0, lohObjects = 0, gen2Objects = 0;
            ulong gen2Bytes = 0;

            foreach (CachedTypeStatistics stat in typeStats.Values)
            {
                lohBytes     += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects   += stat.LohCount;
                int nonLoh    = Math.Max(0, stat.Count - stat.LohCount);
                ulong nonLohBytes = stat.TotalSize >= stat.LohSize ? stat.TotalSize - stat.LohSize : 0;
                gen2Objects  += nonLoh;
                gen2Bytes    += nonLohBytes;
            }

            ulong totalManagedBytes = gen2Bytes + lohBytes;
            double lohPct  = totalManagedBytes == 0 ? 0.0 : lohBytes    * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects      == 0 ? 0.0 : gen2Objects  * 100.0 / totalObjects;

            // No LINQ in hot paths — explicit sort + loop.
            var lohList = new List<CachedTypeStatistics>(capacity: 32);
            foreach (CachedTypeStatistics stat in typeStats.Values)
                if (stat.LohCount > 0) lohList.Add(stat);
            lohList.Sort(static (a, b) => b.LohSize.CompareTo(a.LohSize));
            int lohTake = Math.Min(TopLohTypeLimit, lohList.Count);
            var topLohTypes = new List<TypeSnapshot>(lohTake);
            for (int i = 0; i < lohTake; i++)
            {
                CachedTypeStatistics stat = lohList[i];
                topLohTypes.Add(new TypeSnapshot(stat.TypeName, stat.LohCount, stat.LohSize, stat.LohSize));
            }

            return new GCGenerationDomainResult(
                Gen0Bytes: 0, Gen0Objects: 0,
                Gen1Bytes: 0, Gen1Objects: 0,
                gen2Bytes, gen2Objects,
                lohBytes, lohPct,
                totalObjects, lohObjects,
                topLohTypes,
                gen2Pct,
                PerTypeGenerationProfiles: []);
        }
    }
}
