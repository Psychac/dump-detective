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

        AnalysisReportDocument doc = _trendComposer.ComposeCanonicalTrendReport(
            dumpPath, currentRuns, elapsed, _builders, trendData, audience);

        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");
        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(doc);
    }
}
