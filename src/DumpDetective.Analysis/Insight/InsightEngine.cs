using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Insight;

/// <summary>
/// Cross-cutting insight engine. Consumes the per-analyzer domain results already produced
/// by the pipeline and emits ranked <see cref="InsightFinding"/> records that correlate
/// patterns across multiple analyzers — things no single analyzer can detect in isolation.
/// </summary>
/// <remarks>
/// This is a stateless, synchronous engine. All logic is pure pattern-matching on the
/// domain results passed in; no heap access or ClrMD calls are made here.
/// </remarks>
internal sealed class InsightEngine
{
    private static readonly IReadOnlyList<IInsightRuleGroup> RuleGroups =
    [
        new BaselineRuleGroup(),
        new MemoryAndRuntimeRuleGroup(),
        new CorrelationRuleGroup(),
    ];

    // ── Thresholds ────────────────────────────────────────────────────────────

    private const double LohPressureWarningPct = 25.0;
    private const double LohPressureCriticalPct = 40.0;
    private const double PohPressureWarningPct = 10.0;
    private const double LohFragWarningPct = 30.0;
    private const double LohFragCriticalPct = 60.0;
    private const double ThreadBlockedWarningPct = 50.0;
    private const double ThreadBlockedCriticalPct = 75.0;
    private const int FinalizerQueueWarning = 1_000;
    private const int FinalizerQueueCritical = 10_000;
    private const int PinnedHandleWarning = 100;
    private const int AnalyzerFailureWarning = 3;

    // Fatal exception detection
    private static readonly HashSet<string> FatalExceptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.OutOfMemoryException",
        "System.StackOverflowException",
        "System.ExecutionEngineException",
        "System.AccessViolationException",
    };

    // New thresholds for Part 4 additions
    private const double StringDuplicationWarningRatio = 0.50;
    private const double WeakRefDeadTargetWarningRatio = 0.50;
    private const double EphemeralFillCriticalPct = 90.0;
    private const int DynamicModuleWarning = 20;
    private const ulong JitHeapBloatThreshold = 500UL * 1024 * 1024;      // 500 MB
    private const int JitModuleHotspotMinFrameHits = 50;
    private const int SuspendedMethodFireForgetThreshold = 100;
    private const ulong LohArrayPressureThreshold = 256UL * 1024 * 1024;  // 256 MB
    private const ulong GCRootLargeRetentionThreshold = 50UL * 1024 * 1024; // 50 MB
    private const double ClusterHangOverlapWarningRatio = 0.60;
    private const ulong MemoryGenerationCorrelationMinBytes = 10UL * 1024 * 1024;    // 10 MB
    private const ulong MemoryGenerationCorrelationCriticalBytes = 100UL * 1024 * 1024; // 100 MB
    private const double MemoryGenerationCorrelationGen2FractionPct = 85.0;
    private const int MemoryGenerationCorrelationTopTypesScanned = 30;
    private const ulong StringMemoryCorrelationMinWastedBytes = 5UL * 1024 * 1024; // 5 MB
    private const int StringMemoryCorrelationMaxRank = 10;

    private const string Source = "InsightEngine";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Analyzes the completed analyzer run results and returns cross-cutting insight findings.
    /// The returned list is ordered by severity descending (Critical → Warning → Info).
    /// </summary>
    public IReadOnlyList<InsightFinding> Analyze(IReadOnlyList<AnalyzerRunResult> runs)
    {
        var findings = new List<InsightFinding>();

        // Extract domain results by type once — avoids repeated iteration.
        MemoryDomainResult? memory = FindResult<MemoryDomainResult>(runs);
        GCGenerationDomainResult? gcGen = FindResult<GCGenerationDomainResult>(runs);
        LohFragmentationDomainResult? lohFrag = FindResult<LohFragmentationDomainResult>(runs);
        HeapTopologyDomainResult? segments = FindResult<HeapTopologyDomainResult>(runs);
        ThreadDomainResult? threads = FindResult<ThreadDomainResult>(runs);
        HangDomainResult? hang = FindResult<HangDomainResult>(runs);
        ThreadStackClusterDomainResult? clusters = FindResult<ThreadStackClusterDomainResult>(runs);
        AsyncTaskDomainResult? asyncTasks = FindResult<AsyncTaskDomainResult>(runs);
        DominatorDomainResult? leak = FindResult<DominatorDomainResult>(runs);
        GCHandleDomainResult? handles = FindResult<GCHandleDomainResult>(runs);
        CrashDomainResult? crash = FindResult<CrashDomainResult>(runs);
        CollectionDomainResult? collections = FindResult<CollectionDomainResult>(runs);
        StringDomainResult? strings = FindResult<StringDomainResult>(runs);
        FinalizableObjectDomainResult? finalizable = FindResult<FinalizableObjectDomainResult>(runs);

        // Part 4 — new domain result inputs
        GCRootDomainResult? gcRoot = FindResult<GCRootDomainResult>(runs);
        AllocationPatternDomainResult? allocPattern = FindResult<AllocationPatternDomainResult>(runs);
        ArrayDomainResult? arrays = FindResult<ArrayDomainResult>(runs);
        AsyncStateMachineDomainResult? stateMachines = FindResult<AsyncStateMachineDomainResult>(runs);
        WeakReferenceDomainResult? weakRef = FindResult<WeakReferenceDomainResult>(runs);
        SegmentReservationDomainResult? segReservation = FindResult<SegmentReservationDomainResult>(runs);
        ModuleDomainResult? appDomains = FindResult<ModuleDomainResult>(runs);
        JitDomainResult? jit = FindResult<JitDomainResult>(runs);
        BoxingDomainResult? boxing = FindResult<BoxingDomainResult>(runs);
        EventLeakDomainResult? eventLeaks = FindResult<EventLeakDomainResult>(runs);

        // Part 6 — Infrastructure domain results
        DbConnectionDomainResult? dbConn = FindResult<DbConnectionDomainResult>(runs);
        WcfChannelDomainResult? wcf = FindResult<WcfChannelDomainResult>(runs);
        HttpObjectDomainResult? http = FindResult<HttpObjectDomainResult>(runs);

        var ruleContext = new InsightRuleContext(
            Runs: runs,
            Memory: memory,
            GcGen: gcGen,
            LohFrag: lohFrag,
            Segments: segments,
            Threads: threads,
            Hang: hang,
            Clusters: clusters,
            AsyncTasks: asyncTasks,
            Leak: leak,
            Handles: handles,
            Crash: crash,
            Collections: collections,
            Strings: strings,
            Finalizable: finalizable,
            GcRoot: gcRoot,
            AllocPattern: allocPattern,
            Arrays: arrays,
            StateMachines: stateMachines,
            WeakRef: weakRef,
            SegReservation: segReservation,
            AppDomains: appDomains,
            Jit: jit,
            Boxing: boxing,
            EventLeaks: eventLeaks,
            DbConn: dbConn,
            Wcf: wcf,
            Http: http);

        for (int i = 0; i < RuleGroups.Count; i++)
            RuleGroups[i].Apply(findings, in ruleContext);

        // Sort by severity descending: Critical(2) > Warning(1) > Info(0)
        findings.Sort(static (a, b) => b.Severity.CompareTo(a.Severity));
        return findings;
    }

    private interface IInsightRuleGroup
    {
        void Apply(List<InsightFinding> findings, in InsightRuleContext context);
    }

    private readonly record struct InsightRuleContext(
        IReadOnlyList<AnalyzerRunResult> Runs,
        MemoryDomainResult? Memory,
        GCGenerationDomainResult? GcGen,
        LohFragmentationDomainResult? LohFrag,
        HeapTopologyDomainResult? Segments,
        ThreadDomainResult? Threads,
        HangDomainResult? Hang,
        ThreadStackClusterDomainResult? Clusters,
        AsyncTaskDomainResult? AsyncTasks,
        DominatorDomainResult? Leak,
        GCHandleDomainResult? Handles,
        CrashDomainResult? Crash,
        CollectionDomainResult? Collections,
        StringDomainResult? Strings,
        FinalizableObjectDomainResult? Finalizable,
        GCRootDomainResult? GcRoot,
        AllocationPatternDomainResult? AllocPattern,
        ArrayDomainResult? Arrays,
        AsyncStateMachineDomainResult? StateMachines,
        WeakReferenceDomainResult? WeakRef,
        SegmentReservationDomainResult? SegReservation,
        ModuleDomainResult? AppDomains,
        JitDomainResult? Jit,
        BoxingDomainResult? Boxing,
        EventLeakDomainResult? EventLeaks,
        DbConnectionDomainResult? DbConn,
        WcfChannelDomainResult? Wcf,
        HttpObjectDomainResult? Http);

    private sealed class BaselineRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectLohPressure(findings, context.Memory, context.GcGen, context.Segments);
            DetectLohFragmentation(findings, context.LohFrag, context.Segments);
            DetectPohGrowth(findings, context.Segments);
            DetectThreadContention(findings, context.Threads, context.Hang);
            DetectFinalizerQueueBacklog(findings, context.Threads, context.Leak, context.Finalizable);
            DetectPinnedHandlePressure(findings, context.Handles, context.LohFrag);
            DetectActiveCrash(findings, context.Crash);
            DetectLeakSuspicion(findings, context.Leak, context.Strings);
            DetectWastefulCollections(findings, context.Collections);
            DetectOrphanedTaskAccumulation(findings, context.AsyncTasks, context.Threads);
            DetectAnalyzerFailures(findings, context.Runs);
        }
    }

    private sealed class MemoryAndRuntimeRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectGCRootLargeRetention(findings, context.GcRoot);
            DetectAllocationPressureCrossCorrelation(findings, context.AllocPattern, context.Threads);
            DetectStringDuplicationRatio(findings, context.Strings);
            DetectLohArrayPressure(findings, context.Arrays, context.LohFrag);
            DetectAsyncStateMachineFireAndForget(findings, context.StateMachines, context.Memory);
            DetectStaleWeakReferenceAccumulation(findings, context.WeakRef);
            DetectAddressSpacePressure(findings, context.SegReservation, context.Segments);
            DetectDynamicAssemblyAccumulation(findings, context.AppDomains);
            DetectJitHeapBloat(findings, context.Jit, context.Threads);
            DetectBoxingGCCorrelation(findings, context.Boxing, context.GcGen);
            DetectJitModuleHotspot(findings, context.Jit, context.AppDomains);
        }
    }

    private sealed class CorrelationRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectFatalExceptionOnHeap(findings, context.Crash);
            DetectEventLeakPattern(findings, context.EventLeaks, context.GcGen, context.Finalizable);
            DetectDataTableLifecyclePattern(findings, context.Finalizable, context.Memory);
            DetectKnownLeakPatterns(findings, context.Memory);
            DetectMemoryTypeGenerationCorrelation(findings, context.Memory, context.GcGen);
            DetectStringMemoryConcentration(findings, context.Memory, context.Strings);
            DetectRecurringTimeoutPattern(findings, context.Crash);

            DetectDbConnectionLeak(findings, context.DbConn, context.Crash);
            DetectWcfChannelFault(findings, context.Wcf, context.Crash);
            DetectHttpClientAccumulation(findings, context.Http);
            DetectClusterHangCorrelation(findings, context.Clusters, context.Hang);
        }
    }

    // ── Detection rules ───────────────────────────────────────────────────────

    private static void DetectLohPressure(
        List<InsightFinding> findings,
        MemoryDomainResult? memory,
        GCGenerationDomainResult? gcGen,
        HeapTopologyDomainResult? segments)
    {
        double lohPct = memory?.LohPercent
            ?? gcGen?.LohPercent
            ?? segments?.LohPercent
            ?? -1;

        if (lohPct < LohPressureWarningPct)
            return;

        FindingSeverity sev = lohPct >= LohPressureCriticalPct
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        ulong lohBytes = memory?.LohBytes ?? gcGen?.LohBytes ?? segments?.LohBytes ?? 0;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "Large Object Heap (LOH) pressure detected",
            Evidence: $"LOH is {lohPct:F1}% of total heap ({FormatBytes(lohBytes)}). " +
                      $"Threshold: Warning ≥ {LohPressureWarningPct}%, Critical ≥ {LohPressureCriticalPct}%.",
            Recommendation: "Review allocations ≥ 85 KB. Consider pooling large arrays (ArrayPool<T>), " +
                            "chunking large collections, or enabling Server GC with LOH compaction.",
            Tags: ["loh", "gc", "memory-pressure"]));
    }

    private static void DetectLohFragmentation(
        List<InsightFinding> findings,
        LohFragmentationDomainResult? lohFrag,
        HeapTopologyDomainResult? segments)
    {
        if (lohFrag is null)
            return;

        if (lohFrag.FragmentationPercent < LohFragWarningPct)
            return;

        FindingSeverity sev = lohFrag.FragmentationPercent >= LohFragCriticalPct
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "LOH fragmentation is high",
            Evidence: $"LOH fragmentation: {lohFrag.FragmentationPercent:F1}% " +
                      $"({FormatBytes(lohFrag.FreeBytes)} free in {lohFrag.FreeBlockCount} blocks, " +
                      $"largest free block: {FormatBytes(lohFrag.LargestFreeBlock)}).",
            Recommendation: "Enable LOH compaction via GCSettings.LargeObjectHeapCompactionMode, " +
                            "reduce LOH allocations, or upgrade to .NET 6+ with improved LOH handling.",
            Tags: ["loh", "fragmentation", "gc"]));
    }

    private static void DetectPohGrowth(
        List<InsightFinding> findings,
        HeapTopologyDomainResult? segments)
    {
        if (segments is null || segments.PohPercent < PohPressureWarningPct)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Pinned Object Heap (POH) occupies significant heap space",
            Evidence: $"POH is {segments.PohPercent:F1}% of total heap ({FormatBytes(segments.PohBytes)}).",
            Recommendation: "Audit use of GC.AllocateArray with pinned=true and unsafe pinned buffers. " +
                            "Excessive POH usage can fragment address space and increase GC pause times.",
            Tags: ["poh", "pinning", "gc"]));
    }

    private static void DetectThreadContention(
        List<InsightFinding> findings,
        ThreadDomainResult? threads,
        HangDomainResult? hang)
    {
        // Prefer HangDomainResult for waiting-percent (more precise); fall back to ThreadDomainResult.
        if (hang is not null && hang.WaitingPercent >= ThreadBlockedWarningPct)
        {
            FindingSeverity sev = hang.WaitingPercent >= ThreadBlockedCriticalPct
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Threads",
                Severity: sev,
                Title: "High proportion of threads are blocked or waiting",
                Evidence: $"{hang.WaitingPercent:F1}% of threads waiting ({hang.WaitingThreadCount} / total threads). " +
                          $"Threads holding locks: {hang.ThreadsHoldingLocks}.",
                Recommendation: "Inspect blocked threads for lock contention, deadlocks, or async-over-sync " +
                                "anti-patterns. Review the Hang and ThreadAnalyzer findings for specific call stacks.",
                Tags: ["threads", "contention", "hang", "blocking"]));
            return;
        }

        if (threads is null)
            return;

        int alive = threads.AliveThreadCount;
        if (alive == 0)
            return;

        double blockedPct = threads.BlockedThreadCount * 100.0 / alive;
        if (blockedPct >= ThreadBlockedWarningPct)
        {
            FindingSeverity sev = blockedPct >= ThreadBlockedCriticalPct
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Threads",
                Severity: sev,
                Title: "High proportion of threads are blocked",
                Evidence: $"{blockedPct:F1}% of alive threads blocked ({threads.BlockedThreadCount}/{alive}). " +
                          $"Lock-holding threads: {threads.LockHoldingThreadCount}. " +
                          (threads.FinalizerThreadBlocked ? "Finalizer thread is blocked. " : string.Empty),
                Recommendation: "Review lock usage, async-over-sync patterns, and finalizer overhead. " +
                                "A blocked finalizer thread can cause memory to accumulate.",
                Tags: ["threads", "blocking", "contention"]));
        }

        // Separately flag a blocked finalizer thread regardless of overall blocked-%.
        if (threads.FinalizerThreadBlocked)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Threads",
                Severity: FindingSeverity.Critical,
                Title: "Finalizer thread is blocked",
                Evidence: $"Finalizer thread (OS tid: {threads.FinalizerOsThreadId}) is blocked " +
                          $"with {threads.FinalizerLockCount} lock(s) held.",
                Recommendation: "A blocked finalizer thread prevents finalization of all objects in the queue. " +
                                "Investigate the finalizer thread's call stack and remove blocking operations from finalizers.",
                Tags: ["finalizer", "threads", "blocking", "gc"]));
        }
    }

    private static void DetectFinalizerQueueBacklog(
        List<InsightFinding> findings,
        ThreadDomainResult? threads,
        DominatorDomainResult? leak,
        FinalizableObjectDomainResult? finalizable)
    {
        int queueCount = finalizable?.FinalizerQueueCount ?? 0;
        if (queueCount < FinalizerQueueWarning)
            return;

        FindingSeverity sev = queueCount >= FinalizerQueueCritical
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        bool finalizerBlocked = threads?.FinalizerThreadBlocked ?? false;
        ulong retainedBytes = finalizable?.FinalizerQueueRetainedBytes ?? 0;

        // Starvation risk: large retained sub-graphs + blocked finalizer thread
        if (finalizerBlocked && retainedBytes > 0)
            sev = FindingSeverity.Critical;

        string retainedPart = retainedBytes > 0
            ? $" Estimated retained memory in queue sub-graphs: {FormatBytes(retainedBytes)}."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "Large finalizer queue backlog detected",
            Evidence: $"{queueCount:N0} objects are waiting in the finalizer queue.{retainedPart} " +
                      (finalizerBlocked ? "Finalizer thread is currently blocked — starvation risk." : string.Empty),
            Recommendation: "Objects with finalizers hold memory for at least two GC cycles. " +
                            "Prefer IDisposable + using/Dispose over finalizers. " +
                            "Implement IDisposable in finalizable types and call GC.SuppressFinalize.",
            Tags: ["finalizer", "gc", "memory-leak", "dispose"]));
    }

    private static void DetectPinnedHandlePressure(
        List<InsightFinding> findings,
        GCHandleDomainResult? handles,
        LohFragmentationDomainResult? lohFrag)
    {
        if (handles is null || handles.PinnedHandleTargets < PinnedHandleWarning)
            return;

        // Elevate to Critical when pinned handles correlate with LOH fragmentation.
        bool correlatedWithFrag = lohFrag is not null &&
                                  lohFrag.FragmentationPercent >= LohFragWarningPct;
        FindingSeverity sev = correlatedWithFrag
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        string correlation = correlatedWithFrag
            ? $" LOH fragmentation is {lohFrag!.FragmentationPercent:F1}%, which may be caused by pinning."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "High number of pinned GC handles detected",
            Evidence: $"{handles.PinnedHandleTargets:N0} pinned handles found.{correlation}",
            Recommendation: "Excessive pinning prevents GC from compacting memory, increasing fragmentation. " +
                            "Use Memory<T>/Span<T> with MemoryHandle or GCHandle.Alloc(pin=false) where possible. " +
                            "Ensure pinned buffers are released promptly.",
            Tags: ["pinning", "gc", "fragmentation", "handles"]));
    }

    private static void DetectActiveCrash(
        List<InsightFinding> findings,
        CrashDomainResult? crash)
    {
        if (crash is null || crash.ActiveExceptions == 0)
            return;

        // Find the dominant exception type.
        string? dominantType = null;
        int dominantCount = 0;
        foreach (KeyValuePair<string, int> kv in crash.ActiveExceptionTypeCounts)
        {
            if (kv.Value > dominantCount)
            {
                dominantCount = kv.Value;
                dominantType = kv.Key;
            }
        }

        string evidenceDetail = dominantType is not null
            ? $"Dominant type: {dominantType} ({dominantCount} active). Total active: {crash.ActiveExceptions}."
            : $"Total active exceptions: {crash.ActiveExceptions}.";

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Crash",
            Severity: FindingSeverity.Critical,
            Title: "Active exceptions found — dump may represent a crash or unhandled exception state",
            Evidence: evidenceDetail,
            Recommendation: "Examine the Crash analyzer findings for full exception messages and stack traces. " +
                            "Correlate with the Thread analyzer to identify the faulting thread.",
            Tags: ["crash", "exception", "active-exception"]));
    }

    private static void DetectLeakSuspicion(
        List<InsightFinding> findings,
        DominatorDomainResult? leak,
        StringDomainResult? strings)
    {
        if (leak is not null && leak.HighlyReferencedObjectCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Objects with unusually high incoming reference counts detected",
                Evidence: $"{leak.HighlyReferencedObjectCount:N0} objects have an abnormally high number of " +
                          "incoming references, indicating potential retention through event handlers, " +
                          "static collections, or observer patterns.",
                Recommendation: "Review the Retention analyzer findings for specific types. " +
                                "Common causes: static event subscriptions, global caches, and long-lived delegates " +
                                "capturing closures over short-lived objects.",
                Tags: ["memory-leak", "retention", "references"]));
        }

        // Duplicate string waste — now sourced from StringDomainResult.
        if (strings is not null && strings.DuplicateWastedBytes > 10 * 1024 * 1024) // 10 MB threshold
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Info,
                Title: "Significant memory wasted by duplicate string instances",
                Evidence: $"{FormatBytes(strings.DuplicateWastedBytes)} wasted across " +
                          $"{strings.DuplicatePatternCount:N0} duplicate string pattern(s). " +
                          $"Total strings: {strings.TotalStrings:N0} ({FormatBytes(strings.TotalStringMemoryBytes)}).",
                Recommendation: "Use string.Intern or a shared lookup table for repeated strings. " +
                                "Consider replacing string keys with enum or int IDs in hot dictionaries.",
                Tags: ["memory-leak", "strings", "interning"]));
        }
    }

    private static void DetectWastefulCollections(
        List<InsightFinding> findings,
        CollectionDomainResult? collections)
    {
        if (collections is null || collections.WastefulCollectionCount == 0)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "Collections with significant wasted capacity found",
            Evidence: $"{collections.WastefulCollectionCount:N0} wasteful collection(s) identified, " +
                      $"wasting {FormatBytes(collections.TotalWastedMemory)} total. " +
                      $"Total collections scanned: {collections.TotalCollections:N0}.",
            Recommendation: "Call TrimExcess() after bulk-removing items from List<T> or Dictionary<K,V>. " +
                            "Use initial capacity hints in collection constructors to avoid over-allocation.",
            Tags: ["collections", "capacity", "wasted-memory"]));
    }

    private static void DetectOrphanedTaskAccumulation(
        List<InsightFinding> findings,
        AsyncTaskDomainResult? asyncTasks,
        ThreadDomainResult? threads)
    {
        if (asyncTasks is null) return;

        // Cross-cutting: orphaned faulted tasks + blocked finalizer = risk of starvation
        if (asyncTasks.FaultedTasks > 0 && threads is { FinalizerThreadBlocked: true })
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Async",
                Severity: FindingSeverity.Warning,
                Title: "Faulted tasks combined with blocked finalizer thread",
                Evidence: $"{asyncTasks.FaultedTasks:N0} faulted tasks detected while the finalizer thread is blocked. Unobserved task exceptions may prevent finalizable resources from being reclaimed.",
                Recommendation: "Ensure task exceptions are observed (await, .Exception, or UnobservedTaskException handler). Unblock the finalizer thread to resume resource cleanup.",
                Tags: ["async", "task", "fault", "finalizer"],
                MetricValue: asyncTasks.FaultedTasks,
                MetricUnit: "faulted-tasks"));
        }

        // Cross-cutting: large orphan count relative to total tasks
        if (asyncTasks.TotalTasks > 0)
        {
            double orphanPct = asyncTasks.OrphanedTasks * 100.0 / asyncTasks.TotalTasks;
            if (orphanPct >= 30.0 && asyncTasks.OrphanedTasks >= 50)
            {
                findings.Add(new InsightFinding(
                    Analyzer: Source,
                    Category: "Async",
                    Severity: FindingSeverity.Warning,
                    Title: "High proportion of orphaned tasks indicates systemic fire-and-forget",
                    Evidence: $"{asyncTasks.OrphanedTasks:N0} of {asyncTasks.TotalTasks:N0} tasks ({orphanPct:F1}%) have no continuation. This pattern prevents exception propagation and may mask faults.",
                    Recommendation: "Audit call sites that produce tasks without await. Use Task.WhenAll for bulk orchestration and structured concurrency to ensure tasks are always observed.",
                    Tags: ["async", "task", "orphan", "pattern"],
                    MetricValue: orphanPct,
                    MetricUnit: "% orphaned"));
            }
        }
    }

    private static void DetectAnalyzerFailures(
        List<InsightFinding> findings,
        IReadOnlyList<AnalyzerRunResult> runs)
    {
        int failCount = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Status == AnalyzerExecutionStatus.Failed)
                failCount++;
        }

        if (failCount < AnalyzerFailureWarning)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Analysis",
            Severity: FindingSeverity.Warning,
            Title: $"{failCount} analyzer(s) failed — insights may be incomplete",
            Evidence: $"{failCount} of {runs.Count} analyzers did not complete successfully. " +
                      "Cross-cutting correlations that depend on their results will be absent.",
            Recommendation: "Check the diagnostic output for per-analyzer error messages. " +
                            "Re-run with --diagnostic flag for detailed error information.",
            Tags: ["analysis-quality", "failed-analyzer"],
            ConfidenceScore: 0.55,
                Caveats: ["Derived from a partially complete analyzer set."]));
    }

    // ── Part 4: New cross-cutting detection methods ───────────────────────────

    /// <summary>
    /// Flags GC roots that each retain a large estimated sub-graph (≥ 50 MB).
    /// A single powerful root with a large retained set is a primary leak pattern.
    /// </summary>
    private static void DetectGCRootLargeRetention(
        List<InsightFinding> findings,
        GCRootDomainResult? gcRoot)
    {
        if (gcRoot is null || gcRoot.TopRootsBySeverity.Count == 0)
            return;

        // Find the most impactful root overall
        ulong maxRetained = 0;
        string? rootKind = null;
        string? targetType = null;

        for (int i = 0; i < gcRoot.TopRootsBySeverity.Count; i++)
        {
            RootFinding r = gcRoot.TopRootsBySeverity[i];
            if (r.EstimatedRetainedBytes > maxRetained)
            {
                maxRetained = r.EstimatedRetainedBytes;
                rootKind = r.RootKind;
                targetType = r.TargetTypeName;
            }
        }

        if (maxRetained < GCRootLargeRetentionThreshold)
            return;

        // Also count how many roots exceed the threshold
        int largeRootCount = 0;
        for (int i = 0; i < gcRoot.TopRootsBySeverity.Count; i++)
        {
            if (gcRoot.TopRootsBySeverity[i].EstimatedRetainedBytes >= GCRootLargeRetentionThreshold)
                largeRootCount++;
        }

        // Check if any by-kind summary shows a dominant large root kind
        string? dominantKind = null;
        ulong dominantKindBytes = 0;
        for (int i = 0; i < gcRoot.ByKind.Count; i++)
        {
            RootKindSummary s = gcRoot.ByKind[i];
            if (s.EstimatedRetainedBytes > dominantKindBytes)
            {
                dominantKindBytes = s.EstimatedRetainedBytes;
                dominantKind = s.Kind;
            }
        }

        string kindNote = dominantKind is not null
            ? $" Dominant root kind: {dominantKind} ({FormatBytes(dominantKindBytes)} retained)."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "GC roots retaining large object sub-graphs detected",
            Evidence: $"{largeRootCount:N0} root(s) each retain ≥ {FormatBytes(GCRootLargeRetentionThreshold)}. " +
                      $"Largest root ({rootKind ?? "unknown"} → {targetType ?? "?"}) retains {FormatBytes(maxRetained)}.{kindNote}",
            Recommendation: "Review the GC Root analyzer findings for specific retention paths. " +
                            "Look for static fields, GC handles, or thread locals holding large collections.",
            Tags: ["gc-root", "retention", "memory-leak"],
            MetricValue: (double)maxRetained,
            MetricUnit: "bytes"));
    }

    /// <summary>
    /// Correlates high GC pressure (from AllocationPattern) with thread-blocking patterns.
    /// When gen0 churn is extreme and threads are blocked, GC pauses are likely the bottleneck.
    /// </summary>
    private static void DetectAllocationPressureCrossCorrelation(
        List<InsightFinding> findings,
        AllocationPatternDomainResult? allocPattern,
        ThreadDomainResult? threads)
    {
        if (allocPattern is null)
            return;

        // Standalone: GC pressure is Critical
        if (allocPattern.GCPressure == GCPressureLevel.Critical)
        {
            bool threadImpact = threads is not null &&
                                threads.AliveThreadCount > 0 &&
                                threads.BlockedThreadCount * 100.0 / threads.AliveThreadCount >= 30.0;

            string crossNote = threadImpact
                ? $" Combined with {threads!.BlockedThreadCount:N0} blocked threads ({threads.BlockedThreadCount * 100.0 / threads.AliveThreadCount:F0}%), GC pauses may be causing thread stalls."
                : string.Empty;

            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Critical,
                Title: "Critical GC allocation pressure detected",
                Evidence: $"Allocation pattern: {allocPattern.Profile}. " +
                          $"Gen0: {allocPattern.Gen0CountPct:F1}% of objects ({allocPattern.Gen0SizePct:F1}% of size). " +
                          $"Promotion pressure score: {allocPattern.PromotionPressureScore:F2}.{crossNote}",
                Recommendation: "Profile allocations with dotnet-trace. Reduce transient allocations with " +
                                "object pooling (ArrayPool<T>, ObjectPool<T>), struct types, and Span<T>.",
                Tags: ["gc-pressure", "allocation", "gen0"],
                MetricValue: allocPattern.PromotionPressureScore,
                MetricUnit: "score"));
            return;
        }

        // Standalone: GC pressure is High
        if (allocPattern.GCPressure == GCPressureLevel.High)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "High GC allocation pressure detected",
                Evidence: $"Allocation pattern: {allocPattern.Profile}. " +
                          $"Gen0: {allocPattern.Gen0CountPct:F1}% of objects. " +
                          $"Promotion pressure score: {allocPattern.PromotionPressureScore:F2}.",
                Recommendation: "Consider object pooling and reduced short-lived allocations on hot paths.",
                Tags: ["gc-pressure", "allocation", "gen0"],
                MetricValue: allocPattern.PromotionPressureScore,
                MetricUnit: "score"));
        }
    }

    /// <summary>
    /// Flags high string duplication ratio (> 50%). Complements the per-waste-bytes check
    /// already in <see cref="DetectLeakSuspicion"/> — this fires on ratio regardless of
    /// absolute waste bytes.
    /// </summary>
    private static void DetectStringDuplicationRatio(
        List<InsightFinding> findings,
        StringDomainResult? strings)
    {
        if (strings is null || strings.TotalStrings == 0)
            return;

        if (strings.DuplicationRatio < StringDuplicationWarningRatio)
            return;

        // Avoid double-firing if DetectLeakSuspicion already emitted a finding for this.
        // Only emit when the absolute waste is < 10 MB (otherwise DetectLeakSuspicion covered it).
        if (strings.DuplicateWastedBytes >= 10 * 1024 * 1024)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "High string duplication ratio detected",
            Evidence: $"{strings.DuplicationRatio:P0} of string instances are duplicates " +
                      $"({strings.TotalStrings - strings.SampledUniquePatterns:N0} duplicate out of {strings.TotalStrings:N0} total). " +
                      $"Wasted: {FormatBytes(strings.DuplicateWastedBytes)}. " +
                      $"(Based on {strings.SamplingCoverage:P1} sampling coverage; interpret with caution at low coverage.)",
            Recommendation: "Consider string.Intern for frequently duplicated strings, " +
                            "or use a string→int dictionary for repeated tokens.",
            Tags: ["strings", "duplication", "memory"],
            MetricValue: strings.DuplicationRatio,
            MetricUnit: "ratio"));
    }

    /// <summary>
    /// Cross-correlates LOH array pressure with LOH fragmentation — when large arrays dominate
    /// the LOH AND the LOH is already fragmented, it is a compounding risk.
    /// </summary>
    private static void DetectLohArrayPressure(
        List<InsightFinding> findings,
        ArrayDomainResult? arrays,
        LohFragmentationDomainResult? lohFrag)
    {
        if (arrays is null || arrays.LohArrayBytes < LohArrayPressureThreshold)
            return;

        bool fragCorrelation = lohFrag is not null &&
                               lohFrag.FragmentationPercent >= LohFragWarningPct;

        FindingSeverity sev = fragCorrelation ? FindingSeverity.Warning : FindingSeverity.Info;

        string fragNote = fragCorrelation
            ? $" Combined with LOH fragmentation of {lohFrag!.FragmentationPercent:F1}%, " +
              "this indicates compounding heap pressure."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "Large arrays are a major LOH contributor",
            Evidence: $"{FormatBytes(arrays.LohArrayBytes)} of LOH memory is held by " +
                      $"{arrays.LohArrayCount:N0} large array object(s).{fragNote}",
            Recommendation: "Rent large arrays from ArrayPool<T> instead of allocating them directly. " +
                            "Pool buffers prevent LOH growth and associated fragmentation.",
            Tags: ["loh", "arrays", "pooling"],
            MetricValue: (double)arrays.LohArrayBytes,
            MetricUnit: "bytes"));
    }

    /// <summary>
    /// Detects fire-and-forget async patterns: a single originating method has > 100 suspended
    /// state machines currently alive. This is a classic async leak pattern.
    /// </summary>
    private static void DetectAsyncStateMachineFireAndForget(
        List<InsightFinding> findings,
        AsyncStateMachineDomainResult? stateMachines,
        MemoryDomainResult? memory)
    {
        if (stateMachines is null || stateMachines.SuspendedMethodMap.Count == 0)
            return;

        // Find the method with the highest suspended count
        SuspendedMethodEntry? worst = null;
        for (int i = 0; i < stateMachines.SuspendedMethodMap.Count; i++)
        {
            SuspendedMethodEntry e = stateMachines.SuspendedMethodMap[i];
            if (worst is null || e.SuspendedCount > worst.SuspendedCount)
                worst = e;
        }

        if (worst is null || worst.SuspendedCount < SuspendedMethodFireForgetThreshold)
            return;

        // Cross-reference: how much do state machines contribute to total managed memory?
        string memNote = string.Empty;
        if (memory is not null && memory.TotalBytes > 0)
        {
            double smPct = stateMachines.TotalStateMachineBytes * 100.0 / (double)memory.TotalBytes;
            if (smPct >= 1.0)
                memNote = $" State machines account for {smPct:F1}% of total managed heap.";
        }

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Async",
            Severity: FindingSeverity.Warning,
            Title: "Likely fire-and-forget async accumulation detected",
            Evidence: $"{worst.SuspendedCount:N0} suspended state machine instances for " +
                      $"'{worst.DeclaringType}.{worst.MethodName}' — potential fire-and-forget pattern. " +
                      $"Total state machines: {stateMachines.TotalStateMachines:N0} " +
                      $"({FormatBytes(stateMachines.TotalStateMachineBytes)}).{memNote}",
            Recommendation: "Ensure async methods are always awaited. " +
                            "Use structured concurrency (Task.WhenAll / CancellationToken propagation) " +
                            "to prevent unbounded accumulation of suspended state machines.",
            Tags: ["async", "state-machine", "fire-and-forget", "memory-leak"],
            MetricValue: worst.SuspendedCount,
            MetricUnit: "instances"));
    }

    /// <summary>
    /// Warns when the dead target ratio for weak references exceeds 50%.
    /// High dead-target ratios mean the application is holding many stale wrappers —
    /// objects that are gone but whose <see cref="WeakReference{T}"/> wrappers remain allocated.
    /// </summary>
    private static void DetectStaleWeakReferenceAccumulation(
        List<InsightFinding> findings,
        WeakReferenceDomainResult? weakRef)
    {
        if (weakRef is null || weakRef.TotalWeakHandles == 0)
            return;

        if (weakRef.DeadTargetRatio < WeakRefDeadTargetWarningRatio)
            return;

        FindingSeverity sev = weakRef.DeadTargetRatio >= 0.80
            ? FindingSeverity.Warning
            : FindingSeverity.Info;

        string staleNote = weakRef.StaleWrapperCount > 0
            ? $" {weakRef.StaleWrapperCount:N0} stale WeakReference<T> wrapper object(s) detected."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "High dead-target ratio in weak GC handles",
            Evidence: $"{weakRef.DeadWeakTargets:N0} of {weakRef.TotalWeakHandles:N0} weak handles " +
                      $"({weakRef.DeadTargetRatio:P0}) point to already-collected objects.{staleNote}",
            Recommendation: "Review code that creates WeakReference<T> objects and cleans up stale entries. " +
                            "ConditionalWeakTable<TKey,TValue> manages lifetime automatically; " +
                            "custom caches using WeakReference need periodic compaction.",
            Tags: ["weak-reference", "stale", "gc-handles"],
            MetricValue: weakRef.DeadTargetRatio,
            MetricUnit: "ratio"));
    }

    /// <summary>
    /// Surfaces address space pressure risk and near-full ephemeral segments.
    /// The <see cref="SegmentReservationDomainResult.AddressSpacePressureRisk"/> flag is produced
    /// by the SegmentReservationAnalyzer; the InsightEngine promotes it to a ranked finding.
    /// </summary>
    private static void DetectAddressSpacePressure(
        List<InsightFinding> findings,
        SegmentReservationDomainResult? segReservation,
        HeapTopologyDomainResult? segments)
    {
        if (segReservation is null)
            return;

        // Address space exhaustion risk (set by the analyzer when reserved > threshold)
        if (segReservation.AddressSpacePressureRisk)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Managed heap virtual address space pressure",
                Evidence: $"Reserved: {FormatBytes(segReservation.TotalReservedBytes)}, " +
                          $"committed: {FormatBytes(segReservation.TotalCommittedBytes)}, " +
                          $"ratio: {segReservation.ReservedToCommittedRatio:F1}×. " +
                          $"Reason: {segReservation.PressureRiskReason}.",
                Recommendation: "On 32-bit processes, reserved memory > 1.5 GB risks address space exhaustion. " +
                                "Consider migrating to 64-bit, enabling Server GC, or reducing the number of " +
                                "GC segments via heap hard limit configuration.",
                Tags: ["segment", "address-space", "virtual-memory"],
                MetricValue: (double)segReservation.TotalReservedBytes,
                MetricUnit: "bytes"));
        }

        // Ephemeral segment fill critical (> 90% full)
        if (segReservation.EphemeralSegmentCount > 0 &&
            segReservation.AvgEphemeralFillPct >= EphemeralFillCriticalPct)
        {
            // Cross-correlate with segment count from segments result
            string segNote = segments is not null
                ? $" {segments.TotalSegments} total segments across heap."
                : string.Empty;

            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Ephemeral GC segments critically full",
                Evidence: $"Average ephemeral segment fill: {segReservation.AvgEphemeralFillPct:F1}% " +
                          $"across {segReservation.EphemeralSegmentCount} ephemeral segment(s).{segNote} " +
                          $"When ephemeral segments are nearly full, GC must commit new segments or trigger full compaction.",
                Recommendation: "Reduce Gen0/Gen1 object survival rates. " +
                                "Review long-lived objects promoted from Gen1 to Gen2 — " +
                                "they fragment ephemeral segments and force premature full GC.",
                Tags: ["segment", "ephemeral", "gc-pressure"],
                MetricValue: segReservation.AvgEphemeralFillPct,
                MetricUnit: "% full"));
        }
    }

    /// <summary>
    /// Detects dynamic assembly accumulation. Dynamic assemblies are generated at runtime and
    /// never collected (in .NET 4.x) or collected only when their AssemblyLoadContext is freed.
    /// A growing count indicates an ongoing leak of code-gen or reflection-emit patterns.
    /// </summary>
    private static void DetectDynamicAssemblyAccumulation(
        List<InsightFinding> findings,
        ModuleDomainResult? appDomains)
    {
        if (appDomains is null || appDomains.TotalDynamicModules < DynamicModuleWarning)
            return;

        string anonNote = appDomains.AnonymousModuleCount > 0
            ? $" {appDomains.AnonymousModuleCount:N0} anonymous module(s) detected (no file path)."
            : string.Empty;

        FindingSeverity sev = appDomains.TotalDynamicModules > 100
            ? FindingSeverity.Warning
            : FindingSeverity.Info;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Modules",
            Severity: sev,
            Title: "High dynamic/anonymous assembly count detected",
            Evidence: $"{appDomains.TotalDynamicModules:N0} dynamic module(s) found " +
                      $"across {appDomains.TotalDomains} AppDomain(s).{anonNote}",
            Recommendation: "Dynamic assemblies created by Expression.Compile, Reflection.Emit, or " +
                            "code generators accumulate until their AssemblyLoadContext is unloaded. " +
                            "Cache compiled expressions / delegates; prefer collectible AssemblyLoadContext " +
                            "for plugin or script scenarios.",
            Tags: ["modules", "dynamic-assembly", "reflection", "memory-leak"],
            MetricValue: appDomains.TotalDynamicModules,
            MetricUnit: "modules"));
    }

    /// <summary>
    /// Cross-correlates JIT heap size with active thread count.
    /// A large JIT heap with many threads indicates concurrent JIT warm-up cost.
    /// </summary>
    private static void DetectJitHeapBloat(
        List<InsightFinding> findings,
        JitDomainResult? jit,
        ThreadDomainResult? threads)
    {
        if (jit is null || jit.TotalJitHeapBytes < JitHeapBloatThreshold)
            return;

        // The per-analyzer JitFindingGenerator already warns on this; the InsightEngine only
        // emits a cross-cutting finding when thread data correlates (many threads → JIT contention).
        bool highThreadCount = threads is not null && threads.AliveThreadCount > 100;
        if (!highThreadCount)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Performance",
            Severity: FindingSeverity.Warning,
            Title: "Large JIT heap combined with high thread count may indicate JIT warm-up contention",
            Evidence: $"JIT code heap: {FormatBytes(jit.TotalJitHeapBytes)} across " +
                      $"{jit.JitManagerCount} JIT manager(s). " +
                      $"Alive threads: {threads!.AliveThreadCount:N0}. " +
                      $"Concurrent JIT compilation under heavy load can increase latency spikes.",
            Recommendation: "Use ReadyToRun (dotnet publish -r ...) or NativeAOT to pre-compile hot paths. " +
                            "Profile startup with dotnet-trace to identify cold JIT paths.",
            Tags: ["jit", "threads", "performance", "warm-up"],
            MetricValue: (double)jit.TotalJitHeapBytes,
            MetricUnit: "bytes"));
    }

    /// <summary>
    /// Correlates boxing pressure with GC generation distribution.
    /// Excessive boxing creates short-lived objects that inflate Gen0 and increase promotion pressure.
    /// </summary>
    private static void DetectBoxingGCCorrelation(
        List<InsightFinding> findings,
        BoxingDomainResult? boxing,
        GCGenerationDomainResult? gcGen)
    {
        if (boxing is null || boxing.TotalBoxedObjects == 0)
            return;

        // High Gen0 is inferred from AllocationPattern (Gen0CountPct) if GCGeneration isn't available,
        // or from GCGenerationDomainResult by computing gen0 fraction from raw counts.
        bool highGen0Pct = gcGen is not null &&
                           gcGen.TotalObjects > 0 &&
                           (gcGen.Gen0Objects * 100.0 / gcGen.TotalObjects) >= 40.0;
        bool highBoxedEnums = boxing.BoxedEnumCount > 10_000;

        if (!highBoxedEnums && !highGen0Pct)
            return;

        string gen0Note = highGen0Pct
            ? $" Gen0 holds {gcGen!.Gen0Objects * 100.0 / gcGen.TotalObjects:F1}% of managed heap objects — consistent with high transient boxing."
            : string.Empty;

        string enumNote = highBoxedEnums
            ? $" {boxing.BoxedEnumCount:N0} boxed enum instances found."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "Boxing pressure correlates with elevated Gen0 churn",
            Evidence: $"{boxing.TotalBoxedObjects:N0} boxed value type instances " +
                      $"({FormatBytes(boxing.TotalBoxedBytes)}).{enumNote}{gen0Note}",
            Recommendation: "Replace object/non-generic collection APIs with generic alternatives " +
                            "(List<T>, Dictionary<TKey,TValue>). Use enum-typed parameters instead of object.",
            Tags: ["boxing", "gen0", "gc-pressure", "value-type"],
            MetricValue: boxing.TotalBoxedObjects,
            MetricUnit: "objects"));
    }

    /// <summary>
    /// Correlates JitAnalyzer's per-module active-frame heatmap with ModuleAnalyzer's own
    /// per-module size/version-conflict data. Neither analyzer can produce this alone: JitAnalyzer
    /// has no module size/conflict data, and ModuleAnalyzer never walks thread stacks.
    /// </summary>
    private static void DetectJitModuleHotspot(
        List<InsightFinding> findings,
        JitDomainResult? jit,
        ModuleDomainResult? modules)
    {
        if (jit is null || modules is null || jit.TopActiveModulesByFrameHits.Count == 0)
            return;

        NameCountEntry topModule = jit.TopActiveModulesByFrameHits[0];
        if (topModule.Count < JitModuleHotspotMinFrameHits)
            return;

        LoadedModuleSnapshot? topModuleSizeMatch = FindModuleByName(modules.TopModulesBySize, topModule.Name);
        bool topModuleInConflict = ContainsModuleName(modules.ConflictingAssemblyNames, topModule.Name);

        if (topModuleSizeMatch is null && !topModuleInConflict)
            return; // no cross-analyzer signal beyond what JitSectionBuilder's own heatmap already shows

        int rowCount = Math.Min(jit.TopActiveModulesByFrameHits.Count, 5);
        var rows = new List<IReadOnlyList<object?>>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            NameCountEntry entry = jit.TopActiveModulesByFrameHits[i];
            LoadedModuleSnapshot? sizeMatch = FindModuleByName(modules.TopModulesBySize, entry.Name);
            bool inConflict = ContainsModuleName(modules.ConflictingAssemblyNames, entry.Name);

            rows.Add(new object?[]
            {
                entry.Name,
                entry.Count,
                sizeMatch is not null ? FormatBytes(sizeMatch.Size) : "n/a",
                inConflict ? "Yes" : "No",
            });
        }

        var evidenceTable = new FindingEvidenceTable(
            "Per-module JIT stack heatmap (top active modules)",
            ["Module", "Active JIT Frames", "Module Size", "Version Conflict"],
            rows);

        string sizeNote = topModuleSizeMatch is not null ? $" and is {FormatBytes(topModuleSizeMatch.Size)} on disk" : string.Empty;
        string conflictNote = topModuleInConflict ? " and is involved in an assembly version conflict" : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Performance",
            Severity: FindingSeverity.Info,
            Title: "Module with heavy active JIT stack presence also flagged by module analysis",
            Evidence: $"Module '{topModule.Name}' accounts for {topModule.Count:N0} active JIT stack " +
                      $"frames{sizeNote}{conflictNote}.",
            Recommendation: "Correlate this module's size/version-conflict status with the JIT stack " +
                            "heatmap to prioritize ReadyToRun/NativeAOT precompilation or dependency " +
                            "deduplication for this assembly.",
            Tags: ["jit", "modules", "cross-analyzer"],
            MetricValue: topModule.Count,
            MetricUnit: "frames",
            EvidenceTables: [evidenceTable]));
    }

    private static LoadedModuleSnapshot? FindModuleByName(IReadOnlyList<LoadedModuleSnapshot> modules, string name)
    {
        for (int i = 0; i < modules.Count; i++)
        {
            if (string.Equals(modules[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return modules[i];
        }
        return null;
    }

    private static bool ContainsModuleName(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Raises a Critical finding when fatal exception type(s) — OOM, SOE, EEE, AV —
    /// are found on the managed heap, even when no exception is currently active.
    /// Their presence indicates the process experienced a near-fatal event prior to the dump.
    /// </summary>
    private static void DetectFatalExceptionOnHeap(
        List<InsightFinding> findings,
        CrashDomainResult? crash)
    {
        if (crash is null || crash.TotalExceptions == 0)
            return;

        var fatalFound = new List<string>();
        foreach (KeyValuePair<string, int> kv in crash.ExceptionTypeCounts)
        {
            if (FatalExceptionTypes.Contains(kv.Key))
                fatalFound.Add($"{kv.Key} ×{kv.Value}");
        }

        if (fatalFound.Count == 0)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Crash",
            Severity: FindingSeverity.Critical,
            Title: "Fatal exception type(s) found on managed heap",
            Evidence: $"Fatal exception object(s) present: {string.Join(", ", fatalFound)}. " +
                      "These exception types indicate a previous near-fatal process event.",
            Recommendation: "OutOfMemoryException: reduce allocations, enable large address space, or scale out. " +
                            "StackOverflowException: review recursive call depth and unbounded recursion. " +
                            "ExecutionEngineException: indicates CLR corruption — check native interop and upgrade runtime. " +
                            "AccessViolationException: unsafe code or native interop writing beyond allocated buffers.",
            Tags: ["crash", "fatal-exception", "oom", "soe", "critical"]));
    }

    /// <summary>
    /// Cross-correlates event leak subscriptions with high Gen2 and finalizer queue pressure.
    /// Emits a finding only when at least one other signal is present to avoid duplicate noise.
    /// </summary>
    private static void DetectEventLeakPattern(
        List<InsightFinding> findings,
        EventLeakDomainResult? eventLeaks,
        GCGenerationDomainResult? gcGen,
        FinalizableObjectDomainResult? finalizable)
    {
        if (eventLeaks is null || eventLeaks.TotalEventLeakInstances == 0)
            return;

        bool highGen2 = gcGen is not null && gcGen.Gen2Pct >= 40.0;
        bool highFinalizer = finalizable is not null && finalizable.FinalizerQueueCount >= FinalizerQueueWarning;

        // Only emit cross-cutting finding when at least one other signal correlates.
        if (!highGen2 && !highFinalizer)
            return;

        string gen2Note = highGen2
            ? $" Gen2 holds {gcGen!.Gen2Pct:F1}% of managed heap — event subscribers may be keeping objects alive across GC generations."
            : string.Empty;

        string finNote = highFinalizer
            ? $" Finalizer queue has {finalizable!.FinalizerQueueCount:N0} objects — some may be retained by event subscription chains."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "Event subscriptions likely amplify Gen2 and finalizer queue pressure",
            Evidence: $"{eventLeaks.TotalEventLeakInstances:N0} event-leak group(s) with " +
                      $"{eventLeaks.TotalSubscribers:N0} total subscribers detected.{gen2Note}{finNote}",
            Recommendation: "Unsubscribe event handlers in Dispose() to release publisher references. " +
                            "Use WeakEventManager or weak-reference delegate patterns for long-lived publishers. " +
                            "Review PropertyChanged and custom event patterns for unbounded subscription growth.",
            Tags: ["event-leak", "gen2", "finalizer", "cross-cutting"],
            MetricValue: eventLeaks.TotalSubscribers,
            MetricUnit: "subscribers"));
    }

    /// <summary>
    /// Detects classic DataTable/DataColumn/DataRow accumulation: DataColumn objects carry
    /// finalizers and delay collection. When combined with large DataRow counts on the heap,
    /// this indicates DataTable instances are not being disposed.
    /// </summary>
    private static void DetectDataTableLifecyclePattern(
        List<InsightFinding> findings,
        FinalizableObjectDomainResult? finalizable,
        MemoryDomainResult? memory)
    {
        if (finalizable is null && memory is null)
            return;

        long dataColumnFinalizer = 0;
        long dataTableFinalizer = 0;

        if (finalizable is not null)
        {
            for (int i = 0; i < finalizable.TopFinalizableTypesByGen2Count.Count; i++)
            {
                TypeGenerationProfile profile = finalizable.TopFinalizableTypesByGen2Count[i];
                if (profile.TypeName.Contains("DataColumn", StringComparison.OrdinalIgnoreCase))
                    dataColumnFinalizer += profile.Gen2Count;
                else if (profile.TypeName.Contains("DataTable", StringComparison.OrdinalIgnoreCase))
                    dataTableFinalizer += profile.Gen2Count;
            }
            // Also check finalizer queue entries by counting objects per type
            var queueTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < finalizable.TopQueueEntriesByRetainedSize.Count; i++)
            {
                string typeName = finalizable.TopQueueEntriesByRetainedSize[i].TypeName;
                queueTypeCounts.TryGetValue(typeName, out int existing);
                queueTypeCounts[typeName] = existing + 1;
            }
            foreach (KeyValuePair<string, int> kv in queueTypeCounts)
            {
                if (kv.Key.Contains("DataColumn", StringComparison.OrdinalIgnoreCase))
                    dataColumnFinalizer += kv.Value;
                else if (kv.Key.Contains("DataTable", StringComparison.OrdinalIgnoreCase))
                    dataTableFinalizer += kv.Value;
            }
        }

        int dataRowHeap = 0;
        if (memory is not null)
        {
            for (int i = 0; i < memory.TopTypes.Count; i++)
            {
                TypeSnapshot t = memory.TopTypes[i];
                if (t.TypeName.Contains("DataRow", StringComparison.OrdinalIgnoreCase))
                    dataRowHeap += t.Count;
            }
        }

        // Trigger: DataColumn in finalizer queue AND/OR DataRow on heap in large numbers.
        if (dataColumnFinalizer < 100 && dataRowHeap < 10_000)
            return;

        var evidenceParts = new List<string>(4);
        if (dataColumnFinalizer > 0) evidenceParts.Add($"DataColumn ×{dataColumnFinalizer:N0} in finalizer queue");
        if (dataTableFinalizer > 0) evidenceParts.Add($"DataTable ×{dataTableFinalizer:N0} in finalizer queue");
        if (dataRowHeap > 0) evidenceParts.Add($"DataRow ×{dataRowHeap:N0} on heap");

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Warning,
            Title: "DataTable lifecycle pattern: large DataRow/DataColumn accumulation",
            Evidence: string.Join("; ", evidenceParts) + ". " +
                      "DataColumn objects have finalizers and do not release promptly without explicit Dispose().",
            Recommendation: "Call DataTable.Dispose() and DataSet.Dispose() when tables are no longer needed. " +
                            "Avoid sharing DataTable instances across request scopes. " +
                            "Consider replacing DataTable/DataSet with strongly typed models to eliminate finalizer overhead.",
            Tags: ["datatable", "datarow", "finalizer", "memory-leak", "dispose"],
            MetricValue: dataColumnFinalizer + dataRowHeap,
            MetricUnit: "objects"));
    }

    /// <summary>
    /// Checks the top heap types for well-known problematic accumulation patterns
    /// that indicate specific framework or library bugs/anti-patterns.
    /// Currently detects: TdsParser async closure accumulation (ADO.NET .NET Framework).
    /// </summary>
    private static void DetectKnownLeakPatterns(
        List<InsightFinding> findings,
        MemoryDomainResult? memory)
    {
        if (memory is null)
            return;

        // TdsParser closure accumulation — known .NET Framework 4.x System.Data.SqlClient issue.
        // Async SqlCommand continuations capture closures that linger when connections are not disposed promptly.
        int tdsClosureCount = 0;
        for (int i = 0; i < memory.TopTypes.Count; i++)
        {
            TypeSnapshot t = memory.TopTypes[i];
            if (t.TypeName.Contains("TdsParser", StringComparison.OrdinalIgnoreCase) &&
                (t.TypeName.Contains("DisplayClass", StringComparison.OrdinalIgnoreCase) ||
                 t.TypeName.Contains("c__", StringComparison.OrdinalIgnoreCase) ||
                 t.TypeName.Contains("<>", StringComparison.OrdinalIgnoreCase)))
            {
                tdsClosureCount += t.Count;
            }
        }

        if (tdsClosureCount >= 1_000)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "SqlClient TdsParser closure accumulation — known ADO.NET pattern",
                Evidence: $"{tdsClosureCount:N0} TdsParser compiler-generated closure object(s) on heap. " +
                          "In .NET Framework System.Data.SqlClient, async SqlCommand continuations can accumulate " +
                          "closures when connections are not disposed promptly or when async paths are abandoned.",
                Recommendation: "Upgrade to Microsoft.Data.SqlClient NuGet package which has resolved this issue. " +
                                "Ensure SqlConnection and SqlCommand are disposed immediately after use via using statements. " +
                                "Avoid fire-and-forget async ADO.NET operations on .NET Framework.",
                Tags: ["ado-net", "sqlclient", "closure", "memory-leak", "known-pattern"],
                MetricValue: tdsClosureCount,
                MetricUnit: "objects"));
        }

        // Reflection metadata accumulation: RuntimeMethodInfo / RuntimePropertyInfo > 50 k
        // indicates hot-path reflection (Type.GetMethod / GetProperty) without result caching.
        int reflectionCount = 0;
        for (int i = 0; i < memory.TopTypes.Count; i++)
        {
            TypeSnapshot t = memory.TopTypes[i];
            if (t.TypeName is "System.Reflection.RuntimeMethodInfo" or
                "System.Reflection.RuntimePropertyInfo" or
                "System.Reflection.RuntimeFieldInfo" or
                "System.Reflection.RuntimeConstructorInfo")
            {
                reflectionCount += t.Count;
            }
        }

        if (reflectionCount >= 50_000)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: "Uncached reflection metadata accumulation detected",
                Evidence: $"{reflectionCount:N0} RuntimeMethodInfo/RuntimePropertyInfo/RuntimeFieldInfo object(s) on heap. " +
                          "Each call to Type.GetMethod(), GetProperty(), or GetField() allocates a new metadata wrapper " +
                          "unless results are cached.",
                Recommendation: "Cache reflection results in static dictionaries keyed by Type. " +
                                "Use compiled Expression trees or source generators (System.Text.Json, Mapster) " +
                                "to replace runtime reflection in hot paths.",
                Tags: ["reflection", "memory-leak", "performance", "known-pattern"],
                MetricValue: reflectionCount,
                MetricUnit: "objects"));
        }
    }

    /// <summary>
    /// Cross-references the memory analyzer's top types by size with the GC generation
    /// analyzer's per-type generation distribution. Neither analyzer alone shows this: Memory
    /// ranks types by total bytes but has no generation breakdown, while GCGeneration has the
    /// breakdown but doesn't rank by total size. A large type that is almost entirely stuck in
    /// Gen2 is a strong long-lived-leak candidate rather than ordinary working-set memory.
    /// </summary>
    private static void DetectMemoryTypeGenerationCorrelation(
        List<InsightFinding> findings,
        MemoryDomainResult? memory,
        GCGenerationDomainResult? gcGen)
    {
        if (memory is null || gcGen is null || gcGen.PerTypeGenerationProfiles is not { Count: > 0 } profiles)
            return;

        var profileByType = new Dictionary<string, TypeGenerationProfile>(profiles.Count, StringComparer.Ordinal);
        for (int i = 0; i < profiles.Count; i++)
            profileByType[profiles[i].TypeName] = profiles[i];

        int scanCount = Math.Min(memory.TopTypes.Count, MemoryGenerationCorrelationTopTypesScanned);
        var matches = new List<(TypeSnapshot Snapshot, TypeGenerationProfile Profile, double Gen2FractionPct)>();

        for (int i = 0; i < scanCount; i++)
        {
            TypeSnapshot snapshot = memory.TopTypes[i];
            if (snapshot.TotalBytes < MemoryGenerationCorrelationMinBytes)
                continue;

            if (!profileByType.TryGetValue(snapshot.TypeName, out TypeGenerationProfile profile))
                continue;

            long totalCounted = profile.Gen0Count + profile.Gen1Count + profile.Gen2Count + profile.LohCount;
            if (totalCounted == 0)
                continue;

            double gen2FractionPct = profile.Gen2Count * 100.0 / totalCounted;
            if (gen2FractionPct >= MemoryGenerationCorrelationGen2FractionPct)
                matches.Add((snapshot, profile, gen2FractionPct));
        }

        if (matches.Count == 0)
            return;

        matches.Sort(static (a, b) => b.Snapshot.TotalBytes.CompareTo(a.Snapshot.TotalBytes));

        ulong worstBytes = matches[0].Snapshot.TotalBytes;
        FindingSeverity sev = worstBytes >= MemoryGenerationCorrelationCriticalBytes
            ? FindingSeverity.Warning
            : FindingSeverity.Info;

        int rowCount = Math.Min(matches.Count, 5);
        var rows = new List<IReadOnlyList<object?>>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            (TypeSnapshot snapshot, TypeGenerationProfile profile, double gen2FractionPct) = matches[i];
            rows.Add(new object?[]
            {
                snapshot.TypeName,
                FormatBytes(snapshot.TotalBytes),
                profile.Gen0Count,
                profile.Gen1Count,
                profile.Gen2Count,
                profile.LohCount,
                $"{gen2FractionPct:F1}%",
            });
        }

        var evidenceTable = new FindingEvidenceTable(
            "Top memory-consuming types stuck in Gen2 (size × generation cross-reference)",
            ["Type", "Total Bytes", "Gen0", "Gen1", "Gen2", "LOH", "Gen2 %"],
            rows);

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: sev,
            Title: "Large heap types are almost entirely long-lived (Gen2)",
            Evidence: $"{matches.Count:N0} of the top {scanCount} memory-consuming type(s) are ≥ " +
                      $"{MemoryGenerationCorrelationGen2FractionPct:F0}% Gen2. Largest: '{matches[0].Snapshot.TypeName}' " +
                      $"at {FormatBytes(matches[0].Snapshot.TotalBytes)} ({matches[0].Gen2FractionPct:F1}% Gen2).",
            Recommendation: "High Gen2 residency for a top-size type usually means the instances are held by " +
                            "long-lived roots (statics, caches, event subscriptions) rather than transient churn. " +
                            "Cross-check the Dominator/GC Root analyzer findings for these type names to locate the " +
                            "retaining reference chain.",
            Tags: ["memory", "gc-generation", "cross-analyzer", "long-lived"],
            MetricValue: (double)worstBytes,
            MetricUnit: "bytes",
            EvidenceTables: [evidenceTable]));
    }

    /// <summary>
    /// Cross-references the memory analyzer's own top-types-by-size ranking with the string
    /// analyzer's duplication data. The Memory section only ever sees <c>System.String</c> as one
    /// more ranked type entry — it has no visibility into duplication. The String section computes
    /// duplication in isolation and never learns whether that duplication is happening inside a
    /// top-ranked heap consumer. Combining the two turns "String is duplicated somewhere" into
    /// "String is your #N largest type, and here's why."
    /// </summary>
    private static void DetectStringMemoryConcentration(
        List<InsightFinding> findings,
        MemoryDomainResult? memory,
        StringDomainResult? strings)
    {
        if (memory is null || strings is null || strings.TopDuplicates.Count == 0)
            return;

        if (strings.DuplicateWastedBytes < StringMemoryCorrelationMinWastedBytes)
            return;

        int stringRank = -1;
        ulong stringTypeBytes = 0;
        int scanCount = Math.Min(memory.TopTypes.Count, StringMemoryCorrelationMaxRank);
        for (int i = 0; i < scanCount; i++)
        {
            if (string.Equals(memory.TopTypes[i].TypeName, "System.String", StringComparison.Ordinal))
            {
                stringRank = i + 1;
                stringTypeBytes = memory.TopTypes[i].TotalBytes;
                break;
            }
        }

        if (stringRank < 0)
            return;

        int rowCount = Math.Min(strings.TopDuplicates.Count, 5);
        var rows = new List<IReadOnlyList<object?>>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            DuplicateStringSnapshot d = strings.TopDuplicates[i];
            rows.Add(new object?[] { d.Preview, d.Count, FormatBytes(d.WastedBytes) });
        }

        var evidenceTable = new FindingEvidenceTable(
            "Top duplicate string values (System.String is a top memory-section consumer)",
            ["Preview", "Occurrences", "Wasted Bytes"],
            rows);

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "String data is a top heap consumer with significant duplication",
            Evidence: $"System.String ranks #{stringRank} in the Memory section by size " +
                      $"({FormatBytes(stringTypeBytes)}). {FormatBytes(strings.DuplicateWastedBytes)} of managed " +
                      $"heap memory is wasted across {strings.DuplicatePatternCount:N0} duplicate string pattern(s).",
            Recommendation: "Intern or cache the frequently duplicated string values below rather than allocating " +
                            "fresh instances per request. See the String Analysis section for full duplicate detail.",
            Tags: ["memory", "strings", "duplication", "cross-analyzer"],
            MetricValue: (double)strings.DuplicateWastedBytes,
            MetricUnit: "bytes",
            EvidenceTables: [evidenceTable]));
    }

    /// <summary>
    /// Raises a Warning when a large number of operational timeout exceptions are present on the heap,
    /// indicating systematic connection pool exhaustion, network instability, or slow dependencies.
    /// </summary>
    private static void DetectRecurringTimeoutPattern(
        List<InsightFinding> findings,
        CrashDomainResult? crash)
    {
        if (crash is null || crash.TotalExceptions == 0)
            return;

        int timeoutCount = 0;
        int objectDisposedCount = 0;
        int taskCanceledCount = 0;

        foreach (KeyValuePair<string, int> kv in crash.ExceptionTypeCounts)
        {
            if (kv.Key is "System.TimeoutException" or
                "System.OperationCanceledException" or
                "System.Net.WebException" ||
                kv.Key.EndsWith("TimeoutException", StringComparison.OrdinalIgnoreCase))
            {
                timeoutCount += kv.Value;
            }
            else if (kv.Key is "System.ObjectDisposedException")
            {
                objectDisposedCount += kv.Value;
            }
            else if (kv.Key is "System.Threading.Tasks.TaskCanceledException" or
                     "System.OperationCanceledException")
            {
                taskCanceledCount += kv.Value;
            }
        }

        if (timeoutCount < 10)
            return;

        string disposedNote = objectDisposedCount >= 5
            ? $" Combined with {objectDisposedCount:N0} ObjectDisposedException(s), this may indicate connections " +
              "being used after pool exhaustion or channel faults."
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Exceptions",
            Severity: timeoutCount >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
            Title: "Recurring timeout exceptions indicate dependency instability",
            Evidence: $"{timeoutCount:N0} timeout/cancellation exception(s) on heap.{disposedNote}",
            Recommendation: "Review connection pool sizing, query/call timeouts, and retry policies. " +
                            "High timeout counts often indicate: (1) DB connection pool exhaustion, " +
                            "(2) downstream service latency, or (3) network intermittency. " +
                            "Correlate with DB connections on heap and WCF channel state.",
            Tags: ["timeout", "exceptions", "connection-pool", "performance"],
            MetricValue: timeoutCount,
            MetricUnit: "exceptions"));
    }

    // ── Infrastructure detection rules ────────────────────────────────────────

    private static void DetectDbConnectionLeak(
        List<InsightFinding> findings,
        DbConnectionDomainResult? dbConn,
        CrashDomainResult? crash)
    {
        if (dbConn is null || !dbConn.ConnectionsFound) return;

        // Already covered by DbConnectionFindingGenerator for the direct findings.
        // InsightEngine cross-correlates with timeout/crash exceptions.
        if (dbConn.TotalConnections < 10) return;

        int timeoutCount = 0;
        if (crash?.ExceptionTypeCounts is not null)
        {
            foreach (KeyValuePair<string, int> kv in crash.ExceptionTypeCounts)
            {
                if (kv.Key.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key is "System.InvalidOperationException") // "Timeout expired waiting for a pool"
                    timeoutCount += kv.Value;
            }
        }

        if (dbConn.OpenConnections >= 10 && timeoutCount >= 5)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Infrastructure",
                Severity: FindingSeverity.Critical,
                Title: "DB connection pool exhaustion suspected",
                Evidence: $"{dbConn.OpenConnections:N0} open connections on heap combined with " +
                          $"{timeoutCount:N0} timeout/InvalidOperation exception(s) strongly indicates " +
                          "connection pool exhaustion.",
                Recommendation: "Verify Max Pool Size in the connection string. " +
                                "Ensure all SqlConnection objects are disposed (use 'using'). " +
                                "Check for long-running transactions holding connections open. " +
                                "Consider connection pool monitoring via Performance Counters.",
                Tags: ["infrastructure", "connections", "timeout", "pool-exhaustion"],
                MetricValue: dbConn.OpenConnections,
                MetricUnit: "open connections"));
        }
    }

    private static void DetectWcfChannelFault(
        List<InsightFinding> findings,
        WcfChannelDomainResult? wcf,
        CrashDomainResult? crash)
    {
        if (wcf is null || !wcf.WcfPresent) return;
        if (wcf.FaultedChannels == 0) return;

        // Cross-correlate faulted channels with ObjectDisposedException or CommunicationException
        int commExCount = 0;
        if (crash?.ExceptionTypeCounts is not null)
        {
            foreach (KeyValuePair<string, int> kv in crash.ExceptionTypeCounts)
            {
                if (kv.Key.StartsWith("System.ServiceModel.", StringComparison.Ordinal) ||
                    kv.Key is "System.ObjectDisposedException")
                    commExCount += kv.Value;
            }
        }

        if (commExCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Infrastructure",
                Severity: FindingSeverity.Critical,
                Title: "WCF faulted channels combined with communication exceptions",
                Evidence: $"{wcf.FaultedChannels:N0} faulted WCF channel(s) on heap with " +
                          $"{commExCount:N0} WCF/communication exception(s). " +
                          "Calling any method on a faulted channel throws CommunicationObjectFaultedException.",
                Recommendation: "In the catch block for any WCF exception, call channel.Abort() instead of Close(). " +
                                "Create a new channel per operation. Cache ChannelFactory<T>, not the channel itself.",
                Tags: ["infrastructure", "wcf", "fault", "communication"],
                MetricValue: wcf.FaultedChannels,
                MetricUnit: "faulted channels"));
        }
    }

    private static void DetectHttpClientAccumulation(
        List<InsightFinding> findings,
        HttpObjectDomainResult? http)
    {
        if (http is null || !http.HttpObjectsFound) return;

        // Only flag when HttpClient and HttpWebResponse are both present, suggesting mixed API usage.
        if (http.HttpClientCount >= 3 && http.HttpWebResponseCount >= 10)
        {
            findings.Add(new InsightFinding(
                Analyzer: Source,
                Category: "Infrastructure",
                Severity: FindingSeverity.Warning,
                Title: "Mixed HTTP API usage: HttpClient and HttpWebResponse both present",
                Evidence: $"{http.HttpClientCount:N0} HttpClient instance(s) and " +
                          $"{http.HttpWebResponseCount:N0} HttpWebResponse object(s) found simultaneously. " +
                          "Mixed usage suggests an incomplete migration from HttpWebRequest to HttpClient.",
                Recommendation: "Consolidate all HTTP calls to HttpClient/IHttpClientFactory. " +
                                "HttpWebRequest and HttpWebResponse are legacy and do not benefit from " +
                                "modern connection pooling or HTTP/2 support.",
                Tags: ["infrastructure", "http", "httpclient", "legacy"],
                MetricValue: http.TotalHttpObjects,
                MetricUnit: "HTTP objects"));
        }
    }

    /// <summary>
    /// Cross-references the dominant thread-stack cluster with HangAnalyzer's blocked-thread
    /// findings. When most of the dominant cluster's threads are independently reported as
    /// waiting by HangAnalyzer, the two single-analyzer findings describe the same bottleneck —
    /// this promotes that overlap into one elevated, correlated finding instead of leaving the
    /// reader to notice the connection themselves.
    /// </summary>
    private static void DetectClusterHangCorrelation(
        List<InsightFinding> findings,
        ThreadStackClusterDomainResult? clusters,
        HangDomainResult? hang)
    {
        if (clusters is null || hang is null)
            return;
        if (clusters.TopClusters is not { Count: > 0 } topClusters)
            return;
        if (hang.TopWaitingThreads is not { Count: > 0 } waitingThreads)
            return;

        ThreadClusterSnapshot dominant = topClusters[0];
        if (dominant.SampleOsThreadIds.Count == 0)
            return;

        var waitingOsThreadIds = new HashSet<uint>();
        for (int i = 0; i < waitingThreads.Count; i++)
            waitingOsThreadIds.Add(waitingThreads[i].OSThreadId);

        int overlapCount = 0;
        for (int i = 0; i < dominant.SampleOsThreadIds.Count; i++)
        {
            if (waitingOsThreadIds.Contains(dominant.SampleOsThreadIds[i]))
                overlapCount++;
        }

        double overlapRatio = overlapCount / (double)dominant.SampleOsThreadIds.Count;
        if (overlapRatio < ClusterHangOverlapWarningRatio)
            return;

        string? dominantWaitReason = null;
        int dominantWaitReasonCount = 0;
        var waitReasonCounts = new Dictionary<string, int>();
        for (int i = 0; i < waitingThreads.Count; i++)
        {
            WaitingThreadSnapshot w = waitingThreads[i];

            bool inCluster = false;
            for (int j = 0; j < dominant.SampleOsThreadIds.Count; j++)
            {
                if (dominant.SampleOsThreadIds[j] == w.OSThreadId)
                {
                    inCluster = true;
                    break;
                }
            }
            if (!inCluster)
                continue;

            waitReasonCounts.TryGetValue(w.WaitReason, out int count);
            count++;
            waitReasonCounts[w.WaitReason] = count;
            if (count > dominantWaitReasonCount)
            {
                dominantWaitReasonCount = count;
                dominantWaitReason = w.WaitReason;
            }
        }

        double dominantPercentOfAlive = clusters.AliveThreadCount > 0
            ? dominant.Count * 100.0 / clusters.AliveThreadCount
            : 0;

        FindingSeverity sev = dominantPercentOfAlive >= 50
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        string reasonNote = dominantWaitReason is not null
            ? $", predominantly waiting on {dominantWaitReason}"
            : string.Empty;

        var reasonRows = new List<IReadOnlyList<object?>>(waitReasonCounts.Count);
        foreach (KeyValuePair<string, int> kv in waitReasonCounts)
            reasonRows.Add(new object?[] { kv.Key, kv.Value });
        reasonRows.Sort((a, b) => ((int)b[1]!).CompareTo((int)a[1]!));

        var evidenceTable = new FindingEvidenceTable(
            "Wait reasons among overlapping threads",
            ["Wait Reason", "Thread Count"],
            reasonRows);

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Threads",
            Severity: sev,
            Title: "Dominant thread-stack cluster correlates with HangAnalyzer's blocked threads",
            Evidence: $"{overlapCount} of {dominant.SampleOsThreadIds.Count} sampled threads in the dominant " +
                      $"stack cluster ({dominant.Count:N0} threads, {dominantPercentOfAlive:F1}% of alive threads) " +
                      $"are also reported as waiting by the Hang analyzer{reasonNote}. " +
                      $"Cluster signature: {dominant.Signature}",
            Recommendation: "A large group of threads sharing an identical stack and wait state strongly " +
                            "suggests a single contended resource or blocking call. Inspect the cluster's " +
                            "innermost frame together with the corresponding Hang analyzer wait details to " +
                            "identify the shared bottleneck.",
            Tags: ["thread-cluster", "hang", "blocking", "contention", "cross-analyzer"],
            MetricValue: overlapRatio,
            MetricUnit: "ratio",
            EvidenceTables: [evidenceTable]));
    }

    // ── Utilities (last block) ────────────────────────────────────────────────

    // Delegates to the shared post-run bus (AnalyzerRunResultsExtensions.GetResult<T>) so other
    // post-hoc consumers (evidence building, leak ranking) can reuse the same typed lookup.
    private static T? FindResult<T>(IReadOnlyList<AnalyzerRunResult> runs) where T : AnalyzerDomainResult
        => runs.GetResult<T>();

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024UL)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
