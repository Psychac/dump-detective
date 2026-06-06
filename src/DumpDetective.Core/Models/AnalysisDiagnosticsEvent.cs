namespace DumpDetective.Core.Models;

public sealed record AnalysisDiagnosticsEvent(
    Guid RunId,
    AnalysisDiagnosticsEventType EventType,
    DateTime TimestampUtc,
    string? AnalyzerName,
    string Category,
    double? DurationMs,
    long ObjectScanCount,
    long CacheHits,
    long CacheMisses,
    string Message,
    string? ExceptionType,
    string? ExceptionMessage);

public enum AnalysisDiagnosticsEventType
{
    RunStarted,
    AnalyzerStarted,
    AnalyzerProgress,
    AnalyzerSubmoduleProgress,
    AnalyzerCompleted,
    AnalyzerFailed,
    AnalyzerCanceled,
    RunCompleted
}