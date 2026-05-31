using DumpDetective.Cli.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Cli.Output;

namespace DumpDetective.Cli.Services;

internal static class AnalysisSummaryFormatter
{
    public static string FormatConfigSummary(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> activeAnalyzers)
        => DumpDetective.Cli.Output.AnalysisSummaryFormatter.FormatConfigSummary(resolved, activeAnalyzers);
}
