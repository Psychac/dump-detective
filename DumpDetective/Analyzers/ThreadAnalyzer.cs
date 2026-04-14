using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;
using System.Runtime.InteropServices;

namespace DumpDetective.Analyzers
{
    internal class ThreadAnalyzer
    {
        private const int MaxFramesForThreadScan = 8;
        private const int MaxThreadsToDisplayPerSection = 12;
        private const int MaxStackRootsToCount = 256;
        private const int MaxTopFrameHotspotsToDisplay = 10;
        private const int MaxDistributionRowsToDisplay = 10;
        private const int MaxMethodLength = 85;
        private const int TopActiveFramesToShow = 5;

        private static readonly Dictionary<string, (string Icon, string Label, string Pattern, bool IsWarning, string? RiskNote)> GroupMeta =
            new(StringComparer.Ordinal)
            {
                ["TaskBlocking"]      = ("⏳", "Sync-over-Async",         ".Wait() / .Result blocking on async task",           true,  "Deadlock risk under ThreadPool load \u2014 replace with await or Task.Run()"),
                ["MonitorContention"] = ("🔒", "Monitor Lock Contention", "Competing to enter a locked monitor (lock keyword)", true,  null),
                ["MonitorWait"]       = ("🔒", "Monitor Wait",            "Parked in Monitor.Wait() \u2014 awaiting a Pulse()",   false, null),
                ["Sleep"]             = ("😴", "Thread Sleep",            "Thread.Sleep() \u2014 deliberate timed pause",            false, null),
                ["Semaphore"]         = ("🚦", "Semaphore Wait",          "Waiting to acquire a semaphore permit",              false, null),
                ["Mutex"]             = ("🔐", "Mutex Wait",              "Waiting to acquire OS mutex ownership",              false, null),
                ["WaitHandle"]        = ("⌛", "WaitHandle / Event",      "Waiting on a manual/auto-reset event or WaitHandle", false, null),
                ["ThreadJoin"]        = ("🔗", "Thread.Join",             "Blocked waiting for another thread to finish",       false, null),
                ["BlockingIO"]        = ("📡", "Blocking I/O",            "Blocking synchronous network or file I/O call",      false, null),
            };

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

        private readonly OutputWriter _writer;

        public ThreadAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("THREAD ANALYSIS:");

            var threadInfo = CategorizeThreads(runtime.Threads);

            _writer.WriteLine($"\nTotal Threads: {threadInfo.TotalCount}");

            PrintThreadStatistics(threadInfo);
            PrintThreadGroups(threadInfo);
            PrintDistribution("THREAD STATE DISTRIBUTION:", threadInfo.StateDistribution);
            PrintDistribution("GC MODE DISTRIBUTION:", threadInfo.GcModeDistribution);
            PrintDistribution("APP DOMAIN DISTRIBUTION:", threadInfo.AppDomainDistribution);
            PrintDistribution("WAIT CATEGORY DISTRIBUTION:", threadInfo.WaitCategoryDistribution);
            PrintDistribution("ACTIVE EXCEPTION TYPES ON THREADS:", threadInfo.ExceptionTypeDistribution);
            PrintTopFrameHotspots(threadInfo.TopFrameHotspots);
            PrintAsyncIssues(threadInfo);
            PrintFinalizerDiagnostics(threadInfo);
            PrintThreadsWithLocks(threadInfo.ThreadsWithLocks);
            PrintBlockedThreads(threadInfo.PotentiallyBlockedThreads);
            PrintThreadsWithExceptions(threadInfo.ThreadsWithExceptions);

            _writer.WriteLine("\nNote: Deadlock detection requires full lock-graph analysis.");
            _writer.WriteLine("Use lock-heavy + blocked thread overlap and hotspot methods as triage anchors.");
            _writer.WriteLine(StringConstants.Equals80);

            return new AnalyzerOutput(
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

        private static string GetHotspotAnnotation(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return string.Empty;
            foreach (var pattern in WaitPatterns)
            {
                if (signature.Contains(pattern.Token, StringComparison.OrdinalIgnoreCase))
                    return $"  \u26a0\ufe0f  {pattern.Category}";
            }
            return string.Empty;
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

        private void PrintThreadGroups(ThreadCategorization info)
        {
            _writer.WriteLine("\n\nTHREAD GROUPS:");
            _writer.WriteSeparator();

            if (info.AliveCount == 0)
            {
                _writer.WriteLine("No alive managed threads found.");
                return;
            }

            int blockedCount = info.PotentiallyBlockedThreads.Count;
            int systemCount  = info.GcCount + info.FinalizerCount;
            int activeCount  = Math.Max(0, info.AliveCount - blockedCount);

            _writer.WriteLine($"Alive: {info.AliveCount}  |  Blocked/Waiting: {blockedCount}  |  Active: {activeCount}  |  GC/System: {systemCount}");

            // Pre-compute per-category most-common top frame and lock-holder count
            var categoryTopFrames = BuildCategoryTopFrames(info.PotentiallyBlockedThreads);
            var categoryLockHolders = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var bt in info.PotentiallyBlockedThreads)
            {
                if (bt.WaitCategory != null && bt.Thread.LockCount > 0)
                {
                    categoryLockHolders.TryGetValue(bt.WaitCategory, out int n);
                    categoryLockHolders[bt.WaitCategory] = n + 1;
                }
            }

            // ── Active Processing ──────────────────────────────────────────────
            double activePct = activeCount * 100.0 / info.AliveCount;
            _writer.WriteLine(string.Empty);
            _writer.WriteLine($"\u2699\ufe0f  Active Processing                    {activeCount,4} threads ({activePct:F1}%)");
            if (info.ActiveThreadHotspots.Count > 0)
            {
                _writer.WriteLine($"    Top frames:");
                foreach (var kvp in info.ActiveThreadHotspots
                    .OrderByDescending(x => x.Value)
                    .Take(TopActiveFramesToShow))
                {
                    string ann = GetHotspotAnnotation(kvp.Key);
                    _writer.WriteLine($"      {kvp.Value,3}  {FormatHelper.TruncateString(kvp.Key, MaxMethodLength - 8)}{ann}");
                }
            }

            // ── Blocked / Waiting groups, sorted by thread count descending ───
            foreach (var kvp in info.WaitCategoryDistribution
                .OrderByDescending(k => k.Value))
            {
                string category = kvp.Key;
                int    count    = kvp.Value;
                double pct      = count * 100.0 / info.AliveCount;

                _writer.WriteLine(string.Empty);

                if (!GroupMeta.TryGetValue(category, out var meta))
                {
                    _writer.WriteLine($"\u23f8\ufe0f  {category,-36} {count,4} threads ({pct:F1}%)");
                    continue;
                }

                string warn = meta.IsWarning ? "  \u26a0\ufe0f" : string.Empty;
                _writer.WriteLine($"{meta.Icon}  {meta.Label,-36}{warn}  {count,4} threads ({pct:F1}%)");
                _writer.WriteLine($"    Pattern: {meta.Pattern}");

                if (meta.RiskNote != null)
                    _writer.WriteLine($"    Risk:    {meta.RiskNote}");

                if (category == "TaskBlocking" && info.AsyncChainThreadCount > 0)
                    _writer.WriteLine($"    Async chains detected: {info.AsyncChainThreadCount} thread(s)  (max MoveNext depth: {info.MaxAsyncChainDepth})");

                if (categoryLockHolders.TryGetValue(category, out int lockHolders) && lockHolders > 0)
                    _writer.WriteLine($"    Threads also holding locks: {lockHolders}  \u26a0\ufe0f  cross-lock / escalation risk");

                if (categoryTopFrames.TryGetValue(category, out string? topFrame) && !string.IsNullOrEmpty(topFrame))
                    _writer.WriteLine($"    Top frame: {FormatHelper.TruncateString(topFrame, MaxMethodLength - 6)}");
            }

            // ── System thread groups ───────────────────────────────────────────
            if (info.ThreadPoolCount > 0)
            {
                _writer.WriteLine(string.Empty);
                _writer.WriteLine($"\ud83e\uddf5  ThreadPool Workers                   {info.ThreadPoolCount,4} threads (identified by flag or dispatch frame)");
            }

            if (info.GcCount > 0 || info.FinalizerCount > 0)
            {
                string finStatus = info.FinalizerThread != null
                    ? (info.FinalizerIsBlocked ? "BLOCKED \u26a0\ufe0f" : "Running \u2705")
                    : "not detected";

                _writer.WriteLine(string.Empty);
                _writer.WriteLine($"\u267b\ufe0f  GC / System Threads");
                if (info.GcCount > 0)       _writer.WriteLine($"    GC Threads:       {info.GcCount}");
                if (info.FinalizerCount > 0) _writer.WriteLine($"    Finalizer Thread: {finStatus}");
            }
        }

        private static Dictionary<string, string> BuildCategoryTopFrames(
            List<ThreadWithStackTrace> blockedThreads)
        {
            var frameCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var bt in blockedThreads)
            {
                if (bt.WaitCategory == null || bt.TopFrames.Count == 0) continue;
                string sig = GetFrameSignature(bt.TopFrames[0]);
                if (string.IsNullOrWhiteSpace(sig)) continue;

                if (!frameCounts.TryGetValue(bt.WaitCategory, out var catMap))
                {
                    catMap = new Dictionary<string, int>(StringComparer.Ordinal);
                    frameCounts[bt.WaitCategory] = catMap;
                }
                catMap.TryGetValue(sig, out int n);
                catMap[sig] = n + 1;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (category, catMap) in frameCounts)
            {
                string? best = null;
                int bestCount = 0;
                foreach (var (sig, n) in catMap)
                    if (n > bestCount) { bestCount = n; best = sig; }
                if (best != null)
                    result[category] = best;
            }
            return result;
        }

        private void PrintDistribution(string title, Dictionary<string, int> distribution)
        {
            _writer.WriteLine($"\n{title}");
            _writer.WriteSeparator();

            if (distribution.Count == 0)
            {
                _writer.WriteLine("No data.");
                return;
            }

            foreach (var kvp in distribution
                .OrderByDescending(k => k.Value)
                .ThenBy(k => k.Key, StringComparer.Ordinal)
                .Take(MaxDistributionRowsToDisplay))
            {
                _writer.WriteLine($"{kvp.Key}: {kvp.Value}");
            }
        }

        private void PrintTopFrameHotspots(Dictionary<string, int> hotspots)
        {
            _writer.WriteLine("\nTOP STACK HOTSPOTS (TOP FRAME):");
            _writer.WriteSeparator();

            if (hotspots.Count == 0)
            {
                _writer.WriteLine("No stack hotspot data available.");
                return;
            }

            foreach (var hotspot in hotspots
                .OrderByDescending(x => x.Value)
                .Take(MaxTopFrameHotspotsToDisplay))
            {
                string annotation = GetHotspotAnnotation(hotspot.Key);
                _writer.WriteLine($"{hotspot.Value,4}  {FormatHelper.TruncateString(hotspot.Key, MaxMethodLength)}{annotation}");
            }
        }

        private void PrintThreadStatistics(ThreadCategorization info)
        {
            _writer.WriteLine($"Alive Threads: {info.AliveCount}");
            _writer.WriteLine($"Inactive Threads: {info.TotalCount - info.AliveCount}");
            _writer.WriteLine($"GC Threads: {info.GcCount}");
            _writer.WriteLine($"Finalizer Threads: {info.FinalizerCount}");
            _writer.WriteLine($"Background Threads: {info.BackgroundCount}");
            _writer.WriteLine($"ThreadPool Worker Threads (alive): {info.ThreadPoolCount}");
            _writer.WriteLine($"Threads with active exceptions: {info.ThreadsWithActiveExceptionsCount}");
            _writer.WriteLine($"Threads with async chains (MoveNext): {info.AsyncChainThreadCount}  max depth: {info.MaxAsyncChainDepth}");
        }

        private void PrintAsyncIssues(ThreadCategorization info)
        {
            _writer.WriteLine("\n\nASYNC THREAD ISSUES:");
            _writer.WriteSeparator();

            info.WaitCategoryDistribution.TryGetValue("TaskBlocking", out int taskBlockingCount);
            int chainThreads = info.AsyncChainThreadCount;
            int maxDepth = info.MaxAsyncChainDepth;

            if (taskBlockingCount == 0 && chainThreads == 0)
            {
                _writer.WriteLine("No async/await issues detected.");
                return;
            }

            if (taskBlockingCount > 0)
                _writer.WriteLine($"Sync-over-async threads (.Wait() / .Result):  {taskBlockingCount}");

            if (chainThreads > 0)
            {
                _writer.WriteLine($"Threads with async state-machine chains:       {chainThreads}  (max MoveNext depth: {maxDepth})");
                if (maxDepth >= 5)
                    _writer.WriteLine($"  Deep chain depth ({maxDepth}) suggests a long async pipeline stalled on a single resource.");
            }

            if (taskBlockingCount > 0)
            {
                _writer.WriteLine(string.Empty);
                if (taskBlockingCount >= 10)
                {
                    _writer.WriteLine($"\u26a0\ufe0f  SYNC-OVER-ASYNC DEADLOCK RISK: {taskBlockingCount} threads synchronously blocking on async operations.");
                    _writer.WriteLine($"    Under ThreadPool load this exhausts available workers and causes full deadlock.");
                    _writer.WriteLine($"    Fix: replace .Wait() / .Result with await, or isolate with Task.Run().");
                }
                else
                {
                    _writer.WriteLine($"\u26a0\ufe0f  Sync-over-async detected ({taskBlockingCount} thread(s)) — replace .Wait()/.Result with await.");
                }
            }
        }

        private void PrintFinalizerDiagnostics(ThreadCategorization info)
        {
            _writer.WriteLine("\n\nFINALIZER THREAD:");
            _writer.WriteSeparator();

            if (info.FinalizerThread == null)
            {
                _writer.WriteLine("Finalizer thread not found in dump.");
                return;
            }

            var thread = info.FinalizerThread;
            string status = info.FinalizerIsBlocked ? "⚠️  BLOCKED" : "✅ Running";
            _writer.WriteLine($"Status:     {status}");
            _writer.WriteLine($"OS Thread:  {thread.OSThreadId}  Managed: {thread.ManagedThreadId}");
            _writer.WriteLine($"Lock Count: {thread.LockCount}");

            if (info.FinalizerFrames.Count > 0)
            {
                _writer.WriteLine("Stack:");
                foreach (var frame in info.FinalizerFrames)
                {
                    string method = GetFrameSignature(frame);
                    if (!string.IsNullOrWhiteSpace(method))
                        _writer.WriteLine($"  {FormatHelper.TruncateString(method, MaxMethodLength)}");
                }
            }

            if (info.FinalizerIsBlocked)
            {
                _writer.WriteLine("\n⚠️  A blocked finalizer thread prevents GC from draining the finalization queue.");
                _writer.WriteLine("    Objects pending finalization cannot be reclaimed, causing steady memory growth.");
                _writer.WriteLine("    Look for lock contention or blocking calls inside Dispose/finalizer methods above.");
            }
        }

        private void PrintThreadsWithLocks(List<ThreadWithStackTrace> threadsWithLocks)
        {
            if (threadsWithLocks.Count > 0)
            {
                _writer.WriteLine($"\nThreads Holding Locks: {threadsWithLocks.Count}");
                _writer.WriteLine("\nTHREADS WITH LOCKS:");
                _writer.WriteSeparator();

                int displayCount = Math.Min(MaxThreadsToDisplayPerSection, threadsWithLocks.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    var threadInfo = threadsWithLocks[i];
                    var thread = threadInfo.Thread;

                    _writer.WriteLine($"\nThread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                    _writer.WriteLine($"  Lock Count: {thread.LockCount}");
                    _writer.WriteLine($"  State: {thread.State} | GC Mode: {thread.GCMode}");
                    _writer.WriteLine($"  Stack Roots: {threadInfo.StackRootCount}");

                    if (!string.IsNullOrWhiteSpace(threadInfo.ExceptionType))
                    {
                        _writer.WriteLine($"  Active Exception: {threadInfo.ExceptionType}");
                    }

                    if (threadInfo.TopFrames.Count > 0)
                    {
                        _writer.WriteLine($"  Stack Trace (top {threadInfo.TopFrames.Count} frames):");
                        foreach (var frame in threadInfo.TopFrames)
                        {
                            string method = GetFrameSignature(frame);
                            _writer.WriteLine($"    {FormatHelper.TruncateString(method, MaxMethodLength)}");
                        }
                    }
                }

                if (threadsWithLocks.Count > MaxThreadsToDisplayPerSection)
                {
                    _writer.WriteLine($"\n... and {threadsWithLocks.Count - MaxThreadsToDisplayPerSection} more threads with locks");
                }
            }
            else
            {
                _writer.WriteLine("\nNo threads currently holding locks.");
            }
        }

        private void PrintBlockedThreads(List<ThreadWithStackTrace> blockedThreads)
        {
            _writer.WriteLine("\n\nPOTENTIALLY BLOCKED THREADS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Threads that appear to be waiting (based on stack patterns):\n");

            if (blockedThreads.Count == 0)
            {
                _writer.WriteLine("No obviously blocked threads detected (good!).");
            }
            else
            {
                int displayCount = Math.Min(MaxThreadsToDisplayPerSection, blockedThreads.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    var threadInfo = blockedThreads[i];
                    var thread = threadInfo.Thread;

                    _writer.WriteLine($"Thread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                    _writer.WriteLine($"  Category: {threadInfo.WaitCategory ?? "Unknown"}");
                    _writer.WriteLine($"  Reason: {threadInfo.WaitReason ?? "Unknown pattern"}");
                    _writer.WriteLine($"  State: {thread.State} | GC Mode: {thread.GCMode}");
                    _writer.WriteLine($"  Locks: {thread.LockCount}");
                    _writer.WriteLine($"  Stack Roots: {threadInfo.StackRootCount}");

                    if (!string.IsNullOrWhiteSpace(threadInfo.ExceptionType))
                    {
                        _writer.WriteLine($"  Active Exception: {threadInfo.ExceptionType}");
                    }

                    _writer.WriteLine($"  Top frames:");

                    foreach (var frame in threadInfo.TopFrames)
                    {
                        string method = GetFrameSignature(frame);
                        _writer.WriteLine($"    {FormatHelper.TruncateString(method, MaxMethodLength)}");
                    }
                    _writer.WriteLine(string.Empty);
                }

                if (blockedThreads.Count > MaxThreadsToDisplayPerSection)
                {
                    _writer.WriteLine($"... and {blockedThreads.Count - MaxThreadsToDisplayPerSection} more potentially blocked threads");
                }
            }
        }

        private void PrintThreadsWithExceptions(List<ThreadWithStackTrace> threadsWithExceptions)
        {
            _writer.WriteLine("\n\nTHREADS WITH ACTIVE EXCEPTIONS:");
            _writer.WriteSeparator();

            if (threadsWithExceptions.Count == 0)
            {
                _writer.WriteLine("No active exceptions associated with alive threads.");
                return;
            }

            int displayCount = Math.Min(MaxThreadsToDisplayPerSection, threadsWithExceptions.Count);
            for (int i = 0; i < displayCount; i++)
            {
                var info = threadsWithExceptions[i];
                var thread = info.Thread;

                _writer.WriteLine($"Thread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                _writer.WriteLine($"  Exception: {info.ExceptionType ?? StringConstants.UnknownType}");

                if (!string.IsNullOrWhiteSpace(info.ExceptionMessage))
                {
                    _writer.WriteLine($"  Message: {FormatHelper.TruncateString(info.ExceptionMessage, MaxMethodLength)}");
                }

                _writer.WriteLine($"  Locks: {thread.LockCount} | State: {thread.State} | GC Mode: {thread.GCMode}");
                _writer.WriteLine($"  Stack Roots: {info.StackRootCount}");

                if (info.TopFrames.Count > 0)
                {
                    _writer.WriteLine("  Top frames:");
                    foreach (var frame in info.TopFrames)
                    {
                        _writer.WriteLine($"    {FormatHelper.TruncateString(GetFrameSignature(frame), MaxMethodLength)}");
                    }
                }

                _writer.WriteLine(string.Empty);
            }

            if (threadsWithExceptions.Count > MaxThreadsToDisplayPerSection)
            {
                _writer.WriteLine($"... and {threadsWithExceptions.Count - MaxThreadsToDisplayPerSection} more threads with active exceptions");
            }
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
