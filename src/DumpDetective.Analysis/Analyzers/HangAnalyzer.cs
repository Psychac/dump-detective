using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
public class HangAnalyzer : IAnalyzer
    {
        private const int LongWaitThreshold = 5; // threads waiting
        private const int HighThreadPoolThreshold = 100;
        private const int MaxTasksToScan = 50000;
        private const int TopWaitingThreadsPerGroup = 5;
        private const int TopContinuationTypesToShow = 5;

        public string Name => "Hang Analysis";
        public string Category => "Hang";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return Analyze(runtime, heap, cache: null, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress)
        {
            var hangInfo = AnalyzeForHang(runtime, heap, cache, progress);

            var waitCategoryBreakdown = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var wt in hangInfo.WaitingThreads)
            {
                string category = wt.WaitType.ToString();
                waitCategoryBreakdown.TryGetValue(category, out int count);
                waitCategoryBreakdown[category] = count + 1;
            }

            double waitingPct = hangInfo.TotalAliveThreads == 0 ? 0
                : hangInfo.WaitingThreads.Count * 100.0 / hangInfo.TotalAliveThreads;

            return new HangDomainResult(
                    hangInfo.TotalAliveThreads,
                    hangInfo.WaitingThreads.Count,
                    hangInfo.ThreadsHoldingLocks,
                    waitingPct,
                    waitCategoryBreakdown,
                    hangInfo.TotalContinuations,
                    hangInfo.ThreadPoolInfo.QueuedWorkItems,
                    hangInfo.ThreadPoolInfo.TotalTasks,
                    hangInfo.ThreadPoolInfo.PendingTasks,
                    hangInfo.ThreadPoolInfo.FaultedTasks,
                    hangInfo.ThreadPoolInfo.CanceledTasks,
                    hangInfo.ThreadPoolInfo.RuntimeInitialized,
                    hangInfo.ThreadPoolInfo.TaskScanLimited,
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
                        .ToList());
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

        private HangAnalysis AnalyzeForHang(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress)
        {
            var analysis = new HangAnalysis();
            var waitingThreads = new List<WaitingThreadInfo>();
            var threadScanCounter = new ObjectScanCounter("scanning threads for hang", progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

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
            progress?.Report(new(threadScanCounter.Scanned, "analyzing async work items"));
            AnalyzeAsyncWork(heap, cache, analysis);

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
        /// so the score degrades predictably â€” not arbitrarily.
        /// </summary>
        private static int ComputeHealthScore(HangAnalysis analysis)
        {
            int score = 100;

            // â”€â”€ Waiting-thread pressure (up to -40) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            double waitingPct = analysis.TotalAliveThreads == 0 ? 0
                : analysis.WaitingThreads.Count * 100.0 / analysis.TotalAliveThreads;

            if (waitingPct >= 80)      score -= 40;
            else if (waitingPct >= 50) score -= 25;
            else if (waitingPct >= 30) score -= 10;

            // â”€â”€ Probable deadlock candidates: monitor-wait + holding a lock (up to -30) â”€â”€
            int circularCandidates = 0;
            foreach (var t in analysis.WaitingThreads)
            {
                if (t.WaitType == WaitType.MonitorWait && t.LockCount > 0)
                    circularCandidates++;
            }
            score -= Math.Min(circularCandidates * 15, 30);

            // â”€â”€ Thread pool health (up to -15) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var tp = analysis.ThreadPoolInfo;
            bool saturated  = tp.RuntimeMaxThreads > 0 && tp.RuntimeActiveWorkerThreads >= tp.RuntimeMaxThreads;
            bool starvation = saturated && tp.RuntimeCpuUtilization < 20;
            if (starvation)     score -= 15;
            else if (saturated) score -= 10;

            // â”€â”€ Task backpressure (-5) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if (tp.PendingTasks > HighThreadPoolThreshold) score -= 5;

            // â”€â”€ Unobserved task faults (-5) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if (tp.FaultedTasks > 0) score -= 5;

            // â”€â”€ Async continuation backlog (-5) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

        private void AnalyzeAsyncWork(ClrHeap heap, IHeapAnalysisCache? cache, HangAnalysis analysis)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var heapIdx))
            {
                // In-memory index: parallel over the flat entry array
                if (heapIdx.StorageKind == HeapIndexStorageKind.Memory && heapIdx.InMemoryEntries is { } entries)
                {
                    RunParallelAsyncScan(heap, inMemoryEntries: entries, analysis);
                    return;
                }

                // Disk-backed index: sequential (I/O bound)
                RunSequentialAsyncScan(heap, heapCache, analysis);
                return;
            }

            // No cache: parallel over GC segments
            RunParallelAsyncScan(heap, inMemoryEntries: null, analysis);
        }

        // Unified parallel async-work scanner — drives either a flat in-memory HeapEntry[]
        // or a per-segment ClrObject walk.  The early scan-limit is honored via a volatile flag.
        private void RunParallelAsyncScan(ClrHeap heap, HeapEntry[]? inMemoryEntries, HangAnalysis analysis)
        {
            var profileByMethodTable = new ConcurrentDictionary<ulong, AsyncTypeProfile>(
                concurrencyLevel: Environment.ProcessorCount, capacity: 64);
            var taskContinuations = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

            int queuedWorkItems = 0, totalTasks = 0, pendingTasks = 0, faultedTasks = 0, canceledTasks = 0;
            int totalContinuations = 0, tasksScanned = 0;
            bool taskScanLimited = false;

            void ProcessEntry(ulong address, ulong mt)
            {
                if (address == 0 || mt == 0 || Volatile.Read(ref taskScanLimited))
                    return;

                var entry = new HeapEntry(address, mt, 0);
                AsyncTypeProfile profile = profileByMethodTable.GetOrAdd(mt, _ =>
                {
                    ClrObject o = heap.GetObject(address);
                    return (!o.IsValid || o.Type == null)
                        ? AsyncTypeProfile.None
                        : AsyncTypeProfile.FromTypeName(o.Type.Name ?? string.Empty);
                });

                if (!profile.IsPotentiallyRelevant)
                    return;

                ClrObject obj = heap.GetObject(address);
                if (!obj.IsValid || obj.Type == null)
                    return;

                if (profile.IsQueuedWorkItem)
                    Interlocked.Increment(ref queuedWorkItems);

                if (profile.IsTask)
                {
                    int scanned = Interlocked.Increment(ref tasksScanned);
                    Interlocked.Increment(ref totalTasks);

                    if (scanned <= MaxTasksToScan)
                    {
                        var stateField = obj.Type.GetFieldByName("m_stateFlags");
                        if (stateField != null)
                        {
                            int stateFlags = stateField.Read<int>(obj, interior: false);
                            bool isCompleted = (stateFlags & 0x1000000) != 0;
                            bool isFaulted   = (stateFlags & 0x200000) != 0;
                            bool isCanceled  = (stateFlags & 0x400000) != 0;

                            if (isFaulted)        Interlocked.Increment(ref faultedTasks);
                            else if (isCanceled)  Interlocked.Increment(ref canceledTasks);
                            else if (!isCompleted) Interlocked.Increment(ref pendingTasks);
                        }
                    }

                    // Honor scan limit: signal remaining threads to skip task processing
                    if (scanned > MaxTasksToScan && Volatile.Read(ref queuedWorkItems) > 1000)
                        Volatile.Write(ref taskScanLimited, true);
                }

                if (profile.IsContinuation)
                {
                    Interlocked.Increment(ref totalContinuations);
                    taskContinuations.AddOrUpdate(profile.TypeName, 1, (_, c) => c + 1);
                }
            }

            if (inMemoryEntries != null)
            {
                Parallel.ForEach(inMemoryEntries, entry =>
                {
                    if (entry.Address == 0 || entry.MethodTable == 0)
                        return;
                    ProcessEntry(entry.Address, entry.MethodTable);
                });
            }
            else
            {
                Parallel.ForEach(heap.Segments, segment =>
                {
                    foreach (ClrObject obj in segment.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type is null)
                            continue;
                        ulong mt = obj.Type.MethodTable;
                        if (mt == 0)
                            continue;
                        ProcessEntry(obj.Address, mt);
                    }
                });
            }

            analysis.ThreadPoolInfo = new ThreadPoolAnalysis
            {
                QueuedWorkItems  = queuedWorkItems,
                TotalTasks       = totalTasks,
                PendingTasks     = pendingTasks,
                FaultedTasks     = faultedTasks,
                CanceledTasks    = canceledTasks,
                TaskScanLimited  = taskScanLimited
            };
            analysis.TaskContinuations = new Dictionary<string, int>(taskContinuations, StringComparer.Ordinal);
            analysis.TotalContinuations = totalContinuations;
        }

        private void RunSequentialAsyncScan(ClrHeap heap, HeapAnalysisCache heapCache, HangAnalysis analysis)
        {
            var threadPool = new ThreadPoolAnalysis();
            var taskContinuations = new Dictionary<string, int>();
            var profileByMethodTable = new Dictionary<ulong, AsyncTypeProfile>(capacity: 64);
            int tasksScanned = 0;
            int totalContinuations = 0;
            var objectScanCounter = new ObjectScanCounter("Hang async object scan");

            foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
            {
                objectScanCounter.Tick();

                ulong objectAddress = entry.Address;
                if (objectAddress == 0)
                    continue;

                AsyncTypeProfile profile = ResolveAsyncTypeProfile(heap, entry, profileByMethodTable);
                if (!profile.IsPotentiallyRelevant)
                    continue;

                AnalyzeHeapObjectByAddress(
                    heap,
                    objectAddress,
                    profile,
                    threadPool,
                    taskContinuations,
                    ref tasksScanned,
                    ref totalContinuations);

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

        private static AsyncTypeProfile ResolveAsyncTypeProfile(ClrHeap heap, in HeapEntry entry, Dictionary<ulong, AsyncTypeProfile> profileByMethodTable)
        {
            if (entry.MethodTable == 0)
                return AsyncTypeProfile.None;

            if (profileByMethodTable.TryGetValue(entry.MethodTable, out AsyncTypeProfile existing))
                return existing;

            ClrObject obj = heap.GetObject(entry.Address);
            if (!obj.IsValid || obj.Type == null)
            {
                profileByMethodTable[entry.MethodTable] = AsyncTypeProfile.None;
                return AsyncTypeProfile.None;
            }

            string typeName = obj.Type.Name ?? string.Empty;
            AsyncTypeProfile profile = AsyncTypeProfile.FromTypeName(typeName);
            profileByMethodTable[entry.MethodTable] = profile;
            return profile;
        }

        private static void AnalyzeHeapObjectByAddress(
            ClrHeap heap,
            ulong objectAddress,
            AsyncTypeProfile profile,
            ThreadPoolAnalysis threadPool,
            Dictionary<string, int> taskContinuations,
            ref int tasksScanned,
            ref int totalContinuations)
        {
            ClrObject obj = heap.GetObject(objectAddress);
            if (!obj.IsValid || obj.Type == null)
                return;

            if (profile.IsQueuedWorkItem)
            {
                threadPool.QueuedWorkItems++;
            }

            if (profile.IsTask)
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

            if (profile.IsContinuation)
            {
                totalContinuations++;
                string typeName = profile.TypeName;
                taskContinuations.TryGetValue(typeName, out int count);
                taskContinuations[typeName] = count + 1;
            }
        }

        private readonly record struct AsyncTypeProfile(string TypeName, bool IsTask, bool IsQueuedWorkItem, bool IsContinuation)
        {
            public static AsyncTypeProfile None => new(string.Empty, false, false, false);

            public bool IsPotentiallyRelevant => IsTask || IsQueuedWorkItem || IsContinuation;

            public static AsyncTypeProfile FromTypeName(string typeName)
            {
                bool isQueuedWorkItem = typeName.Contains("QueueUserWorkItemCallback", StringComparison.Ordinal)
                    || typeName.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal);
                bool isTask = typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal);
                bool isContinuation = typeName.Contains("ContinuationTask", StringComparison.Ordinal)
                    || typeName.Contains("AwaitTaskContinuation", StringComparison.Ordinal);

                return new AsyncTypeProfile(typeName, isTask, isQueuedWorkItem, isContinuation);
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


