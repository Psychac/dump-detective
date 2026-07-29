using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers
{
    // Canonical stack-walk provider for the thread-domain quartet (this analyzer, HangAnalyzer,
    // ThreadStackClusterAnalyzer, LockGraphAnalyzer): registers as an IThreadStackScanParticipant
    // so its per-thread categorization runs off ThreadStackScanDispatcher's single shared
    // EnumerateStackTrace() pass instead of walking runtime.Threads independently. The standalone
    // Analyze(...)/CategorizeThreads path below is kept as a fallback for callers that invoke this
    // analyzer directly (tests, benchmarks) without going through AnalysisPipeline's dispatcher.
    public class ThreadAnalyzer : IAnalyzer, IThreadStackScanParticipant
    {
        internal static int ComputeSamplerCapacity(int maxSampled, DumpSizeTier? tier, int totalThreads)
        {
            int capacity = maxSampled;
            if (tier is not null)
            {
                switch (tier.Value)
                {
                    case DumpSizeTier.Large:
                        capacity = Math.Max(1, capacity / 4);
                        break;
                    case DumpSizeTier.Medium:
                        capacity = Math.Max(1, capacity / 2);
                        break;
                    default:
                        break;
                }
            }

            capacity = Math.Min(capacity, Math.Max(0, totalThreads / 10));
            return capacity;
        }

        private static readonly WaitPattern[] WaitPatterns =
        [
            new("MonitorWait", "monitor.wait", "Thread waiting on monitor pulse/event."),
            new("MonitorContention", "monitor.enter", "Thread contending for a lock (monitor)."),
            new("TaskBlocking", "task.wait", "Synchronous wait on task completion."),
            new("TaskBlocking", "task`1.get_result", "Blocking on Task.Result."),
            new("Sleep", "thread.sleep", "Thread is sleeping."),
            new("Semaphore", "semaphore", "Waiting on semaphore permit."),
            new("Mutex", "mutex", "Waiting on mutex ownership."),
            new("WaitHandle", "waithandle", "Waiting on synchronization handle."),
            new("WaitHandle", "manualresetevent", "Waiting on ManualResetEvent."),
            new("WaitHandle", "autoresetevent", "Waiting on AutoResetEvent."),
            new("ThreadJoin", "thread.join", "Waiting for another thread to complete."),
            new("BlockingIO", "socket.receive", "Blocking on socket receive."),
            new("BlockingIO", "socket.accept", "Blocking on socket accept."),
            new("BlockingIO", "filestream.read", "Blocking on file I/O read."),
        ];

        public string Name => "Thread Analysis";
        public string Category => "Threads";

        // --- IThreadStackScanParticipant state: populated by BeforeThreadStackScan/OnThreadStack
        // (called by AnalysisPipeline's ThreadStackScanDispatcher) and consumed by AnalyzeAsync once
        // the shared stack-scan pass has completed. See OnThreadStackScanCompleted for the single
        // source of truth on whether this state is trustworthy.
        private ThreadAnalysisOptions? _participantOptions;
        private IHeapAnalysisCache? _participantCache;
        private IProgress<AnalyzerProgressReport>? _participantProgress;
        private ThreadCategorization? _categorization;
        private List<ThreadWithStackTrace>? _threadsWithLocks;
        private List<ThreadWithStackTrace>? _blockedThreads;
        private List<ThreadWithStackTrace>? _threadsWithExceptions;
        private Dictionary<ulong, int>? _stackRootCountByThreadAddress;
        private Utilities.ReservoirSampler<ThreadWithStackTrace>? _sampler;
        private ObjectScanCounter? _scanCounter;
        private bool _participantScanSucceeded;

        public int GetRequiredFrameCount(AnalysisContext context) =>
            ComputeEffectiveMaxFramesForSnapshot(context.AnalysisOptions.ThreadAnalysis);

        public void BeforeThreadStackScan(AnalysisContext context)
        {
            _participantOptions = context.AnalysisOptions.ThreadAnalysis;
            _participantCache = context.Cache;
            _participantProgress = context.Progress;

            PrewarmStackRootCounts(context.Runtime, _participantCache, _participantOptions, _participantProgress);

            // Cheap metadata-only pass (no stack walking) to size the reservoir sampler up front,
            // mirroring the threadList.Count used by the standalone CategorizeThreads path below.
            int totalThreads = 0;
            foreach (ClrThread _ in context.Runtime.Threads)
                totalThreads++;

            _categorization = new ThreadCategorization();
            _threadsWithLocks = new List<ThreadWithStackTrace>();
            _blockedThreads = new List<ThreadWithStackTrace>();
            _threadsWithExceptions = new List<ThreadWithStackTrace>();
            _stackRootCountByThreadAddress = new Dictionary<ulong, int>();
            _scanCounter = new ObjectScanCounter("Scanning threads", _participantProgress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            int samplerCapacity = ComputeSamplerCapacity(_participantOptions.MaxSampledStackSnapshots, _participantCache?.SizeTier, totalThreads);
            _sampler = new Utilities.ReservoirSampler<ThreadWithStackTrace>(samplerCapacity, _participantOptions.SamplingSeed);
        }

        void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot) => OnThreadStack(in snapshot);

        private void OnThreadStack(in ThreadStackSnapshot snapshot)
        {
            _scanCounter!.Tick();
            ProcessThread(
                snapshot.Thread,
                snapshot.TopFrames,
                _participantOptions!,
                _participantCache,
                _categorization!,
                _threadsWithLocks!,
                _blockedThreads!,
                _threadsWithExceptions!,
                _stackRootCountByThreadAddress!,
                _sampler!);
        }

        public void OnThreadStackScanCompleted(bool succeeded)
        {
            _participantScanSucceeded = succeeded;
            if (succeeded && _categorization != null)
            {
                FinalizeCategorization(_categorization, _threadsWithLocks!, _blockedThreads!, _threadsWithExceptions!, _sampler!, _scanCounter!, _participantProgress);
            }
        }

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThreadAnalysisOptions options = context.AnalysisOptions.ThreadAnalysis;

            AnalyzerDomainResult result = _participantScanSucceeded && _categorization != null
                ? BuildDomainResult(_categorization, options, context.Progress)
                : Analyze(context.Runtime, options, context.Progress, context.Cache);

            return ValueTask.FromResult(result.Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, new ThreadAnalysisOptions(), progress: null, cache: null);
        }

        // Fallback path used when this analyzer is invoked directly (tests, benchmarks) instead of
        // through AnalysisPipeline's ThreadStackScanDispatcher.
        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ThreadAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, IHeapAnalysisCache? cache)
        {
            PrewarmStackRootCounts(runtime, cache, options, progress);
            ThreadCategorization threadInfo = CategorizeThreads(runtime.Threads, options, progress, cache);
            return BuildDomainResult(threadInfo, options, progress);
        }

        private static void PrewarmStackRootCounts(ClrRuntime runtime, IHeapAnalysisCache? cache, ThreadAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            // Prewarm stack-root counts either synchronously or in background depending on options.
            // For the Full preset we prefer background prewarm.
            if (cache is null || options.MaxThreadsToCaptureSnapshots <= 0)
                return;

            int prewarm = options.MaxThreadsToCaptureSnapshots;
            if (options.PrewarmCacheInBackground)
            {
                progress?.Report(new(0, "Starting background prewarm of thread stack-root counts"));
                _ = Task.Run(() =>
                {
                    int idx = 0;
                    foreach (var t in runtime.Threads)
                    {
                        cache.GetOrCountThreadStackRoots(t, options.MaxStackRootsToCount);
                        if (++idx >= prewarm)
                            break;
                        if ((idx & 0xF) == 0)
                            progress?.Report(new(idx, $"Background prewarm: {idx}/{prewarm}"));
                    }
                    progress?.Report(new(prewarm, $"Background prewarm complete: {Math.Min(prewarm, prewarm)} threads"));
                });
            }
            else if (cache.SizeTier != DumpSizeTier.Large)
            {
                progress?.Report(new(0, "Prewarming thread stack-root counts"));
                int idx = 0;
                foreach (var t in runtime.Threads)
                {
                    cache.GetOrCountThreadStackRoots(t, options.MaxStackRootsToCount);
                    if (++idx >= prewarm)
                        break;
                }
                progress?.Report(new(0, $"Prewarmed {Math.Min(prewarm, idx)} threads"));
            }
        }

        private static AnalyzerDomainResult BuildDomainResult(ThreadCategorization threadInfo, ThreadAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            // Decide effective frame window for snapshots (expand when Full requested)
            int effectiveMaxFramesForSnapshot = ComputeEffectiveMaxFramesForSnapshot(options);

            // Materialize limited snapshots without LINQ to avoid iterator allocations in hot paths.
            var locksSnapshots = new List<ThreadStateSnapshot>(Math.Min(options.MaxThreadsToCaptureSnapshots, threadInfo.ThreadsWithLocks.Count));
            for (int i = 0; i < threadInfo.ThreadsWithLocks.Count && locksSnapshots.Count < options.MaxThreadsToCaptureSnapshots; i++)
                locksSnapshots.Add(ToThreadStateSnapshot(threadInfo.ThreadsWithLocks[i], effectiveMaxFramesForSnapshot));

            var blockedSnapshots = new List<ThreadStateSnapshot>(Math.Min(options.MaxThreadsToCaptureSnapshots, threadInfo.PotentiallyBlockedThreads.Count));
            for (int i = 0; i < threadInfo.PotentiallyBlockedThreads.Count && blockedSnapshots.Count < options.MaxThreadsToCaptureSnapshots; i++)
                blockedSnapshots.Add(ToThreadStateSnapshot(threadInfo.PotentiallyBlockedThreads[i], effectiveMaxFramesForSnapshot));

            var exceptionSnapshots = new List<ThreadExceptionSnapshot>(Math.Min(options.MaxThreadsToCaptureSnapshots, threadInfo.ThreadsWithExceptions.Count));
            for (int i = 0; i < threadInfo.ThreadsWithExceptions.Count && exceptionSnapshots.Count < options.MaxThreadsToCaptureSnapshots; i++)
                exceptionSnapshots.Add(ToThreadExceptionSnapshot(threadInfo.ThreadsWithExceptions[i], effectiveMaxFramesForSnapshot));

            var topFrameHotspots = new List<NameCountEntry>(Math.Min(options.MaxTopHotspots, threadInfo.TopFrameHotspots.Count));
            if (threadInfo.TopFrameHotspots.Count > 0)
            {
                var kvpList = new List<KeyValuePair<string, int>>(threadInfo.TopFrameHotspots);
                kvpList.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < kvpList.Count && topFrameHotspots.Count < options.MaxTopHotspots; i++)
                    topFrameHotspots.Add(new NameCountEntry(kvpList[i].Key, kvpList[i].Value));
            }

            var activeThreadHotspots = new List<NameCountEntry>(Math.Min(options.MaxTopHotspots, threadInfo.ActiveThreadHotspots.Count));
            if (threadInfo.ActiveThreadHotspots.Count > 0)
            {
                var kvpList = new List<KeyValuePair<string, int>>(threadInfo.ActiveThreadHotspots);
                kvpList.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < kvpList.Count && activeThreadHotspots.Count < options.MaxTopHotspots; i++)
                    activeThreadHotspots.Add(new NameCountEntry(kvpList[i].Key, kvpList[i].Value));
            }

            progress?.Report(new(threadInfo.TotalCount, "Materializing snapshots"));

            var sampledSnapshots = new List<ThreadStateSnapshot>(Math.Min(options.MaxSampledStackSnapshots, threadInfo.SampledThreads?.Count ?? 0));
            var sampledSource = threadInfo.SampledThreads ?? new List<ThreadWithStackTrace>();
            for (int i = 0; i < sampledSource.Count && sampledSnapshots.Count < options.MaxSampledStackSnapshots; i++)
                sampledSnapshots.Add(ToThreadStateSnapshot(sampledSource[i], effectiveMaxFramesForSnapshot));

            var finalizerFrameStrings = new List<string>(Math.Min(options.MaxFramesForThreadScan, threadInfo.FinalizerFrames?.Count ?? 0));
            if (threadInfo.FinalizerFrames != null)
            {
                for (int i = 0; i < threadInfo.FinalizerFrames.Count && finalizerFrameStrings.Count < options.MaxFramesForThreadScan; i++)
                {
                    var f = threadInfo.FinalizerFrames[i];
                    finalizerFrameStrings.Add(f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType);
                }
            }

            return new ThreadDomainResult(
                    threadInfo.TotalCount,
                    threadInfo.AliveCount,
                    Math.Max(0, threadInfo.TotalCount - threadInfo.AliveCount),
                    threadInfo.GcCount,
                    threadInfo.PotentiallyBlockedThreads.Count,
                    threadInfo.ThreadsWithLocks.Count,
                    threadInfo.ThreadsWithActiveExceptionsCount,
                    threadInfo.BackgroundCount,
                    new Dictionary<string, int>(threadInfo.WaitCategoryDistribution),
                    new Dictionary<string, int>(threadInfo.StateDistribution),
                    new Dictionary<string, int>(threadInfo.AppDomainDistribution),
                    new Dictionary<string, int>(threadInfo.GcModeDistribution),
                    locksSnapshots,
                    blockedSnapshots,
                    exceptionSnapshots,
                    topFrameHotspots,
                    activeThreadHotspots,
                    sampledSnapshots,
                    threadInfo.ThreadPoolCount,
                    threadInfo.FinalizerCount,
                    threadInfo.FinalizerIsBlocked,
                    threadInfo.FinalizerThread != null ? (uint?)threadInfo.FinalizerThread.ManagedThreadId : null,
                    threadInfo.FinalizerThread?.OSThreadId,
                    threadInfo.FinalizerThread != null ? (int)threadInfo.FinalizerThread.LockCount : 0,
                    finalizerFrameStrings,
                    threadInfo.AsyncChainThreadCount,
                    threadInfo.MaxAsyncChainDepth,
                    sampledSnapshots.Count,
                    (locksSnapshots.Count + blockedSnapshots.Count + exceptionSnapshots.Count),
                    options.MaxSampledStackSnapshots,
                    options.SamplingSeed);
        }

        private static ThreadStateSnapshot ToThreadStateSnapshot(ThreadWithStackTrace source, int maxFramesForThreadScan)
        {
            ulong stackSizeBytes = source.Thread.StackBase > source.Thread.StackLimit
                ? source.Thread.StackBase - source.Thread.StackLimit
                : 0;

            return new ThreadStateSnapshot(
                (uint)source.Thread.ManagedThreadId,
                source.Thread.OSThreadId,
                (int)source.Thread.LockCount,
                FormatThreadState(source.Thread.State),
                source.Thread.GCMode.ToString(),
                source.WaitCategory,
                source.WaitReason,
                source.TopFrames
                    .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                    .Take(maxFramesForThreadScan)
                    .ToArray(),
                source.StackRootCount,
                stackSizeBytes);
        }

        private static ThreadExceptionSnapshot ToThreadExceptionSnapshot(ThreadWithStackTrace source, int maxFramesForThreadScan)
        {
            return new ThreadExceptionSnapshot(
                (uint)source.Thread.ManagedThreadId,
                source.Thread.OSThreadId,
                source.ExceptionType ?? StringConstants.UnknownType,
                source.ExceptionMessage,
                FormatThreadState(source.Thread.State),
                source.Thread.GCMode.ToString(),
                (int)source.Thread.LockCount,
                source.TopFrames
                    .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                    .Take(maxFramesForThreadScan)
                    .ToArray(),
                source.StackRootCount);
        }

        // Fallback (non-participant) path: walks each thread's stack once, up to the effective
        // snapshot window, then runs it through the same per-thread logic OnThreadStack uses.
        private ThreadCategorization CategorizeThreads(IEnumerable<ClrThread> threads, ThreadAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, IHeapAnalysisCache? cache)
        {
            IList<ClrThread> threadList = threads as IList<ClrThread> ?? threads.ToArray();

            var result = new ThreadCategorization();
            var threadsWithLocks = new List<ThreadWithStackTrace>();
            var blockedThreads = new List<ThreadWithStackTrace>();
            var threadsWithExceptions = new List<ThreadWithStackTrace>();
            var stackRootCountByThreadAddress = new Dictionary<ulong, int>();
            var scanCounter = new ObjectScanCounter("Scanning threads", progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            // Adaptive sampler capacity: reduce sampling on very large dumps to limit work.
            int samplerCapacity = ComputeSamplerCapacity(options.MaxSampledStackSnapshots, cache?.SizeTier, threadList.Count);
            var sampler = new Utilities.ReservoirSampler<ThreadWithStackTrace>(samplerCapacity, options.SamplingSeed);

            int extendedWindow = ComputeEffectiveMaxFramesForSnapshot(options);

            foreach (var thread in threadList)
            {
                scanCounter.Tick();

                IReadOnlyList<ClrStackFrame> frames = Array.Empty<ClrStackFrame>();
                if (thread.IsAlive)
                {
                    var buffer = new List<ClrStackFrame>(extendedWindow);
                    foreach (var f in thread.EnumerateStackTrace())
                    {
                        if (buffer.Count >= extendedWindow)
                            break;
                        buffer.Add(f);
                    }
                    frames = buffer;
                }

                ProcessThread(thread, frames, options, cache, result, threadsWithLocks, blockedThreads, threadsWithExceptions, stackRootCountByThreadAddress, sampler);
            }

            FinalizeCategorization(result, threadsWithLocks, blockedThreads, threadsWithExceptions, sampler, scanCounter, progress);
            return result;
        }

        // Shared per-thread categorization body for both the dispatcher-fed path (OnThreadStack)
        // and the standalone fallback path (CategorizeThreads). `availableFrames` must already
        // contain up to ComputeEffectiveMaxFramesForSnapshot(options) frames captured in a single
        // walk — this method never re-enumerates the thread's stack.
        private static void ProcessThread(
            ClrThread thread,
            IReadOnlyList<ClrStackFrame> availableFrames,
            ThreadAnalysisOptions options,
            IHeapAnalysisCache? cache,
            ThreadCategorization result,
            List<ThreadWithStackTrace> threadsWithLocks,
            List<ThreadWithStackTrace> blockedThreads,
            List<ThreadWithStackTrace> threadsWithExceptions,
            Dictionary<ulong, int> stackRootCountByThreadAddress,
            Utilities.ReservoirSampler<ThreadWithStackTrace> sampler)
        {
            result.TotalCount++;
            IncrementCount(result.StateDistribution, FormatThreadState(thread.State));
            IncrementCount(result.GcModeDistribution, thread.GCMode.ToString());

            string appDomain = thread.CurrentAppDomain?.Name ?? "<No AppDomain>";
            IncrementCount(result.AppDomainDistribution, appDomain);

            // Cache the property — each access reads from CLRMD runtime structures
            var currentException = thread.CurrentException;
            if (currentException != null)
            {
                result.ThreadsWithActiveExceptionsCount++;
                string exceptionType = currentException.Type?.Name ?? StringConstants.UnknownType;
                IncrementCount(result.ExceptionTypeDistribution, exceptionType);
            }

            if (thread.IsAlive)
            {
                result.AliveCount++;

                // Base detection window — matches the pre-shared-scan behavior where wait-pattern
                // detection, hotspot tracking, and thread-pool detection only looked at the first
                // MaxFramesForThreadScan frames. The window is only widened below (in place) for
                // the stored TopFrames when an async continuation chain is detected.
                int baseWindow = Math.Min(options.MaxFramesForThreadScan, availableFrames.Count);
                var stackFrames = new List<ClrStackFrame>(baseWindow);
                for (int i = 0; i < baseWindow; i++)
                    stackFrames.Add(availableFrames[i]);

                TrackTopFrameHotspot(result.TopFrameHotspots, stackFrames);

                if (currentException != null)
                {
                    int exceptionStackRoots = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount);
                    threadsWithExceptions.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = stackFrames,
                        ExceptionType = currentException.Type?.Name ?? StringConstants.UnknownType,
                        ExceptionMessage = currentException.Message,
                        StackRootCount = exceptionStackRoots
                    });
                }

                // Check for locks
                if (thread.LockCount > 0)
                {
                    int lockStackRoots = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount);
                    threadsWithLocks.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = stackFrames,
                        ExceptionType = currentException?.Type?.Name,
                        StackRootCount = lockStackRoots
                    });
                }

                // Detect wait/block patterns across all alive threads — cheap since frames are already materialized
                WaitClassification? waitClassification = options.DetectWaitPatterns ? ThreadWaitClassifier.Classify(stackFrames, WaitPatterns) : null;
                if (waitClassification != null)
                {
                    IncrementCount(result.WaitCategoryDistribution, waitClassification.Value.Category);
                    int blockedStackRoots = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount);
                    blockedThreads.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = stackFrames,
                        WaitCategory = waitClassification.Value.Category,
                        WaitReason = waitClassification.Value.Reason,
                        ExceptionType = currentException?.Type?.Name,
                        StackRootCount = blockedStackRoots
                    });
                }
                else if (!thread.IsGc && !thread.IsFinalizer)
                {
                    // Non-blocked user thread — track top frame for the Active Processing group
                    if (options.IncludeStackSamples)
                        TrackTopFrameHotspot(result.ActiveThreadHotspots, stackFrames);
                }

                // ThreadPool worker threads surface a recognisable dispatch frame;
                // TS_TPWorkerThread is the authoritative flag for this version of ClrMD.
                if (thread.State.HasFlag(ClrThreadState.TS_TPWorkerThread) || IsThreadPoolWorker(stackFrames))
                    result.ThreadPoolCount++;

                // Capture the finalizer thread's stack and blocked state once
                if (thread.IsFinalizer)
                {
                    result.FinalizerThread = thread;
                    result.FinalizerFrames = stackFrames;
                    result.FinalizerIsBlocked = waitClassification != null;
                }

                // Count MoveNext frames to measure async state-machine chain depth
                if (options.AsyncChainDetection != AsyncChainDetectionMode.Disabled)
                {
                    int moveNextDepth = CountMoveNextDepth(stackFrames);
                    if (moveNextDepth > 0)
                    {
                        result.AsyncChainThreadCount++;
                        if (moveNextDepth > result.MaxAsyncChainDepth)
                            result.MaxAsyncChainDepth = moveNextDepth;

                        // If configured for Full, widen the stored frame window (in place — every
                        // entry above already references this same list) using the frames already
                        // captured by the single shared walk, up to the effective snapshot window.
                        if (options.AsyncChainDetection == AsyncChainDetectionMode.Full)
                        {
                            int extendedWindow = Math.Min(availableFrames.Count, ComputeEffectiveMaxFramesForSnapshot(options));
                            for (int i = stackFrames.Count; i < extendedWindow; i++)
                                stackFrames.Add(availableFrames[i]);
                        }
                    }
                }

                // Sample non-top threads when enabled. Use reservoir sampling to cap selection.
                if (options.IncludeStackSamples && options.MaxSampledStackSnapshots > 0)
                {
                    // Only consider threads not already recorded in locks/blocked/exceptions lists
                    bool isAlreadyCaptured = thread.LockCount > 0 || waitClassification != null || currentException != null;
                    if (!isAlreadyCaptured)
                    {
                        var candidate = new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            ExceptionType = currentException?.Type?.Name,
                            StackRootCount = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount)
                        };

                        sampler.Add(candidate);
                    }
                }
            }

            if (thread.IsGc)
                result.GcCount++;

            if (thread.IsFinalizer)
                result.FinalizerCount++;

            if (thread.State.HasFlag(ClrThreadState.TS_Background))
                result.BackgroundCount++;
        }

        private static void FinalizeCategorization(
            ThreadCategorization result,
            List<ThreadWithStackTrace> threadsWithLocks,
            List<ThreadWithStackTrace> blockedThreads,
            List<ThreadWithStackTrace> threadsWithExceptions,
            Utilities.ReservoirSampler<ThreadWithStackTrace> sampler,
            ObjectScanCounter scanCounter,
            IProgress<AnalyzerProgressReport>? progress)
        {
            // Sort threads with locks by lock count (descending)
            result.ThreadsWithLocks = threadsWithLocks
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            result.PotentiallyBlockedThreads = blockedThreads
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            result.ThreadsWithExceptions = threadsWithExceptions
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            // materialize reservoir samples into the categorization result
            if (sampler.Capacity > 0)
            {
                result.SampledThreads = sampler.Samples().ToList();
                progress?.Report(new(scanCounter.Scanned, "Thread sampling complete"));
            }

            scanCounter.Complete();

            progress?.Report(new(scanCounter.Scanned, "Thread analysis complete"));
        }

        private static int GetOrCountStackRoots(ClrThread thread, Dictionary<ulong, int> cache, IHeapAnalysisCache? sharedCache, int maxStackRootsToCount)
        {
            if (thread.Address == 0)
                return 0;

            if (cache.TryGetValue(thread.Address, out int existing))
                return existing;

            int count = sharedCache is not null
                ? sharedCache.GetOrCountThreadStackRoots(thread, maxStackRootsToCount)
                : CountStackRoots(thread, maxStackRootsToCount);

            cache[thread.Address] = count;
            return count;
        }

        private static int CountStackRoots(ClrThread thread, int maxStackRootsToCount)
        {
            int count = 0;
            foreach (var _ in thread.EnumerateStackRoots())
            {
                if (count >= maxStackRootsToCount)
                    break;
                count++;
            }

            return count;
        }

        // Testable helper — sample integer candidate indices deterministically.
        internal static IReadOnlyList<int> SampleCandidateIndices(int totalCandidates, int capacity, int seed)
        {
            var sampler = new Utilities.ReservoirSampler<int>(capacity, seed);
            for (int i = 0; i < totalCandidates; i++) sampler.Add(i);
            return sampler.Samples();
        }

        // Internal helper: compute effective frame window for snapshot materialization.
        internal static int ComputeEffectiveMaxFramesForSnapshot(ThreadAnalysisOptions options)
        {
            return (options.AsyncChainDetection == AsyncChainDetectionMode.Full)
                ? Math.Min(64, options.MaxFramesForThreadScan * 2)
                : options.MaxFramesForThreadScan;
        }

        // Internal test helper: count occurrences of MoveNext() in a list of frame signature strings.
        internal static int CountMoveNextDepthFromSignatures(IReadOnlyList<string> frameSignatures)
        {
            int depth = 0;
            foreach (var sig in frameSignatures)
            {
                if (!string.IsNullOrEmpty(sig) && sig.Contains(".MoveNext()", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }
            return depth;
        }

        private static bool IsThreadPoolWorker(List<ClrStackFrame> frames)
        {
            foreach (var frame in frames)
            {
                string sig = ThreadWaitClassifier.GetFrameSignature(frame);
                if (sig.Contains("ThreadPoolWorkQueue", StringComparison.OrdinalIgnoreCase) ||
                    sig.Contains("ThreadPool.WorkQueue", StringComparison.OrdinalIgnoreCase) ||
                    sig.Contains("PortableThreadPool", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int CountMoveNextDepth(List<ClrStackFrame> frames)
        {
            int depth = 0;
            foreach (var frame in frames)
            {
                if (ThreadWaitClassifier.GetFrameSignature(frame).Contains(".MoveNext()", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }
            return depth;
        }

        private static string FormatThreadState(ClrThreadState state)
        {
            ulong raw = Convert.ToUInt64(state);
            if (raw == 0)
                return "0";

            var parts = new List<string>(capacity: 4);
            ulong remaining = raw;

            foreach (ClrThreadState flag in Enum.GetValues<ClrThreadState>())
            {
                ulong flagValue = Convert.ToUInt64(flag);
                if (flagValue == 0)
                    continue;

                if ((remaining & flagValue) == flagValue)
                {
                    parts.Add(flag.ToString());
                    remaining &= ~flagValue;
                }
            }

            if (parts.Count == 0)
                return $"0x{raw:X}";

            if (remaining != 0)
                parts.Add($"0x{remaining:X}");

            return string.Join(" | ", parts);
        }

        private static void TrackTopFrameHotspot(Dictionary<string, int> hotspots, List<ClrStackFrame> frames)
        {
            if (frames.Count == 0)
                return;

            string top = ThreadWaitClassifier.GetFrameSignature(frames[0]);
            if (string.IsNullOrWhiteSpace(top))
                return;

            IncrementCount(hotspots, top);
        }

        private static void IncrementCount(Dictionary<string, int> map, string key)
        {
            // Single hash lookup vs two (TryGetValue + indexer)
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
            count++;
        }

        public void Dispose() { }
    }

    internal class ThreadCategorization
    {
        public int TotalCount { get; set; }
        public int AliveCount { get; set; }
        public int GcCount { get; set; }
        public int FinalizerCount { get; set; }
        public int BackgroundCount { get; set; }
        public int ThreadPoolCount { get; set; }
        public int ThreadsWithActiveExceptionsCount { get; set; }

        // Finalizer thread detail
        public ClrThread? FinalizerThread { get; set; }
        public bool FinalizerIsBlocked { get; set; }
        public List<ClrStackFrame> FinalizerFrames { get; set; } = new();

        // Async state-machine chain depth
        public int AsyncChainThreadCount { get; set; }
        public int MaxAsyncChainDepth { get; set; }

        // Non-blocked user thread top-frame hotspots (Active Processing group)
        public Dictionary<string, int> ActiveThreadHotspots { get; set; } = new(StringComparer.Ordinal);

        public List<ThreadWithStackTrace> ThreadsWithLocks { get; set; } = new();
        public List<ThreadWithStackTrace> PotentiallyBlockedThreads { get; set; } = new();
        public List<ThreadWithStackTrace> ThreadsWithExceptions { get; set; } = new();
        public List<ThreadWithStackTrace> SampledThreads { get; set; } = new();
        public Dictionary<string, int> StateDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> GcModeDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> AppDomainDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> WaitCategoryDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ExceptionTypeDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TopFrameHotspots { get; set; } = new(StringComparer.Ordinal);
    }

    internal class ThreadWithStackTrace
    {
        public required ClrThread Thread { get; set; }
        public List<ClrStackFrame> TopFrames { get; set; } = new();
        public int StackRootCount { get; set; }
        public string? WaitCategory { get; set; }
        public string? WaitReason { get; set; }
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }
    }
}
