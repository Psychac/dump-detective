namespace DumpDetective.Core.Models;

internal enum AnalysisDiagnosticsEventType
{
    RunStarted,
    AnalyzerStarted,
    AnalyzerCompleted,
    AnalyzerFailed,
    AnalyzerCanceled,
    RunCompleted
}

internal sealed record AnalysisDiagnosticsEvent(
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
