using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalyzerExecutionRunner(AnalysisDiagnosticsPublisher diagnosticsPublisher)
{
    private readonly AnalysisDiagnosticsPublisher _diagnosticsPublisher = diagnosticsPublisher;

    public async Task<AnalyzerDomainResult> ExecuteAsync(
        Guid runId,
        IAnalyzer analyzer,
        RuntimeAnalysisContext context,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        const int progressTickMs = 300;

        long latestScannedCount = 0;
        string latestPhase = "scanning";
        string? latestDetail = null;
        long lastHeartbeatScannedCount = -1;
        string? lastHeartbeatPhase = null;
        string? lastHeartbeatDetail = null;

        var analyzerProgress = new Progress<AnalyzerProgressReport>(report =>
        {
            Interlocked.Exchange(ref latestScannedCount, report.ScannedCount);
            latestPhase = report.Phase;
            latestDetail = report.Detail;

            _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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

        context.Progress = new ThrottledProgress<AnalyzerProgressReport>(analyzerProgress, 150);

        Task<AnalyzerDomainResult> analyzeTask = Task.Run(
            async () => await analyzer.AnalyzeAsync(context, cancellationToken),
            cancellationToken);

        while (true)
        {
            Task completedTask = await Task.WhenAny(analyzeTask, Task.Delay(progressTickMs, cancellationToken));
            if (completedTask == analyzeTask)
                break;

            long heartbeatScanCount = Interlocked.Read(ref latestScannedCount);
            if (heartbeatScanCount == 0)
                heartbeatScanCount = context.Cache.ObjectScanCount;

            if (heartbeatScanCount == lastHeartbeatScannedCount
                && string.Equals(latestPhase, lastHeartbeatPhase, StringComparison.Ordinal)
                && string.Equals(latestDetail, lastHeartbeatDetail, StringComparison.Ordinal))
            {
                continue;
            }

            lastHeartbeatScannedCount = heartbeatScanCount;
            lastHeartbeatPhase = latestPhase;
            lastHeartbeatDetail = latestDetail;

            _diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
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
}