using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

internal sealed class ReportBuilderFacade(
    IEnumerable<IReportFormatter> formatters,
    ISectionBuilderFactory builderFactory,
    CanonicalReportDocumentFactory documentFactory,
    TrendReportComposer trendReportComposer)
{
    private readonly IReadOnlyList<IReportFormatter> _formatters = formatters.ToArray();
    private readonly IReadOnlyList<IAnalyzerSectionBuilder> _analyzerBuilders = builderFactory.CreateAnalyzerBuilders();
    private readonly IReadOnlyList<IReportSectionBuilder> _reportBuilders = builderFactory.CreateReportBuilders();
    private readonly CanonicalReportDocumentFactory _documentFactory = documentFactory;
    private readonly TrendReportComposer _trendComposer = trendReportComposer;

    public string BuildRenderedReport(
        string dumpPath,
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
        => BuildRenderedReport(dumpPath, format, audience, runs, elapsed, null, cancellationToken);

    public string BuildRenderedReport(
        string dumpPath,
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = BuildReportDocument(dumpPath, audience, runs, elapsed, incidentContext);
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");
        cancellationToken.ThrowIfCancellationRequested();
        if (formatter is HtmlReportRenderer htmlFormatter)
        {
            // Trend reports respect the v2 visual style per TV2-1 plan (use v2 token)
            return htmlFormatter.Render(doc, new HtmlRenderSettings(PreRender: false, StyleVersion: ReportStyleVersion.V2));
        }
        return formatter.Render(doc);
    }

    public string BuildRenderedTrendReport(
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        TrendReportData trendData,
        CancellationToken cancellationToken)
        => BuildRenderedTrendReport(format, audience, currentRuns, elapsed, null, trendData, cancellationToken);

    public string BuildRenderedTrendReport(
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext,
        TrendReportData trendData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = BuildTrendReportDocument(
            audience,
            currentRuns,
            elapsed,
            incidentContext,
            trendData);

        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");
        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(doc);
    }

    public AnalysisReportDocument BuildTrendReportDocument(
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext,
        TrendReportData trendData)
        => _trendComposer.ComposeCanonicalTrendReport(
            currentRuns, elapsed, incidentContext, _analyzerBuilders, _reportBuilders, trendData, audience);

    public AnalysisReportDocument BuildReportDocument(
        string dumpPath,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext = null,
        IReadOnlyList<InsightFinding>? additionalFindings = null)
    {
        return _documentFactory.BuildDocument(dumpPath, runs, elapsed, _analyzerBuilders, _reportBuilders, audience, incidentContext, additionalFindings);
    }

    public string RenderDocument(AnalysisReportDocument doc, ReportFormat format, HtmlRenderSettings? htmlRenderSettings = null)
    {
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");

        if (formatter is HtmlReportRenderer htmlFormatter)
            return htmlFormatter.Render(doc, htmlRenderSettings ?? HtmlRenderSettings.Default);

        return formatter.Render(doc);
    }
}