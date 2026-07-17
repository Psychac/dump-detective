using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Execution;

internal sealed record TrendDumpExecution(
    string DumpPath,
    IReadOnlyList<AnalyzerRunResult> Runs,
    TimeSpan Elapsed,
    AnalysisIncidentContext IncidentContext,
    DateTime GeneratedAtUtc,
    IReadOnlyList<(string StageName, AnalyzerMemoryStats Stats)> StageMemoryStats,
    AnalyzerMemoryStats? MemoryStats);
