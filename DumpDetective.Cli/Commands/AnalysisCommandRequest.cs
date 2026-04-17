using DumpDetective.Core.Configuration;

namespace DumpDetective.Cli.Commands;

internal sealed record AnalysisCommandRequest(
    string DumpPath,
    string? OutputPath,
    ReportFormat? OutputFormat,
    string? ConfigPath,
    IReadOnlyCollection<string> IncludeAnalyzers,
    IReadOnlyCollection<string> ExcludeAnalyzers,
    bool DiagnosticMode,
    string? BaselineDumpPath,
    IReadOnlyList<string>? TrendDumpPaths,
    int? HighReferenceThreshold,
    int? MaxDuplicateStringLength,
    int? MinDuplicateStringCount,
    int? MaxReferenceAddresses,
    int? ReferenceChainTopCount,
    int? ReferenceChainMaxPathSearchObjects,
    int? EventLeakMinSubscribers,
    bool EnableMemoryDiagnostics,
    bool EnablePerformanceDiagnostics);
