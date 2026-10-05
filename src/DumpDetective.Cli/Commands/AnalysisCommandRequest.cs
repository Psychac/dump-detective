using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Commands;

internal sealed record AnalysisCommandRequest(
    string? DumpPath,
    string? OutputPath,
    ReportFormat? OutputFormat,
    string? ConfigPath,
    IReadOnlyCollection<string> IncludeAnalyzers,
    IReadOnlyCollection<string> ExcludeAnalyzers,
    bool DiagnosticMode,
    IReadOnlyList<string>? TrendDumpPaths,
    bool EnableMemoryDiagnostics,
    bool EnablePerformanceDiagnostics,
    ReportStyleVersion? ReportStyleVersion = null,
    bool PreRender = false,
    bool ObservationsUncompressed = false,
    string? CacheDirectory = null,
    string? TracePath = null);
