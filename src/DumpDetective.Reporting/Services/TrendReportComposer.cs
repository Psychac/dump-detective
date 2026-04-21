namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Abstractions;
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
        TrendReportData trendData)
    {
        ReportBuilder.FindingLifecycleResult lifecycle = new(
            trendData.NewFindings,
            trendData.PersistentFindings,
            trendData.ResolvedFindings);

        List<InsightFinding> trendFindings =
        [
            .. ReportBuilder.BuildTrendFindings(trendData.Overall, lifecycle),
            .. BuildTopRegressionFindings(trendData.Overall)
        ];

        GenericAnalyzerDomainResult trendResult = new()
        {
            AnalyzerName = "TrendAnalyzer",
            Category = "Comparison",
            Findings = trendFindings,
            Metrics = new Dictionary<string, object?>(),
            Warnings = []
        };

        AnalyzerRunResult trendRun = new(
            AnalyzerName: "TrendAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.Zero,
            Result: trendResult,
            ErrorMessage: null,
            ErrorType: null,
            FindingCount: trendFindings.Count);

        ComposedReport report = ReportBuilder.ComposeCanonicalReport(dumpPath, [trendRun], elapsed, []);

        string trendComparison = ReportBuilder.BuildTrendComparisonSection(
            trendData.Steps,
            trendData.Overall,
            lifecycle,
            trendData.Timeline,
            trendData.Snapshots);

        List<DetailedAnalyzerSection> detailedSections = [];
        detailedSections.Add(new DetailedAnalyzerSection("Trend Comparison", trendComparison));
        detailedSections.AddRange(BuildPerDumpSections(trendData.Snapshots, reporters));

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
        IReadOnlyList<IAnalyzerReporter> reporters)
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
                reporters);

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
}

internal sealed record TrendReportData(
    IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> Steps,
    IReadOnlyList<AnalyzerTrendResult> Overall,
    IReadOnlyList<AnalyzerMetricTimeline> Timeline,
    IReadOnlyList<AnalysisSnapshot> Snapshots,
    IReadOnlyList<InsightFinding> NewFindings,
    IReadOnlyList<InsightFinding> PersistentFindings,
    IReadOnlyList<InsightFinding> ResolvedFindings);

