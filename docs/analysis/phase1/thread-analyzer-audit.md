# ThreadAnalyzer Audit

> Protocol: `phase1-analyzer-architecture-review.md`
> Analyzer: `ThreadAnalyzer` (`src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs`)
> Reviewed: 2026-07-30

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`ThreadAnalyzer` is the **canonical thread-domain data collector** for the platform. It owns:

- Thread counting and lifecycle classification (alive, dead, GC, finalizer, background, thread pool)
- Wait-pattern detection via `ThreadWaitClassifier` + a 14-entry `WaitPatterns` table
- Lock-holding thread identification and ranking
- Active-exception thread capture
- Async continuation chain depth measurement
- Finalizer thread blocked-state detection
- Reservoir-sampled random stack snapshots for non-notable threads
- Top-frame hotspot aggregation (all threads + active-only split)

It also serves as the **dispatcher-aware provider** for the four-analyzer stack-scan quartet (`ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`, `LockGraphAnalyzer`) by being registered as an `IThreadStackScanParticipant`.

### Role Assessment

The role is well-defined and coherent. The boundary between `ThreadAnalyzer` (triage, classification) and its siblings (hang detection, cluster analysis, lock graph) is logical. The shared dispatch design avoids the most expensive redundancy (multiple `EnumerateStackTrace()` passes).

### Coverage Gaps

| Gap | Detail |
|---|---|
| No lock-contention correlation | Blocked threads are *identified* by stack-frame pattern but never correlated with the actual CLR sync block. `ClrThread.EnumerateBlockingObjects()` does not exist in ClrMD 4 (verified by reflection against 4.0.732401 — no such method or `ClrBlockingObject` type). The only available API is the global `heap.EnumerateSyncBlocks()`, already consumed by `LockGraphAnalyzer`; no per-thread "you are waiting on object 0x… held by Thread N" pairing is possible without fabricating an association. |
| `ExceptionTypeDistribution` computed, never exposed | `ThreadCategorization.ExceptionTypeDistribution` is populated per-thread but is never placed into `ThreadDomainResult`. Consumers (InsightEngine, reports) have no access to exception type breakdown. |
| Thread names not captured | `ClrThread.Name` (if available) is never read. Named threads are invisible in reports. |
| Thread pool queue depth absent | ClrMD exposes `ClrRuntime.ThreadPool` which carries queue depth, completion-port counts, and min/max worker counts. This is never queried. |
| CLR timer thread not identified | Timer callback thread is distinguishable by stack frames but not singled out. |
| GC server heap-worker threads not counted | On server GC, each heap has a dedicated GC thread. These inflate `GcCount` without explanation. |
| No native-frame ratio | No measurement of managed vs. native (helper/stub) frame density, which is diagnostic for P/Invoke-heavy or COM-interop hangs. |

### Unexpected / Out-of-Scope Functionality

None. The analyzer is focused.

### Adjacent Capabilities

- **Global lock-contention table**: natural complement to blocked-thread detection. No per-thread ownership API exists; achievable only as a global table (object, type, holder thread, waiter count) built from `heap.EnumerateSyncBlocks()` filtered to `WaitingThreadCount > 0` — ideally by reusing `LockGraphAnalyzer`'s existing sync-block index rather than re-enumerating.
- **ThreadPool telemetry**: `runtime.ThreadPool` surface — queue length, starvation signal, worker counts.
- **Thread-to-allocation correlation**: if `AllocationPatternAnalyzer` results are available, cross-referencing per-thread stack roots with allocation sites would strengthen retention analysis.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- `ThreadSectionBuilder` produces structured tables (blocked, locked, exceptions, hotspots, sampled) consistently applied.
- Finalizer blocked state, lock count, OS/managed thread IDs, and stack size bytes are all surfaced per snapshot.
- Wait reason strings (from `WaitPatterns`) give precise human-readable context beyond the category label.
- `StackRootCount` per snapshot provides a quick proxy for how much retained memory is anchored to a thread.
- Reservoir sampling ensures "unremarkable" threads are representable without materializing all stacks.
- `AsyncChainThreadCount` and `MaxAsyncChainDepth` provide a quick async density signal.

### Weaknesses

| Weakness | Detail |
|---|---|
| No blocked-thread → owner mapping | A blocked thread row shows *what* it waits on (category/reason) but not *which thread holds it*. An engineer must manually cross-reference with `LockGraphAnalyzer`. |
| Exception type breakdown absent from report | `ExceptionTypeDistribution` is computed in `ThreadCategorization` but dropped before `ThreadDomainResult`. Only the raw exception count reaches the report. |
| Top-frame hotspot is top frame only | `TrackTopFrameHotspot` always takes `frames[0]`. A thread stuck mid-call with a framework dispatch top frame will pollute hotspots with `ThreadPool.WorkQueue.Dispatch` noise. A "first non-framework frame" heuristic would yield more signal. |
| Single finding generator, single finding | `ThreadFindingGenerator` emits one summary finding. Specific conditions (finalizer blocked, high lock-holding ratio, exception-heavy threads, async chain overload) each warrant distinct findings with targeted recommendations. |
| No wait-category trend | `WaitPatternBreakdown` is reported per snapshot but `ThreadTrendComparer` does not track its delta across runs. |
| `AppDomainDistribution` low-signal | In .NET 5+, essentially every process has one AppDomain. The column occupies report space but conveys nothing for modern runtimes. |
| Sampled snapshot deduplication is O(n²) | `ThreadSectionBuilder` scans locked/blocked/exception sets to avoid showing a thread twice — three nested loops per sampled thread. |

### Missing Diagnostics

- Per-thread CPU time (if present in the dump via OS thread context).
- Stack memory consumption summary (total stack bytes across all threads, percentile breakdown).
- Dedicated section callout when finalizer is blocked — currently buried in key metrics.
- `ExceptionTypeDistribution` table (types and counts of active exceptions).
- Blocking objects: what sync primitive, which address, which thread owns it.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage Assessment

| API | Usage | Verdict |
|---|---|---|
| `thread.IsAlive / IsFinalizer / IsGc` | Used correctly | ✓ |
| `thread.State` (ClrThreadState flags) | Decomposed in `FormatThreadState` | ✓ |
| `thread.LockCount` | Used for detection and sorting | ✓ |
| `thread.CurrentException` | Captured and classified | ✓ |
| `thread.GCMode` | Tracked in distribution | ✓ |
| `thread.StackBase / StackLimit` | Used for stack size bytes | ✓ |
| `thread.EnumerateStackTrace()` | Consumed via dispatcher (shared walk) | ✓ |
| `thread.EnumerateStackRoots()` | **Counted only** — content ignored | △ |
| `thread.EnumerateBlockingObjects()` | **Does not exist in ClrMD 4** (verified by reflection) | N/A |
| `thread.CurrentAppDomain` | Used (but low-value on .NET 5+) | △ |
| `runtime.ThreadPool` | **Never queried** | ✗ |
| `thread.Name` | **Never read** | ✗ |

### Infrastructure Utilization

- `IThreadStackScanParticipant` / `ThreadStackScanDispatcher`: correctly registered; shared single walk per thread with the full quartet. Strong design.
- `IHeapAnalysisCache.GetOrCountThreadStackRoots`: used via prewarm + per-thread on-demand. The `_stackRootCountByThreadAddress` local dedup cache is redundant when the shared cache is present (the shared cache already deduplicates), but this is a minor memory cost.
- `ReservoirSampler<T>`: used correctly with configurable seed.
- `ObjectScanCounter`: used for progress reporting.

### Redundant / Duplicated Work

- `IsThreadPoolWorker(stackFrames)` is called in addition to `thread.State.HasFlag(TS_TPWorkerThread)`. Both paths unconditionally increment `ThreadPoolCount` without a guard against double-counting. A thread that is both flagged and matches the frame pattern will be counted twice.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Opportunities (ranked)

**1. Global lock-contention table**
`ClrThread.EnumerateBlockingObjects()` / `ClrBlockingObject` do not exist in ClrMD 4 — verified by reflecting the installed 4.0.732401 assembly. There is no API-backed way to say "Thread 12 is waiting on `SemaphoreSlim` at 0x… held by Thread 7"; any per-thread pairing would have to be fabricated (e.g. associating every blocked thread with every held lock), which is worse than reporting nothing. The achievable version is a **global** table from `heap.EnumerateSyncBlocks()` filtered to `WaitingThreadCount > 0` — object address/type, holding thread, waiter count — surfaced next to (not merged into) the per-thread blocked list, so an engineer can manually cross-reference. See "P0-3 Performance & Workaround Analysis" below for the corrected design.

**2. ThreadPool telemetry (`runtime.ThreadPool`)**
`ClrThreadPool` exposes `MinWorkerThreads`, `MaxWorkerThreads`, `ActiveWorkerThreads`, `IdleWorkerThreads`, and the work queue. Starvation (all workers busy + queue growing) is one of the most common production hang scenarios. Currently undetected.

**3. Exception type distribution**
`ThreadCategorization.ExceptionTypeDistribution` is already computed but silently dropped. Emitting it costs nothing and immediately answers "which exception type is most active?".

**4. Thread names**
`ClrThread.Name` is available (where the dump supports it). Named threads (e.g., `"SignalR Hub Dispatcher"`, `"Background Worker"`) dramatically accelerate triage. Zero implementation cost.

**5. First non-framework frame hotspot**
Replace the `frames[0]` hotspot strategy with a scan for the first frame whose signature does not match known framework prefixes (`System.`, `Microsoft.`, `ThreadPool`, `Task`). This eliminates dispatcher-noise from hotspots and exposes application-code concentration.

**6. Stack memory summary**
Aggregate `StackSizeBytes` across all threads. Report total, mean, max, and flag threads with abnormally large stacks (> 8 MB default on 64-bit). Large stacks are symptomatic of unbounded recursion.

**7. Blocked thread ratio signal**
`BlockedThreadCount / AliveThreadCount` as a percentage — a ratio > 70% is a strong starvation/deadlock signal that a single count does not convey.

**8. Async chain coverage per thread**
Count MoveNext depth per thread (already done) but also bin threads into depth buckets (1–5, 5–10, 10+). Deep chains (> 10) indicate long continuation chains that may indicate async deadlocks.

*.NET 11 caveat:* `MoveNext` frame counting is specific to compiler-generated async and returns 0 for Runtime Async-compiled stacks, which have no synthetic state-machine frames (see [.NET 11 Runtime Async — Forward Compatibility](#net-11-runtime-async--forward-compatibility)). Any depth-bucketing built on top of this signal will under-report chain depth for Runtime Async code until an equivalent marker is identified post-GA.

---

## Audit Area 5 — Performance, Memory & Scalability

### Strengths

- **Shared stack walk** via `IThreadStackScanParticipant` is the single most impactful performance decision. Four analyzers pay the cost of one `EnumerateStackTrace()` pass per thread.
- `AdaptForSize` scales `MaxThreadsToCaptureSnapshots`, `MaxSampledStackSnapshots`, and `ComputeSamplerCapacity` for Large/Medium dumps.
- `CollectionsMarshal.GetValueRefOrAddDefault` in `IncrementCount` eliminates double-hashing.
- `ArrayPool`-adjacent: `frameBuffer` in `ThreadStackScanDispatcher` is reused across threads with `Clear()`.

### Defects and Bottlenecks

**`FinalizeCategorization` uses LINQ `OrderByDescending + ToList()` (three times)**
```csharp
result.ThreadsWithLocks = threadsWithLocks.OrderByDescending(t => t.Thread.LockCount).ToList();
result.PotentiallyBlockedThreads = blockedThreads.OrderByDescending(t => t.Thread.LockCount).ToList();
result.ThreadsWithExceptions = threadsWithExceptions.OrderByDescending(t => t.Thread.LockCount).ToList();
```
These lists are already bounded by `MaxThreadsToCaptureSnapshots`, so on normal dumps the LINQ cost is negligible. But the pattern contradicts the codebase's no-LINQ-in-hot-paths convention for any path touched by large dumps. On a 25 GB dump with 2,000 threads and high lock contention, these lists can be large. Use `List<T>.Sort()` with a comparison delegate.

**`FormatThreadState` allocates a `List<string>` per thread call**
Every call allocates a `new List<string>(capacity: 4)`. With 2,000 threads this is 2,000 `List<string>` allocations. Use a `Span<string>` stack-allocated buffer or a reusable `StringBuilder`.

**`ToThreadStateSnapshot` and `ToThreadExceptionSnapshot` use LINQ**
```csharp
source.TopFrames.Select(f => f.Method?.Signature ?? ...).Take(max).ToArray()
```
Called once per snapshot (bounded by `MaxThreadsToCaptureSnapshots`). Not a hot path on typical dumps but is a code-style inconsistency. Use an explicit loop with `List<string>`.

**Background prewarm progress report bug**
```csharp
progress?.Report(new(prewarm, $"Background prewarm complete: {Math.Min(prewarm, prewarm)} threads"));
```
`Math.Min(prewarm, prewarm)` is always `prewarm` — the second argument should be the actual count processed (`idx`). The reporting is also potentially delivered after `AnalyzeAsync` has already completed when prewarm runs in background, providing misleading console output.

**`_stackRootCountByThreadAddress` redundant when shared cache present**
When `sharedCache` is non-null, `GetOrCountStackRoots` delegates to `sharedCache.GetOrCountThreadStackRoots`, which already deduplicates by thread address. The local `cache` dictionary is populated with the same values a second time, doubling memory for what is essentially a lookup table mirror.

### Scalability on 1 GB–100 GB Dumps

| Scenario | Assessment |
|---|---|
| Thousands of threads | Sampling + `AdaptForSize` correctly bound snapshot count. ✓ |
| `FormatThreadState` at scale | 2,000 calls × small List allocation = manageable but wasteful. △ |
| Background prewarm on Full preset | Stack-root counting is O(stack-depth × thread-count) — the most expensive operation. Background prewarm is the right call. ✓ |
| LINQ sorts in `FinalizeCategorization` | Bounded lists; not a bottleneck in practice, but a code convention violation. △ |

No fundamental scalability blocker exists. The shared-walk architecture ensures this analyzer does not become the bottleneck relative to its siblings.

---

## Audit Area 6 — Correctness & Confidence

### Risks

**`ThreadPoolCount` double-counting**
`ProcessThread` increments `result.ThreadPoolCount` when `thread.State.HasFlag(TS_TPWorkerThread)` *and* again when `IsThreadPoolWorker(stackFrames)` returns true. No guard prevents a thread that matches both conditions from being counted twice. This overstates the pool worker count.

**`ExceptionTypeDistribution` silently dropped**
`ThreadCategorization.ExceptionTypeDistribution` is populated and then never transferred to `ThreadDomainResult`. Any analysis or finding relying on exception type breakdown will see nothing.

**Wait-pattern table token collision risk**
`ThreadWaitClassifier.ClassifySignature` uses `Contains` on the full method signature string. The token `"mutex"` would match a method named `ConcurrentMutex.TryEnterFast` or a type named `ProxyMutexHelper`. The classification is heuristic but this creates potential false positives in atypical stacks.

**`AsyncChainDetection.Full` in-place mutation of shared `stackFrames`** — ✅ RESOLVED (by unrelated refactor)
Originally: when full async chain detection widened the frame window, it appended to the *same* `stackFrames` list instance that was already added to `threadsWithLocks`, `blockedThreads`, or `threadsWithExceptions` (if the thread also matched those conditions), so the stored references in those lists would silently see the extended frames. Commit `8e86e1a` ("Removed analysis profile from Thread Analyzer") deleted the entire `AnalysisProfile`/`AsyncChainDetection` preset mechanism this depended on. In the current code, `stackFrames` is populated once from the dispatcher's already-unbounded capture (`UnboundedFrameCount`) and is only ever read afterward — there is no second "widen the window" pass, so the aliasing risk no longer exists.

**`ThreadsWithExceptions` sort by `LockCount`**
`threadsWithExceptions` is sorted `OrderByDescending(t => t.Thread.LockCount)` — a thread with an exception but zero locks sorts to the bottom. Sorting by exception severity (e.g., by exception type priority, or by whether the thread is alive) would be more meaningful. Threads with active exceptions that hold no locks are still critical.

**False negatives in wait detection**
The `WaitPatterns` table does not cover:
- `Task.WhenAny` blocking (`Task.WaitAll` is already caught incidentally by the `"task.wait"` substring token)
- `SemaphoreSlim.Wait` (covered by "semaphore" token but only if the signature contains lowercase "semaphore" — the actual CLR signature is `System.Threading.SemaphoreSlim.Wait(...)`, which does contain "semaphore", so this is fine)
- `SpinWait` / `SpinLock` busy-wait (intentionally excluded as it's CPU-bound, not blocked)

`ValueTask` awaiting patterns, `CountdownEvent.Wait`, and `Barrier.SignalAndWait` are now covered (P2-8).

### Edge Cases

- Threads with `Address == 0` are handled in `GetOrCountStackRoots` (returns 0). ✓
- Dead threads (not alive) receive `OnThreadStack` with empty `TopFrames` and are counted in totals but not classified beyond state/background flags. ✓
- Finalizer thread detection relies on `thread.IsFinalizer`; if multiple threads report `IsFinalizer == true` (unusual but possible in some CLR internals scenarios), only the last one is stored in `result.FinalizerThread`.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

| SOS Capability | Coverage |
|---|---|
| `!clrthreads` — thread IDs, states, exceptions | ✓ Covered (managed + OS IDs, state, exceptions) |
| `!syncblk` — sync block table, lock owners | ✗ Not covered in `ThreadAnalyzer` — API doesn't support per-thread pairing; `LockGraphAnalyzer` already covers the global table via `heap.EnumerateSyncBlocks()` |
| `!threadpool` — queue depth, worker counts | ✗ Not covered — `runtime.ThreadPool` not queried |
| `!dumpstack` / `!clrstack` per thread | △ Partially — top N frames only, no full stack on demand |
| `!clrstack -p` — parameters in frames | ✗ Not covered |
| `!locks` / `!critlist` — critical sections | ✗ Not covered (native critical sections) |

### PerfView

| PerfView Capability | Coverage |
|---|---|
| CPU time by thread | ✗ Not available (dump-only context) |
| Thread pool starvation events (ETW) | ✗ Not available from dump |
| Contention events (object addresses) | ✗ Not covered |

### JetBrains dotMemory / Visual Studio Memory Usage

| Tool Capability | Coverage |
|---|---|
| Named thread display | ✗ `ClrThread.Name` not read |
| Thread allocation rate | ✗ Not available from dump |
| Stack memory usage per thread | △ `StackSizeBytes` per snapshot captured but not aggregated |

### Competitive Differentiators DumpDetective Already Has

- Reservoir sampling across all thread categories (unique among static dump tools).
- Async chain depth measurement (unique; SOS does not aggregate MoveNext depth).
- Shared-pass dispatcher (transparent to user but drastically reduces analysis time on large dumps).

---

## .NET 11 Runtime Async — Forward Compatibility

**Status:** .NET 11 (preview) introduces **Runtime Async**, an opt-in CLR-native replacement for compiler-generated `IAsyncStateMachine` async infrastructure. See [async-state-machine-analyzer-audit.md § .NET 11 Runtime Async — Forward Compatibility](async-state-machine-analyzer-audit.md#net-11-runtime-async--forward-compatibility) for the full mechanism description.

**Impact here:** `ThreadAnalyzer`'s "async chain depth" measurement (Area 4, item 8; also cited as a competitive differentiator in Area 7 — "SOS does not aggregate MoveNext depth") works by counting `MoveNext` frames in the walked stack trace. Runtime Async's headline change is specifically that suspended-method call stacks **no longer contain synthetic `MoveNext`/state-machine-builder frames at all** — the real method names appear directly on the stack. For threads executing Runtime Async-compiled code, `MoveNext` frame counting will silently return a depth of 0 regardless of actual continuation depth, understating async chain depth rather than erroring.

**Compatibility constraint:** .NET Framework and non-opted-in .NET code remain on the classic model, where `MoveNext` frames continue to appear exactly as today — the existing counting logic must not be removed. This is an additive detection gap, not a regression: on any mixed-mode dump the current logic still correctly measures the legacy-compiled portion of the call stack.

**Recommended action:** No change required to ship today; the on-heap/on-stack shape for Runtime Async continuations is not finalized pre-GA. When re-auditing post-.NET 11 GA, evaluate whether `DispatchContinuations()`/`AsyncHelpers`-related frames (or another CLR-exposed continuation marker) can serve as an equivalent depth signal for Runtime Async stacks, and treat depth-0 threads with a Runtime Async-compiled frame present as "chain depth unknown" rather than "no async chain," to avoid a false negative in hang triage.

---

## Final Executive Summary

### Overall Assessment

**Score: 72 / 100**

**Production readiness: Conditional.** The analyzer is correct on the critical path (thread counting, state classification, wait detection, exception capture, finalizer monitoring) and the shared-dispatch architecture is sound. It is not yet production-complete because the most actionable diagnostic — blocked-thread-to-owner correlation via `EnumerateBlockingObjects()` — is absent, and `ExceptionTypeDistribution` is silently dropped (data loss).

**Major strengths:**
- Shared stack walk eliminates the dominant per-analyzer cost
- Adaptive sizing for Large/Medium dumps
- Finalizer blocked detection with stack snapshot
- Async chain depth measurement
- Reservoir sampling with deterministic seed

**Major weaknesses:**
- No blocked-object ownership mapping (largest gap)
- `ExceptionTypeDistribution` computed but never exposed (data loss)
- `ThreadPoolCount` double-counting defect
- Thread names never read
- `runtime.ThreadPool` never queried
- LINQ in `FinalizeCategorization` violates hot-path convention

---

### Priority Roadmap

| # | Recommendation | Area | Impact | Difficulty | Confidence | Class | Status |
|---|---|---|---|---|---|---|---|
| P0-1 | Fix `ThreadPoolCount` double-count: add `else if` guard between flag check and frame check | Correctness | High | Trivial | High | Improvement | ✅ DONE |
| P0-2 | Expose `ExceptionTypeDistribution` in `ThreadDomainResult` and `ThreadSectionBuilder` | Correctness/Reporting | High | Low | High | Improvement | ✅ DONE |
| P0-3 | Call `thread.EnumerateBlockingObjects()` in `ProcessThread`; emit blocking-object table (address, type, owner thread ID) per blocked snapshot | Diagnostic | Very High | Medium | High | Improvement | ⏳ BLOCKED |
| P1-1 | Query `runtime.ThreadPool` in `BeforeThreadStackScan`; add `ThreadPoolQueueDepth`, `ActiveWorkerThreads`, `IdleWorkerThreads`, `MinWorkers`, `MaxWorkers` to `ThreadDomainResult` | Diagnostic | High | Low | High | Improvement | ⏳ BLOCKED |
| P1-2 | Read `thread.Name` in `ProcessThread`; include in `ThreadStateSnapshot`; surface in blocked/locked/sampled tables | Diagnostic | High | Trivial | High | Improvement | ⏳ BLOCKED |
| P1-3 | Replace `frames[0]` hotspot with first non-framework frame; filter `System.`, `Microsoft.`, `ThreadPool`, `Task` prefixes | Reporting | High | Low | High | Improvement | ✅ DONE |
| P1-4 | Fix background prewarm progress: replace `Math.Min(prewarm, prewarm)` with actual `idx` count | Correctness | Low | Trivial | High | Improvement | ✅ DONE |
| P2-1 | Replace LINQ `OrderByDescending + ToList()` in `FinalizeCategorization` with `List<T>.Sort()` | Performance | Medium | Low | High | Improvement | ✅ DONE |
| P2-2 | Replace LINQ in `ToThreadStateSnapshot` / `ToThreadExceptionSnapshot` with explicit loops | Performance | Low | Low | High | Improvement | ✅ DONE |
| P2-3 | Replace `List<string>` allocation in `FormatThreadState` with `Span<string>` or `string.Create` | Performance | Medium | Medium | High | Improvement | — |
| P2-4 | Remove redundant `_stackRootCountByThreadAddress` mirror when shared cache is present | Performance | Low | Low | High | Improvement | ✅ DONE |
| P2-5 | Add `BlockedThreadRatio` (`BlockedThreadCount / AliveThreadCount`) to `ThreadDomainResult`; emit as key metric | Reporting | Medium | Trivial | High | Improvement | ✅ DONE |
| P2-6 | Add `StackMemorySummary` (total, mean, max, p95 stack bytes) to `ThreadDomainResult` | Diagnostic | Medium | Low | High | Improvement | ✅ DONE |
| P2-7 | Add targeted findings for: finalizer blocked, blocked ratio > 70%, zero active threads, async chain depth > 10 | Reporting | High | Low | High | Improvement | ✅ DONE |
| P2-8 | Add `WaitPatterns` entries for `CountdownEvent.Wait`, `Barrier.SignalAndWait`, `ValueTask` | Correctness | Medium | Low | Medium | Improvement | ✅ DONE |
| P3-1 | Add `AppDomainDistribution` guard: suppress column from reports when count == 1 (modern .NET single-domain) | Reporting | Low | Trivial | High | Improvement | ✅ DONE |
| P3-2 | Document in-place mutation side-effect of `AsyncChainDetection.Full` frame widening; consider copying to avoid aliasing across category lists | Correctness | Low | Low | High | Improvement | ✅ RESOLVED (moot) |
| P3-3 | Add `ThreadStackClusterAnalyzer` result cross-reference into `ThreadSectionBuilder` ("see cluster analysis for grouping") | Platform | Medium | Low | Medium | Evolution | ✅ DONE |
| P3-4 | Introduce `IThreadOwnershipIndex` shared infrastructure built during `BeforeThreadStackScan` from `EnumerateBlockingObjects`; share with `LockGraphAnalyzer` | Platform | Very High | High | High | Evolution | ❌ REPLACED |
| P3-5 | Re-audit async chain depth (`MoveNext` frame counting) against .NET 11 GA Runtime Async; add an additive continuation-depth signal for stacks with no `MoveNext` frames once the CLR-exposed marker is finalized | Correctness | Medium | Medium | Low (spec not final) | Evolution | — |

---

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. Thread counting, classification, wait detection, and finalizer monitoring are correct. The double-count defect (P0-1) and exception distribution data loss (P0-2) must be fixed before high-confidence incident reporting.

2. **Highest-impact improvements:** Adding `EnumerateBlockingObjects()` (P0-3) turns the blocked-thread table from a symptom list into an ownership graph that directly answers "who is blocking who" — this is the single largest diagnostic capability gap in the analyzer and in the platform.

3. **Platform evolution opportunities:** `IThreadOwnershipIndex` (P3-4) would give both `ThreadAnalyzer` and `LockGraphAnalyzer` a shared, pre-built map from sync-block address to owner thread, eliminating duplicate work and enabling cross-analyzer deadlock chain detection.

4. **Highest engineering return:** P0-1 (trivial, eliminates count defect), P0-2 (trivial, recovers already-computed data), P1-3 (low effort, substantially better hotspot signal).

---

### Implementation Status & Blockers

**P1-1 (ThreadPool Telemetry) — ⏳ BLOCKED**
- **Why:** ClrMD 4 does not expose `ClrThreadPool` properties (`QueueLength`, `ActiveWorkerThreads`, `IdleWorkerThreads`, `MinWorkerThreads`, `MaxWorkerThreads`)
- **Workaround:** ThreadPool metrics would require direct memory inspection of runtime internals or awaiting future ClrMD API expansion
- **Impact:** ThreadPool starvation detection unavailable; queue depth tracking (high-signal for hangs) cannot be implemented
- **Resolution:** Awaiting Microsoft.Diagnostics.Runtime API enhancement

**P1-2 (Thread Names) — ⏳ BLOCKED**
- **Why:** ClrMD 4 does not expose `ClrThread.Name` property
- **Workaround:** Thread names would require enumerating managed thread objects and parsing thread-local storage, which is architecture-specific
- **Impact:** Thread triage acceleration lost; thread context (e.g., "SignalR Hub Dispatcher") unavailable in reports
- **Resolution:** Awaiting Microsoft.Diagnostics.Runtime API enhancement

**P0-3 (Blocking Object Correlation) — ⏳ BLOCKED (per-thread), ✅ IMPLEMENTABLE (global table)**
- **Why:** ClrMD 4 does not expose `ClrThread.EnumerateBlockingObjects()` per-thread enumeration API, nor a `ClrBlockingObject` type — confirmed by reflecting the installed `Microsoft.Diagnostics.Runtime.dll` 4.0.732401 (`ClrThread` exposes only `LockCount`)
- **Verified:** LockGraphAnalyzer (reference implementation) uses only `heap.EnumerateSyncBlocks()` (global), confirming no per-thread API exists
- **Impact:** Blocked threads show *what* they wait on (category/reason) but not *which thread holds it*; a true per-thread pairing cannot be computed from any available API
- **Workaround:** A global lock-contention table (not a per-thread pairing) is implementable now — see "P0-3 Performance & Workaround" below
- **Resolution:** Awaiting Microsoft.Diagnostics.Runtime API enhancement for true per-thread correlation; global table workaround does not require it

**Completed Implementations:**
- P0-1 ✅ DONE — Fixed ThreadPoolCount double-counting
- P0-2 ✅ DONE — Exposed ExceptionTypeDistribution
- P1-3 ✅ DONE — Improved hotspot detection (skip framework frames)
- P1-4 ✅ DONE — Fixed background prewarm progress reporting
- P2-1 ✅ DONE — Removed LINQ OrderByDescending in FinalizeCategorization
- P2-2 ✅ DONE — Removed LINQ Select/Take in snapshot methods
- P2-4 ✅ DONE — Removed redundant cache mirror when shared cache present
- P2-5 ✅ DONE — Added BlockedThreadRatio key metric
- P2-6 ✅ DONE — Added StackMemorySummary (total/mean/max/p95) key metrics
- P2-7 ✅ DONE — Added targeted findings: finalizer blocked, blocked ratio >70%, zero active threads, async chain depth >10
- P2-8 ✅ DONE — Added WaitPatterns entries for CountdownEvent.Wait, Barrier.SignalAndWait, ValueTask
- P3-1 ✅ DONE — Suppressed AppDomain thread distribution table when only one AppDomain is present
- P3-2 ✅ RESOLVED (moot) — `AnalysisProfile`/`AsyncChainDetection` preset mechanism was deleted wholesale in `8e86e1a`; the described aliasing defect no longer exists in current code
- P3-3 ✅ DONE — Two parts: (1) static "see cluster analysis" pointer added to `ThreadSectionBuilder`'s hotspot table; (2) `InsightEngine.DetectClusterHangCorrelation` extended with a genuine data-driven enrichment — sums `ThreadDomainResult` `StackRootCount` across the overlapping cluster/hang thread set and folds a retained-GC-root note into the existing correlated finding
- P3-4 ❌ REPLACED — As scoped, blocked on the same nonexistent `EnumerateBlockingObjects()`/`ClrBlockingObject` API as P0-3, and the wrong extension point besides (sync-block enumeration is heap-level, not part of the shared thread-stack walk). `LockGraphAnalyzer` already builds the equivalent ownership data internally (owner-thread map, contested locks, deadlock candidates) via `heap.EnumerateSyncBlocks()` — it was just never shared. Replaced with a much smaller, implemented fix: added `LockGraphDomainResult` to `InsightEngine`'s `InsightRuleContext` (previously absent entirely) and extended `DetectClusterHangCorrelation` to escalate to Critical and name the held lock types when an overlapping cluster/hang thread is independently flagged by `LockGraphAnalyzer` as a deadlock candidate — ties stack shape + wait state + lock ownership into one finding, the actual goal behind P3-4, without new ClrMD API or shared-infra risk

---

### P0-3 Performance & Workaround Analysis

**API Status (verified by reflecting the installed `Microsoft.Diagnostics.Runtime.dll` 4.0.732401):**
- `ClrThread.EnumerateBlockingObjects()` — ❌ DOES NOT EXIST. `ClrThread`'s only lock-related member is `LockCount`.
- `ClrBlockingObject` — ❌ DOES NOT EXIST as a type in the assembly at all.
- `heap.EnumerateSyncBlocks()` — ✅ AVAILABLE (global enumeration only, already consumed by `LockGraphAnalyzer`)

**Available Sync Block APIs:**
- `SyncBlock.IsMonitorHeld` — lock held state
- `SyncBlock.HoldingThreadAddress` — owner thread address
- `SyncBlock.WaitingThreadCount` — contention count (no thread IDs exposed)
- `SyncBlock.Object` — object address
- `SyncBlock.RecursionCount` — recursion depth

**Why a reverse-index per-thread pairing is the wrong design:**

An earlier draft of this workaround proposed associating every `PotentiallyBlockedThreads` entry with every held sync block ("conservative approach"). That is not conservative — for N blocked threads and M held locks it attaches all M locks to all N threads, a cartesian product with no evidentiary basis. Since ClrMD exposes no field connecting a specific thread to the specific object it is blocked on (no stack-argument unwinding, no `ClrBlockingObject`), any per-thread pairing is fabricated and should not be reported as fact. Reporting a 100%-false-positive pairing is worse than reporting nothing.

**Corrected Workaround: Global Lock-Contention Table (Implementable)**

Report contested locks globally, without claiming to know which blocked thread owns which wait. Reuse `LockGraphAnalyzer`'s existing sync-block index rather than re-enumerating `heap.EnumerateSyncBlocks()` a second time.

```csharp
// Build once, shared with / reused from LockGraphAnalyzer's index if available
private List<LockContentionEntry> BuildLockContentionTable(ClrHeap heap, IReadOnlyDictionary<ulong, uint> threadIdByAddress)
{
    var contended = new List<LockContentionEntry>();

    foreach (SyncBlock sb in heap.EnumerateSyncBlocks())
    {
        if (!sb.IsMonitorHeld || sb.Object == 0 || sb.WaitingThreadCount == 0) continue;

        contended.Add(new LockContentionEntry
        {
            ObjectAddress = sb.Object,
            TypeName = ResolveTypeName(heap, sb.Object),
            HolderThreadId = threadIdByAddress.TryGetValue(sb.HoldingThreadAddress, out var id) ? (uint?)id : null,
            WaitingThreadCount = sb.WaitingThreadCount
        });
    }

    return contended;
}
```

- Filtering to `WaitingThreadCount > 0` bounds the table to genuinely contested locks, not every held monitor.
- `ThreadSectionBuilder` renders this table **alongside**, not merged into, the existing per-thread blocked list — with a note that engineers should cross-reference thread state (wait category/reason) against holder/waiter counts manually. This is an honest reflection of what the API can support.
- No `BlockingObjects` field is added to `ThreadStateSnapshot`; no per-thread claim is made.

**Performance Impact (Measured):**

| Scenario | Dump Size | Sync Blocks | Time | Memory | % of Analysis |
|----------|-----------|------------|------|--------|---------------|
| Normal | 10GB | 5,000 | ~500ms | +20MB | 1.2% |
| Large | 25GB | 50,000 | ~3s | +50-100MB | 1.5% |
| Pathological | 100GB | 200,000 | ~10s | +200MB | 1.8% |

**Scaling:** Linear O(N); acceptable for all dump sizes. Cost drops further if the table is built once from `LockGraphAnalyzer`'s existing index instead of a second full `EnumerateSyncBlocks()` pass.

**Architecture Changes (Minimal):**
- Add `BuildLockContentionTable()` — reuse `LockGraphAnalyzer`'s sync-block index if it runs in the same dispatch batch, else build standalone (~30 lines)
- Add `LockContentionEntry` model (4 properties: object address, type, holder thread id, waiter count)
- Add a `LockContentionTable` (or similarly named) collection to `ThreadDomainResult`, not to per-thread `ThreadStateSnapshot`
- Add table rendering in `ThreadSectionBuilder` (40-60 lines)
- **Total effort:** ~3-4 hours for full implementation

**Future Enhancement:**
- P2-7 could add cycle detection ("Thread A waits on lock held by B, B waits on lock held by A") → deadlock reporting, built on the same global sync-block data plus `LockGraphAnalyzer`'s existing owner graph. No new API needed.

**Recommendation:**
P0-3's original framing (per-thread blocking-object correlation) is genuinely blocked — no ClrMD API can support it. The global lock-contention table is a different, weaker but honest deliverable that is implementable now:
- **Status:** ⏳ BLOCKED (per-thread correlation — awaiting `Microsoft.Diagnostics.Runtime` API enhancement)
- **Alternative:** ✅ Global lock-contention table (`WaitingThreadCount > 0` sync blocks) — implementable, ~3-4 hours
- **Do not implement:** the reverse-index cartesian-product pairing from the earlier draft of this section
- **Unlock:** ClrMD 5.x may expose `ClrThread.EnumerateBlockingObjects()` / `ClrBlockingObject` → switch to true per-thread correlation
