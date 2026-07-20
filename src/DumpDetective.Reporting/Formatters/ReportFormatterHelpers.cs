using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Formatters;

internal static class ReportFormatterHelpers
{
    public static string GetCanonicalDumpPath(AnalysisReportDocument doc)
    {
        if (doc is SingleDumpReportDocument single)
            return single.DumpPath ?? string.Empty;

        if (doc is TrendReportDocument trend)
        {
            if (trend.TrendDumpPaths is { Count: > 0 })
                return trend.TrendDumpPaths[^1] ?? string.Empty;

            if (trend.PerDumpDocuments is { Count: > 0 })
            {
                var last = trend.PerDumpDocuments[^1];
                if (last is SingleDumpReportDocument sd)
                    return sd.DumpPath ?? string.Empty;
            }

            if (trend.IncidentContext is { } ctx && !string.IsNullOrEmpty(ctx.DumpPath))
                return ctx.DumpPath;
        }

        return string.Empty;
    }
}
