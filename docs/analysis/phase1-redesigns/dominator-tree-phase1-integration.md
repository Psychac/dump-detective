# Building the exact dominator tree during Phase 1 index build

## Status at a glance

**Stage A (§3-§4): shipped, complete.**
- The reachability walk builds the reverse-edge index directly as its own byproduct (§4, §7) — the
  old raw-per-object-scan build of that index is gone.
- `DenseIdMap` → `Dictionary<ulong,int>`/`HashSet<ulong>` (§4.1), and the reverse-edge index's
  `MaxParentsPerChild` cap removed entirely (§4.2).
- Walk successors default to `ForwardEdgeLooseFileReader` (§4.3); `DD_FORCE_LIVE_CLRMD_WALK=1`
  forces the live-ClrMD fallback.
- `DominatorReachableAddresses` persisted and queryable via
  `IHeapAnalysisCache.TryGetReachableAddressProvider()` (§5). `DominatorReachableInDegree` was
  deliberately not built — redundant with the reverse-edge index's `EnumerateChildCounts`.

**Stage A.5: not needed.** See §7.

**Stage B (§3, §5, §9): not started.** `idom[]`/Lengauer-Tarjan computation still runs in Phase 2 via
`DominatorAnalyzer`'s own in-memory `ReachableGraphBuilder`, independent of everything above.

## Terminology, fixed for the rest of this doc

- **Dominator child index** — parent → children in the *dominance* tree (`idom[]` inverted).
  Answers "what would freeing X free?" (`EnumerateRetainedSet`, `TryGetRetainedBytes`). Stage B,
  not built yet.
- **Reverse-edge index** — child → parents in the raw object graph, the disk-backed structure
  `ReverseEdgeExtractor`/`ReverseEdgeSorter`/`ReverseEdgeContainerWriter`/`ReverseEdgeIndexReader`
  build. Uncapped (§4.2) and, as of §7, fed by the reachability walk rather than a raw per-object
  heap scan. There is no separate "walk reverse index" — it *is* this structure.

---

## 1. What this supersedes

- **[analysis-profile-removal-plan.md §10a](../../refactor/analysis-profile-removal-plan.md#10a-b2-design-the-dominator-tree-retention-provider)'s
  in-memory cache-provider idea (B2)** — wrong: it coupled analyzer execution order (whichever ran
  first paid the build cost) and held 1.5-3GB resident for the rest of the pipeline run. A disk-backed
  index, opened independently per analyzer, avoids both.
- **[dominator-tree-lengauer-tarjan.md §D7](dominator-tree-lengauer-tarjan.md)'s "defer the append
  problem" stance** — wrong premise, not just a stale conclusion. D7 assumed the tree gets computed in
  Phase 2, *after* `cache.bin` is already finalized, and deferred solving `CacheContainerWriter`'s
  write-once limitation as a result. But everything the tree needs — GC roots, forward edges,
  per-object shallow sizes — already exists *inside* the Phase 1 index-build job, before the container
  closes (§2). There's no append problem if the tree is built before `Finish()` is ever called. D7's
  on-disk format (§5) and its already-implemented `DominatorTreeIndexWriter`/`DominatorTreeIndexReader`
  are inherited into this design.

---

## 2. Where it slots into the existing Phase 1 job

[DiskBackedObjectIndexWriter.Build](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs)
is the single orchestrator for the whole Phase 1 build. Shipped order:

1. Columnar object scan — `ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations`.
2. `WriteSatelliteSections` — `Handles`, **`Roots`** (`RootIndexWriter.Write`), `Tasks`, `LargeObjects`,
   `LohFreeBlocks`.
3. **Forward-edge index Phase A→B**: extract (during step 1) → sort
   (`SortForwardIndexBuckets`, wrapping `ForwardEdgeSorter.SortBucketsAsync`), writing per-bucket
   loose `.dat`/`.idx` scratch files. Runs unconditionally, before the walk below, so the walk can
   read successors from these files.
4. **The walk** (§4): BFS from the GC roots (step 2), feeding the reverse-edge index directly
   (`IncrementalReachableWalker.Walk`). Successors default to `ForwardEdgeLooseFileReader`, falling
   back to a live ClrMD walk (`obj.EnumerateReferences(carefully: true)`) when forced
   (`DD_FORCE_LIVE_CLRMD_WALK=1`) or when the forward index/loose files aren't available.
5. Reverse-edge index Phase B+C: sort and merge the buckets the walk just populated
   (`WriteReverseIndexSections`).
6. Forward-edge index Phase C: merge step 3's loose files into the container and delete them
   (`WriteForwardIndexSections`, now narrowed to just this).
7. `TypeAggregates`.
8. `containerWriter.Finish()`.

**Not done:** persisting the Stage B dominator-tree sections as part of this same Phase 1 pass — only
the reachable-graph/reverse-edge-index side of Stage A's on-disk footprint has shipped so far.

---

## 3. The staged build

Three independently-gated stages — gating "build the dominator tree" as one all-or-nothing unit would
force every path-search-only consumer to pay for the full, expensive pipeline just for a byproduct
they don't need dominance for.

- **Stage A — reachability walk, incremental and bounded by construction.** BFS from roots, builds the
  reverse-edge index. Bounded memory throughout, uncapped (no `MaxParentsPerChild`-equivalent
  truncation). Persists on completion, independent of whether Stage B ever runs.
- **Stage A.5 — close the reachable-only scope gap (optional, separately gated).** The walk never
  visits garbage (unreachable) objects, so the reverse-edge index has no entry for edges whose
  *source* is garbage. **Determined not required** — see §7.
- **Stage B — fold + LT + rollup.** Only runs if an analyzer wants `idom[]`/retained bytes
  specifically. Gated on top of Stage A succeeding.

### Gating

Two marker interfaces (same shape as the existing `IParallelHeapIndexScanParticipant`), checked
against the already-resolved `activeAnalyzers` list
([DumpAnalysisService.cs:53-64](../../../src/DumpDetective.Cli/Execution/DumpAnalysisService.cs#L53-L64)):

```csharp
bool wantsReachableGraph =
    !SkipDominatorIndexBuild
    && activeAnalyzers.Any(a => a is IRequiresReachableGraphIndex);  // broad — Stage A/A.5 consumers

bool wantsExactTree =
    wantsReachableGraph                                             // Stage B implies Stage A
    && retentionOptions.EnableExactDominatorTree
    && activeAnalyzers.Any(a => a is IRequiresDominatorTreeIndex);   // narrow — idom/retained bytes

bool canBuildReachableGraph =
    forwardEdgeExtractor is not null
    && !SkipRootIndexBuild;

bool buildStageA = wantsReachableGraph && canBuildReachableGraph;
bool buildStageB = buildStageA && wantsExactTree;
```

If a stage is wanted but can't build (missing prerequisite), add to `satelliteWarnings` — same pattern
as the existing `Handles`/`Roots`/`Tasks` try/catch blocks — rather than fail silently.

**`IRequiresReachableGraphIndex`** — `DominatorAnalyzer`, `GCRootAnalyzer`, `StaticRootLeakAnalyzer`,
`FinalizableObjectAnalyzer` (all also want Stage B, so implement both). Every current
`IBackwardReferenceProvider` consumer (`EventLeakAnalyzer`, `ReferenceChainAnalyzer`,
`TimerLeakAnalyzer`, `StaticRootLeakDetector`, `CollectionAnalyzer`) implements this too, since
Stage A now backs `IBackwardReferenceProvider` for everyone (§7).

**`IRequiresDominatorTreeIndex`** — stays narrow: `DominatorAnalyzer`, `GCRootAnalyzer`,
`StaticRootLeakAnalyzer`, `FinalizableObjectAnalyzer`.

---

## 4. Stage A's design — three decisions, each settled by measurement

Real dumps were measured before building anything speculative in each case; §8 has the numbers.

1. **Visited tracking: `HashSet<ulong>`, not a bitset or `DenseIdMap`.** A bitset-over-`ObjectAddresses`
   + ordinal-lookup design was built expecting a peak-memory win over `DenseIdMap` (~13 bytes/reachable
   node). Measured peak working set was within noise (±17MB) of the baseline in every variant tried —
   the memory win never materialized — so the design was picked on wall-clock alone instead, and plain
   `HashSet<ulong>` beat every alternative (§8.1). Discovered edges are written straight to the
   reverse-edge extractor's bucket files as found (the same hash-partition-by-child-address scheme
   `ReverseEdgeExtractor` already used for its scan-fed version), so the BFS frontier only ever holds
   the current wavefront, never the whole reachable graph.
2. **Uncapped.** `MaxParentsPerChild` (previously 10,000) is deleted outright from
   `ReverseEdgeExtractor`/`ReverseEdgeSorter` — real hub sizes measured on both dumps stay well under
   the sort phase's 600MB-per-bucket ceiling even at the extreme end (§8.3), so hub-overflow routing
   was never built. `ReverseIndexMetadata.MaxParentsPerChild` is kept for on-disk format stability,
   always written as `int.MaxValue`.
3. **Successors: `ForwardEdgeLooseFileReader` by default** — an mmap'd reader over the forward-edge
   index's sorted-but-not-yet-merged loose files. ~2x faster than a live ClrMD walk at 25GB, roughly at
   parity at 3.3GB, after three measured iterations (§8.4). `DD_FORCE_LIVE_CLRMD_WALK=1` forces the
   live-ClrMD fallback (used when the forward index/loose files aren't available, or for future
   re-measurement).

This shape — measure real skew/scale before building routing complexity — isn't specific to the
reachability walk; it applies to any structure with the same profile.

---

## 5. On-disk format

Everything persisted, by stage. All additive `CacheSectionId` values (next available past
`ForwardEdgeMetadata` = 20), no `FormatVersion` bump.

**Stage A — reachable graph (shipped):**

| Section | Shape | Purpose |
|---|---|---|
| `DominatorReachableAddresses` | sorted `ulong[]` | Written by `DominatorReachableAddressWriter`, read by `DominatorReachableAddressReader`/`IReachableAddressProvider` — "is this object reachable from a GC root?" from disk, no walk re-run needed. |
| `DominatorReachableInDegree` | — | **Not built.** The reverse-edge index's `EnumerateChildCounts` already exposes exact, uncapped fan-in per reachable object; a second copy would cost disk space for zero new capability. |

The reverse-edge index (`ReverseEdgeBuckets`/`ReverseEdgeDirectories`/`ReverseEdgeMetadata`) is Stage
A's "who points at this reachable object?" answer — nothing new to persist for that beyond what
already existed, since §7 made it walk-fed rather than scan-fed.

**Stage A.5** — determined unnecessary (§7); no format entries needed.

**Stage B — dominance tree (not started):**

| Section | Shape | Purpose |
|---|---|---|
| `DominatorImmediateDominatorAddresses` | `ulong[]`, aligned to `DominatorReachableAddresses` | already reserved (22), D7 — `idom[]` |
| Dominator child index (name TBD) | bucket/directory, keyed by dominator-tree parent | `EnumerateRetainedSet(address)` = streaming subtree walk, no resident array |
| `DominatorTreeMetadata` (JSON, mirrors `ReverseIndexMetadata`) | whole-tree total retained bytes + per-`MethodTable` rollup | the one unavoidably O(N) computation — done once at write time |

`TryGetRetainedBytes(address)` = the dominator child index's subtree walk, summing each visited node's
shallow size (already available via `ObjectSizes`/`SegmentIndex` point lookup).

**Folded leaves need their own entry, not just an aggregate.** `LeafFolder.Fold` currently discards
which old-ids were folded into a surviving parent, keeping only the aggregate byte total
(`FoldedBytesByNewId`). For `EnumerateRetainedSet` to include folded leaves — disproportionately
likely to be exactly what a retained-set query cares about (interned strings, small caches) —
`LeafFolder` needs to additionally emit a compact `(parentNewId → foldable old-ids)` CSR. Once folded
leaves appear as ordinary children, `FoldedBytesByNewId` becomes redundant — don't persist both.

**Open (Stage B only):** whether the dominator child index needs its own hub-overflow treatment (a
single dominance-tree parent could, in principle, have an enormous number of direct children).

---

## 6. What's deliberately not persisted

| Structure | Why it's dropped |
|---|---|
| `graph.MethodTables` | `ObjectMethodTables` already covers it; only the per-type rollup is worth keeping, as `DominatorTreeMetadata` |
| `graph.ShallowSizes` | `ObjectSizes` already covers it |
| Raw pre-fold forward CSR (reachable-subset-only) | Redundant with the forward-edge index |
| `IsRoot` | `RootIndexWriter`'s `Roots` section already answers "is X directly rooted" |
| `Depth` (dominator-tree depth per node) | Cheap to add later if wanted (report UX) — not worth blocking on |
| `FoldedBytesByNewId` | Superseded by the folded-leaf CSR (§5) |

**New idea surfaced by this audit:** the dominator tree's virtual root has one real GC root as a
direct ancestor per rooted node. Cross-referencing that with `RootIndex`'s `(TargetAddr, RootAddr,
Kind)` triples would let a report answer "how much memory would become collectible if thread N
exited" or "how much is `Static` roots holding vs. `Handle` roots." Effectively free once the tree and
`Roots` section both exist, but needs a new analyzer or report section — nothing today surfaces it.

---

## 7. Total replacement of the reverse-edge index — shipped

Every current `IBackwardReferenceProvider` consumer (`EventLeakAnalyzer`, `ReferenceChainAnalyzer`,
`TimerLeakAnalyzer`, `StaticRootLeakDetector`, `CollectionAnalyzer`, `DominatorAnalyzer`) now reads
from the walk-fed reverse-edge index instead of a raw per-object scan. The interface-facing classes
(`ReverseEdgeExtractor`/`Sorter`/`ContainerWriter`/`IndexReader`, `IBackwardReferenceProvider`,
`HeapAnalysisCache.TryGetReverseIndexProvider()`) needed **no changes** — only the *feed* changed:
the raw per-object scan no longer populates `ReverseEdgeExtractor`; `IncrementalReachableWalker.Walk`'s
BFS from GC roots does, in the same pipeline slot where the extractor's buckets were already being
sorted and written (§2 step 4-5).

**Why this is safe — parity confirmed, not assumed:**
- `TryGetParents` has exactly one caller in the codebase
  ([IndexBackedBidirectionalSearch.cs:114](../../../src/DumpDetective.Analysis/Traversal/IndexBackedBidirectionalSearch.cs#L114),
  path-finding) and `EnumerateChildCounts` exactly one
  (`DominatorAnalyzer.BuildLeakSignalsFromReverseIndex`, fan-in ranking) — no third usage shape to
  account for.
- Garbage objects reached mid-search are a dead end either way: if garbage object G had a real parent
  P, G wouldn't be garbage — contradiction, so every one of G's real parents is also garbage,
  transitively. A search can never reach a root past a garbage node under either index; the walk-fed
  index just reports the dead end immediately (no entry) instead of burning a few more expansion
  levels finding the same thing.
- The walk sources roots via the same `RootSetCache` call path-finding's own consumers already use
  ([ReachableGraphBuilder.cs:29](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphBuilder.cs#L29))
  — no new root-enumeration dependency class introduced. The 256-frame stack cap
  (`RootSetCache.MaxFramesPerThread`) only bounds cosmetic root labeling, not root discovery, and
  `GCRootKind` coverage is complete.
- **Stage A.5 is unnecessary as a result:** any reachable non-root object has at least one *reachable*
  parent by construction (that's how BFS found it). A garbage-sourced parent can never itself continue
  toward a root, so it's a dead end for path-finding regardless, and for fan-in ranking a
  garbage-sourced reference would only inflate the signal with objects about to be collected.

**The one real trade-off:** the old scan-built index's correctness was independent of root
enumeration; the walk-fed one's isn't — an object only gets a parent-list entry if BFS actually
reaches it. Not new risk (the exact dominator tree already had this dependency), but a second consumer
now shares it too.

**Cost:** see §8.2. In the common case — some standard, always-registered analyzer already wants the
dominator tree — Stage A's cost is already sunk, so the reverse-edge index becomes a free byproduct.
Only in the rare case where no dominator-tree consumer is active does replacement cost more than the
old standalone scan-fed index would have.

**Smaller, independent win regardless:** the dominator tree can already confirm reachability and give
an exact retained-bytes answer for an object whose reverse-edge-index parent list would otherwise look
truncated. A `searchTruncated` confidence penalty (`Evidence.cs`, 0.8→0.6) shouldn't necessarily also
suppress a retained-bytes figure that's exact for unrelated reasons.

---

## 8. Measurements behind the shipped design

All figures are from real dumps, foreground, single run at a time, per this project's real-dump-test
rule: a 3.3GB dump (`Crash_IIS_BALTSTPRD`, 6.69M nodes / 17.4M edges reachable) and a 25.6GB dump
(`w3wp.exe_260421_175618.dmp`, 58.3M nodes / 137M edges reachable — ~8.7x/~7.9x the 3.3GB dump).

### 8.1 Visited tracking: `HashSet<ulong>` won on wall-clock, not memory

| Design | 3.3GB walk-only | 3.3GB full pipeline | vs. reverse-edge index (5,570 ms fair cost, §8.2) |
|---|---|---|---|
| `DenseIdMap` (previous production) | 11,511 ms | — | — |
| Ordinal binary search + bitset | 14,994 ms | 16,601 ms | 3.0x |
| + bounded ordinal cache (45.7% hit rate) | 11,397 ms | 12,887 ms | 2.3x |
| Plain `HashSet<ulong>` (shipped) | 8,156 ms | **10,045 ms** | **1.8x** |

Peak working set was within ±17MB across every variant — the memory win the bitset/ordinal design was
built for never showed up on this dump, so the choice came down to wall-clock alone. A batched-edge
variant (`RecordEdgesBatch`, meant to amortize lock overhead) measured *slower*, not faster —
`IncrementalReachableWalker` is single-threaded, so there was no contention to amortize, only extra
bookkeeping. A component split found `RecordEdge` itself is ~55% of the walk's cost (larger than
successors-lookup + visited-tracking combined), and within that the fanout-count dictionary (required
for correctness — reporting exact live parent counts) costs roughly 2x the raw `BinaryWriter` write.

At 25.6GB, the shipped `HashSet` design scaled 11.2x for ~8x data growth vs. `DenseIdMap`'s 20.9x —
directionally consistent with the 3.3GB result, but confounded by the machine dropping to ~0.5GB free
mid-run (plausible paging/compression effects on `DenseIdMap`'s larger backing arrays), so treat the
exact ratio as unconfirmed rather than a proven algorithmic property.

**Shipped:** `ReachableGraphWalker`'s `DenseIdMap` replaced with `Dictionary<ulong,int>` (drop-in,
since the APIs matched exactly and this walker still needs dense-id assignment for its own CSR build).
`IncrementalReachableWalker` uses `HashSet<ulong>` directly — no dense-id assignment needed there.
`DenseIdMap.cs` deleted (zero remaining callers). Verified: all 42 `Traversal.Dominator` tests pass
unchanged, identical dominator-tree results on a fresh 3.3GB real-dump run.

### 8.2 The Stage A vs. reverse-edge-index comparison, made fair

Comparing Stage A's full cost against only the reverse-edge index's sort+merge phase understates the
index's real cost — its `RecordEdge` calls during the mandatory per-object scan are real, marginal
work too, not free. Measured via `DD_SKIP_REVERSE_INDEX_BUILD=1` (diffing total Phase 1 time with and
without reverse-edge extraction):

| | Stage A (full pipeline) | Reverse-edge index (fair: extraction+sort+merge) | Ratio |
|---|---|---|---|
| 3.3GB | 10,045 ms | 5,570 ms | Stage A 1.8x |
| 25.6GB | 125,081 ms | 178,419 ms | Stage A 0.70x (cheaper) |

The 25.6GB pair is the least trustworthy figure in this doc — both runs were taken under real memory
pressure on this machine (free memory in the single-digit GB or below), and the reverse-edge index's
own per-bucket fanout-counter dictionaries are plausibly exposed to the same effect suspected in §8.1.
The gap is large enough it's unlikely to fully invert, but the precise ratio shouldn't be trusted.

**Why the ratio mostly doesn't matter:** `DominatorAnalyzer`, `GCRootAnalyzer`, `StaticRootLeakAnalyzer`,
and `FinalizableObjectAnalyzer` are standard, always-registered analyzers (not opt-in), so in a typical
run Stage A already has to build regardless of this comparison. The reverse-edge index then becomes a
free byproduct, and the table above only applies to the rare case where a run has none of those
analyzers active (e.g. a CLI filter down to only `EventLeakAnalyzer`/`ReferenceChainAnalyzer`).
Confirmed rare, not just assumed to be — this narrow case essentially doesn't happen in practice.

### 8.3 `MaxParentsPerChild`: measured unnecessary, deleted

Real worst-case hub fan-in, measured by temporarily patching the extractor to keep counting past the
cap without changing its on-disk output:

| Dump | Worst real hub (true fan-in) | Its bucket's total size | 600MB ceiling |
|---|---|---|---|
| 3.3GB | 346,470 | ~77MB | not close |
| 25.6GB | **10,757,536** | ~233MB | not close |

A single object would need ~37.5M fan-in, alone in its own bucket, to actually threaten the ceiling —
~3.5x the most extreme real hub observed. Bucket count already scales with dump size
(`ceil(dumpSizeMB / 500)`) to keep average bucket size roughly constant, and it absorbed this much
real-world skew with room to spare.

**Shipped:** `MaxParentsPerChild` deleted outright from `ReverseEdgeExtractor.RecordEdge`/
`RecordEdgesBatch` and `ReverseEdgeSorter` — no hub-overflow routing built. The on-disk `truncated`
byte is now always `false`; `ReverseIndexMetadata.MaxParentsPerChild` stays for format stability,
always written as `int.MaxValue`. No `IBackwardReferenceProvider` consumer needed changes — a
permanently-`false` `truncated` flag was already the correct behavior everywhere it's read. Verified
via a 3.3GB real-dump run: identical dominator-tree results; real edge count rose from ~29M to ~33.76M
once nothing is dropped, consistent with correctness rather than regression.

### 8.4 Successors source for the walk: three rounds, conclusion reversed at scale

| Successors source | 3.3GB Phase 1 build | 25.6GB Phase 1 build |
|---|---|---|
| Live ClrMD walk | 29,589 ms | 1,663,879 ms (~27m44s) |
| `ForwardEdgeLooseFileReader` v1 (`FileStream` Seek+Read per probe) | 221,106 ms (7.5x slower) | not measured |
| `ForwardEdgeLooseFileReader` v2 (mmap, raw pointer binary search) | 34,025 ms (~15% slower) | not measured |
| `ForwardEdgeLooseFileReader` v3 (mmap `.dat` + decoded-array directory, **shipped**) | ~31-33 ms — within noise of live ClrMD | **833,702 ms (~13m54s) — ~2x faster** |

Every variant produced the identical graph (6,686,490 nodes / 17,367,740 edges at 3.3GB) — differences
are pure perf, not correctness. v1's per-probe `Seek`+`Read` cost ~20-25 syscalls per binary-search
step against a multi-million-entry directory; mmap'ing the loose files directly (the same technique
`ForwardEdgeIndexReader` already uses for finalized container sections) fixed most of that (v2).
Decoding the directory into a plain managed array once, instead of binary-searching through raw mmap
pointer dereferences, closed the rest of the 3.3GB gap (v3). At 25GB, live ClrMD's per-object
DAC/type-resolution cost scales worse than v3's one-time directory decode + in-memory binary search,
reversing the 3.3GB result decisively.

**Shipped:** `ForwardEdgeLooseFileReader` (v3) is the default successors source — no meaningful
regression at 3.3GB, a clean ~2x win at the 10GB-25GB+ scale this project targets.
`DD_FORCE_LIVE_CLRMD_WALK=1` forces the live-ClrMD fallback. The 25GB pair was measured under real
memory pressure (as low as ~1.4GB free) and the two runs weren't pressure-matched, so treat "~2x" as
directional rather than a precise ratio.

### 8.5 Known measurement caveats

- Every 25.6GB figure in this doc was taken on a machine that dropped to single-digit-GB (sometimes
  under ~1.5GB) free RAM mid-run. The most extreme scaling numbers (`DenseIdMap`'s 20.9x, the
  reverse-edge index's ~66x extraction-cost jump) are plausibly page/compression artifacts rather than
  pure algorithmic scaling, and weren't independently re-verified with comfortable headroom. Settling
  this would need a 25GB re-run pair on a machine with real headroom throughout — not achievable on
  this 16GB-RAM machine for a dump this size.
- The garbage→reachable edge count specifically (as opposed to garbage→garbage noise) was never
  isolated — moot since Stage A.5 was determined unnecessary regardless (§7). Whole-heap forward edges
  were 33,757,072 vs. 17,367,740 reachable-subgraph edges on the 3.3GB dump, a ceiling on Stage A.5's
  scope gap, not the gap itself.

---

## 9. New use cases beyond the four known consumers

Every current consumer of retained-size-shaped data, audited against what it does today:

- **`EventLeakAnalyzer.EstimateGroupRetainedBytes`** — currently `subscriberCount × typeSizeMap[type]`,
  a shallow multiplication, not a graph walk. A direct `TryGetRetainedBytes` replacement per subscriber
  would be an accuracy upgrade, not just a performance one.
- **`GCHandleAnalyzer`'s pinned-retained-bytes totals** — a genuine **correctness gap**, not an
  enhancement. `totalPinnedRetainedBytes`/`totalAsyncPinnedRetainedBytes` are misnamed today:
  `ResolveSize` returns the pinned object's own shallow size, not what it transitively holds.
- **`WeakReferenceAnalyzer`** — audited, no gap. `WeakReferenceObjectBytes` is honestly the wrapper
  objects' own shallow size. A `TryGetRetainedBytes(target)` addition would be a genuine enhancement,
  not a fix.
- **`CollectionAnalyzer.PopulateRootDescriptions`** — already a `RootPathFinder` consumer; a wasteful
  collection's *retained* bytes would be a natural additional column once cheap to compute exactly.
- **`WcfChannelAnalyzer`/`DbConnectionAnalyzer`** — the biggest gap found in this audit.
  `WcfChannelSnapshot`/`DbConnectionSnapshot` carry **no size field of any kind**. A
  `TryGetRetainedBytes` column would distinguish "100 faulted channels retaining 50KB each" from "100
  faulted channels retaining 200 bytes each" — currently indistinguishable.
- **"Retention roots" report concept** — see §6's new-idea callout.

---

## 10. Open questions

Everything from earlier drafts of this doc that's since been resolved has been folded into §4/§7/§8
above as shipped state. What's actually still open:

- [ ] **The rare narrower cost case (§8.2):** for a run with no dominator-tree consumer active, the
      fair 3.3GB comparison (~1.8x) is trustworthy; the 25.6GB comparison (~0.70x, favoring Stage A) is
      not, given the memory-pressure confound (§8.5). Resolving cleanly may need a machine with more
      headroom than this one has, or accepting the 3.3GB figure as the more reliable data point.
- [ ] Whether the `DenseIdMap`-vs-`Dictionary` wall-clock gap at 25GB and the reverse-edge index's
      extraction-cost jump are the same underlying memory-pressure effect — both show the same
      "far worse than data growth would predict" shape on this machine (§8.5).
- [ ] Whether the dominator child index (Stage B) needs its own hub-overflow handling — a single
      dominance-tree parent could, in principle, have an enormous number of direct children (§5).
- [ ] `DominatorAnalyzer` should stop owning its own `TryComputeExactDominatorTree` build path and
      become a normal reader-consumer like everyone else, once Stage B ships.
- [ ] The garbage→reachable edge split specifically (§8.5) — separate from garbage→garbage noise —
      would size Stage A.5's useful output, though Stage A.5 itself is no longer believed necessary.
