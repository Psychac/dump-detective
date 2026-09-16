using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Models;

internal sealed record ResolvedExecutionOptions(
    string DumpPath,
    string OutputPath,
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

    /// <summary>
    /// Trace capture to analyze alongside <see cref="DumpPath"/> — see
    /// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5d. Mutually
    /// exclusive with <see cref="TrendDumpPaths"/>, enforced in
    /// <c>DumpAnalysisService.ExecuteAsync</c> (a session accepts either N dumps or exactly one
    /// dump plus one trace, not both — see <c>SessionValidator</c>).
    /// </summary>
    public string? TracePath { get; init; }
}
