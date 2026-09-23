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
        new BaselineRuleGroup(),
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
    // instead.
    private const int AnalyzerFailureWarning = 3;

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
    private const ulong MemoryGenerationCorrelationMinBytes = 10UL * 1024 * 1024;    // 10 MB
    private const ulong MemoryGenerationCorrelationCriticalBytes = 100UL * 1024 * 1024; // 100 MB
    private const double MemoryGenerationCorrelationGen2FractionPct = 85.0;
    private const int MemoryGenerationCorrelationTopTypesScanned = 30;
    private const ulong StringMemoryCorrelationMinWastedBytes = 5UL * 1024 * 1024; // 5 MB
    private const int StringMemoryCorrelationMaxRank = 10;
    private const int CrashModuleHotspotMinActiveExceptions = 3;
    private const double CrashModuleHotspotDominantSharePct = 50.0;
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

    // 10 of this group's original 11 methods were migrated to *InsightRule ISynthesisRule classes
    // (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2) and removed from here to
    // avoid double-firing. DetectAnalyzerFailures is the sole survivor — deliberately held, not
    // migrated: it reads raw AnalyzerRunResult execution status, not any observation, and
    // SynthesisContext (always `new SynthesisContext()` at the real call site,
    // SynthesisFindingEnricher.cs) carries no run data today. Migrating it needs a SynthesisContext
    // extension, a real design decision, not a routine observation-emission gap — see that doc's
    // tracking for this item.
    private sealed class BaselineRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectAnalyzerFailures(findings, context.Runs);
        }
    }

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

    // 14 of this group's original 17 methods were migrated to *InsightRule ISynthesisRule classes
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
    // relation shape lock.contested already uses. The remaining 3 are held for reasons documented in
    // that doc: MemoryTypeGenerationCorrelation/StringMemoryConcentration need
    // MemoryDomainResult.TopTypes' full per-type population cross-joined against another unbounded
    // per-type list — a bigger design decision (see docs/ReportStructure/ReportSystemVision.md
    // §10.1/§10.4's dd-scatter Correlation shape), not a routine emission gap; CrashModuleHotspot is
    // large/complex enough to need its own dedicated pass. ReferenceChainDominatorCorrelation
    // (migrated) and the still-held
    // MemoryTypeGenerationCorrelation/StringMemoryConcentration/CrashModuleHotspot/JitModuleHotspot
    // all used InsightFinding.EvidenceTables, which Sdk.Synthesis.Finding has no equivalent field
    // for — ran that question against docs/ReportStructure/ReportSystemVision.md § 6.2/Appendix C,
    // which already decided not to grow Finding toward Claim/report-rendering shape ahead of the
    // report phase, and separately marks CompactTable (what an EvidenceTable becomes) as something
    // the future dd-table widget replaces, rendered live from cited observations instead of carried
    // on the finding. So ReferenceChainDominatorCorrelation migrated without its table (see its own
    // remarks) rather than growing Finding to keep it; the other three stay held since their tables
    // aren't optional supplementary detail the way that one's was.
    private sealed class CorrelationRuleGroup : IInsightRuleGroup
    {
        public void Apply(List<InsightFinding> findings, in InsightRuleContext context)
        {
            DetectMemoryTypeGenerationCorrelation(findings, context.Memory, context.GcGen);
            DetectStringMemoryConcentration(findings, context.Memory, context.Strings);

            DetectCrashModuleHotspot(findings, context.Crash, context.AppDomains);
        }
    }

    // ── Detection rules ───────────────────────────────────────────────────────

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

    /// <summary>
    /// Correlates CrashAnalyzer's per-candidate top-user-frame module attribution with
    /// ModuleAnalyzer's own per-module size/version-conflict data. Neither analyzer can produce
    /// this alone: CrashAnalyzer resolves the owning module directly via ClrStackFrame but has no
    /// module size/conflict data, and ModuleAnalyzer never walks exception thread stacks.
    /// </summary>
    private static void DetectCrashModuleHotspot(
        List<InsightFinding> findings,
        CrashDomainResult? crash,
        ModuleDomainResult? modules)
    {
        if (crash is null || modules is null || crash.TopCrashThreadCandidates is not { Count: > 0 })
            return;

        var moduleActiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int attributedActiveExceptions = 0;
        for (int i = 0; i < crash.TopCrashThreadCandidates.Count; i++)
        {
            CrashThreadCandidateSnapshot candidate = crash.TopCrashThreadCandidates[i];
            if (string.IsNullOrWhiteSpace(candidate.TopUserFrameModule))
                continue;
            moduleActiveCounts.TryGetValue(candidate.TopUserFrameModule, out int count);
            moduleActiveCounts[candidate.TopUserFrameModule] = count + candidate.ActiveExceptionCount;
            attributedActiveExceptions += candidate.ActiveExceptionCount;
        }

        if (attributedActiveExceptions < CrashModuleHotspotMinActiveExceptions)
            return;

        string topModuleName = string.Empty;
        int topModuleCount = -1;
        foreach (KeyValuePair<string, int> kvp in moduleActiveCounts)
        {
            if (kvp.Value > topModuleCount)
            {
                topModuleCount = kvp.Value;
                topModuleName = kvp.Key;
            }
        }

        double topModuleSharePct = 100.0 * topModuleCount / attributedActiveExceptions;
        if (topModuleSharePct < CrashModuleHotspotDominantSharePct)
            return;

        LoadedModuleSnapshot? sizeMatch = FindModuleByName(modules.TopModulesBySize, topModuleName);
        bool inConflict = ContainsModuleName(modules.ConflictingAssemblyNames, topModuleName);

        if (sizeMatch is null && !inConflict)
            return; // no cross-analyzer signal beyond what the Exception Analysis section's own attribution table already shows

        var moduleEntries = new List<KeyValuePair<string, int>>(moduleActiveCounts);
        moduleEntries.Sort(static (a, b) => b.Value.CompareTo(a.Value));

        int rowCount = Math.Min(moduleEntries.Count, 5);
        var rows = new List<IReadOnlyList<object?>>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            KeyValuePair<string, int> entry = moduleEntries[i];
            LoadedModuleSnapshot? rowSizeMatch = FindModuleByName(modules.TopModulesBySize, entry.Key);
            bool rowInConflict = ContainsModuleName(modules.ConflictingAssemblyNames, entry.Key);

            rows.Add(new object?[]
            {
                entry.Key,
                entry.Value,
                rowSizeMatch is not null ? FormatBytes(rowSizeMatch.Size) : "n/a",
                rowInConflict ? "Yes" : "No",
            });
        }

        var evidenceTable = new FindingEvidenceTable(
            "Active exception attribution by module (crash thread candidates)",
            ["Module", "Active Exceptions", "Module Size", "Version Conflict"],
            rows);

        string sizeNote = sizeMatch is not null ? $" and is {FormatBytes(sizeMatch.Size)} on disk" : string.Empty;
        string conflictNote = inConflict
            ? " and has a conflicting assembly version loaded — a version mismatch may be the underlying cause"
            : string.Empty;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Crash",
            Severity: inConflict ? FindingSeverity.Warning : FindingSeverity.Info,
            Title: "Active exceptions concentrated in a module flagged by module analysis",
            Evidence: $"Module '{topModuleName}' owns {topModuleSharePct:F1}% of attributed active exceptions " +
                      $"({topModuleCount:N0} of {attributedActiveExceptions:N0}){sizeNote}{conflictNote}.",
            Recommendation: inConflict
                ? $"Check the module inventory for duplicate/conflicting assembly versions of '{topModuleName}' — " +
                  "a version mismatch loading the wrong dependency is a common cause of concentrated runtime faults."
                : $"Correlate '{topModuleName}'s size and load context with the exception attribution table to " +
                  "prioritize investigation of this assembly's fault handling.",
            Tags: ["crash", "modules", "cross-analyzer"],
            MetricValue: topModuleSharePct,
            MetricUnit: "% of active exceptions",
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
    /// Cross-references the memory analyzer's top types by size with the GC generation
    /// analyzer's per-type generation distribution. Neither analyzer alone shows this: Memory
    /// ranks types by total bytes but has no generation breakdown, while GCGeneration has the
    /// breakdown but doesn't rank by total size. A large type that is almost entirely stuck in
    /// Gen2 is a strong long-lived-leak candidate rather than ordinary working-set memory.
    /// The Gen2 fraction is computed from exact Gen2 bytes when the heap index provides them
    /// (P2-4), falling back to instance-count fraction for older indices — a byte-based fraction
    /// catches types whose surviving instances are disproportionately large.
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

            // Prefer the exact byte-based Gen2 fraction (P2-4) over the count-based one: a type
            // whose Gen2 instances happen to be much larger than its Gen0/Gen1 instances — e.g. a
            // growing collection that keeps reallocating bigger backing arrays as it survives GCs —
            // can look mild by count while actually retaining most of its bytes in Gen2.
            double gen2FractionPct = profile.Gen2Bytes > 0 && profile.TotalBytes > 0
                ? profile.Gen2Bytes * 100.0 / profile.TotalBytes
                : profile.Gen2Count * 100.0 / totalCounted;
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
                profile.Gen2Bytes > 0 ? FormatBytes(profile.Gen2Bytes) : "-",
                $"{gen2FractionPct:F1}%",
            });
        }

        var evidenceTable = new FindingEvidenceTable(
            "Top memory-consuming types stuck in Gen2 (size × generation cross-reference)",
            ["Type", "Total Bytes", "Gen0", "Gen1", "Gen2", "LOH", "Gen2 Bytes", "Gen2 %"],
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
