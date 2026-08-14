using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

using System.Collections.Concurrent;

namespace DumpDetective.Analysis.Analyzers
{
    public class HangAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, IThreadStackScanParticipant
    {
        public string Name => "Hang Analysis";
        public string Category => "Hang";

        // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
        // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
        // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
        private ClrHeap? _heap;
        private HangAnalysisOptions? _options;
        private Dictionary<ulong, AsyncTypeProfile>? _profileByMethodTable;
        private Dictionary<string, int>? _taskContinuations;
        private ThreadPoolAnalysis? _threadPoolInfo;
        private int _tasksScanned;
        private int _totalContinuations;
        private ObjectScanCounter? _scanCounter;
        // Set by OnHeapIndexScanCompleted — the single source of truth for whether the
        // participant-accumulated state above is trustworthy. Avoids re-deriving "did the
        // shared scan run" from a second cache.TryGetHeapIndex call in AnalyzeAsyncWork.
        private bool _participantScanSucceeded;

        // Instance accumulator state for the IThreadStackScanParticipant path — only the top
        // stack frame is needed here, so GetRequiredFrameCount is pinned to 1 regardless of what
        // other quartet members (e.g. ThreadStackClusterAnalyzer) request; the dispatcher walks
        // max(all participants) frames but each participant only reads what it asked for.
        private int _threadScanTotalAliveThreads;
        private int _threadScanThreadsHoldingLocks;
        private List<WaitingThreadInfo>? _threadScanWaitingThreads;
        private ObjectScanCounter? _threadScanCounter;
        private bool _threadScanSucceeded;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HangAnalysisOptions options = context.AnalysisOptions.HangAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        // Resets per-entry accumulator fields ahead of the shared heap-index scan pass.
        public void BeforeHeapIndexScan(AnalysisContext context)
        {
            _options = context.AnalysisOptions.HangAnalysis;
            _heap = context.Heap;
            _profileByMethodTable = new Dictionary<ulong, AsyncTypeProfile>(capacity: 64);
            _taskContinuations = new Dictionary<string, int>();
            _threadPoolInfo = new ThreadPoolAnalysis();
            _tasksScanned = 0;
            _totalContinuations = 0;
            _scanCounter = new ObjectScanCounter("Hang async object scan");
        }

        // Ports RunSequentialAsyncScan's loop body onto the disk-backed heap-index scan driven
        // by HeapIndexScanDispatcher, so this analyzer shares one pass over the index instead of
        // enumerating it independently via the ClrMD API.
        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

        private void OnHeapEntry(in HeapEntry entry)
        {
            _scanCounter!.Tick();

            if (entry.Address == 0 || _threadPoolInfo!.TaskScanLimited)
                return;

            AsyncTypeProfile profile = ResolveAsyncTypeProfile(_heap!, entry, _profileByMethodTable!);
            if (!profile.IsPotentiallyRelevant)
                return;

            AnalyzeHeapObjectByAddress(
                _heap!,
                entry.Address,
                profile,
                _threadPoolInfo,
                _taskContinuations!,
                ref _tasksScanned,
                ref _totalContinuations,
                _options!.MaxTasksToScan);

            if (_tasksScanned > _options.MaxTasksToScan && _threadPoolInfo.QueuedWorkItems > 1000)
                _threadPoolInfo.TaskScanLimited = true;
        }

        public void OnHeapIndexScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

        IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new HangAnalyzer();

        // Merges heap-scan-accumulated counters from disjoint-range workers into this instance.
        // Thread-pool Runtime* fields (set by ReadRuntimeThreadPool in AnalyzeForHang) are not
        // populated on workers and do not need to be merged.
        void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
        {
            foreach (IHeapIndexScanParticipant p in partials)
            {
                var other = (HangAnalyzer)p;

                // Profile cache: merge missing keys — same MT always resolves to the same profile.
                if (other._profileByMethodTable != null)
                {
                    foreach (var kvp in other._profileByMethodTable)
                    {
                        if (!_profileByMethodTable!.ContainsKey(kvp.Key))
                            _profileByMethodTable[kvp.Key] = kvp.Value;
                    }
                }

                // Continuation-type counts: sum per type name.
                if (other._taskContinuations != null)
                {
                    foreach (var kvp in other._taskContinuations)
                    {
                        _taskContinuations!.TryGetValue(kvp.Key, out int count);
                        _taskContinuations[kvp.Key] = count + kvp.Value;
                    }
                }

                // ThreadPoolAnalysis heap-scan counters.
                ThreadPoolAnalysis tp = _threadPoolInfo!;
                ThreadPoolAnalysis otherTp = other._threadPoolInfo!;
                tp.QueuedWorkItems += otherTp.QueuedWorkItems;
                tp.TotalTasks += otherTp.TotalTasks;
                tp.PendingTasks += otherTp.PendingTasks;
                tp.FaultedTasks += otherTp.FaultedTasks;
                tp.CanceledTasks += otherTp.CanceledTasks;
                tp.TaskScanLimited |= otherTp.TaskScanLimited;

                _tasksScanned += other._tasksScanned;
                _totalContinuations += other._totalContinuations;
            }
        }

        // IThreadStackScanParticipant — shares ThreadStackScanDispatcher's single
        // EnumerateStackTrace() pass with ThreadAnalyzer/ThreadStackClusterAnalyzer/
        // LockGraphAnalyzer instead of independently walking runtime.Threads.
        public int GetRequiredFrameCount(AnalysisContext context) => 1;

        public void BeforeThreadStackScan(AnalysisContext context)
        {
            _threadScanTotalAliveThreads = 0;
            _threadScanThreadsHoldingLocks = 0;
            _threadScanWaitingThreads = new List<WaitingThreadInfo>();
            _threadScanCounter = new ObjectScanCounter("scanning threads for hang", context.Progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));
        }

        void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot) => OnThreadStack(in snapshot);

        private void OnThreadStack(in ThreadStackSnapshot snapshot)
        {
            _threadScanCounter!.Tick();

            ClrThread thread = snapshot.Thread;
            if (!thread.IsAlive)
                return;

            _threadScanTotalAliveThreads++;

            if (snapshot.TopFrames.Count == 0)
                return;

            var waitInfo = DetectWaitPattern(thread, snapshot.TopFrames[0]);
            if (waitInfo != null)
                _threadScanWaitingThreads!.Add(waitInfo);

            if (thread.LockCount > 0)
                _threadScanThreadsHoldingLocks++;
        }

        public void OnThreadStackScanCompleted(bool succeeded)
        {
            _threadScanSucceeded = succeeded;
            if (succeeded)
                _threadScanCounter?.Complete();
        }

        private void BuildAsyncAnalysisFromParticipantState(HangAnalysis analysis)
        {
            _scanCounter?.Complete();

            analysis.ThreadPoolInfo = _threadPoolInfo!;
            analysis.TaskContinuations = _taskContinuations!;
            analysis.TotalContinuations = _totalContinuations;
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return Analyze(runtime, heap, cache: null, new HangAnalysisOptions(), progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, HangAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var hangInfo = AnalyzeForHang(runtime, heap, cache, options, progress);

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
                    hangInfo.ThreadPoolInfo.RuntimeMinThreads,
                    hangInfo.ThreadPoolInfo.RuntimeMaxThreads,
                    hangInfo.ThreadPoolInfo.RuntimeActiveWorkerThreads,
                    hangInfo.ThreadPoolInfo.RuntimeIdleWorkerThreads,
                    hangInfo.ThreadPoolInfo.RuntimeRetiredWorkerThreads,
                    hangInfo.ThreadPoolInfo.RuntimeQueueLength,
                    hangInfo.ThreadPoolInfo.RuntimeCpuUtilization,
                    hangInfo.ThreadPoolInfo.RuntimeInitialized &&
                        hangInfo.ThreadPoolInfo.RuntimeMaxThreads > 0 &&
                        hangInfo.ThreadPoolInfo.RuntimeQueueLength.GetValueOrDefault() > 0 &&
                        hangInfo.ThreadPoolInfo.RuntimeActiveWorkerThreads >= hangInfo.ThreadPoolInfo.RuntimeMaxThreads,
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
                        .Take(options.TopContinuationTypesToShow)
                        .Select(k => new NameCountEntry(k.Key, k.Value))
                        .ToList());
        }

        private HangAnalysis AnalyzeForHang(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, HangAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var analysis = new HangAnalysis();

            if (_threadScanSucceeded)
            {
                // BeforeThreadStackScan/OnThreadStack already ran via the pipeline's
                // ThreadStackScanDispatcher — read back the accumulated state instead of a
                // second independent walk of runtime.Threads.
                analysis.TotalAliveThreads = _threadScanTotalAliveThreads;
                analysis.ThreadsHoldingLocks = _threadScanThreadsHoldingLocks;
                analysis.WaitingThreads = _threadScanWaitingThreads!;
            }
            else
            {
                // Fallback (non-participant) path: used when this analyzer is invoked directly
                // (tests, benchmarks) instead of through AnalysisPipeline's dispatcher.
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
            }

            ReadRuntimeThreadPool(runtime, analysis);
            progress?.Report(new(analysis.TotalAliveThreads, "analyzing async work items"));
            AnalyzeAsyncWork(heap, cache, analysis, options);

            analysis.HealthScore = ComputeHealthScore(analysis, options);

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
            info.RuntimeQueueLength = GetIntProperty(tp, "QueueLength");
            info.RuntimeCpuUtilization = tp.CpuUtilization;
            info.UsingPortableThreadPool = tp.UsingPortableThreadPool;
            info.UsingWindowsThreadPool = tp.UsingWindowsThreadPool;
        }

        private static int? GetIntProperty(object? instance, string propertyName)
        {
            if (instance is null)
                return null;

            try
            {
                object? value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
                return value switch
                {
                    int intValue => intValue,
                    long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Produces a 0-100 composite score. Each penalty maps to a named, observable signal
        /// so the score degrades predictably â€” not arbitrarily.
        /// </summary>
        private static int ComputeHealthScore(HangAnalysis analysis, HangAnalysisOptions options)
        {
            int score = 100;

            // â”€â”€ Waiting-thread pressure (up to -40) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            double waitingPct = analysis.TotalAliveThreads == 0 ? 0
                : analysis.WaitingThreads.Count * 100.0 / analysis.TotalAliveThreads;

            if (waitingPct >= 80) score -= 40;
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
            bool saturated = tp.RuntimeMaxThreads > 0 && tp.RuntimeActiveWorkerThreads >= tp.RuntimeMaxThreads;
            bool starvation = saturated && tp.RuntimeCpuUtilization < 20;
            if (starvation) score -= 15;
            else if (saturated) score -= 10;

            // â”€â”€ Task backpressure (-5) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if (tp.PendingTasks > options.HighThreadPoolThreshold) score -= 5;

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

        private void AnalyzeAsyncWork(ClrHeap heap, IHeapAnalysisCache? cache, HangAnalysis analysis, HangAnalysisOptions options)
        {
            // BeforeHeapIndexScan/OnHeapEntry already ran via the pipeline's HeapIndexScanDispatcher
            // before AnalyzeAsync executes when a heap index was available; read back the
            // participant-accumulated state instead of re-scanning the index. If the shared scan
            // failed partway (isolated by the dispatcher) or never ran, fall back to a full scan.
            if (_participantScanSucceeded)
            {
                BuildAsyncAnalysisFromParticipantState(analysis);
                return;
            }

            // No cache, or shared scan unavailable/failed: parallel over GC segments
            RunParallelAsyncScan(heap, inMemoryEntries: null, analysis, options);
        }

        // Unified parallel async-work scanner — drives either a flat in-memory HeapEntry[]
        // or a per-segment ClrObject walk.  The early scan-limit is honored via a volatile flag.
        private void RunParallelAsyncScan(ClrHeap heap, HeapEntry[]? inMemoryEntries, HangAnalysis analysis, HangAnalysisOptions options)
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
                // OPT (docs/cache/cache-architecture.md Phase 5): mt is already the
                // GetOrAdd key — resolve via the metadata cache instead of materializing a
                // ClrObject just for the type name.
                AsyncTypeProfile profile = profileByMethodTable.GetOrAdd(mt, static (mt, heap) =>
                {
                    ClrType? type = heap.GetTypeByMethodTable(mt);
                    return type is null
                        ? AsyncTypeProfile.None
                        : AsyncTypeProfile.FromTypeName(type.Name ?? string.Empty);
                }, heap);

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

                    if (scanned <= options.MaxTasksToScan)
                    {
                        var stateField = obj.Type.GetFieldByName("m_stateFlags");
                        if (stateField != null)
                        {
                            int stateFlags = stateField.Read<int>(obj, interior: false);
                            bool isCompleted = (stateFlags & 0x1000000) != 0;
                            bool isFaulted = (stateFlags & 0x200000) != 0;
                            bool isCanceled = (stateFlags & 0x400000) != 0;

                            if (isFaulted) Interlocked.Increment(ref faultedTasks);
                            else if (isCanceled) Interlocked.Increment(ref canceledTasks);
                            else if (!isCompleted) Interlocked.Increment(ref pendingTasks);
                        }
                    }

                    // Honor scan limit: signal remaining threads to skip task processing
                    if (scanned > options.MaxTasksToScan && Volatile.Read(ref queuedWorkItems) > 1000)
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
                QueuedWorkItems = queuedWorkItems,
                TotalTasks = totalTasks,
                PendingTasks = pendingTasks,
                FaultedTasks = faultedTasks,
                CanceledTasks = canceledTasks,
                TaskScanLimited = taskScanLimited
            };
            analysis.TaskContinuations = new Dictionary<string, int>(taskContinuations, StringComparer.Ordinal);
            analysis.TotalContinuations = totalContinuations;
        }

        private static AsyncTypeProfile ResolveAsyncTypeProfile(ClrHeap heap, in HeapEntry entry, Dictionary<ulong, AsyncTypeProfile> profileByMethodTable)
        {
            if (entry.MethodTable == 0)
                return AsyncTypeProfile.None;

            if (profileByMethodTable.TryGetValue(entry.MethodTable, out AsyncTypeProfile existing))
                return existing;

            // OPT (docs/cache/cache-architecture.md Phase 5): entry.MethodTable is
            // already known — resolve via the metadata cache instead of materializing a ClrObject.
            ClrType? type = heap.GetTypeByMethodTable(entry.MethodTable);
            if (type is null)
            {
                profileByMethodTable[entry.MethodTable] = AsyncTypeProfile.None;
                return AsyncTypeProfile.None;
            }

            string typeName = type.Name ?? string.Empty;
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
            ref int totalContinuations,
            int maxTasksToScan)
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

                if (tasksScanned <= maxTasksToScan)
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

        public void Dispose() { }
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
        public int? RuntimeQueueLength { get; set; }
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

// removed stray partial Dispose wrapper (Dispose implemented inside class)


