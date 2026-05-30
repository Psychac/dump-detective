using DumpDetective.Core.Models;
using System.Diagnostics;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalysisPipeline(
    IEnumerable<IAnalyzer> analyzers,
    FindingGenerationPipeline findingGenerationPipeline,
    AnalyzerCleanupPolicy? cleanupPolicy = null,
    AnalyzerExecutionRunner? executionRunner = null,
    AnalysisDiagnosticsPublisher? diagnosticsPublisher = null,
    AnalyzerResultPostProcessor? resultPostProcessor = null)
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = analyzers.ToList();
    private readonly AnalyzerResultPostProcessor _resultPostProcessor = resultPostProcessor ?? new AnalyzerResultPostProcessor(findingGenerationPipeline);
    private readonly AnalyzerCleanupPolicy _cleanupPolicy = cleanupPolicy ?? new AnalyzerCleanupPolicy();
    private readonly AnalysisDiagnosticsPublisher _diagnosticsPublisher = diagnosticsPublisher ?? new AnalysisDiagnosticsPublisher();
    private readonly AnalyzerExecutionRunner _executionRunner = executionRunner ?? new AnalyzerExecutionRunner(diagnosticsPublisher ?? new AnalysisDiagnosticsPublisher());
    // Cached once per pipeline instance to avoid repeated OS round-trips per analyzer.
    private static readonly Process _currentProcess = Process.GetCurrentProcess();

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(RuntimeAnalysisContext context, CancellationToken cancellationToken)
    {
        Guid runId = Guid.NewGuid();
        Stopwatch runStopwatch = Stopwatch.StartNew();
        List<AnalyzerRunResult> runResults = new(_analyzers.Count);

        _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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
                    AnalyzerExecutionStatus.SkippedByCancellation,
                    TimeSpan.Zero,
                    null,
                    "Skipped because cancellation was requested before analyzer start.",
                    nameof(OperationCanceledException),
                    SkipReason: "Skipped by cancellation before analyzer start.");

                runResults.Add(skipped);

                _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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
            _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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

            if (context.Cache is IHeapIndexBuilder cacheWithProgress)
                cacheWithProgress.SetProgress(context.Progress);

            AnalyzerMemoryStats? memoryStats = null;
            long wsBefore = 0, managedBefore = 0;
            bool trackWorkingSet = context.Diagnostics.EnableMemoryDiagnostics || context.Diagnostics.HasAnalyzerCollectionPolicy();
            if (trackWorkingSet)
            {
                _currentProcess.Refresh();
                wsBefore = _currentProcess.WorkingSet64;
                if (context.Diagnostics.EnableMemoryDiagnostics)
                    managedBefore = GC.GetTotalMemory(false);
            }

            try
            {
                AnalyzerDomainResult analyzerResult = await _executionRunner.ExecuteAsync(
                    runId,
                    analyzer,
                    context,
                    stopwatch,
                    cancellationToken);

                long objectScans = context.Cache.ObjectScanCount;
                long cacheHits = context.Cache.CacheHits;
                long cacheMisses = context.Cache.CacheMisses;
                int warningCount = analyzerResult.Warnings.Count;

                stopwatch.Stop();

                long wsAfter = 0;
                if (trackWorkingSet)
                {
                    _currentProcess.Refresh();
                    wsAfter = _currentProcess.WorkingSet64;
                }

                if (context.Diagnostics.EnableMemoryDiagnostics)
                {
                    memoryStats = new AnalyzerMemoryStats(
                        WorkingSetBefore: wsBefore,
                        WorkingSetAfter: wsAfter,
                        ManagedHeapBefore: managedBefore,
                        ManagedHeapAfter: GC.GetTotalMemory(false));
                }

                AnalyzerRunResult success = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Success,
                    stopwatch.Elapsed,
                    analyzerResult,
                    null,
                    null,
                    Findings: null,
                    FindingCount: 0,
                    WarningCount: warningCount,
                    Artifacts: analyzerResult.Artifacts,
                    Diagnostics: new AnalyzerExecutionDiagnostics(
                        ObjectScanCount: objectScans,
                        CacheHits: cacheHits,
                        CacheMisses: cacheMisses,
                        MemoryStats: memoryStats));

                runResults.Add(success);

                _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                    RunId: runId,
                    EventType: AnalysisDiagnosticsEventType.AnalyzerCompleted,
                    TimestampUtc: DateTime.UtcNow,
                    AnalyzerName: analyzer.Name,
                    Category: analyzer.Category,
                    DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                    ObjectScanCount: success.ObjectScanCount,
                    CacheHits: success.CacheHits,
                    CacheMisses: success.CacheMisses,
                    Message: $"Analyzer '{analyzer.Name}' completed.",
                    ExceptionType: null,
                    ExceptionMessage: null));
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();

                AnalyzerRunResult canceled = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.SkippedByCancellation,
                    stopwatch.Elapsed,
                    null,
                    "Analyzer execution canceled.",
                    nameof(OperationCanceledException),
                    SkipReason: "Skipped by cancellation during analyzer execution.",
                    Diagnostics: new AnalyzerExecutionDiagnostics(
                        ObjectScanCount: context.Cache.ObjectScanCount,
                        CacheHits: context.Cache.CacheHits,
                        CacheMisses: context.Cache.CacheMisses));

                runResults.Add(canceled);

                _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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

                // Capture full exception details (includes stack trace) to aid diagnostics
                string fullEx = ex.ToString();

                AnalyzerRunResult failed = new(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Failed,
                    stopwatch.Elapsed,
                    null,
                    fullEx,
                    ex.GetType().Name,
                    SkipReason: null,
                    Diagnostics: new AnalyzerExecutionDiagnostics(
                        ObjectScanCount: context.Cache.ObjectScanCount,
                        CacheHits: context.Cache.CacheHits,
                        CacheMisses: context.Cache.CacheMisses));

                runResults.Add(failed);

                _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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
                    ExceptionMessage: fullEx));

                if (!context.Diagnostics.ContinueOnAnalyzerFailure)
                {
                    break;
                }
            }
            finally
            {
                if (context.Cache is IHeapIndexBuilder cacheWithProgressCleanup)
                    cacheWithProgressCleanup.SetProgress(null);

                context.Progress = null;
            }

            _cleanupPolicy.CleanupAfterAnalyzer(context, analyzer, runResults.Count, wsBefore, trackWorkingSet);
        }

        runStopwatch.Stop();

        runResults = _resultPostProcessor.Enrich(runResults, cancellationToken).ToList();

        _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
            RunId: runId,
            EventType: AnalysisDiagnosticsEventType.RunCompleted,
            TimestampUtc: DateTime.UtcNow,
            AnalyzerName: null,
            Category: "Run",
            DurationMs: runStopwatch.Elapsed.TotalMilliseconds,
            ObjectScanCount: runResults.Sum(r => r.ObjectScanCount),
            CacheHits: runResults.Sum(r => r.CacheHits),
            CacheMisses: runResults.Sum(r => r.CacheMisses),
            Message: $"Run completed. Success={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Success)}, Failed={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Failed)}, SkippedByFilter={runResults.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter)}, SkippedByCancellation={runResults.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation)}",
            ExceptionType: null,
            ExceptionMessage: null));

        return runResults;
    }
}


