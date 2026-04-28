using DumpDetective.Core.Configuration;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Services;

internal sealed class ReportBuilderFacade(
    IEnumerable<IReportFormatter> formatters,
    ISectionBuilderFactory builderFactory,
    ReportSerializer serializer,
    TrendReportComposer trendReportComposer)
{
    private readonly IReadOnlyList<IReportFormatter>        _formatters    = formatters.ToList();
    private readonly IReadOnlyList<IAnalyzerSectionBuilder> _builders      = builderFactory.CreateBuilders();
    private readonly ReportSerializer                       _serializer    = serializer;
    private readonly TrendReportComposer                    _trendComposer = trendReportComposer;

    public string BuildRenderedReport(
        string dumpPath,
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = _serializer.Serialize(dumpPath, runs, elapsed, _builders, audience);
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");
        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(doc);
    }

    public string BuildRenderedTrendReport(
        string dumpPath,
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        TrendReportData trendData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // TrendReportComposer still returns ComposedReport — bridged until it is migrated.
        ComposedReport composed = _trendComposer.ComposeCanonicalTrendReport(
            dumpPath, currentRuns, elapsed,
            reporters: [],    // reporters removed; builders handle structured sections
            trendData, audience);

        AnalysisReportDocument doc = BridgeComposedReport(composed);

        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");
        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(doc);
    }

    // Bridge: maps ComposedReport (legacy TrendReportComposer output) → AnalysisReportDocument
    private static AnalysisReportDocument BridgeComposedReport(ComposedReport composed)
    {
        var findings = new List<FindingRecord>(composed.Sections.Count);
        foreach (ReportSection s in composed.Sections)
        {
            findings.Add(new FindingRecord(
                Analyzer:       s.Category,
                Category:       s.Category,
                Severity:       s.Severity.ToString(),
                Title:          s.Title,
                Evidence:       s.NarrativeSummary,
                Recommendation: string.Join(" ", s.RemediationHints),
                Tags:           s.Fingerprints,
                Fingerprint:    s.SectionKey));
        }

        var analyzerSections = new List<AnalyzerDetailSection>();
        if (composed.DetailedAnalyzerSections is { Count: > 0 })
        {
            for (int i = 0; i < composed.DetailedAnalyzerSections.Count; i++)
            {
                DetailedAnalyzerSection ds = composed.DetailedAnalyzerSections[i];
                analyzerSections.Add(new AnalyzerDetailSection(
                    ds.Title, ds.Title, i * 10 + 100,
                    [new TextBlock(ds.Content)]));
            }
        }

        return new AnalysisReportDocument
        {
            DumpPath         = composed.DumpPath,
            GeneratedAtUtc   = composed.GeneratedAtUtc,
            ElapsedSeconds   = composed.Elapsed.TotalSeconds,
            IsTrendReport    = composed.IsTrendReport,
            TrendDumpCount   = composed.TrendDumpCount,
            TrendDumpPaths   = composed.TrendDumpPaths,
            Findings         = findings,
            AnalyzerSections = analyzerSections,
            DedupDiagnostics = new DedupRecord(
                MergedSections:      composed.DedupDiagnostics.MergedSections,
                DuplicateCandidates: composed.DedupDiagnostics.DuplicateCandidates,
                EvidenceBeforeMerge: composed.DedupDiagnostics.EvidenceBeforeMerge)
        };
    }
}
