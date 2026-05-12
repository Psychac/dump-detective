using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend
{
    internal sealed class TrendAnalyzer(IEnumerable<IAnalyzerTrendComparer> comparers)
    {
        private const string RetentionAnalyzerName = "Retention Analysis";
        private const string LegacyLeakAnalyzerName = "Memory Leak Analysis";

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
                {
                    // Backward compatibility for snapshots produced before analyzer naming was updated.
                    if (!string.Equals(analyzerName, LegacyLeakAnalyzerName, StringComparison.Ordinal) ||
                        !_comparers.TryGetValue(RetentionAnalyzerName, out comparer))
                    {
                        continue;
                    }
                }

                string outputAnalyzerName = string.Equals(analyzerName, LegacyLeakAnalyzerName, StringComparison.Ordinal)
                    ? RetentionAnalyzerName
                    : analyzerName;

                IReadOnlyList<MetricDelta> deltas = comparer.Compare(baselineDomain, currentDomain);
                if (deltas.Count == 0)
                    continue;

                results.Add(new AnalyzerTrendResult(outputAnalyzerName, deltas));
            }

            return results;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<NewLeakSignal>> ComputeNewLeakSignals(AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            return ComputeNewLeakSignalsCore(baseline, current);
        }

        private static Dictionary<string, IReadOnlyList<NewLeakSignal>> ComputeNewLeakSignalsCore(
            AnalysisSnapshot baseline,
            AnalysisSnapshot current)
        {
            var result = new Dictionary<string, IReadOnlyList<NewLeakSignal>>(StringComparer.Ordinal);

            // Retention Analysis (legacy alias: Memory Leak Analysis) — compare TopHighlyReferencedObjects by type.
            if (TryGetRetentionResult(baseline, out RetentionDomainResult bLeak) &&
                TryGetRetentionResult(current, out RetentionDomainResult cLeak))
            {
                var baselineByType = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (HighlyReferencedObjectSnapshot obj in bLeak.TopHighlyReferencedObjects ?? [])
                {
                    baselineByType.TryGetValue(obj.TypeName, out double prev);
                    baselineByType[obj.TypeName] = prev + obj.Size;
                }

                var currentByType = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (HighlyReferencedObjectSnapshot obj in cLeak.TopHighlyReferencedObjects ?? [])
                {
                    currentByType.TryGetValue(obj.TypeName, out double prev);
                    currentByType[obj.TypeName] = prev + obj.Size;
                }

                var signals = new List<NewLeakSignal>();
                foreach (var (typeName, currentBytes) in currentByType)
                {
                    baselineByType.TryGetValue(typeName, out double baseBytes);
                    if (currentBytes > baseBytes * 1.5 + 1024)
                        signals.Add(new NewLeakSignal(typeName, baseBytes, currentBytes, "RetentionAnalyzer"));
                }

                if (signals.Count > 0)
                    result[RetentionAnalyzerName] = signals;
            }

            // Static Root Leak Detection — compare TopRootsByRetainedBytes
            if (baseline.DomainResults.TryGetValue("Static Root Leak Detection", out var bStaticRaw) &&
                current.DomainResults.TryGetValue("Static Root Leak Detection", out var cStaticRaw) &&
                bStaticRaw is StaticRootDomainResult bStatic && cStaticRaw is StaticRootDomainResult cStatic)
            {
                var baselineByName = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (NameBytesEntry entry in bStatic.TopRootsByRetainedBytes ?? [])
                    baselineByName[entry.Name] = entry.Bytes;

                var signals = new List<NewLeakSignal>();
                foreach (NameBytesEntry entry in cStatic.TopRootsByRetainedBytes ?? [])
                {
                    double currentBytes = entry.Bytes;
                    baselineByName.TryGetValue(entry.Name, out double baseBytes);
                    if (currentBytes > baseBytes * 1.5 + 1024)
                        signals.Add(new NewLeakSignal(entry.Name, baseBytes, currentBytes, "StaticRootLeakDetector"));
                }

                if (signals.Count > 0)
                    result["Static Root Leak Detection"] = signals;
            }

            return result;
        }

        private static bool TryGetRetentionResult(AnalysisSnapshot snapshot, out RetentionDomainResult result)
        {
            if (snapshot.DomainResults.TryGetValue(RetentionAnalyzerName, out AnalyzerDomainResult? retentionRaw) &&
                retentionRaw is RetentionDomainResult retention)
            {
                result = retention;
                return true;
            }

            if (snapshot.DomainResults.TryGetValue(LegacyLeakAnalyzerName, out AnalyzerDomainResult? leakRaw) &&
                leakRaw is RetentionDomainResult leak)
            {
                result = leak;
                return true;
            }

            result = null!;
            return false;
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
                var snapshotLookups = new List<Dictionary<string, double>>(snapshots.Count);
                var allKeys = new Dictionary<string, (string Unit, MetricTrendDirection Direction)>(StringComparer.Ordinal);
                bool anyFound = false;

                foreach (var snapshot in snapshots)
                {
                    var lookup = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (snapshot.DomainResults.TryGetValue(analyzerName, out var domainResult))
                    {
                        anyFound = true;
                        foreach (AnalyzerMetric metric in comparer.ExtractMetrics(domainResult))
                        {
                            if (metric.Scope is not null)
                                continue;

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
