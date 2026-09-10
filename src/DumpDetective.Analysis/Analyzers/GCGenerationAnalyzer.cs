using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase 1 retyping pilot (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// the first analyzer retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>,
    /// sourcing everything through <see cref="IHeapTypeStatisticsQuery"/> (the <c>heap.types</c>
    /// capability) instead of a raw <c>ClrHeap</c>/<c>IHeapAnalysisCache</c>. Runs through the
    /// existing pipeline via <see cref="GCGenerationAnalyzerLegacyAdapter"/>, which also wraps
    /// <c>Core.Abstractions.IAnalyzer</c> for <c>BenchmarkSuite1</c>.
    /// </summary>
    public sealed class GCGenerationAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
    {
        public string Name => "GC Generation Analysis";
        public string Category => "GC";
        public IReadOnlyCollection<string> Tags => new[] { "gc", "generations", "memory", "performance" };
        public int Order => 10;
        public bool IsThreadSafe => false;

        public AnalyzerDomainResult? LastResult { get; private set; }

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IHeapTypeStatisticsQuery statsQuery = context.HeapTypeStatistics
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");
            GCGenerationAnalysisOptions options = context.AnalyzerOptions as GCGenerationAnalysisOptions ?? new GCGenerationAnalysisOptions();

            LastResult = Analyze(statsQuery, options, context.Progress);
            return ValueTask.CompletedTask;
        }

        private static GCGenerationDomainResult Analyze(
            IHeapTypeStatisticsQuery statsQuery,
            GCGenerationAnalysisOptions options,
            IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, statsQuery.HasExactGenerationData ? "reading type aggregates" : "reading type statistics (fallback)"));

            IReadOnlyDictionary<string, HeapTypeStatistics> stats = statsQuery.GetTypeStatistics();

            return statsQuery.HasExactGenerationData
                ? BuildFromIndex(statsQuery, stats, options)
                : BuildFromTypeStatistics(stats, options);
        }

        // ── Fast path: exact per-generation type statistics ────────────────────────

        private static GCGenerationDomainResult BuildFromIndex(
            IHeapTypeStatisticsQuery statsQuery,
            IReadOnlyDictionary<string, HeapTypeStatistics> stats,
            GCGenerationAnalysisOptions options)
        {
            ulong lohBytes = 0;
            long totalObjects = 0, lohObjects = 0;
            long gen0Objects = 0, gen1Objects = 0, gen2Objects = 0;
            bool anyGen2Bytes = false;

            var lohCandidates = new List<(string Name, HeapTypeStatistics Entry)>(stats.Count);
            var genCandidates = new List<(string Name, HeapTypeStatistics Entry)>(stats.Count);

            foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
            {
                HeapTypeStatistics e = kv.Value;
                gen0Objects += e.Gen0Count;
                gen1Objects += e.Gen1Count;
                gen2Objects += e.Gen2Count;
                lohBytes += e.LohSize;
                lohObjects += e.LohCount;
                totalObjects += e.InstanceCount;

                if (e.Gen2TotalSize > 0)
                    anyGen2Bytes = true;

                if (e.LohCount > 0)
                    lohCandidates.Add((kv.Key, e));

                genCandidates.Add((kv.Key, e));
            }

            long accountedGen = gen0Objects + gen1Objects + gen2Objects;

            // Exact gen bytes from segment metadata.
            (ulong gen0Bytes, ulong gen1Bytes, ulong gen2Bytes) = statsQuery.GetExactGenerationByteTotals();

            ulong totalManagedBytes = gen0Bytes + gen1Bytes + gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // Top LOH types, ranked by LOH size.
            lohCandidates.Sort(static (a, b) => b.Entry.LohSize.CompareTo(a.Entry.LohSize));
            var topLohTypes = new List<TypeSnapshot>(lohCandidates.Count);
            foreach ((string name, HeapTypeStatistics e) in lohCandidates)
                topLohTypes.Add(new TypeSnapshot(name, (int)Math.Min(int.MaxValue, e.LohCount), e.LohSize, e.LohSize));

            // Per-type generation profiles, ranked by exact Gen2 bytes so memory-heavy accumulators
            // surface ahead of small high-count types. Falls back to instance count when no entry
            // carries Gen2 bytes (heap indices written before Gen2TotalSize existed).
            List<TypeGenerationProfile> profiles = [];
            long finalizableGen2Count = 0;
            ulong finalizableGen2Bytes = 0;
            if (accountedGen > 0)
            {
                if (anyGen2Bytes)
                    genCandidates.Sort(static (a, b) => b.Entry.Gen2TotalSize.CompareTo(a.Entry.Gen2TotalSize));
                else
                    genCandidates.Sort(static (a, b) => b.Entry.InstanceCount.CompareTo(a.Entry.InstanceCount));

                profiles = new List<TypeGenerationProfile>(genCandidates.Count);
                foreach ((string name, HeapTypeStatistics e) in genCandidates)
                {
                    profiles.Add(new TypeGenerationProfile(
                        name, e.Gen0Count, e.Gen1Count, e.Gen2Count,
                        (int)Math.Min(int.MaxValue, e.LohCount),
                        e.TotalSize,
                        e.Gen2TotalSize,
                        e.IsFinalizableType));

                    if (e.IsFinalizableType)
                    {
                        finalizableGen2Count += e.Gen2Count;
                        finalizableGen2Bytes += e.Gen2TotalSize;
                    }
                }
            }

            // POH detection for .NET 5+ — always zero today; see AnalyzerHelpers.ComputePohMetrics'
            // own remarks (POH objects aren't in TypeAggregates, detection needs runtime-level
            // segment inspection that hasn't been built). Inlined rather than routed through the
            // capability surface since it never varies with the input.
            const ulong pohBytes = 0;
            const long pohObjects = 0;

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
                PohBytes: pohBytes,
                PohObjects: pohObjects,
                Gen2Pct: gen2Pct,
                PerTypeGenerationProfiles: profiles,
                GenBytesAreApproximate: false,
                FallbackMode: false,
                LohThresholdPercent: options.LohThresholdPercent,
                Gen0PressureThresholdPercent: options.Gen0PressureThresholdPercent,
                PohThresholdPercent: options.PohThresholdPercent,
                FinalizableGen2Count: finalizableGen2Count,
                FinalizableGen2Bytes: finalizableGen2Bytes);
        }

        // ── Slow / fallback path (no per-generation index) ─────────────────────────

        private static GCGenerationDomainResult BuildFromTypeStatistics(
            IReadOnlyDictionary<string, HeapTypeStatistics> typeStats,
            GCGenerationAnalysisOptions options)
        {
            ulong lohBytes = 0;
            int totalObjects = 0, lohObjects = 0, gen2Objects = 0;
            ulong gen2Bytes = 0;

            foreach (HeapTypeStatistics stat in typeStats.Values)
            {
                lohBytes += stat.LohSize;
                totalObjects += (int)stat.InstanceCount;
                lohObjects += (int)stat.LohCount;
                int nonLoh = Math.Max(0, (int)stat.InstanceCount - (int)stat.LohCount);
                ulong nonLohBytes = stat.TotalSize >= stat.LohSize ? stat.TotalSize - stat.LohSize : 0;
                gen2Objects += nonLoh;
                gen2Bytes += nonLohBytes;
            }

            ulong totalManagedBytes = gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // No LINQ in hot paths — explicit sort + loop.
            var lohList = new List<(string Name, HeapTypeStatistics Stat)>(capacity: 32);
            foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStats)
                if (kv.Value.LohCount > 0) lohList.Add((kv.Key, kv.Value));
            lohList.Sort(static (a, b) => b.Stat.LohSize.CompareTo(a.Stat.LohSize));
            var topLohTypes = new List<TypeSnapshot>(lohList.Count);
            foreach ((string name, HeapTypeStatistics stat) in lohList)
                topLohTypes.Add(new TypeSnapshot(name, (int)stat.LohCount, stat.LohSize, stat.LohSize));

            return new GCGenerationDomainResult(
                Gen0Bytes: 0, Gen0Objects: 0,
                Gen1Bytes: 0, Gen1Objects: 0,
                gen2Bytes, gen2Objects,
                lohBytes, lohPct,
                totalObjects, lohObjects,
                topLohTypes,
                PohBytes: 0,
                PohObjects: 0,
                Gen2Pct: gen2Pct,
                PerTypeGenerationProfiles: [],
                GenBytesAreApproximate: true,
                FallbackMode: true,
                LohThresholdPercent: options.LohThresholdPercent,
                Gen0PressureThresholdPercent: options.Gen0PressureThresholdPercent,
                PohThresholdPercent: options.PohThresholdPercent);
        }

        public void Dispose() { }
    }
}
