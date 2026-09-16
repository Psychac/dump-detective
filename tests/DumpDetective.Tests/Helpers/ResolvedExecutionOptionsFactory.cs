using DumpDetective.Cli.Models;
using DumpDetective.Core.Options;

internal static class ResolvedExecutionOptionsFactory
{
    public static ResolvedExecutionOptions Create(string outputPath)
    {
        string dumpPath = Path.ChangeExtension(outputPath, ".dmp");
        return new ResolvedExecutionOptions(
            DumpPath: dumpPath,
            OutputPath: outputPath,
            TrendDumpPaths: null,
            Diagnostics: new DiagnosticsOptions(),
            Report: new ReportOptions(),
            ConfigPath: null,
            UsedConfigFile: false,
            IncludeAnalyzers: System.Array.Empty<string>(),
            ExcludeAnalyzers: System.Array.Empty<string>(),
            DiagnosticMode: false);
    }
}
