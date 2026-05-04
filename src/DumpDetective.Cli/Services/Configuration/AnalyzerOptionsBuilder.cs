using DumpDetective.Cli.Commands;
using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Cli.Services.Configuration;

internal static class AnalyzerOptionsBuilder
{
    public static T BuildBalancedPresetFromCli<T>(
        AnalysisCommandRequest _,
        Func<AnalysisProfile, T> presetFactory)
        where T : class
        => presetFactory(AnalysisProfile.Balanced);

    public static T BuildValidatedBalancedPresetFromCli<T>(
        AnalysisCommandRequest _,
        Func<AnalysisProfile, T> presetFactory,
        Func<T, T> validate)
        where T : class
    {
        T preset = presetFactory(AnalysisProfile.Balanced);
        return validate(preset);
    }

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
            Audience = request.ReportAudience ?? ReportAudience.All
        };
    }

    public static HeapIndexPrebuildMode BuildIndexPrebuildModeFromCli(AnalysisCommandRequest request)
        => request.IndexPrebuildMode ?? HeapIndexPrebuildMode.Auto;

}
