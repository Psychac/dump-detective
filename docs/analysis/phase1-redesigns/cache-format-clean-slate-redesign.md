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

**What:** A ground-up redesign of `cache.bin`'s largest sections — replace the hash-bucket-sort-
directory edge indices with true CSR (compressed sparse row) graphs, dictionary-encode
`MethodTable` (14,003 distinct types for 14.6M objects on the measured dump), and layer block-level
compression on top of the now-much-smaller, much more regular structures — all while preserving the
memory-mapped random-access point-lookup property that every reader in this codebase depends on.

**Why:** [cache-footprint-comparison.md](../../discrepancy/cache-footprint-comparison.md) measured
this tool's `cache.bin` at 1.37 GB for a 14.6M-object dump, ~5x a comparable tool's cache directory
(271 MB) for the same dump, with 73.4% of the file in the edge indices (57.1%) and dominator tree
(16.3%) alone. The values-only compaction in the superseded doc capped out around ~20% because it
kept the address-keyed group-header-and-directory structure, which doesn't shrink from narrowing
values. This design removes that structure instead of shrinking it.

**Projected impact** (see §7 for full derivation — these are computed from this dump's real,
measured counts, not a formula guess, but the compression-ratio component is an informed estimate,
not a live measurement, and is flagged as such):

| Stage | Size | Cumulative reduction |
|---|---|---|
| Current `cache.bin` | 1,398.3 MB | — |
| + CSR edge indices (§2) | ~889.0 MB | 36.4% |
| + CSR/lazy dominator tree, conservative option (§4) | ~841.2 MB | 39.9% |
| + `MethodTable` dictionary encoding (§3) | ~758.9 MB | 45.7% |
| + block compression on top (§5, estimated 2.5–3x, not measured) | **~250–350 MB** | **~75–82%** |

That final range would be competitive with or better than the other tool's 271 MB — **without**
giving up bounded-memory point lookups the way their full-in-memory-decompress design does. That
last property is the actual point of this whole exercise: get their size without their tradeoff.

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
  [cache-footprint-comparison.md](../../discrepancy/cache-footprint-comparison.md)'s "separate files"
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

1. Resolves every scratch `(a, b)` address pair to `(indexA, indexB)` via
   `ObjectAddressLookup.TryGetNodeIndex` (the same new method the superseded doc already
   scoped — reused here unchanged).
2. Counts out-degree (forward) / in-degree (reverse) per source index — a single pass over the
   already-extracted scratch data, not a second heap walk.
3. Prefix-sums degree counts into `Offsets[count+1]`.
4. Fills `Children[edgeCount]` by source index — a second pass over the same scratch data.

This is exactly the other tool's two-pass CSR technique (`BfsIndexBuilder.BuildPass2`/`BuildPass3`),
but applied to already-captured edge data during the existing post-scan sort phase, not to the heap
itself — so it doesn't add a second `ClrHeap` walk, doesn't touch the main scan's timing, and
doesn't need segment base-offsets to be known mid-scan (the concern that ruled out doing this
inline during the main scan, per the superseded doc's §1).

### 2.3 Index space: full-object vs. reachable-only

Forward CSR indexes into the **full object space** (0..N-1, N = 14,620,162 on the measured dump) —
matches today's scope (every object, reachable or not, per the documented intentional design in
`DiskBackedObjectIndexWriter.cs` lines 807–813).

Reverse CSR indexes into the **reachable-only space** (0..R-1, R = 6,686,490) — matches today's
scope (the reverse index is already GC-root-reachable-only by design; see
[cache-footprint-comparison.md](../../discrepancy/cache-footprint-comparison.md) "Root cause 2").
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
[cache-footprint-comparison.md](../../discrepancy/cache-footprint-comparison.md)):

| | Offsets | Children (int32) | Total | vs. current |
|---|---|---|---|---|
| Forward CSR | (N+1)×4 = 58.48 MB | E_forward×4 = 135.03 MB | **193.5 MB** | 462.4 MB → **58.2% smaller** |
| Reverse CSR | (R+1)×4 = 26.75 MB | E_reverse×4 = 69.47 MB | **96.2 MB** | 336.5 MB → **71.4% smaller** |
| **Combined** | | | **289.7 MB** | 799.0 MB → **63.7% smaller** |

This is a real, computed number from measured counts and known fixed-width record sizes — not a
compression-ratio estimate. It's also meaningfully better than the superseded doc's "Phase 2"
(re-key but still directory-based), because true CSR removes the directory, Phase 2 only narrowed it.

### 2.6 Failure mode and coupling — unchanged from the superseded doc

Whole-section fallback to the current address-based format on any `ObjectAddressLookup` miss during
Phase B, logged via the existing `satelliteWarnings` mechanism — same reasoning as before (a defensive
case that shouldn't trigger for a valid `ClrObject` reference, but must degrade gracefully, not
partially encode). Readers gain the same new coupling to `ObjectAddresses` for index→address
translation on the way out, same as previously scoped.

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
`DominatorChildOffsets`/`DominatorChildAddresses` entirely (75.4 MB combined today). The
dominance-chain-tree UI feature (`MEMORY.md` `project_dominator-p3-3-chain-tree-20260827`) is the
only consumer of the child-list direction; `idom[]` alone is enough to walk *up* from any node, and
a child-list can be derived on-demand by inverting the (now-small, R-sized) `idom[]` array in
memory — O(R) work, done only when a user actually opens the chain-tree UI for a specific node, not
on every build or every query.
- Total dominator: 228.4 MB → ~129.25 MB (**43.4% reduction**)

**Recommendation**: measure how often the chain-tree UI is actually exercised relative to build
frequency before choosing. If it's rare, the aggressive option is a clean win (recomputing an R-sized
inversion on the rare occasions it's needed is cheap). If it's common enough that repeated O(R)
inversions would be noticeable, the conservative option avoids that cost at a smaller (but still
real) disk saving. This doc doesn't have the usage data to decide — flagging the fork, not resolving
it, same discipline as the width-flag boundary case in §2.4 that also can't be validated without
real data at the relevant scale.

## 5. Block-compressed sections — the actual resolution to compression vs. point-lookup

[cache-footprint-comparison.md](../../discrepancy/cache-footprint-comparison.md)'s compression
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

**Honest gap**: the compression ratio here is an *estimate*, not a measurement.
[cache-footprint-comparison.md](../../discrepancy/cache-footprint-comparison.md) measured the other
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

- **Address delta-encoding within a segment.** Objects inside a GC segment are laid out
  contiguously, so `ObjectAddresses[i+1] - ObjectAddresses[i]` is usually a small gap (roughly the
  previous object's size), not a random 64-bit jump. A checkpoint-every-256-records-plus-narrow-
  deltas scheme (the same technique time-series databases use for timestamps) could plausibly shrink
  `ObjectAddresses` well below its current 8 bytes/object. Needs measuring actual real-heap packing
  tightness first — free-list gaps, pinned objects, and dead space between GCs all break the delta
  assumption to varying degrees, and the projected ratio is unknown without that data.
- **Size-via-type-fixed-layout.** Most non-array, non-string types have a constant instance size —
  `ObjectSizes` is redundant with type metadata for that subset. Storing size explicitly only for
  genuinely variable-length instances could be a bigger win than the `MethodTable` dictionary, but
  it's data-dependent (needs checking what fraction of this dump's objects are fixed-size types
  before promising a number) and, unlike `MethodTable` dictionary encoding, interacts with the
  fixed-stride point-lookup story (`ObjectAddressLookup` returns size directly today; a mixed
  fixed/variable scheme needs a per-object "is this a lookup or a type-derived value" flag, adding
  real complexity for an unmeasured payoff).
- **Varint/checkpointed `RetainedBytes`.** Heavy-tailed distribution (most objects retain their own
  small shallow size; a few hub objects retain gigabytes) — same checkpoint-block technique as
  addresses would exploit that, same complexity cost, same "measure the real distribution first"
  caveat.

**Why these aren't in the recommended path**: past CSR + dictionary + block compression, most of
the remaining redundancy these three techniques target is exactly the kind of local statistical
regularity generic block compression (§5) already captures for free. Each one adds real
implementation and testing surface for a return that's likely a shrinking percentage of an
already-much-smaller file — in tension with this project's explicit stance against abstraction
beyond what's needed. Worth revisiting only if a real measurement after §2–§5 ship shows block
compression isn't already capturing most of this.

## 7. Combined projection, fully derived

Starting from the measured 1,398.3 MB `cache.bin`:

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

If §4's aggressive option (don't persist the dominator child-list) is chosen instead, the subtotal
improves to ~707.6 MB (49.4%) before compression.

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
- **New reader coupling.** CSR readers need `ObjectAddresses` open for index→address translation
  (unchanged from the superseded doc); `MethodTable` dictionary readers need the (tiny) dictionary
  section open. Both are required sections already part of the cache-hit fast-path check, so no new
  defensive-fallback complexity, but it is a new dependency between previously-independent readers.
- **Block-compression header/directory design is unspecified here.** §5 describes the *technique*,
  not exact byte layouts (block size, directory record shape, checksum-per-block vs.
  checksum-per-section) — that's real remaining design work, not a detail to gloss over when this
  moves toward implementation.
- **Testing burden scales with the number of new encodings.** Each of CSR, dictionary encoding, and
  block compression needs its own round-trip parity test (compact-encoded output must decode to the
  exact same logical data the current raw format produces) plus its own width/boundary-condition
  tests (dynamic-width flags, same "synthetic only, no real dump at that scale" honesty gap already
  flagged in the superseded doc for `int`/`int64` boundaries).
- **This is a genuinely large undertaking, not a weekend patch.** Even though this doc is framed as
  "clean slate," landing it for real should still happen incrementally (§2 first — the single
  biggest, most isolated win; §3 next — small and independent; §4 after, pending the usage-data
  question; §5 last, since it depends on §2–§4's output shape and needs its own prototype-and-measure
  cycle) rather than as one big-bang rewrite, for the same risk-management reasons any large format
  change would warrant regardless of how "optimal" the target design is on paper.
- **§8 is explicitly excluded from any implementation plan** derived from this doc unless a separate,
  explicit decision is made to revisit `feedback_exact-full-data-no-topn-sampling`. Nothing in §2–§7
  depends on or assumes that decision either way.
