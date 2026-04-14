using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class HangAnalyzer
    {
        private readonly OutputWriter _writer;
        private const int LongWaitThreshold = 5; // threads waiting
        private const int HighThreadPoolThreshold = 100;
        private const int MaxTasksToScan = 50000;
        private const int TopWaitingThreadsPerGroup = 5;
        private const int TopContinuationTypesToShow = 5;

        public HangAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            _writer.WriteHeader("HANG ANALYSIS:");

            var hangInfo = AnalyzeForHang(runtime, heap);

            PrintHangSummary(hangInfo);
            PrintHealthScore(hangInfo);
            PrintWaitingThreads(hangInfo.WaitingThreads);
            PrintThreadPoolInfo(hangInfo);
            PrintTaskInfo(hangInfo);
            PrintDeadlockSuspicion(hangInfo);

            var waitCategoryBreakdown = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var wt in hangInfo.WaitingThreads)
            {
                string category = wt.WaitType.ToString();
                waitCategoryBreakdown.TryGetValue(category, out int count);
                waitCategoryBreakdown[category] = count + 1;
            }

            double waitingPct = hangInfo.TotalAliveThreads == 0 ? 0
                : hangInfo.WaitingThreads.Count * 100.0 / hangInfo.TotalAliveThreads;

            _writer.WriteLine(StringConstants.Equals80);
            return new AnalyzerOutput(
                [CreateFinding(hangInfo)],
                new HangDomainResult(
                    hangInfo.TotalAliveThreads,
                    hangInfo.WaitingThreads.Count,
                    waitingPct,
                    waitCategoryBreakdown,
                    hangInfo.ThreadPoolInfo.QueuedWorkItems,
                    hangInfo.ThreadPoolInfo.PendingTasks,
                    hangInfo.ThreadPoolInfo.FaultedTasks,
                    hangInfo.ThreadPoolInfo.CanceledTasks,
                    hangInfo.HealthScore));
        }

        private static InsightFinding CreateFinding(HangAnalysis analysis)
        {
            double waitingPct = analysis.TotalAliveThreads == 0
                ? 0
                : analysis.WaitingThreads.Count * 100.0 / analysis.TotalAliveThreads;

            FindingSeverity severity = waitingPct >= 80
                ? FindingSeverity.Critical
                : waitingPct >= 50 || analysis.ThreadPoolInfo.QueuedWorkItems > HighThreadPoolThreshold
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(HangAnalyzer),
                Category: "Hang",
                Severity: severity,
                Title: "Hang-risk assessment",
                Evidence: $"Waiting threads: {analysis.WaitingThreads.Count:N0}/{analysis.TotalAliveThreads:N0} ({waitingPct:F1}%); queued work items: {analysis.ThreadPoolInfo.QueuedWorkItems:N0}; health score: {analysis.HealthScore}/100.",
                Recommendation: severity == FindingSeverity.Critical
                    ? "Investigate wait groups and lock owners immediately for deadlock/contention storms."
                    : "Review waiting-thread categories and thread-pool saturation indicators.",
                Tags: ["hang", "deadlock", "threadpool", "waits"],
                MetricValue: waitingPct,
                MetricUnit: "% waiting threads");
        }

        private HangAnalysis AnalyzeForHang(ClrRuntime runtime, ClrHeap heap)
        {
            var analysis = new HangAnalysis();
            var waitingThreads = new List<WaitingThreadInfo>();
            var threadScanCounter = new ObjectScanCounter("Hang thread scan", reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (var thread in runtime.Threads)
            {
                threadScanCounter.Tick();

                if (!thread.IsAlive)
                    continue;

                analysis.TotalAliveThreads++;

                // Analyze thread state (top frame only to reduce allocations)
                ClrStackFrame? topFrame = null;
                foreach (var frame in thread.EnumerateStackTrace())
                {
                    topFrame = frame;
                    break;
                }

                if (topFrame == null)
                    continue;

                var waitInfo = DetectWaitPattern(thread, topFrame);
                if (waitInfo != null)
                {
                    waitingThreads.Add(waitInfo);
                }

                // Check for lock ownership
                if (thread.LockCount > 0)
                {
                    analysis.ThreadsHoldingLocks++;
                }
            }

            threadScanCounter.Complete();

            analysis.WaitingThreads = waitingThreads;
            ReadRuntimeThreadPool(runtime, analysis);
            AnalyzeAsyncWork(heap, analysis);

            analysis.HealthScore = ComputeHealthScore(analysis);

            return analysis;
        }

        private static void ReadRuntimeThreadPool(ClrRuntime runtime, HangAnalysis analysis)
        {
            ClrThreadPool? tp = runtime.ThreadPool;
            if (tp == null)
                return;

            var info = analysis.ThreadPoolInfo;
            info.RuntimeInitialized = true;
            info.RuntimeMinThreads = tp.MinThreads;
            info.RuntimeMaxThreads = tp.MaxThreads;
            info.RuntimeActiveWorkerThreads = tp.ActiveWorkerThreads;
            info.RuntimeIdleWorkerThreads = tp.IdleWorkerThreads;
            info.RuntimeRetiredWorkerThreads = tp.RetiredWorkerThreads;
            info.RuntimeCpuUtilization = tp.CpuUtilization;
            info.UsingPortableThreadPool = tp.UsingPortableThreadPool;
            info.UsingWindowsThreadPool = tp.UsingWindowsThreadPool;
        }

        /// <summary>
        /// Produces a 0-100 composite score. Each penalty maps to a named, observable signal
        /// so the score degrades predictably — not arbitrarily.
        /// </summary>
        private static int ComputeHealthScore(HangAnalysis analysis)
        {
            int score = 100;

            // ── Waiting-thread pressure (up to -40) ──────────────────────────────────
            double waitingPct = analysis.TotalAliveThreads == 0 ? 0
                : analysis.WaitingThreads.Count * 100.0 / analysis.TotalAliveThreads;

            if (waitingPct >= 80)      score -= 40;
            else if (waitingPct >= 50) score -= 25;
            else if (waitingPct >= 30) score -= 10;

            // ── Probable deadlock candidates: monitor-wait + holding a lock (up to -30) ──
            int circularCandidates = 0;
            foreach (var t in analysis.WaitingThreads)
            {
                if (t.WaitType == WaitType.MonitorWait && t.LockCount > 0)
                    circularCandidates++;
            }
            score -= Math.Min(circularCandidates * 15, 30);

            // ── Thread pool health (up to -15) ───────────────────────────────────────
            var tp = analysis.ThreadPoolInfo;
            bool saturated  = tp.RuntimeMaxThreads > 0 && tp.RuntimeActiveWorkerThreads >= tp.RuntimeMaxThreads;
            bool starvation = saturated && tp.RuntimeCpuUtilization < 20;
            if (starvation)     score -= 15;
            else if (saturated) score -= 10;

            // ── Task backpressure (-5) ────────────────────────────────────────────────
            if (tp.PendingTasks > HighThreadPoolThreshold) score -= 5;

            // ── Unobserved task faults (-5) ───────────────────────────────────────────
            if (tp.FaultedTasks > 0) score -= 5;

            // ── Async continuation backlog (-5) ───────────────────────────────────────
            if (analysis.TotalContinuations > 1000) score -= 5;

            return Math.Max(0, Math.Min(100, score));
        }

        private void PrintHealthScore(HangAnalysis analysis)
        {
            int score = analysis.HealthScore;
            string icon  = score >= 80 ? "🟢" : score >= 60 ? "🟡" : score >= 40 ? "🟠" : "🔴";
            string label = score >= 80 ? "Healthy" : score >= 60 ? "Degraded" : score >= 40 ? "Stressed" : "Critical";
            _writer.WriteLine($"\nThread Health Score: {score}/100  {icon} {label}");
        }

        private WaitingThreadInfo? DetectWaitPattern(ClrThread thread, ClrStackFrame topFrame)
        {
            if (topFrame.Method == null)
                return null;

            string topMethod = topFrame.Method.Signature?.ToLowerInvariant() ?? "";
            
            WaitType? waitType = null;
            string? waitReason = null;

            if (topMethod.Contains("monitor.wait") || topMethod.Contains("monitor.enter"))
            {
                waitType = WaitType.MonitorWait;
                waitReason = "Waiting to acquire monitor lock";
            }
            else if (topMethod.Contains("task.wait") || topMethod.Contains("task.result"))
            {
                waitType = WaitType.TaskWait;
                waitReason = "Waiting for async task to complete (potential deadlock)";
            }
            else if (topMethod.Contains("thread.sleep"))
            {
                waitType = WaitType.Sleep;
                waitReason = "Thread sleeping";
            }
            else if (topMethod.Contains("semaphore"))
            {
                waitType = WaitType.SemaphoreWait;
                waitReason = "Waiting on semaphore";
            }
            else if (topMethod.Contains("waithandle") || topMethod.Contains("manualresetevent") || topMethod.Contains("autoresetevent"))
            {
                waitType = WaitType.EventWait;
                waitReason = "Waiting on synchronization event";
            }
            else if (topMethod.Contains("socket") && (topMethod.Contains("receive") || topMethod.Contains("accept")))
            {
                waitType = WaitType.IOWait;
                waitReason = "Waiting on I/O operation";
            }
            else if (topMethod.Contains("thread.join"))
            {
                waitType = WaitType.ThreadJoin;
                waitReason = "Waiting for another thread to complete";
            }

            if (waitType.HasValue)
            {
                return new WaitingThreadInfo
                {
                    ThreadId = (uint)thread.ManagedThreadId,
                    OSThreadId = thread.OSThreadId,
                    WaitType = waitType.Value,
                    WaitReason = waitReason ?? "Unknown wait",
                    LockCount = (int)thread.LockCount,
                    TopStackFrame = topFrame.Method?.Signature ?? topFrame.ToString() ?? "Unknown"
                };
            }

            return null;
        }

        private void AnalyzeAsyncWork(ClrHeap heap, HangAnalysis analysis)
        {
            var threadPool = new ThreadPoolAnalysis();
            var taskContinuations = new Dictionary<string, int>();
            int tasksScanned = 0;
            int totalContinuations = 0;
            var objectScanCounter = new ObjectScanCounter("Hang async object scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                objectScanCounter.Tick();

                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "";

                // Count queued work items
                if (typeName.Contains("QueueUserWorkItemCallback", StringComparison.Ordinal) ||
                    typeName.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal))
                {
                    threadPool.QueuedWorkItems++;
                }

                // Count tasks
                if (typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
                {
                    tasksScanned++;
                    threadPool.TotalTasks++;

                    if (tasksScanned <= MaxTasksToScan)
                    {
                        var stateField = obj.Type.GetFieldByName("m_stateFlags");
                        if (stateField != null)
                        {
                            int stateFlags = stateField.Read<int>(obj, interior: false);
                            bool isCompleted = (stateFlags & 0x1000000) != 0;
                            bool isFaulted = (stateFlags & 0x200000) != 0;
                            bool isCanceled = (stateFlags & 0x400000) != 0;

                            if (isFaulted)
                                threadPool.FaultedTasks++;
                            else if (isCanceled)
                                threadPool.CanceledTasks++;
                            else if (!isCompleted)
                                threadPool.PendingTasks++;
                        }
                    }
                }

                if (typeName.Contains("ContinuationTask", StringComparison.Ordinal) ||
                    typeName.Contains("AwaitTaskContinuation", StringComparison.Ordinal))
                {
                    totalContinuations++;
                    taskContinuations.TryGetValue(typeName, out int count);
                    taskContinuations[typeName] = count + 1;
                }

                if (tasksScanned > MaxTasksToScan && threadPool.QueuedWorkItems > 1000)
                {
                    threadPool.TaskScanLimited = true;
                    break;
                }
            }

            objectScanCounter.Complete();

            analysis.ThreadPoolInfo = threadPool;
            analysis.TaskContinuations = taskContinuations;
            analysis.TotalContinuations = totalContinuations;
        }

        private void PrintHangSummary(HangAnalysis analysis)
        {
            _writer.WriteLine("HANG INDICATORS:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Alive Threads: {analysis.TotalAliveThreads}");
            _writer.WriteLine($"Waiting/Blocked Threads: {analysis.WaitingThreads.Count}");
            _writer.WriteLine($"Threads Holding Locks: {analysis.ThreadsHoldingLocks}");

            if (analysis.TotalAliveThreads == 0)
            {
                _writer.WriteLine("No alive managed threads found.");
                return;
            }

            double waitingPercentage = (analysis.WaitingThreads.Count / (double)analysis.TotalAliveThreads) * 100;

            if (waitingPercentage > 80)
            {
                _writer.WriteLine($"\n⚠️  SEVERE HANG: {waitingPercentage:F1}% of threads are waiting!");
                _writer.WriteLine($"    This indicates a likely application hang or deadlock.");
            }
            else if (waitingPercentage > 50)
            {
                _writer.WriteLine($"\n⚠️  POSSIBLE HANG: {waitingPercentage:F1}% of threads are waiting.");
                _writer.WriteLine($"    Application may be experiencing performance issues.");
            }
            else
            {
                _writer.WriteLine($"\nWaiting Thread Percentage: {waitingPercentage:F1}%");
            }
        }

        private void PrintWaitingThreads(List<WaitingThreadInfo> waitingThreads)
        {
            if (waitingThreads.Count == 0)
            {
                _writer.WriteLine("\nNo waiting threads detected.");
                return;
            }

            _writer.WriteLine($"\n\nWAITING THREADS BREAKDOWN:");
            _writer.WriteSeparator();

            // Manual grouping - no LINQ allocations
            var byWaitType = new Dictionary<WaitType, List<WaitingThreadInfo>>();
            foreach (var thread in waitingThreads)
            {
                if (!byWaitType.TryGetValue(thread.WaitType, out var list))
                {
                    list = new List<WaitingThreadInfo>();
                    byWaitType[thread.WaitType] = list;
                }
                list.Add(thread);
            }

            // Manual sorting by count
            var sortedGroups = new List<KeyValuePair<WaitType, List<WaitingThreadInfo>>>(byWaitType);
            sortedGroups.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

            foreach (var group in sortedGroups)
            {
                _writer.WriteLine($"\n{group.Key} ({group.Value.Count} thread(s)):");

                int threadCount = 0;
                foreach (var waitThread in group.Value)
                {
                    if (threadCount >= TopWaitingThreadsPerGroup) break;
                    _writer.WriteLine($"  Thread {waitThread.ThreadId} (OS: {waitThread.OSThreadId})");
                    _writer.WriteLine($"    Reason: {waitThread.WaitReason}");
                    _writer.WriteLine($"    Locks Held: {waitThread.LockCount}");

                    _writer.WriteLine($"    Top Stack Frame:");
                    _writer.WriteLine($"      {FormatHelper.TruncateString(waitThread.TopStackFrame, 65)}");
                    threadCount++;
                }

                if (group.Value.Count > TopWaitingThreadsPerGroup)
                {
                    _writer.WriteLine($"  ... and {group.Value.Count - TopWaitingThreadsPerGroup} more");
                }
            }
        }

        private void PrintThreadPoolInfo(HangAnalysis analysis)
        {
            var tpInfo = analysis.ThreadPoolInfo;

            _writer.WriteLine($"\n\nTHREAD POOL STATUS:");
            _writer.WriteSeparator();

            // Runtime counters (from ClrThreadPool — most reliable source)
            if (tpInfo.RuntimeInitialized)
            {
                string impl = tpInfo.UsingPortableThreadPool ? "Portable (.NET)" :
                              tpInfo.UsingWindowsThreadPool ? "Windows OS" : "Native CLR";
                _writer.WriteLine($"Implementation: {impl}");
                _writer.WriteLine($"Worker Threads: {tpInfo.RuntimeActiveWorkerThreads} active / {tpInfo.RuntimeIdleWorkerThreads} idle / {tpInfo.RuntimeMaxThreads} max (min {tpInfo.RuntimeMinThreads})");
                _writer.WriteLine($"Retired Workers: {tpInfo.RuntimeRetiredWorkerThreads}");
                _writer.WriteLine($"CPU Utilization: {tpInfo.RuntimeCpuUtilization}%");

                bool workersSaturated = tpInfo.RuntimeMaxThreads > 0 &&
                                        tpInfo.RuntimeActiveWorkerThreads >= tpInfo.RuntimeMaxThreads;
                bool cpuIdle = tpInfo.RuntimeCpuUtilization < 20;
                if (workersSaturated && cpuIdle)
                {
                    _writer.WriteLine($"\n⚠️  THREAD STARVATION: all {tpInfo.RuntimeMaxThreads} worker threads active at low CPU ({tpInfo.RuntimeCpuUtilization}%).");
                    _writer.WriteLine($"    Threads are likely blocked (sync-over-async, I/O waits, or lock contention) — not CPU-bound.");
                }
                else if (workersSaturated)
                {
                    _writer.WriteLine($"\n⚠️  WORKER SATURATION: {tpInfo.RuntimeActiveWorkerThreads}/{tpInfo.RuntimeMaxThreads} workers active.");
                    _writer.WriteLine($"    New work items will queue. Increase ThreadPool.SetMinThreads or switch to async I/O.");
                }
            }
            else
            {
                _writer.WriteLine("Runtime ThreadPool data unavailable (dump may be from managed-only snapshot).");
            }

            // Heap-scan counters
            _writer.WriteLine($"\nQueued Work Items (heap scan): {tpInfo.QueuedWorkItems:N0}");
            _writer.WriteLine($"Total Tasks: {tpInfo.TotalTasks:N0}");
            _writer.WriteLine($"Pending Tasks: {tpInfo.PendingTasks:N0}");
            _writer.WriteLine($"Faulted Tasks: {tpInfo.FaultedTasks:N0}");
            _writer.WriteLine($"Canceled Tasks: {tpInfo.CanceledTasks:N0}");

            if (tpInfo.QueuedWorkItems > HighThreadPoolThreshold)
            {
                _writer.WriteLine($"\n⚠️  WARNING: {tpInfo.QueuedWorkItems} queued work items!");
                _writer.WriteLine($"    ThreadPool may be saturated - consider increasing threads or async patterns.");
            }

            if (tpInfo.PendingTasks > HighThreadPoolThreshold)
            {
                _writer.WriteLine($"\n⚠️  WARNING: {tpInfo.PendingTasks} pending tasks!");
                _writer.WriteLine($"    Many tasks waiting to execute - may indicate thread starvation.");
            }

            if (tpInfo.TaskScanLimited)
            {
                _writer.WriteLine($"\n📊 Note: Task scan limited to prevent memory issues (50,000 tasks analyzed).");
            }
        }

        private void PrintTaskInfo(HangAnalysis analysis)
        {
            _writer.WriteLine($"\n\nASYNC TASK ANALYSIS:");
            _writer.WriteSeparator();

            if (analysis.TotalContinuations > 0)
            {
                _writer.WriteLine($"Total Task Continuations: {analysis.TotalContinuations:N0}");
                
                if (analysis.TotalContinuations > 1000)
                {
                    _writer.WriteLine($"⚠️  HIGH: Many continuations may indicate async over-use or hangs.");
                }

                _writer.WriteLine($"\nContinuation Types:");
                var continuationTypes = new List<KeyValuePair<string, int>>(analysis.TaskContinuations);
                continuationTypes.Sort((a, b) => b.Value.CompareTo(a.Value));

                int shown = 0;
                foreach (var kvp in continuationTypes)
                {
                    if (shown >= TopContinuationTypesToShow)
                        break;

                    _writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0}");
                    shown++;
                }
            }
            else
            {
                _writer.WriteLine("No task continuations detected.");
            }
        }

        private void PrintDeadlockSuspicion(HangAnalysis analysis)
        {
            _writer.WriteLine($"\n\nDEADLOCK DETECTION:");
            _writer.WriteSeparator();

            // Cross-correlate: threads waiting on a monitor WHILE holding a lock
            // are the only true circular-wait candidates accessible without a full lock graph.
            var circularWaitCandidates = new List<WaitingThreadInfo>();
            int monitorWaitCount = 0;

            foreach (var waiting in analysis.WaitingThreads)
            {
                if (waiting.WaitType == WaitType.MonitorWait)
                {
                    monitorWaitCount++;
                    if (waiting.LockCount > 0)
                        circularWaitCandidates.Add(waiting);
                }
            }

            if (circularWaitCandidates.Count >= 2)
            {
                _writer.WriteLine($"⚠️  PROBABLE DEADLOCK: {circularWaitCandidates.Count} thread(s) waiting on a monitor while holding a lock.");
                _writer.WriteLine($"    Each of these threads holds a lock it won't release until it acquires another.");

                foreach (var t in circularWaitCandidates)
                    _writer.WriteLine($"    Thread {t.ThreadId} (OS: {t.OSThreadId}) — locks held: {t.LockCount}, frame: {FormatHelper.TruncateString(t.TopStackFrame, 65)}");

                _writer.WriteLine($"\n💡 INVESTIGATION STEPS:");
                _writer.WriteLine($"    1. Run !syncblk in WinDbg/SOS to map lock addresses to owner threads");
                _writer.WriteLine($"    2. Confirm these thread IDs form a wait cycle (A waits for B's lock, B waits for A's lock)");
                _writer.WriteLine($"    3. Review lock acquisition order in source to identify the inversion point");
            }
            else if (circularWaitCandidates.Count == 1)
            {
                var t = circularWaitCandidates[0];
                _writer.WriteLine($"⚠️  DEADLOCK CANDIDATE: Thread {t.ThreadId} (OS: {t.OSThreadId}) is waiting on a monitor while holding {t.LockCount} lock(s).");
                _writer.WriteLine($"    A second thread holding the contested lock and waiting on this one would complete the cycle.");
                _writer.WriteLine($"    Frame: {FormatHelper.TruncateString(t.TopStackFrame, 65)}");
            }
            else if (monitorWaitCount >= 2 && analysis.ThreadsHoldingLocks >= 2)
            {
                _writer.WriteLine($"⚠️  POSSIBLE DEADLOCK: {monitorWaitCount} thread(s) waiting on monitors; {analysis.ThreadsHoldingLocks} thread(s) holding locks.");
                _writer.WriteLine($"    No thread was observed doing both simultaneously in this snapshot (top-frame only scan).");
                _writer.WriteLine($"    Run !syncblk and cross-reference lock owners against the waiting thread list.");
            }
            else if (analysis.WaitingThreads.Count > analysis.TotalAliveThreads * 0.8)
            {
                _writer.WriteLine($"⚠️  APPLICATION APPEARS HUNG:");
                _writer.WriteLine($"    - {analysis.WaitingThreads.Count}/{analysis.TotalAliveThreads} threads are waiting");
                _writer.WriteLine($"    - Most threads blocked (hang or resource starvation)");
                _writer.WriteLine($"\n💡 COMMON CAUSES:");
                _writer.WriteLine($"    - Deadlock (check monitor waits)");
                _writer.WriteLine($"    - Thread pool starvation (check ThreadPool status above)");
                _writer.WriteLine($"    - Blocking I/O on UI/main thread");
                _writer.WriteLine($"    - Synchronous wait on async code (Task.Wait/Result)");
            }
            else if (analysis.WaitingThreads.Count >= LongWaitThreshold)
            {
                _writer.WriteLine($"⚠️  Elevated wait activity detected ({analysis.WaitingThreads.Count} waiting threads).");
                _writer.WriteLine("Review top waiting groups and thread pool pressure for early hang signals.");
            }
            else
            {
                _writer.WriteLine("No obvious deadlock patterns detected.");
                _writer.WriteLine("Application may be functioning normally or experiencing other issues.");
            }
        }
    }

    internal class HangAnalysis
    {
        public int TotalAliveThreads { get; set; }
        public int ThreadsHoldingLocks { get; set; }
        public int HealthScore { get; set; }
        public List<WaitingThreadInfo> WaitingThreads { get; set; } = new();
        public ThreadPoolAnalysis ThreadPoolInfo { get; set; } = new();
        public int TotalContinuations { get; set; }
        public Dictionary<string, int> TaskContinuations { get; set; } = new();
    }

    internal class WaitingThreadInfo
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public WaitType WaitType { get; set; }
        public string WaitReason { get; set; } = string.Empty;
        public int LockCount { get; set; }
        public string TopStackFrame { get; set; } = string.Empty;
    }

    internal class ThreadPoolAnalysis
    {
        // Heap-scan counters
        public int QueuedWorkItems { get; set; }
        public int TotalTasks { get; set; }
        public int PendingTasks { get; set; }
        public int FaultedTasks { get; set; }
        public int CanceledTasks { get; set; }
        public bool TaskScanLimited { get; set; }

        // Runtime-sourced counters (from ClrThreadPool)
        public bool RuntimeInitialized { get; set; }
        public int RuntimeMinThreads { get; set; }
        public int RuntimeMaxThreads { get; set; }
        public int RuntimeActiveWorkerThreads { get; set; }
        public int RuntimeIdleWorkerThreads { get; set; }
        public int RuntimeRetiredWorkerThreads { get; set; }
        public int RuntimeCpuUtilization { get; set; }
        public bool UsingPortableThreadPool { get; set; }
        public bool UsingWindowsThreadPool { get; set; }
    }

    internal enum WaitType
    {
        MonitorWait,
        TaskWait,
        Sleep,
        SemaphoreWait,
        EventWait,
        IOWait,
        ThreadJoin
    }
}
