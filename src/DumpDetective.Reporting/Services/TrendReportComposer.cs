namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Output;

internal sealed class TrendReportComposer(IEnumerable<IFindingGenerator> generators)
{
    private readonly IReadOnlyDictionary<string, IFindingGenerator> _generators =
        generators.ToDictionary(g => g.AnalyzerName, StringComparer.Ordinal);
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

        List<DetailedAnalyzerSection> detailedSections = [];
        detailedSections.Add(BuildTrendComparisonSection(
            trendData.Steps,
            trendData.Overall,
            lifecycle,
            trendData.Timeline,
            trendData.Snapshots));
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

    private IReadOnlyList<DetailedAnalyzerSection> BuildPerDumpSections(
        IReadOnlyList<AnalysisSnapshot> snapshots,
        IReadOnlyList<IAnalyzerReporter> reporters,
        ReportAudience audience)
    {
        List<DetailedAnalyzerSection> sections = new(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];

            IReadOnlyList<AnalyzerRunResult> snapshotRuns = snapshot.DomainResults
                .Select(kvp =>
                {
                    IReadOnlyList<InsightFinding> domainFindings = _generators.TryGetValue(kvp.Key, out IFindingGenerator? gen)
                        ? gen.Generate(kvp.Value)
                        : [];
                    return new AnalyzerRunResult(
                        AnalyzerName: kvp.Key,
                        Status: AnalyzerExecutionStatus.Success,
                        Duration: TimeSpan.Zero,
                        Result: kvp.Value,
                        ErrorMessage: null,
                        ErrorType: null,
                        Findings: domainFindings,
                        FindingCount: domainFindings.Count,
                        WarningCount: kvp.Value.Warnings.Count);
                })
                .ToList();

            ComposedReport snapshotReport = ReportBuilder.ComposeCanonicalReport(
                snapshot.DumpPath,
                snapshotRuns,
                TimeSpan.Zero,
                reporters,
                audience);

            sections.Add(BuildStructuredDumpSection(snapshotReport, i, snapshots.Count));
        }

        return sections;
    }

    /// <summary>Builds one card per dump: summary header + findings summary, then each analyzer as a
    /// collapsible nested <c>&lt;details&gt;</c> block inside the same dark content area.</summary>
    private static DetailedAnalyzerSection BuildStructuredDumpSection(ComposedReport snapshotReport, int dumpIndex, int totalDumps)
    {
        string sectionTitle = $"Dump {dumpIndex + 1} of {totalDumps}: {Path.GetFileName(snapshotReport.DumpPath)}";

        // Build summary + findings through the writer (for both text content and submodules)
        var writer = new StructuredCaptureReportWriter();
        writer.WriteSubHeading("DUMP SUMMARY:");
        writer.WriteSeparator();
        writer.WritePathMetric("Path", snapshotReport.DumpPath);
        writer.WriteMetric("Generated (UTC)", $"{snapshotReport.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
        writer.WriteMetric("Findings", $"{snapshotReport.Sections.Count}");

        if (snapshotReport.Sections.Count > 0)
        {
            writer.WriteDetailBlank();
            writer.WriteSubHeading("FINDINGS:");
            writer.WriteSeparator();
            foreach (ReportSection section in snapshotReport.Sections)
            {
                writer.WriteDetailHeading($"[{section.Severity}] {section.Title}", indentLevel: 1);
                writer.WriteDetailText(section.NarrativeSummary, indentLevel: 2);
                foreach (ReportEvidenceRow row in section.EvidenceRows)
                    writer.WriteDetailMetric(row.Label, row.Value, indentLevel: 2);
            }
        }

        // Start from the writer's submodules, then append SectionBegin/content/SectionEnd per analyzer
        List<DetailedAnalyzerSubmodule> allSubmodules = [.. writer.GetSubmodules()];
        if (snapshotReport.DetailedAnalyzerSections is { Count: > 0 })
        {
            allSubmodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Empty, null, null, null));
            foreach (DetailedAnalyzerSection analyzerSection in snapshotReport.DetailedAnalyzerSections)
            {
                allSubmodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.SectionBegin, null, null, analyzerSection.Title));
                if (analyzerSection.Submodules is { Count: > 0 })
                    allSubmodules.AddRange(analyzerSection.Submodules);
                allSubmodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.SectionEnd, null, null, null));
            }
        }

        // Keep full text content for text/markdown formatters (no regression there)
        return new DetailedAnalyzerSection(sectionTitle, BuildSnapshotFullReport(snapshotReport), allSubmodules);
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

    private static DetailedAnalyzerSection BuildTrendComparisonSection(
        IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> steps,
        IReadOnlyList<AnalyzerTrendResult> overall,
        FindingLifecycleResult lifecycle,
        IReadOnlyList<AnalyzerMetricTimeline> timeline,
        IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        int totalRegressions = overall.Sum(r => r.Regressions.Count);
        int totalImprovements = overall.Sum(r => r.Improvements.Count);

        var writer = new StructuredCaptureReportWriter();
        writer.WriteHeader("TREND COMPARISON");
        writer.WriteSubHeading("LIFECYCLE SUMMARY:");
        writer.WriteSeparator();
        writer.WriteMetric("Dumps analyzed", $"{snapshots.Count}");
        writer.WriteMetric("New findings", $"{lifecycle.NewFindings.Count}");
        writer.WriteMetric("Persistent findings", $"{lifecycle.PersistentFindings.Count}");
        writer.WriteMetric("Resolved findings", $"{lifecycle.ResolvedFindings.Count}");
        writer.WriteMetric("Metric regressions", $"{totalRegressions}");
        writer.WriteMetric("Metric improvements", $"{totalImprovements}");

        if (timeline.Count > 0)
        {
            writer.WriteDetailBlank();
            writer.WriteSubHeading($"METRIC TIMELINE ({snapshots.Count} dumps):");
            writer.WriteSeparator();

            var regressionsByAnalyzer = overall.ToDictionary(r => r.AnalyzerName, r => r.Regressions.Count, StringComparer.Ordinal);
            var orderedTimeline = timeline.OrderByDescending(t => regressionsByAnalyzer.GetValueOrDefault(t.AnalyzerName)).ToList();

            foreach (var analyzerTimeline in orderedTimeline)
            {
                List<DetailedAnalyzerTableRow> rows = [];
                foreach (var point in analyzerTimeline.Points)
                {
                    if (point.Values.All(double.IsNaN)) continue;

                    double firstVal = point.Values.FirstOrDefault(v => !double.IsNaN(v));
                    double lastVal = point.Values.Last(v => !double.IsNaN(v));
                    double delta = lastVal - firstVal;
                    double? deltaPercent = Math.Abs(firstVal) > double.Epsilon ? delta * 100.0 / firstVal : null;

                    string trendText = snapshots.Count <= 6
                        ? string.Join(" \u2192 ", point.Values.Select(v => FormatHelper.FormatMetricValue(v, point.Unit)))
                        : $"{FormatHelper.FormatMetricValue(firstVal, point.Unit)} \u2192 \u2026 \u2192 {FormatHelper.FormatMetricValue(lastVal, point.Unit)}";

                    string pctStr = deltaPercent.HasValue ? $" ({(deltaPercent.Value >= 0 ? "+" : string.Empty)}{deltaPercent.Value:F1}%)" : string.Empty;
                    string deltaDisplay = delta == 0 ? "no change" : $"{(delta >= 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(delta, point.Unit)}{pctStr}";

                    string status = (point.Direction, delta > 0, delta < 0) switch
                    {
                        (MetricTrendDirection.HigherIsWorse, true, _)  => "\u26a0 Regression",
                        (MetricTrendDirection.HigherIsWorse, _, true)  => "\u2705 Improvement",
                        (MetricTrendDirection.LowerIsWorse,  _, true)  => "\u26a0 Regression",
                        (MetricTrendDirection.LowerIsWorse,  true, _)  => "\u2705 Improvement",
                        _                                               => "\u2014 Stable"
                    };

                    rows.Add(new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(point.Key),
                        new DetailedAnalyzerTableCell(trendText),
                        new DetailedAnalyzerTableCell(deltaDisplay, delta == 0 ? 0L : (long)Math.Round(Math.Abs(delta))),
                        new DetailedAnalyzerTableCell(status)
                    ]));
                }

                if (rows.Count > 0)
                {
                    writer.WriteDetailBlank();
                    writer.WriteSubHeading($"[{analyzerTimeline.AnalyzerName}]", indentLevel: 1);
                    writer.WriteDetailTable(new DetailedAnalyzerTableData(
                        Caption: $"{analyzerTimeline.AnalyzerName} metric timeline",
                        Headers: ["Metric", $"Trend ({snapshots.Count} snapshots)", "\u0394", "Status"],
                        Rows: rows));
                }
            }
        }

        if (lifecycle.NewFindings.Count > 0)
        {
            writer.WriteDetailBlank();
            writer.WriteSubHeading("NEW FINDINGS:");
            writer.WriteSeparator();
            foreach (var f in lifecycle.NewFindings.OrderByDescending(f => f.Severity).Take(5))
                writer.WriteDetailListItem($"[{f.Severity}] {f.Analyzer}: {f.Title}");
        }

        if (lifecycle.ResolvedFindings.Count > 0)
        {
            writer.WriteDetailBlank();
            writer.WriteSubHeading("RESOLVED FINDINGS:");
            writer.WriteSeparator();
            foreach (var f in lifecycle.ResolvedFindings.Take(5))
                writer.WriteDetailListItem($"[{f.Severity}] {f.Analyzer}: {f.Title}");
        }

        writer.WriteDetailDivider();
        return new DetailedAnalyzerSection("Trend Comparison", writer.GetContent(), writer.GetSubmodules());
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

