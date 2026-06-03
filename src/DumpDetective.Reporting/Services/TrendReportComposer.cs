namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using System.Linq;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

internal sealed class TrendReportComposer(
    CanonicalReportDocumentFactory documentFactory,
    ExecutiveSummaryProjector? executiveSummaryProjector = null)
{
    private readonly CanonicalReportDocumentFactory _documentFactory = documentFactory;
    private readonly ExecutiveSummaryProjector _executiveSummaryProjector = executiveSummaryProjector ?? new ExecutiveSummaryProjector();

    public AnalysisReportDocument ComposeCanonicalTrendReport(
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? currentIncidentContext,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        TrendReportData trendData,
        ReportAudience audience = ReportAudience.All)
    {
        string dumpPath = trendData.Snapshots.Count > 0 ? trendData.Snapshots[^1].DumpPath : string.Empty;
        FindingLifecycleResult lifecycle = new(
            trendData.NewFindings,
            trendData.PersistentFindings,
            trendData.ResolvedFindings);

        var trendFindings = new List<InsightFinding>();
        foreach (InsightFinding f in BuildTrendFindings(trendData.Overall, lifecycle))
            trendFindings.Add(f);
        foreach (InsightFinding f in BuildTopRegressionFindings(trendData.Overall))
            trendFindings.Add(f);
        foreach (InsightFinding f in BuildTopImprovementFindings(trendData.Overall))
            trendFindings.Add(f);

        AnalysisReportDocument baseDoc = _documentFactory.BuildDocument(dumpPath, currentRuns, elapsed, Array.Empty<IAnalyzerSectionBuilder>(), reportBuilders, audience, currentIncidentContext);

        // T1.3: Build trend scorecard using all snapshots (not just baseline vs current)
        HealthScorecard? trendScorecard = trendData.Snapshots.Count >= 2
            ? TrendHealthScorecardBuilder.Build(trendData.Snapshots)
            : baseDoc.HealthScorecard;

        // T8: Build trend-specific analyzer sections using dedicated builders
        var analyzerSections = new List<AnalyzerDetailSection>();

        ExecutiveSummaryRecord? trendSummary = ComputeTrendExecutiveSummary(baseDoc, trendData.Snapshots, audience);

        // T9: Map trend findings with MetricBaseline/MetricCurrent populated
        var trendDeltaLookup = BuildTrendDeltaLookup(trendData.Overall);
        FindingRecord[] mappedFindings = trendFindings
            .Select(f => MapTrendFinding(f, trendData.Snapshots.Count - 1, trendDeltaLookup))
            .ToArray();

        // TV2-4: Classify regression findings (persisted on FindingRecord.RegressionClass)
        if (trendData.Snapshots.Count >= 1)
        {
            var baselineSeverityByFingerprint = trendData.Snapshots[0].Findings
                .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Severity.ToString(), StringComparer.Ordinal);

            var newFingerprints = new HashSet<string>(trendData.NewFindings.Select(f => f.EffectiveFingerprint), StringComparer.Ordinal);

            for (int i = 0; i < mappedFindings.Length; i++)
            {
                FindingRecord rec = mappedFindings[i];

                // Only classify regression-tagged findings
                if (!rec.Tags.Contains("regression"))
                    continue;

                string? cls = null;

                if (newFingerprints.Contains(rec.Id))
                {
                    cls = nameof(RegressionClass.NewRisk);
                }
                else
                {
                    // Amplified if severity increased relative to baseline
                    if (baselineSeverityByFingerprint.TryGetValue(rec.Id, out string? baseSevStr))
                    {
                        if (SeverityOrdinal(rec.Severity) > SeverityOrdinal(baseSevStr))
                            cls = nameof(RegressionClass.AmplifiedRisk);
                    }

                    // If not classified yet, check metric delta magnitude (20% default threshold)
                    if (cls is null && rec.MetricBaseline.HasValue && rec.MetricBaseline.GetValueOrDefault() != 0.0)
                    {
                        double baseline = rec.MetricBaseline.GetValueOrDefault();
                        double current = rec.MetricCurrent.GetValueOrDefault();
                        double pct = Math.Abs((current - baseline) / baseline) * 100.0;
                        if (pct >= 20.0)
                            cls = nameof(RegressionClass.AmplifiedRisk);
                    }
                }

                if (cls is null)
                    cls = nameof(RegressionClass.VolatileRisk);

                mappedFindings[i] = rec with { RegressionClass = cls };
            }
        }

        // T3 — Regression Dashboard (when there is anything to report)
        bool hasEscalations = trendData.Snapshots.Count >= 2 &&
            trendData.Snapshots[0].Findings.Any(f => trendData.Snapshots[^1].Findings
                .Any(c => c.EffectiveFingerprint == f.EffectiveFingerprint &&
                          f.Severity == FindingSeverity.Warning && c.Severity == FindingSeverity.Critical));
        if (trendData.NewFindings.Count > 0 || hasEscalations || trendData.NewLeakSignalsByAnalyzer.Values.Any(v => v.Count > 0))
            analyzerSections.Add(TrendRegressionDashboardBuilder.Build(trendData, trendData.Snapshots, mappedFindings));

        // T4 — Metric Timeline
        if (trendData.Timeline.Count > 0)
            analyzerSections.Add(TrendMetricTimelineSectionBuilder.Build(trendData, trendData.Snapshots));

        // T5 — Snapshot Strip
        analyzerSections.Add(TrendSnapshotStripBuilder.Build(trendData.Snapshots));

        // T6 — Per-dump sections (for text/markdown/JSON canonical output)
        analyzerSections.AddRange(BuildPerDumpSections(trendData.Snapshots, builders, reportBuilders, audience));

        // T6 — Per-dump full documents (serialized separately by HtmlReportRenderer; JS renders them via perDumpDocs)
        List<AnalysisReportDocument> perDumpDocuments = BuildPerDumpDocuments(trendData.Snapshots, builders, reportBuilders, audience);

        // T7 — Trend Appendix
        analyzerSections.Add(TrendAppendixBuilder.Build(trendData, currentRuns));

        // TrendAnalyzerSections: T2–T7 without per-dump entries — the JS renderer uses perDumpDocs directly
        var trendHtmlSections = analyzerSections
            .Where(static s => !s.SectionId.StartsWith("detail-", StringComparison.Ordinal))
            .ToArray();

        if (trendSummary is not null)
        {
            var topRegressions = SelectTopTrendFindings(mappedFindings, "regression", 5);
            var topImprovements = SelectTopTrendFindings(mappedFindings, "improvement", 3);
            trendSummary = trendSummary with
            {
                TopRegressions = topRegressions,
                TopImprovements = topImprovements,
            };
        }

        // Build T3b correlation events (heuristic co-occurrence). Cap and ordering per plan.
        var correlationEvents = CorrelationBuilder.BuildFrom(trendData, cap: 10);

        return new TrendReportDocument
        {
            SchemaVersion = baseDoc.SchemaVersion,
            ScoringModelVersion = baseDoc.ScoringModelVersion ?? "trend-v1",
            GeneratedAtUtc = baseDoc.GeneratedAtUtc,
            ElapsedSeconds = baseDoc.ElapsedSeconds,
            TrendDumpCount = trendData.Snapshots.Count,
            TrendDumpPaths = trendData.Snapshots.Select(s => s.DumpPath).ToArray(),
            TrendNewFindingCount = trendData.NewFindings.Count,
            TrendPersistentFindingCount = trendData.PersistentFindings.Count,
            TrendResolvedFindingCount = trendData.ResolvedFindings.Count,
            TrendStory = BuildTrendStory(trendData),
            HealthScorecard = trendScorecard,
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
                        s.Index == trendData.Snapshots.Count - 1,
                        s.IncidentContext?.DumpFileSizeBytes,
                        s.IncidentContext?.DumpCapturedAtUtc)).ToArray()
                },
            ExecutiveSummary = trendSummary,
            Findings = mappedFindings,
            AnalyzerSections = analyzerSections,
            TrendAnalyzerSections = trendHtmlSections,
            PerDumpDocuments = perDumpDocuments,
            CorrelationEvents = correlationEvents,
        };
    }

    // ── Trend findings ────────────────────────────────────────────────────────

    private static IReadOnlyList<InsightFinding> BuildTopRegressionFindings(IReadOnlyList<AnalyzerTrendResult> overall)
    {
        var collected = new List<(string Analyzer, MetricDelta Delta)>();
        foreach (var r in overall)
        {
            foreach (var d in r.Regressions)
                collected.Add((r.AnalyzerName, d));
        }

        var topRegressions = collected
            .OrderByDescending(x => Math.Abs(x.Delta.DeltaPercent ?? x.Delta.Delta))
            .Take(8)
            .ToArray();

        List<InsightFinding> findings = new(topRegressions.Length);
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
                Tags: new[] { "trend", "regression", analyzerName, BuildMetricIdentityToken(delta) },
                MetricValue: delta.DeltaPercent ?? delta.Delta,
                MetricUnit: delta.DeltaPercent.HasValue ? "%" : delta.Unit));
        }

        return findings;
    }

    private static IReadOnlyList<InsightFinding> BuildTopImprovementFindings(IReadOnlyList<AnalyzerTrendResult> overall)
    {
        var collectedImprovements = new List<(string Analyzer, MetricDelta Delta)>();
        foreach (var r in overall)
        {
            foreach (var d in r.Improvements)
                collectedImprovements.Add((r.AnalyzerName, d));
        }

        var topImprovements = collectedImprovements
            .OrderByDescending(x => Math.Abs(x.Delta.DeltaPercent ?? x.Delta.Delta))
            .Take(5)
            .ToArray();

        List<InsightFinding> findings = new(topImprovements.Length);
        foreach (var (analyzerName, delta) in topImprovements)
        {
            string scopeSuffix = string.IsNullOrWhiteSpace(delta.Scope) ? string.Empty : $" ({delta.Scope})";
            string deltaText = delta.DeltaPercent.HasValue
                ? $"{(delta.DeltaPercent.Value >= 0 ? "+" : string.Empty)}{delta.DeltaPercent.Value:F1}%"
                : $"{(delta.Delta >= 0 ? "+" : string.Empty)}{delta.Delta:F1} {delta.Unit}";

            findings.Add(new InsightFinding(
                Analyzer: "TrendAnalyzer",
                Category: "Comparison",
                Severity: FindingSeverity.Info,
                Title: $"Trend improvement: {analyzerName} / {delta.Key}{scopeSuffix}",
                Evidence: $"Metric moved from {FormatHelper.FormatMetricValue(delta.Baseline, delta.Unit)} to {FormatHelper.FormatMetricValue(delta.Current, delta.Unit)} ({deltaText}).",
                Recommendation: "Validate this improvement is stable across subsequent snapshots before closing related investigations.",
                Tags: new[] { "trend", "improvement", analyzerName, BuildMetricIdentityToken(delta) },
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

        return new List<InsightFinding>
        {
            new(
                Analyzer: "TrendAnalyzer",
                Category: "Comparison",
                Severity: lifecycleSeverity,
                Title: "Trend finding lifecycle summary",
                Evidence: $"New {lifecycle.NewFindings.Count}, Persistent {lifecycle.PersistentFindings.Count}, Resolved {lifecycle.ResolvedFindings.Count}",
                Recommendation: "Focus first on new and persistent high-severity findings.",
                Tags: new[] { "trend", "lifecycle", "comparison" },
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
                Tags: new[] { "trend", "metrics", "comparison" })
        };
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

        HealthScorecard firstScorecard = HealthScorecardBuilder.Build(snapshots[0].Runs);
        HealthScorecard lastScorecard = HealthScorecardBuilder.Build(snapshots[^1].Runs);
        var regressionLookup = new Dictionary<string, MetricDelta>(StringComparer.Ordinal);

        var firstFindings = snapshots[0].Findings
            .Select(f => MapTrendFinding(f, snapshots[0].Index, regressionLookup))
            .ToArray();
        firstFindings = SortFindings(firstFindings).ToArray();

        var lastFindings = snapshots[^1].Findings
            .Select(f => MapTrendFinding(f, snapshots[^1].Index, regressionLookup))
            .ToArray();
        lastFindings = SortFindings(lastFindings).ToArray();

        ExecutiveSummaryRecord first = _executiveSummaryProjector.Build(firstFindings, firstScorecard, snapshots[0].Runs);
        ExecutiveSummaryRecord last = _executiveSummaryProjector.Build(lastFindings, lastScorecard, snapshots[^1].Runs);

        return summary with
        {
            LeakScoreDelta = last.LeakLikelihoodScore - first.LeakLikelihoodScore,
            GcPressureScoreDelta = last.GcPressureScore - first.GcPressureScore,
            ThreadContentionScoreDelta = last.ThreadContentionScore - first.ThreadContentionScore,
        };
    }

    private static IReadOnlyList<FindingRecord> SortFindings(IReadOnlyList<FindingRecord> findings)
    {
        var arr = findings.ToArray();
        Array.Sort(arr, static (a, b) =>
        {
            int severityCompare = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
            if (severityCompare != 0)
                return severityCompare;

            int catCompare = StringComparer.Ordinal.Compare(NormalizeSortKey(a.Category), NormalizeSortKey(b.Category));
            if (catCompare != 0)
                return catCompare;

            return StringComparer.Ordinal.Compare(NormalizeSortKey(a.Title), NormalizeSortKey(b.Title));
        });

        return arr;
    }

    private static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning) => 1,
        _ => 0
    };

    private static string NormalizeSortKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    // ── Per-dump sections (canonical text/markdown/JSON output) ──────────────

    private IReadOnlyList<AnalyzerDetailSection> BuildPerDumpSections(
        IReadOnlyList<AnalysisSnapshot> snapshots,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        ReportAudience audience)
    {
        var sections = new List<AnalyzerDetailSection>();

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];
            IReadOnlyList<AnalyzerDetailSection> snapshotSections = _documentFactory
                .BuildSnapshotSections(snapshot.DumpPath, snapshot.Runs, builders, reportBuilders, audience, snapshot.IncidentContext);
            IReadOnlyList<FindingRecord> findings = snapshot.Findings.Select(f => MapFinding(f, snapshot.Index)).ToArray();

            sections.Add(TrendSnapshotSectionComposer.Build(
                snapshot.DumpPath,
                snapshot.GeneratedAtUtc,
                findings,
                snapshot.IncidentContext,
                [],
                i,
                snapshots.Count,
                snapshot: snapshot,
                baseline: i == 0 ? null : snapshots[0]));

            for (int sectionIndex = 0; sectionIndex < snapshotSections.Count; sectionIndex++)
            {
                AnalyzerDetailSection section = snapshotSections[sectionIndex];
                string sectionId = string.IsNullOrWhiteSpace(section.SectionId)
                    ? $"detail-{i}-analyzer-{sectionIndex}"
                    : $"detail-{i}-{section.SectionId}";

                sections.Add(section with
                {
                    SortOrder = (i * 1000) + 300 + section.SortOrder,
                    SectionId = sectionId,
                    Domain = "SnapshotDetail"
                });
            }
        }

        return sections;
    }

    private List<AnalysisReportDocument> BuildPerDumpDocuments(
        IReadOnlyList<AnalysisSnapshot> snapshots,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        ReportAudience audience)
    {
        var docs = new List<AnalysisReportDocument>(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];
            // Build a full single-dump document — same path that produces standalone single-dump reports.
            AnalysisReportDocument fullDoc = _documentFactory.BuildDocument(
                snapshot.DumpPath, snapshot.Runs, TimeSpan.Zero, builders, reportBuilders, audience, snapshot.IncidentContext);
            docs.Add(fullDoc);
        }

        return docs;
    }

    private static TrendStoryRecord? BuildTrendStory(TrendReportData trendData)
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
                .ToArray();

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
                    double?[] safeValues = point.Values
                        .Select(static v => double.IsFinite(v) ? (double?)v : null)
                        .ToArray();
                    string sparkPayload = System.Text.Json.JsonSerializer.Serialize(new { values = safeValues, unit = point.Unit });
                    string sparkToken = "__SPARK__" + sparkPayload;
                    // Use zero-based snapshot index to match `detail-{i}` IDs generated in the report
                    string metricDisplay = point.Key + "||__LINK__detail-" + linkSnapshot;

                    rows.Add(new TableRow(new[]
                    {
                        new TableCell(metricDisplay),
                        new TableCell(sparkToken),
                        new TableCell(deltaDisplay, delta == 0 ? 0L : (long)Math.Round(Math.Abs(delta))),
                        new TableCell(status)
                    }));
                }

                if (rows.Count > 0)
                {
                    blocks.Add(new BlankBlock());
                    blocks.Add(new HeadingBlock($"[{analyzerTimeline.AnalyzerName}]", 1));
                    blocks.Add(new TableBlock(
                        Caption: $"{analyzerTimeline.AnalyzerName} metric timeline",
                        Headers: new[] { "Metric", $"Trend ({snapshots.Count} snapshots)", "\u0394", "Status" },
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
            .ToArray();

        if (allLeakSignals.Length > 0)
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

    // ── T9: Trend delta lookup for MetricBaseline/MetricCurrent ─────────────

    private static Dictionary<string, MetricDelta> BuildTrendDeltaLookup(IReadOnlyList<AnalyzerTrendResult> overall)
    {
        var lookup = new Dictionary<string, MetricDelta>(StringComparer.Ordinal);
        foreach (AnalyzerTrendResult result in overall)
        {
            foreach (MetricDelta delta in result.Regressions)
            {
                // Key by "AnalyzerName/MetricKey" to identify a regression finding
                string key = BuildDeltaLookupKey(result.AnalyzerName, delta);
                lookup.TryAdd(key, delta);
            }
            foreach (MetricDelta delta in result.Improvements)
            {
                string key = BuildDeltaLookupKey(result.AnalyzerName, delta);
                lookup.TryAdd(key, delta);
            }
        }
        return lookup;
    }

    private static FindingRecord MapTrendFinding(
        InsightFinding finding,
        int? snapshotIndex,
        Dictionary<string, MetricDelta> regressionLookup)
    {
        FindingRecord record = MapFinding(finding, snapshotIndex);

        // T9: Inject MetricBaseline/MetricCurrent for regression findings
        if (finding.Tags.Contains("regression") || finding.Tags.Contains("improvement"))
        {
            // Tags are: ["trend","regression",analyzerName,metricKey]
            string? analyzerName = finding.Tags.Count > 2 ? finding.Tags[2] : null;
            string? metricToken  = finding.Tags.Count > 3 ? finding.Tags[3] : null;
            if (analyzerName != null && metricToken != null &&
                regressionLookup.TryGetValue($"{analyzerName}/{metricToken}", out MetricDelta? delta))
            {
                record = record with
                {
                    MetricBaseline = delta.Baseline,
                    MetricCurrent  = delta.Current,
                    MetricUnit     = delta.Unit,
                };
            }
        }

        return record;
    }

    private static IReadOnlyList<FindingRecord> SelectTopTrendFindings(
        IReadOnlyList<FindingRecord> findings,
        string tag,
        int take)
    {
        return findings
            .Where(f => f.Tags.Contains(tag))
            .OrderByDescending(f => SeverityRank(f.Severity))
            .ThenByDescending(f => Math.Abs(f.MetricCurrent.GetValueOrDefault() - f.MetricBaseline.GetValueOrDefault()))
            .Take(take)
            .ToArray();
    }

    private static int SeverityRank(string severity)
    {
        return severity switch
        {
            "Critical" => 3,
            "Warning" => 2,
            "Info" => 1,
            _ => 0,
        };
    }

    private static string BuildDeltaLookupKey(string analyzerName, MetricDelta delta)
        => $"{analyzerName}/{BuildMetricIdentityToken(delta)}";

    private static string BuildMetricIdentityToken(MetricDelta delta)
        => string.IsNullOrWhiteSpace(delta.Scope)
            ? delta.Key
            : delta.Key + "\u001f" + delta.Scope;

    private static FindingRecord MapFinding(InsightFinding finding, int? snapshotIndex = null)
    {
        IReadOnlyList<string>? details = SplitLines(finding.Evidence);

        return new FindingRecord(
            Id: finding.EffectiveFingerprint,
            Analyzer: finding.Analyzer,
            Category: finding.Category,
            Severity: finding.Severity.ToString(),
            Title: finding.Title,
            Details: details,
            Recommendation: finding.Recommendation,
            Tags: finding.Tags)
        {
            Confidence = finding.ConfidenceScore,
            Caveats = finding.EffectiveCaveats.Count > 0 ? finding.EffectiveCaveats : null,
            Refs = new[]
            {
                new EvidenceRef(
                    Analyzer: finding.Analyzer,
                    MetricKey: finding.Tags.FirstOrDefault(t => t.Contains('.', StringComparison.Ordinal) || t.Contains('_', StringComparison.Ordinal)),
                    Addresses: null,
                    ArtifactPath: null,
                    SnapshotIndex: snapshotIndex)
            },
        };
    }

    private static IReadOnlyList<string>? SplitLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
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
            return Array.Empty<NewTypeEntry>();

        if (!TryGetMemorySnapshot(snapshots[0], out MemoryDomainResult baseline) ||
            !TryGetMemorySnapshot(snapshots[^1], out MemoryDomainResult current))
            return Array.Empty<NewTypeEntry>();

        var baselineTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (TypeSnapshot type in baseline.TopTypes)
            baselineTypes.Add(type.TypeName);

        var results = new List<NewTypeEntry>();
        foreach (TypeSnapshot type in current.TopTypes.OrderByDescending(t => t.TotalBytes))
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
            return Array.Empty<SeverityEscalationEntry>();

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
    IReadOnlyList<AnalyzerMetricTimeline> ScopedTimeline,
    IReadOnlyList<AnalysisSnapshot> Snapshots,
    IReadOnlyList<InsightFinding> NewFindings,
    IReadOnlyList<InsightFinding> PersistentFindings,
    IReadOnlyList<InsightFinding> ResolvedFindings);
