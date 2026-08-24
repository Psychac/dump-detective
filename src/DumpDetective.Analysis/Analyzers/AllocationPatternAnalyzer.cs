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
            AllocationPatternAnalysisOptions options = context.AnalysisOptions.AllocationPatternAnalysis;
            return ValueTask.FromResult(Analyze(context, options, context.Progress, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(AnalysisContext context, AllocationPatternAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            if (context.Cache is not HeapAnalysisCache heapCache
                || !heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            {
                System.Diagnostics.Debug.Fail("AllocationPatternAnalyzer requires HeapIndexBuildResult from GCGenerationAnalyzer. Check DefaultAnalyzerFactory ordering.");
                return new AllocationPatternDomainResult(
                    Gen0CountPct: 0, Gen1CountPct: 0, Gen2CountPct: 0, LohCountPct: 0,
                    Gen0SizePct: 0, Gen1SizePct: 0, Gen2SizePct: 0, LohSizePct: 0,
                    TotalManagedBytes: 0, Gen0Bytes: 0, Gen1Bytes: 0, Gen2Bytes: 0, LohBytes: 0,
                    Profile: AllocationProfile.Mixed,
                    GCPressure: GCPressureLevel.Low,
                    PromotionPressureScore: 0,
                    TopTransientTypes: [],
                    TopShortishTypes: [],
                    TopLongLivedTypes: [],
                    TopHighGen1SurvivorTypes: [],
                    LohSizeBands: null,
                    FinalizableTypeCount: 0,
                    FinalizableBytes: 0);
            }

            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            long totalObjects = 0;
            long gen0Objects = 0, gen1Objects = 0, gen2Objects = 0, lohObjects = 0;
            ulong totalSize = 0, lohBytes = 0;
            int finalizableTypeCount = 0;
            ulong finalizableBytes = 0;
            // Approximate per-bucket byte totals from each type's average object size — same
            // approach MemoryAnalysisProjection uses, since exact per-object sizes are not
            // retained past Phase 1 aggregation.
            var bucketBytes = new ulong[SizeBucketHelper.BucketCount];

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
            {
                TypeAggregateIndexEntry e = kv.Value;
                totalObjects += e.Count;
                gen0Objects += e.Gen0Count;
                gen1Objects += e.Gen1Count;
                gen2Objects += e.Gen2Count;
                lohObjects += e.LohCount;
                totalSize += e.TotalSize;
                lohBytes += e.LohSize;

                if ((e.Flags & TypeAggregateFlags.IsFinalizableType) != 0)
                {
                    finalizableTypeCount++;
                    finalizableBytes += e.TotalSize;
                }

                if (e.Count > 0)
                {
                    ulong avgSize = e.TotalSize / (ulong)e.Count;
                    bucketBytes[SizeBucketHelper.GetBucketIndex(avgSize)] += e.TotalSize;
                }
            }

            // LOH size-band distribution: last three buckets (85 KB–1 MB, 1 MB–10 MB, ≥10 MB).
            // Object counts come from the exact Phase 1 global histogram; byte totals use the
            // avgSize approximation above. Falls back gracefully if an older cache.bin loaded
            // an 8-bucket (pre-10MB-split) histogram.
            List<SizeBucketEntry>? lohSizeBands = null;
            if (idx.GlobalSizeBuckets is { Length: >= SizeBucketHelper.BucketCount } globalBuckets)
            {
                lohSizeBands = new List<SizeBucketEntry>(3);
                for (int i = SizeBucketHelper.BucketCount - 3; i < SizeBucketHelper.BucketCount; i++)
                {
                    lohSizeBands.Add(new SizeBucketEntry(SizeBucketHelper.BucketLabels[i], globalBuckets[i], bucketBytes[i]));
                }
            }

            // Exact gen bytes from segment metadata if available; otherwise approximate from aggregates.
            ulong gen0Bytes, gen1Bytes, gen2Bytes;
            try
            {
                AnalyzerHelpers.ComputeExactGenBytes(context.Heap, out gen0Bytes, out gen1Bytes, out gen2Bytes);
            }
            catch
            {
#pragma warning disable CS0618
                AnalyzerHelpers.ComputeApproxGenBytes(aggregates, out gen0Bytes, out gen1Bytes, out gen2Bytes);
#pragma warning restore CS0618
            }

            // Count percentages (relative to total object count) — round to two decimals for clarity
            double gen0CountPct = totalObjects > 0 ? Math.Round(gen0Objects * 100.0 / totalObjects, 2) : 0.0;
            double gen1CountPct = totalObjects > 0 ? Math.Round(gen1Objects * 100.0 / totalObjects, 2) : 0.0;
            double gen2CountPct = totalObjects > 0 ? Math.Round(gen2Objects * 100.0 / totalObjects, 2) : 0.0;
            double lohCountPct = totalObjects > 0 ? Math.Round(lohObjects * 100.0 / totalObjects, 2) : 0.0;
            // Size percentages (relative to total managed bytes) — round to two decimals for clarity
            double gen0SizePct = totalSize > 0 ? Math.Round(gen0Bytes * 100.0 / (double)totalSize, 2) : 0.0;
            double gen1SizePct = totalSize > 0 ? Math.Round(gen1Bytes * 100.0 / (double)totalSize, 2) : 0.0;
            double gen2SizePct = totalSize > 0 ? Math.Round(gen2Bytes * 100.0 / (double)totalSize, 2) : 0.0;
            double lohSizePct = totalSize > 0 ? Math.Round(lohBytes * 100.0 / (double)totalSize, 2) : 0.0;

            AllocationProfile profile = ClassifyProfile(gen0CountPct, gen2CountPct, options.TransientClassificationThreshold);
            // Pressure uses inverted gen0 (high Gen0 count = transient = low pressure), gen2 count%
            // (reflects Gen2 collection frequency), and LOH size% (LOH count% is near-zero on typical heaps).
            double pressureScore = ((100.0 - gen0CountPct) * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2);
            GCPressureLevel pressure = ClassifyPressure(pressureScore);
            double promotionScore = gen2CountPct + (lohSizePct * 2.0);

            // Build a metric list — CompositeScore is the only selection mode (D7, §11.2): it
            // blends Gen0%/Gen2 ratio/LOH size% into a signal aligned with "interesting allocation
            // pattern," unlike raw count/size which just surfaces the biggest collection regardless
            // of whether it's pathological.
            var metrics = new List<(ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore)>(aggregates.Count);
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var e = kv.Value;
                if (e.Count == 0) continue;

                double gen0Pct = e.Gen0Count * 100.0 / e.Count;
                double gen2Ratio = (e.Gen2Count + e.LohCount) * 1.0 / e.Count;
                double mtLohSizePct = totalSize > 0 ? e.LohSize * 100.0 / (double)totalSize : 0.0;
                double composite = (gen0Pct * options.Gen0Weight)
                                   + ((gen2Ratio * 100.0) * options.Gen2Weight)
                                   + (mtLohSizePct * options.LohSizeWeight);

                metrics.Add((kv.Key, e, gen0Pct, gen2Ratio, mtLohSizePct, composite));
            }

            Comparison<(ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore)> comparator =
                (a, b) => b.CompositeScore.CompareTo(a.CompositeScore);

            var transCandidates = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();
            var shortCandidates = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();
            var longCandidates = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();
            var highGen1Survivors = new List<(double Gen1SurvivalRate, TypeAllocationProfile Profile)>();

            progress?.Report(new AnalyzerProgressReport(0, $"scanning {metrics.Count} types for allocation patterns"));

            // Classify every candidate into its bucket first, then sort and take each bucket's
            // own top set independently (D7, §11.2) — the only way to guarantee each table's
            // ranking is actually correct: a single-pass incremental fill is scan-order-dependent
            // and can silently drop a bucket's true top member if other buckets filled first.
            for (int i = 0; i < metrics.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i % 100 == 0)
                    progress?.Report(new AnalyzerProgressReport(i * 100 / Math.Max(metrics.Count, 1), $"scanned {i}/{metrics.Count} types"));
                var item = metrics[i];
                ulong mt = item.Mt;
                TypeAggregateIndexEntry e = item.Entry;

                long mtGen0 = e.Gen0Count;
                long mtGen1 = e.Gen1Count;
                long mtGen2 = e.Gen2Count;

                double longLivedRatio = item.Gen2Ratio;
                double typeGen0Pct = item.Gen0Pct;
                double gen1SurvivalRate = mtGen0 > 0 ? mtGen1 / (double)mtGen0 : 0.0;
                TypeProfile typeProfile = typeGen0Pct > options.TransientClassificationThreshold
                    ? TypeProfile.Transient
                    : longLivedRatio > options.LongLivedClassificationThreshold
                        ? TypeProfile.Retained
                        : TypeProfile.Mixed;

                string typeName = (context.Runtime is not null && heapCache.TryGetTypeName(context.Runtime.Heap, mt, out var resolvedName)) ? resolvedName : $"MT:0x{mt:x}";

                var entry = new TypeAllocationProfile(
                    typeName,
                    (int)Math.Min(int.MaxValue, mtGen0),
                    (int)Math.Min(int.MaxValue, mtGen1),
                    (int)Math.Min(int.MaxValue, mtGen2),
                    longLivedRatio,
                    typeProfile,
                    e.TotalSize,
                    gen1SurvivalRate,
                    IsFinalizable: (e.Flags & TypeAggregateFlags.IsFinalizableType) != 0);

                if (gen1SurvivalRate > 0.5)
                    highGen1Survivors.Add((gen1SurvivalRate, entry));

                if (longLivedRatio > options.LongLivedSelectionThreshold)
                    longCandidates.Add((item, entry));
                else if (typeGen0Pct >= options.TransientClassificationThreshold)
                    transCandidates.Add((item, entry));
                else if (typeGen0Pct >= options.ShortLivedSelectionThreshold)
                    shortCandidates.Add((item, entry));
            }

            // Full ranked population, no Top-N cap — the render layer slices for display
            // (§11.2 D5).
            transCandidates.Sort((a, b) => comparator(a.Metric, b.Metric));
            shortCandidates.Sort((a, b) => comparator(a.Metric, b.Metric));
            longCandidates.Sort((a, b) => comparator(a.Metric, b.Metric));

            var transient = transCandidates.Select(t => t.Profile).ToList();
            var shortish = shortCandidates.Select(s => s.Profile).ToList();
            var longLived = longCandidates.Select(l => l.Profile).ToList();

            highGen1Survivors.Sort((a, b) => b.Gen1SurvivalRate.CompareTo(a.Gen1SurvivalRate));
            var topGen1Survivors = highGen1Survivors
                .Select(x => x.Profile)
                .ToList();

            progress?.Report(new AnalyzerProgressReport(100, "allocation pattern analysis complete"));

            return new AllocationPatternDomainResult(
                gen0CountPct, gen1CountPct, gen2CountPct, lohCountPct,
                gen0SizePct, gen1SizePct, gen2SizePct, lohSizePct,
                TotalManagedBytes: totalSize, Gen0Bytes: gen0Bytes, Gen1Bytes: gen1Bytes, Gen2Bytes: gen2Bytes, LohBytes: lohBytes,
                profile, pressure, promotionScore,
                transient, shortish, longLived, topGen1Survivors,
                LohSizeBands: lohSizeBands,
                FinalizableTypeCount: finalizableTypeCount,
                FinalizableBytes: finalizableBytes);
        }

        private static AllocationProfile ClassifyProfile(double gen0Pct, double gen2Pct, double transientThreshold)
        {
            if (gen0Pct > transientThreshold) return AllocationProfile.Transient;
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
