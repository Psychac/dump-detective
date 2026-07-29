# P1 Item 2: Parallel Heap-Index-Scan Dispatcher — Design Sketch

**Status:** Step 2 (shape B prototype, scoped to `AsyncTaskAnalyzer`) implemented — step 1 (real-dump sequential-vs-parallel measurement) was explicitly skipped; steps 3+ (extend to `CrashAnalyzer`/`HangAnalyzer`/`CollectionAnalyzer`, delete their fallbacks) not started.
**Date:** 2026-07-28 (updated 2026-07-29)
**Relates to:** [phase0-deliverable-10-platform-roadmap.md § Near-term (P1)](phase0-deliverable-10-platform-roadmap.md#near-term-p1), item 2's architect-review finding "Each migrated analyzer now carries three parallel implementations of the same logic"

---

## Why

`HeapIndexScanDispatcher.Run` (`Pipeline/HeapIndexScanDispatcher.cs`) drives all 9 `IHeapIndexScanParticipant` analyzers over the on-disk heap index with a single-threaded `foreach (HeapEntry entry in cache.EnumerateIndexedEntries())` loop. Three of those participants (`CrashAnalyzer`, `HangAnalyzer`, `CollectionAnalyzer`) also carry a second, independent implementation of their own detection logic — a `Parallel.ForEach(heap.Segments, ...)` scan with thread-safe (`ConcurrentDictionary`/`Interlocked`) accumulators — used only when no disk index exists. This sketch asks: could the dispatcher itself become parallel, so the index-backed path gets the same multi-core throughput the fallback already has, without duplicating detection logic a third time?

This is a design sketch to inform a decision, not a scoped implementation plan. It should not be started before the P1 item 2 sequential-vs-parallel measurement (`HeapIndexScanDispatcherPerfTests.cs`) has actually been run against a representative 10GB+ dump — if the current sequential single pass already beats N independent parallel scans, there's less reason to take this on at all.

---

## What the storage layer already supports

The on-disk index (`ObjectIndexReader.cs`) is a fixed-width column store: four parallel memory-mapped arrays (`Address`/`MethodTable`/`Size` at 8 bytes each, `Generation` at 1 byte), one record per heap object. Record *i*'s fields live at a fixed, computable offset (`i * ColumnSize`) in every column. This makes the index trivially splittable into contiguous record ranges for concurrent reads — it's read-only mmap, so N threads reading disjoint `[start, end)` ranges have no contention and share the OS page cache.

Generation is already a first-class persisted column (`ObjectGenerations`, resolved once per object during the Phase 1 build — `DiskBackedObjectIndexWriter.cs:202`), so no participant needs segment identity to know whether an object is Gen2/LOH.

Segment **boundaries**, however, are not persisted. The Phase 1 build already scans segment-parallel (`Parallel.For` over `heap.Segments`, `DiskBackedObjectIndexWriter.cs:106`) and writes one scratch file per segment, but `ConcatenateScratchFiles` (`DiskBackedObjectIndexWriter.cs:374`) flattens them into one segment-ordered but boundary-erased stream — only the final cumulative `objectCount` survives into the container. Reconstructing segment alignment at read time would need a new persisted offset table, and isn't worth it: segment sizes vary too widely (a single LOH segment can dwarf a dozen small ephemeral segments) for segment-aligned chunks to load-balance as well as plain record-count chunking of the already-sorted column data.

**Conclusion: partition by record-count range, not by segment. No format change is required.**

---

## What has to change: the participant contract

Two assumptions baked into `IHeapIndexScanParticipant` today block parallelizing the dispatcher:

1. **Single-threaded accumulation.** Every participant's `OnHeapEntry` mutates plain `Dictionary`/`List`/`int` instance fields, safe only because the dispatcher calls them from one thread today.
2. **"Called once per index record, in address order"** (`IHeapIndexScanParticipant.cs:20`). In practice this is already softer than it sounds — the existing `Parallel.ForEach` fallbacks (e.g. `CrashAnalyzer.RunParallelExceptionScan`) collect into a `ConcurrentBag` out of order and explicitly re-sort by address afterward before applying any capped/first-N-per-type logic, specifically to match the disk-backed scan's determinism. Participants already tolerate a **collect, then merge, then sort, then trim** shape; they don't require true per-record streaming order.

### Two candidate shapes

**A. Shared thread-safe collections.** Every participant swaps its `Dictionary`/`List` fields for `ConcurrentDictionary`/`Interlocked`/`ConcurrentBag`, same as today's `Parallel.ForEach` fallbacks, and the dispatcher just calls `OnHeapEntry` concurrently from K worker threads over the same shared state.

- Minimal interface change — `IHeapIndexScanParticipant` stays as-is.
- Adds lock/interlocked overhead to a loop that's branch-free and lock-free today specifically because it's single-threaded (see `CrashAnalyzer.cs:19-21`'s own comment on why the participant path uses plain fields).
- Doesn't actually remove any duplication: the fallback's thread-safe accumulator code and the (now also thread-safe) participant code stay as two separate implementations, just both slower per-object than today's single-threaded participant path.

**B. Partition + merge (recommended direction).** The dispatcher hands each of K worker threads a disjoint contiguous record range (`recordCount / K` each, using the same batch-read path `ObjectIndexReader.ZeroCopyColumnReader.FillBatch` already provides) and its own **private** accumulator instance. Each worker runs today's single-threaded `OnHeapEntry` logic, untouched, over its own slice — no locking, no shared mutable state, same per-object cost as today. After all K finish, a new interface method combines the K partial results per participant:

```csharp
internal interface IHeapIndexScanParticipant
{
    void BeforeHeapIndexScan(AnalysisContext context);   // unchanged
    void OnHeapEntry(in HeapEntry entry);                 // unchanged - called per-worker, not shared
    void OnHeapIndexScanCompleted(bool succeeded) { }      // unchanged

    // New: called once after all workers finish, with one "self" per worker plus the
    // primary instance. Participant merges partial accumulator state into itself.
    // Default no-op preserves today's single-threaded (K=1) behavior with zero merge cost.
    void MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) { }
}
```

Practical mechanics:

- The dispatcher constructs K participant *instances* per registered analyzer type (or asks a factory for K clones), calls `BeforeHeapIndexScan` on each, runs them each over a disjoint record range on its own thread, then calls `MergePartial` once on the instance that will go on to serve `AnalyzeAsync`, passing it the other K minus 1 as partials.
- `AsyncTaskAnalyzer`'s `MaxTasksToScan` cap is the one place this needs explicit care: each worker must not stop early at the global cap (it doesn't know the global count), so workers either scan uncapped over their own range or use a generously inflated per-worker cap; `MergePartial` re-sorts the union by address and trims to the true global cap — same "collect then trim" shape `CrashAnalyzer`'s existing fallback already uses.
- `CrashAnalyzer`/`HangAnalyzer`/`CollectionAnalyzer` merge is mostly `Dictionary`-combine-by-key (sum counts, concatenate capped lists then re-cap) — mechanical, but new code per participant, not automatic.
- Failure isolation (finding 1, already fixed) extends naturally: a worker throwing mid-range fails only that worker's partial contribution for that participant, same `bool[] failed` tracking, now per-`(participant, worker)` pair instead of per-participant.

### Why B over A

A keeps two implementations (thread-safe fallback, now-also-thread-safe participant) and taxes the hot path with synchronization it doesn't need. B keeps the participant's per-object logic exactly as fast as it is today, at the cost of writing merge logic once per participant — a bounded, one-time cost — and genuinely converges the dispatcher path toward the fallback's throughput characteristics rather than degrading the dispatcher path toward the fallback's overhead.

---

## What this does not solve

Even with B fully built, the no-index `Parallel.ForEach(heap.Segments, ...)` fallback in `CrashAnalyzer`/`HangAnalyzer`/`CollectionAnalyzer` still has to exist as written — there's no on-disk column store to partition when there's no index, only raw `ClrObject` enumeration over live segments. A parallel dispatcher makes the index-backed path competitive with that fallback (removing the performance motivation to fall back when an index does exist), but the fallback itself remains a separate code path. Fully collapsing the duplication (finding 3's original framing) would additionally require generalizing the partition/merge worker abstraction to accept a raw per-segment `ClrObject` enumeration as an alternate entry source alongside the column-store range — plausible future work, structurally similar to what `HangAnalyzer.RunParallelAsyncScan` already did by unifying its own two parallel variants (in-memory `HeapEntry[]` vs. segment walk) behind one method (`HangAnalyzer.cs:468`), but out of scope for this sketch.

---

## Sequencing

1. ~~Run `HeapIndexScanDispatcherPerfTests.cs` against a real 10GB+ dump (`DD_BENCHMARK_DUMP`)~~ — **skipped by explicit decision**, not because it was run and passed. The roadmap doc's sequential-vs-parallel numbers are still missing; this step remains outstanding if the risk of that gap needs to be revisited.
2. **Done (2026-07-29).** Prototyped shape B against `AsyncTaskAnalyzer` only, per this doc's recommendation. One deviation from the sketch above: instead of a default no-op `MergePartial` added directly to the base `IHeapIndexScanParticipant` (§ "What has to change: the participant contract"), partitioning eligibility is gated by a separate opt-in marker interface, `IParallelHeapIndexScanParticipant : IHeapIndexScanParticipant` (`Pipeline/IParallelHeapIndexScanParticipant.cs`), adding `CreateWorkerInstance()` and `MergePartial(...)`. Reasoning: a default no-op on the base interface is an unsafe gate — a participant that never overrides it would silently get partitioned (if the dispatcher ever partitioned unconditionally) with no compile/runtime signal that its results are now incomplete. The marker interface makes opt-in explicit and keeps the other 8 participants' behavior and tests byte-for-byte unchanged. Landed: `IParallelHeapIndexScanParticipant.cs`, ranged reads (`ObjectIndexReader.ReadEntriesRange`, `HeapIndexCache`/`HeapAnalysisCache.EnumerateIndexedEntriesRange`), `HeapIndexScanDispatcher`'s split sequential/parallel run with `Parallel.For` over K disjoint ranges + merge, and `AsyncTaskAnalyzer.CreateWorkerInstance`/`MergePartial`. Tests added in `HeapIndexScanDispatcherTests.cs` (`FakeParallelParticipant`) and `AsyncTaskAnalyzerHeapIndexScanTests.cs`; full fast suite green.
3. **Done (2026-07-29).** Extended to `HangAnalyzer` and `CollectionAnalyzer`, completing all five `IParallelHeapIndexScanParticipant` implementations (`AsyncTaskAnalyzer`, `CrashAnalyzer`, `DominatorAnalyzer`, `HangAnalyzer`, `CollectionAnalyzer`). Each adds `CreateWorkerInstance()` and `MergePartial()` following the same partition-and-merge shape; tests added in `HangAnalyzerHeapIndexScanTests.cs` and `CollectionAnalyzerHeapIndexScanTests.cs`. The no-index `Parallel.ForEach(heap.Segments, ...)` fallbacks in `HangAnalyzer` and `CollectionAnalyzer` remain in place (see § "What this does not solve").
4. **Done (2026-07-29).** Remaining four participants were evaluated individually: `WcfChannelAnalyzer` (state-count sum + `InstanceStateSampler.MergeFrom`) and `StringAnalyzer` (fingerprint-count sum, bucket sum, very-long-string concat) stayed parallel-capable; `EventLeakAnalyzer` was intentionally reverted to sequential-only because its worker-local state and post-scan work made the parallel path a memory/runtime loss; `DbConnectionAnalyzer` is parallel-capable with the same shape as `WcfChannelAnalyzer`. Tests were added for the parallel-capable ones.
5. **Measured (2026-07-29).** `DispatcherPass_ParallelVsSequential_AllParticipants` on the reference 10GB+ IIS crash dump (8-core machine, index pre-warmed, EventLeak excluded because it is sequential): **53.3s → 14.5s** (3.7× speedup) for the remaining parallel participants. `ProcessorCount: 8`, `MinRecordsPerWorker = 250 000`. The `HeapIndexScanDispatcher.Run` overload accepting `maxWorkers` was added to enable this and future perf-comparison tests without git stash.
