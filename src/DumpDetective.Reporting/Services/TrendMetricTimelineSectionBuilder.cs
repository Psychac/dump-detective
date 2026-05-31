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

        var headlineByAnalyzer = trendData.Timeline
            .ToDictionary(t => t.AnalyzerName, StringComparer.Ordinal);

        var scopedByAnalyzer = trendData.ScopedTimeline
            .ToDictionary(t => t.AnalyzerName, StringComparer.Ordinal);

        var analyzers = headlineByAnalyzer.Keys
            .Union(scopedByAnalyzer.Keys, StringComparer.Ordinal)
            .OrderByDescending(a => regressionsByAnalyzer.GetValueOrDefault(a))
            .ToArray();

        if (analyzers.Length == 0)
        {
            return new AnalyzerDetailSection(
                AnalyzerName: "TrendMetricTimeline",
                DisplayTitle: "Metric Timeline",
                SortOrder: 40,
            Blocks: Array.Empty<SectionBlock>(),
                SectionId: "T4",
                Domain: "Trend");
        }

        var domainBuckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string analyzerName in analyzers)
        {
            string domain = SectionIdDomainMap.GetDomain(analyzerName);
            if (string.IsNullOrWhiteSpace(domain))
                domain = "Other";

            if (!domainBuckets.TryGetValue(domain, out List<string>? list))
            {
                list = new List<string>();
                domainBuckets[domain] = list;
            }

            list.Add(analyzerName);
        }

        List<string> orderedDomains = new();
        foreach (string d in SectionIdDomainMap.DomainsInOrder)
        {
            if (domainBuckets.ContainsKey(d))
                orderedDomains.Add(d);
        }

        foreach (string d in domainBuckets.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!orderedDomains.Contains(d, StringComparer.Ordinal))
                orderedDomains.Add(d);
        }

        bool firstDomain = true;
        foreach (string domain in orderedDomains)
        {
            string[] domainAnalyzers = domainBuckets[domain]
                .OrderByDescending(a => regressionsByAnalyzer.GetValueOrDefault(a))
                .ToArray();

            if (domainAnalyzers.Length == 0)
                continue;

            if (!firstDomain)
                blocks.Add(new BlankBlock());
            firstDomain = false;

            blocks.Add(new HeadingBlock($"Domain: {domain}", 1));

            foreach (string analyzerName in domainAnalyzers)
            {
                blocks.Add(new BlankBlock());
                blocks.Add(new HeadingBlock(analyzerName, 2));

                if (headlineByAnalyzer.TryGetValue(analyzerName, out AnalyzerMetricTimeline? headlineTimeline))
                {
                    blocks.Add(new TextBlock("Metric Trends"));

                    List<TableRow> rows = BuildRows(
                        analyzerName,
                        headlineTimeline.Points,
                        snapshots,
                        includeScopeLabel: false,
                        scopedMode: false);

                    if (rows.Count > 0)
                    {
                        AddTimelineTableBlock(
                            blocks,
                            snapshots,
                            $"{analyzerName} metric timeline",
                            rows,
                            firstColumnHeader: "Metric");
                    }
                }

                if (scopedByAnalyzer.TryGetValue(analyzerName, out AnalyzerMetricTimeline? scopedTimeline))
                {
                    blocks.Add(new TextBlock("Comparison Trends"));

                    var scopedMetricGroups = scopedTimeline.Points
                        .Where(p => ShouldIncludeScopedMetric(analyzerName, p.Key))
                        .GroupBy(p => p.Key, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .ToArray();

                    foreach (var metricGroup in scopedMetricGroups)
                    {
                        List<TableRow> rows = BuildRows(
                            analyzerName,
                            metricGroup.ToArray(),
                            snapshots,
                            includeScopeLabel: true,
                            scopedMode: true);

                        if (rows.Count == 0)
                            continue;

                        AddTimelineTableBlock(
                            blocks,
                            snapshots,
                            BuildMetricTableCaption(analyzerName, metricGroup.Key, scoped: true),
                            rows,
                            firstColumnHeader: BuildScopedFirstColumnHeader(metricGroup.Key));
                    }
                }
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

    private static string BuildMetricTableCaption(string analyzerName, string metricKey, bool scoped)
    {
        if (string.Equals(analyzerName, "Memory Analysis", StringComparison.Ordinal))
        {
            if (string.Equals(metricKey, "type.bytes", StringComparison.Ordinal))
                return scoped ? "Memory Analysis top types by bytes" : "Memory Analysis metric trend: type.bytes";
            if (string.Equals(metricKey, "type.count", StringComparison.Ordinal))
                return scoped ? "Memory Analysis top types by count" : "Memory Analysis metric trend: type.count";
        }

        return scoped
            ? $"{analyzerName} comparison trend: {metricKey}"
            : $"{analyzerName} metric trend: {metricKey}";
    }

    private static bool ShouldIncludeScopedMetric(string analyzerName, string metricKey)
    {
        if (string.Equals(analyzerName, "Memory Analysis", StringComparison.Ordinal))
        {
            return string.Equals(metricKey, "type.bytes", StringComparison.Ordinal)
                || string.Equals(metricKey, "type.count", StringComparison.Ordinal);
        }

        return true;
    }

    private static string BuildScopedFirstColumnHeader(string metricKey)
    {
        if (metricKey.Contains(".type.", StringComparison.Ordinal))
            return "Type";
        if (metricKey.Contains(".category.", StringComparison.Ordinal))
            return "Category";
        if (metricKey.Contains(".kind.", StringComparison.Ordinal))
            return "Kind";
        if (metricKey.Contains(".module.", StringComparison.Ordinal))
            return "Module";
        if (metricKey.Contains(".source.", StringComparison.Ordinal))
            return "Source";
        if (metricKey.Contains(".target.", StringComparison.Ordinal))
            return "Target";
        if (metricKey.Contains(".edge.", StringComparison.Ordinal))
            return "Edge";
        if (metricKey.Contains(".byname.", StringComparison.Ordinal))
            return "Name";

        return "Entity";
    }

    private static void AddTimelineTableBlock(
        List<SectionBlock> blocks,
        IReadOnlyList<AnalysisSnapshot> snapshots,
        string caption,
        IReadOnlyList<TableRow> rows,
        string firstColumnHeader)
    {
        if (blocks.Count > 0)
            blocks.Add(new BlankBlock());

        var headers = new List<string>(6 + snapshots.Count)
        {
            firstColumnHeader,
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
            Caption: caption,
            Headers: headers,
            Rows: rows));
    }

    private static List<TableRow> BuildRows(
        string analyzerName,
        IReadOnlyList<MetricTimelinePoint> points,
        IReadOnlyList<AnalysisSnapshot> snapshots,
        bool includeScopeLabel,
        bool scopedMode)
    {
        var rows = new List<(TableRow Row, long SortValue)>();

        foreach (MetricTimelinePoint point in points)
        {
            if (point.Values.All(double.IsNaN))
                continue;

            double firstVal = point.Values.FirstOrDefault(v => !double.IsNaN(v));
            double lastVal = point.Values.Last(v => !double.IsNaN(v));
            double delta = lastVal - firstVal;
            double? deltaPercent = Math.Abs(firstVal) > double.Epsilon
                ? delta * 100.0 / firstVal
                : null;

            RegressionSeverity severity = ComputeSeverity(point.Direction, delta, deltaPercent);
            TrendClassification classification = ClassifyTrend(point.Direction, delta, severity);

            string status = classification switch
            {
                TrendClassification.SevereRegression => "Severe regression",
                TrendClassification.Regression => "Regression",
                TrendClassification.Improvement => "Improvement",
                _ => "Stable"
            };

            string deltaDisplay = delta == 0
                ? "no change"
                : $"{(delta >= 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(delta, point.Unit)}";
            string deltaPercentDisplay = deltaPercent.HasValue
                ? $"{deltaPercent.Value:+0.0;-0.0}%"
                : "—";
            string patternDisplay = BuildPatternLabel(point.Values);

            int linkSnapshot = FindLargestChangeSnapshot(point.Values, snapshots.Count);

            string sparkPayload = SerializeSparklinePayload(point.Values, point.Unit, point.Direction.ToString());

            string metricLabel = includeScopeLabel
                ? (string.IsNullOrWhiteSpace(point.Scope) ? point.Key : point.Scope)
                : point.Key;

            var rowCells = new List<TableCell>(6 + snapshots.Count)
            {
                new TableCell(metricLabel, LinkTarget: $"detail-{linkSnapshot}"),
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

            long sortValue = scopedMode
                ? BuildScopedSortValue(analyzerName, point, severity, delta)
                : ((long)severity * 1_000_000_000L) + ToSortableLong(Math.Abs(delta));
            rows.Add((new TableRow(rowCells), sortValue));
        }

        return rows
            .OrderByDescending(r => r.SortValue)
            .Select(r => r.Row)
            .ToList();
    }

    private static long BuildScopedSortValue(
        string analyzerName,
        MetricTimelinePoint point,
        RegressionSeverity severity,
        double delta)
    {
        long profileRank = GetScopedProfileRank(analyzerName, point);
        double latest = point.Values.LastOrDefault(v => !double.IsNaN(v));
        long latestMagnitude = ToSortableLong(Math.Abs(latest));
        long deltaMagnitude = ToSortableLong(Math.Abs(delta));

        // Sort priority: severity > analyzer-specific profile > latest magnitude > delta magnitude.
        return ((long)severity * 1_000_000_000_000L)
             + (profileRank * 1_000_000_000L)
             + (latestMagnitude * 1_000L)
             + deltaMagnitude;
    }

    private static long GetScopedProfileRank(string analyzerName, MetricTimelinePoint point)
    {
        string key = point.Key;

        if (analyzerName == "Memory Analysis")
        {
            if (key == "type.bytes") return 500;
            if (key == "type.count") return 450;
            if (key.StartsWith("memory.bucket.", StringComparison.Ordinal)) return 400;
            return 150;
        }

        if (analyzerName == "Hang Analysis")
        {
            if (key == "hang.wait.category") return 500;
            return 200;
        }

        if (analyzerName == "Thread Analysis")
        {
            if (key == "thread.wait.category") return 500;
            return 200;
        }

        if (analyzerName == "Crash Analysis")
        {
            if (key == "crash.exception.type") return 500;
            return 200;
        }

        return 100;
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

    private static string SerializeSparklinePayload(
        IReadOnlyList<double> values,
        string unit,
        string direction)
    {
        double?[] safeValues = values
            .Select(static v => double.IsFinite(v) ? (double?)v : null)
            .ToArray();

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            values = safeValues,
            unit,
            direction
        });
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
