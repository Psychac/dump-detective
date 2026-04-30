namespace DumpDetective.Core.Models;
internal enum MetricTrendDirection
{
HigherIsWorse,
LowerIsWorse,
Neutral
}

internal enum RegressionSeverity
{
None,
Minor,
Moderate,
Severe
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

/// <summary>Growth rate as a percentage: (current − baseline) / baseline × 100. Zero when baseline is zero.</summary>
public double GrowthRatePercent => DeltaPercent ?? 0.0;

/// <summary>Severity of this delta if it represents a regression.</summary>
public RegressionSeverity Severity
{
    get
    {
        if (!IsRegression) return RegressionSeverity.None;
        if (!DeltaPercent.HasValue) return RegressionSeverity.Moderate;   // baseline was 0
        double abs = Math.Abs(DeltaPercent.Value);
        return abs switch
        {
            < 10.0 => RegressionSeverity.Minor,
            < 50.0 => RegressionSeverity.Moderate,
            _      => RegressionSeverity.Severe
        };
    }
}
}

internal interface IAnalyzerTrendComparer
{
string AnalyzerName { get; }
IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result);
IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current);
}

/// <summary>A type that appeared in or significantly grew within current leak results relative to baseline.</summary>
internal sealed record NewLeakSignal(
    string TypeName,
    double BaselineBytes,
    double CurrentBytes,
    string Source);

internal sealed record AnalyzerTrendResult(
string AnalyzerName,
IReadOnlyList<MetricDelta> Deltas)
{
public IReadOnlyList<NewLeakSignal> NewLeakSignals { get; init; } = [];
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
