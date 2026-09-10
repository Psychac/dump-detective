using DumpDetective.Cli.Commands;
using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Configuration;

internal static class AnalyzerOptionsBuilder
{
    public static DiagnosticsOptions BuildDiagnosticsFromCli(AnalysisCommandRequest request)
    {
        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = request.EnableMemoryDiagnostics,
            EnablePerformanceDiagnostics = request.EnablePerformanceDiagnostics,
            CollectAfterAnalyzerRun = false
        };
    }

    public static ReportOptions BuildReportFromCli(AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = request.OutputFormat ?? ReportFormat.Html,
            StyleVersion = request.ReportStyleVersion ?? ReportStyleVersion.V1,
            PreRender = request.PreRender,
            SeparateJson = request.SeparateJson
        };
    }


}
