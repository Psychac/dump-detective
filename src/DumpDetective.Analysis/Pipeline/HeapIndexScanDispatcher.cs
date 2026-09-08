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

    // Objects a parallel worker accumulates locally before folding the count into the shared
    // ObjectScanCounter with one Interlocked.Add — trades a slightly coarser progress cadence
    // for eliminating per-object cache-line contention across worker threads.
    private const long LocalTickBatch = 4096;

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

        // Stopwatch/diagnostics start *before* BeforeHeapIndexScan, not after: some participants
        // (e.g. EventLeakAnalyzer building its PublisherRegistry) do real heap/type work here that
        // can take tens of seconds. Starting the clock afterward left that time completely
        // unaccounted for — invisible on console and missing from the reported duration — making
        // "Scan + Index heap" look like it had a silent multi-second gap that didn't exist anywhere
        // else in the instrumentation.
        var diagnosticsPublisher = new AnalysisDiagnosticsPublisher();
        Guid runId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();

        // `elapsedSource` lets each named sub-pass (below) report its own duration instead of
        // the cumulative time since Run() started — otherwise every sub-pass's "duration" would
        // actually be a timestamp relative to dispatcher start, making completed passes look
        // far slower than they really were (and disagree with the stage's own wall-clock total).
        void Publish(AnalysisDiagnosticsEventType eventType, long scannedCount, string message, string? analyzerName = null, Stopwatch? elapsedSource = null) =>
            diagnosticsPublisher.Publish(context.DiagnosticsSink, new AnalysisDiagnosticsEvent(
                RunId: runId,
                EventType: eventType,
                TimestampUtc: DateTime.UtcNow,
                AnalyzerName: analyzerName ?? ScanName,
                Category: "SharedScan",
                DurationMs: (elapsedSource ?? stopwatch).Elapsed.TotalMilliseconds,
                ObjectScanCount: scannedCount,
                CacheHits: 0,
                CacheMisses: 0,
                Message: message,
                ExceptionType: null,
                ExceptionMessage: null));

        Publish(AnalysisDiagnosticsEventType.AnalyzerStarted, 0, $"{ScanName} started.");

        // Participant setup (e.g. EventLeakAnalyzer's PublisherRegistry.Build) can take tens of
        // seconds with no natural per-item progress hook. Run it on a background thread and poll
        // it here so the console keeps ticking a heartbeat instead of appearing frozen — only this
        // one thread ever touches the ClrHeap/ClrRuntime at a time, same as before.
        //
        // currentParticipantIndex/currentSubPhase are written before/during each participant's
        // BeforeHeapIndexScan call so the heartbeat below can name both which participant is
        // running and what it's doing internally (e.g. "building publisher registry") instead of
        // a static message — otherwise the live status line has nothing to show but elapsed time.
        // Both ride the "phase • detail" split other analyzers use to keep the detail portion
        // live-updating; without a " • " separator in the message, only the once-per-change
        // ↳ phase line gets the text, and the ticking status line stays blank.
        int currentParticipantIndex = -1;
        string? currentSubPhase = null;
        double[] setupCostByParticipant = new double[participants.Count];

        // No " • " separator here (unlike other Publish calls below) — each distinct participant/
        // sub-phase combination is reported as the phase itself, not as a fast-changing detail
        // behind a static phase, so ConsoleDiagnosticsSink's phase-transition tracking sees a
        // finite, bounded set of genuine transitions (one per participant × sub-phase).
        string BuildSetupPhaseText(int idx, string? subPhase)
        {
            string participantLabel = idx >= 0 && idx < participants.Count
                ? $"participant setup {idx + 1}/{participants.Count}: {participants[idx].GetType().Name}"
                : "participant setup: starting";
            return string.IsNullOrEmpty(subPhase) ? participantLabel : $"{participantLabel} — {subPhase}";
        }

        // Publishing only from the 300ms poll below (as before) means any participant whose
        // BeforeHeapIndexScan finishes faster than the poll interval can complete entirely between
        // two ticks — the index advances past it without a single poll ever observing it, so it
        // never gets reported at all. Publishing here, synchronously at the exact moment each
        // transition happens (on this same background thread, so no extra synchronization needed
        // beyond the Volatile writes the poll loop already reads), guarantees every participant and
        // every ReportSubPhase call gets its own event regardless of how briefly it ran; the poll
        // loop below still re-publishes periodically so the live elapsed/heartbeat keeps ticking
        // during whichever participant is currently slow.
        context.ReportSubPhase = phase =>
        {
            Volatile.Write(ref currentSubPhase, phase);
            Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, 0,
                BuildSetupPhaseText(Volatile.Read(ref currentParticipantIndex), phase));
        };

        Task setupTask = Task.Run(() =>
        {
            for (int i = 0; i < participants.Count; i++)
            {
                Interlocked.Exchange(ref currentParticipantIndex, i);
                Volatile.Write(ref currentSubPhase, null);
                Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, 0, BuildSetupPhaseText(i, null));
                long setupStart = DispatcherProfiler.Now();
                try
                {
                    participants[i].BeforeHeapIndexScan(context);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                {
                    failed[i] = true;
                }

                if (DispatcherProfiler.Enabled)
                    setupCostByParticipant[i] = DispatcherProfiler.ToMs(DispatcherProfiler.Now() - setupStart);
            }
        }, cancellationToken);

        while (!setupTask.Wait(300))
        {
            int idx = Volatile.Read(ref currentParticipantIndex);
            string? subPhase = Volatile.Read(ref currentSubPhase);
            Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, 0, BuildSetupPhaseText(idx, subPhase));
        }

        setupTask.GetAwaiter().GetResult();

        if (DispatcherProfiler.Enabled)
        {
            var setupRows = new List<(string, double, long)>(participants.Count);
            for (int i = 0; i < participants.Count; i++)
                setupRows.Add((participants[i].GetType().Name, setupCostByParticipant[i], 1));
            DispatcherProfiler.LogTable("PHASE 1 — owner participant setup (BeforeHeapIndexScan, sequential):", setupRows);
        }

        // Scoped to the single-threaded setup pass above: worker instances created for the
        // parallel pass below call BeforeHeapIndexScan concurrently on separate threads, and
        // this context is reused by later AnalyzeAsync calls in the same pipeline run — leaving
        // the hook set past setup would let unrelated code fire submodule text against a scan
        // that's no longer in its setup phase.
        context.ReportSubPhase = null;

        if (cache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
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
                // SynchronousProgress, not Progress<T>: Progress<T> marshals its callback through
                // the ThreadPool, which starves for the whole pass when the reporting thread is a
                // ThreadPool worker inside a CPU-bound Parallel.For that occupies every worker
                // thread (see RunParallelPass below) — the live count would stick at 0 until the
                // pass finished, then the queued callbacks would all fire in a burst at the end.
                var progress = new SynchronousProgress<AnalyzerProgressReport>(report =>
                    Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, report.ScannedCount, report.Phase));

                // Shorter time cadence than the default 2s so the console heartbeat feels as
                // responsive as the surrounding index-build/per-analyzer stages (both ~300ms),
                // instead of appearing to freeze between infrequent reports on small/medium heaps.
                var scanCounter = new ObjectScanCounter(ScanName, progress, reportEveryObjects: 250_000, reportEveryElapsed: TimeSpan.FromMilliseconds(300));

                RunSequentialPass(cache, participants, failed, mask: null, scanCounter, cancellationToken);

                scanCounter.Complete();
                Publish(AnalysisDiagnosticsEventType.AnalyzerCompleted, scanCounter.Scanned, $"{ScanName} completed.");
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

                // Both a sequential-only group and a parallel-capable group are active, so this
                // dispatcher runs two full physical passes over the index instead of one. Report
                // each pass under its own name with its own counter — summing them into a single
                // combined count would silently double-report the object count (each pass alone
                // walks every entry once).
                Publish(AnalysisDiagnosticsEventType.AnalyzerCompleted, 0, $"{ScanName}: participant setup complete.");

                const string ParallelPassName = ScanName + " (parallel)";
                Stopwatch parallelStopwatch = Stopwatch.StartNew();
                // SynchronousProgress: this pass runs under Parallel.For, which occupies every
                // ThreadPool worker for its whole duration — a Progress<T> callback posted from
                // inside it would queue behind (and only run after) the parallel work completes.
                var parallelProgress = new SynchronousProgress<AnalyzerProgressReport>(report =>
                    Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, report.ScannedCount, report.Phase, ParallelPassName, parallelStopwatch));
                var parallelCounter = new ObjectScanCounter(ParallelPassName, parallelProgress, reportEveryObjects: 250_000, reportEveryElapsed: TimeSpan.FromMilliseconds(300));

                Publish(AnalysisDiagnosticsEventType.AnalyzerStarted, 0, $"{ParallelPassName} started.", ParallelPassName, parallelStopwatch);
                RunParallelPass(cache, context, participants, parallelIndices, workerCount, heapIndex.ObjectCount, failed, parallelCounter, cancellationToken);
                parallelCounter.Complete();
                Publish(AnalysisDiagnosticsEventType.AnalyzerCompleted, parallelCounter.Scanned, $"{ParallelPassName} completed.", ParallelPassName, parallelStopwatch);

                if (anySequential)
                {
                    const string SequentialPassName = ScanName + " (sequential)";
                    Stopwatch sequentialStopwatch = Stopwatch.StartNew();
                    var sequentialProgress = new SynchronousProgress<AnalyzerProgressReport>(report =>
                        Publish(AnalysisDiagnosticsEventType.AnalyzerProgress, report.ScannedCount, report.Phase, SequentialPassName, sequentialStopwatch));
                    var sequentialCounter = new ObjectScanCounter(SequentialPassName, sequentialProgress, reportEveryObjects: 250_000, reportEveryElapsed: TimeSpan.FromMilliseconds(300));

                    Publish(AnalysisDiagnosticsEventType.AnalyzerStarted, 0, $"{SequentialPassName} started.", SequentialPassName, sequentialStopwatch);
                    RunSequentialPass(cache, participants, failed, sequentialMask, sequentialCounter, cancellationToken);
                    sequentialCounter.Complete();
                    Publish(AnalysisDiagnosticsEventType.AnalyzerCompleted, sequentialCounter.Scanned, $"{SequentialPassName} completed.", SequentialPassName, sequentialStopwatch);
                }
            }
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
        bool profile = DispatcherProfiler.Enabled;
        long[] sampledTicks = profile ? new long[participants.Count] : [];
        long[] sampledCalls = profile ? new long[participants.Count] : [];
        long sampleCursor = 0;
        long passStart = DispatcherProfiler.Now();

        foreach (HeapEntry entry in cache.EnumerateIndexedEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool sampleThis = profile && ++sampleCursor % DispatcherProfiler.SampleEvery == 0;

            for (int i = 0; i < participants.Count; i++)
            {
                if (failed[i] || (mask is not null && !mask[i]))
                    continue;

                long t0 = sampleThis ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    participants[i].OnHeapEntry(in entry);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                {
                    failed[i] = true;
                }

                if (sampleThis)
                {
                    sampledTicks[i] += Stopwatch.GetTimestamp() - t0;
                    sampledCalls[i]++;
                }
            }

            scanCounter.Tick();
        }

        if (profile)
        {
            DispatcherProfiler.Log($"SEQUENTIAL pass wall time: {DispatcherProfiler.ToMs(DispatcherProfiler.Now() - passStart):N1} ms over {sampleCursor:N0} entries");
            var rows = new List<(string, double, long)>(participants.Count);
            for (int i = 0; i < participants.Count; i++)
            {
                if (sampledCalls[i] == 0) continue;
                rows.Add((participants[i].GetType().Name,
                    DispatcherProfiler.ToMs(sampledTicks[i]) * DispatcherProfiler.SampleEvery, sampledCalls[i]));
            }
            DispatcherProfiler.LogTable(
                $"SEQUENTIAL pass — estimated per-participant OnHeapEntry cost (1-in-{DispatcherProfiler.SampleEvery} sampled, scaled):", rows);
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

        // Per-participant attribution accumulators (only populated under DD_DISPATCHER_PROFILE=1).
        double[] workerSetupMs = new double[participantCount];
        double[] mergeMs = new double[participantCount];
        long[] sampledScanTicks = new long[participantCount];
        long[] sampledScanCalls = new long[participantCount];
        long profileWorkerSetupStart = DispatcherProfiler.Now();

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

                long workerSetupStart = DispatcherProfiler.Now();
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

                if (DispatcherProfiler.Enabled)
                    workerSetupMs[p] += DispatcherProfiler.ToMs(DispatcherProfiler.Now() - workerSetupStart);
            }
        }

        if (DispatcherProfiler.Enabled)
        {
            var rows = new List<(string, double, long)>(participantCount);
            for (int p = 0; p < participantCount; p++)
                rows.Add((participants[parallelIndices[p]].GetType().Name, workerSetupMs[p], workerCount - 1));
            DispatcherProfiler.Log($"PHASE 2 — worker setup: {workerCount} workers, {participantCount} parallel participants, " +
                $"{DispatcherProfiler.ToMs(DispatcherProfiler.Now() - profileWorkerSetupStart):N1} ms total (sequential, on caller thread)");
            DispatcherProfiler.LogTable("PHASE 2 — per-worker CreateWorkerInstance + BeforeHeapIndexScan, summed over workers 1..N-1:", rows);
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

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = workerCount
        };

        long profileScanStart = DispatcherProfiler.Now();
        double[] workerWallMs = new double[workerCount];

        Parallel.For(0, workerCount, parallelOptions, w =>
        {
            long workerStart = DispatcherProfiler.Now();
            bool profile = DispatcherProfiler.Enabled;
            long[] localScanTicks = profile ? new long[participantCount] : [];
            long[] localScanCalls = profile ? new long[participantCount] : [];
            long sampleCursor = 0;

            // Local, non-atomic tally flushed to the shared counter every LocalTickBatch objects
            // instead of once per object — an Interlocked op per object here forces every worker
            // thread to fight over the same cache line for the whole pass, which on wide core
            // counts can cost more than the per-object analyzer work itself.
            long localTicked = 0;

            foreach (HeapEntry entry in cache.EnumerateIndexedEntriesRange(rangeStarts[w], rangeCounts[w]))
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool sampleThis = profile && ++sampleCursor % DispatcherProfiler.SampleEvery == 0;

                for (int p = 0; p < participantCount; p++)
                {
                    if (workerFailed[w][p])
                        continue;

                    long t0 = sampleThis ? Stopwatch.GetTimestamp() : 0;
                    try
                    {
                        workerInstances[w][p].OnHeapEntry(in entry);
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                    {
                        workerFailed[w][p] = true;
                    }

                    if (sampleThis)
                    {
                        localScanTicks[p] += Stopwatch.GetTimestamp() - t0;
                        localScanCalls[p]++;
                    }
                }

                if (++localTicked >= LocalTickBatch)
                {
                    scanCounter.AddParallel(localTicked);
                    localTicked = 0;
                }
            }

            if (localTicked > 0)
                scanCounter.AddParallel(localTicked);

            if (profile)
            {
                workerWallMs[w] = DispatcherProfiler.ToMs(DispatcherProfiler.Now() - workerStart);
                for (int p = 0; p < participantCount; p++)
                {
                    Interlocked.Add(ref sampledScanTicks[p], localScanTicks[p]);
                    Interlocked.Add(ref sampledScanCalls[p], localScanCalls[p]);
                }
            }
        });

        if (DispatcherProfiler.Enabled)
        {
            double scanWallMs = DispatcherProfiler.ToMs(DispatcherProfiler.Now() - profileScanStart);
            DispatcherProfiler.Log($"PHASE 3 — Parallel.For scan wall time: {scanWallMs:N1} ms over {objectCount:N0} entries, {workerCount} workers");

            var rows = new List<(string, double, long)>(participantCount);
            for (int p = 0; p < participantCount; p++)
            {
                // Sampled cost scaled back up to the full entry population, then divided by the
                // worker count to express it as wall-clock contribution rather than CPU-seconds.
                double estimatedCpuMs = DispatcherProfiler.ToMs(sampledScanTicks[p]) * DispatcherProfiler.SampleEvery;
                rows.Add((participants[parallelIndices[p]].GetType().Name, estimatedCpuMs / workerCount, sampledScanCalls[p]));
            }
            DispatcherProfiler.LogTable(
                $"PHASE 3 — estimated per-participant OnHeapEntry cost (1-in-{DispatcherProfiler.SampleEvery} sampled, scaled, /{workerCount} workers):", rows);

            var workerRows = new List<(string, double, long)>(workerCount);
            for (int w = 0; w < workerCount; w++)
                workerRows.Add(($"worker {w}", workerWallMs[w], rangeCounts[w]));
            DispatcherProfiler.LogTable("PHASE 3 — per-worker wall time (imbalance check):", workerRows);
        }

        scanCounter.Report($"{ScanName}: merged {participantCount} parallel-capable participant(s).");

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

            long mergeStart = DispatcherProfiler.Now();
            try
            {
                ((IParallelHeapIndexScanParticipant)participants[originalIndex]).MergePartial(otherWorkers);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                failed[originalIndex] = true;
            }

            if (DispatcherProfiler.Enabled)
                mergeMs[p] = DispatcherProfiler.ToMs(DispatcherProfiler.Now() - mergeStart);
        }

        if (DispatcherProfiler.Enabled)
        {
            var rows = new List<(string, double, long)>(participantCount);
            for (int p = 0; p < participantCount; p++)
                rows.Add((participants[parallelIndices[p]].GetType().Name, mergeMs[p], workerCount - 1));
            DispatcherProfiler.LogTable("PHASE 4 — MergePartial:", rows);
        }
    }
}
