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
    string? ErrorType);
