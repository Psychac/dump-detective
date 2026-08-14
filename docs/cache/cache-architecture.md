# Cache Subsystem — Architecture Spec

Ground truth as verified directly against `src/DumpDetective.Analysis/Cache/` and
`src/DumpDetective.Analysis/Indexing/` on `upgrade/clrmd-4`. This is the current-state
reference; unimplemented proposals live in [backlog.md](backlog.md), not here.

For the exact byte-level container layout (headers, TOC entries, section payloads), see
[docs/binary-format.md](../binary-format.md) — not duplicated here. This doc covers the
subsystem architecture above that: the cache facade, sub-caches, writer/reader
orchestration, and the design decisions that constrain how they're built.

## 1. Facade — `HeapAnalysisCache`

`src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs` is a thin facade implementing
`IHeapAnalysisCache`/`IHeapIndexBuilder`. It owns no cache logic itself — it constructs
and delegates to seven single-responsibility sub-caches, each closed over a
`Func<HeapIndexBuildResult?>` that reads back the shared `HeapIndexCache`'s built index:

| Sub-cache | Owns | Notes |
|---|---|---|
| `HeapIndexCache` | The disk-backed object index lifecycle | Drives `DiskBackedObjectIndexWriter.Build`; source of `HeapIndexBuildResult` every other sub-cache closes over |
| `StatisticsCache` | Per-type aggregate stats (`CachedTypeStatistics`) | Hydrates from the `TypeAggregates` section when available; falls back to a live scan otherwise |
| `RootSetCache` | Canonical root set (`RootRecord`) | Reads the `Roots` section when available; falls back to `heap.EnumerateRoots()` |
| `ReverseIndexCache` | Lazy access to the disk-backed reverse (parent-lookup) index | Opens `ReverseEdgeIndexReader` once, reused across every caller |
| `ThreadCache` | Per-thread stack-root counts | |
| `MethodTableCache` | Backs `TypeMetadataCache`'s outgoing-refs check | |
| `TypeMetadataCache` | Immutable `TypeMetadata` per `MethodTable` | `ConcurrentDictionary<ulong, TypeMetadata>`, never evicted |

Each sub-cache exposes `GetMetrics()` → `CacheMetrics` (name, entry count, last build
time/duration, health, last error); `HeapAnalysisCache.GetCacheMetrics()` /
`GetHealth()` aggregate all seven. **No caller consumes either aggregate today** — see
[backlog.md](backlog.md).

Constraints every sub-cache follows (see § 6):
- Never cache `ClrObject` or `ClrType` — only immutable extracted data or addresses/`MethodTable`s.
- Lazy: nothing builds until first request.
- Falls back to a live ClrMD walk on any read/parse error from its disk section — a
  corrupt or missing satellite section degrades a run, it never fails it.

## 2. Disk container — `cache.bin`

One file per dump, written to `.dumpindex/` (or `--cache-dir`). Replaced the previous
nine-file-plus-JSON-sidecar layout. `FormatVersion` 4 (`CacheFileHeader.CurrentFormatVersion`).

- **64-byte `FileHeader`**: magic `"DDCACHE1"`, format version, a `DumpContentHash`
  (dump file length + `XxHash64` over sampled start/middle/end windows — content-addressed,
  so a dump copied to a new path still hits the cache, and a same-path dump silently
  replaced with different bytes doesn't), section count, TOC offset.
- **TOC**: one 32-byte entry per section (`SectionId`, `Offset`, `Length`, `RecordCount`,
  `XxHash32` checksum — checksum is written always, validated lazily on the section's
  first open).
- **Sections** (`CacheSectionId`, values `0`–`17`; `0`/`Objects` unused since v2):
  `TypeAggregates`, `Roots`, `Handles`, `Tasks`, `EventCandidates`, `LargeObjects`,
  `LohFreeBlocks`, `StringDedup`, `StringDedupMeta`, the four columnar object arrays
  (`ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations`), the three
  reverse-index sections (`ReverseEdgeBuckets`/`ReverseEdgeDirectories`/`ReverseEdgeMetadata`),
  and `SegmentIndex`. The last four (reverse index + `SegmentIndex`) are optional and
  purely additive — their absence (skipped via env var, or an older cache) never fails a
  read; callers fall back to a live ClrMD walk or `heap.GetObject`.
- **Write path**: `CacheContainerWriter` writes `cache.bin.tmp`, a zeroed placeholder
  header+TOC, then each section in sequence (checksummed incrementally as bytes are
  written, no re-read pass); `Finish()` patches the real header/TOC and atomically
  renames `.tmp` → `cache.bin`. Any exception during build deletes the `.tmp` file — a
  half-written container is never left in place.
- **Read path**: `CacheContainerReader.TryOpen` validates magic/version and loads the
  TOC (a few hundred bytes) into memory. `TryOpenSection`/`TryOpenSectionAccessor` map
  `cache.bin` via `MemoryMappedFile` and return a section-bounded view — sequential
  readers get a `Stream`, `ObjectAddressLookup` (§ 4) gets a `MemoryMappedViewAccessor`
  it holds open across many point queries.

There is exactly one writer implementation (`DiskBackedObjectIndexWriter`) and one
container format — the old memory-backed writer and 4GB-threshold branch
(`MemoryBackedObjectIndexWriter`, `HeapIndexPrebuildMode`, `--index-mode`) were deleted
outright, not deprecated. Every dump, regardless of size, goes through the same disk
path.

## 3. Writer — `DiskBackedObjectIndexWriter`

`src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs`, ~1140 lines,
single `Build` entry point.

- **Segment scan**: `Parallel.For` over `ClrHeap.Segments`, degree of parallelism
  tiered by dump size (`Math.Min(Environment.ProcessorCount, 8)` for Large, `4` for
  Medium, `2` otherwise) — caps how many segments' pages the DAC's memory-mapped page
  cache holds resident at once. Each segment writes its own columnar scratch file
  (`ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations` arrays);
  scratch files are concatenated into the container **after** the full scan completes
  (not overlapped — see backlog).
- **Per-object work**: `ComputeTypeFlags`, `IsDelegateType`, `IsAsyncStateMachineType`,
  and a single merged `ComputeTypeShapeAndStringFields` pass (one walk over
  `type.Fields`, not two) compute per-type shape once per unique `MethodTable`, not per
  object.
- **String dedup**: `masterStringDedup` (`Dictionary<ulong, StringDedupEntry>`) capped
  at `MaxDedupUnique` (500k) unique entries — bounded by design.
- **Satellite candidates collected during the scan**: `taskCandidates` and
  `lohFreeBlockCandidates` (`ConcurrentBag<...>`, **uncapped** — see backlog),
  large-object candidates (capped at 100, no patch needed).
- **Satellite sections written serially after the scan** (`WriteSatelliteSections`):
  Handles, Roots (unless `DD_SKIP_ROOT_INDEX_BUILD=1`), Tasks, EventCandidates,
  LargeObjects, LohFreeBlocks, StringDedup/StringDedupMeta, reverse index (unless
  `DD_SKIP_REVERSE_INDEX_BUILD=1`), SegmentIndex (unless
  `DD_SKIP_SEGMENT_INDEX_BUILD=1`). Each section is wrapped in its own try/catch — a
  failed section degrades to a logged warning, build continues. **The cache-hit fast
  path only checks that `TypeAggregates` + the columnar `Objects` sections exist and
  match the content hash** — it does not re-verify satellite sections on a later hit
  (a transient write failure silently and permanently downgrades that dump's cache
  until `.dumpindex/` is deleted by hand — see backlog).

## 4. Point lookup — `ObjectAddressLookup` / `SegmentIndex`

Backs `IHeapAnalysisCache.TryGetObjectMetadata(heap, address)` — an
`address → (MethodTable, Size)` lookup used by callers that only have a raw address
(BFS frontier, handle record, reverse-index neighbor) and need type/size without a live
`ClrObject`.

- **Why not a single global binary search**: segment write/concatenation order isn't
  address-sorted (mirrors `heap.EnumerateObjects()`'s own segment-iteration order, kept
  deterministic on purpose — see § 6), so a flat search over the whole `ObjectAddresses`
  column doesn't work. Instead: a small in-memory `SegmentIndexEntry[]` table
  (`Start`, `End`, `FirstRecordIndex`, `RecordCount` — one row per non-empty GC segment,
  segment-count-sized, not object-count-sized) is binary-searched first to find the
  owning segment, then a second binary search runs over just that segment's slice of
  the mmap'd `ObjectAddresses` column.
- Returns `false` — not an error — for addresses that fall between segments (LOH/POH
  gaps, free blocks, padding) or don't land exactly on a record boundary (interior
  pointers are out of scope; see backlog).
- Optional and additive: a missing `SegmentIndex` section (older cache,
  `DD_SKIP_SEGMENT_INDEX_BUILD=1`, aborted satellite write) just means
  `TryGetObjectMetadata` falls back to a live `heap.GetObject` call — no format-version
  bump was needed to add this section.
- Uses bounds-checked `MemoryMappedViewAccessor.ReadUInt64` reads, not the unsafe
  zero-copy pointer pattern `ObjectIndexReader` uses for its hundreds-of-millions-of-
  records sequential batch reads — that complexity isn't justified for
  thousands-of-lookups-per-run point queries.

## 5. Reverse (parent-lookup) index

`src/DumpDetective.Analysis/Indexing/ReverseIndex/` — a real, shipped, disk-backed
index of incoming references, contrary to older docs in this folder that described a
`ReferenceGraphCache` as "not built, not on the roadmap." It is scoped narrowly
(parent lookup, not a general forward+reverse object graph):

- `ReverseEdgeExtractor` + `ReverseEdgeSorter` build hash-partitioned, per-bucket
  sorted edge data during the writer's satellite pass; `ReverseEdgeContainerWriter`
  writes it into the `ReverseEdgeBuckets`/`ReverseEdgeDirectories`/`ReverseEdgeMetadata`
  sections (bucket byte-ranges recorded in the metadata section's JSON, since the
  container TOC has one fixed slot per `CacheSectionId`, not one per bucket).
- Read via `ReverseEdgeIndexReader`, wrapped by `ReverseIndexBackwardReferenceProvider`
  (`IBackwardReferenceProvider`), exposed through
  `HeapAnalysisCache.TryGetReverseIndexProvider()`.
- `ReverseIndexCache` lazily opens and holds one reader (and its memory-mapped views)
  per run — never rebuilt per query, never eagerly built if no analyzer asks for it.
- Skippable via `DD_SKIP_REVERSE_INDEX_BUILD=1`; missing/failed build → `null` provider,
  same "caller falls back to its own strategy" contract as every other optional
  section.
- Full byte format: `docs/analysis/phase1-redesigns/full-reverse-index-plan.md`.

## 6. Traversal — `BoundedGraphWalk`

`src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs` is the single forward-BFS
primitive for the heap object graph, enforcing a **20-depth cap
(`AbsoluteMaxDepth`)** internally regardless of what a caller requests. It replaced
three previously-separate, inconsistently-capped implementations
(`HeapTypePathTraversal`, `BoundedRetainedSizeBfs`, `HeapAnalysisCache.GetRetainedObjects`).
No persisted forward-graph cache exists or is planned — forward edges are always
walked live off `ClrObject.EnumerateReferences`, bounded by node count and depth.

## 7. Governing constraints

Distilled from the sub-caches' actual behavior (superseding the old
`ArchitectureDecisions.md`, most of which described a general object-reference-graph
direction — CSR storage, dense `ObjectId`s, a lazy reverse-of-forward graph — that was
never built; § 5's narrower parent-lookup index shipped instead):

1. **Index-over-live-ClrMD, whenever the index already has the data.** Not a
   per-call-site cost/benefit judgment — once a section holds the answer, resolving it
   via a live `heap.GetObject`/`EnumerateRoots` call is the wrong default even for a
   call site that's low-volume today, because "low volume in the cases profiled so far"
   isn't the same guarantee as "bounded" (a caller that looks cheap on a small dump can
   become the dominant cost on a dump with millions of GC handles or an exception-heavy
   crash). This is why every sub-cache prefers its disk section first and only falls
   back to a live walk on a miss or error.
2. **Never cache `ClrObject` or `ClrType`.** Extract immutable data (addresses,
   `MethodTable`s, `TypeMetadata` records) instead — avoids hidden memory growth and
   long-lived references into ClrMD.
3. **Memory over CPU when the two trade off**, correctness first. A CPU regression
   that meaningfully bounds or reduces peak memory is an acceptable trade; the reverse
   is not.
4. **Every cache has one job.** No god objects — `HeapAnalysisCache` is a pure facade;
   each sub-cache answers exactly one question.
5. **Missing/corrupt optional data degrades, never fails.** Every satellite section
   (reverse index, `SegmentIndex`, task/event/LOH candidates) has an explicit fallback
   path; a build that couldn't write one section still produces a usable cache and a
   correct (if slower) analysis run.
6. **Disk/memory determinism was a real, solved problem — don't reopen it.** Disk-mode
   enumeration order intentionally matches `heap.EnumerateObjects()`'s own segment
   iteration order (not address-sorted) because capped-scan analyzers depend on *which*
   objects populate a partial scan. This is why `ObjectAddressLookup` needs a two-level
   segment-then-record search instead of one flat sorted index, and why a "just
   re-sort everything by address" shortcut was rejected during `SegmentIndex`'s design.

## 8. Known, accepted, intrinsic cost: GC-root enumeration at scale

Confirmed via `dotnet-trace` sampling profiles on both a 3.3GB and a 25GB reference
dump (methodology: `tools/ProfileRootEnumeration`) — **not re-investigate this**:

- Cold index build's GC-root phase (`RootIndexWriter.Write` → `ClrHeap.EnumerateRoots()`)
  is the dominant cost at large scale: 4.80s (24% of a 20.3s total) on a 3.3GB dump vs.
  **169.46s (56% of a 301s total) on a 25GB dump** — a 35x increase against only a
  7.6x dump-size / 7.9x segment-count growth.
- Root cause is **native, not managed code**: ~97% of that time is inside
  `ClrThread.EnumerateStackRoots()` → `DacThreadHelpers.EnumerateStackRoots` (a native
  DAC call), and within that, `DacDataTarget.ReadVirtual` — raw, small, per-frame
  memory reads issued while walking every live thread's stack. This goes through the
  same cached `IDataReader` used for ordinary heap-object reads elsewhere; it is not a
  missed-cache code path, just the sheer volume/overhead of native stack unwinding at
  scale.
- Confirmed **not** the bottleneck (all negligible on both dump sizes, don't touch):
  `RootIndexWriter.Write`'s own record-pack/write logic, `EnumerateAdditionalRoots()`,
  `GetContainingObject`/`FindPreviousObjectOnSegment`, `GetSegmentByAddress`.
- Unattempted mitigation options are tracked in [backlog.md](backlog.md), not here —
  this section is the diagnosis, not the plan.
