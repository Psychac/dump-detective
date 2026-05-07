namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

internal sealed class TrendReportComposer(
    IEnumerable<IFindingGenerator> generators,
    ReportSerializer serializer)
{
    private readonly IReadOnlyDictionary<string, IFindingGenerator> _generators =
        generators.ToDictionary(g => g.AnalyzerName, StringComparer.Ordinal);
    private readonly ReportSerializer _serializer = serializer;

    public AnalysisReportDocument ComposeCanonicalTrendReport(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
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

        // Wrap trend findings in a synthetic run so ReportSerializer can serialize them
        AnalyzerRunResult trendRun = new(
            AnalyzerName: "TrendAnalyzer",
            Status: AnalyzerExecutionStatus.Success,
            Duration: TimeSpan.Zero,
            Result: null,
            ErrorMessage: null,
            ErrorType: null,
            Findings: trendFindings,
            FindingCount: trendFindings.Count);

        AnalysisReportDocument baseDoc = _serializer.Serialize(dumpPath, [trendRun], elapsed, [], audience);

        // Build trend-specific analyzer sections
        var analyzerSections = new List<AnalyzerDetailSection>();
        analyzerSections.Add(BuildTrendComparisonSection(
            trendData.Steps,
            trendData.Overall,
            lifecycle,
            trendData.Timeline,
            trendData.Snapshots));
        analyzerSections.AddRange(BuildPerDumpSections(trendData.Snapshots, builders, audience));

        return new AnalysisReportDocument
        {
            SchemaVersion       = baseDoc.SchemaVersion,
            DumpPath            = baseDoc.DumpPath,
            GeneratedAtUtc      = baseDoc.GeneratedAtUtc,
            ElapsedSeconds      = baseDoc.ElapsedSeconds,
            IsTrendReport       = true,
            TrendDumpCount      = trendData.Snapshots.Count,
            TrendDumpPaths      = trendData.Snapshots.Select(s => s.DumpPath).ToList(),
            Findings            = baseDoc.Findings,
            ExecutiveSummary    = baseDoc.ExecutiveSummary,
            DeveloperActionPlan = baseDoc.DeveloperActionPlan,
            Confidence          = baseDoc.Confidence,
            DedupDiagnostics    = baseDoc.DedupDiagnostics,
            AnalyzerSections    = analyzerSections
        };
    }

    // ── Trend findings ────────────────────────────────────────────────────────

    private static IReadOnlyList<InsightFinding> BuildTopRegressionFindings(IReadOnlyList<AnalyzerTrendResult> overall)
    {
        var topRegressions = overall
            .SelectMany(r => r.Regressions.Select(d => (Analyzer: r.AnalyzerName, Delta: d)))
            .OrderByDescending(x => Math.Abs(x.Delta.DeltaPercent ?? x.Delta.Delta))
            .Take(8)
            .ToList();

        List<InsightFinding> findings = new(topRegressions.Count);
        foreach (var (analyzerName, delta) in topRegressions)
        {
            string scopeSuffix = string.IsNullOrWhiteSpace(delta.Scope) ? string.Empty : $" ({delta.Scope})";
            string deltaText = delta.DeltaPercent.HasValue
                ? $"{(delta.DeltaPercent.Value >= 0 ? "+" : string.Empty)}{delta.DeltaPercent.Value:F1}%"
                : $"{(delta.Delta >= 0 ? "+" : string.Empty)}{delta.Delta:F1} {delta.Unit}";

            findings.Add(new InsightFinding(
                Analyzer: "TrendAnalyzer",
                Category: "Comparison",
                Severity: delta.Severity switch
                {
                    RegressionSeverity.Severe   => FindingSeverity.Critical,
                    RegressionSeverity.Moderate => FindingSeverity.Warning,
                    _                           => FindingSeverity.Info
                },
                Title: $"Trend regression: {analyzerName} / {delta.Key}{scopeSuffix}",
                Evidence: $"Metric moved from {FormatHelper.FormatMetricValue(delta.Baseline, delta.Unit)} to {FormatHelper.FormatMetricValue(delta.Current, delta.Unit)} ({deltaText}).",
                Recommendation: "Prioritize this regression in the trend timeline and correlate with dump-to-dump finding lifecycle changes.",
                Tags: ["trend", "regression", analyzerName, delta.Key],
                MetricValue: delta.DeltaPercent ?? delta.Delta,
                MetricUnit: delta.DeltaPercent.HasValue ? "%" : delta.Unit));
        }

        return findings;
    }

    private static List<InsightFinding> BuildTrendFindings(
        IReadOnlyList<AnalyzerTrendResult> overall,
        FindingLifecycleResult lifecycle)
    {
        int topRegressions = overall.Sum(r => r.Regressions.Count);
        FindingSeverity regressionSeverity = topRegressions >= 5 ? FindingSeverity.Warning : FindingSeverity.Info;

        FindingSeverity lifecycleSeverity = lifecycle.NewFindings.Count == 0
            ? FindingSeverity.Info
            : lifecycle.NewFindings.Select(f => f.Severity).OrderByDescending(s => s).First();

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

    // ── Per-dump sections ─────────────────────────────────────────────────────

    private IReadOnlyList<AnalyzerDetailSection> BuildPerDumpSections(
        IReadOnlyList<AnalysisSnapshot> snapshots,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        ReportAudience audience)
    {
        var sections = new List<AnalyzerDetailSection>(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];
            IReadOnlyList<AnalyzerRunResult> runs = BuildSnapshotRuns(snapshot);
            AnalysisReportDocument snapshotDoc = _serializer.Serialize(snapshot.DumpPath, runs, TimeSpan.Zero, builders, audience);
            sections.Add(BuildStructuredDumpSection(snapshotDoc, i, snapshots.Count));
        }

        return sections;
    }

    private IReadOnlyList<AnalyzerRunResult> BuildSnapshotRuns(AnalysisSnapshot snapshot)
    {
        return snapshot.DomainResults
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
    }

    private static AnalyzerDetailSection BuildStructuredDumpSection(
        AnalysisReportDocument snapshotDoc, int dumpIndex, int totalDumps)
    {
        string title = $"Dump {dumpIndex + 1} of {totalDumps}: {Path.GetFileName(snapshotDoc.DumpPath)}";
        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("DUMP SUMMARY"));
        blocks.Add(new DividerBlock());
        blocks.Add(new PathBlock("Path", snapshotDoc.DumpPath));
        blocks.Add(new MetricBlock("Generated (UTC)", snapshotDoc.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")));
        blocks.Add(new MetricBlock("Findings", snapshotDoc.Findings.Count.ToString()));

        if (snapshotDoc.Findings.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("FINDINGS"));
            blocks.Add(new DividerBlock());
            foreach (FindingRecord finding in snapshotDoc.Findings)
            {
                blocks.Add(new HeadingBlock($"[{finding.Severity}] {finding.Title}", 1));
                blocks.Add(new TextBlock(finding.Evidence, 2));
            }
        }

        foreach (AnalyzerDetailSection section in snapshotDoc.AnalyzerSections)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new CollapsibleSectionBeginBlock(section.DisplayTitle));
            foreach (SectionBlock block in section.Blocks)
                blocks.Add(block);
            blocks.Add(new CollapsibleSectionEndBlock());
        }

        return new AnalyzerDetailSection(title, title, dumpIndex * 10 + 200, blocks);
    }

    // ── Trend comparison section ──────────────────────────────────────────────

    private static AnalyzerDetailSection BuildTrendComparisonSection(
        IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> steps,
        IReadOnlyList<AnalyzerTrendResult> overall,
        FindingLifecycleResult lifecycle,
        IReadOnlyList<AnalyzerMetricTimeline> timeline,
        IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        int totalRegressions  = overall.Sum(r => r.Regressions.Count);
        int totalImprovements = overall.Sum(r => r.Improvements.Count);

        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("TREND COMPARISON"));
        blocks.Add(new HeadingBlock("LIFECYCLE SUMMARY:"));
        blocks.Add(new DividerBlock());
        blocks.Add(new MetricBlock("Dumps analyzed",       snapshots.Count.ToString()));
        blocks.Add(new MetricBlock("New findings",         lifecycle.NewFindings.Count.ToString()));
        blocks.Add(new MetricBlock("Persistent findings",  lifecycle.PersistentFindings.Count.ToString()));
        blocks.Add(new MetricBlock("Resolved findings",    lifecycle.ResolvedFindings.Count.ToString()));
        blocks.Add(new MetricBlock("Metric regressions",   totalRegressions.ToString()));
        blocks.Add(new MetricBlock("Metric improvements",  totalImprovements.ToString()));

        if (timeline.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock($"METRIC TIMELINE ({snapshots.Count} dumps):"));
            blocks.Add(new DividerBlock());

            var regressionsByAnalyzer = overall.ToDictionary(
                r => r.AnalyzerName, r => r.Regressions.Count, StringComparer.Ordinal);

            var orderedTimeline = timeline
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

                    // compute severity inline from the direction/delta/percent
                    RegressionSeverity severity = RegressionSeverity.None;
                    bool isRegression = (point.Direction == MetricTrendDirection.HigherIsWorse && delta > 0)
                                     || (point.Direction == MetricTrendDirection.LowerIsWorse  && delta < 0);
                    if (isRegression)
                    {
                        if (!deltaPercent.HasValue) severity = RegressionSeverity.Moderate;
                        else
                        {
                            double absPct = Math.Abs(deltaPercent.Value);
                            severity = absPct switch
                            {
                                < 10.0 => RegressionSeverity.Minor,
                                < 50.0 => RegressionSeverity.Moderate,
                                _      => RegressionSeverity.Severe
                            };
                        }
                    }

                    string trendText = snapshots.Count <= 6
                        ? string.Join(" \u2192 ", point.Values.Select(v => FormatHelper.FormatMetricValue(v, point.Unit)))
                        : $"{FormatHelper.FormatMetricValue(firstVal, point.Unit)} \u2192 \u2026 \u2192 {FormatHelper.FormatMetricValue(lastVal, point.Unit)}";

                    string pctStr = deltaPercent.HasValue
                        ? $" ({(deltaPercent.Value >= 0 ? "+" : string.Empty)}{deltaPercent.Value:F1}%)"
                        : string.Empty;
                    string deltaDisplay = delta == 0
                        ? "no change"
                        : $"{(delta >= 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(delta, point.Unit)}{pctStr}";

                    string status = (point.Direction, delta > 0, delta < 0) switch
                    {
                        (MetricTrendDirection.HigherIsWorse, true, _)  => severity == RegressionSeverity.Severe ? "\u26a0\u26a0 Severe" : "\u26a0 Regression",
                        (MetricTrendDirection.HigherIsWorse, _, true)  => "\u2705 Improvement",
                        (MetricTrendDirection.LowerIsWorse,  _, true)  => severity == RegressionSeverity.Severe ? "\u26a0\u26a0 Severe" : "\u26a0 Regression",
                        (MetricTrendDirection.LowerIsWorse,  true, _)  => "\u2705 Improvement",
                        _                                               => "\u2014 Stable"
                    };

                    // Determine snapshot index with largest adjacent change to link to the likely originating dump
                    int linkSnapshot = snapshots.Count - 1; // default: latest
                    try
                    {
                        double best = 0.0; int bestIdx = -1;
                        double? prev = null;
                        for (int si = 0; si < point.Values.Count; si++)
                        {
                            double v = point.Values[si];
                            if (double.IsNaN(v)) continue;
                            if (prev.HasValue)
                            {
                                double d = Math.Abs(v - prev.Value);
                                if (d > best) { best = d; bestIdx = si; }
                            }
                            prev = v;
                        }
                        if (bestIdx >= 0) linkSnapshot = Math.Min(bestIdx, snapshots.Count - 1);
                    }
                    catch { }

                    // Embed sparkline payload as JSON in a special display token; link token appended to metric cell via ||__LINK__detail-{index}
                    string sparkPayload = System.Text.Json.JsonSerializer.Serialize(new { values = point.Values, unit = point.Unit });
                    string sparkToken = "__SPARK__" + sparkPayload;
                    // Use zero-based snapshot index to match `detail-{i}` IDs generated in the report
                    string metricDisplay = point.Key + "||__LINK__detail-" + linkSnapshot;

                    rows.Add(new TableRow(
                    [
                        new TableCell(metricDisplay),
                        new TableCell(sparkToken),
                        new TableCell(deltaDisplay, delta == 0 ? 0L : (long)Math.Round(Math.Abs(delta))),
                        new TableCell(status)
                    ]));
                }

                if (rows.Count > 0)
                {
                    blocks.Add(new BlankBlock());
                    blocks.Add(new HeadingBlock($"[{analyzerTimeline.AnalyzerName}]", 1));
                    blocks.Add(new TableBlock(
                        Caption: $"{analyzerTimeline.AnalyzerName} metric timeline",
                        Headers: ["Metric", $"Trend ({snapshots.Count} snapshots)", "\u0394", "Status"],
                        Rows: rows));
                }
            }
        }

        if (lifecycle.NewFindings.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("NEW FINDINGS:"));
            blocks.Add(new DividerBlock());
            foreach (InsightFinding f in lifecycle.NewFindings.OrderByDescending(f => f.Severity).Take(5))
                blocks.Add(new ListItemBlock($"[{f.Severity}] {f.Analyzer}: {f.Title}"));
        }

        if (lifecycle.ResolvedFindings.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("RESOLVED FINDINGS:"));
            blocks.Add(new DividerBlock());
            foreach (InsightFinding f in lifecycle.ResolvedFindings.Take(5))
                blocks.Add(new ListItemBlock($"[{f.Severity}] {f.Analyzer}: {f.Title}"));
        }

        // New leak signals from cross-snapshot type comparison
        var allLeakSignals = overall
            .Where(r => r.NewLeakSignals.Count > 0)
            .SelectMany(r => r.NewLeakSignals.Select(s => (r.AnalyzerName, Signal: s)))
            .OrderByDescending(x => x.Signal.CurrentBytes)
            .Take(10)
            .ToList();

        if (allLeakSignals.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("NEW LEAK SIGNALS:"));
            blocks.Add(new DividerBlock());
            foreach (var (analyzerName, signal) in allLeakSignals)
            {
                string baseline = FormatHelper.FormatMetricValue(signal.BaselineBytes, "bytes");
                string current  = FormatHelper.FormatMetricValue(signal.CurrentBytes, "bytes");
                blocks.Add(new ListItemBlock(
                    $"[{signal.Source}] {signal.TypeName}: {baseline} \u2192 {current}"));
            }
        }

        blocks.Add(new DividerBlock());
        return new AnalyzerDetailSection("Trend Comparison", "Trend Comparison", 0, blocks);
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
