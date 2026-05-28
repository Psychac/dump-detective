using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the T4 Metric Timeline <see cref="AnalyzerDetailSection"/>:
/// per-analyzer metric trend tables with visual timeline columns.
/// Uses <see cref="TableCell.LinkTarget"/> instead of the legacy __LINK__ token,
/// and <see cref="SparklineBlock"/> instead of the legacy __SPARK__ token.
/// </summary>
internal static class TrendMetricTimelineSectionBuilder
{
    public static AnalyzerDetailSection Build(
        TrendReportData trendData,
        IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        var blocks = new List<SectionBlock>();

        var regressionsByAnalyzer = trendData.Overall.ToDictionary(
            r => r.AnalyzerName, r => r.Regressions.Count, StringComparer.Ordinal);

        var orderedTimeline = trendData.Timeline
            .OrderByDescending(t => regressionsByAnalyzer.GetValueOrDefault(t.AnalyzerName))
            .ToList();

        foreach (AnalyzerMetricTimeline analyzerTimeline in orderedTimeline)
        {
            var rows = new List<TableRow>();

            foreach (MetricTimelinePoint point in analyzerTimeline.Points)
            {
                if (point.Values.All(double.IsNaN)) continue;

                double firstVal = point.Values.FirstOrDefault(v => !double.IsNaN(v));
                double lastVal  = point.Values.Last(v => !double.IsNaN(v));
                double delta    = lastVal - firstVal;
                double? deltaPercent = Math.Abs(firstVal) > double.Epsilon
                    ? delta * 100.0 / firstVal
                    : null;

                RegressionSeverity severity = ComputeSeverity(point.Direction, delta, deltaPercent);
                TrendClassification classification = ClassifyTrend(point.Direction, delta, severity);

                string status = classification switch
                {
                    TrendClassification.SevereRegression => "Severe regression",
                    TrendClassification.Regression       => "Regression",
                    TrendClassification.Improvement      => "Improvement",
                    _                                    => "Stable"
                };

                string deltaDisplay = delta == 0
                    ? "no change"
                    : $"{(delta >= 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(delta, point.Unit)}";
                string deltaPercentDisplay = deltaPercent.HasValue
                    ? $"{deltaPercent.Value:+0.0;-0.0}%"
                    : "—";
                string patternDisplay = BuildPatternLabel(point.Values);

                // Determine snapshot with largest adjacent change to link to
                int linkSnapshot = FindLargestChangeSnapshot(point.Values, snapshots.Count);

                string sparkPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    values = point.Values,
                    unit = point.Unit,
                    direction = point.Direction.ToString()
                });

                var rowCells = new List<TableCell>(6 + snapshots.Count)
                {
                    new TableCell(point.Key, LinkTarget: $"detail-{linkSnapshot}"),
                    new TableCell("__SPARK__" + sparkPayload)
                };

                for (int i = 0; i < snapshots.Count; i++)
                {
                    double value = i < point.Values.Count ? point.Values[i] : double.NaN;
                    string display = double.IsNaN(value)
                        ? "—"
                        : FormatHelper.FormatMetricValue(value, point.Unit);
                    rowCells.Add(new TableCell(display, ToSortableLong(value)));
                }

                rowCells.Add(new TableCell(deltaDisplay, ToSortableLong(delta)));
                rowCells.Add(new TableCell(deltaPercentDisplay, ToSortableLong(deltaPercent ?? 0)));
                rowCells.Add(new TableCell(patternDisplay));
                rowCells.Add(new TableCell(status, (long)severity));

                rows.Add(new TableRow(rowCells));
            }

            if (rows.Count == 0) continue;

            if (blocks.Count > 0)
                blocks.Add(new BlankBlock());

            var headers = new List<string>(6 + snapshots.Count)
            {
                "Metric",
                $"Trend ({snapshots.Count})"
            };

            for (int i = 0; i < snapshots.Count; i++)
            {
                headers.Add($"Dump {i + 1}");
            }

            headers.Add("Δ");
            headers.Add("Δ%");
            headers.Add("Pattern");
            headers.Add("Status");

            blocks.Add(new TableBlock(
                Caption: $"{analyzerTimeline.AnalyzerName}",
                Headers: headers,
                Rows: rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "TrendMetricTimeline",
            DisplayTitle: "Metric Timeline",
            SortOrder:    40,
            Blocks:       blocks,
            SectionId:    "T4",
            Domain:       "Trend");
    }

    private static int FindLargestChangeSnapshot(IReadOnlyList<double> values, int snapshotCount)
    {
        double best = 0.0;
        int bestIdx = snapshotCount - 1;
        double? prev = null;
        for (int si = 0; si < values.Count; si++)
        {
            double v = values[si];
            if (double.IsNaN(v)) continue;
            if (prev.HasValue)
            {
                double d = Math.Abs(v - prev.Value);
                if (d > best) { best = d; bestIdx = si; }
            }
            prev = v;
        }
        return Math.Min(bestIdx, snapshotCount - 1);
    }

    private static RegressionSeverity ComputeSeverity(MetricTrendDirection direction, double delta, double? deltaPercent)
    {
        bool isRegression = (direction == MetricTrendDirection.HigherIsWorse && delta > 0)
                         || (direction == MetricTrendDirection.LowerIsWorse  && delta < 0);
        if (!isRegression) return RegressionSeverity.None;
        if (!deltaPercent.HasValue) return RegressionSeverity.Moderate;
        double absPct = Math.Abs(deltaPercent.Value);
        return absPct switch
        {
            < 10.0 => RegressionSeverity.Minor,
            < 50.0 => RegressionSeverity.Moderate,
            _      => RegressionSeverity.Severe
        };
    }

    private static TrendClassification ClassifyTrend(MetricTrendDirection direction, double delta, RegressionSeverity severity)
    {
        bool isRegression  = (direction == MetricTrendDirection.HigherIsWorse && delta > 0)
                          || (direction == MetricTrendDirection.LowerIsWorse  && delta < 0);
        bool isImprovement = (direction == MetricTrendDirection.HigherIsWorse && delta < 0)
                          || (direction == MetricTrendDirection.LowerIsWorse  && delta > 0);

        if (isImprovement) return TrendClassification.Improvement;
        if (isRegression)  return severity == RegressionSeverity.Severe ? TrendClassification.SevereRegression : TrendClassification.Regression;
        return TrendClassification.Stable;
    }

    private static long ToSortableLong(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0L;

        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded > long.MaxValue) return long.MaxValue;
        if (rounded < long.MinValue) return long.MinValue;
        return (long)rounded;
    }

    private static string BuildPatternLabel(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return "Insufficient data";

        var deltas = new List<double>(values.Count - 1);
        for (int i = 1; i < values.Count; i++)
        {
            double prev = values[i - 1];
            double curr = values[i];
            if (double.IsNaN(prev) || double.IsNaN(curr))
                continue;
            deltas.Add(curr - prev);
        }

        if (deltas.Count == 0)
            return "Sparse";

        const double eps = 1e-9;
        int nonZero = deltas.Count(d => Math.Abs(d) > eps);
        if (nonZero == 0)
            return "Stable";

        double sumAbs = deltas.Sum(d => Math.Abs(d));
        double maxAbs = deltas.Max(d => Math.Abs(d));

        int signChanges = 0;
        int? lastSign = null;
        foreach (double d in deltas)
        {
            if (Math.Abs(d) <= eps) continue;
            int sign = Math.Sign(d);
            if (lastSign.HasValue && sign != lastSign.Value)
                signChanges++;
            lastSign = sign;
        }

        if (nonZero <= 2 && sumAbs > eps && maxAbs / sumAbs >= 0.7)
            return "Single jump";
        if (signChanges == 0)
            return "Gradual drift";
        if (signChanges >= 2)
            return "Oscillating";
        return "Volatile";
    }

}
