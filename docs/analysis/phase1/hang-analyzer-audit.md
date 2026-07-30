# HangAnalyzer — Phase 1 Audit

**Analyzer:** `HangAnalyzer` (`src/DumpDetective.Analysis/Analyzers/HangAnalyzer.cs`)
**Protocol:** Phase 1 Analyzer Audit (`phase1-analyzer-architecture-review.md`)
**Date:** 2026-07-30

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`HangAnalyzer` covers three distinct concerns in a single class:

1. **Thread wait-pattern detection** — walks the thread stack scan (via `IThreadStackScanParticipant`) and pattern-matches the top stack frame to classify waiting threads into seven `WaitType` categories.
2. **Async work inventory** — walks the heap index (via `IParallelHeapIndexScanParticipant`) to count `Task`, `QueuedWorkItemCallback`, and continuation objects, and reads `m_stateFlags` to classify task completion state.
3. **Runtime thread-pool introspection** — reads `ClrRuntime.ThreadPool` to obtain min/max/active/idle worker counts, CPU utilization, and queue length.

The three concerns are coherent: all contribute to hang and blocking diagnosis. The analyzer correctly implements both dispatch interfaces, sharing the global heap-index pass and the global thread-stack pass with `ThreadAnalyzer`, `LockGraphAnalyzer`, and `ThreadStackClusterAnalyzer`.

### Coverage Gaps

- **`LongWaitThreshold` option is declared but never referenced.** `HangAnalysisOptions.LongWaitThreshold` (default 5) has no call site in `HangAnalyzer.cs`. This is a dead option that may have been intended for wait-duration thresholding but was never wired.
- **No wait duration.** The analyzer detects *that* threads are waiting but has no notion of *how long* they have been waiting. Duration evidence is absent from every diagnostic surface.
- **No timer analysis.** `System.Threading.TimerQueue` and `System.Threading.Timer` objects on the heap are not scanned. Stuck or fire-flooded timers are a common hang contributor.
- **No `SynchronizationContext` queue depth.** WPF/WinForms/ASP.NET classic UI threads that hold a `SynchronizationContext` and post work to a saturated queue are invisible.
- **No async causality chains.** The continuation-type histogram counts surviving continuations but does not attempt to link them into async call chains.
- **`UsingPortableThreadPool` / `UsingWindowsThreadPool` flags are stored in `ThreadPoolAnalysis` but never surfaced** in `HangDomainResult`, the section builder, or the finding generator.

### Unexpected Functionality

None. All logic relates directly to hang and blocking diagnosis.

### Adjacent Capabilities

- `LockGraphAnalyzer` is a sibling that builds the lock ownership graph. `HangAnalyzer` detects threads holding locks and waiting, but does not cross-reference `LockGraphAnalyzer` output to name the owning thread for a contested lock.
- `ThreadAnalyzer` enumerates thread details independently. Some metadata duplicated across both analyzers (alive-thread count, lock count) could be unified.

### Architectural Observations

- The dual-participant design (heap + thread stack) results in a class that carries two independent sets of accumulator fields (`_threadScanXxx` vs `_heap`/`_profileByMethodTable`), each gated by its own succeeded flag. This is functional but increases the cognitive surface. Consider documenting the two accumulation lifecycles more explicitly.
- The fallback path `RunParallelAsyncScan` is a complete reimplementation of the participant-path logic. It is correctly isolated but divergence between the two paths is a maintenance risk (already mitigated by `HangAnalyzerDiscrepancyTests`).

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- `HealthScore` is a simple, scannable 0–100 composite metric with clearly defined penalty tiers.
- `WaitCategoryBreakdown` provides a per-category distribution, making contention patterns immediately visible.
- `TopWaitingThreads` table with thread IDs, OS thread IDs, wait type, lock count, and top frame is directly actionable from the report.
- `HangSectionBuilder` conditionally surfacesa "Runtime thread-pool metrics" compact table only when data is available, avoiding misleading zeros.
- Runtime queue-length vs. heap-proxy explanation text (`SectionBlock T(...)`) is a good transparency signal for the reader.

### Weaknesses

**`HangFindingGenerator` produces exactly one finding for the entire analyzer.** Severity is driven only by `WaitingPercent` and `QueuedWorkItems`. A dump with 0 waiting threads but a fully saturated thread pool (`IsStarved == true`) and 200 faulted tasks gets severity `Info`. The finding generator does not consult `IsStarved`, `FaultedTasks`, `HealthScore`, or `circularCandidates`.

**`SectionLeadFinding` severity is hardcoded to `"Warning"` in both branches.** The condition `d.IsStarved || d.HealthScore < 50` could warrant `"Critical"` for starvation and `"Warning"` for degraded scores, but both branches emit `"Warning"`.

**Top waiting threads truncated to a hardcoded `10`**, not the `TopWaitingThreadsPerGroup` option. The option exists but the `Take(10)` in `Analyze()` ignores it.

**`WaitReason` strings are static and context-free.** "Waiting to acquire monitor lock" gives the same text for every thread, regardless of which lock, which owning thread, or how many threads are queued for the same lock.

**`TopStackFrame` is the method signature only.** For production diagnosis a single method name is frequently insufficient — callers need the method + at least partial stack context to understand the code path that led to the wait.

**`monitor.enter` and `monitor.wait` are both mapped to `WaitType.MonitorWait`.** These represent different states: `Monitor.Enter` means the thread is *actively blocked trying to acquire*; `Monitor.Wait` means the thread *released the lock and is waiting for a pulse*. Conflating them obscures deadlock vs. contention patterns.

**`LongWaitThreshold` has no effect.** As noted in Area 1, the option is wired into presets but never consulted during analysis or reporting.

**`TotalContinuations` and task-state breakdown appear in key metrics and compact tables but are never contextualised.** 5,000 pending tasks means nothing without a baseline. A "Task state breakdown" row set (pending / faulted / canceled / completed) is absent from the section builder.

### Missing Diagnostics

- Total faulted-task count and canceled-task count are in `HangDomainResult` but absent from the compact tables — they appear only in key metrics.
- No section-level note when `TaskScanLimited == true` (the scan was truncated early). Readers may not notice the `task_scan_limited` key metric.
- No correlation between `IsStarved` and the continuation backlog — a starvation + high continuation count combination is a strong signal that deserves a dedicated warning block.

---

## Audit Area 3 — ClrMD & Platform Utilization

### `GetIntProperty` Uses Reflection for `QueueLength`

```csharp
info.RuntimeQueueLength = GetIntProperty(tp, "QueueLength");
```

ClrMD 4 exposes `ClrThreadPool.QueueLength` as a first-class property of type `int`. Reflection is unnecessary, type-unsafe, and silently returns `null` on any name change. The property should be read directly:

```csharp
info.RuntimeQueueLength = tp.QueueLength;
```

### `ClrThread.BlockingObjects` Not Used

ClrMD 4 exposes `ClrThread.BlockingObjects` — a list of `BlockingObject` entries that identify the actual managed lock object, its owner thread(s), and the number of waiters. This is the canonical API for hang diagnosis and is entirely unused. The current implementation infers blocking only from stack frame text matching, which misses native blocking, OS blocking, and any frame where the managed method name does not contain the expected keyword.

### Top-Frame-Only Thread Analysis

`GetRequiredFrameCount` returns `1`. Only the top frame participates in wait pattern detection. In practice:
- A thread blocked in a `Monitor.Enter` will have its top frame in a native/kernel stub (`NtWaitForMultipleObjects`, `WaitForSingleObjectEx`). The managed `Monitor.Enter` frame sits one or more frames below the top.
- With only the top frame, the `topMethod.Contains("monitor.enter")` condition may never match in production dumps because the top managed frame is typically an OS wait primitive, not the `Monitor` method.

This is a potential systemic false-negative source: waiting threads may consistently go undetected when their block occurs below the top frame.

### Thread State Fields Not Consulted

`ClrThread` exposes several relevant fields that are not read:
- `IsBackground` — background threads blocking is generally less diagnostic.
- `IsDebugSuspended` / `IsGCSuspended` — distinguishes GC from real hangs.
- `IsAbortRequested` — thread abort in progress.
- `State` (raw flags) — can be used to identify finalize/GC helper threads.

### `UsingPortableThreadPool` / `UsingWindowsThreadPool` Stored but Not Surfaced

Both flags are read and stored in `ThreadPoolAnalysis` and are also in `HangDomainResult` (implicitly via field access), but they are absent from `HangDomainResult`'s constructor parameters, the section builder output, and the finding generator. Under the Windows Thread Pool the starvation semantics differ; this distinction is invisible to the report reader.

### `AsyncTypeProfile` Correctly Avoids Redundant Heap Lookups

The `profileByMethodTable` cache keyed by `MethodTable` correctly avoids re-classifying the same type for every instance. This is good.

### `m_stateFlags` Field Access

Reading `m_stateFlags` by name is appropriate — it is the internal field name documented in ClrMD tooling literature. The state bit constants `0x1000000`, `0x200000`, `0x400000` correspond to `TASK_STATE_RAN_TO_COMPLETION`, `TASK_STATE_FAULTED`, and `TASK_STATE_CANCELED` in the .NET runtime source and are correct. However they are undocumented magic numbers in the code; named constants or a comment citing the runtime source would reduce future confusion.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High Value

**BlockingObject ownership chains (P0).** `ClrThread.BlockingObjects` returns the managed lock object, its owner, and all waiters. Used in conjunction with the current `LockCount > 0` check, this would allow producing: *"Thread 42 is blocked waiting for a lock held by Thread 17, which is waiting for a lock held by Thread 42"* — a definitive deadlock circuit. This is the single most impactful missing capability.

**Multi-frame wait detection (P0).** Expand `GetRequiredFrameCount` to return 5–10 (configurable). Walk frames top-down looking for the first frame that matches a wait-pattern signature rather than pinning detection to frame 0. This would fix the false-negative risk identified in Area 3.

**Faulted task exception types (P1).** For tasks where `isFaulted == true`, read the `m_contingentProperties.m_exceptionsHolder` field chain to extract the exception type name. "12 faulted tasks" is far less useful than "12 faulted tasks: 10× `System.Net.Http.HttpRequestException`, 2× `System.OperationCanceledException`".

**`SynchronizationContext` queue depth (P1).** Scan the heap for `System.Windows.Threading.DispatcherOperation`, `System.Windows.Forms.Control+ThreadMethodEntry`, and `System.Web.AspNetSynchronizationContext` accumulated work items. These represent blocked UI or legacy ASP.NET threads with a non-empty synchronization queue.

**Task state breakdown in compact table (P1).** Surface `PendingTasks`, `FaultedTasks`, `CanceledTasks`, and `TotalTasks` as a single compact table row set: "pending 1,200 / faulted 18 / canceled 3 / completed 48,779" — making the ratio immediately scannable.

**Timer queue analysis (P2).** Scan `System.Threading.TimerQueueTimer` instances. High timer counts combined with starvation indicate timer callback flooding. A count + top-N callback type histogram would flag this class of hang.

**Wait-type split: MonitorEnter vs. MonitorWait (P2).** Separate `WaitType` values for active-acquire (`MonitorEnter`) and pulse-wait (`MonitorWait`) to enable accurate deadlock heuristics in `ComputeHealthScore`.

**Thread-pool type identification in report (P2).** Surface `UsingPortableThreadPool` / `UsingWindowsThreadPool` in the section builder. Under the portable thread pool, the starvation detection heuristic is valid; under the legacy Windows thread pool the semantics differ.

**Async causality chain reconstruction (P3).** For each continuation-type count, attempt to trace one example instance's continuation chain via `m_continuationObject` / `_target` fields. Showing the first 3 hops of the deepest chain provides structural context for the continuation backlog.

---

## Audit Area 5 — Performance, Memory & Scalability

### Heap Scan (Async Work Path)

- `IParallelHeapIndexScanParticipant` is implemented correctly. The shared heap-index pass is the primary path; `RunParallelAsyncScan` is a correct fallback.
- The `profileByMethodTable` cache eliminates redundant type resolution per MethodTable — good for large heaps with millions of `Task<T>` instances.
- `TaskScanLimited` flag correctly short-circuits processing once `MaxTasksToScan` is exceeded *and* there are already >1,000 queued work items. This heuristic is reasonable but the combined condition is not obvious — a comment would help.
- `RunParallelAsyncScan` fallback does not propagate `CancellationToken` into `Parallel.ForEach`. Both the `inMemoryEntries` and `segments` branches ignore the cancellation token, which means the fallback cannot be interrupted once started.

### Thread Stack Scan

- Top-1-frame scan is extremely cheap, which is appropriate for shared-pass participation.
- If the fix recommended in Area 4 (expanding to 5–10 frames) is adopted, `GetRequiredFrameCount` would need updating. The dispatcher uses `max(all participants)` so this increases cost for *all* thread stack scan participants. This must be weighed carefully.

### Memory

- `_threadScanWaitingThreads` grows unbounded up to the number of alive threads. For a 10,000-thread dump this is approximately 10,000 `WaitingThreadInfo` reference-type instances. The list is bounded by thread count, not heap size — acceptable.
- `_profileByMethodTable` capacity is seeded at 64 in `BeforeHeapIndexScan`, appropriate for typical type variety.
- `ConcurrentDictionary` in `RunParallelAsyncScan` is only allocated in the fallback path. Good.

### Scalability on Very Large Dumps (10GB+)

The primary execution path (shared dispatcher) scales proportionally to the heap-index size, not the object count, and benefits from parallel segment processing. The thread-stack path is bounded by thread count and is not a scalability concern. No identified scalability blockers for 10–100 GB dumps in the primary path.

### Progress Reporting

Thread scan uses `ObjectScanCounter` with `reportEveryObjects: 100` and `reportEveryElapsed: 1s` — appropriate. The async-work participant path calls `_scanCounter.Tick()` on every entry, which is correct. The fallback `RunParallelAsyncScan` has no progress reporting.

---

## Audit Area 6 — Correctness & Confidence

### Top-Frame Pattern Matching — Systemic False Negative Risk

As noted in Area 3, top-frame-only detection will miss blocked threads whose top frame is a native OS wait primitive. On Windows, managed `Monitor.Enter`, `Thread.Sleep`, `WaitHandle.WaitOne`, and similar calls ultimately suspend via `NtWaitForSingleObject` / `NtWaitForMultipleObjects`. The top managed frame may not be visible as frame 0; it depends on whether the managed-to-native transition frame is at the top. This is a correctness risk that affects the most critical detection path in the analyzer.

The current tests do not exercise this scenario with real dump data (only `DiscrepancyFact` integration tests, which require a dump file at a hardcoded path).

### `monitor.enter` vs `monitor.wait` Conflation

Both `topMethod.Contains("monitor.wait")` and `topMethod.Contains("monitor.enter")` map to `WaitType.MonitorWait`. The health-score deadlock heuristic counts threads with `WaitType.MonitorWait && LockCount > 0`. A thread executing `Monitor.Enter` (acquiring a lock) and currently holding another lock is a deadlock candidate. A thread in `Monitor.Wait` has *released* its lock — `LockCount` may be non-zero from a different nested lock, giving a false positive for the deadlock heuristic.

### `ComputeHealthScore` Penalty Capping

```csharp
score -= Math.Min(circularCandidates * 15, 30);
```

With the current false-positive risk in `circularCandidates`, the score can be penalised by 30 points even when no actual deadlock exists. Combined with the 40-point waiting-thread penalty, a score of 30 can be reached with two contested-lock threads and 80% of threads in any wait state — which may be transient contention, not a hang.

### Task State Flag Constants

The bit constants `0x1000000`, `0x200000`, `0x400000` are correct for current .NET runtime versions but are undocumented magic numbers. They should reference the runtime source or be named constants to protect against future confusion. There is no version guard, so these bits are assumed stable across all .NET versions targeted by the analyzer.

### `IsStarved` Logic

```csharp
hangInfo.ThreadPoolInfo.RuntimeInitialized &&
hangInfo.ThreadPoolInfo.RuntimeMaxThreads > 0 &&
hangInfo.ThreadPoolInfo.RuntimeQueueLength.GetValueOrDefault() > 0 &&
hangInfo.ThreadPoolInfo.RuntimeActiveWorkerThreads >= hangInfo.ThreadPoolInfo.RuntimeMaxThreads
```

This condition requires `RuntimeQueueLength > 0`. If ClrMD does not expose `QueueLength` (and `GetIntProperty` returns `null`), `IsStarved` will always be `false` even when `ActiveWorkerThreads >= MaxThreads`. The `QueuedWorkItems` heap-scan proxy is never used as a fallback for starvation detection — it is only surfaced as a metric. A hung thread pool with max workers saturated but no runtime queue length visible will not trigger the starvation finding.

### Test Coverage

- `HangAnalyzerHeapIndexScanTests` covers `CreateWorkerInstance`, `MergePartial` (counters, OR logic, task continuations, profile cache union) — good structural coverage of the parallel-merge path.
- `HangAnalyzerDiscrepancyTests` covers disk-vs-memory result consistency — important regression guard.
- **No unit tests for `DetectWaitPattern`** — the most behaviorally complex method with 7 branches and correctness risks.
- **No unit tests for `ComputeHealthScore`** — the 0–100 scoring function has no test coverage.
- **No tests for the `IsStarved` fallback gap** (starvation without `RuntimeQueueLength`).

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

| Capability | WinDbg SOS | HangAnalyzer |
|---|---|---|
| Thread list with state | `!threads` — full stack, GC mode, domain | Top frame only, 7 wait categories |
| Blocking object table | `!syncblk` — lock address, owner thread, waiters | Not implemented; only lock count |
| Thread pool state | `!tp` — work request queue, active/idle, callbacks | Runtime ClrThreadPool + heap proxy |
| Task/async heap scan | `!dumpheap -type Task` manual | Automated with state classification |
| Deadlock detection | Manual graph from `!syncblk` + `!clrstack` | Heuristic only; no ownership chains |
| Full thread stacks | `!clrstack` per thread | Not exposed |

DumpDetective's automation and structured output are strengths. The critical gap is the absence of `BlockingObjects` usage — WinDbg `!syncblk` exposes exactly this data automatically.

### PerfView

PerfView's async causality chain tracking is based on ETW/EventPipe traces, not dump-time state — not applicable to post-mortem analysis.

### Visual Studio Memory Usage / JetBrains dotMemory

Neither tool provides thread hang analysis. Not relevant for this domain.

### Summary

DumpDetective is ahead of industry tools in automation and structured hang reporting. The primary gap relative to WinDbg is lock-ownership chain analysis, which requires `ClrThread.BlockingObjects` — an available ClrMD API. Closing this gap would make DumpDetective's hang analysis stronger than WinDbg's manual workflow for most production hang investigations.

---

## Final Executive Summary

### Overall Assessment

**Score: 57 / 100**
**Production Readiness: Conditional** — reliable for basic hang detection and thread-pool health reporting; correctness gaps in wait detection reduce confidence in thread-blocking diagnostics.

**Major Strengths**
- Clean dual-participant architecture sharing heap and thread-stack passes efficiently.
- `HealthScore` composite metric provides a useful at-a-glance signal.
- Runtime thread-pool introspection (`ClrRuntime.ThreadPool`) alongside heap-derived proxy data is a well-rounded approach.
- Parallel merge path for heap scan is correctly implemented and tested.
- Discrepancy integration test guards against cache-mode divergence.

**Major Weaknesses**
- Top-frame-only wait detection is a systemic false-negative risk; many blocked threads will not be detected.
- `ClrThread.BlockingObjects` is unused — the single most impactful ClrMD capability for hang diagnosis.
- `LongWaitThreshold` option is dead code.
- `monitor.enter` / `monitor.wait` conflation creates false positives in the deadlock heuristic.
- `HangFindingGenerator` generates one finding with coarse severity; starvation, faulted tasks, and deadlock candidates do not produce independent findings.
- `GetIntProperty` uses reflection for `QueueLength` unnecessarily.

---

### Priority Roadmap

| ID | Recommendation | Area | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|---|
| P0-1 | Use `ClrThread.BlockingObjects` to identify lock owners and build ownership chains | 3, 4 | Critical | Medium | High | Improvement |
| P0-2 | Expand frame scan to N frames (5–10) in `DetectWaitPattern`; walk frames to first wait-pattern match | 3, 5, 6 | Critical | Low | High | Improvement |
| P0-3 | Split `WaitType.MonitorWait` into `MonitorEnter` and `MonitorWait`; fix health-score heuristic | 2, 6 | High | Low | High | Improvement |
| P0-4 | Wire `LongWaitThreshold` or remove the dead option | 1, 2 | Medium | Low | High | Improvement |
| P1-1 | Replace `GetIntProperty` reflection with direct `ClrThreadPool.QueueLength` property | 3 | Medium | Low | High | Improvement |
| P1-2 | Use `QueuedWorkItems` heap-proxy as fallback in `IsStarved` when `RuntimeQueueLength` is null | 6 | Medium | Low | High | Improvement |
| P1-3 | Add `HangFindingGenerator` findings for starvation, faulted-task count, and deadlock candidates independently | 2 | High | Low | High | Improvement |
| P1-4 | Fix `SectionLeadFinding` severity: `Critical` for starvation, `Warning` for degraded score | 2 | Medium | Low | High | Improvement |
| P1-5 | Honour `TopWaitingThreadsPerGroup` in `Analyze()` instead of `Take(10)` | 2 | Low | Low | High | Improvement |
| P1-6 | Add unit tests for `DetectWaitPattern` and `ComputeHealthScore` | 6 | High | Medium | High | Improvement |
| P1-7 | Extract faulted-task exception type names (read `m_contingentProperties` chain) | 4 | High | Medium | Medium | Improvement |
| P2-1 | Add `TaskScanLimited` warning block to `HangSectionBuilder` | 2 | Medium | Low | High | Improvement |
| P2-2 | Surface `UsingPortableThreadPool` / `UsingWindowsThreadPool` in `HangDomainResult` and section builder | 3 | Medium | Low | High | Improvement |
| P2-3 | Add task-state breakdown compact table (pending/faulted/canceled/completed row set) | 2 | Medium | Low | High | Improvement |
| P2-4 | Add `SynchronizationContext` queue-depth scan for WPF/WinForms/legacy ASP.NET | 4 | Medium | Medium | Medium | Improvement |
| P2-5 | Name the `m_stateFlags` bit constants and add a reference comment | 3, 6 | Low | Low | High | Improvement |
| P2-6 | Add cancellation token propagation into `RunParallelAsyncScan` `Parallel.ForEach` | 5 | Low | Low | High | Improvement |
| P3-1 | Add timer queue analysis (`TimerQueueTimer` heap scan) | 4 | Medium | Medium | Medium | Improvement |
| P3-2 | Add async causality chain reconstruction (trace `m_continuationObject` for top N continuations) | 4 | High | High | Low | Improvement |
| P3-3 | Correlate `HangAnalyzer` blocking candidates with `LockGraphAnalyzer` output in `InsightEngine` | 1, 4 | High | Medium | Medium | Evolution |

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. Thread-pool health reporting and async-work inventory are reliable. Thread-blocking detection has a systemic false-negative risk due to top-frame-only scanning. Starvation detection silently degrades to false-negative when `RuntimeQueueLength` is unavailable. These are deployable gaps in a monitored environment, but should be prioritised before claiming full production readiness.

2. **Highest-impact improvements:** P0-1 (`ClrThread.BlockingObjects`) and P0-2 (multi-frame detection) together address the most material correctness gap. P1-3 (richer finding generation) addresses the most visible reporting gap.

3. **Platform evolution opportunities:** The lock-ownership chain built from `BlockingObjects` (P0-1) is the right data to feed into `InsightEngine` cross-correlation with `LockGraphAnalyzer` (P3-3). If implemented, this would create the first cross-analyzer deadlock circuit detector in the platform — a capability that no automated tool currently exposes cleanly from dumps.

4. **Highest engineering return:** P0-2 (multi-frame detection) is low effort with disproportionate correctness impact. P1-1 (remove `GetIntProperty` reflection) and P1-5 (fix `TopWaitingThreadsPerGroup`) are trivial fixes. P0-1 (`BlockingObjects`) provides the single largest diagnostic value gain.
