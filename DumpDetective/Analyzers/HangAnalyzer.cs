using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class HangAnalyzer : IAnalyzer
    {
        private const int LongWaitThreshold = 5; // threads waiting
        private const int HighThreadPoolThreshold = 100;
        private const int MaxTasksToScan = 50000;
        private const int TopWaitingThreadsPerGroup = 5;
        private const int TopContinuationTypesToShow = 5;

        public string Name => "Hang Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Runtime, context.Heap);

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            var hangInfo = AnalyzeForHang(runtime, heap);

            var waitCategoryBreakdown = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var wt in hangInfo.WaitingThreads)
            {
                string category = wt.WaitType.ToString();
                waitCategoryBreakdown.TryGetValue(category, out int count);
                waitCategoryBreakdown[category] = count + 1;
            }

            double waitingPct = hangInfo.TotalAliveThreads == 0 ? 0
                : hangInfo.WaitingThreads.Count * 100.0 / hangInfo.TotalAliveThreads;

            return new AnalyzerExecutionResult(
                [CreateFinding(hangInfo)],
                new HangDomainResult(
                    hangInfo.TotalAliveThreads,
                    hangInfo.WaitingThreads.Count,
                    hangInfo.ThreadsHoldingLocks,
                    waitingPct,
                    waitCategoryBreakdown,
                    hangInfo.TotalContinuations,
                    hangInfo.ThreadPoolInfo.QueuedWorkItems,
                    hangInfo.ThreadPoolInfo.PendingTasks,
                    hangInfo.ThreadPoolInfo.FaultedTasks,
                    hangInfo.ThreadPoolInfo.CanceledTasks,
                    hangInfo.HealthScore,
                    hangInfo.WaitingThreads
                        .OrderByDescending(w => w.LockCount)
                        .ThenByDescending(w => w.WaitType)
                        .Take(10)
                        .Select(w => new WaitingThreadSnapshot(
                            w.ThreadId,
                            w.OSThreadId,
                            w.WaitType.ToString(),
                            w.WaitReason,
                            w.LockCount,
                            w.TopStackFrame))
                        .ToList(),
                    hangInfo.TaskContinuations
                        .OrderByDescending(k => k.Value)
                        .Take(TopContinuationTypesToShow)
                        .Select(k => new NameCountEntry(k.Key, k.Value))
                        .ToList()));
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
