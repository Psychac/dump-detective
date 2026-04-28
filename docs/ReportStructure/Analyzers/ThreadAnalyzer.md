# ThreadAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Medium

## Report Sections Served
- §7.1 Thread Lifecycle (counts, finalizer, async chains, thread pool state)
- §7.2 Synchronization Patterns (wait categories, frame hotspots, GcMode)
- §19.2 Compiled Method Analysis (partial — frame hotspots, no native code size)
- §21.3 Finalizer Thread Health ✅ (fully covered)

---

## Currently Produces
- `ThreadDomainResult`: total/alive/background/GC/threadpool/finalizer thread counts
- Wait category distribution, state distribution, AppDomain distribution
- Threads with locks, blocked threads, threads with exceptions
- Frame hotspots (`TopFrameHotspots`), async chain thread count + max depth
- Finalizer thread: `FinalizerIsBlocked`, `FinalizerFrames`, `FinalizerLockCount`,
  `FinalizerManagedThreadId`, `FinalizerOsThreadId` — **fully covers §21.3**

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Long-lived thread classification | §7.1 | Low — single snapshot limitation |
| Thread memory retained (stack-rooted objects per thread) | §7.1 | High |
| Per-thread wait duration (not available from ClrMD static dump) | §7.2 | N/A — not possible |
| `ClrStackFrame.Kind` distribution (Managed vs Runtime vs Unmanaged) per thread | §19.2 | Low — data present in frame list; not aggregated |

---

## Required Changes

1. **Add `ThreadsWithLargeStackRoots`** — `IReadOnlyList<ThreadRootSnapshot>` — threads with
   many stack-rooted objects or high stack-root byte estimate. Use
   `ClrThread.EnumerateStackRoots()` for top N threads (bounded by count threshold).
   New record: `ThreadRootSnapshot(uint ThreadId, int StackRootCount, ulong EstimatedBytes)`.
2. **Add `LongLivedThreadCount`** heuristic — threads with `IsBackground = false` and
   `LockCount == 0` and no wait pattern detected — classified as "always-alive worker".
   This is a naming/classification addition to existing data; no new scan needed.
3. **`AsyncChainThreadCount`** and **`MaxAsyncChainDepth`** are already produced — ensure
   they are prominently exposed in the §8 report path (currently in ThreadAnalyzer, will
   also appear in `AsyncTaskAnalyzer` post-split).

---

## Phase Assignment

`ThreadAnalyzer` is **entirely Phase 2**. Thread state requires live `runtime.Threads`
enumeration which is not capturable during Phase 1 heap streaming.

`ThreadsWithLargeStackRoots` addition:
- `ClrThread.EnumerateStackRoots()` called for top N threads only (bounded by `MaxThreadsForStackRootScan = 20`)
- Each root: look up type size from `TypeAggregates` dict (O(1) — no heap call)
- Total cost: 20 threads × avg 50 roots × O(1) lookup = negligible

---

## Related Analyzers
- **`LockGraphAnalyzer`** — deadlock detection from `ClrThread.BlockingObjects`; complementary to wait pattern analysis
- **`ThreadStackClusterAnalyzer`** — groups threads by stack signature; feeds §7.2 contention hotspot grouping
- **`HangAnalyzer`** — blocking/hang analysis; post-split, task data moves to `AsyncTaskAnalyzer`
- **`JitAnalyzer`** (new) — `ClrStackFrame.Kind` unmanaged frame ratio extends what `ThreadAnalyzer` produces for §19.2
