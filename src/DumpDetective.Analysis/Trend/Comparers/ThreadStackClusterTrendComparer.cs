using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class ThreadStackClusterTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Thread Stack Signature Clustering";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ThreadStackClusterDomainResult r) return [];

            double dominantClusterPercent = 0;
            if (r.TopClusters is { Count: > 0 } && r.AliveThreadCount > 0)
                dominantClusterPercent = r.TopClusters[0].Count * 100.0 / r.AliveThreadCount;

            return
            [
                new("cluster.alive.threads", null, r.AliveThreadCount, "threads", MetricTrendDirection.Neutral),
                new("cluster.unique", null, r.UniqueClusters, "clusters", MetricTrendDirection.Neutral),
                new("cluster.diversity.percent", null, r.DiversityPercent, "%", MetricTrendDirection.LowerIsWorse),
                new("cluster.dominant.percent", null, dominantClusterPercent, "%", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ThreadStackClusterDomainResult b || current is not ThreadStackClusterDomainResult c) return [];

            double bDominantPercent = 0;
            if (b.TopClusters is { Count: > 0 } && b.AliveThreadCount > 0)
                bDominantPercent = b.TopClusters[0].Count * 100.0 / b.AliveThreadCount;

            double cDominantPercent = 0;
            if (c.TopClusters is { Count: > 0 } && c.AliveThreadCount > 0)
                cDominantPercent = c.TopClusters[0].Count * 100.0 / c.AliveThreadCount;

            double top5StabilityPercent = ComputeTop5StabilityPercent(b.TopClusters, c.TopClusters);

            return
            [
                MetricDeltaHelper.Compute("cluster.diversity.percent", null, b.DiversityPercent, c.DiversityPercent, "%", MetricTrendDirection.LowerIsWorse),
                MetricDeltaHelper.Compute("cluster.unique", null, b.UniqueClusters, c.UniqueClusters, "clusters", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("cluster.dominant.percent", null, bDominantPercent, cDominantPercent, "%", MetricTrendDirection.HigherIsWorse),
                new MetricDelta("cluster.top5.stability.percent", null, 0, top5StabilityPercent, top5StabilityPercent, null, "%", MetricTrendDirection.LowerIsWorse)
            ];
        }

        /// <summary>
        /// Percentage of the baseline's top-5 cluster signatures that still appear in current's
        /// top-5. Cross-snapshot-only fact (no meaningful single-dump value), so it is computed
        /// here rather than surfaced from <see cref="ExtractMetrics"/>.
        /// </summary>
        private static double ComputeTop5StabilityPercent(
            IReadOnlyList<ThreadClusterSnapshot>? baselineTopClusters,
            IReadOnlyList<ThreadClusterSnapshot>? currentTopClusters)
        {
            if (baselineTopClusters is not { Count: > 0 }) return 0;

            int baselineTop5Count = Math.Min(5, baselineTopClusters.Count);
            var currentTop5Signatures = new HashSet<string>(StringComparer.Ordinal);
            if (currentTopClusters is { Count: > 0 })
            {
                int currentTop5Count = Math.Min(5, currentTopClusters.Count);
                for (int i = 0; i < currentTop5Count; i++)
                    currentTop5Signatures.Add(currentTopClusters[i].Signature);
            }

            int persistedCount = 0;
            for (int i = 0; i < baselineTop5Count; i++)
            {
                if (currentTop5Signatures.Contains(baselineTopClusters[i].Signature))
                    persistedCount++;
            }

            return persistedCount * 100.0 / baselineTop5Count;
        }
    }
}


