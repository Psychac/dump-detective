using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Maps <see cref="AnalyzerRunResult"/> list → <see cref="AnalysisReportDocument"/>.
/// Pure function — no text formatting, no side effects, no I/O.
/// </summary>
internal sealed class ReportSerializer(ExecutiveSummaryProjector? executiveSummaryProjector = null)
{
    private readonly ExecutiveSummaryProjector _executiveSummaryProjector = executiveSummaryProjector ?? new ExecutiveSummaryProjector();

    public AnalysisReportDocument Serialize(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null,
        IReadOnlyList<InsightFinding>? additionalFindings = null)
    {
        // ── 1. Build per-analyzer sections ───────────────────────────────────
        List<AnalyzerDetailSection> analyzerSections = ReportSectionAssembler.BuildAnalyzerSections(runs, analyzerBuilders);
        AnalyzerResultSet resultSet = new(runs, incidentContext, additionalFindings);
        List<AnalyzerDetailSection> specSections = ReportSectionAssembler.BuildSpecSections(resultSet, reportBuilders);
        analyzerSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        specSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        List<AnalyzerDetailSection> mergedSections = ReportSectionAssembler.MergeSections(analyzerSections, specSections);

        // Annotate sections with Domain / SectionId / LeadSeverity via SectionIdDomainMap
        ReportSectionAssembler.ApplySectionMetadata(mergedSections, runs);

        // Extract typed contract slots (LeadFinding, KeyMetrics, Tables, Provenance) from block stream
        ReportSectionAssembler.NormalizeSectionContractSlots(mergedSections, runs);

        // Apply domain-priority ordering (Critical domains first, then by domain priority, then SortOrder)
        ReportSectionAssembler.ApplyDomainOrdering(mergedSections);

        // ── 2. Map all findings to FindingRecord + collect pipeline failures ──
        List<FindingRecord> deduped = MapAllFindings(runs, additionalFindings);

        IReadOnlyList<ReportDomainSection> domains = ReportDomainProjector.BuildDomainSections(mergedSections, deduped);
        IReadOnlyList<FindingRecord> crossDomainInsights = ReportDomainProjector.BuildCrossDomainInsights(deduped);
        IReadOnlyList<CorrelationEventRecord> correlationEvents = ReportCorrelationBuilder.BuildCorrelationEvents(deduped);
        ReportAppendix appendix = ReportSectionAssembler.BuildAppendix(runs);

        // Sort: Critical → Warning → Info, then by Category, then by Title
        SortFindingsBySeverity(deduped);

        // ── 4. Audience-specific projections ─────────────────────────────────
        long totalManagedBytes = _executiveSummaryProjector.ComputeTotalManagedBytes(runs);

        // Always include Executive summary (previously included for Executive/All)
        HealthScorecard scorecard = HealthScorecardBuilder.Build(runs);
        ExecutiveSummaryRecord? executiveSummary = _executiveSummaryProjector.Build(deduped, runs, totalManagedBytes);

        string? analyzerVersion = typeof(ReportSerializer).Assembly.GetName().Version?.ToString(3);

        var resultDoc = new SingleDumpReportDocument
        {
            DumpPath = dumpPath,
            ScoringModelVersion = ActionPriorityService.ScoringModelVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = elapsed.TotalSeconds,
            AnalyzerVersion = analyzerVersion,
            IncidentContext = incidentContext,
            HealthScorecard = scorecard,
            Domains = domains,
            CrossDomainInsights = crossDomainInsights,
            CorrelationEvents = correlationEvents,
            Appendix = appendix,
            ExecutiveSummary = executiveSummary,
        };

        // In-memory only: make the composed findings available to tests and callers.
        resultDoc = resultDoc with { Findings = deduped };

        return resultDoc;
    }

    /// <summary>
    /// Lightweight projection carrying only the scalar fields and executive-summary/health-scorecard
    /// data that trend composition reads from a "current dump" document. Skips section assembly,
    /// domain projection, cross-domain insights, correlation events, and appendix building, since
    /// none of those are read from the result.
    /// </summary>
    public AnalysisReportDocument SerializeBaseProjection(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null,
        IReadOnlyList<InsightFinding>? additionalFindings = null)
    {
        List<FindingRecord> deduped = MapAllFindings(runs, additionalFindings);
        SortFindingsBySeverity(deduped);

        long totalManagedBytes = _executiveSummaryProjector.ComputeTotalManagedBytes(runs);
        HealthScorecard scorecard = HealthScorecardBuilder.Build(runs);
        ExecutiveSummaryRecord? executiveSummary = _executiveSummaryProjector.Build(deduped, runs, totalManagedBytes);
        string? analyzerVersion = typeof(ReportSerializer).Assembly.GetName().Version?.ToString(3);

        var resultDoc = new SingleDumpReportDocument
        {
            DumpPath = dumpPath,
            ScoringModelVersion = ActionPriorityService.ScoringModelVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = elapsed.TotalSeconds,
            AnalyzerVersion = analyzerVersion,
            IncidentContext = incidentContext,
            HealthScorecard = scorecard,
            ExecutiveSummary = executiveSummary,
        };

        return resultDoc with { Findings = deduped };
    }

    private static List<FindingRecord> MapAllFindings(
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<InsightFinding>? additionalFindings)
    {
        List<FindingRecord> allFindings = [];

        foreach (AnalyzerRunResult run in runs)
        {
            if (run.Findings is { Count: > 0 })
            {
                foreach (InsightFinding finding in run.Findings)
                {
                    allFindings.Add(ReportFindingMapper.MapFinding(finding, run.Artifacts));
                }
            }

            if (run.Status == AnalyzerExecutionStatus.Failed)
            {
                string evidence = run.ErrorMessage ?? "Analyzer failed without error details.";
                allFindings.Add(new FindingRecord(
                    Id: $"analyzer-failure:{run.AnalyzerName}",
                    Analyzer: run.AnalyzerName,
                    Category: "Pipeline",
                    Severity: nameof(FindingSeverity.Warning),
                    Title: $"Analyzer failed: {run.AnalyzerName}",
                    Details: [evidence],
                    Recommendation: "Inspect analyzer failure details and re-run analysis.",
                    Tags: []));
            }

            if (!string.IsNullOrWhiteSpace(run.FindingGeneratorError))
            {
                string evidence = $"The finding generator for '{run.AnalyzerName}' threw an exception. Findings for this analyzer may be incomplete or missing.";
                allFindings.Add(new FindingRecord(
                    Id: $"finding-generator-error:{run.AnalyzerName}",
                    Analyzer: run.AnalyzerName,
                    Category: "Pipeline",
                    Severity: nameof(FindingSeverity.Warning),
                    Title: $"Finding generator failed: {run.AnalyzerName}",
                    Details: [evidence],
                    Recommendation: "Re-run analysis. If the error persists, report it with the full error details.",
                    Tags: []));
            }
        }

        if (additionalFindings is { Count: > 0 })
        {
            foreach (InsightFinding finding in additionalFindings)
                allFindings.Add(ReportFindingMapper.MapFinding(finding));
        }

        return allFindings;
    }

    private static void SortFindingsBySeverity(List<FindingRecord> findings)
    {
        findings.Sort(static (a, b) =>
        {
            int severityCompare = ReportDomainProjector.SeverityOrdinal(b.Severity).CompareTo(ReportDomainProjector.SeverityOrdinal(a.Severity));
            if (severityCompare != 0) return severityCompare;
            int catCompare = StringComparer.Ordinal.Compare(ReportDomainProjector.NormalizeSortKey(a.Category), ReportDomainProjector.NormalizeSortKey(b.Category));
            if (catCompare != 0) return catCompare;
            return StringComparer.Ordinal.Compare(ReportDomainProjector.NormalizeSortKey(a.Title), ReportDomainProjector.NormalizeSortKey(b.Title));
        });
    }

    public IReadOnlyList<AnalyzerDetailSection> SerializeSectionsOnly(
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null)
    {
        List<AnalyzerDetailSection> analyzerSections = ReportSectionAssembler.BuildAnalyzerSections(runs, analyzerBuilders);
        AnalyzerResultSet resultSet = new(runs, incidentContext);
        List<AnalyzerDetailSection> specSections = ReportSectionAssembler.BuildSpecSections(resultSet, reportBuilders);
        analyzerSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        specSections.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        List<AnalyzerDetailSection> mergedSections = ReportSectionAssembler.MergeSections(analyzerSections, specSections);

        // Keep section shape parity with full report serialization.
        ReportSectionAssembler.ApplySectionMetadata(mergedSections, runs);
        ReportSectionAssembler.NormalizeSectionContractSlots(mergedSections, runs);
        ReportSectionAssembler.ApplyDomainOrdering(mergedSections);

        return mergedSections;
    }
}
