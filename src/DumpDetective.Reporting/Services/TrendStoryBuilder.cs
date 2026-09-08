using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the narrative <see cref="TrendStoryRecord"/> summarizing heap evolution across a trend run:
/// first major regression, largest inflection point, worsening domains, and cross-domain coupling hints.
/// </summary>
internal static class TrendStoryBuilder
{
    public static TrendStoryRecord? Build(TrendReportData trendData)
    {
        if (trendData.Snapshots.Count < 2)
            return null;

        int snapshotCount = trendData.Snapshots.Count;
        var inflectionScores = new double[snapshotCount];
        var regressionCandidates = new List<TrendStoryCandidate>(32);
        var metricReferences = new List<string>(8);

        void AnalyzeTimeline(string analyzerName, IReadOnlyList<MetricTimelinePoint> points)
        {
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                MetricTimelinePoint point = points[pointIndex];
                IReadOnlyList<double> values = point.Values;

                for (int snapshotIndex = 1; snapshotIndex < values.Count; snapshotIndex++)
                {
                    double previous = values[snapshotIndex - 1];
                    double current = values[snapshotIndex];
                    if (!double.IsFinite(previous) || !double.IsFinite(current))
                        continue;

                    double delta = current - previous;
                    if (Math.Abs(delta) <= double.Epsilon)
                        continue;

                    double magnitude = point.Direction != MetricTrendDirection.Neutral && Math.Abs(previous) > double.Epsilon
                        ? Math.Abs(delta * 100.0 / previous)
                        : Math.Abs(delta);

                    inflectionScores[snapshotIndex] += magnitude;

                    if (!IsRegression(point.Direction, delta))
                        continue;

                    regressionCandidates.Add(new TrendStoryCandidate(
                        AnalyzerName: analyzerName,
                        MetricKey: point.Key,
                        Scope: point.Scope,
                        Unit: point.Unit,
                        SnapshotIndex: snapshotIndex,
                        Previous: previous,
                        Current: current,
                        Delta: delta,
                        Magnitude: magnitude));
                }
            }
        }

        for (int i = 0; i < trendData.Timeline.Count; i++)
            AnalyzeTimeline(trendData.Timeline[i].AnalyzerName, trendData.Timeline[i].Points);
        for (int i = 0; i < trendData.ScopedTimeline.Count; i++)
            AnalyzeTimeline(trendData.ScopedTimeline[i].AnalyzerName, trendData.ScopedTimeline[i].Points);

        TrendStoryCandidate? firstRegression = regressionCandidates
            .OrderBy(c => c.SnapshotIndex)
            .ThenByDescending(c => c.Magnitude)
            .ThenByDescending(c => Math.Abs(c.Delta))
            .FirstOrDefault();

        int largestInflectionIndex = 0;
        double largestInflectionScore = 0.0;
        for (int i = 1; i < inflectionScores.Length; i++)
        {
            if (inflectionScores[i] <= largestInflectionScore)
                continue;

            largestInflectionScore = inflectionScores[i];
            largestInflectionIndex = i;
        }

        TrendStoryCandidate? inflectionCandidate = null;
        if (largestInflectionIndex > 0)
        {
            inflectionCandidate = regressionCandidates
                .Where(c => c.SnapshotIndex == largestInflectionIndex)
                .OrderByDescending(c => c.Magnitude)
                .ThenByDescending(c => Math.Abs(c.Delta))
                .FirstOrDefault();
        }

        var domainStats = new Dictionary<string, TrendStoryDomainStat>(StringComparer.Ordinal);
        for (int i = 0; i < trendData.Overall.Count; i++)
        {
            AnalyzerTrendResult result = trendData.Overall[i];
            string domain = SectionIdDomainMap.GetDomain(result.AnalyzerName);
            if (string.IsNullOrWhiteSpace(domain))
                domain = "Other";

            foreach (MetricDelta delta in result.Regressions)
            {
                double magnitude = delta.DeltaPercent.HasValue ? Math.Abs(delta.DeltaPercent.Value) : Math.Abs(delta.Delta);
                if (!domainStats.TryGetValue(domain, out TrendStoryDomainStat? stat))
                {
                    stat = new TrendStoryDomainStat(domain);
                    domainStats[domain] = stat;
                }

                stat.Count++;
                stat.Magnitude += magnitude;
            }
        }

        var worseningDomains = domainStats.Values
            .OrderByDescending(s => s.Count)
            .ThenByDescending(s => s.Magnitude)
            .ThenBy(s => s.Domain, StringComparer.Ordinal)
            .Take(3)
            .ToArray();

        var couplingHints = new List<string>(3);
        bool hasMemory = worseningDomains.Any(d => string.Equals(d.Domain, "Memory", StringComparison.Ordinal));
        bool hasGc = worseningDomains.Any(d => string.Equals(d.Domain, "GC", StringComparison.Ordinal));
        bool hasRuntime = worseningDomains.Any(d => string.Equals(d.Domain, "Runtime", StringComparison.Ordinal));

        if (hasMemory && hasGc)
        {
            couplingHints.Add("Memory and GC both worsen in the same window, which usually points to retention or LOH pressure rather than one isolated analyzer.");
        }

        if (trendData.NewLeakSignalsByAnalyzer.Values.Any(signals => signals.Count > 0))
        {
            couplingHints.Add("Leak signals line up with the same regression window, so shared roots or type churn are worth checking first.");
        }

        if (hasRuntime && hasMemory)
        {
            couplingHints.Add("Runtime and Memory move together here, so module churn or type growth may be feeding the same pressure source.");
        }

        metricReferences.Clear();
        AddMetricReference(metricReferences, firstRegression);
        AddMetricReference(metricReferences, inflectionCandidate);
        for (int i = 0; i < regressionCandidates.Count && metricReferences.Count < 5; i++)
            AddMetricReference(metricReferences, regressionCandidates[i]);

        string summary;
        if (firstRegression is not null)
        {
            summary = $"The first clear regression appears in {FormatSnapshotLabel(trendData.Snapshots[firstRegression.SnapshotIndex])}, and the worsening is concentrated in {FormatAnalyzerMetricReference(firstRegression)}.";
        }
        else if (largestInflectionIndex > 0)
        {
            summary = $"No single metric crossed into regression immediately, but {FormatSnapshotLabel(trendData.Snapshots[largestInflectionIndex])} is the biggest inflection point in the window.";
        }
        else
        {
            summary = "The trend is mostly flat, with no major regression spike standing out in the compared window.";
        }

        string firstRegressionText = firstRegression is not null
            ? $"{FormatSnapshotLabel(trendData.Snapshots[firstRegression.SnapshotIndex])}: {FormatAnalyzerMetricReference(firstRegression)} moved from {FormatHelper.FormatMetricValue(firstRegression.Previous, firstRegression.Unit)} to {FormatHelper.FormatMetricValue(firstRegression.Current, firstRegression.Unit)}."
            : "No single regression point dominates the window.";

        string largestInflectionText = largestInflectionIndex > 0 && inflectionCandidate is not null
            ? $"{FormatSnapshotLabel(trendData.Snapshots[largestInflectionIndex])}: largest movement came from {FormatAnalyzerMetricReference(inflectionCandidate)} after {largestInflectionScore.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} units of normalized shift."
            : "No meaningful inflection spike stood out.";

        string[] topDomains = worseningDomains.Length > 0
            ? worseningDomains.Select(d => $"{d.Domain}: {d.Count} regressions").ToArray()
            : ["No domain crossed a regression threshold."];

        string[] couplingHintArray = couplingHints.Count > 0
            ? couplingHints.ToArray()
            : ["No strong coupling hint emerged from the available aggregates."];

        return new TrendStoryRecord(
            Summary: summary,
            FirstMajorRegression: firstRegressionText,
            LargestInflection: largestInflectionText,
            TopWorseningDomains: topDomains,
            LikelyCouplingHints: couplingHintArray,
            MetricReferences: metricReferences.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void AddMetricReference(List<string> metricReferences, TrendStoryCandidate? candidate)
    {
        if (candidate is null)
            return;

        metricReferences.Add(FormatAnalyzerMetricReference(candidate));
    }

    private static bool IsRegression(MetricTrendDirection direction, double delta)
        => direction switch
        {
            MetricTrendDirection.HigherIsWorse => delta > 0,
            MetricTrendDirection.LowerIsWorse => delta < 0,
            _ => false
        };

    private static string FormatSnapshotLabel(AnalysisSnapshot snapshot)
        => $"Dump {snapshot.Index + 1}: {Path.GetFileName(snapshot.DumpPath)}";

    private static string FormatAnalyzerMetricReference(TrendStoryCandidate candidate)
    {
        string scope = string.IsNullOrWhiteSpace(candidate.Scope) ? string.Empty : $" / {candidate.Scope}";
        return $"{candidate.AnalyzerName} · {candidate.MetricKey}{scope}";
    }

    private sealed record TrendStoryCandidate(
        string AnalyzerName,
        string MetricKey,
        string? Scope,
        string Unit,
        int SnapshotIndex,
        double Previous,
        double Current,
        double Delta,
        double Magnitude);

    private sealed class TrendStoryDomainStat
    {
        public TrendStoryDomainStat(string domain) => Domain = domain;

        public string Domain { get; }
        public int Count { get; set; }
        public double Magnitude { get; set; }
    }
}
