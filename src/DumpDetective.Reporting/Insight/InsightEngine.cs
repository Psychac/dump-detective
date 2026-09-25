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
    private static readonly IReadOnlyList<IInsightRuleGroup> RuleGroups =
    [
        new MemoryAndRuntimeRuleGroup(),
        new CorrelationRuleGroup(),
    ];

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
    // *InsightRule class instead. JitModuleHotspotMinFrameHits stays: still read by
    // DetectJitModuleHotspot below, not yet migrated.
    private const int JitModuleHotspotMinFrameHits = 50;
    private const ulong StringMemoryCorrelationMinWastedBytes = 5UL * 1024 * 1024; // 5 MB
    private const int StringMemoryCorrelationMaxRank = 10;
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

    // 10 of this group's original 11 methods were migrated to *InsightRule ISynthesisRule classes
    // (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2) and removed from here to
    // avoid double-firing. DetectJitModuleHotspot is the sole survivor — deliberately held: it needs
    // a new per-module JIT observation (JitDomainResult.TopActiveModulesByFrameHits has no
    // observation of its own today) cross-referenced against ModuleAnalyzer's per-module size/
    // conflict data, a larger lift than this batch's other small gaps — see that doc's tracking.
    private sealed class MemoryAndRuntimeRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectJitModuleHotspot(findings, context.Jit, context.AppDomains);
        }
    }

    // 15 of this group's original 17 methods were migrated to *InsightRule ISynthesisRule classes
    // (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2) and removed from here to
    // avoid double-firing. DataTableLifecyclePattern/KnownLeakPatterns needed a new bounded,
    // known-vocabulary type.known-pattern-population observation (MemoryAnalyzer) rather than the
    // unbounded full-TopTypes population — same shape as TimerWrapperTypeNames, not a capped
    // ranking; the finalizer-side half of DataTableLifecyclePattern already had full observation
    // coverage (type.finalizable-population, finalizer.queue-entry) and needed no new emission.
    // PinnedStringLeak needed a new handle.pinned-target-bytes observation (GCHandleAnalyzer) —
    // its own independent top-N-by-bytes ranking, not summed from the capped per-address
    // handle.pinned observation and not index-zipped against handle.pinned-target-type's separate
    // count ranking. ClusterHangCorrelation needed two new observations — thread.stack-cluster-member
    // (ThreadStackClusterAnalyzer, the dominant cluster's OS thread ids had never been promoted from
    // SampleOsThreadIds) and wait-type.ordinal (HangAnalyzer, the same root-kind.ordinal pattern
    // GCRootAnalyzer already uses for a categorical value with no home in numeric Measures) — see
    // ClusterHangCorrelationInsightRule's own remarks for the full reasoning; its wait-reason
    // breakdown table was dropped, same "supplementary, not the whole payload" call
    // ReferenceChainDominatorCorrelation's own migration made. StaticRootFinalizableCorrelation
    // needed a new heap.static-root-retained-type observation (StaticRootAnalyzer) — its existing
    // heap.static-root subjects on the root object's own type, not the TopRetainedTypes breakdown;
    // no root EntityKind exists in the SDK yet, so the new observation subjects on the retained
    // TypeRef instead and carries the root description as Evidence, the same call already made for
    // thread.stack-cluster's Signature. LongHeldTransactionOnOpenConnection's held-reason — "needs
    // checking whether the addresses are even observable as stable identity, not assumed" — turned
    // out already answered by the SDK: ObjectRef's identity is (artifact, address), explicitly
    // scoped to within-artifact joins, which is exactly what this is (same dump, same run); both
    // infra.sql-transaction and infra.db-connection gained a second ObjectRef subject (the
    // referenced/own connection address) alongside their existing TypeRef, same multi-subject
    // relation shape lock.contested already uses. CrashModuleHotspot needed two new observations —
    // runtime.crash-thread-candidate-module (CrashAnalyzer, per-candidate module attribution had
    // never been promoted; runtime.crash-thread-candidate subjects on exception type instead) and
    // runtime.module-disk-footprint (ModuleAnalyzer, on-disk size — a different quantity from
    // runtime.module-heap-footprint's shallow.bytes, the gap its original held-reason actually
    // named); its version-conflict flag needed no new emission at all — runtime.module-conflict-group
    // already subjects per module with empty Measures, so presence alone is the signal. See
    // CrashModuleHotspotInsightRule's own remarks for the full reasoning; its ranked module table was
    // dropped, same "supplementary, not the whole payload" call ReferenceChainDominatorCorrelation's
    // migration made — it turned out not to be blocked by the EvidenceTables decision below despite
    // the original held-reason grouping it with the three that are. The remaining 2 are held for
    // reasons documented in that doc: MemoryTypeGenerationCorrelation/StringMemoryConcentration need
    // MemoryDomainResult.TopTypes' full per-type population cross-joined against another unbounded
    // per-type list — a bigger design decision (see docs/ReportStructure/ReportSystemVision.md
    // §10.1/§10.4's dd-scatter Correlation shape), not a routine emission gap.
    // ReferenceChainDominatorCorrelation/CrashModuleHotspot (both migrated) and the still-held
    // MemoryTypeGenerationCorrelation/StringMemoryConcentration/JitModuleHotspot all used
    // InsightFinding.EvidenceTables, which Sdk.Synthesis.Finding has no equivalent field for — ran
    // that question against docs/ReportStructure/ReportSystemVision.md § 6.2/Appendix C, which
    // already decided not to grow Finding toward Claim/report-rendering shape ahead of the report
    // phase, and separately marks CompactTable (what an EvidenceTable becomes) as something the
    // future dd-table widget replaces, rendered live from cited observations instead of carried on
    // the finding. So ReferenceChainDominatorCorrelation/CrashModuleHotspot migrated without their
    // tables (see their own remarks) rather than growing Finding to keep them; the remaining two stay
    // held since their tables aren't optional supplementary detail the way those were.
    private sealed class CorrelationRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectStringMemoryConcentration(findings, context.Memory, context.Strings);
        }
    }

    // ── Part 4: New cross-cutting detection methods ───────────────────────────

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
