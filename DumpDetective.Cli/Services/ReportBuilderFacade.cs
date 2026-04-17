using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Services;

internal sealed class ReportBuilderFacade
{
    public string BuildRenderedReport(string dumpPath, ReportFormat format, IReadOnlyList<AnalyzerRunResult> runs, TimeSpan elapsed)
    {
        IReadOnlyList<InsightFinding> findings = runs
            .Where(r => r.Result is not null)
            .SelectMany(r => r.Result!.Findings)
            .ToList();

        string detailedReport = ReportBuilder.BuildCombinedDetailedReport(runs);
        List<string> insights = ReportBuilder.BuildReportInsights(elapsed, findings);

        return ReportFormatter.Format(format, detailedReport, insights, dumpPath, findings);
    }
}
