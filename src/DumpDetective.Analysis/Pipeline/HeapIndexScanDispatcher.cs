using System.Diagnostics;
using System.Threading.Tasks;

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

    // Below this many records per worker, thread/merge overhead outweighs the benefit of
    // partitioning, so parallel-capable participants fall back to the same single-threaded
    // pass as everyone else — this makes the whole feature a no-op on small dumps.
    private const long MinRecordsPerWorker = 250_000;

    public void Run(HeapAnalysisCache cache, AnalysisContext context, IReadOnlyList<IHeapIndexScanParticipant> participants, CancellationToken cancellationToken)
        => Run(cache, context, participants, cancellationToken, maxWorkers: 0);

    /// <param name="maxWorkers">
    /// Maximum worker count for the parallel section. 0 (default) = auto-select based on
    /// object count and processor count. Pass 1 to force the sequential path for all
    /// participants (useful for perf-comparison tests).
    /// </param>
    public void Run(HeapAnalysisCache cache, AnalysisContext context, IReadOnlyList<IHeapIndexScanParticipant> participants, CancellationToken cancellationToken, int maxWorkers)
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

        if (cache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
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

            List<int> parallelIndices = new List<int>();
            for (int i = 0; i < participants.Count; i++)
            {
                if (!failed[i] && participants[i] is IParallelHeapIndexScanParticipant)
                    parallelIndices.Add(i);
            }

            int workerCount = maxWorkers == 1 ? 1 : ComputeWorkerCount(heapIndex.ObjectCount);
            if (maxWorkers > 1) workerCount = Math.Min(workerCount, maxWorkers);

            if (workerCount <= 1 || parallelIndices.Count == 0)
            {
                RunSequentialPass(cache, participants, failed, mask: null, scanCounter, cancellationToken);
            }
            else
            {
                bool[] sequentialMask = new bool[participants.Count];
                bool anySequential = false;
                for (int i = 0; i < participants.Count; i++)
                {
                    sequentialMask[i] = !failed[i] && participants[i] is not IParallelHeapIndexScanParticipant;
                    anySequential |= sequentialMask[i];
                }

                if (anySequential)
                    RunSequentialPass(cache, participants, failed, sequentialMask, scanCounter, cancellationToken);

                RunParallelPass(cache, context, participants, parallelIndices, workerCount, heapIndex.ObjectCount, failed, scanCounter, cancellationToken);
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

    private static int ComputeWorkerCount(long objectCount)
    {
        if (objectCount <= 0)
            return 1;

        long byRecordCount = objectCount / MinRecordsPerWorker;
        long workerCount = Math.Min(Environment.ProcessorCount, Math.Max(1, byRecordCount));
        return (int)Math.Max(1, workerCount);
    }

    // Full-range pass over the shared disk-backed index, dispatching each entry to every
    // participant not excluded by `mask` (or every non-failed participant, if `mask` is null).
    private static void RunSequentialPass(
        HeapAnalysisCache cache,
        IReadOnlyList<IHeapIndexScanParticipant> participants,
        bool[] failed,
        bool[]? mask,
        ObjectScanCounter scanCounter,
        CancellationToken cancellationToken)
    {
        foreach (HeapEntry entry in cache.EnumerateIndexedEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int i = 0; i < participants.Count; i++)
            {
                if (failed[i] || (mask is not null && !mask[i]))
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
    }

    // Runs one shared K-worker pass over disjoint contiguous record ranges of the on-disk
    // index for every participant in `parallelIndices`, then merges each participant's K
    // per-worker partial states back into its original instance via MergePartial.
    private static void RunParallelPass(
        HeapAnalysisCache cache,
        AnalysisContext context,
        IReadOnlyList<IHeapIndexScanParticipant> participants,
        List<int> parallelIndices,
        int workerCount,
        long objectCount,
        bool[] failed,
        ObjectScanCounter scanCounter,
        CancellationToken cancellationToken)
    {
        int participantCount = parallelIndices.Count;

        // workerInstances[w][p] / workerFailed[w][p] — worker w's private instance (and failure
        // state) of the p-th parallel-capable participant. Worker 0 reuses each participant's
        // original instance (already had BeforeHeapIndexScan called on it above), since that
        // instance is what ultimately serves AnalyzeAsync and receives the merged result.
        var workerInstances = new IHeapIndexScanParticipant[workerCount][];
        var workerFailed = new bool[workerCount][];

        for (int w = 0; w < workerCount; w++)
        {
            workerInstances[w] = new IHeapIndexScanParticipant[participantCount];
            workerFailed[w] = new bool[participantCount];

            for (int p = 0; p < participantCount; p++)
            {
                int originalIndex = parallelIndices[p];
                var owner = (IParallelHeapIndexScanParticipant)participants[originalIndex];

                if (w == 0)
                {
                    workerInstances[0][p] = owner;
                    workerFailed[0][p] = failed[originalIndex];
                    continue;
                }

                try
                {
                    IHeapIndexScanParticipant worker = owner.CreateWorkerInstance();
                    worker.BeforeHeapIndexScan(context);
                    workerInstances[w][p] = worker;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                {
                    workerInstances[w][p] = owner; // placeholder; never scanned since marked failed
                    workerFailed[w][p] = true;
                }
            }
        }

        long[] rangeStarts = new long[workerCount];
        long[] rangeCounts = new long[workerCount];
        long baseCount = objectCount / workerCount;
        long remainder = objectCount % workerCount;
        long cursor = 0;
        for (int w = 0; w < workerCount; w++)
        {
            long count = baseCount + (w < remainder ? 1 : 0);
            rangeStarts[w] = cursor;
            rangeCounts[w] = count;
            cursor += count;
        }

        long[] workerScanned = new long[workerCount];

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = workerCount
        };

        Parallel.For(0, workerCount, parallelOptions, w =>
        {
            long scanned = 0;
            foreach (HeapEntry entry in cache.EnumerateIndexedEntriesRange(rangeStarts[w], rangeCounts[w]))
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int p = 0; p < participantCount; p++)
                {
                    if (workerFailed[w][p])
                        continue;

                    try
                    {
                        workerInstances[w][p].OnHeapEntry(in entry);
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                    {
                        workerFailed[w][p] = true;
                    }
                }

                scanned++;
            }

            workerScanned[w] = scanned;
        });

        long totalScanned = 0;
        for (int w = 0; w < workerCount; w++)
            totalScanned += workerScanned[w];
        scanCounter.Advance(totalScanned, $"{ScanName}: merged {participantCount} parallel-capable participant(s).");

        for (int p = 0; p < participantCount; p++)
        {
            int originalIndex = parallelIndices[p];

            bool anyWorkerFailed = false;
            for (int w = 0; w < workerCount; w++)
            {
                if (workerFailed[w][p])
                {
                    anyWorkerFailed = true;
                    break;
                }
            }

            if (anyWorkerFailed)
            {
                failed[originalIndex] = true;
                continue;
            }

            var otherWorkers = new List<IHeapIndexScanParticipant>(workerCount - 1);
            for (int w = 1; w < workerCount; w++)
                otherWorkers.Add(workerInstances[w][p]);

            try
            {
                ((IParallelHeapIndexScanParticipant)participants[originalIndex]).MergePartial(otherWorkers);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                failed[originalIndex] = true;
            }
        }
    }
}
