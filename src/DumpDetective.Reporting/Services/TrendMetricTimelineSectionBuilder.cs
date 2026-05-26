using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the T4 Metric Timeline <see cref="AnalyzerDetailSection"/>:
/// per-analyzer metric trend tables with step-by-step delta sub-sections.
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
        blocks.Add(new HeadingBlock($"Metric Timeline ({snapshots.Count} snapshots)"));

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
                    TrendClassification.SevereRegression => "⚠⚠ Severe",
                    TrendClassification.Regression       => "⚠ Regression",
                    TrendClassification.Improvement      => "✅ Improvement",
                    _                                    => "— Stable"
                };

                string pctStr = deltaPercent.HasValue
                    ? $" ({(deltaPercent.Value >= 0 ? "+" : string.Empty)}{deltaPercent.Value:F1}%)"
                    : string.Empty;
                string deltaDisplay = delta == 0
                    ? "no change"
                    : $"{(delta >= 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(delta, point.Unit)}{pctStr}";

                // Determine snapshot with largest adjacent change to link to
                int linkSnapshot = FindLargestChangeSnapshot(point.Values, snapshots.Count);

                rows.Add(new TableRow([
                    new TableCell(point.Key, LinkTarget: $"detail-{linkSnapshot}"),
                    new TableCell($"__SPARKREF__{analyzerTimeline.AnalyzerName}.{point.Key}"),
                    new TableCell(deltaDisplay, delta == 0 ? 0L : (long)Math.Round(Math.Abs(delta))),
                    new TableCell(status)
                ]));
            }

            if (rows.Count == 0) continue;

            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock($"[{analyzerTimeline.AnalyzerName}]", 1));

            // Sparkline blocks paired with each metric row (insert before table)
            foreach (MetricTimelinePoint point in analyzerTimeline.Points)
            {
                if (point.Values.All(double.IsNaN)) continue;
                string direction = point.Direction switch
                {
                    MetricTrendDirection.HigherIsWorse => "HigherIsWorse",
                    MetricTrendDirection.LowerIsWorse  => "LowerIsWorse",
                    _                                  => "Neutral"
                };
                blocks.Add(new SparklineBlock(
                    MetricKey: $"{analyzerTimeline.AnalyzerName}.{point.Key}",
                    Unit:      point.Unit,
                    Values:    point.Values,
                    Direction: direction));
            }

            blocks.Add(new TableBlock(
                Caption: $"{analyzerTimeline.AnalyzerName} metric timeline",
                Headers: ["Metric", $"Trend ({snapshots.Count} snapshots)", "Δ", "Status"],
                Rows: rows));

            // Collapsible step-by-step delta sub-section
            if (trendData.Steps.Count > 0)
            {
                blocks.Add(new CollapsibleSectionBeginBlock($"{analyzerTimeline.AnalyzerName} — Step-by-Step Δ"));

                var stepRows = new List<TableRow>();
                for (int stepIdx = 0; stepIdx < trendData.Steps.Count; stepIdx++)
                {
                    IReadOnlyList<AnalyzerTrendResult> stepResults = trendData.Steps[stepIdx];
                    AnalyzerTrendResult? analyzerStep = null;
                    foreach (AnalyzerTrendResult r in stepResults)
                    {
                        if (string.Equals(r.AnalyzerName, analyzerTimeline.AnalyzerName, StringComparison.Ordinal))
                        { analyzerStep = r; break; }
                    }
                    if (analyzerStep is null) continue;

                    string fromDump = snapshots.Count > stepIdx     ? Path.GetFileName(snapshots[stepIdx].DumpPath)     : $"S{stepIdx + 1}";
                    string toDump   = snapshots.Count > stepIdx + 1 ? Path.GetFileName(snapshots[stepIdx + 1].DumpPath) : $"S{stepIdx + 2}";

                    foreach (MetricDelta d in analyzerStep.Deltas)
                    {
                        if (d.Delta == 0) continue;

                        string pctStr2 = d.DeltaPercent.HasValue
                            ? $" ({(d.DeltaPercent.Value >= 0 ? "+" : string.Empty)}{d.DeltaPercent.Value:F1}%)"
                            : string.Empty;
                        string deltaStr = $"{(d.Delta >= 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(d.Delta, d.Unit)}{pctStr2}";

                        string sev = d.Severity switch
                        {
                            RegressionSeverity.Severe   => "Severe",
                            RegressionSeverity.Moderate => "Moderate",
                            RegressionSeverity.Minor    => "Minor",
                            _                           => "—"
                        };

                        stepRows.Add(new TableRow([
                            new TableCell((stepIdx + 1).ToString()),
                            new TableCell(fromDump),
                            new TableCell(toDump),
                            new TableCell(d.Key),
                            new TableCell(deltaStr),
                            new TableCell(d.DeltaPercent.HasValue ? $"{d.DeltaPercent.Value:+0.0;-0.0}%" : "—"),
                            new TableCell(sev)
                        ]));
                    }
                }

                if (stepRows.Count > 0)
                {
                    blocks.Add(new TableBlock(
                        Caption: "Step Deltas",
                        Headers: ["Step", "From Dump", "To Dump", "Metric", "Δ", "Δ%", "Severity"],
                        Rows: stepRows));
                }

                blocks.Add(new CollapsibleSectionEndBlock());
            }
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
}
