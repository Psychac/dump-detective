using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers.Threads
{
    /// <summary>
    /// Canonical stack-walk participant for the thread-domain quartet (this analyzer,
    /// <c>HangAnalyzer</c>, <c>ThreadStackClusterAnalyzer</c>, <c>LockGraphAnalyzer</c>). Retyped
    /// version consumes <c>runtime.threads</c> exclusively — <see cref="ThreadAnalyzerLegacyAdapter"/>
    /// (not this class) implements <c>IThreadStackScanParticipant</c> and hands this analyzer a
    /// <c>PrecomputedRuntimeThreadQuery</c> backed by the pipeline's one shared stack scan, mirroring
    /// <c>LockGraphAnalyzerLegacyAdapter</c>'s already-proven shape
    /// (docs/refactor/modularity/phase-1-thread-quartet-plan.md). Falls back to a live
    /// <see cref="RuntimeThreadQuery"/> (which walks every thread independently) only when invoked
    /// outside the pipeline (tests, benchmarks).
    /// </summary>
    [AnalyzerModule(key: "thread", displayName: "Thread Analysis", order: 310, tags: ["threads"])]
    public sealed class ThreadAnalyzer :
        IAnalyzer, IProducesAnalyzerDomainResult, IThreadStackScanParticipant, ICustomizesAnalysisContext
    {
        private readonly ThreadStackScanState _scanState = new();
        private IHeapAnalysisCache? _cache;

        int IThreadStackScanParticipant.GetRequiredFrameCount(RuntimeAnalysisContext context) => UnboundedFrameCount;

        void IThreadStackScanParticipant.BeforeThreadStackScan(RuntimeAnalysisContext context)
        {
            _cache = context.Cache;
            _scanState.BeforeThreadStackScan();
        }

        void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot)
        {
            int stackRootCount = _cache?.GetOrCountThreadStackRoots(snapshot.Thread, UnboundedFrameCount) ?? 0;
            _scanState.OnThreadStack(in snapshot, stackRootCount);
        }

        void IThreadStackScanParticipant.OnThreadStackScanCompleted(bool succeeded) => _scanState.OnThreadStackScanCompleted(succeeded);

        Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
            AnalysisContextBuilder.Build(context, analyzerOptions: null, runtimeThreadsOverride: _scanState.BuildPrecomputedQuery());

        // No dump has a call stack anywhere near this deep — this is "walk the whole stack,
        // no artificial truncation" rather than a real bound. §11.4 M8 measured the identical
        // value on a real dump (135 threads) at 2 ms, so there's no cost concern in using a large
        // sentinel here.
        internal const int UnboundedFrameCount = 100_000;

        private static readonly WaitPattern[] WaitPatterns =
        [
            new("MonitorWait", "monitor.wait", "Thread waiting on monitor pulse/event."),
            new("MonitorContention", "monitor.enter", "Thread contending for a lock (monitor)."),
            new("TaskBlocking", "task.wait", "Synchronous wait on task completion."),
            new("TaskBlocking", "task`1.get_result", "Blocking on Task.Result."),
            new("TaskBlocking", "valuetask", "Blocking on ValueTask completion (e.g., .GetAwaiter().GetResult())."),
            new("Sleep", "thread.sleep", "Thread is sleeping."),
            new("Semaphore", "semaphore", "Waiting on semaphore permit."),
            new("Mutex", "mutex", "Waiting on mutex ownership."),
            new("WaitHandle", "waithandle", "Waiting on synchronization handle."),
            new("WaitHandle", "manualresetevent", "Waiting on ManualResetEvent."),
            new("WaitHandle", "autoresetevent", "Waiting on AutoResetEvent."),
            new("ThreadJoin", "thread.join", "Waiting for another thread to complete."),
            new("CountdownEvent", "countdownevent.wait", "Waiting on CountdownEvent to reach zero."),
            new("Barrier", "signalandwait", "Waiting on Barrier phase synchronization (SignalAndWait)."),
            new("BlockingIO", "socket.receive", "Blocking on socket receive."),
            new("BlockingIO", "socket.accept", "Blocking on socket accept."),
            new("BlockingIO", "filestream.read", "Blocking on file I/O read."),
        ];

        // Internal for direct unit testing of the production wait-pattern table.
        internal static IReadOnlyList<WaitPattern> WaitPatternsForTesting => WaitPatterns;

        public string Name => "Thread Analysis";
        public string Category => "Threads";

        public AnalyzerDomainResult? LastResult
        {
            get => _lastResult;
            // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
            // longer exists) so every LastResult consumer — pipeline or direct caller — sees
            // them populated the same way.
            private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
        }
        private AnalyzerDomainResult? _lastResult;

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IRuntimeThreadQuery threads = context.RuntimeThreads
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");

            LastResult = Analyze(threads, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private static ThreadDomainResult Analyze(IRuntimeThreadQuery threads, CancellationToken cancellationToken)
        {
            var categorization = new ThreadCategorization();
            var threadsWithLocks = new List<ThreadWithStackTrace>();
            var blockedThreads = new List<ThreadWithStackTrace>();
            var threadsWithExceptions = new List<ThreadWithStackTrace>();
            var otherThreads = new List<ThreadWithStackTrace>();

            foreach (RuntimeThreadRef thread in threads.EnumerateThreads())
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ThreadStackFrameRef> frames = thread.IsAlive
                    ? new List<ThreadStackFrameRef>(threads.EnumerateStackFrames(thread))
                    : [];

                ProcessThread(thread, frames, categorization, threadsWithLocks, blockedThreads, threadsWithExceptions, otherThreads);
            }

            FinalizeCategorization(categorization, threadsWithLocks, blockedThreads, threadsWithExceptions, otherThreads);
            return BuildDomainResult(categorization);
        }

        // Shared per-thread categorization body.
        private static void ProcessThread(
            RuntimeThreadRef thread,
            IReadOnlyList<ThreadStackFrameRef> availableFrames,
            ThreadCategorization result,
            List<ThreadWithStackTrace> threadsWithLocks,
            List<ThreadWithStackTrace> blockedThreads,
            List<ThreadWithStackTrace> threadsWithExceptions,
            List<ThreadWithStackTrace> otherThreads)
        {
            result.TotalCount++;
            IncrementCount(result.StateDistribution, thread.ThreadStateDisplay);
            IncrementCount(result.GcModeDistribution, thread.GcModeDisplay);

            string appDomain = thread.AppDomainName ?? "<No AppDomain>";
            IncrementCount(result.AppDomainDistribution, appDomain);

            if (thread.HasCurrentException)
            {
                result.ThreadsWithActiveExceptionsCount++;
                string exceptionType = thread.ExceptionTypeName ?? StringConstants.UnknownType;
                IncrementCount(result.ExceptionTypeDistribution, exceptionType);
            }

            if (thread.IsAlive)
            {
                result.AliveCount++;

                TrackTopFrameHotspot(result.TopFrameHotspots, availableFrames);

                if (thread.StackSizeBytes > 0)
                    result.StackSizeByteSamples.Add(thread.StackSizeBytes);

                if (thread.HasCurrentException)
                {
                    threadsWithExceptions.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = availableFrames,
                        ExceptionType = thread.ExceptionTypeName ?? StringConstants.UnknownType,
                        ExceptionMessage = thread.ExceptionMessage,
                        StackRootCount = thread.StackRootCount,
                    });
                }

                // Check for locks
                if (thread.LockCount > 0)
                {
                    threadsWithLocks.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = availableFrames,
                        ExceptionType = thread.HasCurrentException ? thread.ExceptionTypeName : null,
                        StackRootCount = thread.StackRootCount,
                    });
                }

                // Detect wait/block patterns across all alive threads
                WaitClassification? waitClassification = ClassifyWait(availableFrames);
                if (waitClassification != null)
                {
                    IncrementCount(result.WaitCategoryDistribution, waitClassification.Value.Category);
                    blockedThreads.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = availableFrames,
                        WaitCategory = waitClassification.Value.Category,
                        WaitReason = waitClassification.Value.Reason,
                        ExceptionType = thread.HasCurrentException ? thread.ExceptionTypeName : null,
                        StackRootCount = thread.StackRootCount,
                    });
                }
                else if (!thread.IsGc && !thread.IsFinalizer)
                {
                    // Non-blocked user thread — track top frame for the Active Processing group
                    TrackTopFrameHotspot(result.ActiveThreadHotspots, availableFrames);
                }

                // ThreadPool worker threads surface a recognisable dispatch frame;
                // TS_TPWorkerThread is the authoritative flag for this version of ClrMD.
                if (thread.IsThreadpoolWorker)
                    result.ThreadPoolCount++;
                else if (IsThreadPoolWorker(availableFrames))
                    result.ThreadPoolCount++;

                // Capture the finalizer thread's stack and blocked state once
                if (thread.IsFinalizer)
                {
                    result.FinalizerThread = thread;
                    result.FinalizerFrames = availableFrames;
                    result.FinalizerIsBlocked = waitClassification != null;
                }

                // Count MoveNext frames to measure async state-machine chain depth
                int moveNextDepth = CountMoveNextDepth(availableFrames);
                if (moveNextDepth > 0)
                {
                    result.AsyncChainThreadCount++;
                    if (moveNextDepth > result.MaxAsyncChainDepth)
                        result.MaxAsyncChainDepth = moveNextDepth;
                }

                // Every alive thread not already recorded above — a deterministic complete list,
                // not a reservoir sample (§9.23).
                bool isAlreadyCaptured = thread.LockCount > 0 || waitClassification != null || thread.HasCurrentException;
                if (!isAlreadyCaptured)
                {
                    otherThreads.Add(new ThreadWithStackTrace
                    {
                        Thread = thread,
                        TopFrames = availableFrames,
                        StackRootCount = thread.StackRootCount,
                    });
                }
            }

            if (thread.IsGc)
                result.GcCount++;

            if (thread.IsFinalizer)
                result.FinalizerCount++;

            if (thread.IsBackground)
                result.BackgroundCount++;
        }

        private static void FinalizeCategorization(
            ThreadCategorization result,
            List<ThreadWithStackTrace> threadsWithLocks,
            List<ThreadWithStackTrace> blockedThreads,
            List<ThreadWithStackTrace> threadsWithExceptions,
            List<ThreadWithStackTrace> otherThreads)
        {
            threadsWithLocks.Sort((a, b) => b.Thread.LockCount.CompareTo(a.Thread.LockCount));
            result.ThreadsWithLocks = threadsWithLocks;

            blockedThreads.Sort((a, b) => b.Thread.LockCount.CompareTo(a.Thread.LockCount));
            result.PotentiallyBlockedThreads = blockedThreads;

            threadsWithExceptions.Sort((a, b) => b.Thread.LockCount.CompareTo(a.Thread.LockCount));
            result.ThreadsWithExceptions = threadsWithExceptions;

            result.OtherThreads = otherThreads;
        }

        private static ThreadDomainResult BuildDomainResult(ThreadCategorization threadInfo)
        {
            // Materialize complete snapshots — no per-category cap. Report-width limiting is a
            // render-layer concern (§9.23 D5); the domain result carries the full ranked/complete
            // data.
            var locksSnapshots = new List<ThreadStateSnapshot>(threadInfo.ThreadsWithLocks.Count);
            for (int i = 0; i < threadInfo.ThreadsWithLocks.Count; i++)
                locksSnapshots.Add(ToThreadStateSnapshot(threadInfo.ThreadsWithLocks[i]));

            var blockedSnapshots = new List<ThreadStateSnapshot>(threadInfo.PotentiallyBlockedThreads.Count);
            for (int i = 0; i < threadInfo.PotentiallyBlockedThreads.Count; i++)
                blockedSnapshots.Add(ToThreadStateSnapshot(threadInfo.PotentiallyBlockedThreads[i]));

            var exceptionSnapshots = new List<ThreadExceptionSnapshot>(threadInfo.ThreadsWithExceptions.Count);
            for (int i = 0; i < threadInfo.ThreadsWithExceptions.Count; i++)
                exceptionSnapshots.Add(ToThreadExceptionSnapshot(threadInfo.ThreadsWithExceptions[i]));

            var topFrameHotspots = new List<NameCountEntry>(threadInfo.TopFrameHotspots.Count);
            if (threadInfo.TopFrameHotspots.Count > 0)
            {
                var kvpList = new List<KeyValuePair<string, int>>(threadInfo.TopFrameHotspots);
                kvpList.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < kvpList.Count; i++)
                    topFrameHotspots.Add(new NameCountEntry(kvpList[i].Key, kvpList[i].Value));
            }

            var activeThreadHotspots = new List<NameCountEntry>(threadInfo.ActiveThreadHotspots.Count);
            if (threadInfo.ActiveThreadHotspots.Count > 0)
            {
                var kvpList = new List<KeyValuePair<string, int>>(threadInfo.ActiveThreadHotspots);
                kvpList.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < kvpList.Count; i++)
                    activeThreadHotspots.Add(new NameCountEntry(kvpList[i].Key, kvpList[i].Value));
            }

            var otherThreadsSource = threadInfo.OtherThreads ?? [];
            var otherThreadSnapshots = new List<ThreadStateSnapshot>(otherThreadsSource.Count);
            for (int i = 0; i < otherThreadsSource.Count; i++)
                otherThreadSnapshots.Add(ToThreadStateSnapshot(otherThreadsSource[i]));

            var finalizerFrameStrings = new List<string>(threadInfo.FinalizerFrames?.Count ?? 0);
            if (threadInfo.FinalizerFrames != null)
            {
                for (int i = 0; i < threadInfo.FinalizerFrames.Count; i++)
                    finalizerFrameStrings.Add(GetFrameSignatureOrUnknown(threadInfo.FinalizerFrames[i]));
            }

            RuntimeThreadRef? finalizerThread = threadInfo.FinalizerThread;

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
                    new Dictionary<string, int>(threadInfo.ExceptionTypeDistribution),
                    locksSnapshots,
                    blockedSnapshots,
                    exceptionSnapshots,
                    topFrameHotspots,
                    activeThreadHotspots,
                    otherThreadSnapshots,
                    threadInfo.ThreadPoolCount,
                    threadInfo.ThreadPoolQueueDepth,
                    threadInfo.ThreadPoolActiveWorkers,
                    threadInfo.ThreadPoolIdleWorkers,
                    threadInfo.ThreadPoolMinWorkers,
                    threadInfo.ThreadPoolMaxWorkers,
                    threadInfo.FinalizerCount,
                    threadInfo.FinalizerIsBlocked,
                    finalizerThread.HasValue ? (uint?)finalizerThread.Value.Thread.ManagedThreadId : null,
                    finalizerThread?.Thread.OsThreadId,
                    finalizerThread.HasValue ? finalizerThread.Value.LockCount : 0,
                    finalizerFrameStrings,
                    threadInfo.AsyncChainThreadCount,
                    threadInfo.MaxAsyncChainDepth,
                    threadInfo.AliveCount > 0 ? (double)threadInfo.PotentiallyBlockedThreads.Count / threadInfo.AliveCount : 0.0,
                    BuildStackMemorySummary(threadInfo.StackSizeByteSamples));
        }

        // Internal for direct unit testing, matching CountMoveNextDepthFromSignatures.
        internal static ThreadStackMemorySummary? BuildStackMemorySummary(List<ulong> samples)
        {
            if (samples.Count == 0)
                return null;

            samples.Sort();

            ulong total = 0;
            for (int i = 0; i < samples.Count; i++)
                total += samples[i];

            ulong max = samples[^1];
            ulong p95 = samples[(int)Math.Floor((samples.Count - 1) * 0.95)];
            double mean = (double)total / samples.Count;

            return new ThreadStackMemorySummary(total, mean, max, p95, samples.Count);
        }

        private static ThreadStateSnapshot ToThreadStateSnapshot(ThreadWithStackTrace source)
        {
            var frameStrings = new List<string>(source.TopFrames.Count);
            for (int i = 0; i < source.TopFrames.Count; i++)
                frameStrings.Add(GetFrameSignatureOrUnknown(source.TopFrames[i]));

            return new ThreadStateSnapshot(
                (uint)(source.Thread.Thread.ManagedThreadId ?? 0),
                source.Thread.Thread.OsThreadId,
                source.Thread.LockCount,
                source.Thread.ThreadStateDisplay,
                source.Thread.GcModeDisplay,
                source.WaitCategory,
                source.WaitReason,
                frameStrings,
                source.StackRootCount,
                source.Thread.StackSizeBytes);
        }

        private static ThreadExceptionSnapshot ToThreadExceptionSnapshot(ThreadWithStackTrace source)
        {
            var frameStrings = new List<string>(source.TopFrames.Count);
            for (int i = 0; i < source.TopFrames.Count; i++)
                frameStrings.Add(GetFrameSignatureOrUnknown(source.TopFrames[i]));

            return new ThreadExceptionSnapshot(
                (uint)(source.Thread.Thread.ManagedThreadId ?? 0),
                source.Thread.Thread.OsThreadId,
                source.ExceptionType ?? StringConstants.UnknownType,
                source.ExceptionMessage,
                source.Thread.ThreadStateDisplay,
                source.Thread.GcModeDisplay,
                source.Thread.LockCount,
                frameStrings,
                source.StackRootCount);
        }

        private static WaitClassification? ClassifyWait(IReadOnlyList<ThreadStackFrameRef> frames)
        {
            foreach (ThreadStackFrameRef frame in frames)
            {
                WaitClassification? match = ThreadWaitClassifier.ClassifySignature(GetFrameSignature(frame), WaitPatterns);
                if (match != null)
                    return match;
            }
            return null;
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

        private static bool IsThreadPoolWorker(IReadOnlyList<ThreadStackFrameRef> frames)
        {
            foreach (ThreadStackFrameRef frame in frames)
            {
                string sig = GetFrameSignature(frame);
                if (sig.Contains("ThreadPoolWorkQueue", StringComparison.OrdinalIgnoreCase) ||
                    sig.Contains("ThreadPool.WorkQueue", StringComparison.OrdinalIgnoreCase) ||
                    sig.Contains("PortableThreadPool", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int CountMoveNextDepth(IReadOnlyList<ThreadStackFrameRef> frames)
        {
            int depth = 0;
            foreach (ThreadStackFrameRef frame in frames)
            {
                if (GetFrameSignature(frame).Contains(".MoveNext()", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }
            return depth;
        }

        // Mirrors the pre-retyping ThreadWaitClassifier.GetFrameSignature(ClrStackFrame) exactly,
        // against the SDK-shaped frame instead.
        private static string GetFrameSignature(ThreadStackFrameRef frame) =>
            frame.RawMethodSignature ?? frame.FrameName ?? string.Empty;

        private static string GetFrameSignatureOrUnknown(ThreadStackFrameRef frame) =>
            frame.RawMethodSignature ?? frame.FrameName ?? frame.MethodDisplayName ?? StringConstants.UnknownType;

        private static void TrackTopFrameHotspot(Dictionary<string, int> hotspots, IReadOnlyList<ThreadStackFrameRef> frames)
        {
            if (frames.Count == 0)
                return;

            // Find first non-framework frame for better hotspot signal
            for (int i = 0; i < frames.Count; i++)
            {
                string sig = GetFrameSignature(frames[i]);
                if (!IsFrameworkFrameSignature(sig))
                {
                    IncrementCount(hotspots, sig);
                    return;
                }
            }

            // Fall back to first frame if all are framework frames
            string fallback = GetFrameSignature(frames[0]);
            if (!string.IsNullOrWhiteSpace(fallback))
                IncrementCount(hotspots, fallback);
        }

        private static bool IsFrameworkFrameSignature(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature))
                return false;

            return signature.StartsWith("System.", StringComparison.Ordinal) ||
                   signature.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                   signature.StartsWith("ThreadPool", StringComparison.Ordinal) ||
                   signature.StartsWith("Task", StringComparison.Ordinal) ||
                   signature.StartsWith("mscorlib", StringComparison.Ordinal);
        }

        private static void IncrementCount(Dictionary<string, int> map, string key)
        {
            // Single hash lookup vs two (TryGetValue + indexer)
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
            count++;
        }

        public void Dispose() { }
    }

    internal sealed class ThreadCategorization
    {
        public int TotalCount { get; set; }
        public int AliveCount { get; set; }
        public int GcCount { get; set; }
        public int FinalizerCount { get; set; }
        public int BackgroundCount { get; set; }
        public int ThreadPoolCount { get; set; }
        public int ThreadsWithActiveExceptionsCount { get; set; }

        // Finalizer thread detail
        public RuntimeThreadRef? FinalizerThread { get; set; }
        public bool FinalizerIsBlocked { get; set; }
        public IReadOnlyList<ThreadStackFrameRef>? FinalizerFrames { get; set; }

        // Async state-machine chain depth
        public int AsyncChainThreadCount { get; set; }
        public int MaxAsyncChainDepth { get; set; }

        // Stack size (bytes) of every alive thread with a resolvable stack range — feeds
        // the total/mean/max/p95 StackMemorySummary. Bounded by thread count, not heap size.
        public List<ulong> StackSizeByteSamples { get; } = new();

        // ThreadPool telemetry — not available in ClrMD 4 (matches the pre-retyping analyzer's own
        // remark), left at 0.
        public int ThreadPoolQueueDepth { get; set; }
        public int ThreadPoolActiveWorkers { get; set; }
        public int ThreadPoolIdleWorkers { get; set; }
        public int ThreadPoolMinWorkers { get; set; }
        public int ThreadPoolMaxWorkers { get; set; }

        // Non-blocked user thread top-frame hotspots (Active Processing group)
        public Dictionary<string, int> ActiveThreadHotspots { get; set; } = new(StringComparer.Ordinal);

        public List<ThreadWithStackTrace> ThreadsWithLocks { get; set; } = new();
        public List<ThreadWithStackTrace> PotentiallyBlockedThreads { get; set; } = new();
        public List<ThreadWithStackTrace> ThreadsWithExceptions { get; set; } = new();
        public List<ThreadWithStackTrace>? OtherThreads { get; set; }
        public Dictionary<string, int> StateDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> GcModeDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> AppDomainDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> WaitCategoryDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ExceptionTypeDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TopFrameHotspots { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class ThreadWithStackTrace
    {
        public required RuntimeThreadRef Thread { get; set; }
        public IReadOnlyList<ThreadStackFrameRef> TopFrames { get; set; } = [];
        public int StackRootCount { get; set; }
        public string? WaitCategory { get; set; }
        public string? WaitReason { get; set; }
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }
    }
}
