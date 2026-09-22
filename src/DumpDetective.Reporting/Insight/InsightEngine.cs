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
    private const double ClusterHangOverlapWarningRatio = 0.60;
    private const ulong MemoryGenerationCorrelationMinBytes = 10UL * 1024 * 1024;    // 10 MB
    private const ulong MemoryGenerationCorrelationCriticalBytes = 100UL * 1024 * 1024; // 100 MB
    private const double MemoryGenerationCorrelationGen2FractionPct = 85.0;
    private const int MemoryGenerationCorrelationTopTypesScanned = 30;
    private const ulong StringMemoryCorrelationMinWastedBytes = 5UL * 1024 * 1024; // 5 MB
    private const int StringMemoryCorrelationMaxRank = 10;
    private const ulong PinnedStringLeakMinBytes = 1UL * 1024 * 1024;   // 1 MB
    private const ulong PinnedStringLeakCriticalBytes = 20UL * 1024 * 1024; // 20 MB
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

    // 8 of this group's original 17 methods were migrated to *InsightRule ISynthesisRule classes
    // (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2) and removed from here to
    // avoid double-firing. The remaining 9 are held for reasons documented in that doc:
    // DataTableLifecyclePattern/KnownLeakPatterns/MemoryTypeGenerationCorrelation/
    // StringMemoryConcentration need MemoryDomainResult.TopTypes, an unbounded per-type list
    // deliberately never promoted to an observation; StaticRootFinalizableCorrelation and
    // LongHeldTransactionOnOpenConnection need per-entity cross joins with no clean observation
    // shape yet; ClusterHangCorrelation and CrashModuleHotspot are large/complex enough to need
    // their own dedicated pass; PinnedStringLeak needs a per-type pinned-bytes measure that doesn't
    // exist yet. ReferenceChainDominatorCorrelation (migrated) and the still-held
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
            DetectDataTableLifecyclePattern(findings, context.Finalizable, context.Memory);
            DetectStaticRootFinalizableCorrelation(findings, context.StaticRoot, context.Finalizable);
            DetectKnownLeakPatterns(findings, context.Memory);
            DetectMemoryTypeGenerationCorrelation(findings, context.Memory, context.GcGen);
            DetectStringMemoryConcentration(findings, context.Memory, context.Strings);
            DetectPinnedStringLeak(findings, context.Handles);

            DetectLongHeldTransactionOnOpenConnection(findings, context.SqlTxn, context.DbConn);
            DetectCrashModuleHotspot(findings, context.Crash, context.AppDomains);
            DetectClusterHangCorrelation(findings, context.Clusters, context.Hang, context.Threads, context.LockGraph);
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
    /// P3-3 (docs/analysis/phase1/static-root-leak-detector-audit.md): cross-references each
    /// static root's top retained types against <see cref="FinalizableObjectDomainResult.TopQueueTypesByCount"/>
    /// — types with objects *currently* enqueued for finalization, not merely finalizable types —
    /// by type name. No new heap walk or shared index is needed; both lists are already computed
    /// by their respective analyzers.
    /// </summary>
    private static void DetectStaticRootFinalizableCorrelation(
        List<InsightFinding> findings,
        StaticRootDomainResult? staticRoot,
        FinalizableObjectDomainResult? finalizable)
    {
        if (staticRoot is null || finalizable is null)
            return;

        if (staticRoot.TopRootsByRetainedBytes is null || finalizable.TopQueueTypesByCount.Count == 0)
            return;

        var queueCountByType = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < finalizable.TopQueueTypesByCount.Count; i++)
        {
            QueueTypeStatistic stat = finalizable.TopQueueTypesByCount[i];
            queueCountByType[stat.TypeName] = stat.QueueCount;
        }

        string? bestRootDescription = null;
        string? bestTypeName = null;
        int bestQueueCount = 0;
        int bestRetainedCount = 0;

        foreach (StaticRootSnapshot root in staticRoot.TopRootsByRetainedBytes)
        {
            if (root.TopRetainedTypes is null)
                continue;

            foreach (RetainedTypeInfo typeInfo in root.TopRetainedTypes)
            {
                if (!queueCountByType.TryGetValue(typeInfo.TypeName, out int queueCount) || queueCount <= bestQueueCount)
                    continue;

                bestQueueCount = queueCount;
                bestRootDescription = root.RootDescription;
                bestTypeName = typeInfo.TypeName;
                bestRetainedCount = typeInfo.Count;
            }
        }

        if (bestRootDescription is null)
            return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "Static root retains a type with objects queued for finalization",
            Evidence: $"Static root '{bestRootDescription}' retains {bestRetainedCount:N0} instance(s) of " +
                      $"{bestTypeName}, which also has {bestQueueCount:N0} instance(s) currently queued for " +
                      "finalization — this root may be delaying their collection.",
            Recommendation: "Confirm this static root's ownership of the type is intentional (e.g. a cache), " +
                            "and that instances are removed or disposed once no longer needed so finalization " +
                            "isn't delayed by long-lived static retention.",
            Tags: ["static-root", "finalizer", "cross-cutting"],
            MetricValue: bestQueueCount,
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

    /// <summary>
    /// P2-4 (string-analyzer-audit.md): pinned System.String detection. Reuses the handle
    /// classification GCHandleAnalyzer already performs during its single EnumerateHandles()
    /// pass — rather than a second, redundant handle-table scan inside StringAnalyzer — and
    /// looks for "System.String" among the ranked pinned-target-type breakdown.
    /// </summary>
    private static void DetectPinnedStringLeak(
        List<InsightFinding> findings,
        GCHandleDomainResult? handles)
    {
        if (handles is null) return;

        ulong pinnedBytes = FindBytes(handles.TopPinnedObjectsBySize, "System.String")
            + FindBytes(handles.TopAsyncPinnedObjectsBySize, "System.String");
        if (pinnedBytes < PinnedStringLeakMinBytes)
            return;

        int pinnedCount = FindCount(handles.TopPinnedTargetTypes, "System.String");
        FindingSeverity severity = pinnedBytes >= PinnedStringLeakCriticalBytes
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Memory",
            Severity: severity,
            Title: "Pinned strings detected — blocking GC compaction",
            Evidence: $"{pinnedCount:N0} pinned System.String handle target(s) totaling {FormatBytes(pinnedBytes)}.",
            Recommendation: "Pinning managed strings (e.g. via GCHandle.Alloc(pin: true) for P/Invoke marshalling) " +
                            "prevents the GC from compacting the small object heap around them. Free pinned string " +
                            "handles as soon as the interop call returns, or use Marshal.StringToHGlobalAnsi/Unicode " +
                            "(which copies into unmanaged memory) instead of pinning the managed string itself.",
            Tags: ["strings", "pinning", "gc", "handles", "cross-analyzer"],
            MetricValue: (double)pinnedBytes,
            MetricUnit: "bytes"));

        static ulong FindBytes(IReadOnlyList<NameBytesEntry>? entries, string name)
        {
            if (entries is null) return 0;
            foreach (NameBytesEntry e in entries)
                if (string.Equals(e.Name, name, StringComparison.Ordinal))
                    return e.Bytes;
            return 0;
        }

        static int FindCount(IReadOnlyList<NameCountEntry>? entries, string name)
        {
            if (entries is null) return 0;
            foreach (NameCountEntry e in entries)
                if (string.Equals(e.Name, name, StringComparison.Ordinal))
                    return e.Count;
            return 0;
        }
    }


    // R8 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md): correlates SqlTransactionAnalyzer's
    // Active transaction snapshots (still referencing their owning connection) against
    // DbConnectionAnalyzer's TopOpenConnections addresses. "N active transactions" alone is already
    // reported by SqlTransactionFindingGenerator; this rule adds the cross-analyzer signal that
    // specific open connections are being held by a live transaction, not just idle.
    private static void DetectLongHeldTransactionOnOpenConnection(
        List<InsightFinding> findings,
        SqlTransactionDomainResult? sqlTxn,
        DbConnectionDomainResult? dbConn)
    {
        if (sqlTxn is null || dbConn is null) return;
        if (sqlTxn.ActiveCount == 0 || dbConn.OpenConnections == 0) return;

        var openConnectionAddresses = new HashSet<ulong>(dbConn.TopOpenConnections.Count);
        foreach (DbConnectionSnapshot conn in dbConn.TopOpenConnections)
            openConnectionAddresses.Add(conn.Address);

        int correlatedCount = 0;
        foreach (SqlTransactionSnapshot txn in sqlTxn.TopActiveTransactions)
        {
            if (txn.ConnectionAddress is ulong connAddress && openConnectionAddresses.Contains(connAddress))
                correlatedCount++;
        }

        if (correlatedCount < 3) return;

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Infrastructure",
            Severity: correlatedCount >= 15 ? FindingSeverity.Critical : FindingSeverity.Warning,
            Title: $"{correlatedCount:N0} open DB connections are held by a live transaction",
            Evidence: $"{correlatedCount:N0} of {dbConn.OpenConnections:N0} open connections on the heap are " +
                      "referenced by an Active SqlTransaction/IDbTransaction object that has not yet been " +
                      "Committed, Rolled back, or Disposed. These connections cannot return to the pool while " +
                      "the transaction is held open.",
            Recommendation: "Review call sites that open a transaction alongside its connection. Ensure both " +
                            "are wrapped in using statements scoped tightly to the unit of work, and that no " +
                            "unrelated I/O or awaits happen while the transaction is open.",
            Tags: ["infrastructure", "connections", "transaction", "pool-exhaustion"],
            MetricValue: correlatedCount,
            MetricUnit: "connections held by open transactions"));
    }

    /// <summary>
    /// Cross-references the dominant thread-stack cluster with HangAnalyzer's blocked-thread
    /// findings. When most of the dominant cluster's threads are independently reported as
    /// waiting by HangAnalyzer, the two single-analyzer findings describe the same bottleneck —
    /// this promotes that overlap into one elevated, correlated finding instead of leaving the
    /// reader to notice the connection themselves. When ThreadAnalyzer's data is also available,
    /// the overlapping threads' StackRootCount is folded in as a retention signal: a stuck thread
    /// that also anchors GC roots means resolving the block may relieve retained memory too. When
    /// LockGraphAnalyzer's data is also available and any overlapping thread is independently
    /// flagged as a deadlock candidate (holding a lock while itself blocked), severity is escalated
    /// to Critical and the held lock types are named — this is the strongest signal available
    /// short of true circular-wait detection, since it ties stack shape + wait state + lock
    /// ownership into one finding instead of three disconnected single-analyzer observations.
    /// </summary>
    private static void DetectClusterHangCorrelation(
        List<InsightFinding> findings,
        ThreadStackClusterDomainResult? clusters,
        HangDomainResult? hang,
        ThreadDomainResult? threads,
        LockGraphDomainResult? lockGraph)
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
        var overlappingOsThreadIds = new HashSet<uint>();
        for (int i = 0; i < dominant.SampleOsThreadIds.Count; i++)
        {
            uint osThreadId = dominant.SampleOsThreadIds[i];
            if (waitingOsThreadIds.Contains(osThreadId))
            {
                overlapCount++;
                overlappingOsThreadIds.Add(osThreadId);
            }
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

        // If any overlapping thread is independently flagged by LockGraphAnalyzer as a deadlock
        // candidate (holding a lock while itself blocked), that's structural evidence outweighing
        // the raw percentage heuristic above — escalate regardless of dominantPercentOfAlive.
        int overlappingDeadlockCandidateCount = 0;
        var deadlockLockTypes = new List<string>();
        if (lockGraph?.DeadlockCandidateDetails is { Count: > 0 } deadlockCandidates)
        {
            var seenLockTypes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < deadlockCandidates.Count; i++)
            {
                DeadlockCandidateSnapshot candidate = deadlockCandidates[i];
                if (!overlappingOsThreadIds.Contains(candidate.OsThreadId))
                    continue;

                overlappingDeadlockCandidateCount++;
                for (int j = 0; j < candidate.LockObjectTypes.Count; j++)
                {
                    if (seenLockTypes.Add(candidate.LockObjectTypes[j]))
                        deadlockLockTypes.Add(candidate.LockObjectTypes[j]);
                }
            }
        }
        if (overlappingDeadlockCandidateCount > 0)
            sev = FindingSeverity.Critical;

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

        string retentionNote = string.Empty;
        if (threads is not null)
        {
            int retainedStackRoots = SumStackRootCounts(threads, overlappingOsThreadIds);
            if (retainedStackRoots > 0)
                retentionNote = $" These threads also anchor {retainedStackRoots:N0} GC root(s) via their stacks, so resolving the block may relieve retained memory too.";
        }

        string deadlockNote = string.Empty;
        var tags = new List<string> { "thread-cluster", "hang", "blocking", "contention", "cross-analyzer" };
        if (overlappingDeadlockCandidateCount > 0)
        {
            string lockTypeList = deadlockLockTypes.Count > 0 ? string.Join(", ", deadlockLockTypes) : "unresolved type(s)";
            deadlockNote = $" {overlappingDeadlockCandidateCount} of these threads are additionally flagged by " +
                           $"LockGraphAnalyzer as deadlock candidates, holding lock(s) on: {lockTypeList}.";
            tags.Add("deadlock-candidate");
        }

        findings.Add(new InsightFinding(
            Analyzer: Source,
            Category: "Threads",
            Severity: sev,
            Title: "Dominant thread-stack cluster correlates with HangAnalyzer's blocked threads",
            Evidence: $"{overlapCount} of {dominant.SampleOsThreadIds.Count} sampled threads in the dominant " +
                      $"stack cluster ({dominant.Count:N0} threads, {dominantPercentOfAlive:F1}% of alive threads) " +
                      $"are also reported as waiting by the Hang analyzer{reasonNote}. " +
                      $"Cluster signature: {dominant.Signature}{retentionNote}{deadlockNote}",
            Recommendation: overlappingDeadlockCandidateCount > 0
                ? "Threads in this cluster are both blocked and independently confirmed to be holding contended " +
                  "locks. Review lock acquisition order across these threads and use cycle-detection tooling " +
                  "(e.g., !dlk in WinDbg) to confirm circular-wait before restarting the process."
                : "A large group of threads sharing an identical stack and wait state strongly " +
                  "suggests a single contended resource or blocking call. Inspect the cluster's " +
                  "innermost frame together with the corresponding Hang analyzer wait details to " +
                  "identify the shared bottleneck.",
            Tags: tags,
            MetricValue: overlapRatio,
            MetricUnit: "ratio",
            EvidenceTables: [evidenceTable]));
    }

    // Sums StackRootCount for the given OS thread IDs, deduplicated across ThreadAnalyzer's
    // per-category snapshot lists — a thread can appear in both TopBlockedThreads and
    // TopLockedThreads simultaneously (it can hold a lock while separately blocked waiting on
    // something else), so a naive sum across lists would double-count its StackRootCount.
    private static int SumStackRootCounts(ThreadDomainResult threads, HashSet<uint> osThreadIds)
    {
        if (osThreadIds.Count == 0)
            return 0;

        var stackRootsByOsThreadId = new Dictionary<uint, int>();
        AddStackRootCounts(stackRootsByOsThreadId, threads.TopBlockedThreads);
        AddStackRootCounts(stackRootsByOsThreadId, threads.TopLockedThreads);
        AddStackRootCounts(stackRootsByOsThreadId, threads.OtherThreads);

        int total = 0;
        foreach (uint osThreadId in osThreadIds)
        {
            if (stackRootsByOsThreadId.TryGetValue(osThreadId, out int count))
                total += count;
        }
        return total;
    }

    private static void AddStackRootCounts(Dictionary<uint, int> map, IReadOnlyList<ThreadStateSnapshot>? snapshots)
    {
        if (snapshots is null)
            return;
        for (int i = 0; i < snapshots.Count; i++)
            map[snapshots[i].OSThreadId] = snapshots[i].StackRootCount;
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
