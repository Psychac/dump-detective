using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Pipeline;

// Runs a single shared pass over runtime.Threads and fans each thread's stack frames out to
// every registered IThreadStackScanParticipant, so N participating analyzers share one
// EnumerateStackTrace() walk per thread instead of each enumerating independently.
// Mirrors HeapIndexScanDispatcher's per-participant failure isolation and Completed(bool) gate,
// including diagnostics events — without these, symbol-heavy stack walks on many threads run
// with zero console output, appearing as a frozen gap between the heap-index scan and "Run analyzers".
internal sealed class ThreadStackScanDispatcher
{
    private const string ScanName = "Shared thread stack scan";

    public void Run(ClrRuntime runtime, AnalysisContext context, IReadOnlyList<IThreadStackScanParticipant> participants, int maxFramesPerThread, CancellationToken cancellationToken)
    {
        if (participants.Count == 0)
            return;

        bool[] failed = new bool[participants.Count];

        // Stopwatch/diagnostics start *before* BeforeThreadStackScan, not after — see the matching
        // comment in HeapIndexScanDispatcher.Run. Any expensive per-participant setup here would
        // otherwise be a silent, un-reported gap.
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

        // See matching comment in HeapIndexScanDispatcher.Run — participant setup can be slow
        // with no natural progress hook, so poll it on a background task to keep the console
        // heartbeat ticking instead of appearing frozen.
        Task setupTask = Task.Run(() =>
        {
            for (int i = 0; i < participants.Count; i++)
            {
                try
                {
                    participants[i].BeforeThreadStackScan(context);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                {
                    failed[i] = true;
                }
            }
        }, cancellationToken);

        while (!setupTask.Wait(300))
            Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, 0, $"{ScanName}: preparing (participant setup)...");

        setupTask.GetAwaiter().GetResult();

        // SynchronousProgress, not Progress<T> — see HeapIndexScanDispatcher for why: Progress<T>'s
        // ThreadPool-marshaled callback can stall behind other queued work instead of rendering live.
        var progress = new SynchronousProgress<AnalyzerProgressReport>(report =>
            Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, report.ScannedCount, report.Phase));

        // Threads are far fewer than heap objects, so report on every thread instead of a large
        // count threshold — the time-based cadence (300ms) is what actually matters here.
        var scanCounter = new ObjectScanCounter(ScanName, progress, reportEveryObjects: 1, reportEveryElapsed: TimeSpan.FromMilliseconds(300));

        // Reused across threads (Clear()-ed, not reallocated) — participants that need to retain
        // frames beyond the OnThreadStack call must copy from ThreadStackSnapshot.TopFrames.
        var frameBuffer = new List<ClrStackFrame>(maxFramesPerThread);

        foreach (ClrThread thread in runtime.Threads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            frameBuffer.Clear();

            // Only alive threads have a walkable stack; dead threads still get an OnThreadStack
            // callback (with empty TopFrames) so participants that tally all threads — not just
            // alive ones — don't need a second independent enumeration of runtime.Threads.
            if (thread.IsAlive)
            {
                foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
                {
                    if (frameBuffer.Count >= maxFramesPerThread)
                        break;
                    frameBuffer.Add(frame);
                }
            }

            var snapshot = new ThreadStackSnapshot { Thread = thread, TopFrames = frameBuffer };

            for (int i = 0; i < participants.Count; i++)
            {
                if (failed[i])
                    continue;

                try
                {
                    participants[i].OnThreadStack(in snapshot);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                {
                    failed[i] = true;
                }
            }

            scanCounter.Tick($"thread {thread.OSThreadId:X}");
        }

        scanCounter.Complete();
        Publish(AnalysisDiagnosticsEventType.AnalyzerCompleted, scanCounter.Scanned, $"{ScanName} completed.");

        for (int i = 0; i < participants.Count; i++)
            participants[i].OnThreadStackScanCompleted(succeeded: !failed[i]);
    }
}
