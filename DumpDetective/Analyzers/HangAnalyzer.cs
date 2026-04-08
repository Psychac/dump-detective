using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class HangAnalyzer
    {
        private readonly OutputWriter _writer;
        private const int LongWaitThreshold = 5; // threads waiting
        private const int HighThreadPoolThreshold = 100;

        public HangAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            _writer.WriteHeader("HANG ANALYSIS:");
            _writer.WriteLine("Detecting potential application hangs...\n");

            var hangInfo = AnalyzeForHang(runtime, heap);

            PrintHangSummary(hangInfo);
            PrintWaitingThreads(hangInfo.WaitingThreads);
            PrintThreadPoolInfo(hangInfo);
            PrintTaskInfo(heap);
            PrintDeadlockSuspicion(hangInfo);

            _writer.WriteLine(StringConstants.Equals80);
        }

        private HangAnalysis AnalyzeForHang(ClrRuntime runtime, ClrHeap heap)
        {
            var analysis = new HangAnalysis();
            var waitingThreads = new List<WaitingThreadInfo>();

            foreach (var thread in runtime.Threads)
            {
                if (!thread.IsAlive)
                    continue;

                analysis.TotalAliveThreads++;

                // Analyze thread state
                var stackTrace = thread.EnumerateStackTrace().Take(10).ToList();
                if (stackTrace.Count == 0)
                    continue;

                var waitInfo = DetectWaitPattern(thread, stackTrace);
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

            analysis.WaitingThreads = waitingThreads;
            analysis.ThreadPoolInfo = AnalyzeThreadPool(heap);

            return analysis;
        }

        private WaitingThreadInfo? DetectWaitPattern(ClrThread thread, List<ClrStackFrame> stackTrace)
        {
            var topFrame = stackTrace.FirstOrDefault();
            if (topFrame.Method == null)
                return null;

            string topMethod = topFrame.Method.Signature?.ToLower() ?? "";
            
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
                    StackTrace = stackTrace
                };
            }

            return null;
        }

        private ThreadPoolAnalysis AnalyzeThreadPool(ClrHeap heap)
        {
            var analysis = new ThreadPoolAnalysis();

            // Limit memory usage - don't enumerate all tasks
            const int MaxTasksToScan = 50000;
            int tasksScanned = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "";

                // Count queued work items
                if (typeName.Contains("QueueUserWorkItemCallback", StringComparison.Ordinal) ||
                    typeName.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal))
                {
                    analysis.QueuedWorkItems++;
                }

                // Count tasks (limit to prevent memory issues)
                if (typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
                {
                    tasksScanned++;
                    analysis.TotalTasks++;

                    // Only analyze task state for first N tasks to save memory
                    if (tasksScanned <= MaxTasksToScan)
                    {
                        // Try to determine task state
                        var stateField = obj.Type.GetFieldByName("m_stateFlags");
                        if (stateField != null)
                        {
                            int stateFlags = stateField.Read<int>(obj, interior: false);

                            // Task state flags (from Task source code)
                            bool isCompleted = (stateFlags & 0x1000000) != 0;
                            bool isFaulted = (stateFlags & 0x200000) != 0;
                            bool isCanceled = (stateFlags & 0x400000) != 0;

                            if (isFaulted)
                                analysis.FaultedTasks++;
                            else if (isCanceled)
                                analysis.CanceledTasks++;
                            else if (!isCompleted)
                                analysis.PendingTasks++;
                        }
                    }
                }

                // Early exit if we've scanned enough
                if (tasksScanned > MaxTasksToScan && analysis.QueuedWorkItems > 1000)
                {
                    analysis.TaskScanLimited = true;
                    break;
                }
            }

            return analysis;
        }

        private void PrintHangSummary(HangAnalysis analysis)
        {
            _writer.WriteLine("HANG INDICATORS:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Alive Threads: {analysis.TotalAliveThreads}");
            _writer.WriteLine($"Waiting/Blocked Threads: {analysis.WaitingThreads.Count}");
            _writer.WriteLine($"Threads Holding Locks: {analysis.ThreadsHoldingLocks}");

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

            // Group by wait type
            var byWaitType = waitingThreads
                .GroupBy(w => w.WaitType)
                .OrderByDescending(g => g.Count());

            foreach (var group in byWaitType)
            {
                _writer.WriteLine($"\n{group.Key} ({group.Count()} thread(s)):");
                
                foreach (var waitThread in group.Take(5))
                {
                    _writer.WriteLine($"  Thread {waitThread.ThreadId} (OS: {waitThread.OSThreadId})");
                    _writer.WriteLine($"    Reason: {waitThread.WaitReason}");
                    _writer.WriteLine($"    Locks Held: {waitThread.LockCount}");
                    
                    if (waitThread.StackTrace.Count > 0)
                    {
                        _writer.WriteLine($"    Top Stack Frame:");
                        var topFrame = waitThread.StackTrace[0];
                        string method = topFrame.Method?.Signature ?? topFrame.ToString() ?? "Unknown";
                        _writer.WriteLine($"      {FormatHelper.TruncateString(method, 65)}");
                    }
                }

                if (group.Count() > 5)
                {
                    _writer.WriteLine($"  ... and {group.Count() - 5} more");
                }
            }
        }

        private void PrintThreadPoolInfo(HangAnalysis analysis)
        {
            var tpInfo = analysis.ThreadPoolInfo;

            _writer.WriteLine($"\n\nTHREAD POOL STATUS:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Queued Work Items: {tpInfo.QueuedWorkItems:N0}");
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

        private void PrintTaskInfo(ClrHeap heap)
        {
            _writer.WriteLine($"\n\nASYNC TASK ANALYSIS:");
            _writer.WriteSeparator();

            var taskContinuations = new Dictionary<string, int>();
            int totalContinuations = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                if (obj.Type.Name?.Contains("ContinuationTask", StringComparison.Ordinal) == true ||
                    obj.Type.Name?.Contains("AwaitTaskContinuation", StringComparison.Ordinal) == true)
                {
                    totalContinuations++;
                    taskContinuations.TryGetValue(obj.Type.Name, out int count);
                    taskContinuations[obj.Type.Name] = count + 1;
                }
            }

            if (totalContinuations > 0)
            {
                _writer.WriteLine($"Total Task Continuations: {totalContinuations:N0}");
                
                if (totalContinuations > 1000)
                {
                    _writer.WriteLine($"⚠️  HIGH: Many continuations may indicate async over-use or hangs.");
                }

                _writer.WriteLine($"\nContinuation Types:");
                foreach (var kvp in taskContinuations.OrderByDescending(x => x.Value).Take(5))
                {
                    _writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0}");
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

            var monitorWaits = analysis.WaitingThreads.Where(w => w.WaitType == WaitType.MonitorWait).ToList();
            var lockedThreads = analysis.WaitingThreads.Where(w => w.LockCount > 0).ToList();

            if (monitorWaits.Count >= 2 && analysis.ThreadsHoldingLocks >= 2)
            {
                _writer.WriteLine($"⚠️  POTENTIAL DEADLOCK DETECTED:");
                _writer.WriteLine($"    - {monitorWaits.Count} thread(s) waiting on monitors");
                _writer.WriteLine($"    - {analysis.ThreadsHoldingLocks} thread(s) holding locks");
                _writer.WriteLine($"    - Threads may be waiting on each other (circular dependency)");
                _writer.WriteLine($"\n💡 INVESTIGATION STEPS:");
                _writer.WriteLine($"    1. Check 'THREADS WITH LOCKS' section above");
                _writer.WriteLine($"    2. Look for threads waiting on monitors while holding locks");
                _writer.WriteLine($"    3. Use WinDbg command: !syncblk to see detailed lock information");
                _writer.WriteLine($"    4. Review code for lock acquisition order issues");
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
        public List<WaitingThreadInfo> WaitingThreads { get; set; } = new();
        public ThreadPoolAnalysis ThreadPoolInfo { get; set; } = new();
    }

    internal class WaitingThreadInfo
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public WaitType WaitType { get; set; }
        public string WaitReason { get; set; } = string.Empty;
        public int LockCount { get; set; }
        public List<ClrStackFrame> StackTrace { get; set; } = new();
    }

    internal class ThreadPoolAnalysis
    {
        public int QueuedWorkItems { get; set; }
        public int TotalTasks { get; set; }
        public int PendingTasks { get; set; }
        public int FaultedTasks { get; set; }
        public int CanceledTasks { get; set; }
        public bool TaskScanLimited { get; set; }
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
