using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class GCGenerationAnalyzer : IAnalyzer
    {
        public string Name => "GC Generation Analysis";
        public string Category => "GC";
        public IReadOnlyCollection<string> Tags => new[] { "gc", "generations", "memory", "performance" };
        public int Order => 10;
        public bool IsThreadSafe => false;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GCGenerationAnalysisOptions options = context.AnalysisOptions.GCGenerationAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, new GCGenerationAnalysisOptions(), progress: null);
        }

        private static AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, GCGenerationAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "reading type aggregates"));

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIdx))
                return BuildFromIndex(heap, heapCache, heapIdx, options, progress);

            // No heap index available — fall back to type statistics (gen split will be Gen2-centric).
            progress?.Report(new(0, "reading type statistics (fallback)"));
            return BuildFromTypeStatistics(heap, cache.GetOrBuildTypeStatistics(heap), options);
        }

        // ── Fast path: TypeAggregates ──────────────────────────────────────────────

        private static GCGenerationDomainResult BuildFromIndex(
            ClrHeap heap,
            HeapAnalysisCache heapCache,
            HeapIndexBuildResult heapIdx,
            GCGenerationAnalysisOptions options,
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
                lohBytes += e.LohSize;
                lohObjects += e.LohCount;
                totalObjects += e.Count;

                if (e.LohCount > 0)
                    lohCandidates.Add((kv.Key, e));

                genCandidates.Add((kv.Key, e));
            }

            long nonLohTotal = totalObjects - lohObjects;
            long accountedGen = gen0Objects + gen1Objects + gen2Objects;

            // Exact gen bytes from segment metadata.
            AnalyzerHelpers.ComputeExactGenBytes(heap, out ulong gen0Bytes, out ulong gen1Bytes, out ulong gen2Bytes);

            ulong totalManagedBytes = gen0Bytes + gen1Bytes + gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalManagedBytes;
            double gen0Pct = totalObjects == 0 ? 0.0 : gen0Objects * 100.0 / totalObjects;
            double gen2Pct = totalObjects == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // Top LOH types — resolve names only for top N.
            lohCandidates.Sort(static (a, b) => b.Entry.LohSize.CompareTo(a.Entry.LohSize));
            int lohTake = Math.Min(options.TopLohTypeLimit, lohCandidates.Count);
            var topLohTypes = new List<TypeSnapshot>(lohTake);
            for (int i = 0; i < lohTake; i++)
            {
                (ulong mt, TypeAggregateIndexEntry e) = lohCandidates[i];
                string name = heap.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";
                topLohTypes.Add(new TypeSnapshot(name, (int)Math.Min(int.MaxValue, e.LohCount), e.LohSize, e.LohSize));
            }

            // Per-type generation profiles.
            List<TypeGenerationProfile> profiles = [];
            if (accountedGen > 0)
            {
                genCandidates.Sort(static (a, b) => b.Entry.Count.CompareTo(a.Entry.Count));
                int genTake = Math.Min(options.TopGenProfileLimit, genCandidates.Count);
                profiles = new List<TypeGenerationProfile>(genTake);
                for (int i = 0; i < genTake; i++)
                {
                    (ulong mt, TypeAggregateIndexEntry e) = genCandidates[i];
                    string name = heap.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";
                    profiles.Add(new TypeGenerationProfile(
                        name, e.Gen0Count, e.Gen1Count, e.Gen2Count,
                        (int)Math.Min(int.MaxValue, e.LohCount),
                        e.TotalSize,
                        (e.Flags & TypeAggregateFlags.IsFinalizableType) != 0));
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
                profiles,
                GenBytesAreApproximate: false,
                FallbackMode: false,
                LohThresholdPercent: options.LohThresholdPercent,
                Gen0PressureThresholdPercent: options.Gen0PressureThresholdPercent);
        }

        // ── Slow / fallback path (no heap index) ──────────────────────────────────

        private static GCGenerationDomainResult BuildFromTypeStatistics(
            ClrHeap heap,
            Dictionary<string, CachedTypeStatistics> typeStats,
            GCGenerationAnalysisOptions options)
        {
            ulong lohBytes = 0;
            int totalObjects = 0, lohObjects = 0, gen2Objects = 0;
            ulong gen2Bytes = 0;

            foreach (CachedTypeStatistics stat in typeStats.Values)
            {
                lohBytes += stat.LohSize;
                totalObjects += stat.Count;
                lohObjects += stat.LohCount;
                int nonLoh = Math.Max(0, stat.Count - stat.LohCount);
                ulong nonLohBytes = stat.TotalSize >= stat.LohSize ? stat.TotalSize - stat.LohSize : 0;
                gen2Objects += nonLoh;
                gen2Bytes += nonLohBytes;
            }

            ulong totalManagedBytes = gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // No LINQ in hot paths — explicit sort + loop.
            var lohList = new List<CachedTypeStatistics>(capacity: 32);
            foreach (CachedTypeStatistics stat in typeStats.Values)
                if (stat.LohCount > 0) lohList.Add(stat);
            lohList.Sort(static (a, b) => b.LohSize.CompareTo(a.LohSize));
            int lohTake = Math.Min(options.TopLohTypeLimit, lohList.Count);
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
                PerTypeGenerationProfiles: [],
                GenBytesAreApproximate: true,
                FallbackMode: true,
                LohThresholdPercent: options.LohThresholdPercent,
                Gen0PressureThresholdPercent: options.Gen0PressureThresholdPercent);
        }

        public void Dispose() { }
    }
}
