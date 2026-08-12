# 18 — Index Build Phase: Performance & Memory Opportunities

## Context

Architect-level review of `DiskBackedObjectIndexWriter.Build` (the Phase 1 heap
scan/index build) and its immediate callers (`HeapIndexCache`,
`HeapIndexScanDispatcher`, `BuildHeapIndexStage`, `LoadDumpStage`, `DumpLoader`),
looking for further speedups / memory-footprint reductions on top of the
optimizations already in place (columnar scratch files, ArrayPool reuse,
once-per-MT caching, batched reverse-edge writes, etc.).

Not exhaustive — a snapshot of what stood out on read-through, not a profiled
result. Each item should be validated with the existing
`DiskIndexBuildPhaseBreakdownPerfTests` harness (see
[17-DiskIndexBuildPhaseBreakdown.md](17-DiskIndexBuildPhaseBreakdown.md)) before
and after any change.

## Findings

### 1. Scratch-file round-trip costs 3x I/O on Small/Medium dumps

`DiskBackedObjectIndexWriter.Build` always writes per-segment scratch files
during the parallel scan, then serially reads them back and rewrites into the
container (`DiskBackedObjectIndexWriter.cs:343-397` write, `:475-494`
concatenate). That's the right tradeoff for Large dumps (bounds memory), but
for Small/Medium tiers the whole object set fits comfortably in memory —
paying write + read + write for data that could be buffered once and written
directly is pure overhead.

**Proposal:** tier-gate the write path — for Small/Medium, accumulate segment
output into in-memory column arrays and do a single write pass instead of the
scratch-file dance. Zero risk to Large-dump behavior since it stays on the
existing path. Best first cut: narrow, tier-scoped, easy to A/B.

### 6. Every index consumer re-reads from disk, always — even for tiny dumps

`HeapIndexCache.EnumerateIndexedEntries()`
(`src/DumpDetective.Analysis/Cache/HeapIndexCache.cs:76-83`) unconditionally
reads through `ObjectIndexReader.Instance.ReadEntries(...)` for every pass —
including every analyzer pass fanned out by `HeapIndexScanDispatcher`. And
`PrebuildHeapIndex` (`HeapIndexCache.cs:47`) hardcodes
`new DiskBackedObjectIndexWriter()` regardless of the `_sizeTier` it computes —
the tier is never actually used to pick a strategy.

Notably, `HeapIndexBuildResult.InMemoryEntries`
(`src/DumpDetective.Analysis/Indexing/HeapIndexBuildResult.cs:51`) and doc
comments referencing it (e.g. `AsyncTaskAnalyzer.cs:373`) already assume an
in-memory fast path exists — it doesn't; the field is always `null`. This
looks like a partially-built feature.

**Proposal:** wire up the in-memory path for Small/Medium tiers so both the
build (#1) and every subsequent scan (Phase 2 shared analyzer pass, ad-hoc
queries) skip disk entirely. This subsumes #1 and compounds with it — bigger
win than #1 alone, but touches more call sites (`HeapIndexCache`,
`ObjectIndexReader` call sites), so more surface area to validate.

### 2. Unbounded in-memory satellite candidate collections

`taskCandidates` and `lohFreeBlockCandidates` are `ConcurrentBag<...>` with no
cap (`DiskBackedObjectIndexWriter.cs:113-116`) — unlike `masterStringDedup`,
which is capped at 500k entries. A dump with millions of live `Task`s, or a
heavily fragmented LOH, can push these collections into real memory territory
during the build, working against the "bounded memory" definition-of-done in
`CLAUDE.md`.

**Proposal:** cap + sample like `masterStringDedup` does, or stream to disk
incrementally the way `ReverseEdgeExtractor` already does for edges.

### 3. Concatenation phase doesn't overlap with the scan

The parallel segment scan fully completes before `ConcatenateScratchFiles`
starts (`DiskBackedObjectIndexWriter.cs:178-475`). Segment scratch files are
already ordered and each becomes ready independently, so concatenation of
segment 0's files could start as soon as segment 0 finishes, overlapping
I/O-bound flush with the still-running CPU-bound scan of later segments.

Only pays off on Large tier, where concatenation I/O is non-trivial; more
invasive than #1/#2 since it needs to track completion order vs. segment
order correctly. Lower priority — validate #1 and #6 first.

### 4. `masterStringDedup` entry representation

Up to 500k `StringDedupEntry` class instances, each with a `ulong[]?` sample
array — real per-entry allocation overhead / GC pressure during a large scan.
Candidate for a struct-of-arrays representation, but only worth chasing if
profiling shows GC pressure from this specifically after #1/#2/#6 land.

### 5. `maxSegmentParallelism` cap of 8 for Large tier

Fixed regardless of actual core count or storage type
(`DiskBackedObjectIndexWriter.cs:92-97`). Worth validating against a modern
many-core + NVMe box — may be leaving throughput on the table — but this
needs a benchmark, not a guess.

### 7. Redundant per-type field walks

`ComputeTypeShape` and `ComputeStringFieldIndices`
(`DiskBackedObjectIndexWriter.cs:236-239`) each iterate `type.Fields`
separately, back-to-back, for the same `ClrType`. Bounded by unique-type-count
(cheap in aggregate), but on dumps with tens of thousands of types (plugin-
heavy or generic-heavy apps) it's a free merge into a single loop.

### 8. Segment-level parallelism has a floor of "number of segments"

`Parallel.For` partitions work by segment. A dump with few, large segments
(some Server GC configs produce one huge segment per GC heap) gets little or
no scan parallelism regardless of core count — the inverse failure mode of
the `MinRecordsPerWorker` handling `HeapIndexScanDispatcher` already applies
for the Phase 2 shared scan. Needs confirmation this is actually hit on real
target dumps before investing here.

## Suggested order of attack

1. **#1** — tier-gated in-memory write path for Small/Medium (narrow, safe,
   fast to validate).
2. **#6** — wire up `InMemoryEntries` end-to-end so Phase 2 scans also skip
   disk for Small/Medium (bigger win, more surface area).
3. **#2** — cap/stream `taskCandidates` / `lohFreeBlockCandidates` (addresses
   a real bounded-memory gap, independent of #1/#6).
4. **#3, #4, #5, #7, #8** — lower priority; validate with profiling/benchmarks
   before investing.
