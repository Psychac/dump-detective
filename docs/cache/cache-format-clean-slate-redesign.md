# Cache Storage Format — Clean-Slate Redesign

> Supersedes an earlier, narrower doc in this folder (`edge-index-node-index-compaction.md`, removed).
> That doc scoped a values-only compaction bolted onto the *existing* hash-bucket-sort-directory edge
> format and projected ~17.5–21.5% savings on the whole file. This doc asks a bigger question — if the
> format weren't constrained by what's already built, what would the best design actually look like —
> and gets a meaningfully bigger number as a result, because a real CSR eliminates the directory
> overhead entirely instead of just narrowing it. Everything reusable from the old doc (dynamic-width
> flags, the address→index resolver, the whole-section fallback pattern) is carried forward here,
> reframed for the new design.

## Executive Summary (Read This First)

**What:** A ground-up redesign of `cache.bin`'s largest sections — block-level compression of the
point-lookup sections, delta encoding of the sorted address columns, `MethodTable` dictionary
encoding (14,003 distinct types for 14.6M objects on the measured dump), and replacement of the
hash-bucket-sort-directory edge indices with true CSR (compressed sparse row) graphs — all while
preserving the memory-mapped random-access point-lookup property that every reader in this codebase
depends on.

That ordering is the post-measurement one and is deliberately the reverse of how this doc was
originally written, which led with CSR and treated compression as a final polish. See §7.1.

**Why:** [cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md) measured
this tool's `cache.bin` at 1.37 GB for a 14.6M-object dump, ~5x a comparable tool's cache directory
(271 MB) for the same dump, with 73.4% of the file in the edge indices (57.1%) and dominator tree
(16.3%) alone. The values-only compaction in the superseded doc capped out around ~20% because it
kept the address-keyed group-header-and-directory structure, which doesn't shrink from narrowing
values. This design removes that structure instead of shrinking it.

> **⚠ This summary has been superseded in part by measurement.** See
> [cache-redesign-measurements.md](cache-redesign-measurements.md), which measured real
> compression ratios on real section bytes across two dumps and overturned two conclusions
> below (§6's dismissal of delta encoding, and §7's uniform application of compression).
> The revised plan is in §7.1. Sections below are kept as written except where explicitly
> corrected, so the reasoning that led to the measurements stays legible.

> **Units — read this before comparing any two numbers across these docs.** Sizes written "MB" in
> this doc are actually **MiB** (1024²), inherited from
> [cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md)'s convention.
> [cache-redesign-measurements.md](cache-redesign-measurements.md) uses true **MB** (10⁶), because
> that is what the measurement scripts emit. The reference `cache.bin` is the *same file* in both:
> 1,466,259,011 bytes = **1,398.3 MiB = 1,466.3 MB**. Ratios, percentages and timings are
> unaffected; absolute figures differ by 4.9%. Newly-added tables below say "MiB" explicitly.

**Projected impact.** The original estimate, and what measurement returned (all MiB here):

| Stage | Original estimate | Status after measurement |
|---|---|---|
| Current `cache.bin` (reference dump) | 1,398.3 MiB | confirmed exactly, from the TOC |
| + CSR edge indices (§2) | ~889.0 MiB | not yet built — arithmetic still stands |
| + CSR/lazy dominator tree, conservative (§4) | ~841.2 MiB | not yet built |
| + `MethodTable` dictionary encoding (§3) | ~758.9 MiB | not yet built, and **superseded as a size lever** (§7.1) |
| + block compression (§5) | ~250–350 MiB (guessed 2.5–3x) | **228.8 MiB measured (6.1x) — achieved on the *current* format, with no §2–§4 work at all** |

The headline correction: **block compression alone, on the format exactly as it stands today,
measures 1,398.3 → 228.8 MiB (83.6%) on the reference dump, and 9,423.7 → 2,217.0 MiB (76.5%) on a
27.5 GB dump.** That is more than §2–§4 combined (45.7% projected) and requires no structural
change. §5 was ordered last in this doc; on size grounds it should be first. Revised plan in §7.1.

> **⚠ Before any of this: 33% of `cache.bin` is write-only.** The three `ForwardEdge*` sections are
> written every build and have **zero production readers**
> ([measurements § 9](cache-redesign-measurements.md)) — 462.4 MiB on the reference dump, 3,087.8 MiB
> on the 27.5 GB one. Deleting that write is a larger, cheaper and more certain footprint win than
> anything in this document, needs no encoding work, and shrinks the file this design would then
> compress. It should be settled before §7.1 item 1 is scheduled.
>
> **Update: the § 7.2.1 objection has been withdrawn by measurement.** The reverse-edge index turns
> out to serve only 8,851 point lookups per run across 710 distinct 64 KB blocks, so a 16.8 MB block
> cache makes decompression cost ~34 ms (zstd) — not the seconds § 7.2.1 predicted. "Compression
> first" stands for that section. The open risk has moved to `ForwardEdgeBuckets`, which is *larger*
> and whose access pattern is untraced (measurements § 8.1).

228.8 MiB also beats the other tool's 271 MB outright — **without** giving up bounded-memory point
lookups the way their full-in-memory-decompress design does. That property was the actual point of
this exercise: get their size without their tradeoff. The one qualification is §5.1 — the four base
object columns must stay uncompressed to protect the zero-copy streaming path, so the realistic
figure is somewhat above 228.8 MiB and the levers for those columns are dictionary and delta
encoding instead (§7.1 items 2–3).

**Code-side review pass.** §2.2.1, §3.1, §4's precondition, §5.1, §5.2 and the revised §9 caveats
came from reading the current reader/writer implementations rather than the format alone. Two of
them are corrections to things this doc previously asserted as settled: the write-side address
resolver cannot be `ObjectAddressLookup`, and the cache-hit fast path does **not** already validate
the sections this design makes load-bearing. Neither changes the design's direction; both change
its sequencing, and both argue for landing parts of
[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md) first.

**Explicitly flagged, not decided here**: §8 covers the one idea that would change the ceiling by a
large factor rather than a percentage — not persisting full detail for objects no analyzer ever
queries — and why it's in direct conflict with a value this project already locked in
(`feedback_exact-full-data-no-topn-sampling`). That decision belongs to the user, not this doc.

---

## 1. Design Principles

Carried forward from the existing format, non-negotiable for any redesign:

- **Bounded memory, point-lookup access.** Every big reader in this codebase
  (`ReverseEdgeIndexReader`, `ForwardEdgeIndexReader`, `DominatorTreeIndexReader`,
  `DominatorChildIndexReader`, `ObjectAddressLookup`) does raw-pointer random access into
  memory-mapped bytes, not sequential-only reads. A redesign that breaks this (e.g. whole-section
  compression, decompress-into-RAM) trades away a property this project's own rules
  ("never materialize... into memory," "works on 10GB+ dumps without crashing") explicitly require,
  and that the other tool's design doesn't have to satisfy.
- **Node-order stability.** Node index must remain heap-scan (segment-iteration) order, not
  address-sorted — an existing, deliberate constraint (`docs/binary-format.md` § SegmentIndex:
  "disk-mode enumeration order intentionally matches `heap.EnumerateObjects()`'s own segment-
  iteration order... because capped-scan analyzers depend on *which* objects populate a partial
  scan"). Nothing in this redesign changes what "node index" means, only what's stored per index.
- **Single atomic container, one content-hash validity check.** Reaffirmed from
  [cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md)'s "separate files"
  discussion: this project already migrated away from a multi-file layout for real correctness
  reasons (no partial staleness across independently-validated files, one atomic tmp-then-rename).
  Nothing here reopens that. All of the techniques below are per-*section* metadata within the
  existing `cache.bin` container, the same idiom `HandleSnapshot.bin`'s version-gated record size
  already uses.
- **Exact data, no silent caps.** Every technique below is a *re-encoding* of the same information
  (same objects, same edges, same exact counts) — not a sampling or truncation scheme. §8 is called
  out separately specifically because it's the one idea that crosses this line.

## 2. True CSR for both edge directions (replaces hash-bucket-sort-directory entirely)

### 2.1 What changes and why it's bigger than narrowing values

Today's format: `[key: 8-byte address][count: 4][pad][values: N × 8-byte address]` per unique
key, plus a directory `[key: 8][offset: 8]` per unique key, so a query does two binary searches
(directory, then within-bucket). The superseded doc's Phase 1 narrowed the *values* to 4-byte node
indices but kept this shape — meaning the group-header-and-directory overhead (which is
address-keyed and doesn't shrink) stayed, capping savings around ~20–24%.

A true CSR removes the directory concept entirely: once an address is resolved to its dense node
index (one binary search via `ObjectAddressLookup`, which today's design *also* requires as a
first step for any caller starting from an address), "children of node X" becomes
`Children[Offsets[X]..Offsets[X+1]]` — a direct array slice, no second binary search, no per-key
header. This is smaller **and** faster than today, not a size/speed tradeoff.

### 2.2 Where this fits in the existing pipeline — smaller change than it sounds

This does **not** require restructuring the main per-segment parallel scan into a multi-pass
pipeline (unlike the other tool's `BfsIndexBuilder`, which assigns node indices before it can
extract anything). Phase A (raw `(parent, child)`/`(child, parent)` address-pair extraction into
scratch buckets — `ForwardEdgeExtractor`, `ReverseEdgeExtractor`, and the reachability-walk-driven
reverse extraction) is **unchanged**, because none of it needs node indices to run. What changes is
Phase B: instead of sorting each bucket by key and writing grouped-data-plus-directory, Phase B
now:

1. Resolves every scratch `(a, b)` address pair to `(indexA, indexB)`.
2. Counts out-degree (forward) / in-degree (reverse) per source index — a single pass over the
   already-extracted scratch data, not a second heap walk.
3. Prefix-sums degree counts into `Offsets[count+1]`.
4. Fills `Children[edgeCount]` by source index — a second pass over the same scratch data.

This is exactly the other tool's two-pass CSR technique (`BfsIndexBuilder.BuildPass2`/`BuildPass3`),
but applied to already-captured edge data during the existing post-scan sort phase, not to the heap
itself — so it doesn't add a second `ClrHeap` walk, doesn't touch the main scan's timing, and
doesn't need segment base-offsets to be known mid-scan (the concern that ruled out doing this
inline during the main scan, per the superseded doc's §1).

#### 2.2.1 The resolver cannot be `ObjectAddressLookup` — use the scratch-file pattern instead

An earlier draft of this doc said step 1 would call `ObjectAddressLookup.TryGetNodeIndex`,
"reused unchanged" from the superseded doc. **That cannot work**, and the reason is
structural rather than a detail to patch later.

`ObjectAddressLookup.TryOpen` reads the *finished* container: it calls
`SegmentIndexWriter.ReadRecords(containerPath)` and then `CacheContainerReader.TryOpen`,
both of which require a complete header + TOC
([ObjectAddressLookup.cs:53-85](../../src/DumpDetective.Analysis/Indexing/ObjectAddressLookup.cs#L53-L85)).
Phase B runs *before* `CacheContainerWriter.Finish()` patches the real header and TOC into
place, so at Phase B time there is nothing valid to open.

This exact problem was already hit and already solved elsewhere in the build. Stage B's
dominator work needs per-node `MethodTable`/`Size` at the same point in the pipeline, and
resolves it through `ScratchFileObjectMetadataLookup` — which memory-maps the retained
per-segment scratch columns directly, precisely because (quoting
`BuildAndPersistDominatorTree`'s own doc comment) `cache.TryGetObjectMetadata` is
"unusable before `CacheContainerWriter.Finish` writes a complete TOC." It already exposes a
sorted-batch `ResolveBatch` path for exactly this access pattern.

**Revised step 1**: extend the `ScratchFileObjectMetadataLookup` pattern to return a node
index (it already binary-searches the same per-segment address ranges to find a record), and
resolve Phase B's address pairs through that. Consequences worth planning for:

- The Address/MethodTable/Size scratch files must be retained past Phase B, not deleted after
  concatenation. Mechanism already exists — `ConcatenateScratchFiles`' `deleteAfterCopy: false`
  parameter, added for Stage B's benefit — but the deletion point now has to account for a
  second consumer, and today's deletion is scattered across several `catch` blocks.
- Phase B's resolution and Stage B's metadata resolution want the same lookup over the same
  files. Building one shared instance is preferable to two, and is the write-side analogue of
  the read-side `CacheSession` proposed in
  [cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md) § 6.1.

`ObjectAddressLookup` is still the right resolver for the *read* side (index→address and
address→index at query time, against a finished container). It is only the write-side use
that has to change.

### 2.3 Index space: full-object vs. reachable-only

Forward CSR indexes into the **full object space** (0..N-1, N = 14,620,162 on the measured dump) —
matches today's scope (every object, reachable or not, per the documented intentional design in
`DiskBackedObjectIndexWriter.cs` lines 807–813).

Reverse CSR indexes into the **reachable-only space** (0..R-1, R = 6,686,490) — matches today's
scope (the reverse index is already GC-root-reachable-only by design; see
[cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md) "Root cause 2").
Sizing `Offsets` to R instead of N avoids ~7.93M essentially-empty rows (the unreachable objects)
costing 4 bytes each for nothing — a small (~31.7 MB) but free additional saving from choosing the
right index space per direction, not a new capability.

### 2.4 Dynamic width — unchanged from the superseded doc

Two independent flags, same reasoning as before, carried forward unchanged:

- `FullObjectIndexWidth` — `int32` unless N > `int.MaxValue`, governs forward `Children`.
- `ReachableIndexWidth` — `int32` unless R > `int.MaxValue`, governs reverse `Children`.

Both stored per-section, same TOC-metadata idiom `HandleSnapshot.bin` already uses.

### 2.5 Measured projected size

Using this dump's real counts (N=14,620,162, R=6,686,490, E_forward=33,757,072,
E_reverse=17,367,740 — all confirmed identical across both tools' extraction pipelines, see
[cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md)):

| | Offsets | Children (int32) | Total | vs. current |
|---|---|---|---|---|
| Forward CSR | (N+1)×4 = 58.48 MB | E_forward×4 = 135.03 MB | **193.5 MB** | 462.4 MB → **58.2% smaller** |
| Reverse CSR | (R+1)×4 = 26.75 MB | E_reverse×4 = 69.47 MB | **96.2 MB** | 336.5 MB → **71.4% smaller** |
| **Combined** | | | **289.7 MB** | 799.0 MB → **63.7% smaller** |

This is a real, computed number from measured counts and known fixed-width record sizes — not a
compression-ratio estimate. It's also meaningfully better than the superseded doc's "Phase 2"
(re-key but still directory-based), because true CSR removes the directory, Phase 2 only narrowed it.

### 2.6 Failure mode and coupling — unchanged from the superseded doc

Whole-section fallback to the current address-based format on any resolver miss during
Phase B, logged via the existing `satelliteWarnings` mechanism — same reasoning as before (a defensive
case that shouldn't trigger for a valid `ClrObject` reference, but must degrade gracefully, not
partially encode). Readers gain the same new coupling to `ObjectAddresses` for index→address
translation on the way out, same as previously scoped.

**New, and not previously noted: `SegmentIndex` gets promoted from optional to load-bearing.**
Address→index resolution — on either side, scratch-file or finished-container — depends on the
per-segment `(Start, End, FirstRecordIndex, RecordCount)` table, because within-segment address
monotonicity is what makes the binary search valid; there is no global address ordering to fall
back on (`ObjectAddressLookup`'s own remarks, and cache-architecture.md's "why a naive global
binary search doesn't work"). Today `SegmentIndex` is explicitly optional: it is skipped entirely
under `DD_SKIP_SEGMENT_INDEX_BUILD=1`, and its write is wrapped in the same abort-and-warn block
every other satellite section uses, so a transient failure just drops it. Under this design that
same transient failure silently downgrades the single largest format win to the legacy
address-based encoding. Either `SegmentIndex` becomes a required section with its own hard failure
path, or the fallback needs to be loud rather than a `satelliteWarnings` string nobody reads.

## 3. `MethodTable` dictionary encoding

Not part of the superseded doc (it only touched edge/dominator sections, not base columns). This
dump has 14,620,162 objects but only **14,003 distinct types** (`TypeAggregates` record count).
`ObjectMethodTables` currently stores the full 8-byte `MethodTable` pointer per object (111.5 MB)
for a column with 14.6M/14,003 ≈ 1,044x redundancy.

**Design**: a small `MethodTable → TypeId` dictionary (14,003 entries — the same information
`TypeAggregates` already indexes, this reuses that existing structure rather than duplicating it),
plus a narrow, dynamically-widened `TypeId` per object (1 byte if ≤255 types, 2 bytes if ≤65,535,
4 bytes beyond that — same width-flag idiom as §2.4, guarding against an unrealistic but
checkable ceiling rather than assuming 2 bytes is always enough). At 14,003 types, 2 bytes suffices
today with real headroom (up to 65,535).

**Size**: 14,620,162 × 2 bytes = 29.24 MB, down from 111.5 MB — **saves 82.26 MB, a 73.8%
reduction on this one column**, for zero information loss (the dictionary is exact, not
approximate) and zero change to point-lookup semantics (`TypeId` is still fixed-width, still an
O(1) indexed read — a caller resolves `TypeId → MethodTable` via one extra dictionary lookup, which
is tiny — 14,003 entries fits trivially in memory, unlike the per-object arrays this whole exercise
is about).

### 3.1 Read-path constraints this imposes

"One extra dictionary lookup, which is tiny" is true per call and misleading in aggregate: the
lookup lands in the hottest loop in the codebase. `ObjectIndexReader.ZeroCopyColumnReader.FillBatch`
materializes `HeapEntry` records with a raw `Unsafe.ReadUnaligned<ulong>` per column per object,
deliberately — the class exists because bounds-checked accessor reads were too slow "at the
hundreds-of-millions-of-records scale." Every object enumerated by every consumer passes through it.

Three concrete requirements follow, none of them hard, all of them cheap to get wrong:

- **The dictionary must be a flat `ulong[]` indexed by `TypeId`, not a `Dictionary<int, ulong>`.**
  A dense array keeps `FillBatch` to an extra bounds-checked array index per object; a hash lookup
  would put hashing in that loop. At 14,003 entries the array is ~112 KB — load it once per session.
- **`TryOpenColumns`' record-count cross-validation has to be reworked.** It currently derives the
  record count from `addrLen / 8` and requires `mtLen / 8`, `sizeLen / 8`, and `genLen / 1` to agree
  ([ObjectIndexReader.cs:123-127](../../src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs#L123-L127)).
  A 2-byte `TypeId` column breaks that identity, and the check is load-bearing — it is what
  currently catches a truncated or mismatched column set.
- **The dictionary section becomes a hard dependency of the base object columns.** Today the four
  columns are self-describing; afterwards, `ObjectMethodTables` is meaningless without its
  dictionary. That is a new required-section relationship, not an optional-satellite one.

**Opportunity, not requirement**: most consumers of `EnumerateIndexedEntries` don't want the
`MethodTable` value — they filter it against a candidate set (`AsyncStateMachineAnalyzer`,
`TimerLeakAnalyzer`, `EventLeak/PublisherRegistry`, `WeakReferenceAnalyzer`). Those callers could
compare 2-byte `TypeId`s directly and never resolve to a `MethodTable` at all, which is both
narrower and faster than today. That pairs naturally with the column-projection API in
[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md) § 6.3,
and is a reason to sequence that work before this.

## 4. Dominator tree — narrow values, or don't persist the child-list at all

The dominator tree's per-row columns (`ReachableAddresses`, `RetainedBytes`) already store real
values (an address, a byte count) that can't be dictionary- or index-encoded — they aren't
redundant with anything else. Only `ImmediateDominatorAddresses` (a *reference to another row*) and
`DominatorChildAddresses` (a *list of references to other rows*) are index-shaped, same as the edge
graph.

Unlike the edge indices, the dominator child-list is **already** stored in CSR shape
(`DominatorChildOffsets` is already a narrow `int32` offset array — there was never a
directory/hash-bucket structure here to remove) — so this section only needs value-narrowing, not a
structural change:

**Conservative option** — narrow both to row-indices (dynamic width, `ReachableIndexWidth` from
§2.4, reused unchanged):
- `ImmediateDominatorAddresses`: 51.0 MB → R×4 = 26.75 MB (saves 24.25 MB)
- `DominatorChildAddresses`: 49.4 MB → (dominator-tree edge count)×4 ≈ 25.88 MB (saves 23.56 MB)
- Total dominator: 228.4 MB → ~180.6 MB (**21.0% reduction**)

**Aggressive option** — keep `ImmediateDominatorAddresses` narrowed, but stop persisting
`DominatorChildOffsets`/`DominatorChildAddresses` entirely (75.4 MB combined today). `idom[]` alone
is enough to walk *up* from any node, and a child-list can be derived on-demand by inverting the
(now-small, R-sized) `idom[]` array in memory — O(R) work, done only when something actually needs
the child direction, not on every build or every query.
- Total dominator: 228.4 MB → ~129.25 MB (**43.4% reduction**)

> **⚠ CORRECTED — the consumer named here was the wrong one.** This paragraph originally said the
> dominance-chain-tree UI (`MEMORY.md` `project_dominator-p3-3-chain-tree-20260827`) is the only
> consumer of the child-list direction. It is not a consumer at all: `DominatorAnalyzer`'s chain
> detection walks *upward* via `TryGetImmediateDominator`. The child list's only production consumer
> is `IDominatorTreeProvider.EnumerateRetainedSet`, called only from `StaticRootLeakDetector` to
> build a candidate root's per-type/per-namespace retained breakdown. See
> [cache-redesign-measurements.md](cache-redesign-measurements.md) § 13.3.

> **✅ PRECONDITION VERIFIED (2026-09-06).** The paragraph below asked for the writer to be checked
> before the aggressive option could be costed. It has been:
> `DiskBackedObjectIndexWriter`'s per-row loop iterates every one of the *n* rows, and its
> `newId < 0` branch writes a folded leaf's folding-parent address rather than skipping the row, so
> `DominatorImmediateDominatorAddresses` does cover every node the child list covers. Inverting
> `idom[]` reproduces the persisted child list exactly. The remaining open item is not correctness
> but frequency — see the corrected consumer above and
> [cache-redesign-measurements.md](cache-redesign-measurements.md) § 13.3.

**Precondition the aggressive option depends on, found in review.** "Invert the `idom[]` array"
is only equivalent to the persisted child list if `idom[]` covers every node the child list
covers. `DominatorChildIndexBuilder.Build` does *not* invert `idom[]` — it merges **two** edge
sources into each row: the real dominator-tree edges (`tree.ChildOffsets`/`ChildTargets`) and the
folded leaves (`fold.FoldedLeafOffsets`/`FoldedLeafOldIds`, §D8/§10.5). So on-demand derivation
reproduces the current data only if `DominatorImmediateDominatorAddresses` carries a row for every
folded leaf as well as every unfolded node. It is row-aligned with `DominatorReachableAddresses`,
which suggests it does — but this needs confirming against the writer before the aggressive option
is costed, because if folded leaves are absent from `idom[]`, the option silently changes what the
chain-tree UI shows rather than just moving where it is computed. Note also that the derivation is
**new code**, not existing code relocated: the current builder consumes Lengauer-Tarjan output, not
a persisted `idom[]`.

**Recommendation**: count how many static-root candidates a real run pushes through
`EnumerateRetainedSet` before choosing. If it's a handful, the aggressive option is a clean win —
one R-sized inversion, reused across all of them. If it's frequent enough that the ~51 MiB of
resident `int[]` the inversion needs at 6.69M rows would sit live for most of a run, that is a
bounded-memory cost the conservative option avoids at a smaller (but still real) disk saving. This
doc doesn't have that count — flagging the fork, not resolving it, same discipline as the width-flag
boundary case in §2.4 that also can't be validated without real data at the relevant scale.

## 5. Block-compressed sections — the actual resolution to compression vs. point-lookup

[cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md)'s compression
discussion hit a real wall: whole-section Brotli (what the other tool does, and what this project's
`cache.bin` currently does *not* do at all) makes a section only readable start-to-end, breaking
every point-lookup reader in this codebase. That's a binary choice — compress and lose bounded-memory
random access, or don't compress — only if compression is applied to the *whole section*.

**Design**: compress in fixed-size blocks (e.g. 64 KB decompressed per block), with a small
block-offset directory (one entry per block — for a 289.7 MB CSR structure at 64 KB/block, that's
~4,500 blocks, a directory of ~36 KB, trivially small and easily fully loaded into memory even
though the data it points to isn't). A point lookup:

1. Resolves address → node index (unchanged, existing `ObjectAddressLookup`).
2. Computes which block contains that index's data (index × record-width ÷ block-size — for a
   dense fixed-width CSR array this is a direct calculation, not a search, since record position is
   already known once the index is known).
3. Decompresses just that one ~64 KB block (fast — Brotli/zstd decompress in the tens-of-MB/s to
   GB/s range depending on level, so 64 KB is sub-millisecond) and reads the target record from it.

This is not a novel idea — it's the same technique columnar/random-access formats designed for both
properties already use (Parquet's page-level compression, RocksDB's block-based SSTables). It gets
most of generic compression's ratio while keeping the same bounded-memory, no-full-decompress
property this project's rules require. The other tool doesn't have this option available to it
because it never built point-lookup access into its compressed structures in the first place — it
made the opposite tradeoff (full in-memory decompression) instead.

### 5.1 Point lookup and zero-copy streaming are two different properties — this preserves one

§1's principle is stated as "bounded memory, point-lookup access," and §5 preserves point lookup.
But the base object columns' dominant access pattern is not point lookup — it is **zero-copy
sequential streaming**, and block compression is incompatible with it by construction.

`ZeroCopyColumnReader` acquires raw pointers into the mapped view and reads records with
`Unsafe.ReadUnaligned` directly off the page cache — no copy, no decode
([ObjectIndexReader.cs:177-228](../../src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs#L177-L228)).
If the section's bytes are compressed, there is nothing to point at; every pass must decompress
into a buffer first. That is the fastest path in the codebase, and it is used by roughly nine
call sites, whereas the genuine point-lookup consumer of those same columns —
`ObjectAddressLookup` — deliberately does *not* use it (it takes bounds-checked `ReadUInt64`
reads instead, since it does thousands of lookups, not hundreds of millions of reads).

The consequence is that §5's target should be chosen per section, by access pattern:

| Section group | Dominant access | Block compression |
|---|---|---|
| Edge indices (CSR), dominator tree | Point lookup / bounded traversal | **Good fit** — this is the case §5 is designed for |
| Base object columns (`ObjectAddresses`/`TypeId`/`Sizes`/`Generations`) | Full-column streaming via `HeapEntry` | **Measure first** — trades a known-hot zero-copy loop for disk |

Streaming a block-compressed column isn't obviously *worse* — decompressing 64 KB blocks in order
reads fewer bytes from disk, and modern zstd decompresses in the GB/s range per core — but it is a
CPU-for-I/O trade against a loop that is currently near-free, and on a warm page cache the I/O
saving is zero while the CPU cost is not. This has to be measured, not assumed. It is the one place
where the format redesign and the read-path redesign genuinely constrain each other.

### 5.2 Block framing also fixes the whole-section checksum problem

A useful synergy rather than a cost. Today `CacheContainerReader` verifies a section's *entire*
XxHash32 before returning any view of it, on every open, with no memoization — so a point lookup
into a 117 MB section faults in and hashes all 117 MB (see
[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md) § 1).
That behaviour is already the largest avoidable cost on the read side, and it would make §5
pointless if carried forward unchanged: decompressing one 64 KB block is worthless if opening the
section still hashes every compressed byte first.

So §9's "checksum-per-block vs. checksum-per-section" is **not** an open detail to settle later —
per-block checksums are a precondition for §5 delivering anything. Adopting them resolves both
problems at once: verification becomes proportional to what is actually read, for compressed and
uncompressed sections alike.

### 5.3 Measured — this section's estimate has been replaced with data

[cache-redesign-measurements.md](cache-redesign-measurements.md) § 2 measured independent per-block
compression on real section bytes from two dumps. Results, for the sections that dominate the file:

| Section | Raw MiB (ref) | zstd9 @ 64 KB | Penalty vs. 32 MB contiguous |
|---|---:|---:|---:|
| `ForwardEdgeBuckets` | 345.3 | 4.69x | **−4.0%** (block beat contiguous) |
| `ReverseEdgeBuckets` | 234.5 | 4.51x | 5.3% |
| `ForwardEdgeDirectories` | 117.1 | 4.95x | 0.2% |
| `ReverseEdgeDirectories` | 102.0 | 5.27x | 0.5% |

The block penalty this doc never quantified is **0.2–5.3% on the sections that are 57–78% of the
file**, and negative in two cases — 64 KB windows suit this data's local regularity better than a
32 MB window does. §5's central premise is confirmed by data, not merely argued. 64 KB is the right
default: 16 KB is consistently worse, and 256 KB helps only the hyper-compressible base columns
that shouldn't be compressed anyway. zstd-3 is within ~5% of zstd-9 on the edge sections, so the
cheap level suffices where the bytes actually are.

**Original honest gap, retained for the record**: the compression ratio here was an *estimate*, not
a measurement.
[cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md) measured the other
tool's Brotli ratios directly (5.1x on their edge graph, 12.8x on their dominator index) — but that
was on *their* data shape (raw addresses, sparse `idom` sentinels for unreachable nodes). Our
post-CSR, post-dictionary data is differently shaped (dense monotonic offsets, low-cardinality type
IDs, narrow indices) — plausibly still compresses well (offset arrays are monotonic and
delta-friendly; dictionary-encoded `TypeId`s are extremely low-cardinality), but the 2.5–3x figure
used in the executive summary's projection is an informed guess, not a measurement, and should be
validated on a real, small-block prototype before being treated as a real number.

## 6. Further headroom considered, not recommended as a first cut

Raised in discussion, real but higher-complexity-per-byte-saved than everything above — listed here
so they aren't re-derived from scratch later, not because they're endorsed for immediate work:

- **Address delta-encoding within a segment.** ✅ **MEASURED AND PROMOTED — §10.** Objects inside a
  GC segment are laid out contiguously, so `ObjectAddresses[i+1] - ObjectAddresses[i]` is usually a
  small gap (roughly the previous object's size), not a random 64-bit jump. A checkpoint-plus-narrow-
  deltas scheme (the same technique time-series databases use for timestamps) was the guess here;
  the packing tightness this bullet asked to measure first has now been measured on both real caches
  ([measurements](cache-redesign-measurements.md) § 13.2) and the answer is 55.66 MiB / 331.63 MiB
  with zero overflow blocks. Spec in §10.
- **Size-via-type-fixed-layout.** Still unmeasured, and now largely moot: this bullet's premise was
  that `ObjectSizes`' redundancy has to be attacked through type metadata. It doesn't. The column's
  actual problem is that it spends 8 bytes on a value whose measured maximum is 23.3 MB — 25 bits —
  across both real dumps. Plain width narrowing with an escape (§10) captures 83.6 MiB / 498.1 MiB
  without a per-object "lookup or derive" flag, without depending on what fraction of the heap is
  fixed-size types, and without touching the fixed-stride point-lookup story. Type-derived sizes
  would have to beat *that* residue, not the original 8 bytes, which is a much worse trade than this
  bullet assumed.
- **Varint/checkpointed `RetainedBytes`.** Heavy-tailed distribution (most objects retain their own
  small shallow size; a few hub objects retain gigabytes) — same checkpoint-block technique as
  addresses would exploit that, same complexity cost, same "measure the real distribution first"
  caveat.

**⚠ CORRECTED BY MEASUREMENT.** The paragraph that stood here argued these were "exactly the kind
of local statistical regularity generic block compression (§5) already captures for free."
[cache-redesign-measurements.md](cache-redesign-measurements.md) § 3 measured it, and that claim is
false for sorted address columns by a factor of 4–5:

| Section | zstd9 alone | delta + zstd9 | Further gain |
|---|---:|---:|---:|
| `ObjectAddresses` (reference dump) | 6.57x | **35.60x** | **5.42x** |
| `ObjectAddresses` (27.5 GB dump) | 5.38x | **22.83x** | **4.24x** |
| `DominatorReachableAddresses` | 6.21x | **31.80x** | **5.12x** |
| `DominatorChildAddresses` | 4.31x | 9.27x | 2.15x |
| `DominatorImmediateDominatorAddresses` | 7.96x | 8.40x | 1.06x |
| `DominatorRetainedBytes` | 22.78x | 17.72x | **0.78x — actively worse** |

Sortedness explains and predicts the split cleanly: sorted columns gain ~5x, the unsorted `idom`
column gains nothing, and the heavy-tailed `RetainedBytes` column is harmed. Revised positions:

- **Delta encoding on sorted address columns moves into the recommended path** (`ObjectAddresses`,
  `DominatorReachableAddresses`). It matters most precisely where compression *can't* be used —
  `ObjectAddresses` is a streamed base column (§5.1), so delta is the only lever available to it,
  and a fixed-width or checkpointed delta stays compatible with the zero-copy read path in a way
  compression is not. Checkpoint interval must be chosen so `ObjectAddressLookup`'s binary search
  stays O(log n) plus a bounded scan.
- **Varint/checkpointed `RetainedBytes` (third bullet) is dropped.** Compression alone already
  reaches 24.69x there, and the delta transform makes it worse.
- **Size-via-type-fixed-layout (second bullet) remains unmeasured** and stays out of the
  recommended path, unchanged.

## 7. Combined projection, fully derived

> **⚠ SUPERSEDED — kept as the record of the original derivation, not as the current plan.**
> The §2–§4 subtotal (~758.9 MiB, 45.7%) is still valid arithmetic on real counts. Everything
> involving §5 in this section rests on the 2.5–3x guess, which measurement replaced with 6.1x
> against the *current* format — making the compression row's placement here (applied last, on top
> of §2–§4 output) the wrong shape entirely. **Use §7.1.**

| Step | Section(s) affected | Before | After | Saved |
|---|---|---|---|---|
| §2 CSR edge indices | Forward+Reverse edge index | 799.0 MB | 289.7 MB | 509.3 MB |
| §4 dominator (conservative) | Dominator tree | 228.4 MB | 180.6 MB | 47.8 MB |
| §3 `MethodTable` dictionary | `ObjectMethodTables` (part of base columns) | 111.5 MB | 29.24 MB | 82.26 MB |
| **Subtotal, computed exactly** | | **1,398.3 MB** | **~758.9 MB** | **~45.7%** |
| §5 block compression, estimated | Everything above (now much smaller, more regular) | 758.9 MB | ~250–350 MB* | ~54–67% further |
| **Total, if compression estimate holds** | | **1,398.3 MB** | **~250–350 MB** | **~75–82%** |

\* The §2–§4 subtotal (~758.9 MB, ~45.7% reduction) is arithmetic on real counts and known
record widths — solid. The final ~250–350 MB range depends on §5's unmeasured compression-ratio
estimate and should be treated as directional, not committed to, until a real prototype measures it.

**The §5 row over-applies, per §5.1.** It compresses "everything above" uniformly, but the base
object columns (~266.3 MB of the ~758.9 MB subtotal, after §3 narrows `ObjectMethodTables`) are
streamed through the zero-copy read path, not point-queried, so compressing them is a measured
trade rather than a free win. If §5 is applied only to the sections whose access pattern actually
suits it — the CSR edge indices and dominator tree, ~470.3 MB of the subtotal — the compressed
total lands nearer **~420–460 MB** (~67–70% off current) rather than ~250–350 MB. The larger
number remains available *if* measurement shows compressed streaming of the base columns is
acceptable. Both figures inherit §5's unmeasured ratio estimate; the point of splitting them is
that the narrower one doesn't also stake a behavioural change on an unmeasured assumption.

If §4's aggressive option (don't persist the dominator child-list) is chosen instead, the subtotal
improves to ~707.6 MB (49.4%) before compression.

## 7.1 Revised plan, post-measurement

Measurement reorders this doc substantially. The techniques are all still sound; their *priority*
was wrong, because §5 was costed with a guess (2.5–3x) that came in low, and §2–§4 were costed
exactly. Corrected ordering:

**1 — Block compression of point-lookup sections (§5), on the current format.** Biggest measured
win by a wide margin (83.6% / 76.5% whole-file), needs no structural change to CSR or dictionaries,
and is independent of every other item here. Requires per-block checksums (§5.2), which
simultaneously fixes the largest measured problem on the read side — the 68.8 ms per-open
whole-section verify, 197% of the scan it gates
([measurements](cache-redesign-measurements.md) § 5). One change, both wins.

**2 — Delta encoding on sorted address columns (§6, revised).** 4.2–5.4x on `ObjectAddresses` and
`DominatorReachableAddresses`. Necessary specifically because those columns can't be compressed
(§5.1), so nothing else reaches them.

**3 — `MethodTable` dictionary encoding (§3).** Keep it — but for access-pattern reasons, not size.
Compression would get 57.25x on that column versus dictionary encoding's 4x, so as *size* levers
they are substitutes and §7's table above double-counts them. Since the base columns must stay
uncompressed to protect the 10.49 GB/s streaming path, dictionary encoding is the right tool there
and §5 simply doesn't apply to that column.

**4 — CSR edge indices (§2).** Still worth doing, and worth more than this doc claimed: directory
overhead alone is 219.1 MiB on the reference dump and **1,992.4 MiB on the 27.5 GB dump**, and the
edge index grows from 57.1% to 77.6% of the file with scale. But compressed CSR's marginal gain
over compressed-current-format is unmeasured, CSR is by far the largest implementation item, and it
carries the §2.2.1 resolver rework and the §2.6 `SegmentIndex`-becomes-required decision. It also
makes queries faster, which compression alone does not — that, more than size, is now its argument.

**5 — Dominator tree narrowing (§4).** Smallest and most conditional; unchanged.

The one number still missing is the run-level multiplier on § 5's per-open cost — how many times a
real run opens each section. That needs an instrumented cache-hit run against a real dump; see
[cache-redesign-measurements.md](cache-redesign-measurements.md) § 6.

## 7.1.1 Ordering with compression deliberately deferred (2026-09-06)

§7.1 orders by measured size-per-unit-of-work and puts compression first. That ordering stands on
the numbers, but compression is the one item that introduces a third-party codec dependency (there
is no compression library in the repo at all today), a block-framing change to every point-lookup
section, and a read-path behavioural change. The decision taken here is to **land the encoding work
first and hold compression for last**, on the grounds that the encoding levers are the ones
compression can never reach anyway (§5.1 rules it out for the streamed base columns) and are
therefore not wasted work under either ordering.

The resulting sequence, each step its own `CurrentFormatVersion` bump per [measurements
§10.2](cache-redesign-measurements.md):

| Version | Contents | Saves | % of 852.4 MiB |
|---|---|---:|---:|
| **v6** | Base + sorted-column narrowing: `ObjectSizes` width, `ObjectAddresses` block-delta, `DominatorReachableAddresses` block-delta, plus the §3 section manifest as a rider (§10) | **164.7 MiB** | **19.3%** |
| v7 | Dominator: aggressive or conservative (§4), once the `EnumerateRetainedSet` frequency count exists | ~98 or ~46 MiB | 11.5% / 5.4% |
| v8 | CSR edge indices (§2) | ~245 MiB | 28.7% |
| v9 | Block compression + per-block checksums (§5) | remainder | — |

Compression's own arithmetic is unaffected by going last: it applies to whatever the file is at
that point, and the three v6 columns are excluded from it either way.

## 7.2 Design scrutiny — three problems with the plan above

Found by pressure-testing §7.1 rather than by measurement. The first is serious enough to
qualify the headline recommendation.

### 7.2.1 ⚠ WITHDRAWN BY MEASUREMENT — this section's objection was ~1000x too pessimistic

> **Read this before the argument below.** The concern was measured directly and does not hold. On
> the reference dump a full run makes **8,851** `TryGetParents` calls, not "potentially millions",
> and they touch only **710 distinct 64 KB blocks** (18.9% of the section) at **12.1 touches per
> block** — locality is *good*, not defeated by hash-scattering. A 16.8 MB LRU block cache yields an
> **87.9% hit rate**, putting decompression at **34 ms with zstd** against the "3–14 seconds" below.
> Full trace and LRU simulation in [cache-redesign-measurements.md](cache-redesign-measurements.md)
> § 8. **Compressing the reverse-edge index is viable**, and §7.1's "compression first" ordering
> stands unqualified for that section.
>
> Two things the measurement did *not* settle, and they now matter more than this section did:
> whether the 710-block working set holds on the 27.5 GB dump (12x larger reverse section), and
> whether **`ForwardEdgeBuckets`** — 33% of both measured files, *larger* than the reverse index — is
> streamed rather than point-queried, in which case § 5.1's 22x zero-copy penalty applies to it and
> it must not be compressed. See measurements § 8.1.
>
> The reasoning below is kept as the record of what was argued and why it was wrong: it assumed one
> parent lookup per BFS node over ~100,000 nodes, and inferred poor locality from the bucket hash
> without checking the working-set size.

#### Original argument (superseded)

§5.1 asked "streaming or point lookup?" and concluded: compress the point-lookup sections. It never
asked the follow-up question — **how many point lookups do those sections serve, and with what
locality?** The answer undermines the recommendation.

The reverse-edge index's consumer is `IndexBackedBidirectionalSearch`, which calls
`TryGetParents(node, …)` once per node in a BFS, plus `DominatorAnalyzer`, `ReferenceChainAnalyzer`,
and `CollectionAnalyzer`. Root-path finding over a large heap is potentially millions of lookups.

Today a lookup is a binary search over mmap'd bytes — effectively a page touch. Under block
compression it becomes: locate block, read compressed block, **decompress 64 KB**, then search.
At the measured 0.47 GB/s (brotli-5) or an optimistic ~2 GB/s (zstd), that is 32–140 µs per block.
A BFS touching 100,000 nodes with one uncached block each costs **3–14 seconds of pure
decompression** against roughly zero today.

Locality does not rescue it: the current format assigns edges to buckets by *hash* of the child
address, deliberately scattering them, so consecutive BFS nodes land in unrelated blocks. A
decompressed-block LRU would need to hold a large fraction of ~47,000 blocks (3 GB / 64 KB) to help.

Nor does the cold-cache argument: today a cold random lookup faults one 4 KB page; compressed it
reads a ~12 KB block *and* decompresses 64 KB. Compression wins on bytes-read only for sequential
access, which is the pattern §5.1 already ruled out for the base columns.

**Revised position.** Block compression remains the largest disk-footprint lever by a wide margin
and that measurement stands. But "compression first" was justified on size alone, and the runtime
cost on the edge index is unmeasured and plausibly severe. Before §7.1 item 1 is treated as
approved, one of these must happen:

- measure `TryGetParents` call volume and block-hit-rate under a real root-path workload; or
- restrict compression to sections that are *not* randomly point-queried at high volume — which,
  given §5.1 already excludes the streamed base columns, may leave little (the dominator columns
  are row-indexed and largely sequential, so they are the plausible survivors); or
- accept compression as a cold-storage format with an explicit decompress-on-open step for the
  edge index, which reintroduces exactly the bounded-memory tradeoff §1 forbids.

This does not invalidate §5. It moves it from "obvious first win" to "biggest size lever, with an
unresolved runtime question that must be answered before sequencing."

### 7.2.2 §5's "direct calculation, not a search" holds only for CSR, not for today's format

§5 step 2 says locating a record's block is "a direct calculation, not a search, since record
position is already known once the index is known." That is true for a **fixed-width dense array**
— i.e. after §2's CSR lands. It is *not* true for the current hash-bucket-sort-directory format,
where bucket payloads are variable-length and a query already does two binary searches.

Since the measured 83.6% was obtained against the *current* format, compressing it there requires
an additional binary search over a per-section block directory to map a byte offset to
(block, offset-within-block). Still workable, but it is a third search layer, and §5 currently
implies the lookup path stays as simple as it is today. This also compounds 7.2.1.

### 7.2.3 §3's dictionary and §7.1's ordering interact more than stated

§7.1 lists dictionary encoding (item 4) after delta encoding (item 3), both applying to base
columns. But `ObjectMethodTables` narrowing to a 2-byte `TypeId` changes the record stride that
`TryOpenColumns`' cross-column consistency check depends on (§3.1), and `ObjectAddresses` delta
encoding changes how `ObjectAddressLookup.FindRecord` binary-searches that column. Both touch the
same reader, and the second is the more invasive: a delta-encoded column is no longer directly
binary-searchable without checkpoints. They should be designed together as one change to the base
column layout and its reader, not sequenced as two independent items.

---

## 8. The one idea that changes the ceiling, not the constant — flagged, not decided

Everything in §2–§6 is a *re-encoding* of the same exact information — same object count, same edge
count, same retained-byte values, just represented more compactly. There's a structurally different
lever available: **don't build full per-object detail for objects no analyzer ever actually
queries.** Most heaps are dominated by simple leaf objects (short strings, boxed primitives, small
POCOs with no outgoing references) that never appear in a root-path, retention, or leak-candidate
query. Reducing *record count* for that subset would move the ceiling by a large factor, not a
percentage — bigger than everything in this doc combined.

This is **not** recommended as part of this design, and is called out separately rather than folded
into §6, because it's in direct, explicit conflict with `feedback_exact-full-data-no-topn-sampling`
in this project's memory: this project deliberately removed every top-K/capped-sample pattern it
used to have so results are exact, not "probably complete." Any "skip objects nothing queries"
scheme is a sampling/tiering decision by another name, even framed principled ("skip zero-outgoing-
ref leaf types" rather than a blunt top-N cap) rather than arbitrary. This doc's job is to surface it
honestly, not to quietly omit the biggest lever because it's inconvenient, and not to silently
decide a philosophical tradeoff that isn't this doc's call to make.

## 9. Caveats — full list

- **Format version bump.** Every technique in §2–§4 is a breaking on-disk change.
  `CacheFileHeader.CurrentFormatVersion` needs a real bump (4→5 at minimum, likely higher once §5's
  block-compression header shape is finalized); old `cache.bin` files fail the version check and
  rebuild — consistent with every prior format change, no compatibility shim needed or wanted.
- **Phase B write-time cost.** Both the CSR conversion (§2) and dictionary encoding (§3) add
  per-value/per-object work (an `ObjectAddressLookup`/`MethodTable→TypeId` lookup each) to a phase
  that's already doing a full pass over the data — real, not assumed free, needs a before/after
  timing pass, same discipline as the superseded doc already called for.
- **New reader coupling, and the fast path does not currently cover it.** CSR readers need
  `ObjectAddresses` open for index→address translation (unchanged from the superseded doc);
  `MethodTable` dictionary readers need the (tiny) dictionary section open; both resolvers need
  `SegmentIndex` (§2.6). An earlier version of this caveat claimed these are "required sections
  already part of the cache-hit fast-path check, so no new defensive-fallback complexity."
  **That is wrong.** `TryLoadFromCache` validates exactly two sections — `ObjectAddresses`'
  TOC record count and `TypeAggregates` — out of 25 written
  ([DiskBackedObjectIndexWriter.cs:1684-1704](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L1684-L1704)).
  `ObjectMethodTables` is not checked; `SegmentIndex` is not checked and is optional by design.
  So this design does add defensive-fallback surface unless the fast-path check is broadened first —
  which is [cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)
  § 6.2, and is a reason to sequence that work ahead of this one.
- **Write-side resolver must not be `ObjectAddressLookup`** (§2.2.1) — it opens a finished
  container, which does not exist during Phase B. Reuse the `ScratchFileObjectMetadataLookup`
  pattern, and plan the scratch-file retention/deletion lifetime for two consumers rather than one.
- **Per-block checksums are a precondition for §5, not an open detail** (§5.2). Block size and
  directory record shape remain genuine design work, but the checksum-granularity question is
  already decided by what §5 is for.
- **Block compression is incompatible with the zero-copy streaming read path** (§5.1), which is the
  base object columns' dominant access pattern. Applying §5 to those columns is a measured trade,
  not a free win, and §7's headline range assumes otherwise.
- **Testing burden scales with the number of new encodings.** Each of CSR, dictionary encoding, and
  block compression needs its own round-trip parity test (compact-encoded output must decode to the
  exact same logical data the current raw format produces) plus its own width/boundary-condition
  tests (dynamic-width flags, same "synthetic only, no real dump at that scale" honesty gap already
  flagged in the superseded doc for `int`/`int64` boundaries).
- **This is a genuinely large undertaking, not a weekend patch.** Even though this doc is framed as
  "clean slate," landing it for real should still happen incrementally rather than as one big-bang
  rewrite. The code-side review revises the ordering the earlier draft proposed:
  1. **Broaden the cache-hit fast-path validation first** (implementation doc § 6.2). Every step
     below adds a required section; shipping them against a fast path that validates 2 of 25 means
     a dropped section is invisible until something misbehaves much later.
  2. **§5 block compression** — the biggest measured win (83.6% / 76.5%), and it does **not**
     depend on §2–§4's output shape: it was measured against the current format. Needs per-block
     checksums (§5.2), which also fix the read side's largest problem. Restricted to the
     point-lookup sections per §5.1.
  3. **§6 delta encoding of sorted address columns** — 4.2–5.4x, and the only lever available to
     the base columns since §5.1 rules compression out for them.
  4. **§3 dictionary encoding** — small and independent; sequence the column-projection API
     (implementation doc § 6.3) alongside it, since that is what turns the `TypeId` indirection
     from a cost into a win for the filter-by-type consumers.
  5. **§2 CSR** — the largest implementation item, carrying the §2.2.1 scratch-file resolver work
     and the §2.6 `SegmentIndex`-becomes-required decision. Its remaining argument after
     compression is query speed and the elimination of ~1,992 MiB of directory overhead at 27.5 GB
     scale, not raw size.
  6. **§4 dominator** — pending both the usage-data question and the folded-leaf precondition.

  An earlier version of this list put §2 first and §5 last, on the assumption that compression was
  a final polish over already-shrunk structures. Measurement inverted that.
- **§8 is explicitly excluded from any implementation plan** derived from this doc unless a separate,
  explicit decision is made to revisit `feedback_exact-full-data-no-topn-sampling`. Nothing in §2–§7
  depends on or assumes that decision either way.

---

## 10. Format v6 — narrow columns with escapes (specified, 2026-09-06)

The v6 batch from §7.1.1. All three levers are the same primitive applied three ways, so they share
one encoder, one decoder, and one set of tests. Sizing and the distribution evidence behind every
width choice below are in [cache-redesign-measurements.md](cache-redesign-measurements.md) § 13.2.

### 10.1 The shared primitive

A **narrow column with escapes** is a fixed-stride array of *w*-byte little-endian values plus a
sorted side table of the values that don't fit:

- The all-ones value at width *w* (`0xFFFF` at 2 bytes, `0xFFFFFFFF` at 4) is the **escape
  sentinel**, never a real value.
- The **overflow table** is a sorted `(uint32 recordIndex, uint64 value)` array, 12 bytes per entry,
  in its own section. Record indices fit in `uint32`: the largest real dump measured has 87.1M
  objects.
- **Streaming decode** (`ZeroCopyColumnReader.FillBatch`) keeps a cursor into the overflow table and
  advances it in record order — O(1) amortized, no search, one predictable compare per record.
- **Point decode** (`ObjectAddressLookup`) binary-searches the overflow table only on a sentinel
  hit — a few thousand entries, so ~12 probes, on a path that already does two binary searches.

Fixed stride is preserved, which is the property §5.1 protects: `base + i * w` still addresses
record *i* directly, so the zero-copy streaming path and the mmap'd binary search both survive.

### 10.2 `ObjectSizes` — width 2, absolute values ✅ SHIPPED (format v6)

> Landed as specified: 111.54 → 27.89 MiB, 3,843 escaped records, `cache.bin` 852.4 → 768.8 MiB.
> See [cache-redesign-measurements.md](cache-redesign-measurements.md) § 14.

Sizes are stored as-is, not scaled: 13.3% of them on the reference dump and 20.6% on 21-04 are not
multiples of 8, so `size / 8` is lossy. The measured maximum across both dumps is 23.3 MB, and the
escape rate at 2 bytes is 0.026–0.037%.

The width is **chosen by the writer, not hard-coded**, from a running histogram maintained during
the heap scan (three counters — values ≥ 2¹⁶−1, ≥ 2³²−1, and the max — merged per segment like the
type aggregates already are). The writer picks the *w* ∈ {2, 4, 8} minimising
`n·w + escapes·12`, subject to an escape rate below 1% so the hot-loop branch stays predictable and
the table stays small. On both real dumps that picks 2. A dump full of giant arrays picks 4 or 8 and
degrades to today's behaviour rather than to a pathological side table.

The width is a writer decision but it does **not** need to be stored: the TOC already carries each
section's `Length` and `RecordCount`, so `w = Length / RecordCount` recovers it unambiguously, the
same way §3's `TypeId` width is recovered from the dictionary's record count. That also gives the
fallback for free — a column the writer couldn't narrow is simply written at `w = 8` and read as
today's plain column, with no flag anywhere.

### 10.3 `ObjectAddresses` — 4-byte block delta, unscaled

The stored value is `address − blockBase` at width 4, with one base per **N = 1024 records**.
Measured escape rates: **16 records of 14.6M** on the reference dump (0.00011%, all of them at the
one 3.94 GB inter-segment gap) and **zero of 87.1M** on 21-04.

Scaling the delta by 8 was considered and rejected. Both dumps are entirely 8-byte-aligned, and
scaling would widen a block's reach from 4 GB to 34.4 GB and take the reference dump's 16 escapes to
zero — but a 32-bit dump's addresses are 4-byte aligned, and there every second record would escape.
The measured cost of not scaling is 16 records; the cost of scaling on an unmeasured but entirely
real dump class is half the column. Unscaled is bitness-agnostic and needs no alignment check in
either the writer or the reader.

The block base array is a separate section, loaded into memory at open (it is small enough that
mmap'ing it would buy nothing) — 114 KB at 14.6M objects, 664 KB at 87.1M. Decode is
`base[i >> 10] + delta[i]`, O(1) for both access paths; the checkpoint interval is a power of two
precisely so record → block is a shift, not a search. N is a format constant, not a stored
parameter: changing it is a version bump, which this format already has a mechanism for.

**The encoding does not assume the column is sorted.** A descending step produces a delta that
doesn't fit and escapes. Global monotonicity happens to hold on both dumps and is recorded in the
measurements, but nothing here depends on it, and after dropping the scaling there is no alignment
assumption left either — the encoding is total over `ulong`.

### 10.4 `DominatorReachableAddresses` — 4-byte block delta

Same primitive, same encoding, same N: 6,686,490 rows, 6,530 blocks, **1,246 escaped records**
(0.019%), **25.44 MiB saved**. It is binary-searched by `DominatorScalarReader`, so it needs the
same point-decode path as `ObjectAddresses` and gets it from the shared primitive for free.

### 10.5 Section manifest — the rider

[measurements §10.2](cache-redesign-measurements.md) argues cheap breaking changes should ride the
next bump rather than pay for their own. v6 carries one: a manifest section listing the sections
the writer *intended* to write, so a lost conditional section (`Handles`, `Tasks`, the edge indices,
the dominator sections) is detectable on the cache-hit path. Today the TOC lists only sections that
were successfully closed, so a section lost to a transient write failure leaves nothing to diff
against — the open remainder of the fast-path-validation item in [backlog.md](backlog.md).

This rider is purely opportunistic: §§10.2–10.4 need nowhere to store encoding parameters, because
widths derive from the TOC and N is a format constant. The manifest rides v6 only because it is a
breaking change that would otherwise buy its own bump, and it is independent enough to be dropped
from the batch without touching the three columns.

### 10.6 What v6 does not do

No structural index changes (that's §2, v8), no dominator child-list decision (that's §4, v7), no
compression or block framing (§5, v9), and no change to `ObjectMethodTables`, which format v5
already narrowed, or to `ObjectGenerations`, whose remaining 10.4 MiB is not worth breaking fixed
stride for.
