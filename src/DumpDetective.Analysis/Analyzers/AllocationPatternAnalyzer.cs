using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Pure Phase-2 post-processor: no heap scan, no ClrMD enumeration.
    /// Reads TypeAggregates built during Phase 1 and classifies allocation
    /// behaviour and GC pressure using arithmetic heuristics.
    /// Must run immediately after GCGenerationAnalyzer (list order in DefaultAnalyzerFactory).
    /// </summary>
    public sealed class AllocationPatternAnalyzer : IAnalyzer
    {
        private const int TopTypeLimit = 20;
        private const ulong LohThresholdBytes = 85_000;

        public string Name => "Allocation Pattern Analysis";
        public string Category => "GC";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(AnalysisContext context)
        {
            if (context.Cache is not HeapAnalysisCache heapCache
                || !heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            {
                return new AllocationPatternDomainResult(
                    Gen0CountPct: 0, Gen1CountPct: 0, Gen2CountPct: 0, LohCountPct: 0,
                    Gen0SizePct: 0,  Gen1SizePct: 0,  Gen2SizePct: 0,  LohSizePct: 0,
                    Profile: AllocationProfile.Mixed,
                    GCPressure: GCPressureLevel.Low,
                    PromotionPressureScore: 0,
                    TopShortLivedTypes: [],
                    TopLongLivedTypes: []);
            }

            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            long totalObjects = 0;
            long gen0Objects = 0, gen1Objects = 0, gen2Objects = 0, lohObjects = 0;
            ulong totalSize = 0, lohBytes = 0;

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
            {
                TypeAggregateIndexEntry e = kv.Value;
                totalObjects += e.Count;
                gen0Objects  += e.Gen0Count;
                gen1Objects  += e.Gen1Count;
                gen2Objects  += e.Gen2Count;
                lohObjects   += e.LohCount;
                totalSize    += e.TotalSize;
                lohBytes     += e.LohSize;
            }

            long accountedGen = gen0Objects + gen1Objects + gen2Objects;
            long nonLohTotal  = totalObjects - lohObjects;

            // Per-MT gen counts built by ephemeral fallback scan (null = use TypeAggregates directly).
            Dictionary<ulong, (long Gen0, long Gen1, long Gen2)>? perMtGen = null;
            ulong gen0Bytes = 0, gen1Bytes = 0, gen2Bytes = 0;

            if (accountedGen == 0 && nonLohTotal > 0)
            {
                // Ephemeral/workstation GC: Phase-1 segment-kind gen counts are all 0 because
                // ClrMD reports the ephemeral segment as GCSegmentKind.Ephemeral (not Generation0/1/2).
                // Scan cached index entries and resolve generation per-object via seg.GetGeneration().
                (perMtGen, gen0Objects, gen1Objects, gen2Objects, gen0Bytes, gen1Bytes, gen2Bytes) =
                    BuildPerMtGenCounts(context.Heap!, heapCache, idx);
            }
            else if (accountedGen > 0)
            {
                // Server GC (dedicated per-generation segments): approximate gen bytes using
                // average non-LOH size × per-MT gen count — same heuristic as GCGenerationAnalyzer.
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

            // Count percentages (relative to total object count)
            double gen0CountPct = totalObjects > 0 ? gen0Objects * 100.0 / totalObjects : 0.0;
            double gen1CountPct = totalObjects > 0 ? gen1Objects * 100.0 / totalObjects : 0.0;
            double gen2CountPct = totalObjects > 0 ? gen2Objects * 100.0 / totalObjects : 0.0;
            double lohCountPct  = totalObjects > 0 ? lohObjects  * 100.0 / totalObjects : 0.0;
            // Size percentages (relative to total managed bytes)
            double gen0SizePct  = totalSize > 0 ? gen0Bytes * 100.0 / (double)totalSize : 0.0;
            double gen1SizePct  = totalSize > 0 ? gen1Bytes * 100.0 / (double)totalSize : 0.0;
            double gen2SizePct  = totalSize > 0 ? gen2Bytes * 100.0 / (double)totalSize : 0.0;
            double lohSizePct   = totalSize > 0 ? lohBytes  * 100.0 / (double)totalSize : 0.0;

            AllocationProfile profile  = ClassifyProfile(gen0CountPct, gen2CountPct);
            // Pressure uses count% for gen0/2 (reflects GC collection frequency) and
            // size% for LOH (LOH count% is near-zero on typical heaps, size% is meaningful).
            double pressureScore       = (gen0CountPct * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2);
            GCPressureLevel pressure   = ClassifyPressure(pressureScore);
            double promotionScore      = gen2CountPct + (lohSizePct * 2.0);

            // Sort entries by descending total count for top-N selection
            var sorted = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>(aggregates.Count);
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
                sorted.Add((kv.Key, kv.Value));
            sorted.Sort(static (a, b) => b.Entry.Count.CompareTo(a.Entry.Count));

            var shortLived = new List<TypeAllocationProfile>(TopTypeLimit);
            var longLived  = new List<TypeAllocationProfile>(TopTypeLimit);

            int scanLimit = Math.Min(sorted.Count, TopTypeLimit * 2);
            for (int i = 0; i < scanLimit; i++)
            {
                (ulong mt, TypeAggregateIndexEntry e) = sorted[i];
                if (e.Count == 0) continue;

                long mtGen0, mtGen1, mtGen2;
                if (perMtGen is not null && perMtGen.TryGetValue(mt, out var g))
                    (mtGen0, mtGen1, mtGen2) = g;
                else
                    (mtGen0, mtGen1, mtGen2) = (e.Gen0Count, e.Gen1Count, e.Gen2Count);

                double longLivedRatio = (mtGen2 + e.LohCount) * 1.0 / e.Count;
                double typeGen0Pct    = mtGen0 * 100.0 / e.Count;
                AllocationProfile typeProfile = typeGen0Pct > 70.0
                    ? AllocationProfile.Transient
                    : longLivedRatio > 0.5
                        ? AllocationProfile.Retained
                        : AllocationProfile.Mixed;

                string typeName = context.Heap?.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";

                var entry = new TypeAllocationProfile(
                    typeName,
                    (int)Math.Min(int.MaxValue, mtGen0),
                    (int)Math.Min(int.MaxValue, mtGen1),
                    (int)Math.Min(int.MaxValue, mtGen2),
                    longLivedRatio,
                    typeProfile);

                if (longLivedRatio > 0.3)
                {
                    if (longLived.Count < TopTypeLimit)
                        longLived.Add(entry);
                }
                else
                {
                    if (shortLived.Count < TopTypeLimit)
                        shortLived.Add(entry);
                }
            }

            return new AllocationPatternDomainResult(
                gen0CountPct, gen1CountPct, gen2CountPct, lohCountPct,
                gen0SizePct,  gen1SizePct,  gen2SizePct,  lohSizePct,
                profile, pressure, promotionScore,
                shortLived, longLived);
        }

        private static AllocationProfile ClassifyProfile(double gen0Pct, double gen2Pct)
        {
            if (gen0Pct > 70.0) return AllocationProfile.Transient;
            if (gen2Pct > 50.0) return AllocationProfile.Retained;
            if (gen0Pct > 50.0) return AllocationProfile.Steady;
            return AllocationProfile.Mixed;
        }

        private static GCPressureLevel ClassifyPressure(double score)
        {
            if (score > 70.0) return GCPressureLevel.Critical;
            if (score > 45.0) return GCPressureLevel.High;
            if (score > 20.0) return GCPressureLevel.Moderate;
            return GCPressureLevel.Low;
        }

        // ── Ephemeral GC fallback ──────────────────────────────────────────────────
        // Called when Phase-1 segment-kind gen counts are all 0 (workstation/ephemeral GC).
        // Scans the cached HeapEntry[] (or disk index) and resolves generation per-object
        // via ClrSegment.GetGeneration — same approach as GCGenerationAnalyzer's fallback.
        // Builds a per-MT gen count dictionary for the type-level breakdown.

        private static (Dictionary<ulong, (long Gen0, long Gen1, long Gen2)> PerMt,
                        long Gen0Total, long Gen1Total, long Gen2Total,
                        ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes)
            BuildPerMtGenCounts(ClrHeap heap, HeapAnalysisCache heapCache, HeapIndexBuildResult idx)
        {
            var perMt = new ConcurrentDictionary<ulong, long[]>(
                concurrencyLevel: Environment.ProcessorCount,
                capacity: idx.TypeAggregates.Count);
            long g0 = 0, g1 = 0, g2 = 0;
            long g0B = 0, g1B = 0, g2B = 0;

            void Process(ulong address, ulong mt, ulong size)
            {
                if (address == 0 || mt == 0 || size >= LohThresholdBytes)
                    return;

                int gen = Math.Clamp(ResolveObjectGeneration(heap, address), 0, 2);

                long[] counts = perMt.GetOrAdd(mt, static _ => new long[3]);
                Interlocked.Increment(ref counts[gen]);

                if (gen == 0)       { Interlocked.Increment(ref g0); Interlocked.Add(ref g0B, (long)size); }
                else if (gen == 1)  { Interlocked.Increment(ref g1); Interlocked.Add(ref g1B, (long)size); }
                else                { Interlocked.Increment(ref g2); Interlocked.Add(ref g2B, (long)size); }
            }

            if (idx.StorageKind == HeapIndexStorageKind.Memory && idx.InMemoryEntries is { } entries)
            {
                int safeCount = (int)Math.Min(idx.ObjectCount, entries.Length);
                Parallel.For(0, safeCount, i =>
                {
                    HeapEntry e = entries[i];
                    Process(e.Address, e.MethodTable, e.Size);
                });
            }
            else
            {
                foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                    Process(entry.Address, entry.MethodTable, entry.Size);
            }

            var result = new Dictionary<ulong, (long Gen0, long Gen1, long Gen2)>(perMt.Count);
            foreach (KeyValuePair<ulong, long[]> kv in perMt)
                result[kv.Key] = (kv.Value[0], kv.Value[1], kv.Value[2]);
            return (result, g0, g1, g2, (ulong)g0B, (ulong)g1B, (ulong)g2B);
        }

        // Uses ClrMD 3.x public API: heap.GetSegmentByAddress → segment.GetGeneration.
        // Correctly handles Ephemeral segments where all generations share one segment.
        private static int ResolveObjectGeneration(ClrHeap heap, ulong address)
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
    }
}
