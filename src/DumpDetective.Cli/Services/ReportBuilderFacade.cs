using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Services;

internal sealed class ReportBuilderFacade(IEnumerable<IReportFormatter> formatters)
{
    private readonly IReadOnlyList<IReportFormatter> _formatters = formatters.ToList();

    public string BuildRenderedReport(string dumpPath, ReportFormat format, IReadOnlyList<AnalyzerRunResult> runs, TimeSpan elapsed)
    {
        ComposedReport report = ReportBuilder.ComposeCanonicalReport(dumpPath, runs, elapsed);
        IReportFormatter formatter = _formatters.FirstOrDefault(f => f.Format == format)
            ?? throw new InvalidOperationException($"No formatter registered for '{format}'.");

        return formatter.Render(report);
    }
}
