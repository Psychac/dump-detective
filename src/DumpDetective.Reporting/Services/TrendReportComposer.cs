namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

internal sealed class TrendReportComposer
{
    public ComposedReport ComposeCanonicalTrendReport(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        IReadOnlyList<IAnalyzerReporter> reporters,
        TrendReportData trendData,
        ReportAudience audience = ReportAudience.All)
    {
        FindingLifecycleResult lifecycle = new(
            trendData.NewFindings,
            trendData.PersistentFindings,
            trendData.ResolvedFindings);

        List<InsightFinding> trendFindings =
        [
            .. BuildTrendFindings(trendData.Overall, lifecycle),
            .. BuildTopRegressionFindings(trendData.Overall)
        ];

        ComposedReport report = ReportBuilder.ComposeFromFindings(dumpPath, trendFindings, elapsed);

        string trendComparison = BuildTrendComparisonSection(
            trendData.Steps,
            trendData.Overall,
            lifecycle,
            trendData.Timeline,
            trendData.Snapshots);

        List<DetailedAnalyzerSection> detailedSections = [];
        detailedSections.Add(new DetailedAnalyzerSection("Trend Comparison", trendComparison));
        detailedSections.AddRange(BuildPerDumpSections(trendData.Snapshots, reporters, audience));

        if (report.DetailedAnalyzerSections is { Count: > 0 })
        {
            detailedSections.AddRange(report.DetailedAnalyzerSections);
        }

        return report with
        {
            DetailedAnalyzerSections = detailedSections,
            IsTrendReport = true,
            TrendDumpCount = trendData.Snapshots.Count,
            TrendDumpPaths = trendData.Snapshots.Select(s => s.DumpPath).ToList()
        };
    }

    private static IReadOnlyList<InsightFinding> BuildTopRegressionFindings(IReadOnlyList<AnalyzerTrendResult> overall)
    {
        var topRegressions = overall
            .SelectMany(r => r.Regressions.Select(d => (Analyzer: r.AnalyzerName, Delta: d)))
            .OrderByDescending(x => Math.Abs(x.Delta.DeltaPercent ?? x.Delta.Delta))
            .Take(8)
            .ToList();

        List<InsightFinding> findings = new(topRegressions.Count);
        foreach (var regression in topRegressions)
        {
            MetricDelta delta = regression.Delta;
            string scopeSuffix = string.IsNullOrWhiteSpace(delta.Scope) ? string.Empty : $" ({delta.Scope})";
            string deltaText = delta.DeltaPercent.HasValue
                ? $"{(delta.DeltaPercent.Value >= 0 ? "+" : string.Empty)}{delta.DeltaPercent.Value:F1}%"
                : $"{(delta.Delta >= 0 ? "+" : string.Empty)}{delta.Delta:F1} {delta.Unit}";

            findings.Add(new InsightFinding(
                Analyzer: "TrendAnalyzer",
                Category: "Comparison",
                Severity: FindingSeverity.Warning,
                Title: $"Trend regression: {regression.Analyzer} / {delta.Key}{scopeSuffix}",
                Evidence: $"Metric moved from {FormatHelper.FormatMetricValue(delta.Baseline, delta.Unit)} to {FormatHelper.FormatMetricValue(delta.Current, delta.Unit)} ({deltaText}).",
                Recommendation: "Prioritize this regression in the trend timeline and correlate with dump-to-dump finding lifecycle changes.",
                Tags: ["trend", "regression", regression.Analyzer, delta.Key],
                MetricValue: delta.DeltaPercent ?? delta.Delta,
                MetricUnit: delta.DeltaPercent.HasValue ? "%" : delta.Unit));
        }

        return findings;
    }

    private static IReadOnlyList<DetailedAnalyzerSection> BuildPerDumpSections(
        IReadOnlyList<AnalysisSnapshot> snapshots,
        IReadOnlyList<IAnalyzerReporter> reporters,
        ReportAudience audience)
    {
        List<DetailedAnalyzerSection> sections = new(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];

            IReadOnlyList<AnalyzerRunResult> snapshotRuns = snapshot.DomainResults
                .Select(kvp => new AnalyzerRunResult(
                    AnalyzerName: kvp.Key,
                    Status: AnalyzerExecutionStatus.Success,
                    Duration: TimeSpan.Zero,
                    Result: kvp.Value,
                    ErrorMessage: null,
                    ErrorType: null,
                    FindingCount: kvp.Value.Findings.Count,
                    WarningCount: kvp.Value.Warnings.Count))
                .ToList();

            ComposedReport snapshotReport = ReportBuilder.ComposeCanonicalReport(
                snapshot.DumpPath,
                snapshotRuns,
                TimeSpan.Zero,
                reporters,
                audience);

            sections.Add(new DetailedAnalyzerSection(
                $"Dump {i + 1}: {Path.GetFileName(snapshot.DumpPath)} - Full Report",
                BuildSnapshotFullReport(snapshotReport)));
        }

        return sections;
    }

    private static string BuildSnapshotFullReport(ComposedReport snapshotReport)
    {
        List<string> lines =
        [
            "DUMP FULL REPORT",
            "--------------------------------------------------------------------------------",
            $"Dump path: {snapshotReport.DumpPath}",
            $"Generated (UTC): {snapshotReport.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}",
            $"Sections: {snapshotReport.Sections.Count}",
            string.Empty
        ];

        foreach (ReportSection section in snapshotReport.Sections)
        {
            lines.Add($"[{section.Severity}] {section.Title} ({section.Category})");
            lines.Add(section.NarrativeSummary);

            foreach (ReportEvidenceRow row in section.EvidenceRows)
            {
                lines.Add($"  - {row.Label}: {row.Value}");
            }

            if (section.RemediationHints.Count > 0)
            {
                lines.Add("  Remediation:");
                foreach (string hint in section.RemediationHints)
                {
                    lines.Add($"    - {hint}");
                }
            }

            lines.Add(string.Empty);
        }

        if (snapshotReport.DetailedAnalyzerSections is { Count: > 0 })
        {
            lines.Add("Detailed analyzer sections:");
            lines.Add("--------------------------------------------------------------------------------");
            foreach (DetailedAnalyzerSection detail in snapshotReport.DetailedAnalyzerSections)
            {
                lines.Add($"[{detail.Title}]");
                lines.Add(detail.Content);
                lines.Add(string.Empty);
            }
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }
    private static List<InsightFinding> BuildTrendFindings(
        IReadOnlyList<AnalyzerTrendResult> overall,
        FindingLifecycleResult lifecycle)
    {
        int topRegressions = overall.Sum(r => r.Regressions.Count);
        FindingSeverity regressionSeverity = topRegressions >= 5 ? FindingSeverity.Warning : FindingSeverity.Info;

        // Lifecycle severity is driven by the highest severity among *new* findings, not just count.
        FindingSeverity lifecycleSeverity = lifecycle.NewFindings.Count == 0
            ? FindingSeverity.Info
            : lifecycle.NewFindings
                .Select(f => f.Severity)
                .OrderByDescending(s => s)
                .First();

        return
        [
            new(
                Analyzer: "TrendAnalyzer",
                Category: "Comparison",
                Severity: lifecycleSeverity,
                Title: "Trend finding lifecycle summary",
                Evidence: $"New {lifecycle.NewFindings.Count}, Persistent {lifecycle.PersistentFindings.Count}, Resolved {lifecycle.ResolvedFindings.Count}",
                Recommendation: "Focus first on new and persistent high-severity findings.",
                Tags: ["trend", "lifecycle", "comparison"],
                MetricValue: lifecycle.NewFindings.Count - lifecycle.ResolvedFindings.Count,
                MetricUnit: "net-findings"),
            new(
                Analyzer: "TrendAnalyzer",
                Category: "Comparison",
                Severity: regressionSeverity,
                Title: "Trend metric regression summary",
                Evidence: $"{topRegressions} metric regression(s) across {overall.Count} analyzer(s) compared.",
                Recommendation: topRegressions > 0
                    ? "Review per-analyzer metric regressions in the trend comparison section."
                    : "No metric regressions detected across compared analyzers.",
                Tags: ["trend", "metrics", "comparison"])
        ];
    }

    private static string BuildTrendComparisonSection(
        IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> steps,
        IReadOnlyList<AnalyzerTrendResult> overall,
        FindingLifecycleResult lifecycle,
        IReadOnlyList<AnalyzerMetricTimeline> timeline,
        IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("TREND COMPARISON:");
        builder.AppendLine(StringConstants.Separator80);
        builder.AppendLine($"Dumps analyzed: {snapshots.Count}");
        builder.AppendLine($"New findings: {lifecycle.NewFindings.Count}");
        builder.AppendLine($"Persistent findings: {lifecycle.PersistentFindings.Count}");
        builder.AppendLine($"Resolved findings: {lifecycle.ResolvedFindings.Count}");

        int totalRegressions = overall.Sum(r => r.Regressions.Count);
        int totalImprovements = overall.Sum(r => r.Improvements.Count);
        builder.AppendLine($"Metric regressions: {totalRegressions}");
        builder.AppendLine($"Metric improvements: {totalImprovements}");

        if (timeline.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"PER-ANALYZER METRIC TIMELINE ({snapshots.Count} dumps):");

            var regressionsByAnalyzer = overall.ToDictionary(r => r.AnalyzerName, r => r.Regressions.Count, StringComparer.Ordinal);
            var orderedTimeline = timeline.OrderByDescending(t => regressionsByAnalyzer.GetValueOrDefault(t.AnalyzerName));

            foreach (var analyzerTimeline in orderedTimeline)
            {
                builder.AppendLine($"  [{analyzerTimeline.AnalyzerName}]");

                foreach (var point in analyzerTimeline.Points)
                {
                    var validValues = point.Values.Where(v => !double.IsNaN(v)).ToList();
                    if (validValues.Count == 0) continue;

                    string valuesLine = string.Join(" \u2192 ", point.Values.Select(v => FormatHelper.FormatMetricValue(v, point.Unit)));

                    double firstVal = point.Values.FirstOrDefault(v => !double.IsNaN(v));
                    double lastVal = point.Values.Last(v => !double.IsNaN(v));
                    double delta = lastVal - firstVal;
                    double? deltaPercent = Math.Abs(firstVal) > double.Epsilon ? delta * 100.0 / firstVal : null;

                    string deltaStr = FormatHelper.FormatDeltaValue(delta, point.Unit);
                    string pctStr = deltaPercent.HasValue ? $", {(deltaPercent.Value >= 0 ? "+" : string.Empty)}{deltaPercent.Value:F1}%" : string.Empty;

                    string icon = (point.Direction, delta > 0) switch
                    {
                        (MetricTrendDirection.HigherIsWorse, true)  => "!! ",
                        (MetricTrendDirection.HigherIsWorse, false) when delta < 0 => "OK ",
                        (MetricTrendDirection.LowerIsWorse, false) when delta < 0  => "!! ",
                        (MetricTrendDirection.LowerIsWorse, true)   => "OK ",
                        _ => "   "
                    };

                    string deltaLabel = delta == 0 ? "no change" : $"\u0394 {deltaStr}{pctStr}";
                    builder.AppendLine($"    {icon} {point.Key}: {valuesLine}   ({deltaLabel})");
                }
            }
        }

        if (lifecycle.NewFindings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("New findings:");
            foreach (var f in lifecycle.NewFindings.OrderByDescending(f => f.Severity).Take(5))
                builder.AppendLine($"  - [{f.Severity}] {f.Analyzer}: {f.Title}");
        }

        if (lifecycle.ResolvedFindings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Resolved findings:");
            foreach (var f in lifecycle.ResolvedFindings.Take(5))
                builder.AppendLine($"  - [{f.Severity}] {f.Analyzer}: {f.Title}");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private sealed record FindingLifecycleResult(
        IReadOnlyList<InsightFinding> NewFindings,
        IReadOnlyList<InsightFinding> PersistentFindings,
        IReadOnlyList<InsightFinding> ResolvedFindings);
}

internal sealed record TrendReportData(
    IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> Steps,
    IReadOnlyList<AnalyzerTrendResult> Overall,
    IReadOnlyList<AnalyzerMetricTimeline> Timeline,
    IReadOnlyList<AnalysisSnapshot> Snapshots,
    IReadOnlyList<InsightFinding> NewFindings,
    IReadOnlyList<InsightFinding> PersistentFindings,
    IReadOnlyList<InsightFinding> ResolvedFindings);

