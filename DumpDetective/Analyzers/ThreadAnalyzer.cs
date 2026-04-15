using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;
using System.Runtime.InteropServices;

namespace DumpDetective.Analyzers
{
    internal class ThreadAnalyzer : IAnalyzer
    {
        private const int MaxFramesForThreadScan = 8;
        private const int MaxStackRootsToCount = 256;

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
            new("BlockingIO", "socket.receive", "Potentially blocked waiting for network data."),
            new("BlockingIO", "socket.accept", "Potentially blocked accepting network connection."),
            new("BlockingIO", "filestream.read", "Potentially blocked on file I/O.")
        ];

        public string Name => "Thread Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Runtime);

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime)
        {
            var threadInfo = CategorizeThreads(runtime.Threads);

            return new AnalyzerExecutionResult(
                [CreateFinding(threadInfo)],
                new ThreadDomainResult(
                    threadInfo.AliveCount,
                    threadInfo.PotentiallyBlockedThreads.Count,
                    threadInfo.ThreadsWithLocks.Count,
                    threadInfo.ThreadsWithActiveExceptionsCount,
                    new Dictionary<string, int>(threadInfo.WaitCategoryDistribution)));
        }

        private static InsightFinding CreateFinding(ThreadCategorization info)
        {
            FindingSeverity severity = (info.ThreadsWithActiveExceptionsCount > 0 || info.PotentiallyBlockedThreads.Count >= 10)
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(ThreadAnalyzer),
                Category: "Threading",
                Severity: severity,
                Title: "Thread-state triage summary",
                Evidence: $"Alive threads: {info.AliveCount:N0}; blocked-pattern threads: {info.PotentiallyBlockedThreads.Count:N0}; lock-holding threads: {info.ThreadsWithLocks.Count:N0}; active thread exceptions: {info.ThreadsWithActiveExceptionsCount:N0}.",
                Recommendation: "Correlate blocked groups with lock owners and hotspot frames to isolate contention/deadlock candidates.",
                Tags: ["threads", "locks", "blocked", "exceptions"],
                MetricValue: info.PotentiallyBlockedThreads.Count,
                MetricUnit: "blocked-threads");
        }

        private ThreadCategorization CategorizeThreads(IEnumerable<ClrThread> threads)
        {
            var result = new ThreadCategorization();
            var threadsWithLocks = new List<ThreadWithStackTrace>();
            var blockedThreads = new List<ThreadWithStackTrace>();
            var threadsWithExceptions = new List<ThreadWithStackTrace>();
            var stackRootCountByThreadAddress = new Dictionary<ulong, int>();
            var scanCounter = new ObjectScanCounter("Thread scan", reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (var thread in threads)
            {
                scanCounter.Tick();

                result.TotalCount++;
                IncrementCount(result.StateDistribution, thread.State.ToString());
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
                    // Enumerate stack once and share the list across all categories for this thread
                    var stackFrames = thread.EnumerateStackTrace().Take(MaxFramesForThreadScan).ToList();
                    TrackTopFrameHotspot(result.TopFrameHotspots, stackFrames);

                    if (currentException != null)
                    {
                        threadsWithExceptions.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            ExceptionType = currentException.Type?.Name ?? StringConstants.UnknownType,
                            ExceptionMessage = currentException.Message,
                            StackRootCount = GetOrCountStackRoots(thread, stackRootCountByThreadAddress)
                        });
                    }

                    // Check for locks
                    if (thread.LockCount > 0)
                    {
                        threadsWithLocks.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            ExceptionType = currentException?.Type?.Name,
                            StackRootCount = GetOrCountStackRoots(thread, stackRootCountByThreadAddress)
                        });
                    }

                    // Detect wait/block patterns across all alive threads — cheap since frames are already materialized
                    var waitDetection = DetectWaitPattern(stackFrames);
                    if (waitDetection != null)
                    {
                        IncrementCount(result.WaitCategoryDistribution, waitDetection.Category);
                        blockedThreads.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            WaitCategory = waitDetection.Category,
                            WaitReason = waitDetection.Reason,
                            ExceptionType = currentException?.Type?.Name,
                            StackRootCount = GetOrCountStackRoots(thread, stackRootCountByThreadAddress)
                        });
                    }
                    else if (!thread.IsGc && !thread.IsFinalizer)
                    {
                        // Non-blocked user thread — track top frame for the Active Processing group
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
                        result.FinalizerIsBlocked = DetectWaitPattern(stackFrames) != null;
                    }

                    // Count MoveNext frames to measure async state-machine chain depth
                    int moveNextDepth = CountMoveNextDepth(stackFrames);
                    if (moveNextDepth > 0)
                    {
                        result.AsyncChainThreadCount++;
                        if (moveNextDepth > result.MaxAsyncChainDepth)
                            result.MaxAsyncChainDepth = moveNextDepth;
                    }
                }

                if (thread.IsGc)
                    result.GcCount++;

                if (thread.IsFinalizer)
                    result.FinalizerCount++;

                if (thread.State.HasFlag(ClrThreadState.TS_Background))
                    result.BackgroundCount++;
            }

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

            scanCounter.Complete();

            return result;
        }

        private static int GetOrCountStackRoots(ClrThread thread, Dictionary<ulong, int> cache)
        {
            if (cache.TryGetValue(thread.Address, out int existing))
                return existing;

            int count = 0;
            foreach (var _ in thread.EnumerateStackRoots().Take(MaxStackRootsToCount))
                count++;

            cache[thread.Address] = count;
            return count;
        }

        private WaitDetection? DetectWaitPattern(List<ClrStackFrame> frames)
        {
            foreach (var frame in frames)
            {
                string signature = GetFrameSignature(frame);

                foreach (var pattern in WaitPatterns)
                {
                    if (signature.Contains(pattern.Token, StringComparison.OrdinalIgnoreCase))
                    {
                        return new WaitDetection(pattern.Category, pattern.Reason);
                    }
                }
            }

            return null;
        }

        private static string GetFrameSignature(ClrStackFrame frame)
        {
            // Intentionally avoid frame.ToString() — it can return raw hex addresses
            // which pollute hotspot keys and are useless as triage output.
            return frame.Method?.Signature
                ?? frame.FrameName
                ?? string.Empty;
        }

        private static bool IsThreadPoolWorker(List<ClrStackFrame> frames)
        {
            foreach (var frame in frames)
            {
                string sig = GetFrameSignature(frame);
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
                if (GetFrameSignature(frame).Contains(".MoveNext()", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }
            return depth;
        }

        private static void TrackTopFrameHotspot(Dictionary<string, int> hotspots, List<ClrStackFrame> frames)
        {
            if (frames.Count == 0)
                return;

            string top = GetFrameSignature(frames[0]);
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

    internal sealed class WaitPattern
    {
        public WaitPattern(string category, string token, string reason)
        {
            Category = category;
            Token = token;
            Reason = reason;
        }

        public string Category { get; }
        public string Token { get; }
        public string Reason { get; }
    }

    internal sealed class WaitDetection
    {
        public WaitDetection(string category, string reason)
        {
            Category = category;
            Reason = reason;
        }

        public string Category { get; }
        public string Reason { get; }
    }
}
