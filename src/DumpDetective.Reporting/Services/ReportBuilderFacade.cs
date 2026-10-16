using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

internal sealed class ReportBuilderFacade(
    IEnumerable<IReportFormatter> formatters,
    ISectionBuilderFactory builderFactory,
    ReportSerializer serializer,
    TrendReportComposer trendReportComposer)
{
    private readonly IReadOnlyList<IReportFormatter> _formatters = formatters.ToArray();
    private readonly IReadOnlyList<IAnalyzerSectionBuilder> _analyzerBuilders = builderFactory.CreateAnalyzerBuilders();
    private readonly IReadOnlyList<IReportSectionBuilder> _reportBuilders = builderFactory.CreateReportBuilders();
    private readonly ReportSerializer _serializer = serializer;
    private readonly TrendReportComposer _trendComposer = trendReportComposer;

    public string BuildRenderedReport(
        string dumpPath,
        ReportFormat format,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
        => BuildRenderedReport(dumpPath, format, runs, elapsed, null, cancellationToken);

    public string BuildRenderedReport(
        string dumpPath,
        ReportFormat format,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = BuildReportDocument(dumpPath, runs, elapsed, incidentContext);
        cancellationToken.ThrowIfCancellationRequested();
        return RenderDocument(doc, format);
    }

    public string BuildRenderedTrendReport(
        ReportFormat format,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        TrendReportData trendData,
        CancellationToken cancellationToken)
        => BuildRenderedTrendReport(format, currentRuns, elapsed, null, trendData, cancellationToken);

    public string BuildRenderedTrendReport(
        ReportFormat format,
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext,
        TrendReportData trendData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisReportDocument doc = BuildTrendReportDocument(currentRuns, elapsed, incidentContext, trendData);
        cancellationToken.ThrowIfCancellationRequested();
        return RenderDocument(doc, format);
    }

    public AnalysisReportDocument BuildTrendReportDocument(
        IReadOnlyList<AnalyzerRunResult> currentRuns,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext,
        TrendReportData trendData)
        => _trendComposer.ComposeCanonicalTrendReport(
            currentRuns, elapsed, incidentContext, _analyzerBuilders, _reportBuilders, trendData);

    public AnalysisReportDocument BuildReportDocument(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        AnalysisIncidentContext? incidentContext = null,
        IReadOnlyList<Claim>? additionalFindings = null,
        IReadOnlyDictionary<string, EvidenceSources>? evidenceSourcesByFingerprint = null)
        => _serializer.Serialize(dumpPath, runs, elapsed, _analyzerBuilders, _reportBuilders, incidentContext, additionalFindings, evidenceSourcesByFingerprint);

    public string RenderDocument(AnalysisReportDocument doc, ReportFormat format, HtmlRenderSettings? htmlRenderSettings = null)
    {
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");

        if (formatter is HtmlReportRenderer htmlFormatter)
            return htmlFormatter.Render(doc, htmlRenderSettings ?? HtmlRenderSettings.Default);

        return formatter.Render(doc);
    }

    /// <summary>Writes <c>report.json</c> to a stream without building it as a string, which fails past about a billion characters.</summary>
    public Task WriteJsonAsync(AnalysisReportDocument doc, Stream output, CancellationToken cancellationToken)
    {
        JsonCanonicalReportFormatter formatter = _formatters.OfType<JsonCanonicalReportFormatter>().FirstOrDefault()
            ?? throw new InvalidOperationException("No JSON formatter registered.");

        return formatter.WriteAsync(doc, output, cancellationToken);
    }
}
