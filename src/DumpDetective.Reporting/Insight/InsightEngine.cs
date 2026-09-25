using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Utilities;
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
    // Every rule group has been migrated to ISynthesisRule classes (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md); the engine is pending
    // retirement. DetectStringMemoryConcentration was dropped rather than migrated — its value was
    // a table of duplicate string values, which have no SDK entity kind, and the String Analysis
    // section already reports the same duplication.
    private static readonly IReadOnlyList<IInsightRuleGroup> RuleGroups = [];

    // ── Thresholds ────────────────────────────────────────────────────────────

    // LohPressureWarningPct/CriticalPct, PohPressureWarningPct, LohFragCriticalPct,
    // ThreadBlockedWarningPct/CriticalPct, FinalizerQueueCritical, PinnedHandleWarning,
    // LohFragWarningPct, and FinalizerQueueWarning were removed here (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) alongside the Detect* methods
    // that were their only readers (DetectLohArrayPressure, DetectPinnedHandlePressure,
    // DetectEventLeakPattern) — each now lives as a private const on its own *InsightRule class
    // instead. AnalyzerFailureWarning was removed here too (same step 6.2 change) — its only
    // reader, DetectAnalyzerFailures, is now AnalyzerFailuresInsightRule's own copy; BaselineRuleGroup
    // itself is gone, since that was its last method.

    // FatalExceptionTypes was removed here too (same step 6.2 change) — its only reader,
    // DetectFatalExceptionOnHeap, is now FatalExceptionOnHeapInsightRule's own copy.

    // New thresholds for Part 4 additions. StringDuplicationWarningRatio, WeakRefDeadTargetWarningRatio,
    // EphemeralFillCriticalPct, DynamicModuleWarning, JitHeapBloatThreshold,
    // SuspendedMethodFireForgetThreshold, and LohArrayPressureThreshold were removed here (phase 5
    // step 6.2, docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) alongside the Detect*
    // methods that were their only reader — each now lives as a private const on its own
    // *InsightRule class instead.
    // ReferenceChainDominatorWarningPct/CriticalPct were removed here (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) alongside
    // DetectReferenceChainDominatorCorrelation, migrated without its evidence table — see
    // ReferenceChainDominatorCorrelationInsightRule's own remarks for why.

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
        SqlTransactionDomainResult? sqlTxn = FindResult<SqlTransactionDomainResult>(runs);
        WcfChannelDomainResult? wcf = FindResult<WcfChannelDomainResult>(runs);
        HttpObjectDomainResult? http = FindResult<HttpObjectDomainResult>(runs);
        StaticRootDomainResult? staticRoot = FindResult<StaticRootDomainResult>(runs);
        ReferenceChainDomainResult? referenceChain = FindResult<ReferenceChainDomainResult>(runs);
        LockGraphDomainResult? lockGraph = FindResult<LockGraphDomainResult>(runs);

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
            SqlTxn: sqlTxn,
            Wcf: wcf,
            Http: http,
            StaticRoot: staticRoot,
            ReferenceChain: referenceChain,
            LockGraph: lockGraph);

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
        SqlTransactionDomainResult? SqlTxn,
        WcfChannelDomainResult? Wcf,
        HttpObjectDomainResult? Http,
        StaticRootDomainResult? StaticRoot,
        ReferenceChainDomainResult? ReferenceChain,
        LockGraphDomainResult? LockGraph);

    // All 11 of this group's original methods were migrated to *InsightRule ISynthesisRule classes
    // (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2) and removed from here —
    // BaselineRuleGroup itself is gone, since it has no methods left. DetectAnalyzerFailures was the
    // last one, held longest: it read raw AnalyzerRunResult execution status, not any observation,
    // and SynthesisContext carried no run data. Closed by adding SynthesisContext.AnalyzerRunSummary
    // (a (total, failed) count pair, not the full AnalyzerRunResult list — that's internal to a
    // different assembly the SDK must not depend on) and threading it through at the one real call
    // site, SynthesisFindingEnricher.EnrichAsync, which already had runs in scope unused. See
    // AnalyzerFailuresInsightRule's own remarks for the full reasoning.

    // ── Part 4: New cross-cutting detection methods ───────────────────────────

    // ── Utilities ─────────────────────────────────────────────────────────────


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
