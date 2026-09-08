# Proposal: Persist Per-Object GC Generation in the Columnar Object Index

**Status:** Steps 1-5 implemented and verified; step 6 (real-dump perf re-run) and the Follow-On dispatcher-level work below are next
**Date:** 2026-07-27
**Trigger:** Perf investigation into `CollectionAnalyzer`/`EventLeakAnalyzer`/`FinalizableObjectAnalyzer` Phase 2 cost

---

## Implementation Status (2026-07-27)

Steps 1-5 of the Proposed Change are complete:

- `HeapEntry` widened with a `Generation` (`sbyte`) field; original 3-arg constructor retained (defaults `Generation = -1`) so all non-indexed construction sites are unaffected.
- `CacheSectionId.ObjectGenerations = 13` added; `CacheFileHeader.CurrentFormatVersion` bumped 2 → 3 (old caches simply fail to parse and are rebuilt, per existing policy).
- `DiskBackedObjectIndexWriter` writes the 4th per-segment scratch-file column (`objGen`) alongside `addr`/`mt`/`size`; `ObjectIndexReader`/`ZeroCopyColumnReader` read it back and populate `HeapEntry.Generation`.
- `CollectionAnalyzer`'s `OnHeapEntry`-based call sites (the ones with a `HeapEntry` in scope) now read `entry.Generation` directly instead of calling `SegmentKindMapper.ResolveGeneration`. **Not** migrated: `FinalizableObjectAnalyzer.cs:78`, `EventLeakAnalyzer.cs:1497,1511,1512`, and `CollectionAnalyzer.cs`'s `RunParallelCollectionAnalysis`/`ProcessEntry` path (`CollectionAnalyzer.cs:536`) — these all walk live `ClrObject`s directly and have no persisted `HeapEntry` to read from, so they remain on the ClrMD fallback as the proposal itself anticipates.
- `docs/binary-format.md` and `docs/architecture.md` updated to document the new section/field and `FormatVersion` 3.
- Verified via `dotnet build DumpDetective.slnx` (0 errors) and `dotnet test` (249/249 unit tests pass, including `ObjectIndexReaderTests`, `AnalysisPipelineTests`, `HeapIndexScanDispatcherTests`).

Not yet done: step 6 (re-running `HeapIndexScanDispatcherPerfTests` against a real dump) requires a local dump file only available to the user running this outside the current environment.

---

## Executive Summary

Phase 2 analyzers (`CollectionAnalyzer`, `FinalizableObjectAnalyzer`, `EventLeakAnalyzer`) resolve an object's GC generation on every matched instance via `SegmentKindMapper.ResolveGeneration(heap, address)`, which calls ClrMD's `heap.GetSegmentByAddress(address)` + `seg.GetGeneration(address)` uncached, once per instance.

Phase 1 (`DiskBackedObjectIndexWriter`) already computes this same value per object during the single-pass heap scan, at effectively zero marginal cost, but only feeds it into transient type-aggregate stats (`TypeIndexBuilder.Add(..., generation)`) — it is never persisted to disk. Phase 2 therefore redundantly recomputes something Phase 1 already knew.

The fix is a small, additive extension to the existing columnar container format: add a 4th per-object column (`Generation`, 1 byte) alongside the existing `Address`/`MethodTable`/`Size` columns, and have Phase 2 read it directly instead of calling back into ClrMD.

---

## Why This Is Cheap

The on-disk object index is **already sorted by segment, then by ascending address within each segment** — this is inherent to how the writer works today, not a change we need to make:

- `DiskBackedObjectIndexWriter.Build` iterates `ClrSegment[] segments` via `Parallel.For(0, segments.Length, ...)`, with each segment writing its own scratch files (`ObjectIndex.bin.seg{i}.addr/mt/size.tmp`).
- `ConcatenateScratchFiles` stitches these together strictly in segment index order after the scan completes.
- Within a segment, `segment.EnumerateObjects()` yields ascending addresses.

Because of this, `objGen` — computed once per object at [DiskBackedObjectIndexWriter.cs:199](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs) — is nearly free to compute:

```csharp
int objGen = isEphemeral ? ResolveObjectGeneration(segment, obj.Address) : segGen;
```

- For non-ephemeral segments (LOH/POH/Frozen, and Regions-GC's `Generation0`/`1`/`2` segment kinds on .NET 8+ Core), `segGen` comes from a static table lookup on `segment.Kind` ([`SegmentKindToGeneration`](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs)) — no ClrMD call at all.
- For ephemeral segments, `ResolveObjectGeneration` calls `segment.GetGeneration(address)` directly — **no** `GetSegmentByAddress` lookup, because the segment is already known from the outer per-segment loop.

Phase 2, by contrast, only has a bare `ulong address` and must rediscover the owning segment from scratch via `heap.GetSegmentByAddress(address)` for every matched instance — work Phase 1 already did once and threw away.

Since the new column follows the same segment-sorted layout as the existing three, it will also compress and batch well (long runs of identical generation values per segment), consistent with the append-only, columnar design already in place.

---

## Alternative Considered and Rejected: Runtime Segment-Cursor, No Schema Change

Before settling on persisting a column, we evaluated an alternative that needs no format change at all: since the object index is already sorted by segment-then-address, Phase 2 could build a tiny in-memory table from `heap.Segments` (address range → `Kind` → fixed-generation-or-Ephemeral-flag) once per analysis, then advance a monotonic cursor through it as entries stream past — turning `heap.GetSegmentByAddress(address)` into an O(1) amortized pointer advance instead of a per-instance lookup.

This was rejected as the primary fix because **it does not generalize across .NET Framework and .NET Core dumps**:

- The "derive generation directly from `segment.Kind`, zero ClrMD calls" fast path only exists for Regions-GC segments (`Generation0`/`1`/`2` kinds), LOH, POH, and Frozen segments — all Core-only concepts (Regions GC is .NET 8+; POH is .NET 5+; Frozen segments are Core-only).
- **.NET Framework has none of these.** It only ever produces `Ephemeral` and `Large` (LOH) segments. For a Framework dump, nearly every non-LOH object sits in an `Ephemeral` segment — exactly the case where the cursor trick cannot shortcut, because gen0/1/2 boundaries move *within* that one segment and still require `segment.GetGeneration(address)` per object.
- So for Framework dumps (and workstation-GC or non-Regions server-GC Core dumps), the cursor idea only ever eliminates the `GetSegmentByAddress` *lookup* — it never eliminates the `GetGeneration` *boundary walk*, which is the more expensive part and the part actually being paid 2-3× redundantly today (once per participant: `CollectionAnalyzer`, `FinalizableObjectAnalyzer`, `EventLeakAnalyzer`).

Persisting the generation column, computed once in Phase 1 (which already pays the `GetGeneration` cost regardless of segment kind or runtime), eliminates that cost from Phase 2 entirely and uniformly — 0 calls instead of up to 3 — for both Framework and Core dumps alike. The cursor's "free for fixed-generation segments" win still applies, but it's a strict subset of what persisting covers, so it isn't worth carrying as the primary design.

**The cursor approach is retained as a secondary, complementary optimization** for any analysis path that operates directly against `ClrHeap` without going through the disk-backed index (i.e. has no persisted `HeapEntry.Generation` to read) — it has no persisted data to fall back on, so shortcutting the segment lookup at runtime is still worthwhile there.

---

## Proposed Change

1. **New columnar section** — `ObjectGenerations`: one `sbyte` per object, written per-segment scratch file alongside `addr`/`mt`/`size`, using the already-computed `objGen`. New `CacheSectionId` entry.
2. **Bump container `FormatVersion`** 2 → 3. Existing policy already handles this: an unrecognized/old version fails to parse and the cache is rebuilt — no migration path needed.
3. **Extend `HeapEntry`** with a `Generation` field (or `sbyte`), read back by `ObjectIndexReader` by zipping in the 4th column alongside `Address`/`MethodTable`/`Size`.
4. **Replace Phase 2 call sites** — `CollectionAnalyzer`, `FinalizableObjectAnalyzer`, `EventLeakAnalyzer` read `entry.Generation` directly instead of calling `SegmentKindMapper.ResolveGeneration(heap, address)`. Removes all runtime `GetSegmentByAddress`/`GetGeneration` calls from these Phase 2 hot paths.
5. **Update `docs/binary-format.md`** — add the new section, bump the documented format version. The doc currently lists "Generation info (Gen0/1/2/LOH/POH)" under Future Extensions; this closes that out.
6. **Verify via `HeapIndexScanDispatcherPerfTests`** — re-run `DispatcherPass_PerParticipantBreakdown_SinglePassEach` before/after to confirm the win in `CollectionAnalyzer` (and any related analyzers) and check the Phase 1 index-build step didn't regress from the extra column write.

---

## Broader Benefit

This establishes a reusable pattern — additive, per-segment-batched columnar columns — for other Future Extensions already called out in `docs/binary-format.md` (e.g. pinned/finalizable flags), without further schema churn beyond an additive `FormatVersion` bump each time.

---

## Open Items Before Implementation

- Confirm whether `HeapEntry` should carry `Generation` directly (widening the hot-path struct) vs. exposing it as a separate parallel array/reader API that only generation-consuming analyzers opt into — needs a look at `docs/architecture.md` §14 and existing `HeapEntry` consumers to avoid growing the struct for analyzers that don't need it.
- Check discrepancy/binary-format tests that assert exact section layout or byte counts, since they'll need updating for the new section and `FormatVersion` value.

---

## Follow-On: Dispatcher-Level Improvements (Implement After This Lands)

**Status: mostly done (2026-07-27).** The persisted column has shipped (see Implementation Status above), and the one genuine dispatcher-level duplication found has been fixed.

**Correction to the audit below:** the "9 call sites" figure for `CollectionAnalyzer.cs` predates the base migration. After migrating the `OnHeapEntry`-based sites to `entry.Generation`, only one `SegmentKindMapper.ResolveGeneration` call remains in `CollectionAnalyzer.cs` (line 536, inside `RunParallelCollectionAnalysis`'s `ProcessEntry`), and it is **not** eligible for the same fix — `ProcessEntry` walks live `ClrObject`s directly and has no `HeapEntry` in scope. Likewise `FinalizableObjectAnalyzer.cs:78` (only reached when no Phase 1 `TypeAggregates` index exists) and `EventLeakAnalyzer.cs`'s `CheckLifetimeMismatch` (subscriber addresses discovered via delegate invocation-list/static-field walks that the dispatcher's `HeapEntry` stream never surfaces) are genuinely ad-hoc paths with no `HeapEntry` in scope — these three call sites correctly remain on the `SegmentKindMapper.ResolveGeneration` fallback.

**What was actually found and fixed:** the original proposal's call-site audit only tracked `SegmentKindMapper.ResolveGeneration`, but `EventLeakFastScanner` (the `OnHeapEntry`-based fast path backing `EventLeakAnalyzer`) has its own private `GetObjectGenerationDirect(ulong address)` helper doing the identical `GetSegmentByAddress`/`GetGeneration` lookup — and `ProcessInstanceFields` was calling it on `entry.Address`, i.e. **on the very `HeapEntry` already in scope**. This is exactly the redundant-lookup pattern the proposal was written to eliminate, just under a different helper name. Fixed: `ProcessInstanceFields` now reads `entry.Generation` directly (`publisherGen = entry.Generation`), removing two live segment lookups per event-bearing publisher instance and the lazy-computation sentinel dance that existed to amortize their cost. `CheckLifetimeMismatchDirect`'s subscriber-address lookups are untouched — those addresses are not the current entry and have no persisted value to read. Also deleted two now-dead-code `IsLifetimeMismatch(ClrHeap, ...)`/overload helpers in `EventLeakAnalyzer.cs` that had no remaining callers. Verified via `dotnet build` (0 errors) and `dotnet test` (249/249 unit tests, including all 32 EventLeak tests).

Once `entry.Generation` is available directly off the disk-backed `HeapEntry`, the shared `HeapIndexScanDispatcher` pass itself becomes a second, independent place to cut cost — separate from the per-analyzer call-site swap in step 4 above. Note this down now so it isn't lost, but treat it as **strictly sequenced after** the persisted-generation column ships and is verified; it has no value on its own without that data being available per entry.

### What changes at the dispatcher level

- Generation resolution was duplicated **per participant** where a participant called into ClrMD for the *current entry's own address* inside its own `OnHeapEntry`/`OnHeapEntry`-derived hot path: `CollectionAnalyzer` (already migrated pre-Follow-On) and `EventLeakFastScanner.ProcessInstanceFields` (migrated as part of this Follow-On — see above) both had this shape.
- With `entry.Generation` populated straight from the disk read, every `IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)` call already carries the answer for free, as part of the single shared enumeration `HeapIndexScanDispatcher` already performs — no participant needs `heap.GetSegmentByAddress`/`GetGeneration` for its *own* entry's generation anymore.
- Lookups for *other* addresses discovered mid-walk (event subscribers reached via delegate invocation lists, static-field sweeps) are a different shape entirely: those addresses are not the current `HeapEntry` and the dispatcher's single pass never surfaces a `HeapEntry` for them at the point they're needed. `SegmentKindMapper.ResolveGeneration` remains the correct tool for those and is not dead code.

### Concrete follow-on work items

1. ~~Audit all `SegmentKindMapper.ResolveGeneration`-shaped call sites once the column is live~~ — **done**. Found and fixed one dispatcher-level duplication the original proposal's audit missed (`EventLeakFastScanner.GetObjectGenerationDirect(entry.Address)` in `ProcessInstanceFields`, migrated to `entry.Generation`), plus removed two now-dead-code `IsLifetimeMismatch` overloads. The remaining 3 call sites (`CollectionAnalyzer.cs:536`; `FinalizableObjectAnalyzer.cs:78`; `EventLeakAnalyzer.cs`'s `CheckLifetimeMismatch`) are genuinely non-indexed/ad-hoc lookups on addresses that are not the current dispatcher entry, and correctly stay on the ClrMD fallback — no further work needed here.
2. Re-run `HeapIndexScanDispatcherPerfTests.DispatcherPass_PerParticipantBreakdown_SinglePassEach` against a real dump to capture the *cumulative* dispatcher-pass improvement from both the column-write and this call-site fix together — still requires a local dump file (see step 6 in Implementation Status), not runnable in this environment.
3. `SegmentKindMapper.ResolveGeneration` should **not** be removed — it is the correct, and only, tool for the 3 remaining genuinely ad-hoc call sites (no `HeapEntry` in scope for those addresses). Keep it as a fallback-only helper, as the base proposal anticipated.
4. Checked for other per-participant duplicated ClrMD lookups beyond generation (e.g. type resolution, field layout lookups) across `CollectionAnalyzer`, `EventLeakFastScanner`, `AsyncTaskAnalyzer`, `HangAnalyzer`, `WcfChannelAnalyzer`, `DbConnectionAnalyzer`, `DominatorAnalyzer`, `CrashAnalyzer`, `StringAnalyzer` (all `IHeapIndexScanParticipant` implementers) — no further recurring pattern of this shape found at this pass; revisit if a new participant is added that resolves per-entry data ClrMD-side instead of reading it off `HeapEntry`.
