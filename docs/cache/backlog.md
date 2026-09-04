# Cache Subsystem — Backlog

High-value work that hasn't been built yet, cross-checked against current source
(`upgrade/clrmd-4`) at time of writing — not a stale wishlist. Everything already
shipped lives in [cache-architecture.md](cache-architecture.md) instead. No priority
ordering implied by section order within a tier; pick based on what a real workload
actually hits.

Two clean-slate redesigns sit alongside this backlog, both optional and independent of
each other: the on-disk byte layout in
[cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md),
and the reader/writer/sub-cache code in
[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)
(which subsumes the fast-path-validation item below). Both are grounded in
[cache-redesign-measurements.md](cache-redesign-measurements.md) — measured against the five real
`cache.bin` files on disk, without loading any dump. Read that first; it overturned several
conclusions the two design docs originally reached.

## Real bounded-memory / correctness gaps

- **Unbounded satellite candidate collections.** `taskCandidates` and
  `lohFreeBlockCandidates` in `DiskBackedObjectIndexWriter` are `ConcurrentBag<...>`
  with no cap, unlike `masterStringDedup` (capped at 500k). A dump with millions of
  live `Task`s, or a heavily fragmented LOH, can push these into real memory territory
  during the build — directly against this project's bounded-memory philosophy. Fix:
  cap + sample like `masterStringDedup`, or stream to disk incrementally the way
  `ReverseEdgeExtractor` already does for edges.
- **Cache-hit fast path validation — partially closed (2026-09-04), remainder needs a format
  change.** `TryLoadFromCache` used to check only `TypeAggregates` + `ObjectAddresses`, so a
  transient write failure (disk-full, AV lock, permissions blip) silently and permanently
  downgraded every future analysis of that dump. It now asserts all seven `Required` sections
  are present — the four columnar object sections, `TypeAggregates`, `Roots`, `SegmentIndex` —
  via `CacheSectionCatalog`, with a unit test guarding against a new section id landing
  unclassified.
  **What remains:** the original fix ("confirm every section the *previous* build wrote") turned
  out to be unimplementable as stated — the TOC only lists sections that were successfully
  closed, so a lost section leaves nothing to diff against. Catching a lost *conditional*
  section (`Handles`, `Tasks`, the edge indices, the dominator sections) requires the writer to
  persist a manifest of intended sections, which is an additive format change. Note also that
  presence is checked but **not** checksum validity — validating every section here would hash
  the whole file on every cache hit, defeating the per-session memoization. Full reasoning in
  [cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)
  § 6.2/§ 6.5(a).

## Real, data-already-collected perf wins

- **`EventCandidates` is a reserved-but-unused section — not a free win.** An earlier
  version of this entry claimed the section "is written every build but never read" and
  called wiring a reader a "zero-new-infrastructure" win. That was wrong. A full-source
  search for `EventCandidate` returns only the reserved enum member
  (`CacheContainerFormat.cs`) and a stale doc comment plus an always-`null`
  `InMemoryEventCandidates` parameter on `HeapIndexBuildResult` — there is no candidate
  collection in the scan loop, no writer, and no reader. So `EventLeakAnalyzer`'s full
  `heap.EnumerateObjects()` scan is real, but eliminating it means adding collection +
  writer + reader, not just a reader. Keep the enum slot reserved (renumbering breaks
  existing caches); re-scope or drop the item.
- **`ConcatenateScratchFiles` runs fully after the parallel segment scan completes.**
  Segment scratch files are already ordered and each becomes ready independently, so
  concatenating segment 0's files could start as soon as segment 0 finishes, overlapping
  I/O-bound flush with the still-running CPU-bound scan of later segments. Only pays
  off on Large tier, where concatenation I/O is non-trivial; needs to track completion
  order vs. segment order correctly.
- **Segment-level scan parallelism has a floor of "number of segments."**
  `Parallel.For` partitions work by segment. A dump with few, large segments (some
  Server GC configs produce one huge segment per GC heap) gets little or no scan
  parallelism regardless of core count. Needs confirmation this is actually hit on real
  target dumps before investing.
- **`masterStringDedup` entry representation.** Up to 500k `StringDedupEntry` class
  instances, each with a `ulong[]?` sample array — real per-entry allocation/GC
  pressure during a large scan. Struct-of-arrays candidate, but only worth chasing if
  profiling shows GC pressure from this specifically (do the two items above first).

## Real, measured disk-footprint win

- **Edge-index and dominator-tree values stored as full 8-byte addresses instead of 4-byte node
  indices into the already-existing `ObjectAddresses` column.** Measured (not projected) on a real
  14.6M-object dump: the combined forward+reverse edge index is 57.1% of `cache.bin` (799 MB of
  1.37 GB), the dominator tree another 16.3% (228 MB) — together 73.4% of the file, discovered while
  investigating why `cache.bin` runs ~5x the size of a comparable tool's cache for the same dump (see
  [docs/discrepancy/cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md)).
  Full clean-slate design — true CSR for both edge directions (no directory overhead at all, not
  just narrower keys), `MethodTable` dictionary encoding, and block-level compression that preserves
  point-lookup access — in [cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md).
  **Since measured** ([cache-redesign-measurements.md](cache-redesign-measurements.md)): block
  compression alone, on the format as it stands today with no CSR work at all, gives 83.6% on this
  dump and 76.5% on a 27.5 GB one — more than CSR + dictionary + dominator combined (45.7%).
  On size grounds that reorders the plan: compression first, CSR last, where CSR's remaining
  argument becomes query speed and ~1,992 MiB of directory overhead at 27.5 GB scale, not raw size.
  **But do not start there.** Scrutiny found compression's *runtime* cost on the edge index is
  unmeasured and plausibly severe — those sections serve high-volume random `TryGetParents` lookups
  from BFS traversals, and bucket assignment is hash-scattered, so block reuse is poor
  ([format doc §7.2.1](cache-format-clean-slate-redesign.md)). Open question 1 in
  [the measurements doc §7](cache-redesign-measurements.md) gates this and is cheap to answer.

## GC-root enumeration at scale (diagnosis is done — see cache-architecture.md § 8; only the fix is open)

Confirmed intrinsic native cost (per-thread stack unwinding inside ClrMD's DAC layer),
56% of a 25GB dump's cold-build time. Three unattempted options, none started:

1. Investigate whether `CachedMemoryReader`'s page/segment cache size or page
   granularity can be tuned to reduce per-`ReadVirtual`-call overhead at large dump
   sizes.
2. ~~Defer GC-root indexing to an on-demand Phase 2 step instead of always paying it
   upfront in the cold Phase 1 build.~~ **Closed by decision, 2026-09-04, not by measurement.**
   `DD_SKIP_ROOT_INDEX_BUILD` was the A/B lever for exactly this, and it was deleted along
   with the other four skip toggles once GC roots were accepted as core output rather than
   an optional feature (see
   [cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)
   § 6.2.1). `Roots` is now a `Required` section validated by the cache-hit fast path, so
   "build it lazily or not at all" is no longer a supported shape. Reopening this would mean
   re-adding the lever first.
3. **Cheapest, no correctness/perf risk**: just surface "GC roots" as its own visible
   progress phase with an ETA (the progress-reporting plumbing already supports this)
   so a 25GB-dump user isn't left staring at an apparently-stuck scan for three
   minutes. Worth doing regardless of whether 1 or 2 ever happen.

## Object address lookup (`SegmentIndex` / `ObjectAddressLookup`)

- **Perf win was never rigorously confirmed — and the A/B lever is now gone.** Steady-state
  `TryGetObjectMetadata` measured comparable to (not clearly faster than) `heap.GetObject`
  on an already-warm heap — the real T2 call-site usage pattern. A BenchmarkDotNet harness
  exists (`src/BenchmarkSuite1/ObjectAddressLookupBenchmark.cs`) but hasn't been run.
  Note that `DD_SKIP_SEGMENT_INDEX_BUILD` was the way to A/B this end-to-end, and it was
  deleted on 2026-09-04 with `SegmentIndex` promoted to a `Required` section — so the
  "is it worth building?" half is decided, and only the narrower "is the lookup faster than
  `heap.GetObject`?" question remains, answerable via the harness alone.
- **`ObjectAddressLookup` now opens through the run's container session** rather than its own
  reader — that was the last meaningful source of duplicate checksum verification
  ([cache-redesign-measurements.md](cache-redesign-measurements.md) § 7.1). Nothing further
  to do here; noted so it isn't re-investigated.
- **Interior-pointer resolution** (nearest object ≤ address, for conservative-GC-style
  lookups) is unimplemented. No current caller needs it — low priority, revisit only if
  one appears.

## Observability

- **`CacheMetrics`/`GetHealth()` are fully implemented dead code.** All seven
  sub-caches report `EntryCount`/`LastBuildDurationMs`/`IsHealthy`/`LastError` via
  `HeapAnalysisCache.GetCacheMetrics()`/`GetHealth()`, but nothing in the CLI, JSON
  output, or report generator calls either method — confirmed via call-graph search,
  zero non-test, non-definition references. Either wire this into something real (a
  `--cache-health` CLI flag, or fold a one-line summary into verbose/debug output), or
  delete it — per this project's "no half-finished implementations" convention.
- **Cache hit/miss telemetry line.** No visible signal today for "did this run hit the
  cache, or rebuild it, and why." A natural place to resurface the `GetHealth()` data
  above rather than building a separate mechanism.

## Gated / speculative — only build if the gate condition is actually observed

Don't build any of these on spec; they're listed so the gate is known, not forgotten.

| Item | Gate |
|---|---|
| Privacy opt-in (`--cache-redact` strips type/method/string names, `--redact-strings`) | A team with strict data-governance requirements needs it; not required for MVP — cache already lives in a user-local, ACL'd directory |
| Manual cache-clean command | Users start asking for one; no TTL/LRU planned regardless (one-shot CLI, not a long-running service with concurrent competing entries) |
| Schema-driven writer/reader parity (source generator for section read/write) | A new section added by hand causes a writer/reader drift bug, or a second hand-written writer/reader pair reappears |
| Analyzer-result caching (not just index caching) | Repeat-invocation interactive workloads show `AnalyzeAsync` itself, not index build, dominating re-run time |
| Concurrent-writer lock file | Telemetry shows duplicate builds actually happening in practice (e.g. CI matrix jobs racing on the same dump) |
| Cache portability / export-import | Only after the privacy opt-in above ships |
| Secondary indices / query pushdown for per-type enumeration | A per-type enumeration query is shown to be a real bottleneck, not a guess |
