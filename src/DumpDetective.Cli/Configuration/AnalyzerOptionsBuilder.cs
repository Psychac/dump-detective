using DumpDetective.Cli.Commands;
using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Configuration;

internal static class AnalyzerOptionsBuilder
{
    public static T BuildBalancedPresetFromCli<T>(
        AnalysisCommandRequest _,
        Func<AnalysisProfile, T> presetFactory)
        where T : class
    {
        T preset = presetFactory(AnalysisProfile.Balanced);
        return preset;
    }

    // Special-case: allow CLI to override a couple of StringAnalysis options that don't go
    // through the (now deleted) profile/preset system.
    public static StringAnalysisOptions BuildStringAnalysisFromCli(AnalysisCommandRequest request)
    {
        var s = new StringAnalysisOptions();
        if (request.MaxDuplicateStringLength is null && request.MinDuplicateStringCount is null)
            return s;

        return new StringAnalysisOptions
        {
            MaxUniqueStringTracking = s.MaxUniqueStringTracking,
            VeryLongStringThresholdBytes = s.VeryLongStringThresholdBytes,
            LohThresholdBytes = s.LohThresholdBytes,
            MaxDuplicateStringLength = request.MaxDuplicateStringLength ?? s.MaxDuplicateStringLength,
            MinDuplicateStringCount = request.MinDuplicateStringCount ?? s.MinDuplicateStringCount,
            ProduceRawExports = s.ProduceRawExports,
            MinDuplicateCharLength = s.MinDuplicateCharLength
        };
    }

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
            StyleVersion = request.ReportStyleVersion ?? ReportStyleVersion.V1,
            PreRender = request.PreRender,
            SeparateJson = request.SeparateJson
        };
    }


}
