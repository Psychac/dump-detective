using DumpDetective.Core.Models;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Analysis.Trend
{
    internal sealed class TrendAnalyzer(IEnumerable<IAnalyzerTrendComparer> comparers)
    {
        private const string RetentionAnalyzerName = "Dominator Analysis";
        private const string LegacyLeakAnalyzerName = "Memory Leak Analysis";

        private readonly IReadOnlyDictionary<string, IAnalyzerTrendComparer> _comparers =
            comparers.ToDictionary(c => c.AnalyzerName, StringComparer.Ordinal);

        public IReadOnlyList<AnalyzerTrendResult> CompareAll(
            AnalysisSnapshot baseline,
            AnalysisSnapshot current,
            IReadOnlyList<Observation>? baselineObservations = null,
            IReadOnlyList<Observation>? currentObservations = null)
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

            if (baselineObservations is { Count: > 0 } && currentObservations is { Count: > 0 })
                results.AddRange(CompareObservations(baselineObservations, currentObservations));

            return MergeByAnalyzer(results);
        }

        // A comparer still owning an analyzer's scoped metrics and the observation differ owning its headline metrics both
        // produce a result under the same analyzer name; consumers key by name, so they must be one result.
        private static IReadOnlyList<AnalyzerTrendResult> MergeByAnalyzer(List<AnalyzerTrendResult> results)
        {
            var merged = new List<AnalyzerTrendResult>(results.Count);
            var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (AnalyzerTrendResult result in results)
            {
                if (!indexByName.TryGetValue(result.AnalyzerName, out int index))
                {
                    indexByName[result.AnalyzerName] = merged.Count;
                    merged.Add(result);
                    continue;
                }

                merged[index] = new AnalyzerTrendResult(result.AnalyzerName, [.. merged[index].Deltas, .. result.Deltas]);
            }

            return merged;
        }

        /// <summary>The observation-differ-driven half of <see cref="CompareAll"/> — the 8 fully
        /// retired comparers (see <see cref="ObservationDifferAnalyzerOwnership"/>) have no
        /// registered <see cref="IAnalyzerTrendComparer"/> at all, so their trend deltas come from
        /// here instead: run the generic <see cref="ObservationTrendDiffer"/> once over every
        /// headline Observation, then bucket its flat <c>(ObservationType, measure key)</c>-keyed
        /// output back into per-analyzer <see cref="AnalyzerTrendResult"/>s.</summary>
        private static IEnumerable<AnalyzerTrendResult> CompareObservations(
            IReadOnlyList<Observation> baselineObservations, IReadOnlyList<Observation> currentObservations)
        {
            IReadOnlyList<MetricDelta> deltas = ObservationTrendDiffer.Compare(baselineObservations, currentObservations);

            var byAnalyzer = new Dictionary<string, List<MetricDelta>>(StringComparer.Ordinal);
            foreach (MetricDelta delta in deltas)
            {
                if (!TryResolveAnalyzerName(delta.Key, out string? analyzerName))
                    continue;

                if (!byAnalyzer.TryGetValue(analyzerName, out List<MetricDelta>? bucket))
                    byAnalyzer[analyzerName] = bucket = [];

                bucket.Add(delta);
            }

            foreach (var (analyzerName, analyzerDeltas) in byAnalyzer)
                yield return new AnalyzerTrendResult(analyzerName, analyzerDeltas);
        }

        /// <summary><see cref="ObservationTrendDiffer"/> keys every delta
        /// <c>$"{ObservationType}::{MeasureKey}"</c> — split that back apart to look the
        /// ObservationType up in <see cref="ObservationDifferAnalyzerOwnership"/>.</summary>
        private static bool TryResolveAnalyzerName(string metricDeltaKey, out string? analyzerName)
        {
            int separatorIndex = metricDeltaKey.IndexOf("::", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                analyzerName = null;
                return false;
            }

            string observationType = metricDeltaKey[..separatorIndex];
            return ObservationDifferAnalyzerOwnership.ObservationTypeToAnalyzerName.TryGetValue(observationType, out analyzerName);
        }

        public IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> CompareSeries(
            IReadOnlyList<AnalysisSnapshot> snapshots,
            IReadOnlyList<IReadOnlyList<Observation>>? observationsPerSnapshot = null)
        {
            if (snapshots.Count < 2)
                return Array.Empty<IReadOnlyList<AnalyzerTrendResult>>();

            var steps = new List<IReadOnlyList<AnalyzerTrendResult>>(snapshots.Count - 1);
            for (int i = 1; i < snapshots.Count; i++)
            {
                steps.Add(CompareAll(
                    snapshots[i - 1], snapshots[i],
                    observationsPerSnapshot?[i - 1], observationsPerSnapshot?[i]));
            }

            return steps;
        }

        /// <summary>
        /// For every analyzer, extracts each headline (unscoped) metric's value at snapshot,
        /// producing timeline that shows how stat evolved across all N dumps. Includes both
        /// comparer-driven analyzers and observation-differ-driven ones (see
        /// <see cref="ObservationDifferAnalyzerOwnership"/>) when <paramref name="observationsPerSnapshot"/>
        /// is supplied.
        /// </summary>
        public IReadOnlyList<AnalyzerMetricTimeline> ExtractTimeline(
            IReadOnlyList<AnalysisSnapshot> snapshots,
            IReadOnlyList<IReadOnlyList<Observation>>? observationsPerSnapshot = null)
        {
            List<AnalyzerMetricTimeline> result = ExtractTimelineCore(snapshots, includeScoped: false).ToList();
            if (observationsPerSnapshot is not null)
            {
                foreach (AnalyzerMetricTimeline fromObservations in ExtractObservationTimeline(snapshots.Count, observationsPerSnapshot))
                {
                    int existing = result.FindIndex(t => string.Equals(t.AnalyzerName, fromObservations.AnalyzerName, StringComparison.Ordinal));
                    if (existing < 0)
                        result.Add(fromObservations);
                    else
                        result[existing] = new AnalyzerMetricTimeline(fromObservations.AnalyzerName, [.. result[existing].Points, .. fromObservations.Points]);
                }
            }

            return result;
        }

        /// <summary>
        /// For every analyzer, extracts scoped (entity-level) metrics as table timelines. Enables
        /// comparisons like type/category rows across all snapshots. Observation-differ-driven
        /// analyzers have no scoped output — this differ is headline-only by design (see
        /// docs/refactor/modularity/phase-5-observations-synthesis.md ## Work step 4) — so this
        /// method is unaffected by that migration.
        /// </summary>
        public IReadOnlyList<AnalyzerMetricTimeline> ExtractScopedTimeline(IReadOnlyList<AnalysisSnapshot> snapshots) =>
            ExtractTimelineCore(snapshots, includeScoped: true);

        private IReadOnlyList<AnalyzerMetricTimeline> ExtractTimelineCore(
            IReadOnlyList<AnalysisSnapshot> snapshots,
            bool includeScoped)
        {
            var result = new List<AnalyzerMetricTimeline>(_comparers.Count);

            foreach (var (analyzerName, comparer) in _comparers)
            {
                var snapshotLookups = new List<Dictionary<string, double>>(snapshots.Count);
                var allKeys = new Dictionary<string, (string Key, string? Scope, string Unit, MetricTrendDirection Direction)>(StringComparer.Ordinal);
                bool anyFound = false;

                foreach (var snapshot in snapshots)
                {
                    var lookup = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (snapshot.DomainResults.TryGetValue(analyzerName, out var domainResult))
                    {
                        anyFound = true;
                        foreach (AnalyzerMetric metric in comparer.ExtractMetrics(domainResult))
                        {
                            bool isScoped = metric.Scope is not null;
                            if (includeScoped != isScoped)
                                continue;

                            string timelineKey = includeScoped
                                ? metric.Key + "" + metric.Scope
                                : metric.Key;

                            lookup[timelineKey] = metric.Value;
                            allKeys.TryAdd(timelineKey, (metric.Key, metric.Scope, metric.Unit, metric.Direction));
                        }
                    }

                    snapshotLookups.Add(lookup);
                }

                if (!anyFound)
                    continue;

                var points = new List<MetricTimelinePoint>(allKeys.Count);
                foreach (var (timelineKey, metricIdentity) in allKeys)
                {
                    var values = snapshotLookups
                        .Select(d => d.TryGetValue(timelineKey, out double v) ? v : double.NaN)
                        .ToArray();

                    points.Add(new MetricTimelinePoint(
                        Key: metricIdentity.Key,
                        Unit: metricIdentity.Unit,
                        Direction: metricIdentity.Direction,
                        Values: values,
                        Scope: metricIdentity.Scope));
                }

                if (points.Count > 0)
                    result.Add(new AnalyzerMetricTimeline(analyzerName, points));
            }

            return result;
        }

        /// <summary>The observation-differ-driven half of <see cref="ExtractTimeline"/> — same job
        /// as <see cref="ExtractTimelineCore"/> but sourced from headline Observations across all N
        /// snapshots instead of a comparer's <c>ExtractMetrics</c>.</summary>
        private static IEnumerable<AnalyzerMetricTimeline> ExtractObservationTimeline(
            int snapshotCount, IReadOnlyList<IReadOnlyList<Observation>> observationsPerSnapshot)
        {
            var byAnalyzer = new Dictionary<string, Dictionary<(string ObservationType, string MeasureKey), (double[] Values, MeasureUnit Unit)>>(StringComparer.Ordinal);

            for (int snapshotIndex = 0; snapshotIndex < observationsPerSnapshot.Count; snapshotIndex++)
            {
                foreach (Observation observation in observationsPerSnapshot[snapshotIndex])
                {
                    if (observation.Subjects.Count != 0)
                        continue; // scoped — this differ is headline-only.

                    if (!ObservationDifferAnalyzerOwnership.ObservationTypeToAnalyzerName.TryGetValue(
                            observation.ObservationType, out string? analyzerName))
                    {
                        continue;
                    }

                    if (!byAnalyzer.TryGetValue(analyzerName, out var keyMap))
                        byAnalyzer[analyzerName] = keyMap = [];

                    foreach ((string measureKey, Measure measure) in observation.Measures)
                    {
                        if (!MeasureDirectionRegistry.TryGetDirection(observation.ObservationType, measureKey, out _))
                            continue; // unclassified — same skip rule as ObservationTrendDiffer.Compare.

                        var compositeKey = (observation.ObservationType, measureKey);
                        if (!keyMap.TryGetValue(compositeKey, out (double[] Values, MeasureUnit Unit) entry))
                        {
                            double[] values = new double[snapshotCount];
                            Array.Fill(values, double.NaN);
                            entry = (values, measure.Unit);
                            keyMap[compositeKey] = entry;
                        }

                        entry.Values[snapshotIndex] = measure.Value;
                    }
                }
            }

            foreach (var (analyzerName, keyMap) in byAnalyzer)
            {
                var points = new List<MetricTimelinePoint>(keyMap.Count);
                foreach (var ((observationType, measureKey), (values, unit)) in keyMap)
                {
                    if (!MeasureDirectionRegistry.TryGetDirection(observationType, measureKey, out MeasureDirection direction) ||
                        !ObservationTrendDiffer.TryToLegacyDirection(direction, out MetricTrendDirection legacyDirection))
                    {
                        continue;
                    }

                    points.Add(new MetricTimelinePoint(
                        Key: $"{observationType}::{measureKey}",
                        Unit: ObservationTrendDiffer.ToUnitLabel(unit),
                        Direction: legacyDirection,
                        Values: values));
                }

                if (points.Count > 0)
                    yield return new AnalyzerMetricTimeline(analyzerName, points);
            }
        }
    }
}
