using DumpDetective.Models;

namespace DumpDetective.Services
{
    internal sealed class TrendAnalyzer
    {
        public IReadOnlyList<TrendStepComparison> CompareSeries(IReadOnlyList<AnalysisSnapshot> snapshots)
        {
            if (snapshots.Count < 2)
            {
                return [];
            }

            var steps = new List<TrendStepComparison>(snapshots.Count - 1);
            for (int i = 1; i < snapshots.Count; i++)
            {
                AnalysisSnapshot baseline = snapshots[i - 1];
                AnalysisSnapshot current = snapshots[i];
                TrendComparisonResult comparison = Compare(baseline, current);
                steps.Add(new TrendStepComparison(baseline, current, comparison));
            }

            return steps;
        }

        public TrendComparisonResult Compare(AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            var baselineByKey = baseline.Findings
                .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var currentByKey = current.Findings
                .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var allKeys = new HashSet<string>(baselineByKey.Keys, StringComparer.Ordinal);
            allKeys.UnionWith(currentByKey.Keys);

            var deltas = new List<FindingTrendDelta>(allKeys.Count);
            foreach (string key in allKeys.OrderBy(k => k, StringComparer.Ordinal))
            {
                bool hasBaseline = baselineByKey.TryGetValue(key, out InsightFinding? baselineFinding);
                bool hasCurrent = currentByKey.TryGetValue(key, out InsightFinding? currentFinding);

                FindingTrendState state = hasBaseline switch
                {
                    true when hasCurrent => FindingTrendState.Persistent,
                    true => FindingTrendState.Resolved,
                    _ => FindingTrendState.New
                };

                bool hasMetricComparison = false;
                double? metricDelta = null;
                double? metricDeltaPercent = null;
                string? metricUnit = null;

                if (hasBaseline && hasCurrent
                    && baselineFinding?.MetricValue is double baselineMetric
                    && currentFinding?.MetricValue is double currentMetric
                    && string.Equals(baselineFinding.MetricUnit, currentFinding.MetricUnit, StringComparison.OrdinalIgnoreCase))
                {
                    hasMetricComparison = true;
                    metricDelta = currentMetric - baselineMetric;
                    metricUnit = currentFinding.MetricUnit;

                    if (Math.Abs(baselineMetric) > double.Epsilon)
                    {
                        metricDeltaPercent = metricDelta.Value * 100.0 / baselineMetric;
                    }
                }

                deltas.Add(new FindingTrendDelta(
                    key,
                    state,
                    currentFinding,
                    baselineFinding,
                    hasMetricComparison,
                    metricDelta,
                    metricDeltaPercent,
                    metricUnit));
            }

            return new TrendComparisonResult(
                deltas,
                CurrentCriticalCount: current.Findings.Count(f => f.Severity == FindingSeverity.Critical),
                BaselineCriticalCount: baseline.Findings.Count(f => f.Severity == FindingSeverity.Critical),
                CurrentWarningCount: current.Findings.Count(f => f.Severity == FindingSeverity.Warning),
                BaselineWarningCount: baseline.Findings.Count(f => f.Severity == FindingSeverity.Warning));
        }
    }
}
