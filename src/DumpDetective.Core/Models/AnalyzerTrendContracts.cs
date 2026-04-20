namespace DumpDetective.Core.Models;
internal enum MetricTrendDirection
{
HigherIsWorse,
LowerIsWorse,
Neutral
}

internal sealed record AnalyzerMetric(
string Key,
string? Scope,
double Value,
string Unit,
MetricTrendDirection Direction);

internal sealed record MetricDelta(
string Key,
string? Scope,
double Baseline,
double Current,
double Delta,
double? DeltaPercent,
string Unit,
MetricTrendDirection Direction)
{
public bool IsRegression => Direction switch
{
    MetricTrendDirection.HigherIsWorse => Delta > 0,
    MetricTrendDirection.LowerIsWorse  => Delta < 0,
    _                                  => false
};

public bool IsImprovement => Direction switch
{
    MetricTrendDirection.HigherIsWorse => Delta < 0,
    MetricTrendDirection.LowerIsWorse  => Delta > 0,
    _                                  => false
};
}

internal interface IAnalyzerTrendComparer
{
string AnalyzerName { get; }
IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result);
IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current);
}

internal sealed record AnalyzerTrendResult(
string AnalyzerName,
IReadOnlyList<MetricDelta> Deltas)
{
public IReadOnlyList<MetricDelta> Regressions =>
    Deltas.Where(d => d.IsRegression).OrderByDescending(d => Math.Abs(d.Delta)).ToList();

public IReadOnlyList<MetricDelta> Improvements =>
    Deltas.Where(d => d.IsImprovement).OrderByDescending(d => Math.Abs(d.Delta)).ToList();

public IReadOnlyList<MetricDelta> NeutralDeltas =>
    Deltas.Where(d => d.Direction == MetricTrendDirection.Neutral && d.Delta != 0)
          .OrderByDescending(d => Math.Abs(d.Delta)).ToList();

public bool HasRegressions => Deltas.Any(d => d.IsRegression);
}

/// <summary>
/// A single metric's value at every snapshot in the trend sequence (one value per dump, in order).
/// </summary>
internal sealed record MetricTimelinePoint(
string Key,
string Unit,
MetricTrendDirection Direction,
IReadOnlyList<double> Values);  // indexed by snapshot position; double.NaN when not available

/// <summary>
/// All headline (unscoped) metric timelines for one analyzer across all trend dumps.
/// </summary>
internal sealed record AnalyzerMetricTimeline(
string AnalyzerName,
IReadOnlyList<MetricTimelinePoint> Points);
