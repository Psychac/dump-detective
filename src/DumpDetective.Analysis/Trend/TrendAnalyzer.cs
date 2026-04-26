using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend
{
    internal sealed class TrendAnalyzer(IEnumerable<IAnalyzerTrendComparer> comparers)
    {
        private readonly IReadOnlyDictionary<string, IAnalyzerTrendComparer> _comparers =
            comparers.ToDictionary(c => c.AnalyzerName, StringComparer.Ordinal);

        public IReadOnlyList<AnalyzerTrendResult> CompareAll(AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            var results = new List<AnalyzerTrendResult>();
            foreach (var (analyzerName, baselineDomain) in baseline.DomainResults)
            {
                if (!current.DomainResults.TryGetValue(analyzerName, out var currentDomain))
                    continue;
                if (!_comparers.TryGetValue(analyzerName, out var comparer))
                    continue;
                var deltas = comparer.Compare(baselineDomain, currentDomain);
                if (deltas.Count > 0)
                    results.Add(new AnalyzerTrendResult(analyzerName, deltas));
            }
            return results;
        }

        public IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> CompareSeries(IReadOnlyList<AnalysisSnapshot> snapshots)
        {
            if (snapshots.Count < 2)
                return [];

            var steps = new List<IReadOnlyList<AnalyzerTrendResult>>(snapshots.Count - 1);
            for (int i = 1; i < snapshots.Count; i++)
                steps.Add(CompareAll(snapshots[i - 1], snapshots[i]));
            return steps;
        }

        /// <summary>
        /// For every analyzer, extracts each headline (unscoped) metric's value at every snapshot,
        /// producing a timeline that shows how each stat evolved across all N dumps.
        /// </summary>
        public IReadOnlyList<AnalyzerMetricTimeline> ExtractTimeline(IReadOnlyList<AnalysisSnapshot> snapshots)
        {
            var result = new List<AnalyzerMetricTimeline>(_comparers.Count);

            foreach (var (analyzerName, comparer) in _comparers)
            {
                // Build per-snapshot metric lookup: key â†’ value
                var snapshotLookups = new List<Dictionary<string, double>>(snapshots.Count);
                var allKeys = new Dictionary<string, (string Unit, MetricTrendDirection Direction)>(StringComparer.Ordinal);
                bool anyFound = false;

                foreach (var snapshot in snapshots)
                {
                    var lookup = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (snapshot.DomainResults.TryGetValue(analyzerName, out var domainResult))
                    {
                        anyFound = true;
                        foreach (var metric in comparer.ExtractMetrics(domainResult))
                        {
                            if (metric.Scope != null)
                                continue;  // skip per-type / per-exception scoped metrics
                            lookup[metric.Key] = metric.Value;
                            allKeys.TryAdd(metric.Key, (metric.Unit, metric.Direction));
                        }
                    }
                    snapshotLookups.Add(lookup);
                }

                if (!anyFound)
                    continue;

                var points = new List<MetricTimelinePoint>(allKeys.Count);
                foreach (var (key, (unit, direction)) in allKeys)
                {
                    var values = snapshotLookups
                        .Select(d => d.TryGetValue(key, out double v) ? v : double.NaN)
                        .ToList();
                    points.Add(new MetricTimelinePoint(key, unit, direction, values));
                }

                if (points.Count > 0)
                    result.Add(new AnalyzerMetricTimeline(analyzerName, points));
            }

            return result;
        }

            }
        }


