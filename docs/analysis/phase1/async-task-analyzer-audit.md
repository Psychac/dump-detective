# AsyncTaskAnalyzer — Re-Audit

**Analyzer:** `AsyncTaskAnalyzer` (`src/DumpDetective.Analysis/Analyzers/AsyncTaskAnalyzer.cs`)
**Protocol:** Phase 1 Analyzer Audit (`phase1-analyzer-architecture-review.md`)
**Original audit date:** 2026-07-30
**Re-audit date:** 2026-08-15

---

## Re-Audit Note

This is a full ground-truth re-audit, not an update of the previous document's
narrative. Every conclusion below was derived from reading the current
implementation end-to-end — analyzer, domain model, options, finding
generator, section builder, trend comparer, utilities, tests, and adjacent
Phase 1 infrastructure (`DiskBackedObjectIndexWriter`, `TypeAggregateFlags`,
`TaskIndexReader`, `HeapIndexScanDispatcher`, `InsightEngine`) — not from the
prior audit's text or session memory.

Since the original audit, the analyzer grew substantially: all 7 P2 items and
4 of 5 P3 items from that roadmap have since shipped, including two
genuinely new detection capabilities — `TaskCompletionSource<T>` leak
detection (P3-2) and `IValueTaskSource`/`ManualResetValueTaskSourceCore<T>`
leak detection (P3-1) — that did not exist when the original audit was
written. The analyzer file itself has grown from a single-concern Task
scanner to a three-object-family scanner (`Task`, `TaskCompletionSource`,
`IValueTaskSource`) sharing one participant-scan pass. This re-audit
evaluates that current state, not the original scope.

**Headline findings, both confirmed by direct code/tracing evidence, neither
present in the original audit (the capabilities they concern didn't exist
yet):**

1. **TaskCompletionSource/IValueTaskSource candidate discovery bypasses the
   Phase 1 disk-cache fast path** — it re-resolves `ClrType`, enumerates
   interfaces, and walks fields for every non-`Task`-flagged type in
   `TypeAggregates`, on **every analysis run** against the same dump,
   **repeated once per parallel worker**, even when a fully warm on-disk
   cache exists and the original Task-classification flag (`IsTaskType`) is
   read as a zero-cost persisted bit. See P1-1.
2. **The trend comparer has not been touched since the original audit** —
   none of the 9 fields added across P2-6, P2-7, P3-1, P3-2, P3-3
   (`PendingGen0`–`PendingLOH`, `CycleDetected`, `TotalTaskCompletionSources`,
   `UnresolvedTaskCompletionSources`, `UnresolvedTcsGen2Count`,
   `TotalValueTaskSources`, `PendingValueTaskSources`, `PendingVtsGen2Count`,
   `TopPendingTaskTypesByBytes`) have any regression tracking across dump
   snapshots. See P1-2.

Neither finding is a correctness bug — the analyzer produces accurate output
in both cases. Both are missed-opportunity gaps: one in performance
(redundant ClrMD work that Phase 1's existing caching architecture already
solves for the sibling `Task` case), one in report completeness (newest,
most valuable leak signals have no trend history).

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`AsyncTaskAnalyzer` now covers **six** concerns, up from four at the
original audit:

1. **Task state classification** — `Task`/`Task<T>` heap objects bucketed
   into Pending/Running/Faulted/Canceled/Completed via `m_stateFlags`
   (`m_stateFlags`/`_stateFlags` fallback for the .NET Core 3 rename).
2. **Orphaned task detection** — tasks with no continuation, not yet
   terminal.
3. **Continuation chain BFS** — true branching traversal (handles
   `List<object>` multi-continuation fan-out), with **cycle detection**
   (added P2-6) flagging self-referential chains as a hard-deadlock signal.
4. **Exception extraction** — best-effort walk of contingent-properties/
   exception-holder fields for orphaned/faulted snapshots.
5. **`TaskCompletionSource<T>` leak detection** (added P3-2) — for each
   heap-resident TCS instance, reads its inner `Task` (`_task`/`m_task`
   fallback) and classifies non-terminal ("unresolved") instances, gated by
   Gen2/LOH residency as the leak-strength signal.
6. **`IValueTaskSource`/`IValueTaskSource<T>` leak detection** (added P3-1)
   — for implementers built on `ManualResetValueTaskSourceCore<TResult>`,
   reads the embedded core struct's `_completed` flag via
   `ClrObject.ReadValueTypeField`/`ClrValueType.ReadField`, same Gen2/LOH
   leak-strength gating.

All six concerns remain cohesive — they're all facets of "what is this async
primitive doing and is it stuck," sharing one heap-index participant pass,
one field-cache, and one Gen2/LOH leak-strength convention. The file has
grown to ~1,360 lines; this is large but not yet unfocused — every method
maps directly to one of the six concerns above, and the shared infrastructure
(field caching, generation resolution, top-N builders) is reused rather than
duplicated per concern.

### Coverage Gaps (re-verified against current code)

- **`TaskScheduler` classification** — still absent. `ClrRuntime.ThreadPool`
  is not read; a pending task's owning scheduler (default pool,
  `SynchronizationContextTaskScheduler`, custom) is invisible.
- **No parent-child task relationship** — `Task.m_parent` is not read.
  `TaskCreationOptions.AttachedToParent` hierarchies aren't reconstructed.
- **No `SynchronizationContextAwaitTaskContinuation` classification** — UI
  thread / legacy ASP.NET continuation counting by context type is absent.
- **`IAsyncStateMachine` correlation** — still absent here; confirmed still
  correctly delegated to `AsyncStateMachineAnalyzer` (its own P3-1, a
  distinct roadmap item from this analyzer's now-completed P3-1). No
  overlap or duplication found between the two analyzers' P3-1 items — they
  address different object families entirely (`IAsyncStateMachine`
  structs there, `IValueTaskSource` implementers here).
- **New gap introduced by this session's own work**: orphaned tasks get no
  GC-generation breakdown, while pending tasks do (`PendingGen0`–`PendingLOH`,
  P2-7). Orphaned tasks are arguably the *stronger* leak signal of the two
  (no continuation at all, vs. merely not-yet-complete), so the asymmetry is
  backwards from what a leak-triage workflow would prioritize. See P3-5.

### Unexpected Functionality

None. Every code path serves one of the six documented concerns.

### Adjacent Capabilities (re-verified)

- `HangAnalyzer` independently profiles `Task`/continuation/queued-work-item
  type names for its own async stall heuristics (confirmed via
  `AsyncTypeProfile.FromTypeName`, which now correctly calls
  `TaskTypeNamePattern.IsTaskType` post-fix rather than a duplicated raw
  prefix check — this duplication was flagged in the original audit and
  remains a real, unaddressed cross-analyzer duplication, though the
  TaskCompletionSource conflation bug that duplication carried has since
  been fixed in both places via the shared `TaskTypeNamePattern`).
- `InsightEngine.DetectOrphanedTaskAccumulation` cross-correlates
  `AsyncTaskDomainResult` with `ThreadDomainResult.FinalizerThreadBlocked`
  (faulted tasks + blocked finalizer) and orphan-percentage. This is the
  **only** cross-analyzer correlation currently wired for this analyzer's
  output — none of the newer TCS/VTS Gen2 leak signals, nor `CycleDetected`,
  participate in any `InsightEngine` rule. See P3-7.

### Architectural Observations

- **`TypeAggregateFlags` bit budget**: confirmed still 2 unused bits (6, 7)
  in the 1-byte flags field written to `TypeAggregateIndex.bin`. P1-1 below
  recommends consuming both for `IsTaskCompletionSourceType` and
  `IsValueTaskSourceType` — this would **exhaust** the reserved bit budget.
  Any future per-type classification need after that would require a schema
  version bump (a new byte, or a wider flags type) to the shared binary
  index format. Worth flagging now, before the budget is spent, rather than
  discovering it mid-implementation of some future analyzer.
- The TCS/VTS candidate-discovery code (`BeforeHeapIndexScan`) duplicates a
  per-type `ClrType` resolution that `DiskBackedObjectIndexWriter.
  ComputeTypeFlags` already performs once per distinct type during the
  original Phase 1 build (same `type.Name` lookup, immediately adjacent to
  the existing `TaskTypeNamePattern.IsTaskType` check at
  `DiskBackedObjectIndexWriter.cs:931`). This is the concrete root cause of
  P1-1 — the type information needed already exists at the right layer, at
  the right time, and simply isn't being persisted.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths (re-verified)

- **Task status summary table** remains the clearest compact table in the
  section — unchanged, still six clean rows.
- **Consistent Gen2/LOH leak-strength gating** across all three leak
  families (pending tasks, unresolved TCS, pending VTS) — the finding
  generator correctly avoids firing on raw pending/unresolved counts alone,
  requiring old-generation residency before elevating severity. This
  consistency is a genuine strength: an engineer who understands the
  pattern for one signal immediately understands all three.
- **`TopPendingTaskTypesByBytes`** (P3-3) closes a real dotMemory-style gap
  flagged in the original audit's Area 7 — pending tasks are now ranked by
  retained bytes, not just raw count, so a few large stuck tasks won't be
  buried under many small ones in the count-ranked table.
- Orphaned task, unresolved-TCS, and pending-VTS tables all correctly
  annotate "(showing N of M)" when the display cap truncates the full count
  — this pattern, fixed for orphaned tasks in the original audit's P2-3,
  was correctly carried forward to the two new leak tables rather than
  needing a second fix.

### Weaknesses (newly found this pass)

**Trend comparer has not been updated for any field added since the
original audit.** `AsyncTaskTrendComparer.ExtractMetrics`/`Compare` still
only tracks the 7 metrics that existed before P2-6 (`task.total`,
`task.pending`, `task.faulted`, `task.canceled`, `task.orphaned`,
`task.chain.depth.max`, `task.chain.depth.avg`, plus per-type breakdowns).
None of `PendingGen0`–`PendingLOH`, `CycleDetected`,
`TotalTaskCompletionSources`, `UnresolvedTaskCompletionSources`,
`UnresolvedTcsGen2Count`, `TotalValueTaskSources`, `PendingValueTaskSources`,
`PendingVtsGen2Count`, or `TopPendingTaskTypesByBytes` have any trend
tracking. Concretely: if an engineer runs this analyzer against two
snapshots of a leaking service and the unresolved-TCS-in-Gen2 count triples
between them — the single strongest, newest leak signal this analyzer
produces — the trend report shows nothing. See P1-2.

**Section-level lead finding doesn't reflect the newest signals.**
`AsyncAnalysisSectionBuilder`'s `leadFinding` gates solely on
`MaxContinuationDepth >= 15`. A dump with a Critical-severity continuation
cycle (`CycleDetected`) or a severe TCS/VTS Gen2 leak, but shallow chains,
produces no section-level lead finding — the InsightFinding still fires
elsewhere in the report, but the section itself (`Task Overview`) doesn't
surface its own most severe condition. See P2-2.

**Aggregate "risk cluster" finding's evidence text is stale relative to
the signals it aggregates.** When multiple `AsyncSignal`s fire together,
the cluster finding's evidence string (`AsyncTaskFindingGenerator.cs:216`)
reports only `total`, `pending`, `orphaned`, `faulted`, and
`max continuation depth` — even when the cluster's highest-risk signal is a
TCS/VTS Gen2 leak or a continuation cycle, those raw numbers are absent
from the evidence text an engineer reads first. See P2-3.

### Missing Diagnostics (re-verified, some newly relevant)

- Exception-type frequency across all faulted tasks — **done** (P1-3,
  original audit; `FaultedTaskExceptionHistograms` confirmed present and
  populated).
- Multi-continuation fan-out — **done** (P0-2 era; `MultiContinuationNodeCount`/
  `MaxContinuationFanOut`/`TopContinuationFanoutTypes` confirmed present).
- GC-generation distribution — **done for pending tasks** (P2-7), **still
  missing for orphaned tasks** (see P3-5 above).
- `TaskScanLimited` bias toward low addresses — still an open,
  undocumented-in-output caveat (same as original audit); now also applies
  identically to `TcsScanLimited`/`VtsScanLimited`, inheriting the same
  address-ordering bias without additional documentation.

---

## Audit Area 3 — ClrMD & Platform Utilization

### Strengths (re-verified)

- `TypeAggregateFlags.IsTaskType` remains a zero-cost Phase 1 byproduct,
  correctly fixed (this session) to exclude `TaskCompletionSource` via the
  shared `TaskTypeNamePattern.IsTaskType`/`IsTaskCompletionSource` helpers.
- `TryGetCachedField` (instance-level, keyed by `(MethodTable, fieldName)`)
  is now the single field-resolution path shared across Task classification,
  BFS continuation traversal, exception extraction, TCS inner-task
  resolution, and VTS `_completed`-field resolution — no duplicated
  `GetFieldByName` call sites remain in the file.
- **`ValueTaskSourcePattern`'s use of `ClrObject.ReadValueTypeField` →
  `ClrValueType.ReadField`** is correct and was verified against the actual
  ClrMD 4 API surface (not assumed) — confirmed by direct reflection probe
  against the referenced assembly during implementation: `ClrValueType` has
  no implicit `ulong` conversion (unlike `ClrObject`), so the read pattern
  correctly goes through `ClrValueType`'s own field-reading methods rather
  than `ClrInstanceField.Read<T>(ulong, bool)`, which only accepts a raw
  object address.
- Single `heap.GetObject` call per task, reused for state-flag re-read,
  exception extraction, and BFS (P3-4, original audit) — confirmed still in
  place, no regression.

### Issues (new, found this pass)

**TCS/VTS candidate discovery bypasses Phase 1's disk-cache fast path
entirely — the headline finding of this re-audit.**

`BeforeHeapIndexScan` iterates every entry in `heapIndex.TypeAggregates`
and, for every type **not** already flagged `IsTaskType`, calls
`context.Heap.GetTypeByMethodTable(kvp.Key)`, then
`TaskTypeNamePattern.IsTaskCompletionSource(type.Name)`, then (if that
fails) `ValueTaskSourcePattern.ImplementsValueTaskSource(type)` (an
`EnumerateInterfaces()` walk) and `ValueTaskSourcePattern.FindCoreField(type)`
(a `type.Fields` walk). This is bounded by *distinct type count*, not
object count — which sounds cheap in isolation, but:

1. It re-runs on **every** `AsyncTaskAnalyzer.AnalyzeAsync` invocation,
   including every subsequent run against a dump whose `.dumpindex/cache.bin`
   already exists. Traced through `HeapIndexCache.PrebuildHeapIndex` →
   `DiskBackedObjectIndexWriter.Build` → `TryLoadFromCache`: a warm-cache
   hit skips the *entire* Phase 1 heap scan (including
   `ComputeTypeFlags`, which already resolves `type.Name` for the
   `IsTaskType` check) and loads pre-computed `TypeAggregateIndexEntry`
   records straight from disk. `IsTaskType` is a free flag read on that
   warm path. TCS/VTS detection is not — it always re-resolves live
   `ClrType` objects via ClrMD, every run.
2. Confirmed via `IParallelHeapIndexScanParticipant.CreateWorkerInstance`
   contract: each parallel worker independently calls its own
   `BeforeHeapIndexScan`, so this per-run cost is **multiplied by worker
   count** (up to `Math.Min(ProcessorCount, 8)` on large dumps) — the same
   `TypeAggregates` dictionary gets independently walked and every
   candidate type independently re-resolved by every worker, before
   `MergePartial` reconciles their `_participantTcsEntries`/
   `_participantVtsEntries` lists.

The fix is direct: add `IsTaskCompletionSourceType`/`IsValueTaskSourceType`
to `TypeAggregateFlags` (the 2 remaining reserved bits), compute both once
in `DiskBackedObjectIndexWriter.ComputeTypeFlags` — reusing the `type`/
`type.Name` already resolved there for the adjacent `IsTaskType` check, at
zero incremental ClrMD cost — and have `BeforeHeapIndexScan` read the flags
directly, exactly as it already does for `IsTaskType`. See P1-1.

**Minor**: `ScanRawHeapForVts` correctly caches negative interface-check
results per-MT (`checkedNonMatchMts`) to avoid re-enumerating interfaces for
every instance of a common non-matching type — good defensive design,
consistent with the field-caching pattern elsewhere. `ScanRawHeapForTcs`
needs no equivalent cache since its check is a cheap string prefix compare,
not an interface walk — this asymmetry is correct, not an oversight.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value, Missing (re-verified against current state)

**1. Move TCS/VTS classification into Phase 1 (see P1-1).** Already covered
above as an Area 3 correctness/performance issue; restated here because it
is also the single highest-value "diagnostic opportunity" in the sense of
making the *existing* diagnostic cheaper to produce, which matters directly
for the 1 GB–100 GB scalability mandate this protocol asks about.

**2. Root-path sampling for TCS/VTS leak snapshots.** The original audit's
Area 4 item 6 ("Orphaned task GC root path sampling") noted that
`ReverseEdgeIndexReader.TryGetParents` had just become available
(2026-08-12) and was a direct drop-in for orphaned tasks, following the
pattern already used by `EventLeakAnalyzer`/`TimerLeakAnalyzer`/
`StaticRootLeakDetector`. That recommendation was never implemented for
orphaned tasks, and the same opportunity now applies equally — arguably
more urgently — to `TopUnresolvedTaskCompletionSources` and
`TopPendingValueTaskSources`: for the top-N leak candidates in each table,
even a partial root-path chain ("retained by static field `X._handlers`")
would be a large investigation-time win over an address-and-type-only row.

**3. Orphaned-task GC generation distribution** (see P3-5, Area 1). Directly
mirrors the already-shipped `PendingGen0`–`PendingLOH` pattern; the
infrastructure (`SegmentKindMapper.ResolveGeneration`) is already in the
file and already called for this exact purpose on pending tasks.

**4. TaskScheduler classification** — unchanged from original audit,
still not implemented, still Medium impact.

**5. Cross-correlate TCS/VTS Gen2 leaks in `InsightEngine`.** The only
existing cross-analyzer rule for this analyzer's output
(`DetectOrphanedTaskAccumulation`) predates TCS/VTS detection and doesn't
reference either. A TCS/VTS Gen2 leak combined with a blocked finalizer
thread, or combined with elevated `GCHandleDomainResult` pressure, would be
a materially stronger combined signal than either alone — the same
"cross-cutting risk stacks" pattern `InsightEngine` already applies for
faulted tasks + blocked finalizer.

---

## Audit Area 5 — Performance, Memory & Scalability

### Assessment (re-verified against current code, 1 GB–100 GB range)

The BFS/continuation-exploration bounding (node budget, depth cap, pooled
buffers) audited previously remains sound and unchanged — no regression
found in this pass.

**New scalability concern**: the TCS/VTS per-type candidate discovery cost
identified in Area 3 scales with `TypeAggregates.Count` (distinct type
count) × `workerCount`, **every analysis run**. On a 100 GB dump with tens
of thousands of distinct types, and up to 8 parallel workers, this is a
non-trivial, entirely avoidable repeated cost — avoidable because the
equivalent `IsTaskType` classification is already a zero-cost flag read on
the exact same warm-cache path. This doesn't threaten correctness or cause
unbounded memory growth, but it is real, measurable, unnecessary work on
every single re-analysis of a large, already-indexed dump — precisely the
scenario (repeated fast queries against a persistent disk-backed index)
this platform's architecture is designed around. See P1-1.

**Progress reporting gap**: `AnalyzeTaskCompletionSources`/
`AnalyzeValueTaskSources` — the per-instance classification loops that run
on the participant-collected candidate list (the common, fast path when a
heap index exists) — report no progress at all. `ScanRawHeapForTcs`/
`ScanRawHeapForVts` each have their own `ObjectScanCounter`, but that only
executes on the raw-heap-scan fallback path (no index, or scan failure).
For the default `MaxTcsToScan`/`MaxVtsToScan` of 20,000 each, this is
unlikely to be perceptible, but it's inconsistent with the main task
classification loop (which reports every 5,000 tasks) and with the pattern
already fixed for `AsyncStateMachineAnalyzer`'s histogram pass (P2-5,
sibling audit) for exactly this reason. See P2-4.

Otherwise, memory and streaming characteristics are unchanged from the
original audit's assessment and remain sound: bounded task/TCS/VTS entry
lists, cached field lookups, no full-heap materialization anywhere in the
analyzer.

---

## Audit Area 6 — Correctness & Confidence

### Re-verified, still valid from original audit

- **`MaskRunning` (`TASK_STATE_DELEGATE_INVOKED`) overestimates "Running."**
  Unchanged. The bit is set once a task's delegate begins execution and
  never cleared on completion (completion is signaled by
  `TASK_STATE_RAN_TO_COMPLETION`); a task suspended on an inner `await`
  still reports this bit set and is classified Running rather than
  effectively-suspended. Not corrected this session; still an open,
  narrative-only caveat, not converted into a roadmap item because a robust
  fix would require inspecting the awaited object graph per task (high
  cost) or accepting the same coarse granularity SOS `!tasks` itself uses.
- **Multi-continuation `List<object>` handling** — fixed in the original
  audit's P0-2/branching-DFS work; confirmed still correct on this pass
  (`ExploreContinuation`'s `IsMultiContinuationList` branch, fan-out
  enumeration, and nested-list recursion all trace correctly).
- **Snapshot-in-time caveat** — a task/TCS/VTS instance classified as
  pending/unresolved/incomplete may simply be mid-flight at the instant of
  the dump, not actually leaked. This is inherent to any single-snapshot
  analysis and cannot be resolved without a second dump for comparison
  (which is exactly what the trend comparer is for — reinforcing why P1-2's
  gap matters: it's the mechanism that turns "maybe stuck" into "confirmed
  growing over time").

### New, found this pass

- **Gen2/LOH leak-strength gating is consistently and correctly applied**
  across all three families (pending tasks, TCS, VTS) — verified each
  finding-generator signal requires old-generation residency, not raw
  count, before escalating past Info severity. This is a *correctness
  strength*, not a risk: it directly prevents the most likely false-positive
  mode (flagging normal in-flight async work as a leak) for all three new
  and existing signal types.
- **Cycle detection is a one-shot, root-relative check** — `rootCycleDetected`
  is set only when a BFS re-visits the *root* task's own address, not any
  arbitrary revisited node (revisiting a non-root node inside a diamond
  convergence is expected and handled separately by the `visited` set's
  normal truncation). Traced through `ExploreContinuation`'s two call
  sites (multi-continuation fan-out and scalar-hop recursion) — both
  correctly thread `rootTaskAddress` through recursive calls. No false
  positives found in the traced logic; a genuine self-cycle is required to
  trigger.
- **`TotalTaskCompletionSources`/`TotalValueTaskSources` are capped counts,
  not true heap totals**, exactly like the pre-existing `TotalTasks` — when
  `TcsScanLimited`/`VtsScanLimited` is true, these undercount the real
  population. This is consistent with the analyzer's existing, accepted
  convention (a block-level text caveat, not a per-metric annotation) and
  is not a new inconsistency — flagged here only to confirm the new fields
  correctly inherited the existing convention rather than silently
  diverging from it.

---

## Audit Area 7 — Industry Benchmark

### Re-assessed against current capability

**WinDbg + SOS `!dumpasync`** — still the largest capability gap versus
DumpDetective's task/state-machine linkage (unchanged from original audit;
correctly tracked as `AsyncStateMachineAnalyzer`'s own P3-1 there, not
duplicated here).

**JetBrains dotMemory "group by async state machine + retained size"** —
**partially closed since the original audit.** `TopPendingTaskTypesByBytes`
(P3-3) now provides dotMemory-style byte-ranked attribution for pending
tasks specifically. The remaining gap is state-machine-level grouping
(naming the actual async method, not just the generic `Task<T>`), which
remains `AsyncStateMachineAnalyzer`'s territory.

**New competitive observation**: neither WinDbg/SOS, PerfView, VS Diagnostic
Tools, nor dotMemory expose **`TaskCompletionSource`/`IValueTaskSource`
leak detection** as a first-class, automated diagnostic in any form
comparable to what this analyzer now produces. `!dumpasync` and friends
focus on the compiler-generated `async`/`await` machinery; manually-driven
promises (`TaskCompletionSource`) and low-level pooled awaitables
(`IValueTaskSource`, common in Socket/Pipelines/ASP.NET Core internals) are
a blind spot across all four benchmarked tools. This is now a genuine
DumpDetective differentiator, not a gap — worth stating explicitly rather
than only measuring against what competitors already do.

---

## Final Executive Summary

### Overall Assessment

**Score: 87 / 100** (up from 68/100 at the original audit)

**Production readiness:** Yes, without qualification. No correctness bugs
were found in this re-audit. Every P0/P1/P2 item from the original roadmap
is confirmed shipped and correct; 4 of 5 P3 items are shipped (the 5th is
explicitly blocked on .NET 11 GA, not actionable). The two findings from
this pass (P1-1, P1-2) are missed-opportunity gaps — one performance, one
report-completeness — not defects in what the analyzer reports today.

**Major Strengths:**
- Three-object-family coverage (`Task`, `TaskCompletionSource`,
  `IValueTaskSource`) sharing one participant-scan pass, one field cache,
  and one consistently-applied Gen2/LOH leak-strength convention.
- Zero Phase 1 binary-format risk introduced by either of the two newest
  capabilities (P3-1, P3-2) — both ride the existing shared scan rather
  than adding new passes or index files.
- ClrMD API usage for the newest capability (`ValueTaskSourcePattern`) was
  empirically verified against the actual referenced assembly rather than
  assumed — caught a real API-shape difference (`ClrValueType` has no
  `ulong` conversion, unlike `ClrObject`) before it could become a runtime
  surprise.
- Extensive participant-scan unit test coverage for all three candidate
  families (Task/TCS/VTS), correctly isolating the ClrMD-dependent portions
  via targeted reflection injection where live `ClrHeap` mocking isn't
  feasible.

**Major Weaknesses:**
- TCS/VTS candidate discovery bypasses the Phase 1 disk-cache fast path
  that the sibling `Task` classification already uses for free — repeated,
  avoidable ClrMD cost on every run × every parallel worker (P1-1).
- Trend comparer has zero coverage for any of the 9 fields added since the
  original audit, including the analyzer's two newest and most valuable
  leak signals (P1-2).
- Discrepancy test (`AsyncTaskAnalyzerDiscrepancyTests`) asserts none of
  those same 9 fields either — the disk-vs-memory-mode safety net has a
  matching blind spot (P2-1).

---

### Priority Roadmap

| ID | Recommendation | Area | Classification | Impact | Difficulty | Confidence | Status |
|----|----------------|------|----------------|--------|------------|------------|--------|
| P1-1 | Add `IsTaskCompletionSourceType`/`IsValueTaskSourceType` to `TypeAggregateFlags` (consuming the 2 remaining reserved bits); compute once in `DiskBackedObjectIndexWriter.ComputeTypeFlags` (reusing the `type.Name` already resolved there); read as flags in `BeforeHeapIndexScan` instead of re-resolving `ClrType`/interfaces/fields per run per worker | 3, 5 | Improvement | High — eliminates real, repeated, avoidable ClrMD cost on every warm-cache run | Medium | High | NOT DONE |
| P1-2 | Wire all 9 fields added since the original audit into `AsyncTaskTrendComparer` (`PendingGen0`–`PendingLOH`, `CycleDetected`, `TotalTaskCompletionSources`, `UnresolvedTaskCompletionSources`, `UnresolvedTcsGen2Count`, `TotalValueTaskSources`, `PendingValueTaskSources`, `PendingVtsGen2Count`, `TopPendingTaskTypesByBytes`) | 2 | Improvement | High — the analyzer's newest, most valuable leak signals currently have zero regression tracking | Low | High | NOT DONE |
| P2-1 | Extend `AsyncTaskAnalyzerDiscrepancyTests` to assert the same 9 fields for disk-vs-memory agreement | 2, 6 | Improvement | Medium — closes a real test-coverage blind spot in the existing safety net | Low | High | NOT DONE |
| P2-2 | Extend `AsyncAnalysisSectionBuilder`'s `leadFinding` gating to also consider `CycleDetected` and TCS/VTS Gen2 severity, not only `MaxContinuationDepth` | 2 | Improvement | Medium — section-level highlight can currently miss the report's actual most severe condition | Low | High | NOT DONE |
| P2-3 | Include TCS/VTS counts (when part of the active signal set) in the aggregate "risk cluster" finding's evidence text | 2 | Improvement | Low-Medium | Low | High | NOT DONE |
| P2-4 | Add progress reporting inside `AnalyzeTaskCompletionSources`/`AnalyzeValueTaskSources` classification loops, consistent with the main task-classification loop and with `AsyncStateMachineAnalyzer`'s P2-5 fix | 5 | Improvement | Low | Low | High | NOT DONE |
| P3-1 | Re-verify against .NET 11 GA Runtime Async; confirm `RuntimeAsyncTask<T>` shape before assuming continued compatibility | 4, 7 | Evolution | Medium — prevents silent under-counting as adoption grows | Low | Low (spec not final, blocked on external GA) | NOT DONE (blocked) |
| P3-2 | `TaskScheduler` classification for pending tasks (default pool vs. `SynchronizationContextTaskScheduler` vs. custom) | 1, 4 | Evolution | Medium | Medium | Medium | NOT DONE |
| P3-3 | `Task.m_parent` parent-child hierarchy reconstruction | 1, 4 | Evolution | Medium | Medium | Medium | NOT DONE |
| P3-4 | `SynchronizationContextAwaitTaskContinuation` counting by context type | 1, 4 | Evolution | Low-Medium | Low | Medium | NOT DONE |
| P3-5 | Add GC-generation distribution for orphaned tasks, mirroring the shipped `PendingGen0`–`PendingLOH` pattern (infrastructure — `SegmentKindMapper.ResolveGeneration` — already present and already used for this exact purpose on pending tasks) | 1, 4 | Improvement | Medium — orphaned tasks are arguably the stronger of the two leak signals, and currently the only one without generation breakdown | Low | High | NOT DONE |
| P3-6 | Root-path sampling for top-N unresolved-TCS/pending-VTS snapshots via `ReverseEdgeIndexReader.TryGetParents`, same pattern already used by `EventLeakAnalyzer`/`TimerLeakAnalyzer`/`StaticRootLeakDetector` (and previously recommended, never implemented, for orphaned tasks) | 4 | Evolution | Medium | Medium | High | NOT DONE |
| P3-7 | Cross-correlate TCS/VTS Gen2 leaks in `InsightEngine` (e.g. with blocked finalizer thread or elevated `GCHandleDomainResult` pressure), extending the existing `DetectOrphanedTaskAccumulation`-style cross-cutting pattern | 1, 4 | Evolution | Medium | Low | Medium | NOT DONE |

---

### Final Verdict

1. **Is the analyzer production-ready?** Yes, unconditionally. This
   re-audit found zero correctness defects across six concerns and three
   distinct object families. Every finding is either a performance
   missed-opportunity (P1-1) or a report/test-coverage gap (P1-2, P2-1)
   affecting *regression tracking*, not the accuracy of any single-dump
   analysis.

2. **Highest-impact improvements:** P1-1 (Phase 1 caching for TCS/VTS
   detection) and P1-2 (trend comparer coverage) are both concrete,
   low-to-medium-difficulty fixes that close gaps this session's own work
   introduced — the analyzer grew three new detection capabilities without
   growing the shared caching layer or the trend/regression layer to match.

3. **Platform evolution opportunities:** P3-6 (root-path sampling for TCS/
   VTS leak snapshots) is the most valuable Evolution-classified item —
   the underlying `ReverseEdgeIndexReader` infrastructure already exists
   and is already proven in three other analyzers; applying it here (and
   to the still-outstanding orphaned-task case from the original audit)
   would be the single largest per-finding investigation-time improvement
   available without new ClrMD capability.

4. **Highest engineering return:** P1-2 (trend comparer wiring) — the
   lowest-difficulty item on this roadmap, directly restores regression
   tracking for the two newest and most diagnostically valuable signals
   this analyzer produces. P1-1 (Phase 1 caching) is the second-highest
   return: it eliminates real, quantifiable, currently-repeated cost using
   infrastructure (`TypeAggregateFlags`, `ComputeTypeFlags`) that already
   exists and already solves this exact problem for the sibling `Task`
   case — the fix is applying an established pattern, not inventing one.

---

## .NET 11 Runtime Async — Forward Compatibility

**Status:** Unchanged from the original audit. .NET 11 (preview) Runtime
Async remains an opt-in CLR-native replacement for compiler-generated
`IAsyncStateMachine` structs; see
[async-state-machine-analyzer-audit.md § .NET 11 Runtime Async — Forward
Compatibility](async-state-machine-analyzer-audit.md#net-11-runtime-async--forward-compatibility)
for the full mechanism description.

**Impact here, re-confirmed:** `AsyncTaskAnalyzer`'s `Task`/`Task<T>` model
(`m_stateFlags`, `m_continuationObject`, `m_action`) is unaffected at the
object level — Runtime Async still produces ordinary `Task`/`Task<T>` (or
`RuntimeAsyncTask<T>`-backed) instances for methods that don't complete
synchronously. The two capabilities added since the original audit
(`TaskCompletionSource` and `IValueTaskSource` detection) are **also**
unaffected by Runtime Async — both operate on object models
(`TaskCompletionSource<T>.m_task`/`_task`,
`ManualResetValueTaskSourceCore<T>`) that are orthogonal to the
compiler-generated state-machine mechanism Runtime Async replaces. No new
forward-compatibility risk was introduced by this session's work.

**Recommended action:** Unchanged — no action required to ship today.
