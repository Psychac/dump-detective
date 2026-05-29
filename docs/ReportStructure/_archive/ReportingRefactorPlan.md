# Reporting Refactor Plan

> **Purpose:** This document is the authoritative implementation record for the Professional Tier Report format (§1–§25). It is self-contained — a developer following only this document can understand the implemented spec sections, the remaining open gaps, and the report-layer conventions used in the current codebase.

---

**Current status:** The Professional Tier implementation is mostly complete in the current repository state. The notes below capture the implemented contract and the remaining open gaps.

## 0. Root Architectural Problem

The current `IAnalyzerSectionBuilder` contract is **1-analyzer-in → 1-section-out**:

```csharp
interface IAnalyzerSectionBuilder {
    string AnalyzerName { get; }       // routing key = analyzer name
    bool CanHandle(AnalyzerDomainResult result);
    AnalyzerDetailSection Build(AnalyzerDomainResult result);
}
```

`ReportSerializer.BuildAnalyzerSections()` iterates runs, finds the first builder whose `AnalyzerName` matches, and calls `Build(run.Result)` with a single domain result.

The Professional Tier spec (§1–§25) is **spec-section-oriented, not analyzer-oriented**. Each spec section aggregates multiple analyzer outputs:

| Spec section | Analyzers needed |
|---|---|
| §1 Executive Summary | Memory + AllocationPattern + Thread + InsightEngine |
| §3.1 Type Table | Memory + GCGeneration + ObjectShape + Module |
| §3.2 Dominator Candidates | Memory + GCGeneration + ObjectShape |
| §4 Retention | Retention + GCRoot + StaticRoot + EventLeak |
| §6 Leak Candidates | GCGeneration + ObjectShape + StaticRoot + GCHandle + EventLeak |
| §9 GC Pressure | GCGeneration + AllocationPattern + SegmentReservation + GCHandle |
| §14 Trend | TrendAnalyzer + MemoryComparer + any delta |

A builder that can only see one `AnalyzerDomainResult` cannot produce these sections correctly — it must either duplicate data across builders or skip cross-cutting signals entirely.

---

## 1. Solution: `AnalyzerResultSet` + `IReportSectionBuilder`

Introduce a parallel builder tier that receives the full result set. Keep the existing per-analyzer builders as-is for backward compatibility and for their current role; add the new spec-aligned builders on top.

### 1.1 `AnalyzerResultSet`

A thin, allocation-light resolver over the completed run list. Resolves by domain result type — no heap scan, no analysis, no side effects. Uses lazy per-type caching so multiple builders calling `Get<T>()` for the same type pay the scan cost only once.

```csharp
// src/DumpDetective.Reporting/Models/AnalyzerResultSet.cs
internal sealed class AnalyzerResultSet
{
    private readonly IReadOnlyList<AnalyzerRunResult> _runs;
    private readonly Dictionary<Type, AnalyzerDomainResult?> _cache = new();

    public AnalyzerResultSet(IReadOnlyList<AnalyzerRunResult> runs) => _runs = runs;

    /// Returns the first successful domain result of type T, or null.
    public T? Get<T>() where T : AnalyzerDomainResult
    {
        if (_cache.TryGetValue(typeof(T), out var cached))
            return (T?)cached;

        T? found = null;
        foreach (var run in _runs)
            if (run.Status == AnalyzerExecutionStatus.Success && run.Result is T t)
            { found = t; break; }

        _cache[typeof(T)] = found;
        return found;
    }

    /// Returns all successful runs (for InsightFindings aggregation).
    public IReadOnlyList<AnalyzerRunResult> AllRuns => _runs;

    /// Returns all InsightFindings across all runs, sorted Critical→Warning→Info.
    public IReadOnlyList<InsightFinding> AllFindingsSorted()
    {
        var all = new List<InsightFinding>();
        foreach (var run in _runs)
            if (run.Findings is { Count: > 0 })
                all.AddRange(run.Findings);
        all.Sort(static (a, b) => b.Severity.CompareTo(a.Severity));
        return all;
    }
}
```

### 1.2 `IReportSectionBuilder`

```csharp
// src/DumpDetective.Reporting/Abstractions/IReportSectionBuilder.cs
internal interface IReportSectionBuilder
{
    string SectionId { get; }       // stable, unique — e.g. "prof.leak-analysis"
    string DisplayTitle { get; }    // shown in TOC and section header
    int SortOrder { get; }          // controls position in final report

    // Return false ONLY when the required results are absent (section has nothing to show).
    // Optional results (section degrades gracefully) must NOT gate CanBuild — check them
    // inside Build() and annotate affected columns with "(analyzer not run)".
    // See §7.4 for the required/optional split per builder.
    bool CanBuild(AnalyzerResultSet results);

    AnalyzerDetailSection Build(AnalyzerResultSet results);
}
```

### 1.3 `ReportSerializer` changes

Add a second pass after the existing per-analyzer pass:

```csharp
// In ReportSerializer.Serialize():
// Pass 1 (unchanged): per-analyzer section builders
List<AnalyzerDetailSection> perAnalyzerSections = BuildAnalyzerSections(runs, analyzerBuilders);

// Pass 2 (new): spec-aligned cross-analyzer section builders
var resultSet = new AnalyzerResultSet(runs);
List<AnalyzerDetailSection> specSections = BuildSpecSections(resultSet, reportSectionBuilders);

// Merge: spec sections take priority by SortOrder; if a spec section covers the same
// analyzer as a per-analyzer section and its SortOrder overlaps, drop the per-analyzer one.
List<AnalyzerDetailSection> merged = MergeSections(perAnalyzerSections, specSections);
merged.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
```

`MergeSections` is a simple union — spec sections are sorted first by SortOrder and rendered before the legacy per-analyzer sections. Over time, as spec sections are added, the corresponding per-analyzer section builders can be retired one by one.

### 1.4 DI registration

```csharp
// In ServiceRegistration.cs — add alongside IAnalyzerSectionBuilder registrations:
services.AddSingleton<IReportSectionBuilder, ExecutiveSummarySectionBuilder>();    // §1
services.AddSingleton<IReportSectionBuilder, MemoryTopologySectionBuilder>();       // §2
services.AddSingleton<IReportSectionBuilder, TypeSystemSectionBuilder>();           // §3.1–3.3
services.AddSingleton<IReportSectionBuilder, RetentionDominatorSectionBuilder>();   // §4
services.AddSingleton<IReportSectionBuilder, GCRootIntelligenceSectionBuilder>();   // §5
services.AddSingleton<IReportSectionBuilder, LeakAnalysisSectionBuilder>();         // §6
services.AddSingleton<IReportSectionBuilder, ThreadConcurrencySectionBuilder>();    // §7
services.AddSingleton<IReportSectionBuilder, AsyncAnalysisSectionBuilder>();        // §8
services.AddSingleton<IReportSectionBuilder, GCPressureSectionBuilder>();           // §9
services.AddSingleton<IReportSectionBuilder, HeapSegmentDiagnosticsSectionBuilder>(); // §10
// §11–§13: keep existing per-analyzer builders until replaced
services.AddSingleton<IReportSectionBuilder, ExceptionAnalysisSectionBuilder>();   // §13 cross-ref
services.AddSingleton<IReportSectionBuilder, InsightsSectionBuilder>();             // §16
services.AddSingleton<IReportSectionBuilder, ConfidenceSectionBuilder>();           // §17
services.AddSingleton<IReportSectionBuilder, AppDomainAssemblySectionBuilder>();    // §18
```

`DefaultSectionBuilderFactory` injects both `IEnumerable<IAnalyzerSectionBuilder>` and `IEnumerable<IReportSectionBuilder>` and hands them both to `ReportSerializer`.

### 1.5 SortOrder ranges

Spec-section builders use ranges 1000–2500 so they never collide with per-analyzer builders (1–999). The per-analyzer sections remain visible for analyzers not yet covered by a spec builder.

| SortOrder | Section |
|---|---|
| 1000 | §1 Executive Summary |
| 1050 | §2 Memory Topology |
| 1100 | §3 Type System |
| 1150 | §4 Retention & Dominators |
| 1200 | §5 GC Root Intelligence |
| 1250 | §6 Leak Analysis |
| 1300 | §7 Thread & Concurrency |
| 1350 | §8 Async & Task |
| 1400 | §9 GC & Allocation Pressure |
| 1450 | §10 LOH / POH / FOH |
| 1500 | §11 String Analysis (existing per-analyzer builder — no new builder needed) |
| 1550 | §12 Event & Delegate (existing per-analyzer builder) |
| 1600 | §13 Exception Analysis |
| 1650 | §14 Trend (handled by TrendReportComposer) |
| 1700 | §16 Insights & Recommendations |
| 1750 | §17 Confidence & Limitations |
| 1800 | §18 AppDomain & Assembly |
| 1850 | §19–§25 (existing per-analyzer builders promoted into this range) |

---

## 2. Spec Section → Builder Mapping (Complete)

This section lists every spec section, its builder, what data is already available, and what gaps remain. Use this as the implementation checklist.

### §1 Executive Summary → `ExecutiveSummarySectionBuilder`

**Domain results needed:** `MemoryDomainResult`, `AllocationPatternDomainResult`, `ThreadDomainResult`, `GCGenerationDomainResult`, + `AllFindingsSorted()`

**Renders:**
- Total managed memory (bytes + formatted) — from `MemoryDomainResult.TotalBytes` ✅
- % of process memory — ❌ not available via ClrMD; emit "N/A (dump-only)" note
- Top 5 memory consumers by shallow size (type name, size, count) — `MemoryDomainResult.TopTypesBySize` ✅
- Top 5 memory consumers by retained size — ❌ blocked by §4.1 BFS gap; use shallow as proxy with "(shallow)" annotation
- GC pressure level — `AllocationPatternDomainResult.GCPressure` enum ✅
- Thread contention signal — `ThreadDomainResult.BlockedThreadCount > 0` ✅
- Leak likelihood — ✅ `ExecutiveSummaryRecord.LeakLikelihoodScore` is rendered in the report; the detailed section still uses top `InsightFinding.DetectLeakSuspicion` evidence as the explanatory proxy
- Top 3 actionable recommendations — take top 3 Critical/Warning `InsightFinding.Recommendation` strings from `AllFindingsSorted()` ✅ (assembly work only)

**Gaps:** % of process (permanently N/A), retained size now comes from bounded BFS, leak score now comes from `LeakCandidateAnalyzer`.

---

### §2 Memory Topology → `MemoryTopologySectionBuilder`

**Domain results needed:** `SegmentAnalysisDomainResult`, `SegmentReservationDomainResult`, `MemoryDomainResult`, `AllocationPatternDomainResult`, `GCGenerationDomainResult`, `AnalysisIncidentContext`

**§2.1 Heap Composition renders:**
- SOH / LOH / POH / FOH bytes and % — `SegmentAnalysisDomainResult`: `SohBytes`, `LohBytes`, `PohBytes`, `FrozenBytes`, `TotalCommittedBytes` ✅
- FrozenPercent — ✅ rendered from `SegmentAnalysisDomainResult.FrozenPercent`
- Object size distribution histogram (8 buckets) — `MemoryDomainResult.SizeBucketHistogram` ✅
- GC mode (Workstation / Server) — `AnalysisIncidentContext.GcMode` ✅; note reflection risk (see §3 domain gaps)
- Server GC heap count — `AnalysisIncidentContext.HeapCount` ✅
- Per-logical-heap breakdown (bytes + object count per heap index) — ✅ rendered from `SegmentAnalysisDomainResult.PerLogicalHeapSummaries` with skew warning

**§2.2 Generation Pressure renders:**
- Gen0/Gen1/Gen2/LOH bytes and object counts — `GCGenerationDomainResult` ✅
- Gen2 % — `GCGenerationDomainResult.Gen2Pct` ✅
- Note: byte totals are approximate (avg × count) — emit caveat in prose ✅
- Per-type generation breakdown top-20 — `GCGenerationDomainResult.PerTypeGenerationProfiles` ✅
- Per-type survival rate (Gen2/total) — derive in builder from `TypeGenerationProfile` fields; no model change needed ✅

**§2.3 Allocation Patterns renders:**
- Gen0 count and % — `AllocationPatternDomainResult.Gen0CountPct` ✅
- Gen0:Gen2 ratio — derive from `Gen0CountPct / Gen2CountPct` in builder ✅
- Ephemeral segment fill % — `SegmentReservationDomainResult.AvgEphemeralFillPct` ✅; flag > 80% ✅
- Heuristic classification (Accumulating / Churning / Balanced) — map `AllocationProfile` enum in builder: `Retained`→Accumulating, `Transient`→Churning, `Steady`→Balanced ✅ (prose mapping only)
- Fragmentation signal row — combine segment pressure, reservation risk, LOH share, and frozen share into a single visible pressure indicator ✅
- Caveat: allocation sites require ETW — emit as a footer note ✅

---

### §3 Type System → `TypeSystemSectionBuilder`

**Domain results needed:** `MemoryDomainResult`, `GCGenerationDomainResult`, `ObjectShapeAnalyzerDomainResult`, `ModuleDomainResult`

**§3.1 Type Table renders (columns):**

| Column | Source | Gap? |
|---|---|---|
| Type name | `TypeSnapshot.TypeName` | ✅ |
| Object count | `TypeSnapshot.ObjectCount` | ✅ |
| Shallow size total | `TypeSnapshot.TotalSize` | ✅ |
| Shallow size avg | `TypeSnapshot.AverageSize` | ✅ |
| Estimated retained | `TypeSnapshot.EstimatedRetainedBytes` | ✅ populated by bounded retained-size BFS |
| Gen0/Gen1/Gen2 % | Join with `PerTypeGenerationProfiles` by type name | ✅ |
| Is finalizable | Join with `ObjectShapeAnalyzerDomainResult.ShapeProfiles` | ✅ (top-N cap) |
| Is value type | Same join | ✅ (top-N cap) |
| Is array | ✅ rendered from `TypeShapeProfile.IsArray` |
| Base type depth | Join with shape profiles | ✅ (top-N cap) |
| Interface count | Join with shape profiles | ✅ (top-N cap) |
| Ref / value field count | Join with shape profiles | ✅ (top-N cap) |
| Module name | `TypeSnapshot.ModuleName` | ✅ populated from cached heap stats / sample module metadata |
| Method table | `TypeSnapshot.MethodTable` | ✅ |

Emit top-100 rows sorted by shallow size. Show "(shallow)" label on size column header as long as retained is 0. ⚠️

**§3.2 Dominator Candidates renders:** ⚠️

Use inline `DominatorCandidateBuilder` helper (not a full analyzer). Join:
- `MemoryDomainResult.TopTypesBySize` → all type aggregates
- `GCGenerationDomainResult.PerTypeGenerationProfiles` → Gen2 % per type
- `ObjectShapeAnalyzerDomainResult.ShapeProfiles` → IsFinalizable per type

Apply four nomination criteria (any one qualifies):
1. TotalSize > 1% of `MemoryDomainResult.TotalBytes`
2. Gen2Count / Count > 0.8
3. IsFinalizable AND Count > 500
4. Type name contains any of: `Dictionary`, `ConcurrentDictionary`, `List<`, `ConcurrentQueue`, `[]` AND TotalSize > 50 MB

Rank top 30 by TotalSize. Per row: type name, nomination reason(s), instance count, shallow size, Gen2%, sample address from `TypeSnapshot.SampleAddress`.

Estimated retained: ✅ show BFS-derived retained bytes for top entries.
GC root reachability: ✅ cross-reference `GCRootDomainResult.TopRootsBySeverity` and `RootPaths` by `TargetTypeName` match; show "Rooted" / "Unknown".

**§3.3 Object Shape Analysis renders:**
- Top 20 reference-heavy types — `ObjectShapeAnalyzerDomainResult.TopReferenceHeavyTypes` ✅
- Note: ranked by GC-scan-cost composite (ratio × count) rather than pure density — add prose clarification ✅
- Classification thresholds: remap in builder output: code `Balanced` → display "Mixed", code `ReferenceHeavy` threshold 0.6 → display as ">60% ref fields (spec threshold: ≥50%)" with caveat
- Pure value containers (0 ref fields): filter `TypeShapeEntry.RefFields == 0`; no model change needed ✅
- Oversized value types: filter `IsValueType && AverageSize > 64` — derive in builder ✅

---

### §4 Retention & Dominator Analysis → `RetentionDominatorSectionBuilder`

**Domain results needed:** `RetentionDomainResult`, `GCRootDomainResult`, `StaticRootDomainResult`, `EventLeakDomainResult`, `FinalizableObjectDomainResult`, `MemoryDomainResult`

**§4.1 Retention Hotspots renders:**
- Highly referenced object count and top types — `RetentionDomainResult.HighlyReferencedObjectCount`, `TopRetentionTypes` ✅
- Per-type estimated retained size — ✅ show BFS-derived retained bytes for top entries
- Retention ratio (retained/shallow) — ✅ bounded BFS retained bytes now computed for top highly referenced objects
- Top 20 by retention ratio — ✅ rendered from bounded BFS retained bytes on the top highly referenced objects
- Limits note: "BFS breadth 10 000 / depth 20 — shown once implemented"

**§4.2 Dominator Tree (Approx) renders:**
- Per-candidate exclusive retained bytes — ✅ bounded BFS retained bytes available; use as exclusive estimate in first pass
- Render placeholder section: "Dominator tree requires bounded BFS retained-size computation. Implement §4.1 BFS first."
- Once BFS is available: top 15 by exclusive retained, dominator impact score (exclusive/total × 1000), shared dominators (addresses in multiple reachable sets)

**§4.3 Retention Patterns renders:**
- Static chains — `StaticRootDomainResult.TopRootsByBytes` (root name, retained bytes) ✅; chain depth ❌ (emit "depth: N/A")
- Event chains — `EventLeakDomainResult.TopPublisherEvents` (publisher type, subscriber count, retained bytes) ✅
- Cache chains (Dictionary/ConcurrentDictionary in Gen2) — type-name pattern match against `PerTypeGenerationProfiles`; no model change ✅
- Thread-local chains — type-name match for `ThreadLocal` in `GCRootDomainResult.TopRootsBySeverity` ✅ (heuristic subsection)
- Finalizer chains — `FinalizableObjectDomainResult.FinalizerQueueCount` + `TopQueueEntriesByRetainedSize` ✅; BFS retained ❌ (estimate only)
- Per-pattern record fields: root type ✅, chain depth ❌ (N/A), retained bytes ✅ (estimate)

---

### §5 GC Root Intelligence → `GCRootIntelligenceSectionBuilder`

**Domain results needed:** `GCRootDomainResult`, `LeakCandidateDomainResult` (P1, optional — fall back to §5.2 severity roots if not available)

**§5.1 Root Distribution renders:**
- Table: Root kind | Count | Estimated retained bytes | % of heap — `GCRootDomainResult.ByKind` ✅
- Add caveat: "Retained bytes are avg-size estimates, not BFS-measured" ✅
- Emit all 6 root kinds listed in spec; filter from `ByKind` list

**§5.2 Root Severity Ranking renders:**
- Top 20 by severity — `GCRootDomainResult.TopRootsBySeverity` ✅
- Severity band inline: Critical(>100 MB) / Warning(10–100 MB) / Info(<10 MB) — verify `GCRootAnalyzer.ComputeSeverity()` uses these thresholds; fix if different ✅
- Nullable `FieldDescription` — render as "—" when null ✅
- Finalizer roots flagged separately — ✅ rendered as a dedicated finalizer-root subtable

**§5.3 Root Paths renders:**
- Group `GCRootDomainResult.RootPaths` by `TargetTypeName` — ✅ grouped in builder
- Per target type: top 3 paths (shortest first = sort by `PathTypeNames.Count`)
- Format each path: `[RootKind] PathTypeNames[0] → PathTypeNames[1] → … → TargetTypeName`; append `[TRUNCATED]` when `WasCapped = true` ✅
- Paths through `object[]` or `List<T>`: annotate as "(indirect)" — ✅ builder adds the indirect tag when those intermediates appear
- Note: current paths are for §5.2 severity roots; `LeakCandidateDomainResult` is now available and can reseed `GCRootAnalyzer` candidates from top leak types in that result

---

### §6 Leak Analysis → `LeakAnalysisSectionBuilder`

**Domain results needed:** `LeakCandidateDomainResult`

**§6.1 Leak Candidates renders:**
- Top 30 by suspicion score — `LeakCandidateDomainResult.TopCandidates`
- Table: type name | score | severity | total size | instance count | Gen2% | classification | root kind
- Score breakdown tooltip/footnote explaining the scoring signals and weights — ✅ rendered beneath the top-candidates table
- Top suspect summary — surface the highest-scoring candidate before the table so the report reads like a narrative first, table second ✅

**§6.2 Leak Classification renders:**
- Group candidates by `LeakClass` enum (see enum definition in §3.2 Analysis Gaps)
- Per class: count of types, total size, top 3 type names
- Classes: StaticRetention, EventLeak, CacheLeak, ThreadLocalLeak, FinalizerRetention, GCHandleRetention, DependentHandleLeak, Unknown

**§6.3 Leak Explanation renders:**
- Per Warning/Critical candidate: `LeakExplainer.Explain(candidate)` → parameterised template string
- Template per `LeakClass` (see `LeakExplainer` in §5 Builder Details)
- Evidence list: root kind, field name, path depth, retained bytes (from candidate record)
- `LeakCandidateRecord.Severity` is first-class and derived from the suspicion score in `LeakCandidateAnalyzer`

**§6.4 Leak Impact renders:**
- Per candidate: shallow size + retained, % of total heap, stability risk band
- Stability risk: Low (<50 MB) / Medium (50–500 MB) / High (500 MB–2 GB) / Critical (>2 GB) — compute from `TotalSize` in builder
- GC impact note (finalizable leaks → two-pass collection) — emit when `IsFinalizable = true`
- LOH fragmentation note — emit when candidate `TotalSize > 85 000 B` and type is array/string

---

### §7 Thread & Concurrency → `ThreadConcurrencySectionBuilder`

**Domain results needed:** `ThreadDomainResult`, `HangDomainResult`, `LockGraphDomainResult`

**§7.1 Thread Lifecycle renders:**
- Counts: Total / Alive / Inactive / Background — `ThreadDomainResult` ✅
- GC thread count, finalizer thread alive/blocked — `ThreadDomainResult` ✅
- Finalizer frames — `ThreadDomainResult.FinalizerFrames` ✅
- Async chain threads and max async chain depth — `ThreadDomainResult.AsyncChainThreadCount`, `MaxAsyncChainDepth` ✅
- Thread pool: MinThreads, MaxThreads, ActiveWorkerThreads, IdleWorkerThreads, RetiredWorkerThreads, CpuUtilization, starvation flag — ✅ rendered from `HangDomainResult`; queue length shown as scan-based proxy
- Starvation flag (`QueueLength > 0 AND Active == Max`) — ✅ derived from runtime worker-thread saturation + low CPU
- Per-thread stack size (StackBase − StackLimit) — ✅ added to `ThreadStateSnapshot`

**§7.2 Synchronization Patterns renders:**
- Wait category breakdown (9 categories) — `ThreadDomainResult.WaitPatternBreakdown` ✅
- Top 10 blocked threads table: OS thread ID, wait category, wait reason, lock count, top frame — `TopBlockedThreads` ✅
- Top 10 lock-holding threads: lock count, GC mode, top frame — `TopLockedThreads` ✅
- Frame hotspots top 10 — `TopStackHotspots` ✅
- GC mode distribution (Cooperative vs Preemptive) — `GcModeDistribution` ✅

**§7.3 Deadlock Detection renders:**
- Deadlock candidate count — `LockGraphDomainResult.DeadlockCandidateCount` ✅
- Per cycle: ManagedThreadId, OSThreadId, LocksHeld (type names), Summary — `DeadlockCandidates` ✅
- Lock addresses per cycle — ✅ rendered from `DeadlockCandidateSnapshot.LockObjectAddresses`
- Suspected deadlocks (contested without confirmed cycle) — ✅ rendered as contested locks owned by deadlock-candidate threads

---

### §8 Async & Task → `AsyncAnalysisSectionBuilder`

**Domain results needed:** `AsyncTaskDomainResult`, `HangDomainResult`

**§8.1 Task Summary renders:**
- Total tasks and status breakdown (Pending/Running/Faulted/Canceled/RanToCompletion) — `AsyncTaskDomainResult` ✅
- QueuedWorkItems — `HangDomainResult.QueuedWorkItems` ⚠️ (sourced from task scan, not ClrRuntime.ThreadPool.QueueLength; caveat in prose)
- TotalTaskContinuations — ✅ added to `AsyncTaskDomainResult`
- RuntimeThreadPoolDataAvailable flag — from `HangDomainResult` ✅; re-emit in this section

**§8.2 Orphaned Tasks renders:**
- Total orphan count — `AsyncTaskDomainResult.OrphanedTasks` ✅
- Split: faulted orphans vs fire-and-forget orphans — ⚠️ combined count; split by checking `AsyncTaskDomainResult.FaultedTasks > 0`; annotate caveat
- Top orphaned faulted tasks table: address, task type, result type, size — `TopOrphanedTasks` ✅
- Exception type and message per orphaned faulted task — ✅ added to `OrphanedTaskSnapshot` and rendered in the table

**§8.3 Continuation Chains renders:**
- MaxContinuationDepth — `AsyncTaskDomainResult.MaxContinuationDepth` ✅
- Top continuation types by frequency — `TopContinuationTypes` ✅
- Top 5 deepest chains as root→continuation sequence — ✅ rendered from `AsyncTaskDomainResult.TopDeepestChains`
- Depth > 50 flag — ✅ derived in builder from `MaxContinuationDepth > 50`

---

### §9 GC & Allocation Pressure → `GCPressureSectionBuilder`

**Domain results needed:** `GCGenerationDomainResult`, `AllocationPatternDomainResult`, `SegmentReservationDomainResult`, `GCHandleDomainResult`, `SegmentAnalysisDomainResult`, `MemoryDomainResult`

**§9.1 Allocation Patterns renders:**
- Gen0 count, bytes, top 10 types — `GCGenerationDomainResult.Gen0Objects`, `Gen0Bytes`; filter `PerTypeGenerationProfiles` by Gen0Count descending ✅ (builder sort only)
- Gen1 top 10 types — filter `PerTypeGenerationProfiles` by Gen1Count descending ✅
- Gen2/LOH top 10 — `TopLohTypes` ✅; Gen2 filter ✅
- Survival ratio per type (Gen2/total) — derive in builder ✅
- Ephemeral segment fill % — `SegmentReservationDomainResult.AvgEphemeralFillPct` ✅; flag > 80%
- Allocation pressure (UsedBytes vs CommittedBytes per SOH segment) — ✅ `HeapSegmentSnapshot.UsedBytes` now tracked and surfaced in segment tables
- Allocation density (objects per KB) — ✅ derived in segment tables from used-bytes/committed-bytes bases
- Size histogram — `MemoryDomainResult.SizeBucketHistogram` ✅; verify bucket labels match spec: `<64B / 64–256B / 256B–1KB / 1–85KB / >85KB`

**§9.2 GC Efficiency renders:**
- Promotion rate per type (Gen1/(Gen0+Gen1+Gen2)) — derive in builder from `TypeGenerationProfile` ✅
- Gen2 accumulation rate — derive ✅
- Finalizable Gen2 overhead — rendered in `TypeSystemSectionBuilder` from `GCGenerationDomainResult.PerTypeGenerationProfiles` using finalizable generation profiles
- Segment utilisation (UsedBytes/CommittedMemory) — ✅ rendered from `SegmentAnalysisDomainResult.TotalUsedBytes`
- Committed vs reserved gap — ✅ Memory Topology now surfaces both used/committed and used/reserved alongside the reservation gap
- Cross-heap distribution — ✅ rendered from `SegmentAnalysisDomainResult.PerLogicalHeapSummaries`
- Compaction blockage score — join `GCHandleDomainResult.PinnedHandleTargets` + `SegmentAnalysisDomainResult.KindSummaries` (POH count) ✅ (builder assembly)

**§9.3 Pinning Impact renders:**
- Total pinned handle count, top pinned target types, top pinned objects by size — `GCHandleDomainResult` ✅
- Gen0/Gen1 pinned objects — ❌ no address→generation correlation (blocked)
- Clustering analysis — ❌ blocked
- POH vs GC-handle comparison — join `SegmentAnalysisDomainResult.PohBytes` + `GCHandleDomainResult.PinnedRetainedBytes` ✅ (builder join)

---

### §10 LOH / POH / FOH → `HeapSegmentDiagnosticsSectionBuilder`

**Domain results needed:** `SegmentAnalysisDomainResult`, `LohFragmentationDomainResult`, `GCGenerationDomainResult`, `ArrayDomainResult`

**§10.1 LOH Summary renders:**
- Total LOH size, segment count, object count — `SegmentAnalysisDomainResult.LohBytes`, `LohSegmentCount`; object count from `KindSummaries` ✅
- Top LOH types by size and count — `GCGenerationDomainResult.TopLohTypes` ✅
- Types just over 85 000 B threshold — filter `MemoryDomainResult.TopTypesBySize` where `AverageSize > 85_000 && AverageSize < 200_000` ✅ (builder filter; approximate)

**§10.2 LOH Fragmentation renders:**
- Global fragmentation % — `LohFragmentationDomainResult.FragmentationPercent` ✅
- Severity band: Critical > 60% / Warning 30–60% / OK < 30% — derive in builder ✅ (no model change)
- Free block count, largest free block — `FreeBlockCount`, `LargestFreeBlock` ✅
- Top 5 fragmented segments table — `TopFragmentedSegments` ✅
- Free gap histogram — `FreeGapHistogram` ✅

**§10.3 Large Object Lifetimes renders:**
- Per-object LOH data — ❌ not available without LOH segment scan (expensive; defer for now)
- Render note: "Per-object LOH lifetime requires a targeted segment scan — not computed for dumps > 1 GB. Use address range from §10.2 top-fragmented segments for manual investigation."
- Large arrays (from §22 data) — cross-reference `ArrayDomainResult.TopLargeArrays` for arrays > 1 MB ✅

**§10.4 POH Diagnostics renders:**
- Segment count, total size, object count — `SegmentAnalysisDomainResult.PohSegmentCount`, `PohBytes`, `KindSummaries` ✅
- Top POH types by size — ✅ rendered from POH segment type scan
- POH vs GC-handle comparison — join `PohBytes` + `GCHandleDomainResult.PinnedRetainedBytes` ✅

**§10.5 FOH Diagnostics renders:**
- Segment count, total size, object count — `FrozenSegmentCount`, `FrozenBytes`, `KindSummaries` ✅
- Top FOH types — ✅ rendered from frozen-segment type scan
- Overuse signal — emitted when `FrozenBytes > 100 MB`; note likely causes in prose ✅

---

### §11 String Analysis (keep existing `StringSectionBuilder`)

No new cross-analyzer builder needed. Existing per-analyzer builder covers this section comprehensively. Only outstanding gap:
- ASCII encoding waste detection — ❌ deferred (significant I/O cost; document as known limitation)
- Estimated saving from interning — add to builder: sum `TopDuplicatesByWaste.Take(20).Sum(d => d.WastedBytes)` and emit ✅

---

### §12 Event & Delegate (keep existing `EventLeakSectionBuilder`)

No new cross-analyzer builder needed. Only outstanding gap:
- Publisher generation (Gen0/1 vs Gen2) — `EventLeakInstanceSnapshot.PublisherGeneration` exists per instance; aggregate in builder as "% instances in Gen2" per group ✅

---

### §13 Exception Analysis → `ExceptionAnalysisSectionBuilder`

**Domain results needed:** `CrashDomainResult`, `ThreadDomainResult`, `ModuleDomainResult`

**§13.1 Exception Frequency renders:**
- Top exception types by count — `CrashDomainResult.ExceptionTypeCounts` ✅
- Active vs total exception counts — `TotalExceptions`, `ActiveExceptions` ✅
- Active exception types — `ActiveExceptionTypeCounts` ✅

**§13.2 Failure Hotspots renders:**
- Top threads with active exceptions — `CrashDomainResult.TopCrashThreadCandidates` ✅
- Frame frequency across exception threads — cross-reference `TopCrashThreadCandidates[].TopFrames` in builder; count frame occurrences; no model change ✅
- Frame origin classification (UserCode / FrameworkCode / ThirdParty) — derive in builder: `System.`/`Microsoft.`→FrameworkCode prefix; others checked against `ModuleDomainResult` known module names→ThirdParty; else UserCode ✅
- InnerException chain depth histogram — ✅ `ExceptionInstanceSnapshot.ChainDepth` added and rendered as a histogram; depth > 5 can be flagged in prose if needed

---

### §14 Temporal / Diff Analysis (handled by `TrendReportComposer`)

No new `IReportSectionBuilder` needed — `TrendReportComposer` already builds the trend section. Gaps:
- New types (in B, absent from A) — after `MemoryAnalyzerTrendComparer.Compare()`, compute `currentTypeNames.Except(baselineTypeNames)` and emit as explicit `NewTypes` list in trend section ✅
- Classification as typed enum — trend report currently emits classification as prose strings ("Growing", "Stable", "Exploding"); add `TrendClassification` enum to `MetricDelta` model for richer rendering ✅
- Severity escalations (Warning→Critical between snapshots) — compare same-fingerprint `InsightFinding.Severity` across snapshots in `FindingLifecycleComparer`; emit escalation sub-list ✅
- §14.2 Regression detection — now can use `LeakCandidateDomainResult`

---

### §15 Visualization

Implemented. `IReportSectionBuilder` can now emit typed `ChartBlock` entries that carry a serialized JSON payload. The HTML formatter preserves the payload as `data-*` attributes and the browser renderer turns it into inline SVG charts, so the report layer stays model-driven without introducing a separate charting dependency.

| Visual | Data source | Implementation |
|---|---|---|
| Memory pie | `SegmentAnalysisDomainResult` | `MemoryTopologySectionBuilder` emits a `ChartBlock` with a heap-composition payload |
| Type treemap | `MemoryDomainResult.TopTypesBySize` | `TypeSystemSectionBuilder` emits a `ChartBlock` with top-type payload |
| Thread timeline | `ThreadDomainResult.ThreadStateDistribution` | Existing `thread-clusters.json` artifact remains the source for timeline-style renderings |
| LOH heatmap | `LohFragmentationDomainResult.TopFragmentedSegments` | `LohFragmentationSectionBuilder` emits a `ChartBlock` with fragmented-segment payload |
| Diff waterfall | `MemoryAnalyzerTrendComparer` deltas | `TrendReportComposer` emits a `ChartBlock` with delta-summary payload |

This is now a rendering-layer feature with typed blocks, server-side HTML passthrough, and browser-side SVG chart rendering.

---

### §16 Insights & Recommendations → `InsightsSectionBuilder`

**Domain results needed:** `AllFindingsSorted()` from `AnalyzerResultSet`

**Renders:**
- All findings ranked Critical→Warning→Info — `AllFindingsSorted()` ✅
- Per finding: source analyzer, title, evidence, recommendation, tags — `InsightFinding` ✅
- ConfidenceScore (0.0–1.0) — ❌ field missing from `InsightFinding`; add and populate (see §4 domain gaps)
- Caveats array — ❌ field missing; add
- Cross-analyzer correlations as distinct cross-ref findings — `InsightEngine.DetectAllocationPressureCrossCorrelation()`, `DetectBoxingGCCorrelation()` etc. ✅
- ≥3 failed analyzers → emit Warning finding — ❌ not implemented; add to `InsightEngine` (see §6 InsightEngine Changes)

---

### §17 Confidence & Limitations → `ConfidenceSectionBuilder`

**Domain results needed:** `AllRuns` from `AnalyzerResultSet`; **full registered analyzer list** injected from DI (see §7.5)

**Renders:**
- Per-analyzer run status table: analyzer name | status (Success/Failed/SkippedByFilter/SkippedByCancellation) | duration ms | objects scanned | error message — from `AllRuns` ✅ (status granularity requires §7.3 four-state enum)
- Aggregate: completed count, failed count, skipped-by-filter count, skipped-by-cancellation count ✅
- Known heuristic limitations table (static, from this plan): retained size BFS bounds, ETW unavailability, task orphan CLR field name dependency, FOH/POH runtime internals, `StackBase/StackLimit` zero for GC threads, cooperative wait deadlock detection limits
- ConfidenceScore legend (1.0/0.8/0.5/<0.5) — static prose ✅
- SkipReason — ✅ added to `AnalyzerRunResult`; rendered as the run-status detail when present
- **Requires full registered list:** `ConfidenceSectionBuilder` must enumerate all registered `IAnalyzer` names, not only the entries in `AllRuns`, to surface analyzers excluded by `--include-analyzers`/`--exclude-analyzers` that produced no synthetic entry. See §7.2 and §7.5 for the synthetic-entry approach that makes this automatic.

---

### §18 AppDomain & Assembly → `AppDomainAssemblySectionBuilder`

**Domain results needed:** `AppDomainDomainResult`, `ModuleDomainResult`

**§18.1 AppDomain Inventory renders:**
- Per domain: name, address, ID, module count, estimated managed bytes — `AppDomainSnapshot` ✅
- Per-domain module list — ✅ stored on `AppDomainSnapshot` and rendered in the inventory table
- IsPEFile per module — ✅ added to `LoadedModuleSnapshot` and rendered in module tables
- Cross-domain types — ❌ still not detected; emit "N/A" note until cross-domain correlation is added

**§18.2 Assembly Version Conflicts renders:**
- Version conflict groups — `ModuleDomainResult.ConflictDetails` ✅
- Dynamic module count and size — `TotalDynamicModules` ✅; `DynamicModuleBytes` ✅
- Anonymous module count — `AppDomainDomainResult.AnonymousModuleCount` ✅

**§18.3 Type Density per Module renders:**
- Top modules by type count — `AppDomainDomainResult.TopModulesByTypeCount` ✅
- Modules with > 5000 types flagged — filter in builder ✅
- Heap footprint per module — `ModuleDomainResult.TopModulesByHeapMemory` ✅
- Objects-per-type ratio — derive from `ObjectCount / UniqueTypeCount` in builder ✅

---

### §19–§25 (existing per-analyzer builders; minor fixes only)

These sections are well-covered by existing per-analyzer builders. Only targeted fixes needed:

| Section | Builder | Fix |
|---|---|---|
| §19 JIT | `JitSectionBuilder` | Emit ">64 KB" flag when `HotSize + ColdSize > 65536`; note R2R detection as N/A via ClrMD |
| §20 Boxing | `BoxingSectionBuilder` | No changes needed; container boxing context is a known limitation |
| §21 Finalizable | `FinalizableObjectSectionBuilder` | Add severity band: Critical >10 000 / Warning 1 000–10 000 / OK <1 000 to `FinalizerQueueCount` |
| §22 Array | `ArraySectionBuilder` | Anti-pattern labels now rendered for `byte[]` > 1 MB and `string[]`/`object[]` > 10,000 elements |
| §23 Async State Machine | `AsyncStateMachineSectionBuilder` | Note: state distribution histogram blocked (only avg stored) |
| §24 Weak Reference | `WeakReferenceSectionBuilder` | Weak-handle kind breakdown now rendered from `WeakReferenceDomainResult.WeakHandleKinds` ✅ |
| §25 Segment Reservation | `SegmentReservationSectionBuilder` | VA gap analysis: sort `SegmentTable` by address, compute gaps between segments in builder ✅ |

---

## 8. Remaining Gaps, Priority Order

These are the follow-up items after the current implementation pass, ranked by user impact.

### 8.1 Completed: Visualization and Report Fidelity

- Complete. The visualization path now uses `ChartBlock` emission plus renderer support for the memory pie, type treemap, LOH heatmap, and diff waterfall.
- Keep the section as the authored record of the implementation, but there is no longer an open chart-block gap.

### 8.2 Medium Priority: AppDomain Correlation

- Add cross-domain type detection to [AppDomainAssemblySectionBuilder](../src/DumpDetective.Reporting/SectionBuilders/AppDomainAssemblySectionBuilder.cs) so the section can show actual cross-domain overlap instead of only an `N/A` note.

### 8.3 Medium Priority: Async Shape Detail

- Add an async state distribution histogram to [AsyncStateMachineSectionBuilder](../src/DumpDetective.Reporting/SectionBuilders/AsyncStateMachineSectionBuilder.cs) if the underlying model can expose the extra counts cheaply.

### 8.4 Low Priority: String Encoding Waste

- Add ASCII encoding waste detection to [StringSectionBuilder](../src/DumpDetective.Reporting/SectionBuilders/StringSectionBuilder.cs) only if the I/O cost is acceptable for large dumps.

---

## 3. Analysis Layer Gaps (must fix before correct output is possible)

### 3.1 Bounded forward-BFS retained size — blocks §3.1, §4.1, §4.2, §6.4, §15

`TypeSnapshot.EstimatedRetainedBytes` is always 0. No forward BFS exists.

**Implementation:** Add `BoundedRetainedSizeBfs` in `DumpDetective.Analysis.Utilities`:

```csharp
internal static class BoundedRetainedSizeBfs
{
    // maxBreadth: total objects across all BFS calls per analyzer run
    // maxDepth: per-path hop limit
    // visited: shared across all candidates in one run (passed in from RetentionAnalyzer)
    public static ulong ComputeExclusiveRetained(
        ClrObject root,
        ClrHeap heap,
        HashSet<ulong> visited,
        int maxBreadth = 10_000,
        int maxDepth = 20)
    {
        // Standard BFS with depth tracking via (address, depth) queue.
        // Count only objects first seen in this call (exclusive).
        // Do NOT add to visited — exclusivity is per-candidate, not global.
        // After BFS, add all discovered addresses to visited so other candidates can't claim them.
    }
}
```

Call from `RetentionAnalyzer` on top-N candidates by shallow size (default N=50). Write result into `TypeSnapshot.EstimatedRetainedBytes`. Run all candidates sharing one `visited` set so addresses claimed by an earlier candidate aren't double-counted.

### 3.2 `LeakCandidateAnalyzer` — blocks §6.1–6.4, §14.2

No suspicion-score model exists. All signal inputs exist but are never joined.

**`LeakClass` enum:**
```csharp
public enum LeakClass
{
    StaticRetention,       // ClrStaticField root → candidate
    EventLeak,             // Delegate._invocationList → candidate
    CacheLeak,             // known cache type in Gen2, no eviction signal
    ThreadLocalLeak,       // ThreadLocal<T> → candidate
    FinalizerRetention,    // candidate in finalizer queue
    GCHandleRetention,     // ClrHandle (Strong/Pinned/RefCounted) → candidate
    DependentHandleLeak,   // ClrHandle (Dependent) source alive, target grown
    Unknown                // reachable from root, pattern unrecognised
}
```

**`LeakCandidateRecord`:**
```csharp
public sealed record LeakCandidateRecord(
    string TypeName,
    ulong TotalSize,
    long InstanceCount,
    double Gen2Pct,
    int SuspicionScore,      // 0–100
    LeakClass Classification,
    string? RootKind,
    bool IsFinalizable,
    bool IsContainer,
    double ReferenceFieldRatio);
```

**Score assembly** (all from Phase 1 domain results — no heap scan):

| Signal | Score | Source field |
|---|---|---|
| Gen2Count/Count > 0.8 | +30 | `GCGenerationDomainResult.PerTypeGenerationProfiles` |
| TotalSize > 100 MB | +20 | `TypeAggregateIndexEntry.TotalSize` (via `MemoryDomainResult`) |
| IsFinalizable AND Gen2Count > 1000 | +15 | `TypeAggregateFlags` + `Gen2Count` |
| Type name in `StaticRootDomainResult.TopRootsByBytes` | +10 | Name match |
| Type name in `GCHandleDomainResult.TopPinnedTargetTypes` or strong handle targets | +10 | Name match |
| Type name contains Dictionary/ConcurrentDictionary/Cache/List/Queue | +5 | Pattern match |
| `TypeShapeProfile.ReferenceFieldRatio > 0.5` | +5 | `ObjectShapeAnalyzerDomainResult` |

**Classification logic** (applied after scoring, first match wins):
1. EventLeak — `EventLeakDomainResult` contains `EventLeakGroupSnapshot` whose `TopSubscriberTypes` includes this type
2. StaticRetention — name in `StaticRootDomainResult.TopRootsByBytes`
3. GCHandleRetention — in `GCHandleDomainResult.TopPinnedTargetTypes`
4. DependentHandleLeak — in `DependentHandleDomainResult.TopSourceTargetPairs`
5. FinalizerRetention — in `FinalizableObjectDomainResult.TopQueueEntriesByRetainedSize`
6. CacheLeak — name contains Cache/Dictionary and Gen2Pct > 0.7
7. ThreadLocalLeak — name contains ThreadLocal
8. Unknown — default

Register `LeakCandidateAnalyzer` in `DefaultAnalyzerFactory` with `Order` after all Phase 1 analyzers. It receives `IReadOnlyList<AnalyzerRunResult>` from Phase 1 via constructor injection.

Add corresponding `LeakCandidateFindingGenerator` and retire `MemoryLeakSectionBuilder` once `LeakAnalysisSectionBuilder` is complete.

### 3.3 Missing: `ReservedMemory` in `SegmentAnalyzer` — blocks §9.2, §25

`ClrSegment.ReservedMemory` is not read in `SegmentAnalyzer`. Add to `HeapSegmentSnapshot` and accumulate in `SegmentAnalysisDomainResult` for committed vs reserved gap reporting.

### 3.4 Async continuation chain depth tracking — blocks §8.3

`AsyncTaskAnalyzer` records `MaxContinuationDepth` but not the ordered type sequence of the deepest chains. Add a `TopDeepestChains` list (`IReadOnlyList<ContinuationChainSnapshot>`) to `AsyncTaskDomainResult`:

```csharp
public sealed record ContinuationChainSnapshot(
    int Depth,
    IReadOnlyList<string> TypeSequence);  // root task type → ... → leaf continuation type
```

Track the top-5 deepest chains during existing `m_continuationObject` traversal.

---

## 4. Domain Model Gaps (model-only changes, no heap scanning)

All changes below are additive (no breaking changes to existing fields).

| Field to add | Type | Model class | Analyzer to update | Notes |
|---|---|---|---|---|
| `FrozenPercent` | `double` | `SegmentAnalysisDomainResult` | `SegmentAnalyzer` | `FrozenBytes / TotalCommittedBytes` — one line |
| `IReadOnlyDictionary<int, PerLogicalHeapSummary>` `ByLogicalHeap` | dict | `SegmentAnalysisDomainResult` | `SegmentAnalyzer` | Aggregate `segment.SubHeap?.Index` in existing loop |
| `PerLogicalHeapSummary` | new record | new | `SegmentAnalyzer` | `{int HeapIndex, ulong CommittedBytes, long ObjectCount}` |
| `MinThreads`, `MaxThreads`, `ActiveWorkerThreads`, `IdleWorkerThreads`, `RetiredWorkerThreads`, `QueueLength`, `CpuUtilization` | numeric | `HangDomainResult` | `HangAnalyzer` | Promote from internal `HangAnalysis` helper object |
| `IsStarved` | `bool` | `HangDomainResult` | `HangAnalyzer` | `QueueLength > 0 && ActiveWorkerThreads >= MaxThreads` |
| `ConfidenceScore` | `double?` | `InsightFinding` | all `IFindingGenerator` impls | 1.0=measured, 0.8=high heuristic, 0.5=moderate, <0.5=speculative |
| `Caveats` | `IReadOnlyList<string>` | `InsightFinding` | all `IFindingGenerator` impls | Known limitations for this specific finding |
| `ConfidenceScore`, `Caveats` | same | `FindingRecord` | `ReportSerializer.MapFinding()` | Propagate from `InsightFinding` |
| `TotalTaskContinuations` | `long` | `AsyncTaskDomainResult` | `AsyncTaskAnalyzer` | Sum of non-null `m_continuationObject` during task scan |
| `TopDeepestChains` | `IReadOnlyList<ContinuationChainSnapshot>` | `AsyncTaskDomainResult` | `AsyncTaskAnalyzer` | Top-5 deepest chains with type sequence (see §3.4) |
| `OrphanedTaskException` (type + message) | `string?`, `string?` | `OrphanedTaskSnapshot` | `AsyncTaskAnalyzer` | Read `_exceptionObject` field on faulted tasks |
| `IsArray` | `bool` | `TypeShapeProfile` | `ObjectShapeAnalyzer` | `ClrType.IsArray` during existing shape scan |
| `IsPEFile` | `bool` | `LoadedModuleSnapshot` | `ModuleAnalyzer` | `ClrModule.IsPEFile` |
| Per-domain module list | `IReadOnlyList<LoadedModuleSnapshot>` | `AppDomainSnapshot` | `AppDomainAnalyzer` | `domain.Modules` during existing enumeration |
| `DynamicModuleBytes` | `ulong` | `ModuleDomainResult` | `ModuleAnalyzer` | Sum sizes of `IsDynamic` modules |
| `ExceptionChainDepth` | `int` | `ExceptionInstanceSnapshot` | `CrashAnalyzer` | Count `_innerException` hops during existing traversal |
| `ChainDepthHistogram` | `IReadOnlyDictionary<int, int>` | `CrashDomainResult` | `CrashAnalyzer` | Depth → count of exceptions at that depth |
| `PerThreadStackSize` | `ulong` | `ThreadStateSnapshot` | `ThreadAnalyzer` | `ClrThread.StackBase - ClrThread.StackLimit`; 0 if unavailable |
| `SkipReason` | `string?` | `AnalyzerRunResult` | `AnalysisPipeline` | Populated when Status = Skipped |
| `ReservedBytes` | `ulong` | `HeapSegmentSnapshot` | `SegmentAnalyzer` | `ClrSegment.ReservedMemory` |

---

## 5. Section Builder: `LeakExplainer` Helper

Static helper in `LeakAnalysisSectionBuilder`. One template string per `LeakClass`. Parameterised with type name, root field/type, retained bytes.

```csharp
internal static class LeakExplainer
{
    public static string Explain(LeakCandidateRecord c, string? rootField = null) => c.Classification switch
    {
        LeakClass.StaticRetention =>
            $"{c.TypeName} is retained by a static field{(rootField != null ? $" ({rootField})" : "")} and cannot be collected. " +
            $"Total retained: ~{FormatBytes(c.TotalSize)}. Review the static field lifetime; consider scoped DI registration.",

        LeakClass.EventLeak =>
            $"{c.TypeName} instances are held alive by event subscriptions. " +
            $"A long-lived publisher is preventing {c.InstanceCount:N0} subscriber objects from being collected. " +
            $"Unsubscribe in Dispose() or use WeakEventManager / IObservable.",

        LeakClass.CacheLeak =>
            $"{c.TypeName} appears to be an unbounded cache: {c.InstanceCount:N0} instances ({FormatBytes(c.TotalSize)}) are in Gen2 with no eviction signal. " +
            $"Apply a size limit (MemoryCache), use WeakReference values, or add an eviction policy.",

        LeakClass.ThreadLocalLeak =>
            $"{c.TypeName} is referenced via ThreadLocal<T> and is being retained per thread. " +
            $"Ensure Dispose() is called on the ThreadLocal wrapper when threads finish.",

        LeakClass.FinalizerRetention =>
            $"{c.TypeName} is queued for finalization and is retaining sub-graph objects during the delay. " +
            $"Implement IDisposable + GC.SuppressFinalize to avoid queuing.",

        LeakClass.GCHandleRetention =>
            $"{c.TypeName} is pinned or strongly referenced via a GC handle. " +
            $"Verify the handle is freed when the object is no longer needed (GCHandle.Free).",

        LeakClass.DependentHandleLeak =>
            $"{c.TypeName} is kept alive as the value in a ConditionalWeakTable where the key is still reachable. " +
            $"Review the table's owner lifetime and consider explicit cleanup.",

        LeakClass.Unknown =>
            $"{c.TypeName} is reachable from a GC root but the retention pattern was not recognised. " +
            $"Investigate using the root paths in §5 and the dominator candidates in §3.2.",

        _ => $"{c.TypeName}: {c.SuspicionScore} suspicion score. Manual investigation required."
    };

    private static string FormatBytes(ulong b) => FormatHelper.FormatBytes(b);
}
```

---

## 6. InsightEngine Changes

### 6.1 Failed-analyzer Warning

Add to `InsightEngine.Analyze()`:

```csharp
int failedCount = runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
if (failedCount >= 3)
{
    yield return new InsightFinding(
        Analyzer: "InsightEngine",
        Category: "Pipeline",
        Severity: FindingSeverity.Warning,
        Title: $"Analysis quality degraded: {failedCount} analyzers failed",
        Evidence: $"{failedCount} of {runs.Count} analyzers did not complete successfully. " +
                  "Findings that depend on these analyzers may be absent or inaccurate.",
        Recommendation: "Review the Confidence & Limitations section (§17) for per-analyzer error details.",
        ConfidenceScore: 1.0,  // the failure itself is observed fact
        Caveats: []);
}
```

### 6.2 ConfidenceScore population in existing detectors

Each `InsightEngine` detection method must be updated to populate `ConfidenceScore`:

| Detector | Score | Rationale |
|---|---|---|
| `DetectLeakSuspicion` (static root found) | 0.8 | High-confidence heuristic |
| `DetectLeakSuspicion` (Gen2 only) | 0.5 | Moderate — could be long-lived legitimate objects |
| `DetectAllocationPressureCrossCorrelation` | 0.7 | Two corroborating signals |
| `DetectBoxingGCCorrelation` | 0.6 | Pattern-matched, not measured |
| `DetectThreadContention` | 0.9 | Directly observed from thread state |
| `DetectDeadlock` (confirmed cycle) | 1.0 | Directly measured via DFS |
| `DetectDeadlock` (suspected) | 0.6 | Heuristic only |

Each `IFindingGenerator` implementation must similarly populate `ConfidenceScore` for all findings it emits, following the scale: 1.0=directly measured via ClrMD, 0.8=high-confidence heuristic, 0.5=moderate heuristic, <0.5=speculative.

---

## 7. Analyzer Filter / Skip Graceful Degradation

Implemented in code: `AnalyzerFilterService.BuildSkippedByFilterResults()` synthesizes `SkippedByFilter` runs, `RunAnalyzersPipelineStage` prepends them to `state.Runs`, and `ConfidenceSectionBuilder` renders the resulting four-state status table.

### 7.1 The Problem: Filtered Analyzers Are Invisible

`AnalyzerFilterService.Apply()` removes analyzers from the execution list _before_ the pipeline runs. Filtered analyzers never produce an `AnalyzerRunResult` entry. This creates two problems:

1. **`IReportSectionBuilder.CanBuild()`** receives an `AnalyzerResultSet` with no entry for the filtered analyzer. A builder that guards with `results.Get<T>() != null` silently drops the entire section — no indication to the reader that it was suppressed by a CLI flag.
2. **`ConfidenceSectionBuilder` (§17)** iterates `AllRuns` to build the per-analyzer status table. Filtered analyzers are absent from that list entirely — the confidence section has no knowledge of what was excluded.

### 7.2 Fix: Synthetic `SkippedByFilter` Entries in `SingleDumpOrchestrationService`

After `AnalyzerFilterService.Apply()` returns the filtered-down list, emit one synthetic `AnalyzerRunResult` per filtered-out analyzer:

```csharp
// In SingleDumpOrchestrationService, after Apply():
IReadOnlyList<IAnalyzer> allRegistered = _analyzerFactory.CreateAll();
IReadOnlyList<IAnalyzer> toRun        = _filterService.Apply(allRegistered, options);

var syntheticSkipped = new List<AnalyzerRunResult>();
foreach (var skipped in allRegistered.Except(toRun))
{
    syntheticSkipped.Add(new AnalyzerRunResult(
        AnalyzerName: skipped.Name,
        Status:       AnalyzerExecutionStatus.SkippedByFilter,
        Result:       null,
        Findings:     [],
        DurationMs:   0,
        SkipReason:   "Excluded by --include-analyzers / --exclude-analyzers filter"));
}

// Merge synthetic entries with pipeline results before constructing AnalyzerResultSet.
IReadOnlyList<AnalyzerRunResult> allResults = [..pipelineResults, ..syntheticSkipped];
var resultSet = new AnalyzerResultSet(allResults);
```

These synthetic entries propagate to every consumer — `AnalyzerResultSet`, `ReportSerializer`, and `ConfidenceSectionBuilder` — without any per-consumer changes.

### 7.3 Four-State `AnalyzerExecutionStatus`

Distinguish four outcomes. The existing `Skipped` value (used by `AnalysisPipeline` for cancellation) must be split:

| Status | Meaning |
|---|---|
| `Success` | Analyzer ran and returned a result |
| `Failed` | Analyzer ran but threw or returned an error |
| `SkippedByFilter` | Removed by `AnalyzerFilterService` before execution (synthetic entry) |
| `SkippedByCancellation` | Pipeline was cancelled before this analyzer ran |

Update `AnalysisPipeline` to emit `SkippedByCancellation` instead of the generic `Skipped`. Update `SingleDumpOrchestrationService` to emit `SkippedByFilter` as shown in §7.2.

`AnalyzerRunResult.SkipReason` (already listed in §4 domain model gaps) is populated only for `SkippedByFilter` and `SkippedByCancellation`. `ReportSerializer.MapFinding()` and `ConfidenceSectionBuilder` must handle all four states.

### 7.4 Required vs Optional in `IReportSectionBuilder.CanBuild()`

Split each builder's domain-result dependencies into **required** (section cannot render at all — `CanBuild` returns `false`) and **optional** (section degrades gracefully — missing results produce annotated columns inside `Build`).

```csharp
// Example: LeakAnalysisSectionBuilder
public bool CanBuild(AnalyzerResultSet results)
{
    // Required: without leak candidates there is nothing to show.
    return results.Get<LeakCandidateDomainResult>() != null;
}

public AnalyzerDetailSection Build(AnalyzerResultSet results)
{
    var leakResult = results.Get<LeakCandidateDomainResult>()!;
    var gcGen      = results.Get<GCGenerationDomainResult>();   // optional

    // Gen2% column: when gcGen is null, fill every cell with "(analyzer not run)".
    bool hasGen2 = gcGen != null;
    // ... render table rows; Gen2% cell = hasGen2 ? pct.ToString("P1") : "(analyzer not run)"
}
```

**Required / optional split for builders most affected by filtering:**

| Builder | Required (gates `CanBuild`) | Optional (annotated in `Build`) |
|---|---|---|
| `ExecutiveSummarySectionBuilder` | `MemoryDomainResult` | `AllocationPatternDomainResult`, `ThreadDomainResult`, `GCGenerationDomainResult` |
| `MemoryTopologySectionBuilder` | `SegmentAnalysisDomainResult` | `AllocationPatternDomainResult`, `GCGenerationDomainResult`, `SegmentReservationDomainResult` |
| `TypeSystemSectionBuilder` | `MemoryDomainResult` | `GCGenerationDomainResult`, `ObjectShapeAnalyzerDomainResult`, `ModuleDomainResult` |
| `RetentionDominatorSectionBuilder` | `RetentionDomainResult` | `GCRootDomainResult`, `StaticRootDomainResult`, `EventLeakDomainResult`, `FinalizableObjectDomainResult` |
| `GCRootIntelligenceSectionBuilder` | `GCRootDomainResult` | `LeakCandidateDomainResult` |
| `LeakAnalysisSectionBuilder` | `LeakCandidateDomainResult` | `GCGenerationDomainResult` |
| `ThreadConcurrencySectionBuilder` | `ThreadDomainResult` | `HangDomainResult`, `LockGraphDomainResult` |
| `AsyncAnalysisSectionBuilder` | `AsyncTaskDomainResult` | `HangDomainResult` |
| `GCPressureSectionBuilder` | `GCGenerationDomainResult` | `AllocationPatternDomainResult`, `SegmentReservationDomainResult`, `GCHandleDomainResult`, `SegmentAnalysisDomainResult` |
| `ExceptionAnalysisSectionBuilder` | `CrashDomainResult` | `ThreadDomainResult`, `ModuleDomainResult` |
| `InsightsSectionBuilder` | _(none — always renders; may be empty)_ | all runs |
| `ConfidenceSectionBuilder` | _(none — always renders)_ | all runs |
| `AppDomainAssemblySectionBuilder` | `AppDomainDomainResult` | `ModuleDomainResult` |

Rule of thumb: a result is **required** when its absence means the section's primary table/content is empty. It is **optional** when only a subset of columns or supplementary sub-sections are affected.

### 7.5 `ConfidenceSectionBuilder`: Full Registered Analyzer List

Thanks to the synthetic entries from §7.2, `ConfidenceSectionBuilder` can iterate `AllRuns` and see every analyzer — including those filtered out — without any extra DI injection. The synthetic entry carries `Status = SkippedByFilter` and `SkipReason = "Excluded by --include-analyzers / --exclude-analyzers filter"`.

Render the §17 status table with a `Status` column that maps the four-state enum (§7.3) to display strings:

| `AnalyzerExecutionStatus` | Display string |
|---|---|
| `Success` | `Completed` |
| `Failed` | `Failed` |
| `SkippedByFilter` | `Skipped (filter)` |
| `SkippedByCancellation` | `Skipped (cancelled)` |

The aggregate line under the table reads: `"{completed} completed, {failed} failed, {skippedFilter} excluded by filter, {skippedCancel} cancelled"`.

---

## 8. Priority Order

Status update:
- P0 complete: architecture unblock landed and validated.
- P1 complete: retained-size BFS, leak-candidate heuristics, segment reserved-byte plumbing, and leak-analysis report wiring are implemented.
- P2 complete: retention/root synthesis, critical-finding narratives, structured actionability projection, and confidence normalization are now surfaced in the report layer.
- P3 complete: finding confidence is now first-class in the model and report projection, and all findings receive a default confidence score with explicit overrides where needed.
- P4 complete: `ExecutiveSummarySectionBuilder`, `MemoryTopologySectionBuilder`, `InsightsSectionBuilder`, `GCPressureSectionBuilder`, and the remaining cross-analyzer sections are implemented and registered.
- P5 complete: `GCRootSectionBuilder`, `LohFragmentationSectionBuilder`, `AllocationPatternSectionBuilder`, `FinalizableObjectSectionBuilder`, `StringSectionBuilder`, `JitSectionBuilder`, `SegmentReservationSectionBuilder`, `ObjectShapeSectionBuilder`, `GCGenerationSectionBuilder`, `HangSectionBuilder`, `CrashSectionBuilder`, and `EventLeakSectionBuilder` are implemented.
- P6 complete: trend comparison now emits explicit new-type lists, typed trend classifications, and severity escalations across snapshots.

Implementation complete: the remaining details below are retained as the authored record of the completed work and the current report contract.

```
P0 — Architecture unblock (enables all subsequent work to be delivered independently)
  ├── AnalyzerResultSet (with lazy caching)
  ├── IReportSectionBuilder interface (with required/optional CanBuild contract — see §7.4)
  ├── ReportSerializer dual-pass + MergeSections
  ├── DefaultSectionBuilderFactory extension
  ├── AnalyzerExecutionStatus four-state enum (Success/Failed/SkippedByFilter/SkippedByCancellation — §7.3)
  ├── AnalysisPipeline: emit SkippedByCancellation (replace generic Skipped)
  └── SingleDumpOrchestrationService: emit synthetic SkippedByFilter entries (§7.2)

    Status: complete

P1 — Analysis layer (implemented)
  ├── BoundedRetainedSizeBfs utility
  ├── LeakCandidateAnalyzer + LeakCandidateDomainResult + LeakClass enum
  └── ReservedMemory in SegmentAnalyzer

        Status: complete

P2 — Domain model (fast changes; can parallelise across team)
  ├── InsightFinding.ConfidenceScore + Caveats + FindingRecord propagation
  ├── HangDomainResult thread pool fields (MinThreads … IsStarved)
  ├── SegmentAnalysisDomainResult.ByLogicalHeap + PerLogicalHeapSummary + FrozenPercent
  ├── AsyncTaskDomainResult.TotalTaskContinuations + TopDeepestChains
  ├── OrphanedTaskSnapshot exception fields
  ├── TypeShapeProfile.IsArray
  ├── LoadedModuleSnapshot.IsPEFile
  ├── AppDomainSnapshot per-domain module list
  ├── ModuleDomainResult.DynamicModuleBytes
  ├── CrashDomainResult ExceptionChainDepth + ChainDepthHistogram
  ├── ThreadStateSnapshot.PerThreadStackSize
  └── AnalyzerRunResult.SkipReason

P3 — InsightEngine changes
  ├── Failed-analyzer Warning (≥3 failures)
  └── ConfidenceScore populated in all detectors and finding generators

P4 — New cross-analyzer section builders (implemented)
  ├── ExecutiveSummarySectionBuilder (§1)
  ├── MemoryTopologySectionBuilder (§2)
    ├── TypeSystemSectionBuilder (§3) — DominatorCandidateBuilder inline
        ├── RetentionDominatorSectionBuilder (§4) — uses bounded retained-size BFS
  ├── GCRootIntelligenceSectionBuilder (§5)
    ├── LeakAnalysisSectionBuilder + LeakExplainer (§6) — implemented
    ├── ThreadConcurrencySectionBuilder (§7)
    ├── AsyncAnalysisSectionBuilder (§8)
  ├── GCPressureSectionBuilder (§9)
  ├── HeapSegmentDiagnosticsSectionBuilder (§10)
    ├── ExceptionAnalysisSectionBuilder (§13)
    ├── InsightsSectionBuilder (§16)
  ├── ConfidenceSectionBuilder (§17) — four-state status table; full coverage via synthetic entries from P0
    └── AppDomainAssemblySectionBuilder (§18)

P5 — Existing section builder fixes (independent; can be done any time)
  ├── GCRootSectionBuilder: group paths by target type; [TRUNCATED] annotation
    ├── LohFragmentationSectionBuilder: severity band
  ├── AllocationPatternSectionBuilder: enum→spec label mapping
  ├── ObjectShapeSectionBuilder: threshold caveat and Mixed label
  ├── GCGenerationSectionBuilder: TopGen0Types / TopGen1Types filter
  ├── HangSectionBuilder: emit thread pool fields once P2 done
  ├── CrashSectionBuilder: frame origin classification
  ├── FinalizableObjectSectionBuilder: severity band on queue count
  ├── EventLeakSectionBuilder: publisher generation aggregate
  ├── SegmentReservationSectionBuilder: VA gap analysis
  └── StringSectionBuilder: estimated interning saving calculation

P6 — Trend enhancements (TrendReportComposer changes) [complete]
  ├── NewTypes explicit list (set-difference after MemoryAnalyzerTrendComparer)
  ├── TrendClassification enum on MetricDelta
  └── Severity escalation detection (Warning→Critical across snapshots)
```

---

## 9. What Stays, What Changes, What Goes

| Component | Fate |
|---|---|
| Existing per-analyzer `IAnalyzerSectionBuilder` implementations (30 builders) | **Keep** — continue to work as-is; retire gradually as spec builders replace them |
| `IAnalyzerSectionBuilder` interface | **Keep unchanged** |
| `IFindingGenerator` / `FindingGenerationPipeline` | **Keep unchanged** — findings remain per-analyzer |
| `ReportSerializer.BuildAnalyzerSections()` | **Keep** — becomes Pass 1; add Pass 2 alongside |
| `AnalyzerResultSet` | **New** — `src/DumpDetective.Reporting/Models/` |
| `IReportSectionBuilder` | **New** — `src/DumpDetective.Reporting/Abstractions/` |
| `DefaultSectionBuilderFactory` | **Extend** — inject `IEnumerable<IReportSectionBuilder>` |
| `BoundedRetainedSizeBfs` | **New** — `src/DumpDetective.Analysis/Utilities/` |
| `LeakCandidateAnalyzer` + domain models + finding generator + section builder | **New** — `src/DumpDetective.Analysis/` + `src/DumpDetective.Reporting/` |
| `LeakExplainer` | **New** — inline static class inside `LeakAnalysisSectionBuilder` |
| `MemoryLeakSectionBuilder` | **Retire** after `LeakAnalysisSectionBuilder` is complete |
| All 14 new `IReportSectionBuilder` implementations | **New** — `src/DumpDetective.Reporting/SectionBuilders/` |

---

## 10. Acceptance Criteria

**Architecture:**
- `AnalyzerResultSet.Get<T>()` is the only mechanism by which spec section builders access domain results.
- No `IReportSectionBuilder` implementation calls into the analysis layer or performs heap traversal.
- All existing per-analyzer section builders compile and produce output unchanged after P0.

**Analysis:**
- `BoundedRetainedSizeBfs` respects breadth 10 000 / depth 20 on a 10 GB+ dump without OOM.
- `LeakCandidateAnalyzer` produces a non-empty scored list on any dump with >50 MB Gen2 objects.
- `LeakCandidateAnalyzer` runs in < 2 seconds on any dump (it performs no heap scan).

**Completeness:**
- All spec §1–§25 sections render with data sourced from the correct domain results — verified by golden report tests.
- Every Critical/Warning finding carries: `Analyzer`, `Category`, `Severity`, `Evidence`, `Recommendation`, `ConfidenceScore` (non-null), `Fingerprint`.
- `ConfidenceScore` is populated (non-null) on all Critical and Warning findings.
- ≥3 failed analyzers always produces at least one Warning finding in the Insights section.
- The "Confidence & Limitations" section lists all per-analyzer run statuses with four-state normalization: `Completed/Failed/Skipped (filter)/Skipped (cancelled)`.
- When `--exclude-analyzers` or `--include-analyzers` filters are active, every excluded analyzer appears in the §17 table as `Skipped (filter)` — not absent.
- No `IReportSectionBuilder` returns `false` from `CanBuild()` due to an optional (non-required) result being absent; optional results are annotated inside `Build()` as `"(analyzer not run)"`.
- `AnalyzerResultSet.Get<T>()` returns `null` for filtered analyzers (their synthetic entry has `Result = null`), and `CanBuild()` on builders whose only absent result is optional still returns `true`.

**Sections specifically validated by golden tests:**
- §1: Executive summary contains TotalBytes, at least one recommendation, GC pressure level.
- §3.2: Dominator candidate table contains ≥1 row on any non-trivial dump.
- §5.3: Root paths are grouped by target type; [TRUNCATED] appears when `WasCapped = true`.
- §6: Leak candidate table populated (non-empty) once P1 ships.
- §7.1: Thread pool fields appear when `RuntimeThreadPoolDataAvailable = true`.
- §10.2: LOH fragmentation severity band appears for any dump with `FragmentationPercent > 0`.
- §13.2: Frame origin labels (UserCode/FrameworkCode/ThirdParty) appear on all exception frames. 

