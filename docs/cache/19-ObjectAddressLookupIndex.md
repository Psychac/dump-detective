# 19 — Object Address → (MethodTable, Size) Lookup Index

> Status: **Implemented and shipped (Phases 0–8 complete).** See [Open question 5](#open-questions-for-discussion)
> for the one unresolved item: Phase 7's perf validation did not clearly confirm the assumed per-call
> latency win over `heap.GetObject` — only architectural consistency/correctness. A rigorous
> BenchmarkDotNet follow-up (`src/BenchmarkSuite1/ObjectAddressLookupBenchmark.cs`) is written but not
> yet run.
>
> Origin: `docs/analysis/phase1/static-root-leak-detector-audit.md`, P1-5 — "Use object index for
> size/MT resolution inside retained-object loop instead of `heap.GetObject` per address."

---

## Decision

Build `TryGetObjectMetadata(heap, address)` (the `SegmentIndex`-backed lookup described below) as a
general platform primitive, and route every genuine T2 site from Appendix A through it — not just the
highest call-volume ones.

Appendix B's volume calculus (only 1 of 20 T2 sites is millions-of-calls scale; the rest are bounded by
handle-table/exception-count/candidate-count) is accurate and useful for prioritization, but wasn't the
deciding factor. Once the object index exists and already holds `Address → (MethodTable, Size)` for the
whole heap, resolving a known address by going back to `heap.GetObject` — a live ClrMD/DAC call — is the
wrong default, independent of how often a given call site happens to run today. The lower-volume T2
sites (`WeakReferenceAnalyzer`, `CrashAnalyzer`, `GCHandleAnalyzer`, `LockGraphAnalyzer`) are exactly the
kind of call site that quietly regresses on whatever large dump exercises them hardest (e.g. millions of
GC handles, or an exception-heavy crash dump) — "low volume in the cases profiled so far" isn't the same
guarantee as "bounded." This mirrors the CLAUDE.md principle: prefer the disk-backed index over live
ClrMD resolution whenever the index already has the data, rather than deciding per call site based on
current measured cost.

Practical implications for scope:

- All **20 T2 sites** get migrated to `TryGetObjectMetadata`, not just `StaticRootLeakDetector` and
  `DominatorAnalyzer`.
- The **T1 sites** (6, MT already known) just need `heap.GetTypeByMethodTable(mt)` — no address lookup
  involved, so `SegmentIndex` doesn't change that fix; do it independently.
- The **free-tuple `BoundedGraphWalk` fix** (Appendix B) is complementary, not a replacement: it avoids
  a live `heap.GetObject` call inside a BFS's own second pass with zero new infrastructure, while
  `TryGetObjectMetadata` handles every case where the address didn't come from an in-process BFS at all.
  Do both.

---

## Problem

`ObjectIndexReader` (`src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs`) only supported
**sequential** enumeration of the columnar `ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/
`ObjectGenerations` sections (`ReadEntries`/`ReadEntriesRange`), zero-copy via mmap — no **random-access
point lookup** `address → (MethodTable, Size)`. Any analyzer that already held a `ulong` address
(typically discovered via a prior BFS or index enumeration) had to re-resolve it via
`heap.GetObject(addr)`, a live ClrMD/DAC call, even though the disk index already had the data for every
object on the heap.

`BoundedGraphWalk` (`src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs`) — the shared BFS
primitive used by `StaticRootLeakDetector`, `DominatorAnalyzer`, and `MemoryAnalyzer` — and at least 16
other analyzers independently called `heap.GetObject` in similar address-driven loops (see Appendix A
for the full audit). The concrete pattern that motivated this: `StaticRootLeakDetector.AnalyzeStaticRoots`
ran a BFS (`BoundedGraphWalk.CollectRetainedObjects`) that necessarily calls `heap.GetObject` per node to
reach `obj.EnumerateReferences()` — unavoidable — then ran a **second** loop over the same retained-address
set calling `heap.GetObject` **again**, purely to read `Size`/`MethodTable`, no traversal needed. That
second pass is the actual opportunity an index-backed lookup targets.

---

## Why a naive global binary search doesn't work

The `ObjectAddresses` column is written per-segment, in `segments[]` traversal order, then concatenated
(`DiskBackedObjectIndexWriter.Build`, see `ConcatenateScratchFiles`). Addresses are strictly increasing
**within** a segment (objects are bump-allocated contiguously), but segments themselves are not
guaranteed to be globally sorted by start address (server GC / multi-heap dumps can interleave). So
`Address` is not monotonic across the whole column, and a plain binary search over the full column is
unsafe. See Appendix C for alternative index designs considered and why the two-level segment-table
approach below won out.

---

## Design

### `SegmentIndex` section (build time)

`DiskBackedObjectIndexWriter` already knows, per segment, the first record index and record count it
wrote into the concatenated columns. `CacheSectionId.SegmentIndex` (= 17) stores one small record per
segment:

```
Start           ulong   // segment.Start
End             ulong   // segment.End
FirstRecordIndex long   // cumulative offset into the concatenated ObjectAddresses/MethodTables/Sizes columns
RecordCount     int     // objects written from this segment (no GC segment holds anywhere near 2^31 objects)
```

Segment count is small (tens to low thousands even for huge dumps), so this section is trivial in size
and fully loaded into memory at read time. Segments with zero objects are skipped entirely — a lookup
can never land in one, so there's nothing to record. Written by
`Indexing/Satellite/SegmentIndexWriter.cs` (header-plus-fixed-records style, magic `"SEGX"`, mirrors
`LargeObjectTracker`). Non-fatal on failure, matching every other satellite section — a build that fails
to write `SegmentIndex` still works, just without `TryGetObjectMetadata`'s fast path.
`DD_SKIP_SEGMENT_INDEX_BUILD=1` disables it for A/B isolation.

### Lookup path (read time)

`Indexing/ObjectAddressLookup.cs` — a sealed class holding **persistent** mmap accessors across many
calls (unlike `ReadEntries`'s per-call iterator):

- `TryOpen(containerPath)`: loads `SegmentIndex` fully into memory, sorts by `Start` (defensive —
  segment write order isn't guaranteed address-sorted), opens persistent `MemoryMappedViewAccessor`s for
  `ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`.
- `TryGetEntry(address, out methodTable, out size)`:
  1. Binary search the small in-memory segment table for the segment whose `[Start, End)` contains
     `address` — O(log segments).
  2. Binary search that segment's slice of the mmap'd `ObjectAddresses` column for exact address
     equality — O(log objects in segment), using plain bounds-checked `MemoryMappedViewAccessor.ReadUInt64`
     reads, **not** `ObjectIndexReader`'s unsafe zero-copy pointer pattern (that complexity is justified
     for hundreds-of-millions-of-records *sequential* batch reads, not thousands-of-lookups-per-run
     point queries).
  3. On a hit, read `MethodTable`/`Size` at the same record index from the other two accessors.
  4. Return `false` on segment miss (address between segments — LOH/POH gaps, free blocks, padding) or
     in-segment miss (interior pointer — out of scope, see open question 2).

### API surface

```csharp
// IObjectIndexReader / ObjectIndexReader — one-shot convenience wrapper
bool TryGetEntry(string containerPath, ulong address, out ulong methodTable, out ulong size);

// IHeapAnalysisCache — the primary entry point every call site actually uses
bool TryGetObjectMetadata(ClrHeap heap, ulong address, out ulong methodTable, out ulong size);
```

`IHeapAnalysisCache.TryGetObjectMetadata` backs onto the disk index when a valid `SegmentIndex` is
available; otherwise (in-memory mode, old cache, aborted satellite write) it falls back to
`heap.GetObject(address).{Type.MethodTable, Size}` transparently — callers never branch on backing mode.
Implementation lives in `HeapIndexCache` (lazily opens and caches one `ObjectAddressLookup` per
container, disposed with the cache) since that's the class that already owns the other lazy-on-first-use
structures this pattern matches. A disk index with a valid `SegmentIndex` is treated as **authoritative**
— a miss there is a final "not a live object" answer, not a trigger for a redundant live fallback (that
would reintroduce exactly the redundant resolution this index removes). Only a genuinely unavailable
`SegmentIndex` falls through to `heap.GetObject`.

---

## Scope and limits

- **Does not help BFS traversal itself.** `EnumerateReferences()` requires a live `ClrObject`, so
  `heap.GetObject` remains unavoidable inside a BFS's own dequeue loop. Only a *second*, size/MT-only
  pass over an already-known address set is addressable by this index.
- **Does help** any post-traversal pass over a known address set that only needs size/MT — see Appendix A
  for the full list of genuine beneficiaries.
- Point lookups return `false` for addresses not present in the index (freed/dead objects at build time,
  interior pointers) — treated as "not found," not an error.

---

## Cost / complexity

- **Build cost**: negligible — segment boundaries and record ranges are already known for free during
  the existing per-segment scan; only a small extra section write.
- **Read cost**: one small in-memory segment table per container open (built once, reused across
  lookups); each lookup is two binary searches. See [Open question 5](#open-questions-for-discussion) —
  the *open* itself has a real, measured one-time cost (~230ms on a ~14.6M-object dump, from
  checksum-verifying the full mmap'd columns), amortized across a whole analysis run since
  `AnalysisContext.Cache` is shared across every analyzer.
- **No container `FormatVersion` bump** — `SegmentIndex` is a new optional section, and the container
  format already treats missing sections as "unavailable, not corrupt." This is a deliberate difference
  from past additions to `CacheSectionId` that *did* bump `CurrentFormatVersion` (`ObjectGenerations`,
  `ReverseEdge*`): those were consumed in-place with no "unavailable, fall back" contract, so an old
  cache missing them could be read successfully but silently feed every consumer wrong defaults.
  `TryGetObjectMetadata` returning `false` is an explicit, already-expected "not available here" signal
  every caller already handles — not a silent default — so a version bump isn't warranted here.

---

## Implementation summary

Built across 8 phases; all shipped. Full phase-by-phase design/status narrative and real-dump validation
numbers live in git history (see the commits touching this doc) rather than duplicated here — this
section captures the decisions and corrections worth knowing when reading the code, not a rebuild log.

**Writer/reader (Phases 0–2)**: validated the load-bearing assumption — `segment.EnumerateObjects()`
yields strictly increasing, in-bounds addresses within a segment — against a real dump (14.6M objects,
`Ephemeral`/`Large` segment kinds, zero violations; server-GC `Generation0/1/2` and `Pinned`/`Frozen`
segments architecturally expected to hold but not yet independently verified against a server-GC dump).
Then built `SegmentIndexWriter`, wired segment record-count bookkeeping into
`DiskBackedObjectIndexWriter.Build`, and built `ObjectAddressLookup`'s two-level binary search exactly as
designed. Real-dump correctness oracle: every `TryGetEntry` result checked against what the index itself
recorded — 0 mismatches.

**Cache API (Phase 3)**: one correction to the original design — `TryGetObjectMetadata` needs a
`ClrHeap heap` parameter after all (the fallback path calls `heap.GetObject`), matching every other
`IHeapAnalysisCache` member with a live-ClrMD fallback. `HeapIndexCache` gained `IDisposable` to release
the lazily-opened `ObjectAddressLookup`'s mmap accessors. Real-dump oracle covering both disk and
in-memory mode against live `heap.GetObject` resolution: 0 mismatches in either mode.

**Free-tuple `BoundedGraphWalk` fix (Phase 4)**: `CollectRetainedObjects` now returns
`Dictionary<ulong, (MethodTable, Size)>` captured for free from the BFS's own `heap.GetObject` call —
zero new infrastructure. `StaticRootLeakDetector`'s second-pass loop consumes it directly with
`MethodTable`-keyed name memoization via `heap.GetTypeByMethodTable`. One correctness nuance documented
in code: an address can appear with `MethodTable == 0` if it was discovered but never dequeued/resolved
(only possible when the scan hit its cap) — accepted as an already-disclosed-uncertainty case (the
report already qualifies capped results) rather than reintroducing a second resolution pass for that
bounded frontier. The parallel change to `ComputeExclusiveRetained` (item 4 of the original plan) was
**deliberately skipped** — no current caller does a second pass over its result, and CLAUDE.md is
explicit about not designing for hypothetical future requirements; left for whoever actually needs it,
informed by real requirements then.

**T1 call-site fixes (Phase 5)**: 6 sites (`CollectionAnalyzer` ×2, `LohFragmentationAnalyzer`,
`HangAnalyzer` ×2, `AsyncTaskAnalyzer`) where the `MethodTable` was already in hand — swapped
`heap.GetObject(address).Type` for `heap.GetTypeByMethodTable(mt)`, no index involved. One deliberate
semantic adjustment in `LohFragmentationAnalyzer`: the gate changed from "is the *object* resolvable"
to "is the *method table* resolvable" — equivalent in practice here since both address and MT are
written together from an already-`IsValid`-checked scan-time capture, on a static dump snapshot that
can't change between build and read.

**T2 call-site migration (Phase 6)**: all 20 sites, in priority order (shared root-path infrastructure
first, since it benefits every analyzer that calls into it). Added
`RootPathSearchSupport.ResolveType(heap, cache, address)` as the single shared resolver used by
`RootPathFinder`, `IndexBackedBidirectionalSearch`, `CandidateSetBuilder`, `ReverseReferenceIndex`, and
`BidirectionalPathFinder` — each gained an optional trailing `IHeapAnalysisCache? cache = null`
parameter, threaded from `RootPathFinder` down into every class it constructs, and from 6 analyzer call
sites (`ReferenceChainAnalyzer` needed threading through two extra private-method layers, following the
same pattern already used for `reverseIndexProvider`). Notable corrections found while migrating:

- `DominatorAnalyzer.CreateHighlyReferencedObjectSnapshot` fully delegates to `cache` when supplied —
  deliberately **not** falling back to `heap.GetObject` on a cache miss, since that would reintroduce
  the redundant-resolution pattern this index removes.
- `CrashAnalyzer.ResolveExceptionType`/`IsExceptionEntry` turned out to already have the `MethodTable` in
  hand — genuinely T1-shaped, not T2 — and simplifying `ResolveExceptionType` around
  `heap.GetTypeByMethodTable(mt)` also deleted a dead sample-address fallback path (both call sites
  always passed `heapIdx: null`) and fixed the redundant re-resolution of `exceptionAddress` the
  Appendix A audit flagged, as a side effect of the simplification rather than a targeted patch.
- `LockGraphAnalyzer` didn't accept `IHeapAnalysisCache` at all before this change (`AnalyzeAsync`
  discarded `context.Cache`) — threaded through 4 method layers.
- `StaticRootLeakDetector.cs`'s retained-object loop is **not** migrated here — it's already fixed by
  Phase 4's free-tuple change, strictly cheaper for that specific call site.

Real-dump end-to-end validation: all 10 touched analyzers run successfully in both disk and in-memory
mode, no exceptions.

**Incident during this phase, fixed at the root**: running discrepancy tests OOM-crashed the development
machine three times. Root cause: this test project had no xunit parallelization configuration, so
xunit's default (run test classes in parallel, up to processor count) let a single `dotnet test` run
schedule multiple dump-loading test classes concurrently, each independently loading the full
1GB-25GB+ dump. Fixed with `tests/DumpDetective.Tests/xunit.runner.json`
(`parallelizeAssembly`/`parallelizeTestCollections: false`, `maxParallelThreads: 1`), wired into the
`.csproj` to copy to the output directory, plus an explicit rule in the root `CLAUDE.md` Testing section:
real-dump tests always run one at a time, foreground, never via `run_in_background` or parallel tool
calls.

**Testing and perf validation (Phase 7)**: closed the remaining coverage gaps — a reader test making
explicit that a zero-object segment is indistinguishable from any other address gap, and a real-dump
test that corrupts a genuine disk build's `SegmentIndex` bytes (not just a synthetic fixture) and
confirms the fallback path still agrees with live `heap.GetObject` (0 mismatches). **The important
finding**: perf validation did not confirm the assumed win.
- One-time `ObjectAddressLookup.TryOpen` cost measured **~230ms** (full-section XxHash32 checksum
  verification over ~350MB of mmap'd columns on this dump) — real, amortized across a shared cache
  instance's whole lifetime, not per-lookup.
- Steady-state per-call cost measured **~0.0024ms/call** for `TryGetObjectMetadata` vs. **~0.0014ms/call**
  for `heap.GetObject`, on an already-warm heap — comparable at best, not a demonstrated win, though the
  sample (146 calls) is small enough that this is more likely noise than a real regression. The
  already-warm scenario measured is representative of actual T2 usage (same process as the preceding
  full-heap scan), which makes the inconclusive result more consequential, not less.
- Thread-safety under concurrent lookups (mmap reads have no shared mutable state, unlike ClrMD/DAC
  access) is a real, architecturally-relevant advantage this single-threaded measurement doesn't
  capture at all.
- Wrote `src/BenchmarkSuite1/ObjectAddressLookupBenchmark.cs` (BenchmarkDotNet, proper warmup/iteration
  counts, parameterized call volumes) for a statistically rigorous follow-up — not run this session.
- `SegmentIndex` build-time A/B was not empirically measured (would need two separate full-dump-load
  processes, since the skip toggle is a `static readonly` field read once at process start) — left as a
  genuine open item; the analytical case (segment-count-sized, not object-count-sized) remains strong.

**Docs (Phase 8)**: `docs/binary-format.md` updated with `SegmentIndex`'s layout (and, found while
updating the same TOC/section-count numbers, filled in a pre-existing gap where the `ReverseEdge*`
sections and the actual `FormatVersion` were never documented despite already existing in code).
`docs/analysis/phase1/static-root-leak-detector-audit.md`'s P1-5 row marked done, with a note that it
shipped via Phase 4's free-tuple capture for `StaticRootLeakDetector` specifically, not by routing that
analyzer through `SegmentIndex` — the index's actual beneficiaries are the Phase 6 sites.

### Test coverage map

| Area | File |
|------|------|
| Segment-contiguity assumption (real dump) | `Integration/CacheDiscrepancies/SegmentAddressContiguityDiscrepancyTests.cs` |
| `SegmentIndexWriter` (synthetic) | `Unit/Indexing/Satellite/SegmentIndexWriterTests.cs` |
| Full writer pipeline (real dump) | `Integration/CacheDiscrepancies/SegmentIndexBuildDiscrepancyTests.cs` |
| `ObjectAddressLookup` (synthetic) | `Unit/Indexing/ObjectAddressLookupTests.cs` |
| Lookup correctness oracle (real dump) | `Integration/CacheDiscrepancies/ObjectAddressLookupDiscrepancyTests.cs` |
| `HeapIndexCache.TryGetObjectMetadata` (synthetic) | `Unit/Cache/HeapIndexCacheTests.cs` |
| `TryGetObjectMetadata` disk+memory oracle (real dump) | `Integration/CacheDiscrepancies/HeapAnalysisCacheObjectMetadataDiscrepancyTests.cs` |
| `CollectRetainedObjects` free-tuple oracle + end-to-end (real dump) | `Integration/CacheDiscrepancies/BoundedGraphWalkCollectRetainedObjectsDiscrepancyTests.cs` |
| T1 sites end-to-end (real dump) | `Integration/CacheDiscrepancies/Phase5T1FixesEndToEndDiscrepancyTests.cs` |
| T2 sites end-to-end, disk+memory (real dump) | `Integration/CacheDiscrepancies/Phase6T2FixesEndToEndDiscrepancyTests.cs` |
| Perf comparison + real-build fallback correctness (real dump) | `Integration/CacheDiscrepancies/ObjectAddressLookupPerfAndFallbackDiscrepancyTests.cs` |
| Rigorous perf benchmark (not yet run) | `src/BenchmarkSuite1/ObjectAddressLookupBenchmark.cs` |

---

## Open questions for discussion

1. Is the second-pass win (avoiding redundant `heap.GetObject` calls after a BFS that already visited
   every address) worth the new section + reader complexity, or is it cheaper to just have
   `BoundedGraphWalk.CollectRetainedObjects` return `(Address, Size, MethodTable)` tuples directly from
   the BFS's own `heap.GetObject` calls, avoiding the second pass entirely with **zero** new
   infrastructure? — **Answered in Appendix B** (scoped to the whole application): only 1 of 20 T2 sites
   is fixed by that trick; the rest have no BFS to piggyback on. Both were built.
2. If we build the index anyway, is it worth going further and supporting *interior-pointer* resolution
   (nearest object ≤ address) for conservative-GC-style lookups, or is exact-match sufficient for all
   current callers? — Still open; no current caller needs it.
3. Should `SegmentIndex` be schema-versioned as a new optional section or should it force a rebuild? —
   Settled: optional section, no version bump (see Cost/complexity above).
4. Which analyzers beyond `StaticRootLeakDetector`, `DominatorAnalyzer`, and `MemoryAnalyzer` would
   actually benefit, versus how many `heap.GetObject` call sites are traversal-bound and unaffected? —
   **Answered in Appendix A**: 20 genuine T2 beneficiaries across 9 files.
5. **Open, from Phase 7's perf validation**: steady-state `TryGetObjectMetadata` measured comparable to
   (not clearly faster than) `heap.GetObject` on an already-warm heap. Given T2 call sites' real usage
   pattern is exactly "same process as a preceding full-heap scan" (i.e. always warm), is there a
   realistic scenario where the index wins decisively enough to justify the ~230ms one-time open cost,
   or does the value of Phases 1–6 rest more on architectural consistency/correctness (single
   index-first code path, thread-safety for future parallel callers) than on a proven latency win? Worth
   settling with the `ObjectAddressLookupBenchmark.cs` BenchmarkDotNet run before assuming this pattern
   should be applied to further call sites on the expectation that they'll be faster.

---

## Appendix A — Full call-site audit of `heap.GetObject` in `DumpDetective.Analysis`

A `grep -n "heap\.GetObject\("` across `src/DumpDetective.Analysis` finds **~75 call sites across 35
files** — every one was read in context and classified below by what it actually needs from the
`ClrObject`, not just whether it's inside a loop.

### Classification key

| Tier | Meaning | Addressable by this index? |
|------|---------|------------------------------|
| **T1 — MT already known** | Caller already has the `MethodTable` (from a `HeapEntry`, a cache key, or a callback parameter) but still calls `heap.GetObject(address)` to reach `.Type`. | **No new index needed at all** — replace with `heap.GetTypeByMethodTable(mt)`, a metadata-cache lookup with no object materialization. |
| **T2 — address-only, size/MT/name only** | Caller has only a `ulong` address (from a BFS frontier, a handle record, a reverse-index neighbor list, etc.) and needs nothing beyond validity + `MethodTable`/`Size`/type name. | **Yes** — the genuine target for `TryGetObjectMetadata`. |
| **T3 — sample-address, low volume** | A single representative address per *type* (`TypeAggregateIndexEntry.SampleAddress`), used when `heap.GetTypeByMethodTable(mt)` alone didn't yield a name. Call volume ≈ unique type count, not object count. | Technically eligible, but marginal. |
| **A — field/content-bound** | Needs instance fields (`GetFieldByName`+`Read`/`ReadObject`), array contents (`AsArray().Length`), or string content (`AsString()`) — data the index doesn't and can't store. | No. |
| **B — traversal-bound** | Calls `obj.EnumerateReferences(...)` directly, or the `ClrObject` feeds a `BoundedGraphWalk` BFS, or walks a live object chain hop-by-hop. | No — requires a live `ClrObject` regardless of any index. |

### T1 — MT already known, index not needed (6 sites)

`CollectionAnalyzer.ResolveCollectionKind`/`ResolveCollectionKindConcurrent`,
`LohFragmentationAnalyzer` (LargeObjectTracker callback), `HangAnalyzer`'s `AsyncTypeProfile`
classification and `ResolveAsyncTypeProfile`, `AsyncTaskAnalyzer.ResolveTypeName` — all a pure
`heap.GetTypeByMethodTable(mt)` substitution. **All migrated in Phase 5.**

### T2 — address-only, genuine index beneficiaries (20 sites across 9 files)

| Site | What it needs | Notes |
|------|----------------|-------|
| `StaticRootLeakDetector.GetObjectMetadata` | `Type.Name`, `Size`, `MethodTable` | The original P1-5 case — post-BFS retained-set aggregation. Fixed by Phase 4's free-tuple change instead. |
| `DominatorAnalyzer.CreateHighlyReferencedObjectSnapshot` | `Type.Name`, `Size` | Address from reverse-index incoming-reference list, not a live traversal. |
| `RootPathFinder`/`IndexBackedBidirectionalSearch` (`_isNoise`/`forceExpand` gating) | `Type` only | Neighbor traversal goes through `IReferenceProvider.GetReferences`, not `obj.EnumerateReferences` — `heap.GetObject` here is purely a type-classification gate. |
| `WeakReferenceAnalyzer` (3 sites) | `IsValid`, `Type.Name` | Classifying weak/dependent-handle targets from handle records. |
| `CrashAnalyzer` (`IsExceptionEntry`, exception name resolution) | `Type.IsException`, `Type.Name` | Reclassified as T1 during Phase 6 — `MethodTable` was already in hand. |
| `GCHandleAnalyzer` (3 sites) | `Size` only (2 sites), `Type.Name` (1 site) | Pinned/AsyncPinned byte accounting + strict type-name resolution. |
| `LockGraphAnalyzer.ResolveTypeNameByAddress` | `Type.Name` | |
| `RootPathSearchSupport.FormatNodeByAddress` | `IsValid`, `Type.Name` | Root-path display formatting. |

This is the real answer to "which analyzers benefit beyond the three named in the audit":
`WeakReferenceAnalyzer`, `CrashAnalyzer`, `GCHandleAnalyzer`, `LockGraphAnalyzer`, and the shared
`RootPathFinder`/`IndexBackedBidirectionalSearch`/`RootPathSearchSupport` traversal-support primitives
— the last three are especially high-leverage since they're used by every analyzer that calls root-path
or bidirectional search. **All 20 migrated in Phase 6.**

### T3 — sample-address, low-volume (~7 sites, not worth building for)

`HeapAnalysisCache`/`TypeMetadataCache`/`TypeAggregateNameResolver`/`MethodTableCache` sample-address
fallbacks, plus two sites in `WeakReferenceAnalyzer`/`AsyncStateMachineAnalyzer` that actually need field
reads too (misclassified as T3 in the initial pass, are really Tier A). All already try
`heap.GetTypeByMethodTable(mt)` first and only fall back to a sample address when that fails — most
calls in this tier never execute in the common case.

### A/B — not addressable by this index (~40 sites)

Instance-field reads, array/string contents, or live `EnumerateReferences`/chain-walk traversal:
`ArrayAnalyzer`, `EventLeakFastScanner`, `EventLeak/DelegateChainWalker`, `TimerLeakAnalyzer`,
`WcfChannelAnalyzer`, `TypedResourceSampler`, `DbConnectionAnalyzer`, `HttpObjectAnalyzer`,
`StringAnalyzer`, `EventLeakAnalyzer`, `StaticRootLeakDetector.HasDelegateFields`,
`AsyncTaskAnalyzer` (field reads / continuation-chain walk), `CollectionAnalyzer`'s
`GetOrBuildFieldLayout`-driven sites, `GCRootAnalysisProjection`, `CrashAnalyzer`'s message/stack-trace/
inner-exception chain walk, `BoundedGraphWalk` itself, `ReferenceGraph.GetReferences`,
`FinalizableObjectAnalyzer`'s own BFS, `MemoryAnalyzer`/`DominatorAnalyzer`'s BFS root-feed sites,
`HangAnalyzer`'s continuation/field-based classification, `ReferenceChainAnalyzer.TryGetValidObject`.

### Summary

| Tier | Count | Outcome |
|------|-------|---------|
| T1 — MT already known | 6 | Fixed Phase 5, no index needed. |
| T2 — genuine index beneficiaries | 20 | Fixed Phase 6 (19 sites) + Phase 4 (`StaticRootLeakDetector`, cheaper without the index). |
| T3 — sample-address, low volume | ~7 | Left alone — volume too low to matter. |
| A/B — field/content/traversal-bound | ~40 | Unaffected by any address→(MT,Size) index; out of scope. |

---

## Appendix B — Does "return tuples from the BFS" replace the need for `SegmentIndex`?

Open question 1 asked whether it's cheaper to have `BoundedGraphWalk.CollectRetainedObjects` return
`(Address, MethodTable, Size)` directly from its own `heap.GetObject` calls, instead of building
`SegmentIndex`. This only makes sense to answer at the whole-application level, since the answer depends
on *where* each analyzer's addresses come from.

### The "free tuple" trick, precisely

`CollectRetainedObjects` already calls `heap.GetObject(current)` for every node it visits, purely to
check `obj.IsValid` and reach `obj.EnumerateReferences()` for the next BFS layer — `obj.Type.MethodTable`
and `obj.Size` are already resolved in memory at that point, so capturing them costs nothing beyond a
struct copy. Changing the return type from `HashSet<ulong>` to
`Dictionary<ulong, (MethodTable, Size)>` costs nothing extra either — same O(1) membership semantics,
+160 KB per root at the default cap, negligible against the multi-GB dumps this tool targets.

Applied to `StaticRootLeakDetector.AnalyzeStaticRoots`, this eliminates its second loop's
`heap.GetObject` call entirely — no new section, no new reader, no schema bump. The remaining need,
`Type.Name`, resolves from the already-known `MethodTable` via `heap.GetTypeByMethodTable` (a
metadata-cache hit, not dump I/O), memoized per-`MethodTable` so a type with 10,000 retained instances
resolves its name once, not 10,000 times.

### Does this generalize?

No. Cross-referencing against Appendix A's 20 T2 sites: only **`StaticRootLeakDetector`** gets its
addresses from an in-process BFS whose `heap.GetObject` call could be piggybacked. The other 19 either
read from a flat table with no traversal at all (GC handles, exception addresses, lock table,
reverse-index incoming-reference lists) or their traversal is *already* index-backed
(`RootPathFinder`/`IndexBackedBidirectionalSearch` use the reverse-edge index for neighbor expansion, so
there's no per-node `heap.GetObject` call to "return" from — the classification call that exists is
separate from expansion).

### Recommendation (both were built)

1. The free-tuple fix in `BoundedGraphWalk.CollectRetainedObjects` — zero infrastructure, fixes the exact
   case the original P1-5 audit item flagged, and any future analyzer reusing this primitive in the same
   shape gets it for free too. **Done (Phase 4).**
2. `SegmentIndex` for the other 19 T2 sites, none of which have a BFS to piggyback on. **Done (Phase 6).**
   The free-tuple fix removes the *strongest volume-based* justification for `SegmentIndex` (the
   millions-of-calls retained-set case), so on a pure call-volume argument the remaining case rests on
   19 lower-volume sites — but per the Decision above, the project proceeded on architectural grounds
   (index-over-live-ClrMD whenever the index has the data) rather than measured volume.

---

## Appendix C — Alternative index designs considered

The proposed design doesn't trade sequential performance for seek performance — `SegmentIndex` is purely
additive, so every existing sequential consumer (`ReadEntries`, `EnumerateIndexedEntriesAsTuples`,
`TypeIndexBuilder`, ...) keeps its current zero-copy linear-scan performance unchanged. Three alternative
*forms* for the seek side were considered and rejected:

**1. Fully re-sort the primary columns globally by address** (single flat binary search, no segment
table). Rejected: `DiskBackedObjectIndexWriter`'s existing segment-order concatenation exists
specifically to keep disk-mode's enumeration order deterministic and matching memory-mode's
(`heap.EnumerateObjects()` iterates `heap.Segments`, itself not address-sorted) — capped-scan analyzers
(`MaxLeakScanObjects`, `StaticRootLeakDetector`'s exact totals) depend on *which* objects populate a
partial scan, a direct function of enumeration order. Full-heap aggregate analyzers wouldn't care, but
capped ones would diverge between disk and memory mode — reopening a determinism problem this codebase
already solved, for a benefit (one binary search instead of two) that doesn't move the needle: the
second-level search already operates over only one segment's worth of records, not the whole heap.

**2. A separate, fully-sorted secondary index** (duplicate `SortedAddress` + permutation/duplicate
columns). Rejected: avoids Alternative 1's determinism risk, but duplicates the address column's full
size (hundreds of MB–low GB on a 25GB-class dump) plus a permutation array — directly against CLAUDE.md's
"don't store large data redundantly." `SegmentIndex` reuses the existing columns as-is and adds only a
segment-count-sized table (kilobytes, not gigabytes).

**3. A fixed-size block index** (e.g. every 4,096 records, instead of per-segment). Rejected: only helps
if it shrinks the second-level search range below "one segment's worth" (which barely matters, already
cheap) or tolerates within-segment order not holding (it doesn't solve that any better than segments
already have to — see the Phase 0 fallback plan for a violating segment). Segment boundaries are free
metadata from `ClrSegment`; block boundaries would be new metadata for no corresponding benefit, at
2–3 orders of magnitude more table entries.

**Conclusion**: the two-level segment-table design is the only option with zero duplicated heap-scale
data, zero extra build cost beyond bookkeeping already available for free, no disturbance to the
existing disk/memory determinism invariant, and a "two binary searches" cost dominated by a
segment-count-sized search (trivial) followed by a within-one-segment search (still small relative to
the whole heap).
