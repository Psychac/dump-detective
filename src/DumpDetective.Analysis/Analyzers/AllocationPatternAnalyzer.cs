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
    /// Pure Phase-2 post-processor: no heap scan, no ClrMD enumeration.
    /// Reads TypeAggregates built during Phase 1 and classifies allocation
    /// behaviour and GC pressure using arithmetic heuristics.
    /// Must run immediately after GCGenerationAnalyzer (list order in DefaultAnalyzerFactory).
    /// </summary>
    public sealed class AllocationPatternAnalyzer : IAnalyzer
    {
        public string Name => "Allocation Pattern Analysis";
        public string Category => "GC";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AllocationPatternAnalysisOptions options = context.GetOption<AllocationPatternAnalysisOptions>();
            return ValueTask.FromResult(Analyze(context, options).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(AnalysisContext context, AllocationPatternAnalysisOptions options)
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
                    TopTransientTypes: [],
                    TopShortishTypes: [],
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

            // Approximate gen bytes using average non-LOH size × per-MT gen count.
            AnalyzerHelpers.ComputeApproxGenBytes(aggregates, out ulong gen0Bytes, out ulong gen1Bytes, out ulong gen2Bytes);

            // Count percentages (relative to total object count) — round to two decimals for clarity
            double gen0CountPct = totalObjects > 0 ? Math.Round(gen0Objects * 100.0 / totalObjects, 2) : 0.0;
            double gen1CountPct = totalObjects > 0 ? Math.Round(gen1Objects * 100.0 / totalObjects, 2) : 0.0;
            double gen2CountPct = totalObjects > 0 ? Math.Round(gen2Objects * 100.0 / totalObjects, 2) : 0.0;
            double lohCountPct  = totalObjects > 0 ? Math.Round(lohObjects  * 100.0 / totalObjects, 2) : 0.0;
            // Size percentages (relative to total managed bytes) — round to two decimals for clarity
            double gen0SizePct  = totalSize > 0 ? Math.Round(gen0Bytes * 100.0 / (double)totalSize, 2) : 0.0;
            double gen1SizePct  = totalSize > 0 ? Math.Round(gen1Bytes * 100.0 / (double)totalSize, 2) : 0.0;
            double gen2SizePct  = totalSize > 0 ? Math.Round(gen2Bytes * 100.0 / (double)totalSize, 2) : 0.0;
            double lohSizePct   = totalSize > 0 ? Math.Round(lohBytes  * 100.0 / (double)totalSize, 2) : 0.0;

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

            var transient = new List<TypeAllocationProfile>(options.TopTypeLimit);
            var shortish  = new List<TypeAllocationProfile>(options.TopTypeLimit);
            var longLived = new List<TypeAllocationProfile>(options.TopTypeLimit);

            int scanLimit = Math.Min(sorted.Count, options.TopTypeLimit * options.ScanMultiplier);
            for (int i = 0; i < scanLimit; i++)
            {
                (ulong mt, TypeAggregateIndexEntry e) = sorted[i];
                if (e.Count == 0) continue;

                long mtGen0, mtGen1, mtGen2;
                (mtGen0, mtGen1, mtGen2) = (e.Gen0Count, e.Gen1Count, e.Gen2Count);

                double longLivedRatio = (mtGen2 + e.LohCount) * 1.0 / e.Count;
                double typeGen0Pct    = mtGen0 * 100.0 / e.Count;
                AllocationProfile typeProfile = typeGen0Pct > options.TransientClassificationThreshold
                    ? AllocationProfile.Transient
                    : longLivedRatio > options.LongLivedClassificationThreshold
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

                if (longLivedRatio > options.LongLivedSelectionThreshold)
                {
                    if (longLived.Count < options.TopTypeLimit)
                        longLived.Add(entry);
                }
                else if (typeGen0Pct >= options.TransientClassificationThreshold)
                {
                    if (transient.Count < options.TopTypeLimit)
                        transient.Add(entry);
                }
                else if (typeGen0Pct >= options.ShortLivedSelectionThreshold)
                {
                    if (shortish.Count < options.TopTypeLimit)
                        shortish.Add(entry);
                }
            }

            return new AllocationPatternDomainResult(
                gen0CountPct, gen1CountPct, gen2CountPct, lohCountPct,
                gen0SizePct,  gen1SizePct,  gen2SizePct,  lohSizePct,
                profile, pressure, promotionScore,
                transient, shortish, longLived);
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
        
        public void Dispose() { }
    }
}
