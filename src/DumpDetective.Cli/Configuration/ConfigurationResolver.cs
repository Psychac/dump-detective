using DumpDetective.Cli.Commands;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Models;

using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Configuration;

internal sealed class ConfigurationResolver
{
    private const string DefaultConfigFileName = "config.json";
    private const string FallbackSampleConfigFileName = "config.sample.json";

    public ResolvedExecutionOptions Resolve(AnalysisCommandRequest request)
    {
        try
        {
            string? configPath = ResolveConfigPath(request.ConfigPath);
            CliConfigurationFileModel? fileModel = configPath is null ? null : LoadConfigurationFile(configPath);

            bool usedConfigFile = fileModel is not null;

            DiagnosticsOptions diagnostics = Resolve(usedConfigFile, BuildDiagnosticsFromConfig, AnalyzerOptionsBuilder.BuildDiagnosticsFromCli, fileModel, request);
            ReportOptions report = Resolve(usedConfigFile, BuildReportFromConfig, AnalyzerOptionsBuilder.BuildReportFromCli, fileModel, request);

            string? configuredDumpPath = fileModel?.DumpPath;
            string? configuredBaseline = fileModel?.BaselineDumpPath;
            IReadOnlyList<string>? configuredTrend = fileModel?.TrendDumpPaths;
            IReadOnlyList<string>? effectiveTrend = configuredTrend ?? request.TrendDumpPaths;
            IReadOnlyCollection<string>? configuredInclude = fileModel?.IncludeAnalyzers;
            IReadOnlyCollection<string>? configuredExclude = fileModel?.ExcludeAnalyzers;
            IReadOnlyCollection<string> effectiveInclude = configuredInclude ?? request.IncludeAnalyzers;
            IReadOnlyCollection<string> effectiveExclude = configuredExclude ?? request.ExcludeAnalyzers;

            // Determine effective dump path.
            // If the user explicitly provided a config path, honor the configured DumpPath when present.
            // Otherwise prefer the request-provided DumpPath (positional CLI) over any implicit config file value.
            string? effectiveDumpPath;
            if (!string.IsNullOrWhiteSpace(request.ConfigPath))
            {
                effectiveDumpPath = !string.IsNullOrWhiteSpace(configuredDumpPath)
                    ? configuredDumpPath
                    : !string.IsNullOrWhiteSpace(request.DumpPath) ? request.DumpPath : effectiveTrend?.LastOrDefault();
            }
            else
            {
                effectiveDumpPath = !string.IsNullOrWhiteSpace(request.DumpPath)
                    ? request.DumpPath
                    : !string.IsNullOrWhiteSpace(configuredDumpPath) ? configuredDumpPath : effectiveTrend?.LastOrDefault();
            }
            if (string.IsNullOrWhiteSpace(effectiveDumpPath))
            {
                throw new ArgumentException("Dump path is required. Provide a positional dump-path, --trend, or DumpPath in config.");
            }

            string outputPath = !string.IsNullOrWhiteSpace(request.OutputPath)
                ? request.OutputPath!
                : BuildOutputPath(effectiveDumpPath!, report.Format);

            return new ResolvedExecutionOptions(
                effectiveDumpPath!,
                outputPath,
                configuredBaseline ?? request.BaselineDumpPath,
                effectiveTrend,
                diagnostics,
                report,
                configPath,
                usedConfigFile,
                effectiveInclude,
                effectiveExclude,
                request.DiagnosticMode)
            {
                CacheDirectory = fileModel?.CacheDirectory ?? request.CacheDirectory
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            throw new DumpDetective.Cli.Diagnostics.ConfigurationException(ex.Message, ex);
        }
    }

    private static string? ResolveConfigPath(string? cliConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(cliConfigPath))
        {
            if (!File.Exists(cliConfigPath))
            {
                throw new FileNotFoundException($"Config file not found at '{cliConfigPath}'.", cliConfigPath);
            }

            return cliConfigPath;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string primaryPath = Path.Combine(baseDirectory, DefaultConfigFileName);
        if (File.Exists(primaryPath))
        {
            return primaryPath;
        }

        string samplePath = Path.Combine(baseDirectory, FallbackSampleConfigFileName);
        return File.Exists(samplePath) ? samplePath : null;
    }

    private static CliConfigurationFileModel LoadConfigurationFile(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file not found at '{configPath}'.", configPath);
        }

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            TypeInfoResolver = CliConfigurationJsonSerializerContext.Default
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());

        string json = File.ReadAllText(configPath);
        WarnIfDeadKeysPresent(json);

        CliConfigurationFileModel? model = JsonSerializer.Deserialize<CliConfigurationFileModel>(json, serializerOptions);
        if (model is null)
        {
            throw new ArgumentException($"Config file '{configPath}' is empty or invalid.");
        }

        return model;
    }

    // Per-analyzer configurability (the AnalysisProfile tier system, and later every individual
    // analyzer threshold/cap under "Analyzers") has been removed entirely — see
    // docs/refactor/analysis-options-removal-plan.md. Every analyzer now runs the same fixed,
    // exact analysis regardless of config.json, so a config file written against either system
    // would otherwise be silently ignored by the deserializer with no signal to the user. Warn
    // instead.
    private static readonly string[] s_deadTopLevelKeys = ["Profile", "Analyzers", "ExecutionPolicy"];

    private static void WarnIfDeadKeysPresent(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                foreach (string deadKey in s_deadTopLevelKeys)
                {
                    if (string.Equals(prop.Name, deadKey, StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleUx.Warning($"Config key '{prop.Name}' is deprecated and no longer has any effect — " +
                            "every analyzer now runs fixed, exact analysis with no user-tunable thresholds. Remove it.");
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON is reported by the real Deserialize call below with a clearer error.
        }
    }

    private static DiagnosticsOptions BuildDiagnosticsFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        bool enableMemoryDiagnostics = config.Diagnostics?.EnableMemoryDiagnostics
            ?? config.EnableMemoryDiagnostics
            ?? request.EnableMemoryDiagnostics;

        bool enablePerformanceDiagnostics = config.Diagnostics?.EnablePerformanceDiagnostics
            ?? config.EnablePerformanceDiagnostics
            ?? request.EnablePerformanceDiagnostics;

        bool collectAfterAnalyzerRun = config.Diagnostics?.CollectAfterAnalyzerRun ?? false;
        int collectAfterAnalyzerRunEveryKAnalyzers = config.Diagnostics?.CollectAfterAnalyzerRunEveryKAnalyzers ?? 0;
        long collectAfterAnalyzerRunWorkingSetThresholdBytes = config.Diagnostics?.CollectAfterAnalyzerRunWorkingSetThresholdBytes ?? 0;
        bool compactLargeObjectHeapAfterAnalyzerCollection = config.Diagnostics?.CompactLargeObjectHeapAfterAnalyzerCollection ?? true;

        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = enableMemoryDiagnostics,
            EnablePerformanceDiagnostics = enablePerformanceDiagnostics,
            CollectAfterAnalyzerRun = collectAfterAnalyzerRun,
            CollectAfterAnalyzerRunEveryKAnalyzers = collectAfterAnalyzerRunEveryKAnalyzers,
            CollectAfterAnalyzerRunWorkingSetThresholdBytes = collectAfterAnalyzerRunWorkingSetThresholdBytes,
            CompactLargeObjectHeapAfterAnalyzerCollection = compactLargeObjectHeapAfterAnalyzerCollection
        };
    }

    private static ReportOptions BuildReportFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = config.Report?.Format ?? ConfigurationParseHelpers.ParseReportFormat(config.ReportFormat) ?? request.OutputFormat ?? ReportFormat.Html,
            StyleVersion = config.Report?.StyleVersion ?? ConfigurationParseHelpers.ParseReportStyle(config.ReportStyleVersion) ?? request.ReportStyleVersion ?? ReportStyleVersion.V1,
            PreRender = config.Report?.PreRender ?? request.PreRender,
            SeparateJson = config.Report?.SeparateJson ?? request.SeparateJson
        };
    }

    private static T Resolve<T>(
        bool fromFile,
        Func<CliConfigurationFileModel, AnalysisCommandRequest, T> fromConfig,
        Func<AnalysisCommandRequest, T> fromCli,
        CliConfigurationFileModel? fileModel,
        AnalysisCommandRequest request)
        => fromFile ? fromConfig(fileModel!, request) : fromCli(request);

    private static string BuildOutputPath(string dumpPath, ReportFormat format)
    {
        string extension = format switch
        {
            ReportFormat.Markdown => ".md",
            ReportFormat.Text => ".txt",
            _ => ".html"
        };

        return Path.ChangeExtension(dumpPath, extension);
    }
}
