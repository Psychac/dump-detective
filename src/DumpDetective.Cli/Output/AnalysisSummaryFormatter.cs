using DumpDetective.Core.Abstractions;
using DumpDetective.Cli.Models;

namespace DumpDetective.Cli.Output;

internal static class AnalysisSummaryFormatter
{
    public static string FormatConfigSummary(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> activeAnalyzers)
    {
        string configSource = resolved.UsedConfigFile
            ? $"file ({resolved.ConfigPath})"
            : "CLI fallback";

        string analyzerNames = string.Join(", ", activeAnalyzers.Select(a => a.Name));
        return $"Config: {configSource}  ·  {activeAnalyzers.Count} analyzers: {analyzerNames}";
    }
}
