using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend
{
    internal sealed class TrendAnalyzer(IEnumerable<IAnalyzerTrendComparer> comparers)
    {
        private readonly IReadOnlyDictionary<string, IAnalyzerTrendComparer> _comparers =
            comparers.ToDictionary(c => c.AnalyzerName, StringComparer.Ordinal);

        public IReadOnlyList<AnalyzerTrendResult> CompareAll(AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            Dictionary<string, IReadOnlyList<NewLeakSignal>> leakSignalsByAnalyzer =
                ComputeNewLeakSignals(baseline, current);

            var results = new List<AnalyzerTrendResult>();
            foreach (var (analyzerName, baselineDomain) in baseline.DomainResults)
            {
                if (!current.DomainResults.TryGetValue(analyzerName, out var currentDomain))
                    continue;
                if (!_comparers.TryGetValue(analyzerName, out var comparer))
                    continue;
                var deltas = comparer.Compare(baselineDomain, currentDomain);
                if (deltas.Count > 0)
                {
                    leakSignalsByAnalyzer.TryGetValue(analyzerName, out var signals);
                    results.Add(new AnalyzerTrendResult(analyzerName, deltas)
                    {
                        NewLeakSignals = signals ?? []
                    });
                }
            }
            return results;
        }

        private static Dictionary<string, IReadOnlyList<NewLeakSignal>> ComputeNewLeakSignals(
            AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            var result = new Dictionary<string, IReadOnlyList<NewLeakSignal>>(StringComparer.Ordinal);

            // Memory Leak Analysis — compare TopHighlyReferencedObjects by type
            if (baseline.DomainResults.TryGetValue("Memory Leak Analysis", out var bLeakRaw) &&
                current.DomainResults.TryGetValue("Memory Leak Analysis", out var cLeakRaw) &&
                bLeakRaw is MemoryLeakDomainResult bLeak && cLeakRaw is MemoryLeakDomainResult cLeak)
            {
                var baselineByType = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (HighlyReferencedObjectSnapshot obj in bLeak.TopHighlyReferencedObjects ?? [])
                {
                    baselineByType.TryGetValue(obj.TypeName, out double prev);
                    baselineByType[obj.TypeName] = prev + (double)obj.Size;
                }

                var currentByType = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (HighlyReferencedObjectSnapshot obj in cLeak.TopHighlyReferencedObjects ?? [])
                {
                    currentByType.TryGetValue(obj.TypeName, out double prev);
                    currentByType[obj.TypeName] = prev + (double)obj.Size;
                }

                var signals = new List<NewLeakSignal>();
                foreach (var (typeName, currentBytes) in currentByType)
                {
                    baselineByType.TryGetValue(typeName, out double baseBytes);
                    if (currentBytes > baseBytes * 1.5 + 1024)
                        signals.Add(new NewLeakSignal(typeName, baseBytes, currentBytes, "MemoryLeakAnalyzer"));
                }
                if (signals.Count > 0)
                    result["Memory Leak Analysis"] = signals;
            }

            // Static Root Leak Detection — compare TopRootsByRetainedBytes
            if (baseline.DomainResults.TryGetValue("Static Root Leak Detection", out var bStaticRaw) &&
                current.DomainResults.TryGetValue("Static Root Leak Detection", out var cStaticRaw) &&
                bStaticRaw is StaticRootDomainResult bStatic && cStaticRaw is StaticRootDomainResult cStatic)
            {
                var baselineByName = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (NameBytesEntry entry in bStatic.TopRootsByRetainedBytes ?? [])
                    baselineByName[entry.Name] = (double)entry.Bytes;

                var signals = new List<NewLeakSignal>();
                foreach (NameBytesEntry entry in cStatic.TopRootsByRetainedBytes ?? [])
                {
                    double currentBytes = (double)entry.Bytes;
                    baselineByName.TryGetValue(entry.Name, out double baseBytes);
                    if (currentBytes > baseBytes * 1.5 + 1024)
                        signals.Add(new NewLeakSignal(entry.Name, baseBytes, currentBytes, "StaticRootLeakDetector"));
                }
                if (signals.Count > 0)
                    result["Static Root Leak Detection"] = signals;
            }

            return result;
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


