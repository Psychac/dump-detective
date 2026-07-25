using System.Diagnostics;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Runs a single shared pass over the on-disk heap index and fans each entry out to every
/// registered <see cref="IHeapIndexScanParticipant"/>, so N participating analyzers share one
/// scan instead of each enumerating the index independently.
/// </summary>
internal sealed class HeapIndexScanDispatcher
{
    // Reported as the "analyzer name" on diagnostics events for this pass so the console
    // progress line and verbose log both attribute it, even though no single IAnalyzer owns it.
    private const string ScanName = "Shared heap index scan";

    public void Run(HeapAnalysisCache cache, AnalysisContext context, IReadOnlyList<IHeapIndexScanParticipant> participants, CancellationToken cancellationToken)
    {
        if (participants.Count == 0)
            return;

        // Each participant's failure is isolated so one buggy analyzer's exception during
        // BeforeHeapIndexScan/OnHeapEntry doesn't blind every other participant sharing this pass.
        bool[] failed = new bool[participants.Count];

        for (int i = 0; i < participants.Count; i++)
        {
            try
            {
                participants[i].BeforeHeapIndexScan(context);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                failed[i] = true;
            }
        }

        if (cache.TryGetHeapIndex(out _))
        {
            var diagnosticsPublisher = new AnalysisDiagnosticsPublisher();
            Guid runId = Guid.NewGuid();
            Stopwatch stopwatch = Stopwatch.StartNew();

            void Publish(AnalysisDiagnosticsEventType eventType, long scannedCount, string message) =>
                diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                    RunId: runId,
                    EventType: eventType,
                    TimestampUtc: DateTime.UtcNow,
                    AnalyzerName: ScanName,
                    Category: "SharedScan",
                    DurationMs: stopwatch.Elapsed.TotalMilliseconds,
                    ObjectScanCount: scannedCount,
                    CacheHits: 0,
                    CacheMisses: 0,
                    Message: message,
                    ExceptionType: null,
                    ExceptionMessage: null));

            Publish(AnalysisDiagnosticsEventType.AnalyzerStarted, 0, $"{ScanName} started.");

            var progress = new Progress<AnalyzerProgressReport>(report =>
                Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, report.ScannedCount, report.Phase));

            var scanCounter = new ObjectScanCounter(ScanName, progress, reportEveryObjects: 250_000);

            foreach (HeapEntry entry in cache.EnumerateIndexedEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < participants.Count; i++)
                {
                    if (failed[i])
                        continue;

                    try
                    {
                        participants[i].OnHeapEntry(in entry);
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                    {
                        failed[i] = true;
                    }
                }

                scanCounter.Tick();
            }

            scanCounter.Complete();
            Publish(AnalysisDiagnosticsEventType.AnalyzerCompleted, scanCounter.Scanned, $"{ScanName} completed.");
        }
        else
        {
            for (int i = 0; i < participants.Count; i++)
                failed[i] = true;
        }

        for (int i = 0; i < participants.Count; i++)
            participants[i].OnHeapIndexScanCompleted(succeeded: !failed[i]);
    }
}
