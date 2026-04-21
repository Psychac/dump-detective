using DumpDetective.Core.Configuration;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Services;

internal sealed class ReportBuilderFacade(IEnumerable<IReportFormatter> formatters, IAnalyzerReporterFactory reporterFactory)
{
    private readonly IReadOnlyList<IReportFormatter> _formatters = formatters.ToList();
    private readonly IReadOnlyList<IAnalyzerReporter> _reporters = reporterFactory.CreateReporters();

    public string BuildRenderedReport(string dumpPath, ReportFormat format, IReadOnlyList<AnalyzerRunResult> runs, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ComposedReport report = ReportBuilder.ComposeCanonicalReport(dumpPath, runs, elapsed, _reporters);
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");

        cancellationToken.ThrowIfCancellationRequested();
        return formatter.Render(report);
    }
}
