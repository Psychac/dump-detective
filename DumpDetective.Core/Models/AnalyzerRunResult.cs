namespace DumpDetective.Core.Models;

internal enum AnalyzerExecutionStatus
{
    Success,
    Failed,
    Skipped,
    Canceled
}

internal sealed record AnalyzerRunResult(
    string AnalyzerName,
    AnalyzerExecutionStatus Status,
    TimeSpan Duration,
    AnalyzerDomainResult? Result,
    string? ErrorMessage,
    string? ErrorType,
    int FindingCount = 0,
    int WarningCount = 0,
    long ObjectScanCount = 0,
    long CacheHits = 0,
    long CacheMisses = 0);
