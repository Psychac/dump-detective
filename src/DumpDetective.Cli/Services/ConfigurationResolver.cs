using DumpDetective.Cli.Commands;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Cli.Services;

internal sealed class ConfigurationResolver
{
    private const string DefaultConfigFileName = "config.json";
    private const string FallbackSampleConfigFileName = "config.sample.json";

    public ResolvedExecutionOptions Resolve(AnalysisCommandRequest request)
    {
        string? configPath = ResolveConfigPath(request.ConfigPath);
        CliConfigurationFileModel? fileModel = configPath is null ? null : LoadConfigurationFile(configPath);

        bool usedConfigFile = fileModel is not null;

        MemoryLeakOptions memoryLeak = usedConfigFile
            ? BuildMemoryLeakFromConfig(fileModel!, request)
            : BuildMemoryLeakFromCli(request);

        ReferenceChainOptions referenceChain = usedConfigFile
            ? BuildReferenceChainFromConfig(fileModel!, request)
            : BuildReferenceChainFromCli(request);

        EventLeakOptions eventLeak = usedConfigFile
            ? BuildEventLeakFromConfig(fileModel!, request)
            : BuildEventLeakFromCli(request);

        DiagnosticsOptions diagnostics = usedConfigFile
            ? BuildDiagnosticsFromConfig(fileModel!, request)
            : BuildDiagnosticsFromCli(request);

        ReportOptions report = usedConfigFile
            ? BuildReportFromConfig(fileModel!, request)
            : BuildReportFromCli(request);

        string? configuredDumpPath = fileModel?.DumpPath;
        string? configuredBaseline = fileModel?.BaselineDumpPath;
        IReadOnlyList<string>? configuredTrend = fileModel?.TrendDumpPaths;

        string? effectiveDumpPath = configuredDumpPath ?? request.DumpPath ?? configuredTrend?.LastOrDefault();
        if (string.IsNullOrWhiteSpace(effectiveDumpPath))
        {
            throw new ArgumentException("Dump path is required. Provide positional dump-path, --trend, or DumpPath in config.");
        }

        string outputPath = !string.IsNullOrWhiteSpace(request.OutputPath)
            ? request.OutputPath!
            : BuildOutputPath(effectiveDumpPath!, report.Format);

        return new ResolvedExecutionOptions(
            effectiveDumpPath!,
            outputPath,
            configuredBaseline ?? request.BaselineDumpPath,
            configuredTrend ?? request.TrendDumpPaths,
            memoryLeak,
            referenceChain,
            eventLeak,
            diagnostics,
            report,
            configPath,
            usedConfigFile,
            request.IncludeAnalyzers,
            request.ExcludeAnalyzers,
            request.DiagnosticMode);
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

        string json = File.ReadAllText(configPath);
        CliConfigurationFileModel? model = JsonSerializer.Deserialize<CliConfigurationFileModel>(json, serializerOptions);
        if (model is null)
        {
            throw new ArgumentException($"Config file '{configPath}' is empty or invalid.");
        }

        return model;
    }

    private static MemoryLeakOptions BuildMemoryLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        int highReferenceThreshold = PositiveOrNull(config.MemoryLeak?.HighReferenceThreshold)
            ?? PositiveOrNull(config.HighReferenceThreshold)
            ?? request.HighReferenceThreshold
            ?? 50;

        int maxDuplicateStringLength = PositiveOrNull(config.MemoryLeak?.MaxDuplicateStringLength)
            ?? PositiveOrNull(config.MaxDuplicateStringLength)
            ?? request.MaxDuplicateStringLength
            ?? 500;

        int minDuplicateStringCount = PositiveOrNull(config.MemoryLeak?.MinDuplicateStringCount)
            ?? PositiveOrNull(config.MinDuplicateStringCount)
            ?? request.MinDuplicateStringCount
            ?? 10;

        int maxReferenceAddresses = PositiveOrNull(config.MemoryLeak?.MaxReferenceAddresses)
            ?? PositiveOrNull(config.MaxReferenceAddressesToTrack)
            ?? request.MaxReferenceAddresses
            ?? 1_000_000;

        return new MemoryLeakOptions
        {
            HighReferenceThreshold = highReferenceThreshold,
            MaxDuplicateStringLength = maxDuplicateStringLength,
            MinDuplicateStringCount = minDuplicateStringCount,
            MaxReferenceAddresses = maxReferenceAddresses
        };
    }

    private static MemoryLeakOptions BuildMemoryLeakFromCli(AnalysisCommandRequest request)
    {
        return new MemoryLeakOptions
        {
            HighReferenceThreshold = request.HighReferenceThreshold ?? 50,
            MaxDuplicateStringLength = request.MaxDuplicateStringLength ?? 500,
            MinDuplicateStringCount = request.MinDuplicateStringCount ?? 10,
            MaxReferenceAddresses = request.MaxReferenceAddresses ?? 1_000_000
        };
    }

    private static ReferenceChainOptions BuildReferenceChainFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        int topCount = PositiveOrNull(config.ReferenceChain?.TopCount)
            ?? PositiveOrNull(config.ReferenceChainTopCount)
            ?? request.ReferenceChainTopCount
            ?? 5;

        int maxPathSearchObjects = PositiveOrNull(config.ReferenceChain?.MaxPathSearchObjects)
            ?? PositiveOrNull(config.ReferenceChainMaxPathSearchObjects)
            ?? request.ReferenceChainMaxPathSearchObjects
            ?? 5_000;

        return new ReferenceChainOptions
        {
            TopCount = topCount,
            MaxPathSearchObjects = maxPathSearchObjects
        };
    }

    private static ReferenceChainOptions BuildReferenceChainFromCli(AnalysisCommandRequest request)
    {
        return new ReferenceChainOptions
        {
            TopCount = request.ReferenceChainTopCount ?? 5,
            MaxPathSearchObjects = request.ReferenceChainMaxPathSearchObjects ?? 5_000
        };
    }

    private static EventLeakOptions BuildEventLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        int minSubscribers = NonNegativeOrNull(config.EventLeak?.MinSubscribers)
            ?? NonNegativeOrNull(config.EventLeakMinSubscribers)
            ?? request.EventLeakMinSubscribers
            ?? 0;

        return new EventLeakOptions
        {
            MinSubscribers = minSubscribers
        };
    }

    private static EventLeakOptions BuildEventLeakFromCli(AnalysisCommandRequest request)
    {
        return new EventLeakOptions
        {
            MinSubscribers = request.EventLeakMinSubscribers ?? 0
        };
    }

    private static DiagnosticsOptions BuildDiagnosticsFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        bool enableMemoryDiagnostics = config.Diagnostics?.EnableMemoryDiagnostics
            ?? config.EnableMemoryDiagnostics
            ?? request.EnableMemoryDiagnostics;

        bool enablePerformanceDiagnostics = config.Diagnostics?.EnablePerformanceDiagnostics
            ?? config.EnablePerformanceDiagnostics
            ?? request.EnablePerformanceDiagnostics;

        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = enableMemoryDiagnostics,
            EnablePerformanceDiagnostics = enablePerformanceDiagnostics
        };
    }

    private static DiagnosticsOptions BuildDiagnosticsFromCli(AnalysisCommandRequest request)
    {
        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = request.EnableMemoryDiagnostics,
            EnablePerformanceDiagnostics = request.EnablePerformanceDiagnostics
        };
    }

    private static ReportOptions BuildReportFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = config.Report?.Format ?? ParseReportFormat(config.ReportFormat) ?? request.OutputFormat ?? ReportFormat.Html
        };
    }

    private static ReportOptions BuildReportFromCli(AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = request.OutputFormat ?? ReportFormat.Html
        };
    }

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

    private static ReportFormat? ParseReportFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "text" or "txt" => ReportFormat.Text,
            "markdown" or "md" => ReportFormat.Markdown,
            "html" or "htm" => ReportFormat.Html,
            _ => throw new ArgumentException($"Invalid ReportFormat value '{format}' in config.")
        };
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static int? NonNegativeOrNull(int? value) => value is >= 0 ? value : null;
}

internal sealed class CliConfigurationFileModel
{
    public string? DumpPath { get; init; }
    public string? BaselineDumpPath { get; init; }
    public List<string>? TrendDumpPaths { get; init; }

    public MemoryLeakOptions? MemoryLeak { get; init; }
    public ReferenceChainOptions? ReferenceChain { get; init; }
    public EventLeakOptions? EventLeak { get; init; }
    public DiagnosticsOptions? Diagnostics { get; init; }
    public ReportOptionsModel? Report { get; init; }

    public int? HighReferenceThreshold { get; init; }
    public int? MaxDuplicateStringLength { get; init; }
    public int? MinDuplicateStringCount { get; init; }
    public int? MaxReferenceAddressesToTrack { get; init; }
    public int? ReferenceChainTopCount { get; init; }
    public int? ReferenceChainMaxPathSearchObjects { get; init; }
    public int? EventLeakMinSubscribers { get; init; }
    public bool? EnableMemoryDiagnostics { get; init; }
    public bool? EnablePerformanceDiagnostics { get; init; }
    public string? ReportFormat { get; init; }
}

internal sealed class ReportOptionsModel
{
    public ReportFormat Format { get; init; } = ReportFormat.Html;
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CliConfigurationFileModel))]
internal partial class CliConfigurationJsonSerializerContext : JsonSerializerContext
{
}
