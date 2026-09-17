using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Threads
{
    /// <summary>
    /// Detects likely hang/deadlock signatures: waiting threads (by top-frame classification),
    /// thread-pool saturation/starvation, and async work backlog (pending/faulted/canceled tasks,
    /// continuation counts).
    /// </summary>
    /// <remarks>
    /// Retyped version consumes <c>heap.objects</c> (Task/QueuedWorkItem/continuation-shaped
    /// candidates), <c>dump.object-fields</c> (<c>m_stateFlags</c>), <c>runtime.threads</c> (top
    /// stack frame per alive thread), and <c>runtime.thread-pool</c> (<c>ClrThreadPool</c>) —
    /// <see cref="HangAnalyzerLegacyAdapter"/> (not this class) implements both
    /// <c>IParallelHeapIndexScanParticipant</c> and <c>IThreadStackScanParticipant</c>, mirroring the
    /// pre-retyping analyzer's own dual participant role, just moved to the adapter layer.
    ///
    /// A real, deliberate behavior simplification: the pre-retyping analyzer's standalone fallback
    /// (an independent parallel heap walk when no shared scan ran) has no equivalent here — same
    /// accepted simplification every other candidate-streaming Tier-2 analyzer already made (e.g.
    /// <c>TimerLeakAnalyzer</c>, <c>WeakReferenceAnalyzer</c>'s Phase B): a real analysis session
    /// always goes through the pipeline's shared scan, so an empty candidate list when it didn't run
    /// is accepted rather than re-implementing a second, independent scan path.
    /// </remarks>
    [AnalyzerModule(key: "hang", displayName: "Hang Analysis", order: 180, tags: ["threads"])]
    [RequiresCapability(
        CapabilityVocabulary.DumpObjectFields,
        CapabilityVocabulary.HeapObjects,
        CapabilityVocabulary.RuntimeThreads)]
    [OptionalCapability(CapabilityVocabulary.TraceThreadTimeline)]
    [OptionalCapability(CapabilityVocabulary.TraceContentionEvents)]
    public sealed class HangAnalyzer :
        IAnalyzer, IProducesAnalyzerDomainResult, IParallelHeapIndexScanParticipant,
        IThreadStackScanParticipant, ICustomizesAnalysisContext
    {
        private ClrHeap? _heap;
        private readonly Dictionary<ulong, string?> _relevantNameByMt = new();
        private readonly List<Sdk.Analysis.HeapObjectRef> _scanCandidates = new();
        private bool _heapScanSucceeded = true;

        private readonly ThreadStackScanState _threadScanState = new();

        public string Name => "Hang Analysis";
        public string Category => "Hang";

        // ── Heap-index-scan participant ──────────────────────────────────────────────
        void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _heap = context.Heap;

        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
        {
            if (entry.Address == 0 || entry.MethodTable == 0)
                return;

            if (!_relevantNameByMt.TryGetValue(entry.MethodTable, out string? name))
            {
                ClrType? type = _heap!.GetTypeByMethodTable(entry.MethodTable);
                string resolvedName = type?.Name ?? string.Empty;
                name = AsyncTypeProfile.FromTypeName(resolvedName).IsPotentiallyRelevant ? resolvedName : null;
                _relevantNameByMt[entry.MethodTable] = name;
            }

            if (name is null)
                return;

            _scanCandidates.Add(new Sdk.Analysis.HeapObjectRef(entry.Address, SdkTypeRefFactory.FromName(name, entry.MethodTable), entry.Size, name));
        }

        IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new HangAnalyzer();

        void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
        {
            foreach (IHeapIndexScanParticipant p in partials)
            {
                if (p is HangAnalyzer other)
                {
                    _scanCandidates.AddRange(other._scanCandidates);
                    if (!other._heapScanSucceeded)
                        _heapScanSucceeded = false;
                }
            }
        }

        void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _heapScanSucceeded = succeeded;

        // ── Thread-stack-scan participant ────────────────────────────────────────────
        // Only the top frame is needed (this analyzer's own wait-pattern check looks at the
        // innermost frame only).
        int IThreadStackScanParticipant.GetRequiredFrameCount(RuntimeAnalysisContext context) => 1;
        void IThreadStackScanParticipant.BeforeThreadStackScan(RuntimeAnalysisContext context) => _threadScanState.BeforeThreadStackScan();
        // This analyzer never reads RuntimeThreadRef.StackRootCount (its domain result carries no
        // such field), so stackRootCount stays 0 here.
        void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot) => _threadScanState.OnThreadStack(in snapshot, stackRootCount: 0);
        void IThreadStackScanParticipant.OnThreadStackScanCompleted(bool succeeded) => _threadScanState.OnThreadStackScanCompleted(succeeded);

        Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
            AnalysisContextBuilder.Build(
                context,
                analyzerOptions: null,
                runtimeThreadsOverride: _threadScanState.BuildPrecomputedQuery(),
                heapObjectsOverride: new PrecomputedHeapObjectStream(_heapScanSucceeded ? _scanCandidates : []));

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

            IHeapObjectStream candidates = context.HeapObjects
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
            IDumpObjectFieldQuery dumpFields = context.DumpObjectFields
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
            IRuntimeThreadQuery threads = context.RuntimeThreads
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");
            IRuntimeThreadPoolQuery threadPool = context.RuntimeThreadPool
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreadPool}' capability.");
            HangAnalysisOptions options = context.AnalyzerOptions as HangAnalysisOptions ?? new HangAnalysisOptions();

            LastResult = Analyze(candidates, dumpFields, threads, threadPool, options, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private static HangDomainResult Analyze(
            IHeapObjectStream candidates,
            IDumpObjectFieldQuery dumpFields,
            IRuntimeThreadQuery threads,
            IRuntimeThreadPoolQuery threadPoolQuery,
            HangAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            var analysis = new HangAnalysis();

            // ── Thread-stack side: waiting-thread detection (top frame only) ─────────────
            foreach (RuntimeThreadRef thread in threads.EnumerateThreads())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!thread.IsAlive)
                    continue;

                analysis.TotalAliveThreads++;
                if (thread.LockCount > 0)
                    analysis.ThreadsHoldingLocks++;

                ThreadStackFrameRef? topFrame = null;
                foreach (ThreadStackFrameRef f in threads.EnumerateStackFrames(thread))
                {
                    topFrame = f;
                    break;
                }
                if (topFrame is null)
                    continue;

                WaitingThreadInfo? waitInfo = DetectWaitPattern(thread, topFrame.Value);
                if (waitInfo != null)
                    analysis.WaitingThreads.Add(waitInfo);
            }

            // ── Heap-scan side: Task/QueuedWorkItem/continuation-shaped candidates ───────
            var threadPoolInfo = new ThreadPoolAnalysis();
            var taskContinuations = new Dictionary<string, int>(StringComparer.Ordinal);
            int totalContinuations = 0;

            foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();

                AsyncTypeProfile profile = AsyncTypeProfile.FromTypeName(candidate.TypeDisplayName);

                if (profile.IsQueuedWorkItem)
                    threadPoolInfo.QueuedWorkItems++;

                if (profile.IsTask)
                {
                    threadPoolInfo.TotalTasks++;

                    if (dumpFields.TryReadField(candidate.Address, "m_stateFlags", out FieldValue value) && value.Kind == FieldValueKind.Int32)
                    {
                        int stateFlags = value.AsInt32();
                        bool isCompleted = (stateFlags & 0x1000000) != 0;
                        bool isFaulted = (stateFlags & 0x200000) != 0;
                        bool isCanceled = (stateFlags & 0x400000) != 0;

                        if (isFaulted)
                            threadPoolInfo.FaultedTasks++;
                        else if (isCanceled)
                            threadPoolInfo.CanceledTasks++;
                        else if (!isCompleted)
                            threadPoolInfo.PendingTasks++;
                    }
                }

                if (profile.IsContinuation)
                {
                    totalContinuations++;
                    taskContinuations[candidate.TypeDisplayName] = taskContinuations.GetValueOrDefault(candidate.TypeDisplayName) + 1;
                }
            }

            analysis.ThreadPoolInfo = threadPoolInfo;
            analysis.TaskContinuations = taskContinuations;
            analysis.TotalContinuations = totalContinuations;

            // ── Runtime-sourced thread-pool telemetry ────────────────────────────────────
            if (threadPoolQuery.TryGetThreadPoolInfo(out RuntimeThreadPoolInfo info))
            {
                threadPoolInfo.RuntimeInitialized = true;
                threadPoolInfo.RuntimeMinThreads = info.MinThreads;
                threadPoolInfo.RuntimeMaxThreads = info.MaxThreads;
                threadPoolInfo.RuntimeActiveWorkerThreads = info.ActiveWorkerThreads;
                threadPoolInfo.RuntimeIdleWorkerThreads = info.IdleWorkerThreads;
                threadPoolInfo.RuntimeRetiredWorkerThreads = info.RetiredWorkerThreads;
                threadPoolInfo.RuntimeQueueLength = info.QueueLength;
                threadPoolInfo.RuntimeCpuUtilization = info.CpuUtilization;
            }

            // ── Build output ──────────────────────────────────────────────────────────────
            var waitCategoryBreakdown = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (WaitingThreadInfo wt in analysis.WaitingThreads)
            {
                string category = wt.WaitType.ToString();
                waitCategoryBreakdown.TryGetValue(category, out int count);
                waitCategoryBreakdown[category] = count + 1;
            }

            double waitingPct = analysis.TotalAliveThreads == 0 ? 0
                : analysis.WaitingThreads.Count * 100.0 / analysis.TotalAliveThreads;

            // Complete ranked lists — no report-width cap in the analyzer (§9.25 D5); the render
            // layer slices for display.
            var topWaitingThreads = new List<WaitingThreadInfo>(analysis.WaitingThreads);
            topWaitingThreads.Sort(static (a, b) =>
            {
                int byLock = b.LockCount.CompareTo(a.LockCount);
                return byLock != 0 ? byLock : b.WaitType.CompareTo(a.WaitType);
            });
            var waitingThreadSnapshots = new List<WaitingThreadSnapshot>(topWaitingThreads.Count);
            foreach (WaitingThreadInfo w in topWaitingThreads)
                waitingThreadSnapshots.Add(new WaitingThreadSnapshot(w.ThreadId, w.OSThreadId, w.WaitType.ToString(), w.WaitReason, w.LockCount, w.TopStackFrame));

            var continuationTypeCounts = new List<KeyValuePair<string, int>>(taskContinuations);
            continuationTypeCounts.Sort(static (a, b) => b.Value.CompareTo(a.Value));
            var continuationTypes = new List<NameCountEntry>(continuationTypeCounts.Count);
            foreach (KeyValuePair<string, int> kv in continuationTypeCounts)
                continuationTypes.Add(new NameCountEntry(kv.Key, kv.Value));

            return new HangDomainResult(
                analysis.TotalAliveThreads,
                analysis.WaitingThreads.Count,
                analysis.ThreadsHoldingLocks,
                waitingPct,
                waitCategoryBreakdown,
                analysis.TotalContinuations,
                threadPoolInfo.QueuedWorkItems,
                threadPoolInfo.TotalTasks,
                threadPoolInfo.PendingTasks,
                threadPoolInfo.FaultedTasks,
                threadPoolInfo.CanceledTasks,
                threadPoolInfo.RuntimeInitialized,
                threadPoolInfo.RuntimeMinThreads,
                threadPoolInfo.RuntimeMaxThreads,
                threadPoolInfo.RuntimeActiveWorkerThreads,
                threadPoolInfo.RuntimeIdleWorkerThreads,
                threadPoolInfo.RuntimeRetiredWorkerThreads,
                threadPoolInfo.RuntimeQueueLength,
                threadPoolInfo.RuntimeCpuUtilization,
                options.HighThreadPoolThreshold,
                waitingThreadSnapshots,
                continuationTypes);
        }

        private static WaitingThreadInfo? DetectWaitPattern(RuntimeThreadRef thread, ThreadStackFrameRef topFrame)
        {
            if (!topFrame.HasMethod)
                return null;

            string topMethod = (topFrame.RawMethodSignature ?? topFrame.MethodDisplayName ?? "").ToLowerInvariant();

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
                    ThreadId = (uint)(thread.Thread.ManagedThreadId ?? 0),
                    OSThreadId = thread.Thread.OsThreadId,
                    WaitType = waitType.Value,
                    WaitReason = waitReason ?? "Unknown wait",
                    LockCount = thread.LockCount,
                    TopStackFrame = topFrame.RawMethodSignature ?? topFrame.MethodDisplayName ?? "Unknown",
                };
            }

            return null;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Structural classification of a candidate heap object's type name into the three shapes this
    /// analyzer cares about. Pure string matching, no ClrMD dependency — shared between
    /// <see cref="HangAnalyzerLegacyAdapter"/> (which uses <see cref="IsPotentiallyRelevant"/> to
    /// decide whether to collect a candidate at all during the heap scan) and <see cref="HangAnalyzer"/>
    /// itself (which re-derives the specific category from each collected candidate's type name).
    /// </summary>
    internal readonly record struct AsyncTypeProfile(string TypeName, bool IsTask, bool IsQueuedWorkItem, bool IsContinuation)
    {
        public static AsyncTypeProfile None => new(string.Empty, false, false, false);

        public bool IsPotentiallyRelevant => IsTask || IsQueuedWorkItem || IsContinuation;

        public static AsyncTypeProfile FromTypeName(string typeName)
        {
            bool isQueuedWorkItem = typeName.Contains("QueueUserWorkItemCallback", StringComparison.Ordinal)
                || typeName.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal);
            bool isTask = TaskTypeNamePattern.IsTaskType(typeName);
            bool isContinuation = typeName.Contains("ContinuationTask", StringComparison.Ordinal)
                || typeName.Contains("AwaitTaskContinuation", StringComparison.Ordinal);

            return new AsyncTypeProfile(typeName, isTask, isQueuedWorkItem, isContinuation);
        }
    }

    internal sealed class HangAnalysis
    {
        public int TotalAliveThreads { get; set; }
        public int ThreadsHoldingLocks { get; set; }
        public List<WaitingThreadInfo> WaitingThreads { get; set; } = new();
        public ThreadPoolAnalysis ThreadPoolInfo { get; set; } = new();
        public int TotalContinuations { get; set; }
        public Dictionary<string, int> TaskContinuations { get; set; } = new();
    }

    internal sealed class WaitingThreadInfo
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public WaitType WaitType { get; set; }
        public string WaitReason { get; set; } = string.Empty;
        public int LockCount { get; set; }
        public string TopStackFrame { get; set; } = string.Empty;
    }

    internal sealed class ThreadPoolAnalysis
    {
        // Heap-scan counters
        public int QueuedWorkItems { get; set; }
        public int TotalTasks { get; set; }
        public int PendingTasks { get; set; }
        public int FaultedTasks { get; set; }
        public int CanceledTasks { get; set; }

        // Runtime-sourced counters (from runtime.thread-pool)
        public bool RuntimeInitialized { get; set; }
        public int RuntimeMinThreads { get; set; }
        public int RuntimeMaxThreads { get; set; }
        public int RuntimeActiveWorkerThreads { get; set; }
        public int RuntimeIdleWorkerThreads { get; set; }
        public int RuntimeRetiredWorkerThreads { get; set; }
        public int? RuntimeQueueLength { get; set; }
        public int RuntimeCpuUtilization { get; set; }
    }

    internal enum WaitType
    {
        MonitorWait,
        TaskWait,
        Sleep,
        SemaphoreWait,
        EventWait,
        IOWait,
        ThreadJoin,
    }
}
