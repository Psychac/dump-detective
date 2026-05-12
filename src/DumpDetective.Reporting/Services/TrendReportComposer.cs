namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

internal sealed class TrendReportComposer(
    IEnumerable<IFindingGenerator> generators,
    CanonicalReportDocumentFactory documentFactory)
{
    private readonly IReadOnlyDictionary<string, IFindingGenerator> _generators =
        generators.ToDictionary(g => g.AnalyzerName, StringComparer.Ordinal);
    private readonly CanonicalReportDocumentFactory _documentFactory = documentFactory;

    public AnalysisReportDocument ComposeCanonicalTrendReport(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? currentIncidentContext,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
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

        AnalysisReportDocument baseDoc = _documentFactory.BuildDocument(dumpPath, currentRuns, elapsed, [], reportBuilders, audience, currentIncidentContext);

        // Build trend-specific analyzer sections
        var analyzerSections = new List<AnalyzerDetailSection>();
        analyzerSections.Add(BuildTrendComparisonSection(
            trendData.Steps,
            trendData.Overall,
            trendData.NewLeakSignalsByAnalyzer,
            lifecycle,
            trendData.Timeline,
            trendData.Snapshots));
        analyzerSections.AddRange(BuildPerDumpSections(trendData.Snapshots, builders, audience));

        return new TrendReportDocument
        {
            SchemaVersion = baseDoc.SchemaVersion,
            DumpPath = dumpPath,
            GeneratedAtUtc = baseDoc.GeneratedAtUtc,
            ElapsedSeconds = baseDoc.ElapsedSeconds,
            TrendDumpCount = trendData.Snapshots.Count,
            TrendDumpPaths = trendData.Snapshots.Select(s => s.DumpPath).ToList(),
            IncidentContext = currentIncidentContext is null
                ? baseDoc.IncidentContext
                : currentIncidentContext with
                {
                    TrendSnapshots = trendData.Snapshots.Select(s => new TrendSnapshotContext(
                        s.Index,
                        s.DumpPath,
                        s.GeneratedAtUtc,
                        0,
                        s.DomainResults.Count,
                        s.Findings.Count,
                        s.Index == 0,
                        s.Index == trendData.Snapshots.Count - 1)).ToList()
                },
            Findings = trendFindings.Select(MapFinding).ToList(),
            ExecutiveSummary = ComputeTrendExecutiveSummary(baseDoc, trendData.Snapshots, audience),
            DeveloperActionPlan = baseDoc.DeveloperActionPlan,
            Confidence = baseDoc.Confidence,
            AnalyzerSections = analyzerSections
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
                    RegressionSeverity.Severe => FindingSeverity.Critical,
                    RegressionSeverity.Moderate => FindingSeverity.Warning,
                    _ => FindingSeverity.Info
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

    // ── P1.2: Trend executive summary with score deltas ───────────────────────

    private ExecutiveSummaryRecord? ComputeTrendExecutiveSummary(
        AnalysisReportDocument baseDoc,
        IReadOnlyList<AnalysisSnapshot> snapshots,
        ReportAudience audience)
    {
        if (baseDoc.ExecutiveSummary is not { } summary)
            return null;

        // Need at least 2 snapshots to compute a meaningful delta.
        if (snapshots.Count < 2)
            return summary;

        AnalysisReportDocument firstDoc = _documentFactory.BuildSnapshotDocument(
            snapshots[0].DumpPath, BuildSnapshotRuns(snapshots[0]), [], audience);
        AnalysisReportDocument lastDoc = _documentFactory.BuildSnapshotDocument(
            snapshots[^1].DumpPath, BuildSnapshotRuns(snapshots[^1]), [], audience);

        if (firstDoc.ExecutiveSummary is not { } first || lastDoc.ExecutiveSummary is not { } last)
            return summary;

        return summary with
        {
            LeakScoreDelta = last.LeakLikelihoodScore - first.LeakLikelihoodScore,
            GcPressureScoreDelta = last.GcPressureScore - first.GcPressureScore,
            ThreadContentionScoreDelta = last.ThreadContentionScore - first.ThreadContentionScore,
        };
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
            IReadOnlyList<AnalyzerDetailSection> snapshotSections = _documentFactory
                .BuildSnapshotSections(snapshot.DumpPath, runs, builders, audience, snapshot.IncidentContext);
            IReadOnlyList<FindingRecord> findings = snapshot.Findings.Select(MapFinding).ToList();
            sections.Add(TrendSnapshotSectionComposer.Build(
                snapshot.DumpPath,
                snapshot.GeneratedAtUtc,
                findings,
                snapshot.IncidentContext,
                snapshotSections,
                i,
                snapshots.Count));
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
                    WarningCount: kvp.Value.Warnings.Count,
                    Diagnostics: new AnalyzerExecutionDiagnostics(
                        ObjectScanCount: 0,
                        CacheHits: 0,
                        CacheMisses: 0));
            })
            .ToList();
    }

    // ── Trend comparison section ──────────────────────────────────────────────

    private static AnalyzerDetailSection BuildTrendComparisonSection(
        IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> steps,
        IReadOnlyList<AnalyzerTrendResult> overall,
        IReadOnlyDictionary<string, IReadOnlyList<NewLeakSignal>> leakSignalsByAnalyzer,
        FindingLifecycleResult lifecycle,
        IReadOnlyList<AnalyzerMetricTimeline> timeline,
        IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        int totalRegressions = overall.Sum(r => r.Regressions.Count);
        int totalImprovements = overall.Sum(r => r.Improvements.Count);

        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("TREND COMPARISON"));
        blocks.Add(new HeadingBlock("LIFECYCLE SUMMARY:"));
        blocks.Add(new DividerBlock());
        blocks.Add(new MetricBlock("Dumps analyzed", snapshots.Count.ToString()));
        blocks.Add(new MetricBlock("New findings", lifecycle.NewFindings.Count.ToString()));
        blocks.Add(new MetricBlock("Persistent findings", lifecycle.PersistentFindings.Count.ToString()));
        blocks.Add(new MetricBlock("Resolved findings", lifecycle.ResolvedFindings.Count.ToString()));
        blocks.Add(new MetricBlock("Metric regressions", totalRegressions.ToString()));
        blocks.Add(new MetricBlock("Metric improvements", totalImprovements.ToString()));

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
                    double lastVal = point.Values.Last(v => !double.IsNaN(v));
                    double delta = lastVal - firstVal;
                    double? deltaPercent = Math.Abs(firstVal) > double.Epsilon
                        ? delta * 100.0 / firstVal
                        : null;

                    // compute severity inline from the direction/delta/percent
                    RegressionSeverity severity = RegressionSeverity.None;
                    bool isRegression = (point.Direction == MetricTrendDirection.HigherIsWorse && delta > 0)
                                     || (point.Direction == MetricTrendDirection.LowerIsWorse && delta < 0);
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
                                _ => RegressionSeverity.Severe
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

                    TrendClassification classification = ClassifyTrend(point.Direction, delta, severity);
                    string status = classification switch
                    {
                        TrendClassification.SevereRegression => "\u26a0\u26a0 Severe",
                        TrendClassification.Regression => "\u26a0 Regression",
                        TrendClassification.Improvement => "\u2705 Improvement",
                        _ => "\u2014 Stable"
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

        var newTypes = BuildNewTypes(snapshots);
        if (newTypes.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("NEW TYPES (BASELINE → CURRENT):"));
            blocks.Add(new DividerBlock());
            foreach (var entry in newTypes)
            {
                blocks.Add(new ListItemBlock($"{entry.TypeName}: {FormatHelper.FormatBytes(entry.CurrentBytes)} in latest dump"));
            }
        }

        var escalations = BuildSeverityEscalations(snapshots);
        if (escalations.Count > 0)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("SEVERITY ESCALATIONS:"));
            blocks.Add(new DividerBlock());
            foreach (var escalation in escalations)
            {
                blocks.Add(new ListItemBlock($"{escalation.Analyzer}: {escalation.Title} ({escalation.BaselineSeverity} -> {escalation.CurrentSeverity})"));
            }
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
        var allLeakSignals = leakSignalsByAnalyzer
            .SelectMany(kvp => kvp.Value.Select(s => (AnalyzerName: kvp.Key, Signal: s)))
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
                string current = FormatHelper.FormatMetricValue(signal.CurrentBytes, "bytes");
                blocks.Add(new ListItemBlock(
                    $"[{signal.Source}] {signal.TypeName}: {baseline} \u2192 {current}"));
            }
        }

        blocks.Add(new DividerBlock());
        return new AnalyzerDetailSection("Trend Comparison", "Trend Comparison", 0, blocks);
    }

    private static FindingRecord MapFinding(InsightFinding finding)
    {
        return new FindingRecord(
            Analyzer: finding.Analyzer,
            Category: finding.Category,
            Severity: finding.Severity.ToString(),
            Title: finding.Title,
            Evidence: finding.Evidence,
            Recommendation: finding.Recommendation,
            Tags: finding.Tags,
            Fingerprint: finding.EffectiveFingerprint)
        {
            EvidenceItems = SplitLines(finding.Evidence),
            RecommendationItems = SplitLines(finding.Recommendation),
            ConfidenceScore = finding.ConfidenceScore,
        };
    }

    private static IReadOnlyList<string>? SplitLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static TrendClassification ClassifyTrend(MetricTrendDirection direction, double delta, RegressionSeverity severity)
    {
        bool isRegression = (direction == MetricTrendDirection.HigherIsWorse && delta > 0)
                         || (direction == MetricTrendDirection.LowerIsWorse && delta < 0);
        bool isImprovement = (direction == MetricTrendDirection.HigherIsWorse && delta < 0)
                          || (direction == MetricTrendDirection.LowerIsWorse && delta > 0);

        if (isImprovement)
            return TrendClassification.Improvement;

        if (isRegression)
            return severity == RegressionSeverity.Severe ? TrendClassification.SevereRegression : TrendClassification.Regression;

        return TrendClassification.Stable;
    }

    private sealed record NewTypeEntry(string TypeName, ulong CurrentBytes);

    private sealed record SeverityEscalationEntry(string Analyzer, string Title, FindingSeverity BaselineSeverity, FindingSeverity CurrentSeverity);

    private static IReadOnlyList<NewTypeEntry> BuildNewTypes(IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        if (snapshots.Count < 2)
            return [];

        if (!TryGetMemorySnapshot(snapshots[0], out MemoryDomainResult baseline) ||
            !TryGetMemorySnapshot(snapshots[^1], out MemoryDomainResult current))
            return [];

        var baselineTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (TypeSnapshot type in baseline.TopTypesBySize)
            baselineTypes.Add(type.TypeName);

        var results = new List<NewTypeEntry>();
        foreach (TypeSnapshot type in current.TopTypesBySize.OrderByDescending(t => t.TotalBytes))
        {
            if (baselineTypes.Contains(type.TypeName))
                continue;

            results.Add(new NewTypeEntry(type.TypeName, type.TotalBytes));
            if (results.Count >= 10)
                break;
        }

        return results;
    }

    private static IReadOnlyList<SeverityEscalationEntry> BuildSeverityEscalations(IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        if (snapshots.Count < 2)
            return [];

        var baselineByFingerprint = snapshots[0].Findings
            .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var escalations = new List<SeverityEscalationEntry>();
        foreach (InsightFinding current in snapshots[^1].Findings)
        {
            if (!baselineByFingerprint.TryGetValue(current.EffectiveFingerprint, out InsightFinding? baseline))
                continue;

            if (baseline.Severity == FindingSeverity.Warning && current.Severity == FindingSeverity.Critical)
            {
                escalations.Add(new SeverityEscalationEntry(current.Analyzer, current.Title, baseline.Severity, current.Severity));
            }
        }

        return escalations;
    }

    private static bool TryGetMemorySnapshot(AnalysisSnapshot snapshot, out MemoryDomainResult result)
    {
        if (snapshot.DomainResults.TryGetValue("Memory Analysis", out AnalyzerDomainResult? raw) && raw is MemoryDomainResult memory)
        {
            result = memory;
            return true;
        }

        result = null!;
        return false;
    }

    private sealed record FindingLifecycleResult(
        IReadOnlyList<InsightFinding> NewFindings,
        IReadOnlyList<InsightFinding> PersistentFindings,
        IReadOnlyList<InsightFinding> ResolvedFindings);
}

internal sealed record TrendReportData(
    IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> Steps,
    IReadOnlyList<AnalyzerTrendResult> Overall,
    IReadOnlyDictionary<string, IReadOnlyList<NewLeakSignal>> NewLeakSignalsByAnalyzer,
    IReadOnlyList<AnalyzerMetricTimeline> Timeline,
    IReadOnlyList<AnalysisSnapshot> Snapshots,
    IReadOnlyList<InsightFinding> NewFindings,
    IReadOnlyList<InsightFinding> PersistentFindings,
    IReadOnlyList<InsightFinding> ResolvedFindings);
