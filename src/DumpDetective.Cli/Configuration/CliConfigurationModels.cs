using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Configuration;

internal sealed class CliConfigurationFileModel
{
    public string? DumpPath { get; init; }
    public string? BaselineDumpPath { get; init; }
    public List<string>? TrendDumpPaths { get; init; }

    public DiagnosticsOptions? Diagnostics { get; init; }
    public ReportOptionsModel? Report { get; init; }

    public bool? EnableMemoryDiagnostics { get; init; }
    public bool? EnablePerformanceDiagnostics { get; init; }
    public string? ReportFormat { get; init; }
    public string? ReportStyleVersion { get; init; }
    public IndexingOptionsModel? Indexing { get; init; }
    public string? IndexMode { get; init; }
    public List<string>? IncludeAnalyzers { get; init; }
    public List<string>? ExcludeAnalyzers { get; init; }
    public string? CacheDirectory { get; init; }
}

internal sealed class ReportOptionsModel
{
    public ReportFormat? Format { get; init; }
    public ReportStyleVersion? StyleVersion { get; init; }
    public bool? PreRender { get; init; }
    public bool? SeparateJson { get; init; }
}

internal sealed class IndexingOptionsModel
{
    public string? Mode { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CliConfigurationFileModel))]
[JsonSerializable(typeof(DiagnosticsOptions))]
internal partial class CliConfigurationJsonSerializerContext : JsonSerializerContext
{
}
