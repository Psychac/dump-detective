using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Services;

internal sealed class ReportBuilderFacade(
    IEnumerable<IReportFormatter> formatters,
    ISectionBuilderFactory builderFactory,
    CanonicalReportDocumentFactory documentFactory,
    TrendReportComposer trendReportComposer)
{
    private readonly IReadOnlyList<IReportFormatter> _formatters = formatters.ToList();
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
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = BuildReportDocument(dumpPath, audience, runs, elapsed, incidentContext);
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
        => BuildRenderedTrendReport(dumpPath, format, audience, currentRuns, elapsed, null, trendData, cancellationToken);

    public string BuildRenderedTrendReport(
        string dumpPath,
        ReportFormat format,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext,
        TrendReportData trendData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = BuildTrendReportDocument(
            dumpPath,
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
        string dumpPath,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext,
        TrendReportData trendData)
        => _trendComposer.ComposeCanonicalTrendReport(
            dumpPath, currentRuns, elapsed, incidentContext, _analyzerBuilders, _reportBuilders, trendData, audience);

    public AnalysisReportDocument BuildReportDocument(
        string dumpPath,
        ReportAudience audience,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext = null)
    {
        return _documentFactory.BuildDocument(dumpPath, runs, elapsed, _analyzerBuilders, _reportBuilders, audience, incidentContext);
    }

    public string RenderDocument(AnalysisReportDocument doc, ReportFormat format)
    {
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");
        return formatter.Render(doc);
    }
}
