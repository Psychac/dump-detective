# Cache Subsystem — Backlog

High-value work that hasn't been built yet, cross-checked against current source
(`upgrade/clrmd-4`) at time of writing — not a stale wishlist. Everything already
shipped lives in [cache-architecture.md](cache-architecture.md) instead. No priority
ordering implied by section order within a tier; pick based on what a real workload
actually hits.

## Real bounded-memory / correctness gaps

- **Unbounded satellite candidate collections.** `taskCandidates` and
  `lohFreeBlockCandidates` in `DiskBackedObjectIndexWriter` are `ConcurrentBag<...>`
  with no cap, unlike `masterStringDedup` (capped at 500k). A dump with millions of
  live `Task`s, or a heavily fragmented LOH, can push these into real memory territory
  during the build — directly against this project's bounded-memory philosophy. Fix:
  cap + sample like `masterStringDedup`, or stream to disk incrementally the way
  `ReverseEdgeExtractor` already does for edges.
- **Cache-hit fast path only validates 2 of ~14 sections.** `TryLoadFromCache` checks
  that `TypeAggregates` + the columnar `Objects` sections exist and match the content
  hash. It never re-checks satellite sections (Roots, Handles, Tasks, EventCandidates,
  reverse index, `SegmentIndex`, …) on a *later* cache hit. A transient write failure
  (disk-full, AV lock, permissions blip) during one run silently and **permanently**
  downgrades every future analysis of that dump until someone deletes `.dumpindex/` by
  hand. Fix: extend the fast-path check to confirm every section the *previous* build
  successfully wrote is still present and checksum-valid, not just the two required
  ones.

## Real, data-already-collected perf wins

- **`EventCandidateIndex` section is written every build but never read.**
  `EventLeakAnalyzer` always does a full `heap.EnumerateObjects()` scan regardless —
  there is no `EventCandidateIndexReader` anywhere in the codebase. The data is
  already collected and paid for during the write pass; wiring `EventLeakAnalyzer` to
  prefer it (mirroring how `AsyncTaskAnalyzer`/`RootSetCache`/etc. already prefer their
  disk-backed candidates) is a real, scoped, zero-new-infrastructure perf win.
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

## GC-root enumeration at scale (diagnosis is done — see cache-architecture.md § 8; only the fix is open)

Confirmed intrinsic native cost (per-thread stack unwinding inside ClrMD's DAC layer),
56% of a 25GB dump's cold-build time. Three unattempted options, none started:

1. Investigate whether `CachedMemoryReader`'s page/segment cache size or page
   granularity can be tuned to reduce per-`ReadVirtual`-call overhead at large dump
   sizes.
2. Defer GC-root indexing to an on-demand Phase 2 step instead of always paying it
   upfront in the cold Phase 1 build, trading a slower on-demand root query for a
   faster initial index build on very large dumps.
3. **Cheapest, no correctness/perf risk**: just surface "GC roots" as its own visible
   progress phase with an ETA (the progress-reporting plumbing already supports this)
   so a 25GB-dump user isn't left staring at an apparently-stuck scan for three
   minutes. Worth doing regardless of whether 1 or 2 ever happen.

## Object address lookup (`SegmentIndex` / `ObjectAddressLookup`)

- **Perf win was never rigorously confirmed.** Steady-state `TryGetObjectMetadata`
  measured comparable to (not clearly faster than) `heap.GetObject` on an
  already-warm heap — the real T2 call-site usage pattern. A BenchmarkDotNet harness
  exists (`src/BenchmarkSuite1/ObjectAddressLookupBenchmark.cs`) but hasn't been run.
  Worth settling before assuming this pattern is a guaranteed win if applied to further
  call sites — the value shipped so far may rest more on architectural consistency
  (one index-first code path) than on a proven latency win.
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
