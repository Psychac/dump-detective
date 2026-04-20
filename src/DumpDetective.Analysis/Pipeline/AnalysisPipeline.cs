using DumpDetective.Core.Models;
using System.Diagnostics;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalysisPipeline(IEnumerable<IAnalyzer> analyzers)
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = analyzers
        .OrderBy(a => a.Order)
        .ThenBy(a => a.Name, StringComparer.Ordinal)
        .ToList();

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        Guid runId = Guid.NewGuid();
        Stopwatch runStopwatch = Stopwatch.StartNew();
        List<AnalyzerRunResult> runResults = new(_analyzers.Count);

        PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
            RunId: runId,
            EventType: AnalysisDiagnosticsEventType.RunStarted,
            TimestampUtc: DateTime.UtcNow,
            AnalyzerName: null,
            Category: "Run",
            DurationMs: null,
            ObjectScanCount: context.Cache.ObjectScanCount,
            CacheHits: context.Cache.CacheHits,
            CacheMisses: context.Cache.CacheMisses,
            Message: "Analysis run started.",
            ExceptionType: null,
            ExceptionMessage: null));

        foreach (IAnalyzer analyzer in _analyzers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AnalyzerRunResult skipped = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Skipped,
                    TimeSpan.Zero,
                    null,
                    "Skipped because cancellation was requested before analyzer start.",
                    nameof(OperationCanceledException));

                runResults.Add(skipped);

                PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                    RunId: runId,
                    EventType: AnalysisDiagnosticsEventType.AnalyzerCanceled,
                    TimestampUtc: DateTime.UtcNow,
                    AnalyzerName: analyzer.Name,
                    Category: analyzer.Category,
                    DurationMs: 0,
                    ObjectScanCount: context.Cache.ObjectScanCount,
                    CacheHits: context.Cache.CacheHits,
                    CacheMisses: context.Cache.CacheMisses,
                    Message: skipped.ErrorMessage ?? "Skipped due to cancellation.",
                    ExceptionType: skipped.ErrorType,
                    ExceptionMessage: skipped.ErrorMessage));
                continue;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                RunId: runId,
                EventType: AnalysisDiagnosticsEventType.AnalyzerStarted,
                TimestampUtc: DateTime.UtcNow,
                AnalyzerName: analyzer.Name,
                Category: analyzer.Category,
                DurationMs: null,
                ObjectScanCount: context.Cache.ObjectScanCount,
                CacheHits: context.Cache.CacheHits,
                CacheMisses: context.Cache.CacheMisses,
                Message: $"Analyzer '{analyzer.Name}' started.",
                ExceptionType: null,
                ExceptionMessage: null));

            try
            {
                AnalyzerDomainResult analyzerResult = await analyzer.AnalyzeAsync(context, cancellationToken);

                long objectScans = ExtractLongMetric(analyzerResult.Metrics, "objectScans") ?? context.Cache.ObjectScanCount;
                long cacheHits = ExtractLongMetric(analyzerResult.Metrics, "cacheHits") ?? context.Cache.CacheHits;
                long cacheMisses = ExtractLongMetric(analyzerResult.Metrics, "cacheMisses") ?? context.Cache.CacheMisses;
                int warningCount = analyzerResult.Warnings.Count;
                int findingCount = analyzerResult.Findings.Count;

                stopwatch.Stop();

                AnalyzerRunResult success = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Success,
                    stopwatch.Elapsed,
                    analyzerResult,
                    null,
                    null,
                    findingCount,
                    warningCount,
                    objectScans,
                    cacheHits,
                    cacheMisses);

                runResults.Add(success);

                PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                    RunId: runId,
                    EventType: AnalysisDiagnosticsEventType.AnalyzerCompleted,
                    TimestampUtc: DateTime.UtcNow,
                    AnalyzerName: analyzer.Name,
                    Category: analyzer.Category,
                    DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                    ObjectScanCount: success.ObjectScanCount,
                    CacheHits: success.CacheHits,
                    CacheMisses: success.CacheMisses,
                    Message: $"Analyzer '{analyzer.Name}' completed with {success.FindingCount} finding(s).",
                    ExceptionType: null,
                    ExceptionMessage: null));
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();

                AnalyzerRunResult canceled = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Canceled,
                    stopwatch.Elapsed,
                    null,
                    "Analyzer execution canceled.",
                    nameof(OperationCanceledException),
                    ObjectScanCount: context.Cache.ObjectScanCount,
                    CacheHits: context.Cache.CacheHits,
                    CacheMisses: context.Cache.CacheMisses);

                runResults.Add(canceled);

                PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                    RunId: runId,
                    EventType: AnalysisDiagnosticsEventType.AnalyzerCanceled,
                    TimestampUtc: DateTime.UtcNow,
                    AnalyzerName: analyzer.Name,
                    Category: analyzer.Category,
                    DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                    ObjectScanCount: canceled.ObjectScanCount,
                    CacheHits: canceled.CacheHits,
                    CacheMisses: canceled.CacheMisses,
                    Message: canceled.ErrorMessage ?? "Analyzer canceled.",
                    ExceptionType: canceled.ErrorType,
                    ExceptionMessage: canceled.ErrorMessage));
                break;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                AnalyzerRunResult failed = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Failed,
                    stopwatch.Elapsed,
                    null,
                    ex.Message,
                    ex.GetType().Name,
                    ObjectScanCount: context.Cache.ObjectScanCount,
                    CacheHits: context.Cache.CacheHits,
                    CacheMisses: context.Cache.CacheMisses);

                runResults.Add(failed);

                PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                    RunId: runId,
                    EventType: AnalysisDiagnosticsEventType.AnalyzerFailed,
                    TimestampUtc: DateTime.UtcNow,
                    AnalyzerName: analyzer.Name,
                    Category: analyzer.Category,
                    DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                    ObjectScanCount: failed.ObjectScanCount,
                    CacheHits: failed.CacheHits,
                    CacheMisses: failed.CacheMisses,
                    Message: $"Analyzer '{analyzer.Name}' failed.",
                    ExceptionType: ex.GetType().Name,
                    ExceptionMessage: ex.Message));

                if (!context.DiagnosticsOptions.ContinueOnAnalyzerFailure)
                {
                    break;
                }
            }
        }

        runStopwatch.Stop();

        PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
            RunId: runId,
            EventType: AnalysisDiagnosticsEventType.RunCompleted,
            TimestampUtc: DateTime.UtcNow,
            AnalyzerName: null,
            Category: "Run",
            DurationMs: runStopwatch.Elapsed.TotalMilliseconds,
            ObjectScanCount: runResults.Sum(r => r.ObjectScanCount),
            CacheHits: runResults.Sum(r => r.CacheHits),
            CacheMisses: runResults.Sum(r => r.CacheMisses),
            Message: $"Run completed. Success={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Success)}, Failed={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Failed)}, Skipped={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Skipped)}, Canceled={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Canceled)}",
            ExceptionType: null,
            ExceptionMessage: null));

        return runResults;
    }

    private static void PublishSafe(IAnalysisDiagnosticsSink diagnosticsSink, AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        try
        {
            diagnosticsSink.Publish(diagnosticsEvent);
        }
        catch
        {
        }
    }

    private static long? ExtractLongMetric(IReadOnlyDictionary<string, object?> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            _ when long.TryParse(value.ToString(), out long parsed) => parsed,
            _ => null
        };
    }
}


