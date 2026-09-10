using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Models;

internal sealed record ResolvedExecutionOptions(
    string DumpPath,
    string OutputPath,
    string? BaselineDumpPath,
    IReadOnlyList<string>? TrendDumpPaths,
    DiagnosticsOptions Diagnostics,
    ReportOptions Report,
    string? ConfigPath,
    bool UsedConfigFile,
    IReadOnlyCollection<string> IncludeAnalyzers,
    IReadOnlyCollection<string> ExcludeAnalyzers,
    bool DiagnosticMode)
{
    public string? CacheDirectory { get; init; }
}
