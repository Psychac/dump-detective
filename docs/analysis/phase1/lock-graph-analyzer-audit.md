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
- **`ClrThread.BlockingObjects`** is not used. This ClrMD API directly exposes what a thread is currently blocked on and is the standard basis for lock graph construction.
- **No wait-for graph**. There is no directed graph of "Thread A waits for lock L held by Thread B." Without it, cycle detection is impossible.
- **No waiting thread identity**. For each contested lock, the number of waiters is known but *which* threads are waiting is not captured.
- **Non-monitor synchronisation primitives** — `ReaderWriterLockSlim`, `SemaphoreSlim`, `Mutex`, `ManualResetEvent`, `SpinLock` — are not covered.
- **`Monitor.TryEnter`** is not matched by the top-frame heuristic despite being a potential blocking call when used with a timeout and a tight spin.
- **Lock nesting depth and acquisition order** are not tracked.

### Unexpected functionality

None. The analyzer is narrowly scoped to `SyncBlock`-based monitors.

### Shared infrastructure opportunities

- `ClrThread.BlockingObjects` data could be elevated to a shared `LockWaitGraph` platform primitive consumable by HangAnalyzer and ThreadAnalyzer.
- A `WaitForGraph<TNode>` utility (DFS cycle detection) would benefit this analyzer and any future synchronisation analyzer.

### Architectural observations

The `IThreadStackScanParticipant` integration is correct and appropriately conservative (`GetRequiredFrameCount` returns 1). The fallback path for non-pipeline invocations is well-structured.

---

## Area 2 — Diagnostic & Report Quality

### Strengths

- Key metrics (held, contested, max waiters, deadlock candidate count) are immediately visible.
- Contested lock table includes address, type, waiter count, owning thread ID, and recursion count — actionable for most contention scenarios.
- "Suspected deadlock locks" table (intersection of deadlock candidates and contested locks) is a useful correlation.
- The caveat `"Detection is based on recorded BlockingObjects; cooperative waits may not appear"` is accurate.
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
- Full wait chain narrative when `BlockingObjects` would enable it.
- Recursion count analysis — a high recursion count on a contested lock is a re-entrancy signal worth calling out separately.

---

## Area 3 — ClrMD & Platform Utilization

### `ClrThread.BlockingObjects` not used

`BlockingObjects` is ClrMD's direct representation of a thread's monitored wait. It returns the lock object addresses, the kind of wait (`MonitorWait`, `MonitorLock`, `WaitOne`, `Unknown`), and for `MonitorLock` the owner thread. Using it would:

1. Eliminate the fragile top-frame string matching entirely.
2. Enable identification of *which threads are waiting* for each contested lock.
3. Provide the raw edges of a wait-for graph without a second heap or stack enumeration.

The current approach — checking `topFrameSignature.ToLowerInvariant().Contains("monitor.enter")` — is fragile: JIT inlining can remove or transform the frame; obfuscation changes method names; `Monitor.TryEnter` with a timeout is missed entirely.

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

### High-value, achievable with existing ClrMD APIs

| Diagnostic | API basis | Value |
|---|---|---|
| Build a real wait-for graph | `ClrThread.BlockingObjects` | Enables true cycle detection |
| Identify waiting threads per lock | `ClrThread.BlockingObjects` | Shows which threads are stuck |
| True deadlock cycle reporting | DFS on wait-for graph | Replaces heuristic with verified cycles |
| Waiting thread top frames | `thread.EnumerateStackTrace()` scoped to waiters | Context for waiting side |
| Owner thread frame summary | Already have owner thread; fetch N frames | Context for owning side |

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

`!dlk` builds a true wait-for graph from `BlockingObjects` and runs DFS cycle detection. It reports the exact cycle: "Thread X owns Lock A, waits for Lock B. Thread Y owns Lock B, waits for Lock A." It shows each thread's full stack at point of blocking. DumpDetective's heuristic produces no equivalent; the `CycleSummary` does not convey a cycle.

**Gap**: `ClrThread.BlockingObjects` is the same data source SOS uses. DumpDetective has access to it and does not use it.

### PerfView

PerfView's thread-time analysis shows lock acquisition latency and wait durations on live processes. On dumps, it has no specific lock graph view. Not directly comparable.

### JetBrains dotMemory

Has a "Synchronization" view listing sync blocks with owner threads. Similar capability to DumpDetective's contested lock table. No automated deadlock detection.

### Visual Studio Memory Profiler

Shows thread states (Blocked, Running) but no lock ownership graph.

### Summary

DumpDetective's contention reporting is broadly competitive with dotMemory and VS. The deadlock detection is significantly weaker than WinDbg SOS because it does not use `ClrThread.BlockingObjects` for graph construction. The name "LockGraphAnalyzer" sets an expectation of graph-based analysis that the current implementation does not meet.

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
- No use of `ClrThread.BlockingObjects` — the primary ClrMD API for lock waits.
- No wait-for graph and no cycle detection; deadlock label is heuristic only.
- `CycleSummary` field is misleadingly named; contains no cycle description.
- `DeadlockCandidateCount >= 2` → `Critical` is a false-positive-prone threshold.
- Waiting thread identities not captured.
- No unit tests; only one integration/discrepancy test.

---

### Priority Roadmap

| # | Recommendation | Area | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|---|
| P0-1 | Use `ClrThread.BlockingObjects` to build actual wait-for edges; replace top-frame heuristic | Correctness | Critical | Medium | High | Improvement |
| P0-2 | Implement DFS cycle detection on the wait-for graph; only emit deadlock when a cycle is proven | Correctness | Critical | Medium | High | Improvement |
| P0-3 | Rename `CycleSummary` to `BlockedAtFrame` or populate it with a real cycle description | Diagnostic quality | High | Low | High | Improvement | ✅ DONE |
| P0-4 | Fix `DeadlockCandidateCount >= 2` → `Critical` threshold to require a confirmed cycle | Correctness | High | Low | High | Improvement | ✅ DONE |
| P1-1 | Capture waiting thread IDs per contested lock (via `BlockingObjects`) | Diagnostic quality | High | Low (follows P0-1) | High | Improvement |
| P1-2 | Add owner thread frame summary (N frames) for deadlock candidate owners | Diagnostic quality | High | Low | High | Improvement | ✅ DONE |
| P1-3 | Replace O(M×N) lock-to-thread grouping with a pre-built `Dictionary<int, List<LockContention>>` | Performance | Medium | Low | High | Improvement |
| P1-4 | Add unit tests: contested lock detection, deadlock cycle detection, no-contention path, null-owner path | Testing | High | Medium | High | Improvement |
| P2-1 | Expose unresolved-owner count in domain result and flag in report (thread exited while holding lock) | Diagnostic quality | Medium | Low | High | Improvement |
| P2-2 | Add `ReaderWriterLockSlim` state detection via heap object field inspection | Coverage | Medium | Medium | Medium | Improvement |
| P2-3 | Make confidence score dynamic (lower when owner resolution rate is poor) | Diagnostic quality | Medium | Low | High | Improvement |
| P2-4 | Elevate `LockWaitGraph` to a shared platform primitive for HangAnalyzer cross-correlation | Architecture | Medium | Medium | Medium | Evolution |
| P2-5 | Lower `reportEveryObjects` in `ObjectScanCounter` to `50` for sync block enumeration | Performance | Low | Trivial | High | Improvement |
| P2-6 | Replace `ToLowerInvariant().Contains(...)` with `string.Contains(..., StringComparison.OrdinalIgnoreCase)` | Correctness | Low | Trivial | High | Improvement |
| P3-1 | Flag high-recursion contested locks as potential re-entrancy signals | Diagnostic quality | Low | Low | Medium | Improvement |
| P3-2 | `SemaphoreSlim` and cooperative-wait primitive detection via heap object traversal | Coverage | Low | High | Low | Improvement |

---

### Final Verdict

1. **Production-ready?** Not for the deadlock detection path. The contention inventory (held lock count, contested lock table) is production-quality. The deadlock detection can produce Critical-severity false positives and should be gated or labelled as heuristic until P0-1 and P0-2 are implemented.

2. **Highest-impact improvements**: P0-1 (use `ClrThread.BlockingObjects`) and P0-2 (DFS cycle detection) together would transform the analyzer from a heuristic flag generator into a genuine deadlock detector. Both are achievable with ClrMD APIs already available in the runtime. P0-3 and P0-4 are single-line fixes that eliminate the most misleading outputs.

3. **Platform evolution opportunity**: A shared `LockWaitGraph` primitive built from `BlockingObjects` would make both HangAnalyzer and LockGraphAnalyzer more coherent and eliminate the duplication of stack-walking for lock-state inference that currently exists across these analyzers.

4. **Highest engineering return**: P0-1 → P0-2 → P1-1 (sequential, each builds on the previous). Delivering these three items would bring the analyzer to production-reliable deadlock detection parity with WinDbg SOS `!dlk` at the ClrMD level.
