using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Analysis.Trend;

/// <summary>
/// Generic pairwise trend diff over headline (whole-run, <see cref="Observation.Subjects"/> empty)
/// Observations — Phase 5 step 4's replacement for the headline half of the ~30 hand-written
/// <c>IAnalyzerTrendComparer.Compare</c> implementations. Scoped (per-type/per-entity) Observations
/// are entirely out of scope here; see
/// docs/refactor/modularity/phase-5-observations-synthesis.md ## Work step 4 for why.
/// </summary>
/// <remarks>
/// Dispatches on each measure's own <see cref="MeasureSemantics"/> for the one genuinely different
/// behavior this pairwise differ implements today: <see cref="MeasureSemantics.Ratio"/> measures
/// diff as a point difference, never "percent of a percent" — this is a real, demonstrated bug fix
/// (<c>AllocationPatternTrendComparer.Compare</c>'s <c>alloc.gen2.count.pct</c>, whose backing
/// Observation measure is already correctly <c>Ratio</c>-typed, currently gets diffed with the same
/// percent-change math as an <c>Absolute</c> byte count — 10%→15% reports "+50%" instead of "+5
/// points"). <see cref="MeasureSemantics.Rate"/>/<see cref="MeasureSemantics.Count"/>/
/// <see cref="MeasureSemantics.Duration"/>'s richer treatment (rate-of-rate acceleration,
/// growth-curve fit across ≥ 3 anchors, distribution shift) needs a real temporal series, not two
/// snapshots — not something a pairwise differ can honestly compute. Those three fall back to the
/// same delta+percent math as <see cref="MeasureSemantics.Absolute"/> here; the richer, series-aware
/// treatment is a separate, not-yet-built differ over <c>TrendAnalyzer.ExtractTimeline</c>'s shape,
/// not faked in this pairwise one.
/// </remarks>
internal static class ObservationTrendDiffer
{
    public static IReadOnlyList<MetricDelta> Compare(IReadOnlyList<Observation> baseline, IReadOnlyList<Observation> current)
    {
        Dictionary<(string ObservationType, string MeasureKey), Measure> baselineMeasures = CollectHeadlineMeasures(baseline);
        Dictionary<(string ObservationType, string MeasureKey), Measure> currentMeasures = CollectHeadlineMeasures(current);

        var deltas = new List<MetricDelta>();

        foreach (var (key, baselineMeasure) in baselineMeasures)
        {
            // A measure key baseline saw but current didn't re-emit isn't diffable without
            // fabricating a value on one side — skip rather than assume it dropped to zero.
            if (!currentMeasures.TryGetValue(key, out Measure currentMeasure))
                continue;

            // No declared direction is "not yet classified," not "neutral" — see
            // MeasureDirectionRegistry's own remarks. Nothing to render a verdict with, so skip.
            if (!MeasureDirectionRegistry.TryGetDirection(key.ObservationType, key.MeasureKey, out MeasureDirection direction))
                continue;

            // ContextDependent is a real, deliberate value (declared, not absent) meaning "worse"
            // can't be expressed as a static per-measure fact — same as an unclassified measure,
            // this differ emits nothing rather than guess; a context-aware exception rule is the
            // right home for it, not this generic differ.
            if (!TryToLegacyDirection(direction, out MetricTrendDirection legacyDirection))
                continue;

            string metricKey = $"{key.ObservationType}::{key.MeasureKey}";
            string unit = ToUnitLabel(baselineMeasure.Unit);

            deltas.Add(baselineMeasure.Semantics == MeasureSemantics.Ratio
                ? ComputeRatioDelta(metricKey, baselineMeasure.Value, currentMeasure.Value, unit, legacyDirection)
                : MetricDeltaHelper.Compute(metricKey, null, baselineMeasure.Value, currentMeasure.Value, unit, legacyDirection));
        }

        return deltas;
    }

    /// <summary>Ratio measures diff as a point difference — never <c>MetricDeltaHelper.Compute</c>'s
    /// percent-of-baseline math, which is exactly the "percent of a percent" trap
    /// <see cref="MeasureSemantics.Ratio"/>'s own doc comment warns against.</summary>
    private static MetricDelta ComputeRatioDelta(string key, double baseline, double current, string unit, MetricTrendDirection direction) =>
        new(key, null, baseline, current, current - baseline, DeltaPercent: null, unit, direction);

    private static Dictionary<(string ObservationType, string MeasureKey), Measure> CollectHeadlineMeasures(
        IReadOnlyList<Observation> observations)
    {
        var result = new Dictionary<(string, string), Measure>();

        foreach (Observation observation in observations)
        {
            if (observation.Subjects.Count != 0)
                continue; // scoped — out of this differ's scope entirely.

            foreach ((string measureKey, Measure measure) in observation.Measures)
                result[(observation.ObservationType, measureKey)] = measure;
        }

        return result;
    }

    private static bool TryToLegacyDirection(MeasureDirection direction, out MetricTrendDirection legacyDirection)
    {
        switch (direction)
        {
            case MeasureDirection.HigherIsWorse:
                legacyDirection = MetricTrendDirection.HigherIsWorse;
                return true;
            case MeasureDirection.LowerIsWorse:
                legacyDirection = MetricTrendDirection.LowerIsWorse;
                return true;
            case MeasureDirection.Neutral:
                legacyDirection = MetricTrendDirection.Neutral;
                return true;
            default:
                legacyDirection = default;
                return false;
        }
    }

    private static string ToUnitLabel(MeasureUnit unit) => unit switch
    {
        MeasureUnit.Count => "count",
        MeasureUnit.Bytes => "bytes",
        MeasureUnit.Percent => "%",
        MeasureUnit.Ratio => "ratio",
        MeasureUnit.Milliseconds => "ms",
        MeasureUnit.PerSecond => "per-second",
        _ => "custom",
    };
}
