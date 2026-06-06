using DumpDetective.Cli.Commands;
using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Services.Configuration;

internal static class AnalyzerOptionsBuilder
{
    public static T BuildBalancedPresetFromCli<T>(
        AnalysisCommandRequest _,
        Func<AnalysisProfile, T> presetFactory)
        where T : class
    {
        T preset = presetFactory(AnalysisProfile.Balanced);

        // Special-case: allow CLI to override a couple of StringAnalysis options
        if (typeof(T) == typeof(StringAnalysisOptions) && _ is not null)
        {
            var req = _ as AnalysisCommandRequest;
            var s = preset as StringAnalysisOptions ?? StringAnalysisOptions.Default;

            if (req?.MaxDuplicateStringLength is not null || req?.MinDuplicateStringCount is not null)
            {
                var overridden = new StringAnalysisOptions
                {
                    EnableDeduplication = s.EnableDeduplication,
                    DeduplicationStringCountThreshold = s.DeduplicationStringCountThreshold,
                    MaxUniqueStringTracking = s.MaxUniqueStringTracking,
                    MaxStringsToDedup = s.MaxStringsToDedup,
                    TopDuplicatesToShow = s.TopDuplicatesToShow,
                    VeryLongStringThresholdBytes = s.VeryLongStringThresholdBytes,
                    LohThresholdBytes = s.LohThresholdBytes,
                    PreviewMaxLength = s.PreviewMaxLength,
                    MaxDuplicateStringLength = req.MaxDuplicateStringLength ?? s.MaxDuplicateStringLength,
                    MinDuplicateStringCount = req.MinDuplicateStringCount ?? s.MinDuplicateStringCount,
                    DeduplicationMode = s.DeduplicationMode,
                    SamplingMode = s.SamplingMode,
                    DetectInterning = s.DetectInterning,
                    ProduceRawExports = s.ProduceRawExports,
                    MinDuplicateCharLength = s.MinDuplicateCharLength
                };

                return overridden as T;
            }
        }

        return preset;
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

    public static HeapIndexPrebuildMode BuildIndexPrebuildModeFromCli(AnalysisCommandRequest request)
        => request.IndexPrebuildMode ?? HeapIndexPrebuildMode.Auto;

}
