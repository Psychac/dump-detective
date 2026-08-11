# LockGraphAnalyzer Audit

**Protocol**: [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)  
**Components reviewed**: `LockGraphAnalyzer.cs`, `LockGraphDomainResult.cs`, `LockGraphAnalysisOptions.cs`, `LockGraphSectionBuilder.cs`, `LockGraphFindingGenerator.cs`, `LockGraphTrendComparer.cs`, `LockGraphAnalyzerDiscrepancyTests.cs`, `IThreadStackScanParticipant.cs`, `ThreadStackScanDispatcher.cs`

---

## Area 1 — Role & Opportunity Assessment

### Current role

Detects monitor lock contention and deadlock *candidates* from a memory dump by enumerating inflated `SyncBlock`s and cross-referencing owning threads' top-frame signatures.

### How well does it solve it?

Partially. It correctly enumerates `SyncBlock`s and produces contention counts. The deadlock detection is purely heuristic — it does not build a wait-for graph or detect cycles. A thread that holds any inflated lock *and* whose top frame contains `"monitor.wait"` or `"monitor.enter"` is labelled a deadlock candidate. No actual circular-wait relationship is verified.

### Coverage gaps

- **Thin locks** (lock word stored in the object header, not yet inflated) are structurally invisible to `EnumerateSyncBlocks()`. This is a fundamental ClrMD limitation but is not communicated to the user.
- **`ClrThread.BlockingObjects` does not exist.** Verified by reflecting the installed `Microsoft.Diagnostics.Runtime.dll` 4.0.732401: `ClrThread` exposes only `LockCount`, `CurrentException`, `State`, `GCMode`, etc. — no `BlockingObjects` property, no per-thread blocking-object enumeration of any kind. This was never available to use, not merely unused (same finding as `ThreadAnalyzer` P0-3 — see [thread-analyzer-audit.md](thread-analyzer-audit.md)).
- **No wait-for graph is possible with current ClrMD APIs.** `heap.EnumerateSyncBlocks()` gives the *holder* of a contested lock (`HoldingThreadAddress`) and a *count* of waiters (`WaitingThreadCount`), but no waiter identity and no way to tell which lock a given blocked thread is waiting on. Without waiter-to-lock identity, "Thread A waits for lock L held by Thread B" edges cannot be constructed from the heap/thread data ClrMD exposes — this isn't a missed API call, it's a structural gap in what the runtime surfaces.
- **No waiting thread identity**. For each contested lock, the number of waiters is known but *which* threads are waiting is not captured.
- **Non-monitor synchronisation primitives** — `ReaderWriterLockSlim`, `SemaphoreSlim`, `Mutex`, `ManualResetEvent`, `SpinLock` — are not covered.
- **`Monitor.TryEnter`** is not matched by the top-frame heuristic despite being a potential blocking call when used with a timeout and a tight spin.
- **Lock nesting depth and acquisition order** are not tracked.

### Unexpected functionality

None. The analyzer is narrowly scoped to `SyncBlock`-based monitors.

### Shared infrastructure opportunities

- No `ClrThread.BlockingObjects`-based `LockWaitGraph` primitive is possible — the API doesn't exist. Any shared primitive would have to be built from the same `heap.EnumerateSyncBlocks()` holder/waiter-count data this analyzer and `ThreadAnalyzer` already have access to, i.e. a shared "contested lock inventory," not a wait-for graph.
- A `WaitForGraph<TNode>` DFS-cycle-detection utility would still be valuable *if* a future ClrMD version exposes per-thread blocking-object data (see P0-1/P0-2 reclassification below); it has no data source to operate on today.

### Architectural observations

The `IThreadStackScanParticipant` integration is correct and appropriately conservative (`GetRequiredFrameCount` returns 1). The fallback path for non-pipeline invocations is well-structured.

---

## Area 2 — Diagnostic & Report Quality

### Strengths

- Key metrics (held, contested, max waiters, deadlock candidate count) are immediately visible.
- Contested lock table includes address, type, waiter count, owning thread ID, and recursion count — actionable for most contention scenarios.
- "Suspected deadlock locks" table (intersection of deadlock candidates and contested locks) is a useful correlation.
- The caveat `"Detection is based on recorded BlockingObjects; cooperative waits may not appear"` (`ReportSectionAssembler.cs:310`) is **inaccurate** — `BlockingObjects` is never used anywhere in this codebase because it does not exist in ClrMD 4. The caveat should instead describe what's actually true: detection is based on `heap.EnumerateSyncBlocks()` (global, held monitors only) plus a top-frame string heuristic, and misses thin locks, non-monitor primitives, and any wait not visible in the captured top frame.
- Trend comparer exposes four meaningful metrics.

### Weaknesses

- **`DeadlockCandidateSnapshot.CycleSummary`** — the field name implies a cycle was verified. The actual value is `"Thread 5 (OS: 3456) holds 2 lock(s), blocked at: System.Threading.Monitor.Enter(Object, Boolean&)"`. No cycle is described.
- **Lead finding threshold** — `DeadlockCandidateCount >= 2` triggers "Critical — Probable deadlock pattern detected." Two independently stuck threads with no circular-wait relationship between them satisfy this condition.
- **Confidence band is hardcoded at 0.85** regardless of whether one or ten candidates exist, whether locks have owners, or whether all owner threads resolved correctly.
- **Waiting thread identities are absent**. The report states "3 waiters" on a lock but does not show which threads those are.
- **Owner thread context is limited to the top frame** during the participant path. No additional stack context for deadlock candidate owners is shown.
- **Top contested types table** aggregates cumulative waiters across all instances of a type. A single highly-contested object of a common type can make the type appear more dominant than it is relative to per-object contention.
- **No text-form wait chain**. "Thread A holds Lock1 → Thread B holds Lock2 → Thread B waits for Lock1 held by Thread A" is not produced even when evidence would support it.

### Missing diagnostics

- Waiting thread IDs per contested lock.
- Owner thread frames beyond the top for deadlock candidate analysis.
- Full wait chain narrative — not achievable with current ClrMD APIs (`BlockingObjects` does not exist; no waiter-to-lock identity is exposed).
- Recursion count analysis — a high recursion count on a contested lock is a re-entrancy signal worth calling out separately.

---

## Area 3 — ClrMD & Platform Utilization

### `ClrThread.BlockingObjects` does not exist in ClrMD 4

Prior drafts of this audit assumed `BlockingObjects` was an unused-but-available ClrMD API that directly exposes what a thread is blocked on (lock addresses, wait kind, owner thread). **It is not.** Reflecting the installed `Microsoft.Diagnostics.Runtime.dll` 4.0.732401 shows `ClrThread` has no `BlockingObjects` member and no `ClrBlockingObject` type exists in the assembly at all (identical finding to `ThreadAnalyzer` P0-3 — see [thread-analyzer-audit.md](thread-analyzer-audit.md)). There is no ClrMD 4 API that would:

1. Eliminate the top-frame string matching (there is no alternative source for "what is this thread blocked on").
2. Enable identification of *which threads are waiting* for each contested lock (`SyncBlock.WaitingThreadCount` is a count, not a set of thread IDs).
3. Provide wait-for graph edges (no waiter-to-lock identity is exposed anywhere in ClrMD 4).

The current top-frame heuristic — checking `topFrameSignature.ToLowerInvariant().Contains("monitor.enter")` — is fragile (JIT inlining can remove/transform the frame; obfuscation changes method names; `Monitor.TryEnter` with a timeout is missed), but it is not a shortcut around a better available API. It is the *only* signal ClrMD 4 offers for inferring what a thread is blocked on. Improvements should focus on hardening the heuristic (more wait patterns, `StringComparison.OrdinalIgnoreCase`) and being explicit in reports that this is inference, not a verified fact — not on replacing it with a nonexistent direct API.

### `heap.EnumerateSyncBlocks()` usage

Correct. `sb.IsMonitorHeld` correctly restricts to held monitors. `sb.Object == 0` guard is correct. The `ObjectScanCounter` is used but `reportEveryObjects: 1000` means progress never fires for typical sync block counts (usually < 200). This is harmless.

### LINQ in candidate-building inner loop

```csharp
// Called per-thread inside a foreach over threads
result.AllHeldLocks
    .Where(l => l.OwnerThread?.ManagedThreadId == thread.ManagedThreadId)
    .ToList()
```

This is O(M × N) where M = threads and N = held locks. A `Dictionary<int, List<LockContention>>` keyed by `ManagedThreadId` would reduce this to O(N) build + O(1) lookup.

### Thread list materialised to `List<ClrThread>`

`var threads = new List<ClrThread>(runtime.Threads)` is used because the list is iterated twice (once to build `threadByAddress`, once for deadlock candidates). This is acceptable — thread count is always small.

### Unused infrastructure

- `HeapAnalysisCache` — correctly not used; this analyzer does not walk heap objects.
- `ObjectIndexReader` — correctly not used.

### `IThreadStackScanParticipant` integration

Correctly implemented. `GetRequiredFrameCount` returns 1. `BeforeThreadStackScan` initialises the dictionary. The `_participantScanSucceeded` gate and fallback path are well-designed.

---

## Area 4 — Diagnostic Opportunity Analysis

### Not achievable with ClrMD 4 (no data source)

| Diagnostic | Why blocked |
|---|---|
| Build a real wait-for graph | Requires waiter-to-lock identity; `ClrThread.BlockingObjects` does not exist and no substitute API exposes this |
| Identify waiting threads per lock | `SyncBlock.WaitingThreadCount` is a count only, no thread IDs |
| True (verified) deadlock cycle reporting | Depends on the wait-for graph above; not constructible |

### High-value, achievable with existing ClrMD APIs

| Diagnostic | API basis | Value |
|---|---|---|
| Waiting thread top frames | `thread.EnumerateStackTrace()` scoped to threads whose top frame matches a wait pattern | Context for waiting side (still heuristic, not verified identity-to-lock mapping) |
| Owner thread frame summary | Already have owner thread; fetch N frames | Context for owning side |
| Global lock-contention table (holder + waiter count, no per-waiter identity) | `heap.EnumerateSyncBlocks()` filtered to `WaitingThreadCount > 0` | Honest, achievable substitute for a wait-for graph; same approach as `ThreadAnalyzer` P0-3 |

### Medium-value additions

- **Recursion count analysis**: flag objects where `RecursionCount >= 3` — potential re-entrancy bug.
- **`ReaderWriterLockSlim` state reader**: enumerate instances, read `_rwLock` field, identify writer-held or reader-starved state.
- **Thread starvation heuristic**: if a lock has been contested across multiple dumps (trend data), flag threads that appear as waiters in both.
- **Per-lock waiter-to-holder ratio**: `WaitingThreadCount / (RecursionCount > 0 ? 1 : 1)` as a contention-pressure metric.
- **Unresolved owners**: count of contested locks with `HoldingThreadAddress != 0` but no matching thread in `threadByAddress` — indicates threads that exited while holding the lock (a potential cause of deadlock).

### Lower-value / high-effort

- `Mutex` / native sync object tracking — requires reading CLR internal structures not exposed by ClrMD.
- `SemaphoreSlim` wait queue traversal — requires object field navigation.

---

## Area 5 — Performance, Memory & Scalability

### `heap.EnumerateSyncBlocks()`

Scales with the number of *inflated* sync blocks, not heap size. Sync block counts are typically O(100–1 000). This section is not a scalability concern even on 100 GB dumps.

### Thread enumeration

`runtime.Threads` returns O(100–1 000) entries in production dumps. Materialising to a list twice is negligible.

### O(M × N) deadlock candidate construction

```csharp
foreach (var thread in threads)   // M threads
{
    ...
    LocksHeld = result.AllHeldLocks
        .Where(l => l.OwnerThread?.ManagedThreadId == thread.ManagedThreadId)
        .ToList();  // scans N locks per thread
}
```

With M = 500 threads and N = 200 held locks this is 100 000 comparisons. Not a problem in practice, but the pattern is avoidable by pre-grouping locks by owner ManagedThreadId.

### Progress reporting dead zone

`reportEveryObjects: 1000` on a sync block enumeration that typically yields < 200 entries means `Tick()` never crosses a report threshold. The `Complete()` call still fires. This should use `reportEveryObjects: 50` or a lower value suited to sync block cardinality.

### Temporary allocations

- `Dictionary<ulong, ClrThread>` — built once, correct.
- `Dictionary<string, int> typeWaiters` — built from contested lock list, small.
- `_participantTopFrameSignatureByThreadAddress` — one entry per alive thread with `LockCount > 0`, small.
- `topContestedTypes`, `contestedLockDetails`, `deadlockDetails` — all bounded by `MaxContestedLocksToShow`. Correct.

### Memory profile across dump sizes

The analyzer's memory footprint is proportional to thread count and sync block count, not heap object count. Bounded and safe for any dump size.

### Cancellation

`cancellationToken.ThrowIfCancellationRequested()` is called once at analyzer entry and within `ThreadStackScanDispatcher.Run`. The `foreach (SyncBlock sb in heap.EnumerateSyncBlocks())` loop does not check cancellation per iteration, which is acceptable given the short iteration count.

---

## Area 6 — Correctness & Confidence

### Deadlock detection is heuristic, not algorithmic

The central correctness risk. The claim "deadlock candidate" means: a thread holds an inflated monitor lock **and** its top frame is at `Monitor.Wait` or `Monitor.Enter`. This does **not** establish a circular wait.

Two threads each independently blocking at `Monitor.Enter` for unrelated locks — neither waiting for the other — will both be flagged, `DeadlockCandidateCount` will be 2, and the finding generator will emit severity `Critical` with recommendation "Deadlock candidates detected." This is a false positive.

The `CycleSummary` field name compounds the confusion: it implies a cycle was proven.

### Top-frame signature matching

```csharp
if (!sig.Contains("monitor.wait") && !sig.Contains("monitor.enter"))
    continue;
```

Fragile for three reasons:
1. JIT inlining can suppress `Monitor.Enter` from the stack; the calling method becomes the top frame.
2. `Monitor.TryEnter(object, int)` — a thread spinning in a timed TryEnter is not detected.
3. `ToLowerInvariant()` is correct for culture-independence but adds an allocation per thread per invocation; `StringComparison.OrdinalIgnoreCase` with `Contains` overload avoids it.

### `_participantScanSucceeded` default is `false`

The field defaults to `false`. If `OnThreadStackScanCompleted` is never called (e.g., `ThreadStackScanDispatcher` is not run), the fallback independent walk is triggered. This is correct behaviour and protects tests and direct invocations.

### `GetRequiredFrameCount` returns 1

Correct for the top-frame-only check, but if the frame capture fails for a thread (e.g., corrupted stack), `TopFrames.Count > 0` is false and `topFrameSignature` is null, which correctly skips that thread.

### Sync block owner resolution

`threadByAddress.TryGetValue(sb.HoldingThreadAddress, ...)` correctly handles the case where the owning thread has exited (null owner). The contested-lock snapshot stores `OwnerManagedThreadId = null` and the section builder renders "unknown." Correct.

### Confidence score

Hardcoded at 0.85 regardless of evidence quality. A scenario with zero resolved owners and `DeadlockCandidateCount = 5` based entirely on `"monitor.enter"` top-frame matches warrants a lower reported confidence.

---

## Area 7 — Industry Benchmark

### WinDbg + SOS `!dlk`

`!dlk` builds a true wait-for graph and runs DFS cycle detection. It reports the exact cycle: "Thread X owns Lock A, waits for Lock B. Thread Y owns Lock B, waits for Lock A." It shows each thread's full stack at point of blocking. DumpDetective's heuristic produces no equivalent; the `CycleSummary` does not convey a cycle.

**Gap**: `!dlk` operates directly on the DAC/debugger layer with native access to thread block-info that ClrMD 4 does not surface as a managed API (no `BlockingObjects`, no `ClrThread`-level wait target). This is a genuine capability gap between what WinDbg's native debugging engine can see and what ClrMD 4 exposes to managed consumers — not a case of DumpDetective having access to an API and failing to call it.

### PerfView

PerfView's thread-time analysis shows lock acquisition latency and wait durations on live processes. On dumps, it has no specific lock graph view. Not directly comparable.

### JetBrains dotMemory

Has a "Synchronization" view listing sync blocks with owner threads. Similar capability to DumpDetective's contested lock table. No automated deadlock detection.

### Visual Studio Memory Profiler

Shows thread states (Blocked, Running) but no lock ownership graph.

### Summary

DumpDetective's contention reporting is broadly competitive with dotMemory and VS. The deadlock detection is significantly weaker than WinDbg SOS because ClrMD 4 does not expose the per-thread blocking-object data `!dlk` relies on — this is a platform limitation, not an unused API. The name "LockGraphAnalyzer" sets an expectation of graph-based analysis that the current implementation cannot meet with ClrMD 4 and should either be renamed or have its scope explicitly documented as "contention inventory + heuristic candidate flagging," not a true wait-for graph.

---

## Executive Summary

### Overall Assessment

**Score: 52 / 100**

**Production readiness**: Partial. The contention inventory (sync block enumeration, contested lock table) is correct and useful. The deadlock detection is not production-reliable because it can emit Critical findings for non-deadlock situations.

**Major strengths**
- Correct and efficient `SyncBlock` enumeration.
- Well-integrated `IThreadStackScanParticipant` design.
- Clear contested lock table with owner, waiter count, recursion.
- "Suspected deadlock locks" cross-correlation table.
- Safe memory footprint — independent of heap size.

**Major weaknesses**
- No wait-for graph and no cycle detection possible — `ClrThread.BlockingObjects` does not exist in ClrMD 4 (verified by reflection); deadlock label is heuristic only and cannot be upgraded to verified without a future ClrMD API.
- `CycleSummary` field is misleadingly named; contains no cycle description.
- `DeadlockCandidateCount >= 2` → `Critical` is a false-positive-prone threshold.
- Waiting thread identities not captured — `SyncBlock.WaitingThreadCount` exposes no thread IDs, so this is not capturable with current APIs either.
- The shipped report limitations text (`ReportSectionAssembler.cs:310`) claims detection is "based on recorded BlockingObjects," which is factually wrong — no such API is called anywhere in the codebase. Should be corrected to describe the actual `heap.EnumerateSyncBlocks()` + top-frame-heuristic basis.
- No unit tests; only one integration/discrepancy test.

---

### Priority Roadmap

| # | Recommendation | Area | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|---|
| P0-1 | ~~Use `ClrThread.BlockingObjects` to build actual wait-for edges~~ — **BLOCKED**: API does not exist in ClrMD 4 (verified by reflection). Reclassified: add a global lock-contention table (`heap.EnumerateSyncBlocks()` filtered to `WaitingThreadCount > 0`, holder + waiter count, no per-waiter identity) and harden the top-frame heuristic instead | Correctness | Critical | Medium | High | Improvement |
| P0-2 | ~~Implement DFS cycle detection on the wait-for graph~~ — **BLOCKED**: no wait-for graph is constructible without waiter-to-lock identity, which ClrMD 4 does not expose. No true cycle detection is achievable until a future ClrMD API surfaces per-thread blocking-object data | Correctness | Critical | Medium | High | Improvement |
| P0-3 | Rename `CycleSummary` to `BlockedAtFrame` or populate it with a real cycle description | Diagnostic quality | High | Low | High | Improvement | ✅ DONE |
| P0-4 | Fix `DeadlockCandidateCount >= 2` → `Critical` threshold to require a confirmed cycle | Correctness | High | Low | High | Improvement | ✅ DONE |
| P0-5 | Fix the shipped limitations text (`ReportSectionAssembler.cs:310`) — it claims detection is "based on recorded BlockingObjects," an API that is never called anywhere in the codebase | Correctness | High | Trivial | High | Improvement | ✅ DONE |
| P1-1 | ~~Capture waiting thread IDs per contested lock (via `BlockingObjects`)~~ — **BLOCKED**: `SyncBlock.WaitingThreadCount` is a count only, no thread IDs; no ClrMD 4 API exposes waiter identity | Diagnostic quality | High | Low (follows P0-1) | High | Improvement |
| P1-2 | Add owner thread frame summary (N frames) for deadlock candidate owners | Diagnostic quality | High | Low | High | Improvement | ✅ DONE |
| P1-3 | Replace O(M×N) lock-to-thread grouping with a pre-built `Dictionary<int, List<LockContention>>` | Performance | Medium | Low | High | Improvement | ✅ DONE |
| P1-4 | Add unit tests: contested lock detection, deadlock cycle detection, no-contention path, null-owner path | Testing | High | Medium | High | Improvement |
| P2-1 | Expose unresolved-owner count in domain result and flag in report (thread exited while holding lock) | Diagnostic quality | Medium | Low | High | Improvement | ✅ DONE |
| P2-2 | Add `ReaderWriterLockSlim` state detection via heap object field inspection | Coverage | Medium | Medium | Medium | Improvement |
| P2-3 | Make confidence score dynamic (lower when owner resolution rate is poor) | Diagnostic quality | Medium | Low | High | Improvement | ✅ DONE |
| P2-4 | Elevate a shared "contested lock inventory" primitive (holder + waiter count from `heap.EnumerateSyncBlocks()`, not a wait-for graph) for HangAnalyzer/ThreadAnalyzer cross-correlation | Architecture | Medium | Medium | Medium | Evolution |
| P2-5 | Lower `reportEveryObjects` in `ObjectScanCounter` to `50` for sync block enumeration | Performance | Low | Trivial | High | Improvement | ✅ DONE |
| P2-6 | Replace `ToLowerInvariant().Contains(...)` with `string.Contains(..., StringComparison.OrdinalIgnoreCase)` | Correctness | Low | Trivial | High | Improvement |
| P3-1 | Flag high-recursion contested locks as potential re-entrancy signals | Diagnostic quality | Low | Low | Medium | Improvement |
| P3-2 | `SemaphoreSlim` and cooperative-wait primitive detection via heap object traversal | Coverage | Low | High | Low | Improvement |

---

### Final Verdict

1. **Production-ready?** Not for the deadlock detection path. The contention inventory (held lock count, contested lock table) is production-quality. The deadlock detection is permanently heuristic — not a temporary gap to be closed by P0-1/P0-2 — because ClrMD 4 exposes no per-thread blocking-object data. It should be gated or explicitly labelled as heuristic/unverified, and P0-5 (fix the inaccurate "based on BlockingObjects" limitations text) should ship regardless.

2. **Highest-impact improvements**: P0-1 and P0-2 as originally framed (use `ClrThread.BlockingObjects`, DFS cycle detection) are **not implementable** — verified by reflection, the API doesn't exist. The achievable substitute is a global lock-contention table (holder + waiter count, `WaitingThreadCount > 0`) plus a hardened, honestly-labelled top-frame heuristic. P0-3, P0-4, and P0-5 are the real near-term wins: they eliminate misleading outputs without depending on a nonexistent API.

3. **Platform evolution opportunity**: A shared "contested lock inventory" primitive built from `heap.EnumerateSyncBlocks()` (not `BlockingObjects`) would make `HangAnalyzer`, `ThreadAnalyzer`, and `LockGraphAnalyzer` more coherent and avoid three separate re-implementations of the same sync-block scan.

4. **Highest engineering return**: P0-3 → P0-4 → P0-5 → P1-1's achievable substitute (global contention table). True per-thread wait-for graph and verified cycle detection are blocked until a future ClrMD version exposes per-thread blocking-object data — track as an external dependency, not an internal implementation task.
