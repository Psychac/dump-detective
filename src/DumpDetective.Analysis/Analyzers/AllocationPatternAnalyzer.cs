using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
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
                    TopHighGen1SurvivorTypes: []);
            }

            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            long totalObjects = 0;
            long gen0Objects = 0, gen1Objects = 0, gen2Objects = 0, lohObjects = 0;
            ulong totalSize = 0, lohBytes = 0;

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

            // Build a metric list and sort according to SelectionMode from options
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

            // Choose comparator based on options.Mode
            Comparison<(ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore)> comparator = (a, b) => b.Entry.Count.CompareTo(a.Entry.Count);
            switch (options.Mode)
            {
                case AllocationPatternAnalysisOptions.SelectionMode.TopByGen0Pct:
                    comparator = (a, b) => b.Gen0Pct.CompareTo(a.Gen0Pct);
                    break;
                case AllocationPatternAnalysisOptions.SelectionMode.TopBySize:
                    comparator = (a, b) => b.Entry.TotalSize.CompareTo(a.Entry.TotalSize);
                    break;
                case AllocationPatternAnalysisOptions.SelectionMode.CompositeScore:
                    comparator = (a, b) => b.CompositeScore.CompareTo(a.CompositeScore);
                    break;
                case AllocationPatternAnalysisOptions.SelectionMode.TopByCount:
                default:
                    comparator = (a, b) => b.Entry.Count.CompareTo(a.Entry.Count);
                    break;
            }

            // Calculate scan limit before sorting to enable partial sort optimization
            int scanLimit;
            if (options.Strategy == AllocationPatternAnalysisOptions.ScanStrategy.FullScan)
                scanLimit = Math.Min(metrics.Count, options.MaxScanItemsAbsolute);
            else
                scanLimit = Math.Min(metrics.Count, options.TopTypeLimit * options.ScanMultiplier);

            // For large FullScan operations, only sort the top items we need to reduce wasted sort work
            if (options.Strategy == AllocationPatternAnalysisOptions.ScanStrategy.FullScan && metrics.Count > scanLimit * 2)
            {
                // Partial sort: only sort enough to guarantee we have good candidates
                var sortLimit = Math.Min(metrics.Count, scanLimit * 2);
                metrics.Sort(0, sortLimit, Comparer<(ulong, TypeAggregateIndexEntry, double, double, double, double)>.Create(comparator));
            }
            else
                metrics.Sort(comparator);

            var transient = new List<TypeAllocationProfile>(options.TopTypeLimit);
            var shortish = new List<TypeAllocationProfile>(options.TopTypeLimit);
            var longLived = new List<TypeAllocationProfile>(options.TopTypeLimit);
            var highGen1Survivors = new List<(double Gen1SurvivalRate, TypeAllocationProfile Profile)>();

            progress?.Report(new AnalyzerProgressReport(0, $"scanning {scanLimit} types for allocation patterns"));

            if (options.Priority == AllocationPatternAnalysisOptions.SelectionPriority.ClassificationFirst
                || options.Priority == AllocationPatternAnalysisOptions.SelectionPriority.Mixed)
            {
                var transCandidates = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();
                var shortCandidates = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();
                var longCandidates = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();

                for (int i = 0; i < scanLimit; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i % 100 == 0)
                        progress?.Report(new AnalyzerProgressReport(i * 100 / scanLimit, $"scanned {i}/{scanLimit} types"));
                    var item = metrics[i];
                    ulong mt = item.Mt;
                    TypeAggregateIndexEntry e = item.Entry;

                    long mtGen0 = e.Gen0Count;
                    long mtGen1 = e.Gen1Count;
                    long mtGen2 = e.Gen2Count;

                    double longLivedRatio = item.Gen2Ratio;
                    double typeGen0Pct = item.Gen0Pct;
                    double gen1SurvivalRate = mtGen0 > 0 ? mtGen1 / (double)mtGen0 : 0.0;
                    AllocationProfile typeProfile = typeGen0Pct > options.TransientClassificationThreshold
                        ? AllocationProfile.Transient
                        : longLivedRatio > options.LongLivedClassificationThreshold
                            ? AllocationProfile.Retained
                            : AllocationProfile.Mixed;

                    string typeName = (context.Runtime is not null && heapCache.TryGetTypeName(context.Runtime.Heap, mt, out var resolvedName)) ? resolvedName : $"MT:0x{mt:x}";

                    var entry = new TypeAllocationProfile(
                        typeName,
                        (int)Math.Min(int.MaxValue, mtGen0),
                        (int)Math.Min(int.MaxValue, mtGen1),
                        (int)Math.Min(int.MaxValue, mtGen2),
                        longLivedRatio,
                        typeProfile,
                        e.TotalSize,
                        gen1SurvivalRate);

                    if (gen1SurvivalRate > 0.5)
                        highGen1Survivors.Add((gen1SurvivalRate, entry));

                    if (longLivedRatio > options.LongLivedSelectionThreshold)
                        longCandidates.Add((item, entry));
                    else if (typeGen0Pct >= options.TransientClassificationThreshold)
                        transCandidates.Add((item, entry));
                    else if (typeGen0Pct >= options.ShortLivedSelectionThreshold)
                        shortCandidates.Add((item, entry));
                }

                // sort each candidate set by the chosen comparator and take top-N
                transCandidates.Sort((a, b) => comparator(a.Metric, b.Metric));
                shortCandidates.Sort((a, b) => comparator(a.Metric, b.Metric));
                longCandidates.Sort((a, b) => comparator(a.Metric, b.Metric));

                foreach (var t in transCandidates.Take(options.TopTypeLimit)) transient.Add(t.Profile);
                foreach (var s in shortCandidates.Take(options.TopTypeLimit)) shortish.Add(s.Profile);
                foreach (var l in longCandidates.Take(options.TopTypeLimit)) longLived.Add(l.Profile);

                // Mixed priority: if some buckets have fewer than TopTypeLimit, allow spillover
                if (options.Priority == AllocationPatternAnalysisOptions.SelectionPriority.Mixed)
                {
                    int needTransient = options.TopTypeLimit - transient.Count;
                    int needShortish = options.TopTypeLimit - shortish.Count;
                    int needLongLived = options.TopTypeLimit - longLived.Count;

                    if (needTransient > 0 || needShortish > 0 || needLongLived > 0)
                    {
                        var spill = new List<TypeAllocationProfile>();
                        // collect remaining candidates from each bucket beyond the taken window
                        spill.AddRange(transCandidates.Skip(options.TopTypeLimit).Select(x => x.Profile));
                        spill.AddRange(shortCandidates.Skip(options.TopTypeLimit).Select(x => x.Profile));
                        spill.AddRange(longCandidates.Skip(options.TopTypeLimit).Select(x => x.Profile));

                        // sort spill by comparator using their metric (rebuild mapping)
                        // we need metric -> profile mapping; rebuild from candidate tuples
                        var spillMetrics = new List<((ulong Mt, TypeAggregateIndexEntry Entry, double Gen0Pct, double Gen2Ratio, double MtLohSizePct, double CompositeScore) Metric, TypeAllocationProfile Profile)>();
                        spillMetrics.AddRange(transCandidates.Skip(options.TopTypeLimit));
                        spillMetrics.AddRange(shortCandidates.Skip(options.TopTypeLimit));
                        spillMetrics.AddRange(longCandidates.Skip(options.TopTypeLimit));

                        spillMetrics.Sort((a, b) => comparator(a.Metric, b.Metric));

                        int spIdx = 0;
                        while ((needTransient > 0 || needShortish > 0 || needLongLived > 0) && spIdx < spillMetrics.Count)
                        {
                            var cand = spillMetrics[spIdx++].Profile;
                            if (needTransient > 0)
                            {
                                transient.Add(cand);
                                needTransient--;
                                continue;
                            }
                            if (needShortish > 0)
                            {
                                shortish.Add(cand);
                                needShortish--;
                                continue;
                            }
                            if (needLongLived > 0)
                            {
                                longLived.Add(cand);
                                needLongLived--;
                                continue;
                            }
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < scanLimit; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i % 100 == 0)
                        progress?.Report(new AnalyzerProgressReport(i * 100 / scanLimit, $"scanned {i}/{scanLimit} types"));
                    var item = metrics[i];
                    ulong mt = item.Mt;
                    TypeAggregateIndexEntry e = item.Entry;

                    long mtGen0 = e.Gen0Count;
                    long mtGen1 = e.Gen1Count;
                    long mtGen2 = e.Gen2Count;

                    double longLivedRatio = item.Gen2Ratio;
                    double typeGen0Pct = item.Gen0Pct;
                    double gen1SurvivalRate = mtGen0 > 0 ? mtGen1 / (double)mtGen0 : 0.0;
                    AllocationProfile typeProfile = typeGen0Pct > options.TransientClassificationThreshold
                        ? AllocationProfile.Transient
                        : longLivedRatio > options.LongLivedClassificationThreshold
                            ? AllocationProfile.Retained
                            : AllocationProfile.Mixed;

                    string typeName = (context.Runtime is not null && heapCache.TryGetTypeName(context.Runtime.Heap, mt, out var resolvedName)) ? resolvedName : $"MT:0x{mt:x}";

                    var entry = new TypeAllocationProfile(
                        typeName,
                        (int)Math.Min(int.MaxValue, mtGen0),
                        (int)Math.Min(int.MaxValue, mtGen1),
                        (int)Math.Min(int.MaxValue, mtGen2),
                        longLivedRatio,
                        typeProfile,
                        e.TotalSize,
                        gen1SurvivalRate);

                    if (gen1SurvivalRate > 0.5)
                        highGen1Survivors.Add((gen1SurvivalRate, entry));

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
            }

            // Honor emit flags: clear lists for disabled emissions
            if (!options.EmitTransient) transient.Clear();
            if (!options.EmitShortish) shortish.Clear();
            if (!options.EmitLongLived) longLived.Clear();

            // Sort high Gen1 survivors by survival rate (descending) and take top N
            highGen1Survivors.Sort((a, b) => b.Gen1SurvivalRate.CompareTo(a.Gen1SurvivalRate));
            var topGen1Survivors = highGen1Survivors
                .Take(options.TopTypeLimit)
                .Select(x => x.Profile)
                .ToList();

            progress?.Report(new AnalyzerProgressReport(100, "allocation pattern analysis complete"));

            return new AllocationPatternDomainResult(
                gen0CountPct, gen1CountPct, gen2CountPct, lohCountPct,
                gen0SizePct, gen1SizePct, gen2SizePct, lohSizePct,
                TotalManagedBytes: totalSize, Gen0Bytes: gen0Bytes, Gen1Bytes: gen1Bytes, Gen2Bytes: gen2Bytes, LohBytes: lohBytes,
                profile, pressure, promotionScore,
                transient, shortish, longLived, topGen1Survivors);
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
