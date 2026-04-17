using DumpDetective.Core.Configuration;

namespace DumpDetective.Cli.Commands;

internal sealed record CliArguments(
    string? DumpPath,
    string? ConfigPath,
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
    bool EnablePerformanceDiagnostics,
    ReportFormat? ReportFormat,
    string? OutputPath);
