using DumpDetective.Core.Configuration;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Services;

internal sealed class ReportBuilderFacade(
    IEnumerable<IReportFormatter> formatters,
    IAnalyzerReporterFactory reporterFactory,
    TrendReportComposer trendReportComposer)
{
    private readonly IReadOnlyList<IReportFormatter> _formatters = formatters.ToList();
    private readonly IReadOnlyList<IAnalyzerReporter> _reporters = reporterFactory.CreateReporters();
    private readonly TrendReportComposer _trendReportComposer = trendReportComposer;

    public string BuildRenderedReport(string dumpPath, ReportFormat format, ReportAudience audience, IReadOnlyList<AnalyzerRunResult> runs, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ComposedReport report = ReportBuilder.ComposeCanonicalReport(dumpPath, runs, elapsed, _reporters);
        report = ApplyAudience(report, audience);
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");

        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(report);
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

        ComposedReport report = _trendReportComposer.ComposeCanonicalTrendReport(
            dumpPath,
            currentRuns,
            elapsed,
            _reporters,
            trendData);
        report = ApplyAudience(report, audience);

        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");

        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(report);
    }

    private static ComposedReport ApplyAudience(ComposedReport report, ReportAudience audience)
    {
        return audience switch
        {
            ReportAudience.Executive => report with
            {
                DeveloperActionPlan = [],
                Sections = [],
                DetailedAnalyzerSections = []
            },
            ReportAudience.Developer => report with
            {
                DetailedAnalyzerSections = []
            },
            _ => report
        };
    }
}
