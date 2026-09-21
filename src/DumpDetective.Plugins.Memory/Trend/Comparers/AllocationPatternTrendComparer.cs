using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class AllocationPatternTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Allocation Pattern Analysis";

        // The 8 gen/loh count/size percentages and finalizable.type.count/.bytes retired onto
        // ObservationTrendDiffer, backed by gc.allocation-composition — see
        // ObservationDifferAnalyzerOwnership and
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md Bucket B.
        // alloc.gc.pressure/alloc.promotion.pressure stay hand-written permanently — both are
        // AllocationPatternPressureClassifier composite scores, banned from raw Observations by the
        // purity rule (Bucket C genuine exceptions).
        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AllocationPatternDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("alloc.gc.pressure",        null, (double)AllocationPatternPressureClassifier.ClassifyPressure(r),    "level", MetricTrendDirection.HigherIsWorse),
                new("alloc.promotion.pressure", null, AllocationPatternPressureClassifier.ComputePromotionPressureScore(r),"score", MetricTrendDirection.HigherIsWorse),
            };

            if (r.LohSizeBands is not null)
            {
                foreach (var band in r.LohSizeBands)
                    metrics.Add(new("alloc.loh.band.bytes", band.RangeLabel, band.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            foreach (TypeAllocationProfile p in r.TopTransientTypes)
                metrics.Add(new("alloc.transient.type.gen0.count", p.TypeName, p.Gen0Count, "objects", MetricTrendDirection.Neutral));
            foreach (TypeAllocationProfile p in r.TopShortishTypes)
                metrics.Add(new("alloc.shortish.type.gen1.count", p.TypeName, p.Gen1Count, "objects", MetricTrendDirection.Neutral));
            foreach (TypeAllocationProfile p in r.TopLongLivedTypes)
            {
                metrics.Add(new("alloc.longlived.type.gen2.count", p.TypeName, p.Gen2Count, "objects", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("alloc.longlived.type.ratio", p.TypeName, p.LongLivedRatio, "ratio", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AllocationPatternDomainResult b || current is not AllocationPatternDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("alloc.gc.pressure",        null, (double)AllocationPatternPressureClassifier.ClassifyPressure(b),    (double)AllocationPatternPressureClassifier.ClassifyPressure(c),    "level", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.promotion.pressure", null, AllocationPatternPressureClassifier.ComputePromotionPressureScore(b),AllocationPatternPressureClassifier.ComputePromotionPressureScore(c),"score", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


