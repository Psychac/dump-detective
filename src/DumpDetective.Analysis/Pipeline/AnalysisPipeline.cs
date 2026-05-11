using DumpDetective.Core.Models;
using System.Diagnostics;
using DumpDetective.Core.Abstractions;
using System.Runtime;
using DumpDetective.Analysis.Cache;
using System.Diagnostics.CodeAnalysis;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalysisPipeline(IEnumerable<IAnalyzer> analyzers)
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = analyzers.ToList();
    // Cached once per pipeline instance to avoid repeated OS round-trips per analyzer.
    private static readonly Process _currentProcess = Process.GetCurrentProcess();

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(RuntimeAnalysisContext context, CancellationToken cancellationToken)
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
                    AnalyzerExecutionStatus.SkippedByCancellation,
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

            if (context.Cache is IHeapIndexBuilder cacheWithProgress)
                cacheWithProgress.SetProgress(context.Progress);

            AnalyzerMemoryStats? memoryStats = null;
            long wsBefore = 0, managedBefore = 0;
            if (context.Diagnostics.EnableMemoryDiagnostics)
            {
                _currentProcess.Refresh();
                wsBefore = _currentProcess.WorkingSet64;
                managedBefore = GC.GetTotalMemory(false);
            }

            try
            {
                AnalyzerDomainResult analyzerResult = await ExecuteAnalyzerWithProgressAsync(
                    runId,
                    analyzer,
                    context,
                    stopwatch,
                    cancellationToken);

                long objectScans = ExtractLongMetric(analyzerResult.Metrics, "objectScans") ?? context.Cache.ObjectScanCount;
                long cacheHits = ExtractLongMetric(analyzerResult.Metrics, "cacheHits") ?? context.Cache.CacheHits;
                long cacheMisses = ExtractLongMetric(analyzerResult.Metrics, "cacheMisses") ?? context.Cache.CacheMisses;
                int warningCount = analyzerResult.Warnings.Count;

                stopwatch.Stop();

                if (context.Diagnostics.EnableMemoryDiagnostics)
                {
                    _currentProcess.Refresh();
                    memoryStats = new AnalyzerMemoryStats(
                        WorkingSetBefore: wsBefore,
                        WorkingSetAfter: _currentProcess.WorkingSet64,
                        ManagedHeapBefore: managedBefore,
                        ManagedHeapAfter: GC.GetTotalMemory(false));
                }

                // If the analyzer domain result exposes a `RawExports` property (some analyzers
                // attach on-disk artifacts there), propagate them into the AnalyzerRunResult
                // so the reporting serializer can collect and write them out.
                IReadOnlyList<ReportArtifact>? propagatedArtifacts = null;
                try
                {
                    var prop = analyzerResult.GetType().GetProperty("RawExports");
                    if (prop is not null)
                    {
                        propagatedArtifacts = prop.GetValue(analyzerResult) as IReadOnlyList<ReportArtifact>;
                    }
                }
                catch
                {
                    propagatedArtifacts = null;
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
                    ObjectScanCount: objectScans,
                    CacheHits: cacheHits,
                    CacheMisses: cacheMisses,
                    Artifacts: propagatedArtifacts,
                    MemoryStats: memoryStats);

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

            // After each analyzer, attempt to dispose it (if it holds resources) and optionally trigger GC.
            try
            {
                if (analyzer is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { }
                }

                if (context.Diagnostics is not null && context.Diagnostics.CollectAfterAnalyzerRun)
                {
                    try
                    {
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    catch
                    {
                        // best-effort only
                    }
                }
            }
            catch
            {
                // swallow errors from cleanup attempts
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
            Message: $"Run completed. Success={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Success)}, Failed={runResults.Count(r => r.Status == AnalyzerExecutionStatus.Failed)}, SkippedByFilter={runResults.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByFilter)}, SkippedByCancellation={runResults.Count(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation)}",
            ExceptionType: null,
            ExceptionMessage: null));

        return runResults;
    }

    private static async Task<AnalyzerDomainResult> ExecuteAnalyzerWithProgressAsync(
        Guid runId,
        IAnalyzer analyzer,
        RuntimeAnalysisContext context,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        const int progressTickMs = 300;

        // Track the most recent progress reported by the analyzer so the 300ms heartbeat
        // poll can fall back to it instead of always showing stale cache counts.
        long latestScannedCount = 0;
        string latestPhase = "scanning";
        string? latestDetail = null;

        var analyzerProgress = new Progress<AnalyzerProgressReport>(report =>
        {
            Interlocked.Exchange(ref latestScannedCount, report.ScannedCount);
            latestPhase = report.Phase;
            latestDetail = report.Detail;

            PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                RunId: runId,
                EventType: AnalysisDiagnosticsEventType.AnalyzerProgress,
                TimestampUtc: DateTime.UtcNow,
                AnalyzerName: analyzer.Name,
                Category: analyzer.Category,
                DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                ObjectScanCount: report.ScannedCount,
                CacheHits: context.Cache.CacheHits,
                CacheMisses: context.Cache.CacheMisses,
                Message: string.IsNullOrEmpty(report.Detail) ? report.Phase : $"{report.Phase} • {report.Detail}",
                ExceptionType: null,
                ExceptionMessage: null));
        });

        context.Progress = analyzerProgress;

        Task<AnalyzerDomainResult> analyzeTask = Task.Run(
            async () => await analyzer.AnalyzeAsync(context, cancellationToken),
            cancellationToken);

        while (true)
        {
            Task completedTask = await Task.WhenAny(analyzeTask, Task.Delay(progressTickMs, cancellationToken));
            if (completedTask == analyzeTask)
                break;

            // Heartbeat poll: only fires if the analyzer hasn't reported directly via Progress.
            // Uses the latest known scan count and phase so display stays accurate.
            long heartbeatScanCount = Interlocked.Read(ref latestScannedCount);
            if (heartbeatScanCount == 0)
            {
                // Analyzer hasn't called Progress yet — fall back to cache counter (legacy path)
                heartbeatScanCount = context.Cache.ObjectScanCount;
            }

            PublishSafe(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                RunId: runId,
                EventType: AnalysisDiagnosticsEventType.AnalyzerProgress,
                TimestampUtc: DateTime.UtcNow,
                AnalyzerName: analyzer.Name,
                Category: analyzer.Category,
                DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                ObjectScanCount: heartbeatScanCount,
                CacheHits: context.Cache.CacheHits,
                CacheMisses: context.Cache.CacheMisses,
                Message: string.IsNullOrEmpty(latestDetail) ? latestPhase : $"{latestPhase} • {latestDetail}",
                ExceptionType: null,
                ExceptionMessage: null));
        }

        return await analyzeTask;
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


