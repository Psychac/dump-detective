namespace DumpDetective.Reporting.Services;

using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using System.Linq;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Enums;

internal sealed class TrendReportComposer(
    ReportSerializer serializer,
    ExecutiveSummaryProjector? executiveSummaryProjector = null)
{
    private readonly ReportSerializer _serializer = serializer;
    private readonly ExecutiveSummaryProjector _executiveSummaryProjector = executiveSummaryProjector ?? new ExecutiveSummaryProjector();

    public AnalysisReportDocument ComposeCanonicalTrendReport(
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? currentIncidentContext,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        TrendReportData trendData)
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

        AnalysisReportDocument baseDoc = _serializer.SerializeBaseProjection(dumpPath, currentRuns, elapsed, currentIncidentContext);

        // T1.3: Build trend scorecard using all snapshots (not just baseline vs current)
        HealthScorecard? trendScorecard = trendData.Snapshots.Count >= 2
            ? TrendHealthScorecardBuilder.Build(trendData.Snapshots)
            : baseDoc.HealthScorecard;

        // T8: Build trend-specific analyzer sections using dedicated builders
        var analyzerSections = new List<AnalyzerDetailSection>();

        ExecutiveSummaryRecord? trendSummary = ComputeTrendExecutiveSummary(baseDoc, trendData.Snapshots);

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
        analyzerSections.AddRange(BuildPerDumpSections(trendData.Snapshots, builders, reportBuilders));

        // T6 — Per-dump full documents (serialized separately by HtmlReportRenderer; JS renders them via perDumpDocs)
        List<AnalysisReportDocument> perDumpDocuments = BuildPerDumpDocuments(trendData.Snapshots, builders, reportBuilders);

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
            TrendStory = TrendStoryBuilder.Build(trendData),
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
        IReadOnlyList<AnalysisSnapshot> snapshots)
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

        ExecutiveSummaryRecord first = _executiveSummaryProjector.Build(firstFindings, snapshots[0].Runs);
        ExecutiveSummaryRecord last = _executiveSummaryProjector.Build(lastFindings, snapshots[^1].Runs);

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
        IReadOnlyList<IReportSectionBuilder> reportBuilders)
    {
        var sections = new List<AnalyzerDetailSection>();

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];
            IReadOnlyList<AnalyzerDetailSection> snapshotSections = _serializer
                .SerializeSectionsOnly(snapshot.Runs, builders, reportBuilders, snapshot.IncidentContext);
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
        IReadOnlyList<IReportSectionBuilder> reportBuilders)
    {
        var docs = new List<AnalysisReportDocument>(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            AnalysisSnapshot snapshot = snapshots[i];
            // Build a full single-dump document — same path that produces standalone single-dump reports.
            AnalysisReportDocument fullDoc = _serializer.Serialize(
                snapshot.DumpPath, snapshot.Runs, TimeSpan.Zero, builders, reportBuilders, snapshot.IncidentContext);
            docs.Add(fullDoc);
        }

        return docs;
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
