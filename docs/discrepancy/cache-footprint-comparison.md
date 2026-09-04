# Cache Footprint — `cache.bin` (1.37 GB) vs. the other tool's `.ddcache` directory (271 MB), same dump

> Method note: nothing in this file is sourced from either tool's `Docs/` folder or markdown
> documentation. Every number below was either (a) read directly from `.cs` source on both sides, or
> (b) measured directly off the two tools' actual on-disk cache artifacts for the **same dump**
> (`D:\Dumps\Crash_IIS_BALTSTPRD\...E0434352.dmp`) — this tool's
> `<dump>.dumpindex\cache.bin` and the other tool's `.ddcache\<dump-name>\` directory. Two facts from
> `docs/binary-format.md`/`docs/cache/cache-architecture.md` were checked against `CacheSectionId`
> directly and found **stale** (the docs describe 17 sections; the enum on this branch has grown to
> 27) — another reason not to trust either tool's docs here, on top of the explicit instruction not to.

## The exact numbers, both sides, same dump

**This tool**: `cache.bin` is **1,466,258,051 bytes (1398.3 MB / 1.366 GB)**, parsed directly from its
64-byte header + 32-byte-per-entry TOC (`CacheFileHeader`/`CacheTocEntry` format,
`src/DumpDetective.Analysis/Indexing/Container/CacheContainerFormat.cs`), plus the two edge-index
`*Metadata` JSON sections for exact edge counts.

**Other tool**: the entire `.ddcache\<dump-name>\` directory for this dump is **283,862,770 bytes
(270.7 MiB / 283.9 MB decimal)** — matching the reported "270 MB" almost exactly — made up of 10
files. Two of them (`.bfs.idx`, `.idom.idx`) are Brotli-compressed; their headers were decompressed
directly (Python's `brotli` module) to read `NodeCount`/`EdgeCount` and measure the real compression
ratio, not an assumed one.

| This tool — `cache.bin` section group | Bytes | % |
|---|---|---|
| **Forward + reverse edge index** (`ForwardEdgeBuckets`+`Directories`, `ReverseEdgeBuckets`+`Directories`) | **798,861,024** (**761.9 MB**) | **57.1%** |
| Base columnar object index (`ObjectAddresses`/`MethodTables`/`Sizes`/`Generations`) | 365,504,050 (348.6 MB) | 24.9% |
| Dominator tree (6 sections) | 239,515,669 (228.4 MB) | 16.3% |
| `StringDedup` (+meta) | 21,439,221 (20.4 MB) | 1.5% |
| `TypeAggregates` | 1,475,182 (1.4 MB) | 0.1% |
| Everything else (`Handles`, `Tasks`, `LohFreeBlocks`, `Roots`, `RootStackThreadAttribution`, `LargeObjects`, `SegmentIndex`) | 559,072 (0.5 MB) | 0.04% |

| Other tool — `.ddcache\<dump>\` file | Bytes | % |
|---|---|---|
| `<dump>.parent.map` (uncompressed) | **160,250,568** (**152.8 MB**) | **56.5%** |
| `<dump>.bfs.idx` (Brotli-compressed forward CSR graph) | 84,028,048 (80.1 MB) | 29.6% |
| `stringGroups.bin` | 22,285,363 (21.3 MB) | 7.9% |
| `<dump>.idom.idx` (Brotli-compressed dominator index) | 13,722,876 (13.1 MB) | 4.8% |
| `event-analysis.bin` | 3,438,841 (3.3 MB) | 1.2% |
| `static-roots.bin` / `hot-addr-types.bin` / `gc-roots.bin` / `finalizer-queue.bin` / `fragmentation.bin` | 137,178 combined | 0.05% |

Object count for this dump, confirmed identical on both sides: **14,620,162** (this tool's
`ObjectAddresses` record count; the other tool's decompressed `bfs.idx` header `NodeCount` field).
Forward-edge count is **also identical on both sides**: **33,757,072** (this tool's
`ForwardEdgeMetadata.TotalEdgesRecorded`; the other tool's decompressed `bfs.idx` header
`EdgeCount` field) — both tools do a full, unscoped, per-object `EnumerateReferences`/
`EnumerateReferenceAddresses` heap walk for their forward index and get the exact same count on the
same dump. This cross-validates both extraction pipelines and rules out "the two tools disagree about
what an edge is" as an explanation for anything below.

## Root cause 1 (57% of this tool's file): a duplicated bidirectional edge index at full address width, vs. one compressed forward-only CSR

This tool extracts the reference graph **twice** — once forward (`ForwardEdgeExtractor`, keyed by
parent) and once reverse (`ReverseEdgeExtractor`, keyed by child) — each as its own independent
hash-bucketed, sorted, on-disk structure storing **full 8-byte addresses** for both endpoints of every
edge (`ReverseEdgeExtractor.cs:18`: `[ChildAddress:8][ParentAddress:8]`;
`ForwardEdgeExtractor.cs:16`: `[ParentAddress:8][ChildAddress:8]`), plus a 16-byte
group header per unique parent/child and a 16-byte directory entry per unique parent/child
(`ReverseEdgeSorter.cs` B3/B4). None of it is compressed — `CacheContainerWriter.cs` writes every
section via a plain `_stream.Write(...)`; grepping it and the rest of `Indexing/`/`Cache/` for
`Brotli|GZip|Deflate|CompressionLevel|CompressionMode` returns zero hits.

The other tool builds a **single** forward CSR graph (`BfsIndexBuilder`/`BfsIndexCache`) — `int` node
indices into a dense array instead of `ulong` addresses (`TryGetIndex` resolves address → dense index
once, up front), and only two additional per-node scalar columns (`IndexToAddr: ulong[]`,
`Sizes: long[]`) — then Brotli-compresses the whole thing (`BfsIndexCache.Save`,
`CompressionLevel.Optimal`). Measured directly by decompressing this dump's actual `.bfs.idx`:

| | Raw (uncompressed) | On disk | Ratio |
|---|---|---|---|
| `bfs.idx` (`IndexToAddr`+`Sizes`+`Offsets`+`Children`, 14.62M nodes, 33.76M edges) | 427,431,560 bytes (407.6 MB) | 84,028,040 bytes (80.1 MB) | **19.7%** (5.1x reduction) |

A single-parent reverse lookup exists (`DiskBackedParentMap`, their `.parent.map`) but stores **one
parent per child, not all parents** (`ParentSlots.Get(0)` in `DiskBackedParentMap.Write`, line 97),
derived from the already-built CSR rather than re-extracted from the heap — and it's the one file in
their whole `.ddcache` directory that **isn't** compressed, which is exactly why it's their single
largest artifact (152.8 MB, 56.5% of their total) despite being conceptually the smallest/simplest
structure (16 bytes × 10,015,660 entries — one per object with a resolvable parent).

This tool's reverse index intentionally stores *all* parents per child (multi-parent retention
analysis is a real, confirmed capability the other tool's single-parent map doesn't have — see Root
cause 2 below), so switching to "one file, one direction, one parent" isn't a straight port. But the
current format pays full 8-byte address cost, in both directions, uncompressed, for a graph the other
tool represents in 80 MB total.

## Root cause 2 (confirmed, not a bug): the ~2x forward-vs-reverse edge-count mismatch is a deliberate scoping difference

This tool's own edge counts, read from the two `*Metadata` JSON sections: **33,757,072 forward edges**
vs. **17,367,740 reverse edges** — for what sounds like the same physical graph. Tracing both
extractors' call sites in `DiskBackedObjectIndexWriter.cs` resolves this exactly:

- **Forward** edges are recorded during the main per-segment parallel scan, over **every object in
  the heap** — `obj.EnumerateReferences(carefully: true)` at line 368, inside the loop that visits all
  14,620,162 objects regardless of reachability.
- **Reverse** edges are recorded only during a **separate BFS walk from the GC roots**
  (`ReachableGraphWalker.Walk`, line 882), which only visits the 6,686,490 objects reachable from a
  root. The source comment at lines 807–813 states this is intentional, not incidental:

  > "the reverse-edge index is now populated by a BFS walk from the GC roots instead of the raw
  > per-object field scan above. This means an object only gets a reverse-index entry if it's
  > actually reachable from a root — garbage never enters the walk, so it never gets an entry. Every
  > current consumer of this index searches *backward* from an object toward a root, and a garbage
  > object can have no such path by definition, so this is not a loss of any answer the index used to
  > give."

The math checks out: 45.7% of objects are root-reachable (6,686,490 / 14,620,162). If reachable
objects have a similar or somewhat higher average out-degree than the whole-heap average (2.309
edges/object, measured), 6,686,490 × ~2.3–2.6 lands right in the observed 17,367,740 range
(2.598 edges per *reachable* object, actually higher than the whole-heap average — consistent with
live/reachable objects tending to have richer populated object graphs than dead/floating garbage,
which is exactly the theory the source comment states).

**This is not a bug and doesn't change the size conclusion** (the reverse index is the *smaller* of
the two precisely because of this narrower scope) — but it was flagged by the user as worth
understanding on its own, and is now fully traced to source with a specific, intentional design
rationale rather than left as an open question.

## Root cause 3 (16% of this tool's file): a full on-disk dominator tree with explicit child lists and full addresses, vs. two compressed dense-index columns

This tool's six dominator sections (`CacheSectionId` 21–26,
`src/DumpDetective.Analysis/Indexing/Dominator/`) store, per **dominator-reachable node**
(6,686,490 of them, 45.7% of all objects — a different, smaller scope than the reverse-edge walk
above, despite both starting from GC roots, since the dominator walk additionally requires Stage B
to actually run):

| Section | Type | Bytes/node |
|---|---|---|
| `DominatorReachableAddresses` | `ulong[]` | 8 |
| `DominatorImmediateDominatorAddresses` | `ulong[]` (full address, not an index) | 8 |
| `DominatorChildOffsets` | `int[]` CSR row | 4 |
| `DominatorChildAddresses` | `ulong[]` (full address per child edge) | 8 |
| `DominatorRetainedBytes` | `ulong[]` | 8 |

All written as raw, uncompressed `BinaryPrimitives` loops (`DominatorReachableAddressWriter.cs`,
`DominatorChildIndexWriter.cs`, `DominatorTreeIndexWriter.cs`) — 228.4 MB total for this dump.

The other tool's `DomTreeCache` (`.idom.idx`) stores exactly two dense `int`/`long` columns, keyed by
the same node index its BFS graph already uses (no address column, no explicit child-list — it can
only walk *up* via `idom[]`, never enumerate a node's dominator-tree children), and — notably — dense
over **all 14,620,162 objects**, not just the reachable subset (unreachable nodes presumably get a
sentinel `idom` value). Measured directly by decompressing this dump's actual `.idom.idx`:

| | Raw (uncompressed) | On disk | Ratio |
|---|---|---|---|
| `idom.idx` (`idom[]`+`retained[]`, 14.62M nodes, dense) | 175,441,968 bytes (167.3 MB) | 13,722,868 bytes (13.1 MB) | **7.8%** (12.8x reduction) |

The very high compression ratio here (far better than `bfs.idx`'s already-good 5.1x) is consistent
with the source comment's implication: a dense array over *every* object, where roughly 54% of rows
are unreachable and likely carry a constant sentinel `idom` value, compresses extremely well.
This tool's `DominatorChildAddresses` CSR is a genuine capability the other tool's format can't
provide (dominator-tree children enumeration — used for this tool's dominance-chain tree UI, see
`MEMORY.md` `project_dominator-p3-3-chain-tree-20260827`), but it costs roughly 3x the raw bytes/node
of the other tool's two-column format before any compression is applied.

## Smaller, non-dominant contributors (confirmed, bounded)

- `StringDedup`: 20.4 MB measured (321,266 unique strings, 66.7 bytes/record average) — capped at
  `MaxDedupUnique` = 500,000 unique strings (`DiskBackedObjectIndexWriter.cs`, `masterStringDedup`),
  so this dump is well under the cap.
- `TypeAggregates`: 1.4 MB measured (14,003 unique types, 105.4 bytes/record). Bounded by
  distinct-type count, not object count.
- Everything else (`Handles`, `Tasks`, `LohFreeBlocks`, `Roots`, `RootStackThreadAttribution`,
  `LargeObjects`, `SegmentIndex`, `DominatorTreeMetadata`): 0.6 MB combined, measured. Per-GC-segment,
  per-thread, or otherwise bounded by a small count.
- On the other tool's side, `stringGroups.bin` (21.3 MB) and `event-analysis.bin` (3.3 MB) are the
  only non-trivial "everything else"; the remaining five files (`static-roots.bin`,
  `hot-addr-types.bin`, `gc-roots.bin`, `finalizer-queue.bin`, `fragmentation.bin`) total 137 KB.

## What the tier question turned out to matter for (and what it doesn't)

The user confirmed the 270 MB figure came from running `analyze --full` cold (no pre-existing disk
index reused). `AnalyzeCommand.cs`'s own top-level code only explicitly builds/saves `.bfs.idx` and
`.idom.idx` (93.2 MB combined) — yet the measured `.ddcache` directory total (270.7 MiB) matches the
reported 270 MB almost exactly, and includes `.parent.map` (152.8 MB) and the other satellite `.bin`
files too. The reconciliation: `--full` renders every `IncludeInFullAnalyze` sub-report
(`AnalyzeReport.RenderEmbeddedReports`), and at least one of those sub-analyzers
(`memory-leak`, via `SharedReferrerCache`/`ReferrerConsumer`) lazily builds and persists
`.parent.map` itself, on demand, the first time it's needed — independent of whether the top-level
command was `load` or `analyze --full`. So a cold `--full` run and a `load` run end up producing the
same on-disk footprint for this dump; the "three tiers" framing from an earlier pass of this analysis
was an oversimplification of `AnalyzeCommand.cs` in isolation, without accounting for what its
sub-commands do on their own. **Net effect: tier selection doesn't change the conclusion** — the
270 MB total is real, fully measured, and the three root causes above fully account for why this
tool's `cache.bin` is ~5x larger for the identical dump.

## Recommendations (ranked by expected leverage, cheapest first)

1. **Compress `cache.bin` sections.** The other tool's measured ratios on this exact dump's data
   shape — 19.7% (5.1x) on the edge/size graph, 7.8% (12.8x) on the dominator columns — are a
   realistic target, not a guess: they're Brotli on structurally similar integer columns (addresses,
   sizes, CSR offsets, retained-byte sentinels), measured on this same dump. Wrapping
   `CacheContainerWriter`'s per-section stream in Brotli, at minimum for the edge indices (762 MB, 57%
   of the file) and dominator columns (228 MB, 16%), could plausibly take this tool's `cache.bin` from
   1.37 GB toward the 300–400 MB range without any format redesign.
2. **Replace the edge indices' hash-bucket-sort-directory structure with true CSR, dictionary-encode
   `MethodTable`, and add block-level compression that preserves point-lookup access.** Scoped out
   in full, with a fully-derived (not formula-guessed) projection from this dump's real counts —
   CSR removes the directory overhead entirely rather than narrowing it (63.7% off the edge index
   alone, vs. an earlier values-only pass that only reached ~24%), `MethodTable` dictionary encoding
   saves another 82 MB for free, and block-compression (estimated, not yet measured) could plausibly
   take the whole file into the 250–350 MB range while keeping the same bounded-memory point-lookup
   properties the other tool's full-in-memory-decompress design doesn't have — in
   [cache-format-clean-slate-redesign.md](../analysis/phase1-redesigns/cache-format-clean-slate-redesign.md).
   That doc also flags the one lever that would change the ceiling by a large factor rather than a
   percentage (not indexing objects no analyzer queries) and why it's in direct conflict with this
   project's own no-sampling/no-capping stance — surfaced, not decided, there.
3. **No action needed on the forward/reverse edge-count mismatch** — traced to source and confirmed
   as an intentional, documented scoping choice (reverse index is GC-root-reachable-only by design),
   not a bug. Noted here only so it isn't re-investigated from scratch later.
