# AsyncTaskAnalyzer — Phase 1 Audit

**Analyzer:** `AsyncTaskAnalyzer` (`src/DumpDetective.Analysis/Analyzers/AsyncTaskAnalyzer.cs`)
**Protocol:** Phase 1 Analyzer Audit (`phase1-analyzer-architecture-review.md`)
**Date:** 2026-07-30

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`AsyncTaskAnalyzer` covers four concerns in a single, cohesive class:

1. **Task state classification** — enumerates all `Task` heap objects and buckets them into Pending / Running / Faulted / Canceled / Completed using `m_stateFlags` bit masks.
2. **Orphaned task detection** — identifies tasks whose `m_continuationObject` is null, zero, or the no-op sentinel, and whose state is not yet terminal — a fire-and-forget or unobserved-fault signal.
3. **Continuation chain BFS** — traverses `m_continuationObject` chains per-task up to `MaxContinuationDepth`, collecting the top-5 deepest chains and an aggregate depth histogram.
4. **Exception extraction** — for orphaned/faulted task snapshots, walks the contingent-properties graph via `ObjectGraphTraversal` to extract exception type and message.

All four concerns are tightly cohesive. The analyzer correctly implements `IParallelHeapIndexScanParticipant`, sharing the heap index pass with the full pipeline dispatcher.

### Coverage Gaps

- **`ValueTask` is entirely absent.** `ValueTask<T>` and `IValueTaskSource<T>` don't produce heap-allocated `Task` objects when the fast path succeeds. Async methods built on pooled value task sources (common in ASP.NET Core and gRPC) are invisible to the analyzer. There is no `IsValueTaskType` flag in `TypeAggregateFlags`.
- **No `IAsyncStateMachine` correlation.** Compiler-generated state machines (e.g., `<MethodName>d__N`) are boxed on the heap as `IAsyncStateMachine` instances when an async method suspends. The analyzer never links these to their controlling `Task`, making it impossible to identify which async methods have live, suspended instances.
- **No `TaskCompletionSource<T>` (TCS) detection.** TCS objects hold a `Task` and a result/exception slot. Orphaned TCS objects that were never resolved are a common resource-leak pattern and are not surfaced.
- **No `TaskScheduler` classification.** The scheduler that owns a pending task (default thread pool, `SynchronizationContextTaskScheduler`, custom) is not reported. Pending tasks on a UI-thread scheduler are qualitatively different from pool-thread pending tasks.
- **No multi-continuation handling.** When multiple continuations are attached to the same `Task`, the runtime stores them as a `List<object>` in `m_continuationObject` rather than a scalar reference. The analyzer reads the field as a scalar object and misclassifies such tasks as either orphaned (if the `List` object appears invalid) or single-hop (if the field is read as a non-task object). All but the first continuation are lost.
- **No parent-child task relationship.** `Task.m_parent` links child tasks created with `TaskCreationOptions.AttachedToParent` into an aggregate hierarchy. These parent tasks can report `Faulted` when children fault even though the parent's own body succeeded.
- **`LongWaitThreshold`-equivalent missing.** There is no configurable threshold for the minimum continuation depth that elevates a chain to "deep" in the finding generator (current threshold 10 is hardcoded).

### Unexpected Functionality

None. All logic directly serves async task diagnostics.

### Adjacent Capabilities

- `HangAnalyzer` independently counts `Task` objects and reads `m_stateFlags` for its own task-state histogram. The two analyzers duplicate the per-object state-read work; if cross-referencing task counts against thread wait states were desired, sharing a task-state result would eliminate the duplication.
- `EventLeakAnalyzer` detects delegate-leaked objects. There is a natural correlation opportunity: orphaned tasks retained by event subscriptions are an important case that neither analyzer currently surfaces.

### Architectural Observations

- The `stateFlags == 0` sentinel reuse is technically sound (participant path writes 0 as "not yet read"; Phase 2 re-reads from ClrMD) but the value 0 is also the legitimate `TASK_STATE_CREATED` state for a freshly instantiated, unstarted task. The double-read is harmless because the participant path is not used when `_participantScanSucceeded == false`, but the conflation is a latent source of confusion.
- `BeforeHeapIndexScan` does not reset `_participantScanSucceeded` to `false` — if a second scan were triggered on the same instance, stale success state could be read. The current pipeline never does this, but the instance reset contract should be documented.
- `MergePartial` correctly re-sorts by address and trims to the global cap. This avoids the per-worker starvation problem (uncapped per-worker accumulation) at the cost of a sort over the merged union — correct and well-tested.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- **Task status summary table** is the clearest compact table across all analyzers: six rows, one status per row, immediately scannable.
- **Orphaned task snapshot** includes `ExceptionType` and `ExceptionMessage` — this is the most actionable per-object data in the section.
- **Continuation chain table** renders the full chain type sequence (`A -> B -> C`), making it possible to identify the async call path at a glance.
- **`TaskScanLimited` caveat** is surfaced both in the section block and in the lead finding caveat list.
- **Trend comparer** covers pending, faulted, orphaned, and chain depth — enough to spot regression across dump snapshots.
- **`AsyncTaskFindingGenerator` cluster signal** correctly elevates severity when multiple signals fire simultaneously and caps findings at two (aggregate + top detail).

### Weaknesses

**Finding thresholds are inconsistent across the stack.** The section builder lead finding fires when `MaxContinuationDepth > 50`. The finding generator fires at `>= 10`. An engineer reading both sections simultaneously sees a "Warning" finding at depth 12 but no lead finding until depth 51 — two different policies that are never reconciled or documented.

**Pending task finding threshold ignores rate.** `PendingTasks > 500` fires regardless of `TotalTasks`. 501 pending out of 500,000 total (0.1%) is not alarming, but 501 out of 600 (83%) is a crisis. The threshold has no denominator.

**`TopContinuationTypes` count is not "distinct continuation object count."** During the BFS each continuation object encountered increments `continuationCount` for that type. A chain of depth 20 through 20 `AwaitTaskContinuation` objects contributes 20 to that type's count. The resulting histogram is "continuation-type occurrence across all BFS hops," which is hard to interpret and not labeled as such in the section or key metrics.

**Orphaned count vs. snapshot count mismatch is silent.** `OrphanedTasks` may be 2,000 but `TopOrphanedTasks` only captures the first 20. The section renders the partial table with no note distinguishing "showing 20 of 2,000" from "all 20 orphans shown."

**No per-type faulted exception aggregation.** `TopFaultedTaskTypes` ranks types by count but loses the exception information. An engineer cannot tell whether all faulted `HttpClient` tasks threw `TaskCanceledException` or a mix of `SocketException`, `TimeoutException`, etc.

**`AvgContinuationDepth` samples only tasks that have at least one valid continuation hop.** Tasks with no continuation do not contribute to `depthSampleCount`. The denominator is `depthSampleCount`, not `TotalTasks`, but this is not stated anywhere in the output.

### Missing Diagnostics

- **Exception type frequency across all faulted tasks** — not just a per-type count but an exception-type histogram.
- **Multi-continuation fan-out** — tasks with more than one continuation attached (the `List<object>` case) indicate broadcast-style async patterns; count and top types are missing.
- **GC generation distribution of pending tasks** — pending tasks in Gen0 are transient; Gen2/LOH pending tasks are a much stronger leak signal.
- **`TaskScanLimited` affects orphan and chain counts** — a capped scan biases towards low-address objects; Gen2/long-lived tasks at higher addresses may be systematically excluded.

---

## Audit Area 3 — ClrMD & Platform Utilization

### Strengths

- `TypeAggregateFlags.IsTaskType` is set during Phase 1 index build (`DiskBackedObjectIndexWriter`), enabling the participant path to filter without reading type names at all.
- `InMemoryTaskCandidates` fast path avoids even a linear scan of `InMemoryEntries`.
- `ResolveTypeName` caches resolved names by MethodTable, eliminating repeated `obj.Type.Name` lookups per MT.
- `ObjectGraphTraversal.TryFindByPredicate` with prioritized field names is the correct approach for exception extraction — it checks known fields first and avoids exhaustive reference enumeration in the common case.

### Issues

**`m_stateFlags` field lookup is uncached and runtime-version fragile.**

The code calls `obj.Type.GetFieldByName("m_stateFlags")` per-object for any entry whose `stateFlags == 0`. This field name is internal to the CLR and changed in some runtime builds:

- .NET 5–7: `m_stateFlags`
- .NET 8+: private fields migrated to `_stateFlags` naming convention in some assemblies

If `GetFieldByName` returns `null`, the task silently gets `stateFlags = 0` and is classified as Pending. On a .NET 8 dump this would inflate pending counts to 100%. The field lookup result is never cached by `ClrType` (MethodTable), so it is repeated per-task rather than once per unique type.

**`m_continuationObject` BFS does not handle `List<object>` continuations.**

`Task.m_continuationObject` can hold:
1. `null` / 0 — no continuation
2. Single `Task` (or wrapped continuation) — scalar case handled
3. `List<object>` — multiple continuations; set when a second `ContinueWith` is added

The analyzer calls `continuationField.ReadObject(taskObj, interior: false)` and checks `continuationObj.IsValid`. When the field holds a `List<object>`, `ReadObject` will return a valid `ClrObject` of type `System.Collections.Generic.List<object>`, which has no `m_continuationObject` field. The BFS immediately breaks on the first hop (`nextField == null`) and reports `depth = 1`. The task is not marked as orphaned (the list is valid). All actual continuation targets are invisible.

**`GetFieldByName("m_continuationObject")` inside the inner BFS loop is not cached.**

Each BFS hop calls `current.Type?.GetFieldByName("m_continuationObject")`. `GetFieldByName` is a linear scan of the type's field list in ClrMD 3.x. For 50,000 tasks with average depth 5, this is ~250,000 `GetFieldByName` calls. Caching per unique `ClrType` pointer (same as `typeNameByMt`) would reduce this to O(unique continuation types).

**`TryFindExceptionLikeObject` falls through to `source.EnumerateReferences`.**

`ObjectGraphTraversal.TryFindByPredicate` enumerates all references of a node after checking prioritized fields. For a faulted task with a large contingent-properties object, this could traverse dozens of references. The recursion depth is capped at 4, which limits worst-case exposure but not worst-case breadth.

**`IsTasThreadPoolRunning` / `IsThreadPoolAware` flags not consulted.**

`ClrRuntime.ThreadPool` is not read. `HangAnalyzer` reads these; `AsyncTaskAnalyzer` could cross-reference a pool-thread starvation signal against a high pending-task count but does not.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value, Missing

**1. `IAsyncStateMachine` instance inventory**

Every suspended `async` method has a boxed state machine on the heap. Scanning for objects implementing `IAsyncStateMachine` (identified by a specific interface implementation flag in ClrMD) and counting by type name would produce a "live async method invocation" histogram. This is more informative than the raw task type histogram because it names the actual async method, not the generic `Task<T>`.

Impact: High. Difficulty: Medium. Confidence: High.

*.NET 11 caveat:* under Runtime Async (see [.NET 11 Runtime Async — Forward Compatibility](#net-11-runtime-async--forward-compatibility)), a suspended method may have no `IAsyncStateMachine` instance at all. Implement this inventory as best-effort per task, not as a required correlation — a `Task` with no matching state machine is an expected outcome, not a detection failure.

**2. Multi-continuation fan-out detection**

Detect tasks where `m_continuationObject` is a `List<object>`, enumerate the list, and report the fan-out count and target types. A task with 50 continuations attached indicates a broadcast-style event completion (e.g., a shared lock-release task). Unreleased broadcast tasks with large fan-outs are a common cause of suspension storms.

Impact: High. Difficulty: Medium. Confidence: High.

**3. `ValueTask` / `IValueTaskSource` analysis**

Scan for `ManualResetValueTaskSourceCore<T>` instances and their `_version` / `_continuation` fields. In ASP.NET Core, these are frequently the root of async stalls that produce no `Task` heap objects at all.

Impact: High. Difficulty: High. Confidence: Medium (runtime-version dependent).

**4. Per-faulted-task exception type histogram**

Group `TopFaultedTaskTypes` entries by exception type extracted from representative faulted instances. Output: "HttpClient.Task<HttpResponseMessage> — 843 faulted: TaskCanceledException 701, SocketException 142." This transforms a raw count into an actionable diagnosis.

Impact: High. Difficulty: Low. Confidence: High.

**5. Pending task GC generation distribution**

For each pending task sampled, read `heap.GetGeneration(address)` and bucket into Gen0/Gen1/Gen2/LOH. Gen2 pending tasks are significantly more likely to be leaked. Current output cannot distinguish a transient spike from a slow accumulation.

Impact: Medium. Difficulty: Low. Confidence: High.

**6. Orphaned task GC root path sampling**

For the top-N orphaned tasks by size, call `RootPathFinder` to identify the GC root chain. Even a partial path (e.g., "retained by static field `EventSource._events`") would dramatically accelerate investigation.

Impact: Medium. Difficulty: Medium. Confidence: High.

> **Reverse index available (2026-08-12):** `RootPathFinder` is now backed by `ReverseEdgeIndexReader.TryGetParents` — this recommendation is a direct drop-in, same pattern already used by EventLeakAnalyzer/TimerLeakAnalyzer/StaticRootLeakDetector. See `docs/analysis/phase1/phase1-completion-tracker.md` § Reverse Edge Index — Consumer Opportunities.

**7. Async deadlock heuristic**

A task that is Pending and whose continuation chain leads back to itself (cycle in `m_continuationObject` graph) is a hard deadlock. The BFS already uses a `visited` `HashSet<ulong>` — a cycle detected during traversal should be flagged as `CycleDetected` and elevated as Critical in the finding generator.

Impact: High. Difficulty: Low. Confidence: High.

**8. `SynchronizationContextAwaitTaskContinuation` continuations**

When a task is awaited on a thread with a custom `SynchronizationContext` (WPF, WinForms, ASP.NET classic), the continuation is wrapped in `SynchronizationContextAwaitTaskContinuation`. Counting these by context type identifies which UI/legacy contexts have queued continuations.

Impact: Medium. Difficulty: Low. Confidence: High.

---

## Audit Area 5 — Performance, Memory & Scalability

### Assessment

The analyzer's primary scaling bottleneck is the per-task BFS in Phase 2. For 50,000 tasks with `MaxContinuationDepth = 20`:

- Up to **1,000,000** `GetFieldByName("m_continuationObject")` calls (uncached per hop).
- Up to **50,000** `GetFieldByName("m_stateFlags")` calls (uncached per object, per type).
- Up to **50,000** `heap.GetObject(address)` calls for state-flag re-reads.
- Each `heap.GetObject` may perform a memory-mapped read into a potentially cold page of the dump.

On a 10 GB dump with a hot heap cache (mmap warm), this is plausible. On a 25 GB dump with a cold page cache (first analysis run), page-fault pressure on the `m_stateFlags` and `m_continuationObject` reads could cause significant latency.

### Specific Issues

**`GetFieldByName` inside BFS loop is O(fields) per hop, per task.**

Fix: Build a `Dictionary<nint, ClrInstanceField?>` (keyed on `ClrType` pointer or MethodTable) for `m_continuationObject` outside the per-task loop. This reduces the inner BFS cost from O(fields × depth × tasks) to O(unique continuation types × depth + tasks).

**Phase 2 re-reads `m_stateFlags` for every task with `stateFlags == 0`.**

In the participant path, `stateFlags` is written as 0 for all entries (the comment says "StateFlags resolved in Phase 2"). This means Phase 2 unconditionally re-reads `m_stateFlags` for every task via ClrMD even when the heap index already parsed the state flags during Phase 1. The `DiskBackedObjectIndexWriter` writes Address+MethodTable+Size but not state flags. Adding state flags to the task index record (4 extra bytes per entry) would eliminate all Phase 2 re-reads.

**`BuildTopN` partial sort has broken threshold tracking.**

`threshold` is initialized to 0 and only updated inside the fill branch when `kvp.Value < threshold || result.Count == 1`. Because threshold starts at 0 and all counts are positive, the condition `kvp.Value < threshold` is never true during fill. `threshold` stays 0 after fill. Every subsequent item satisfies `kvp.Value > threshold` (i.e., `kvp.Value > 0`), causing the inner min-scan loop to run for every item in the dictionary past the initial `topTypesToShow` fill. This is O(n × k) instead of the intended O(n) with a maintained threshold. For small k (default 10) and typical dictionaries (< 500 entries), the impact is negligible, but the algorithm is incorrect as written.

**`deepestChains` uses a linear min-scan over 5 elements.**

A heap of size 5 is fine in practice, but the linear scan is re-implemented inline rather than extracted. Not a performance concern at scale.

**Cancellation is checked at the per-task loop level (`ct.ThrowIfCancellationRequested()`) but not inside the BFS inner loop.** For very deep chains (MaxContinuationDepth = 40) on many tasks, cancellation response time is bounded by the BFS depth of the current task, not the loop iteration. In practice this is fast, but adding the check inside the BFS while-loop costs nothing.

### Scalability Assessment

- **1 GB dump, ~500K objects, ~5K tasks**: negligible — finishes in milliseconds.
- **10 GB dump, ~5M objects, ~50K tasks**: plausible within seconds. The uncached `GetFieldByName` calls are the primary risk.
- **25 GB+ dump, ~20M objects, ~200K tasks** (with `MaxTasksToScan = 100K`): Phase 2 classification will dominate. Caching `ClrInstanceField` by type is mandatory at this scale. The memory footprint of `_participantEntries` (3 × 8 bytes × 100K = ~2.4 MB) and `typeNameByMt` is acceptable.

---

## Audit Area 6 — Correctness & Confidence

### `MaskRunning = 0x10000` misidentifies task execution state

`0x10000` is `TASK_STATE_DELEGATE_INVOKED` — the bit is set when the task delegate *begins* execution. It is **not** cleared when the delegate completes; completion is indicated by setting `TASK_STATE_RAN_TO_COMPLETION` (`0x1000000`). A task that has started and completed will have both bits set. The guard `!isCompleted && !isFaulted && !isCanceled` correctly filters completed tasks, but `TASK_STATE_DELEGATE_INVOKED` alone does not mean the task is *currently* running — it means the delegate was invoked at some past point. A task that was started and is now stuck awaiting an inner task will have this bit set and will appear "Running" when it is actually suspended.

The practical impact is that "Running" is overestimated relative to "Pending" for tasks that have reached their first `await`. This is observable in dumps where the async workload is mostly IO-bound.

### `m_stateFlags` field name fragility

`GetFieldByName("m_stateFlags")` will silently return `null` if the field has been renamed in the runtime version being analyzed. On .NET 8+ releases where internal fields were standardized to `_`-prefixed names, this lookup fails for all tasks. The fallback is that `stateFlags` stays 0, classifying every task as Pending. No warning or diagnostic is produced. The analyzer should try `"m_stateFlags"` then `"_stateFlags"` and optionally log a warning if neither is found.

### `stateFlags == 0` sentinel conflates two meanings

Value `0` in the participant-accumulated entry means "not yet read from ClrMD" but also legitimately represents a `TASK_STATE_CREATED` (new task, not started) or a partially-initialized task. A task with genuine `stateFlags = 0` that is read from the heap will produce `stateFlags = 0`, be re-read in Phase 2, and re-classified correctly. This is not a bug — Phase 2 always re-reads on 0 — but the dual meaning is unintuitive and documented only by a comment.

### Multi-continuation misclassification

Tasks with `m_continuationObject` pointing to a `List<object>` are not orphans (the field is valid and non-null) but their continuations are not traversed. They contribute depth=1 to the chain histogram (the BFS finds the `List<object>` but can't follow it) and are not marked orphaned. If the application heavily uses `WhenAll` aggregation or broadcast completion tasks, these appear as correctly-continued tasks with shallow chains — a false negative.

### No `obj.IsValid` guard before BFS continuation read

In the classification loop, `heap.GetObject(address)` is called to read `m_continuationObject`. No explicit validity check is performed before calling `taskObj.Type.GetFieldByName(...)`. The code checks `if (taskObj.IsValid && taskObj.Type != null)` before accessing the continuation field, which is correct. However, the initial `heap.GetObject(address)` call for state-flag re-read uses `if (obj.IsValid && obj.Type != null)` but this object is a separate local — the outer `taskObj` re-reads the same address again. Two `heap.GetObject` calls for the same address are redundant; they should be merged.

### Exception extraction confidence

`TryReadExceptionSummary` returns `true` if `exceptionType` is non-empty, regardless of whether `message` is populated. An orphaned task snapshot with `ExceptionType = "System.Exception"` and `ExceptionMessage = null` is technically correct but potentially misleading — the message may exist in the dump but was unreadable.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS `!dumpasync`

SOS `!dumpasync` reconstructs the full async call tree by:
1. Walking all `IAsyncStateMachine` implementors on the heap.
2. Linking state machines to their `Task` via the `<>t__builder` field.
3. Grouping by async method, showing state (running / awaiting / faulted), the awaited object, and the full chain.

`AsyncTaskAnalyzer` does not link state machines to tasks at all. The BFS on `m_continuationObject` traces the task continuation chain but does not name the async method associated with each chain node. From a diagnostic standpoint, SOS `!dumpasync` is substantially more useful for identifying *what async code is stuck* — DumpDetective shows counts and depths but not the call paths.

**Opportunity:** Add `IAsyncStateMachine` inventory (Area 4, item 1) to achieve parity with the most important SOS async diagnostic.

### WinDbg + SOS `!tasks` / `!threadpool`

SOS `!tasks` shows all tasks with their state, scheduler ID, and `m_action` delegate type. DumpDetective reports the task type but not the delegate/action type (what the task will run). SOS `!threadpool` shows the thread pool queue; `AsyncTaskAnalyzer` does not read `ClrRuntime.ThreadPool`.

### PerfView

PerfView's "GC Heap Dump" view groups async state machines by method signature and shows the byte size of live suspended async frames. DumpDetective has no analog — it has task byte sizes but not async frame byte sizes.

### JetBrains dotMemory

dotMemory provides a "Group by async state machine" view and identifies the largest async method trees by retained size. DumpDetective's closest analog is `TopPendingTaskTypes` by size, but size is not in `NameCountEntry` and is not part of the pending-type ranking.

### Competitive Opportunities

1. `IAsyncStateMachine` correlation — closes the biggest gap vs. `!dumpasync`.
2. Delegate / action type for pending tasks — closes the `!tasks` gap without requiring state machine correlation.
3. Pending tasks ranked by `Size` × `Count` (total retained bytes) — provides dotMemory-style size attribution.

---

## Final Executive Summary

### Overall Assessment

**Score: 68 / 100**

**Production readiness:** Yes, with caveats. The analyzer produces correct and useful output for typical .NET Core 6/7 workloads. On .NET 8+ the `m_stateFlags` name fragility is a silent correctness risk that can inflate pending counts to 100%. Multi-continuation tasks are silently under-counted.

**Major Strengths:**
- Clean `IParallelHeapIndexScanParticipant` implementation with correct merge semantics.
- Three fast paths (InMemoryTaskCandidates → disk index → heap scan) make cold-start cost negligible.
- Orphaned task snapshot with exception extraction is the most immediately actionable per-object data across all async diagnostics in the tool.
- Trend comparer tracks all key metrics.

**Major Weaknesses:**
- `m_stateFlags` lookup fragility on .NET 8+ (silent failure → 100% pending inflation).
- Multi-continuation (`List<object>`) case not handled — false negatives on broadcast-style tasks.
- `IAsyncStateMachine` correlation absent — the most important diagnostic gap vs. SOS `!dumpasync`.
- `GetFieldByName` uncached inside BFS inner loop — latent scalability bottleneck at high task counts.

---

### Priority Roadmap

| ID | Recommendation | Area | Classification | Impact | Difficulty | Confidence | Status |
|----|----------------|------|----------------|--------|------------|------------|--------|
| P0-1 | Handle `m_stateFlags` / `_stateFlags` name fallback; cache field by `ClrType` | 3, 6 | Improvement | Critical | Low | High | ✅ DONE (b5c8107) |
| P0-2 | Detect and traverse `List<object>` multi-continuation in BFS | 4, 6 | Improvement | High | Medium | High | ✅ DONE (8cf7849) |
| P1-1 | Cache `ClrInstanceField` for `m_continuationObject` and `m_stateFlags` by `ClrType` | 5 | Improvement | High | Low | High | ✅ DONE (b5572e4) |
| P1-2 | Add `IAsyncStateMachine` inventory (type name + count + controlling task linkage) | 4, 7 | Evolution | High | Medium | High | ⤷ SUPERSEDED by [AsyncStateMachineAnalyzer P3-1](async-state-machine-analyzer-audit.md#priority-roadmap) — inventory already covered there; only the task-linkage half is a genuine gap, tracked on that side |
| P1-3 | Add exception type histogram across all faulted tasks (not just orphaned snapshots) | 2, 4 | Improvement | High | Low | High | ✅ DONE (91babb4) |
| P1-4 | Write `m_stateFlags` into the task index record during Phase 1 to eliminate Phase 2 re-reads | 5 | Evolution | Medium | Medium | High | ✅ DONE (22ecc52) |
| P2-1 | Fix `BuildTopN` threshold tracking bug (threshold stays 0 during fill) | 5 | Improvement | Low | Low | High | ✅ DONE (3117069) |
| P2-2 | Normalize pending-task finding threshold to a rate (pct of total) in addition to raw count | 2 | Improvement | Medium | Low | High | ✅ DONE (8e6eb40) |
| P2-3 | Add orphaned task snapshot count vs. total orphan count note in section builder | 2 | Improvement | Medium | Low | High | ✅ DONE (8e6eb40) |
| P2-4 | Harmonize section builder lead finding threshold (>50) with finding generator threshold (≥10) | 2 | Improvement | Low | Low | High |
| P2-5 | Report `AvgContinuationDepth` denominator (`depthSampleCount`) in key metrics | 2 | Improvement | Low | Low | High |
| P2-6 | Detect continuation chain cycles (async deadlock heuristic) during BFS | 4 | Improvement | High | Low | High |
| P2-7 | Pending task GC generation distribution (Gen0/Gen1/Gen2/LOH) | 4 | Improvement | Medium | Low | High |
| P3-1 | Add `ValueTask` / `IValueTaskSource` tracking via `ManualResetValueTaskSourceCore` | 4 | Evolution | High | High | Medium |
| P3-2 | Add `TaskCompletionSource<T>` orphan detection | 4 | Evolution | Medium | Medium | High |
| P3-3 | Rank pending types by total retained bytes (Size × Count) | 4, 7 | Improvement | Medium | Low | High |
| P3-4 | Merge duplicate state-read with BFS `heap.GetObject` call to eliminate second lookup | 6 | Improvement | Low | Low | High |
| P3-5 | Re-verify P1-2 (`IAsyncStateMachine` correlation) treats "no state machine found" as expected once .NET 11 Runtime Async adoption grows; confirm `RuntimeAsyncTask<T>` shape against GA runtime before hard-coding | 4, 7 | Evolution | Medium — prevents false-positive "orphan" classification | Low | Low (spec not final) |

---

## .NET 11 Runtime Async — Forward Compatibility

**Status:** .NET 11 (preview) introduces **Runtime Async**, an opt-in CLR-native replacement for compiler-generated `IAsyncStateMachine` structs. See [async-state-machine-analyzer-audit.md § .NET 11 Runtime Async — Forward Compatibility](async-state-machine-analyzer-audit.md#net-11-runtime-async--forward-compatibility) for the full mechanism description; this section covers the impact specific to `AsyncTaskAnalyzer`.

**Impact here:** `AsyncTaskAnalyzer`'s task model (`Task`/`Task<T>`, `m_stateFlags`, `m_continuationObject`, `m_action`) is **unaffected at the `Task` object level** — Runtime Async still produces ordinary `Task`/`Task<T>` (or `RuntimeAsyncTask<T>`-backed) instances on the heap for methods that don't complete synchronously, so the existing pending/orphan/continuation-BFS logic continues to function without modification. The risk is narrower than in the state machine analyzer:

- The planned `IAsyncStateMachine` correlation work (P1-2 in the roadmap below, and the `!dumpasync`-parity item in Area 7) assumes every suspended async call is backed by a `<>t__builder`-linked state machine struct. Under Runtime Async, that link doesn't exist — the builder/state-machine bridge is replaced by `AsyncHelpers` and `RuntimeAsyncTask<T>`. **When P1-2 is implemented, it must not assume `IAsyncStateMachine` correlation is exhaustive** — a `Task` with no matching state machine instance is expected and normal for Runtime Async-compiled callers, not a bug or an orphan.
- `RuntimeAsyncTask<T>`'s exact field layout (equivalent to `m_stateFlags`/`m_continuationObject` for correlation purposes) is not finalized pre-GA; do not hard-code assumptions about it yet.

**Compatibility constraint:** As with the state machine analyzer, .NET Framework and non-opted-in .NET code paths remain on the classic model indefinitely. `Task`-level analysis (the bulk of this analyzer) needs no version branching since `Task` itself is unchanged; only the future `IAsyncStateMachine` correlation feature needs to treat "no state machine found" as a valid outcome rather than a detection failure.

**Recommended action:** No change required to ship today. When implementing P1-2 (`IAsyncStateMachine` inventory + task linkage), gate the correlation as best-effort/optional per task, and re-verify against .NET 11 GA before assuming `RuntimeAsyncTask<T>` structure.

---

### Final Verdict

1. **Is the analyzer production-ready?** Yes for .NET Core 6/7 workloads. On .NET 8+ the `m_stateFlags` naming risk (P0-1) must be fixed before the analyzer can be trusted. Multi-continuation mis-handling (P0-2) is a silent false-negative that understates orphan counts on any modern async codebase.

2. **Highest-impact improvements:** P0-1 (field name fragility), P0-2 (multi-continuation BFS), P1-1 (field caching for scalability), P1-3 (exception histogram).

3. **Platform evolution opportunities:** P1-2 (`IAsyncStateMachine` inventory) would close the most significant capability gap versus SOS `!dumpasync` and is the single highest-engineering-return addition to the overall platform. P1-4 (state flags in task index) eliminates a Phase 2 ClrMD read pass entirely.

4. **Highest engineering return:** P0-1 + P0-2 together — low difficulty, eliminate the two main correctness risks. P1-2 — medium difficulty, produces a qualitatively new diagnostic tier. P2-6 (cycle detection) — trivially cheap since the `visited` HashSet is already present in the BFS, and it closes a critical async deadlock gap.
