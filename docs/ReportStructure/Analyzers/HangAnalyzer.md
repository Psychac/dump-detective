# HangAnalyzer — Coverage & Change Spec

## Status
**Existing** · Split + Modify · Implementation Priority **2** (split) · Effort: Medium · ✅ **Split Completed**

## Report Sections Served (post-split — hang/blocking portion only)
- §7.1 Thread Lifecycle (thread pool state: MinThreads/MaxThreads/QueueLength/CpuUtilization)
- §7.2 Synchronization Patterns (waiting threads, lock holders, async-over-sync detection)

> ⚠️ Task/async analysis is extracted to `AsyncTaskAnalyzer` (Priority 2, same split effort).
> See [AsyncTaskAnalyzer.md](AsyncTaskAnalyzer.md) for the extracted component.

---

## Currently Produces
- `HangDomainResult`: waiting threads, threads holding locks, wait category breakdown
- Task data: `TotalTasks`, `PendingTasks`, `FaultedTasks`, `CanceledTasks`
- Continuation types, threadpool info, `HealthScore`

---

## Problem
`HangDomainResult` conflates two orthogonal concerns:
- **Hang/blocking** — waiting threads, lock holders, health score → §7
- **Async/Task state** — pending/faulted tasks, continuation types → §8

---

## Required Changes — SPLIT

### ✂️ Extract `AsyncTaskAnalyzer` (new, Priority 2)
Move all task lifecycle logic to `AsyncTaskAnalyzer`. See [AsyncTaskAnalyzer.md](AsyncTaskAnalyzer.md).

### Modify `HangAnalyzer` (after split)
Remove all task scanning logic. `HangAnalyzer` retains:
- `WaitingThreads` analysis
- `ThreadsHoldingLocks`
- Async-over-sync detection (threads blocking on `.Result`/`.Wait()`)
- `HealthScore` (now thread-blocking only — no task count contribution)
- Updated `HangDomainResult` removes task fields (they move to `AsyncTaskDomainResult`)

**Deprecate from `HangDomainResult`:**
`TotalTaskContinuations`, `QueuedWorkItems`, `TotalTasks`, `PendingTasks`,
`FaultedTasks`, `CanceledTasks`, `TaskScanLimited`

---

## Phase Assignment

`HangAnalyzer` (post-split) is **entirely Phase 2**. Thread blocking analysis requires live
`runtime.Threads` + `ClrThread.BlockingObjects` — not capturable in Phase 1.

The split does NOT add any Phase 1 work to `HangAnalyzer`. All retained logic is Phase 2.

`TaskScanLimited` flag must survive the split on the `AsyncTaskDomainResult` side (§17 confidence).

---

## Related Analyzers
- **`AsyncTaskAnalyzer`** (new, extracted) — handles §8 task lifecycle; split from this analyzer
- **`ThreadAnalyzer`** — thread counts and wait categories; complementary to hang/blocking detection
- **`LockGraphAnalyzer`** — deadlock detection; `HangAnalyzer` provides the blocking-thread input
