using DumpDetective.Analysis.Models;
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

    // New thresholds for Part 4 additions
    private const double StringDuplicationWarningRatio = 0.50;
    private const double WeakRefDeadTargetWarningRatio = 0.50;
    private const double EphemeralFillCriticalPct = 90.0;
    private const int DynamicModuleWarning = 20;
    private const ulong JitHeapBloatThreshold = 500UL * 1024 * 1024;      // 500 MB
    private const int SuspendedMethodFireForgetThreshold = 100;
    private const ulong LohArrayPressureThreshold = 256UL * 1024 * 1024;  // 256 MB
    private const ulong GCRootLargeRetentionThreshold = 50UL * 1024 * 1024; // 50 MB

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
        SegmentAnalysisDomainResult? segments = FindResult<SegmentAnalysisDomainResult>(runs);
        ThreadDomainResult? threads = FindResult<ThreadDomainResult>(runs);
        HangDomainResult? hang = FindResult<HangDomainResult>(runs);
        AsyncTaskDomainResult? asyncTasks = FindResult<AsyncTaskDomainResult>(runs);
        RetentionDomainResult? leak = FindResult<RetentionDomainResult>(runs);
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
        AppDomainDomainResult? appDomains = FindResult<AppDomainDomainResult>(runs);
        JitDomainResult? jit = FindResult<JitDomainResult>(runs);
        BoxingDomainResult? boxing = FindResult<BoxingDomainResult>(runs);

        // Existing detection rules
        DetectLohPressure(findings, memory, gcGen, segments);
        DetectLohFragmentation(findings, lohFrag, segments);
        DetectPohGrowth(findings, segments);
        DetectThreadContention(findings, threads, hang);
        DetectFinalizerQueueBacklog(findings, threads, leak, finalizable);
        DetectPinnedHandlePressure(findings, handles, lohFrag);
        DetectActiveCrash(findings, crash);
        DetectLeakSuspicion(findings, leak, strings);
        DetectWastefulCollections(findings, collections);
        DetectOrphanedTaskAccumulation(findings, asyncTasks, threads);
        DetectAnalyzerFailures(findings, runs);

        // Part 4 — new cross-cutting detection rules
        DetectGCRootLargeRetention(findings, gcRoot);
        DetectAllocationPressureCrossCorrelation(findings, allocPattern, threads);
        DetectStringDuplicationRatio(findings, strings);
        DetectLohArrayPressure(findings, arrays, lohFrag);
        DetectAsyncStateMachineFireAndForget(findings, stateMachines, memory);
        DetectStaleWeakReferenceAccumulation(findings, weakRef);
        DetectAddressSpacePressure(findings, segReservation, segments);
        DetectDynamicAssemblyAccumulation(findings, appDomains);
        DetectJitHeapBloat(findings, jit, threads);
        DetectBoxingGCCorrelation(findings, boxing, gcGen);

        // Sort by severity descending: Critical(2) > Warning(1) > Info(0)
        findings.Sort(static (a, b) => b.Severity.CompareTo(a.Severity));
        return findings;
    }

    // ── Detection rules ───────────────────────────────────────────────────────

    private static void DetectLohPressure(
        List<InsightFinding> findings,
        MemoryDomainResult? memory,
        GCGenerationDomainResult? gcGen,
        SegmentAnalysisDomainResult? segments)
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
        SegmentAnalysisDomainResult? segments)
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
        SegmentAnalysisDomainResult? segments)
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
        RetentionDomainResult? leak,
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
        RetentionDomainResult? leak,
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
                      $"({strings.TotalStrings - strings.UniqueStrings:N0} duplicate out of {strings.TotalStrings:N0} total). " +
                      $"Wasted: {FormatBytes(strings.DuplicateWastedBytes)}.",
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
        SegmentAnalysisDomainResult? segments)
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
        AppDomainDomainResult? appDomains)
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

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static T? FindResult<T>(IReadOnlyList<AnalyzerRunResult> runs) where T : AnalyzerDomainResult
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is T typed)
                return typed;
        }
        return null;
    }

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
