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
        MemoryLeakDomainResult? leak = FindResult<MemoryLeakDomainResult>(runs);
        GCHandleDomainResult? handles = FindResult<GCHandleDomainResult>(runs);
        CrashDomainResult? crash = FindResult<CrashDomainResult>(runs);
        CollectionDomainResult? collections = FindResult<CollectionDomainResult>(runs);
        StringDomainResult? strings = FindResult<StringDomainResult>(runs);
        FinalizableObjectDomainResult? finalizable = FindResult<FinalizableObjectDomainResult>(runs);

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
        MemoryLeakDomainResult? leak,
        FinalizableObjectDomainResult? finalizable)
    {
        int queueCount = finalizable?.FinalizerQueueCount ?? leak?.FinalizerQueueCount ?? 0;
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
        MemoryLeakDomainResult? leak,
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
                Recommendation: "Review the Memory Leak analyzer findings for specific types. " +
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
            Tags: ["analysis-quality", "failed-analyzer"]));
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
