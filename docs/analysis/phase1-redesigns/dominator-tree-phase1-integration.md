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

**Stage B (§3, §5, §9, §10): fully shipped — §10.1, Batch 1, Batch 2a, Batch 2b, and Batch 3 are all
in, plus the two real problems Batch 2a surfaced are fixed. The double-computation problem flagged at
the start of the whole "review the budget" thread is also finally closed.**
`IncrementalReachableWalker` and `ReachableGraphWalker` are now one walker (§10.1).
`DiskBackedObjectIndexWriter.Build` computes a real `buildStageB` (§10.3) and, when true, runs Stage
B's fold + LT + idom + dominator child index + retained-bytes-per-row + per-type rollup, all persisted
inside Phase 1 (§10.4's `BuildAndPersistDominatorTree`). `DominatorAnalyzer` no longer recomputes any
of this in Phase 2 — it reads `IDominatorTreeProvider` (§10.6, `IHeapAnalysisCache.TryGetDominatorTreeProvider()`)
instead (§10.7). `ReachableGraphBuilder.Build` (Phase 2's old live-walk path, now unused) and the
D7-era `DominatorTreeResult`/`DominatorTreeMode` models (never fit anything real) were deleted as
confirmed dead code. §10.8's four real-dump measurement items (hub-overflow, unified-walk cost,
scratch-file monotonicity, child-index re-keying cost) are now all measured on both the 3.3GB and
25.6GB dumps, via a `DD_PERF_DOMINATOR_STAGEB=1`-gated instrumentation pass
([DominatorStageBPerfMeasurementTests.cs](../../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DominatorStageBPerfMeasurementTests.cs))
— see §10.8. Nothing architectural or measurement-related remains open for Stage A/B themselves.

**The budget review concluded: drop `ExactDominatorTreeBudget` entirely, fix what it was covering
for at the root cause instead.** Removed: the calibrated byte-cost model, its 20 GiB default, the
`budget` parameter on `ReachableGraphWalker.Walk`, and `ReachableGraphWalkResult.CapExceeded` — a walk
either completes or throws now, no third "capped" state. `RetentionOptions.EnableExactDominatorTree`
(the feature toggle) stays; `ExactDominatorTreeMemoryBudgetBytes` (the memory limit) is gone. Two real
problems the review surfaced are both fixed, not just documented:
- **Walk-phase isolation.** `DiskBackedObjectIndexWriter.Build`'s reachability-walk phase — previously
  the one place in that method with no failure isolation of its own — now has a try/catch matching
  every other satellite section's existing pattern: a `Walk()` failure discards the reverse-edge
  index's partial state and the deferred per-segment scratch files, logs a warning, and lets the rest
  of the index build (forward index, TypeAggregates, `Finish()`) continue untouched. This also
  resolves the "Stage B budget trip corrupts Stage A" risk directly — there's no cap left to trip
  mid-walk, and if the walk fails for any other reason, Stage A's now-unreliable partial data is
  discarded rather than persisted incomplete.
- **Silent `int` overflow.** `ChunkedBuffer<T>.Add` — the one place every downstream node/edge count
  in this pipeline is ultimately bounded by — now throws before `Count` could wrap past
  `int.MaxValue`, instead of silently wrapping. One guard at the root cause protects `LeafFolder` and
  `DominatorTreeComputer` transitively, converting "wrong dominator tree, no diagnostic" into a loud
  exception the walk-phase isolation above already knows how to degrade gracefully.

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
the reachable-graph/reverse-edge-index side of Stage A's on-disk footprint has shipped so far. §10
scopes this, including replacing step 4's walker with one that also produces Stage B's CSR in the
same pass, rather than running a second, independent walk later.

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

## 10. Stage B design

Scope for Stage B's implementation, decided before any code is written. Includes unifying the two
walkers — `IncrementalReachableWalker` (Stage A, shipped) and `ReachableGraphWalker` (Stage B's CSR
build, currently run standalone in Phase 2) — into one walk. That unification is part of *this* scope,
not a follow-up: Stage B is exactly the consumer that needs the second walker's output, so building
Stage B without folding the two walks together would ship a second full reachability walk right next
to the first one, on purpose, in the same pass.

### 10.1 Walker unification — shipped

**Implemented as designed below, with two naming/shape differences from the original decision, both
smaller than the design predicted:**
- The mode parameter is `buildCsr: bool`, not `buildStageB: bool` — named for what it does (build the
  CSR) rather than which stage wants it, since §10.3's gating (which would have supplied a
  `buildStageB` value) isn't implemented yet; today's only caller passes `buildCsr: false` (see below).
- A second parameter, `captureSortedAddresses: bool`, was added beyond the original design — needed
  because `DiskBackedObjectIndexWriter.Build`'s call always wants the sorted `DominatorReachableAddresses`
  regardless of `buildCsr`, while `ReachableGraphBuilder.Build`'s Phase 2 call never wants it; baking the
  sort into both paths unconditionally would have cost Phase 2 an unwanted O(N log N) sort.

`IncrementalReachableWalker.cs` is deleted; `ReachableGraphWalker.cs`
([ReachableGraphWalker.cs](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs))
now dispatches to `WalkWithoutCsr` (formerly `IncrementalReachableWalker.Walk`, `HashSet<ulong>`-based,
now also accepting an optional `captureSortedAddresses` flag it already effectively always exercised) or
`WalkWithCsr` (formerly this file's own `Walk`, `Dictionary<ulong,int>`+`ChunkedBuffer`-based, now also
accepting an optional `reverseEdgeExtractor` and streaming to it inline). Both call sites updated:
`DiskBackedObjectIndexWriter.Build`'s step 4 (`buildCsr: false, captureSortedAddresses: true`) and
`ReachableGraphBuilder.Build`'s Phase 2 path (`reverseEdgeExtractor: null, buildCsr: true,
captureSortedAddresses: false`) — the latter's behavior and performance are unchanged, since it always
built the CSR anyway and never asked for sorted addresses. Tests: `IncrementalReachableWalkerTests.cs`
merged into `ReachableGraphWalkerTests.cs` (both modes covered in one file now, plus a new test for
`captureSortedAddresses: false` leaving `ReachableAddresses` empty). All 45 `Traversal.Dominator` unit
tests and all 95 `Unit.Indexing` tests pass; `DumpDetective.Analysis`, `.Tests`, `.Cli`, and `.Reporting`
all build clean.

**Not done as part of this change** (deliberately out of scope for §10.1 alone, per §10.3/§10.7): no
caller passes `buildCsr: true` together with a non-null `reverseEdgeExtractor` in production yet — that
combination (the actual "both stages in one pass" win §10.1 was designed for) only activates once §10.3's
gating exists to compute a real `buildStageB` value and thread it into `DiskBackedObjectIndexWriter.Build`'s
call. Until then, the unification's only realized benefit is eliminating the second walker class and
proving both modes share one call surface — the double-`successors()`-call cost §10.1 targets is still
paid today, just by two different callers (Phase 1 and Phase 2) instead of by design necessity.

---

Original design (for reference — see "shipped" note above for what actually landed):

Today, two independent BFS walks exist:

- `IncrementalReachableWalker.Walk` (§4, shipped) — `HashSet<ulong>` visited tracking, no dense ids, no
  in-memory CSR. Streams every edge straight to `ReverseEdgeExtractor.RecordEdge(fromAddr, toAddr)` as
  discovered. Returns only a sorted `ulong[]` of reachable addresses + counts.
- `ReachableGraphWalker.Walk` — `Dictionary<ulong,int>` id map, `ChunkedBuffer<T>` edge-list capture,
  `ExactDominatorTreeBudget` enforced mid-walk, O(N+E) counting-sort CSR build at the end. Feeds nothing
  to `ReverseEdgeExtractor` — purely in-memory, currently invoked by `ReachableGraphBuilder.Build` from
  `DominatorAnalyzer` in Phase 2.

**Decision:** replace both with a single walker (extend `ReachableGraphWalker` in place;
`IncrementalReachableWalker` is deleted, its doc comments and `Result` shape folded in) that always
feeds `ReverseEdgeExtractor` per edge — Stage A's existing, unconditional contract — and additionally
builds the dense-id CSR only when a new `buildStageB: bool` parameter is `true`:

- `buildStageB == false`: unchanged from today's `IncrementalReachableWalker` behavior — `HashSet<ulong>`
  visited only, no id map, no edge-list capture, no budget check. This preserves §4.1's measured result
  (plain `HashSet<ulong>` beat `Dictionary`/bitset) for the common case where Stage A alone is wanted.
- `buildStageB == true`: additionally maintains the id map and `ChunkedBuffer` edge lists exactly as
  `ReachableGraphWalker` does today, applies `ExactDominatorTreeBudget` mid-walk unchanged, and runs the
  same counting-sort CSR build at the end. If the budget trips, only the CSR-capture side aborts —
  Stage A's `ReverseEdgeExtractor` calls up to that point are independent of the CSR and are unaffected,
  so a capped Stage B degrades to "Stage A shipped, Stage B skipped, warning logged," the same
  graceful-degradation contract §3's gating already has for every other missing prerequisite.

**Net effect:** `successors()` — the mmap probe against the forward-edge loose files, or the live ClrMD
fallback — is called exactly once per reachable node regardless of how many of Stage A/B are wanted,
instead of once per walker per node. Per §8.2, `DominatorAnalyzer`/`GCRootAnalyzer`/`StaticRootLeakAnalyzer`/
`FinalizableObjectAnalyzer` are standard, always-registered analyzers that want both stages in the
common case — today that means paying for successor lookups twice over the same reachable set. This is
a real perf win from unification, not just a memory-neutral relocation.

**Exact call site (confirmed against current code, not just the design doc's pseudocode):**
`DiskBackedObjectIndexWriter.Build` already assembles everything the unified walker needs, in one place
([DiskBackedObjectIndexWriter.cs:756-809](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L756-L809)):
`walkRootAddresses` (built from `heap.EnumerateRoots()`), `walkSuccessors` (the
`ForwardEdgeLooseFileReader`-or-live-ClrMD delegate §4.3 describes), and `reverseEdgeExtractor`. The only
change at this call site is swapping `IncrementalReachableWalker.Walk(walkRootAddresses, walkSuccessors,
reverseEdgeExtractor, cancellationToken, progress)` for the unified walker's call, adding `budget` and
`buildStageB` as new arguments. No new plumbing is needed to get root addresses or a successors function
to this point — both already exist exactly where the unified walker needs them.

**New per-node metadata resolution gap, found while confirming the call site.** `ReachableGraphBuilder.Build`'s
post-walk loop resolves each node's `MethodTable`/`ShallowSize` via `cache.TryGetObjectMetadata`
([ReachableGraphBuilder.cs:51-64](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphBuilder.cs#L51-L64)),
which prefers a disk-backed `ObjectAddressLookup` opened from the *finalized* container file
([HeapIndexCache.cs:107-138](../../../src/DumpDetective.Analysis/Cache/HeapIndexCache.cs#L107-L138)). That
lookup requires a complete TOC, which doesn't exist until `containerWriter.Finish()` — Phase 1's own
`ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes` sections are written into the in-progress container
stream well before `Finish()` ([DiskBackedObjectIndexWriter.cs:532-547](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L532-L547))
but aren't queryable as a finished index yet, and the per-segment scratch files that fed them are
concatenated-and-discarded, not kept as an in-memory address→metadata map. Running Stage B's metadata
resolution inside Phase 1 therefore means `cache.TryGetObjectMetadata`'s disk branch always misses and
every call would silently take the existing live-ClrMD fallback branch — same code path this already
has today for in-memory-mode/pre-v4-cache runs, just always-taken here instead of sometimes-taken.

**Decision: don't fall back to live ClrMD — reuse the already-in-memory `SegmentIndexEntry[]` against the
per-segment scratch files instead, deferring their deletion.** `ObjectAddressLookup`
([ObjectAddressLookup.cs](../../../src/DumpDetective.Analysis/Indexing/ObjectAddressLookup.cs)) already
solves exactly this problem post-`Finish()` with a two-level binary search: a small in-memory
`SegmentIndexEntry[]` table narrows to a segment, then a binary search over that segment's `ObjectAddresses`
slice finds the record index, and `ObjectMethodTables`/`ObjectSizes` are read at that same index
([ObjectAddressLookup.cs:93-162](../../../src/DumpDetective.Analysis/Indexing/ObjectAddressLookup.cs#L93-L162)).
The `SegmentIndexEntry[]` half of that already exists in memory during Phase 1 —
`DiskBackedObjectIndexWriter.Build` builds it at
[DiskBackedObjectIndexWriter.cs:573-587](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L573-L587),
well before step 4's walk runs. The only real blocker is that `ConcatenateScratchFiles` deletes each
per-segment `Address`/`MethodTable`/`Size` scratch file immediately after copying it into the container
stream ([DiskBackedObjectIndexWriter.cs:1427](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L1427))
— by the time the walk runs, the source files are already gone even though the in-memory segment table
describing them survives.

Fix: when `buildStageB` is true, skip deleting `segAddrScratchFiles`/`segMtScratchFiles`/`segSizeScratchFiles`
during concatenation, and do point lookups directly against those per-segment files during Stage B's
metadata-resolution pass — the same `FindSegment`/`FindRecord` binary-search logic
`ObjectAddressLookup` already has, actually *simpler* here since each per-segment scratch file is
self-contained (record indices start at 0 for that segment, no `FirstRecordIndex`-relative offset math
needed the way the merged container column requires). Delete the three scratch-file arrays once Stage
B's metadata pass completes, instead of immediately after concatenation.

This avoids all three costs the live-ClrMD fallback would have paid: no `heap.GetObject` calls, no new
in-memory address→metadata structure (reuses the segment table that's already built), and no changes to
`CacheContainerWriter` or its in-progress stream (the per-segment scratch files are read directly,
independent of what's already been copied into the container). It also doesn't need `ObjectAddressLookup`
itself touched — a new, small sibling reader over per-segment files, sharing its binary-search shape, is
enough. **Pending:** confirm per-segment scratch files support the same within-segment address
monotonicity `ObjectAddressLookup`'s doc comment already validated for the merged case (expected to hold
trivially, since the merged column is just these same per-segment files concatenated in order) and pick
`FileStream.Seek`+`Read` vs. a small `MemoryMappedFile` per segment for the point-read itself — moved to
§10.8.

### 10.2 Placement in the Phase 1 pipeline — resolved

Runs once, in the existing walk slot (§2 step 4), replacing that step's call to
`IncrementalReachableWalker.Walk` with the unified walker, `buildStageB` passed through from §10.3's
gating.

The successors-source placement question this section previously left pending is resolved by reading
the call site directly: `ForwardEdgeLooseFileReader.TryOpen` already runs successfully at this exact
point today, against step 3's loose, not-yet-merged forward-edge files
([DiskBackedObjectIndexWriter.cs:769-799](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L769-L799)) —
step 6 (`WriteForwardIndexSections`, which merges those loose files into the container and deletes them)
runs *after* this call, not before. Stage A's shipped walk is proof this works. No step-ordering change
needed; the unified walker's `buildStageB == true` mode reuses `walkSuccessors` exactly as constructed
today, unchanged.

### 10.3 Gating — shipped (Batch 2a), narrower than §3's original design

**Shipped:** both marker interfaces exist and are applied to all 8 analyzers listed below (with the
`StaticRootLeakDetector` name correction). The four-hop plumbing chain is wired end to end —
`BuildHeapIndexStage` now passes `state.ActiveAnalyzers` and
`state.Resolved.MemoryLeak.EnableExactDominatorTree` through
`IHeapIndexBuilder.PrebuildHeapIndex` → `HeapAnalysisCache`/`HeapIndexCache.PrebuildHeapIndex` →
`IObjectIndexWriter.Build`/`DiskBackedObjectIndexWriter.Build`. All new parameters have safe defaults
(`null`/`false`) so every pre-existing caller — benchmarks, discrepancy tests — keeps compiling
unchanged; only `BuildHeapIndexStage` (the one production call site) passes real values. (A third
parameter, the memory budget, was threaded through this same chain when Batch 2a first shipped —
removed along with `ExactDominatorTreeBudget` itself once the budget review concluded it should be
dropped; see the top-of-doc status summary and §10.4/§10.8.)

**Narrower than §3's original design, deliberately:** only `buildStageB` is actually computed and
consumed —

```csharp
bool buildStageB =
    reverseEdgeExtractor is not null       // Stage A actually running — its own existing gate
    && !SkipDominatorIndexBuild
    && enableExactDominatorTree
    && (activeAnalyzers?.Any(a => a is IRequiresDominatorTreeIndex) ?? false);
```

`IRequiresReachableGraphIndex` is implemented by every listed analyzer but **not yet consumed** —
Stage A's own construction stays unconditional (gated only by `SkipReverseIndexBuild`, unchanged), not
`wantsReachableGraph`-gated as §3 originally specified. Making Stage A itself skippable when no analyzer
wants it is a real, separate change to already-shipped behavior, deliberately deferred rather than
bundled into this pass.

§3's `wantsReachableGraph`/`wantsExactTree`/`buildStageA`/`buildStageB` pseudocode, and the
`IRequiresReachableGraphIndex`/`IRequiresDominatorTreeIndex` marker interfaces it depends on, **didn't
exist in code before this pass** — confirmed absent from the codebase at the time of scoping. Today's
prior gating was a single, simpler flag, `SkipReverseIndexBuild`
([DiskBackedObjectIndexWriter.cs:174-176](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L174-L176)),
with no analyzer-based decision at all — Stage A's walk always runs whenever the reverse index isn't
explicitly skipped.

**Marker interfaces — decided.** Two empty tag interfaces (no members needed; §3's gating only ever does
`activeAnalyzers.Any(a => a is ...)`), placed in `DumpDetective.Analysis.Pipeline` alongside
`IHeapIndexScanParticipant`/`IParallelHeapIndexScanParticipant`
([IParallelHeapIndexScanParticipant.cs](../../../src/DumpDetective.Analysis/Pipeline/IParallelHeapIndexScanParticipant.cs)) —
same assembly as every analyzer class that will implement them, same opt-in convention.

**Analyzer name correction, found while confirming this section against real code.** §3's consumer
lists mostly check out — `DominatorAnalyzer`, `GCRootAnalyzer`, `FinalizableObjectAnalyzer`,
`EventLeakAnalyzer`, `ReferenceChainAnalyzer`, `TimerLeakAnalyzer`, `CollectionAnalyzer` all exist under
those exact names — but **`StaticRootLeakAnalyzer` doesn't exist**; the real class is
`StaticRootLeakDetector`
([StaticRootLeakDetector.cs:12](../../../src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs#L12)).
§3's two consumer lists should read `StaticRootLeakDetector` throughout, not `StaticRootLeakAnalyzer`.

**Plumbing chain to reach `DiskBackedObjectIndexWriter.Build` — decided, four hops, all confirmed by
reading the actual call chain (not assumed from the design doc):**

1. [BuildHeapIndexStage.cs:19-65](../../../src/DumpDetective.Cli/Pipeline/Stages/BuildHeapIndexStage.cs#L19-L65) —
   `state.ActiveAnalyzers` and `state.Resolved.MemoryLeak.EnableExactDominatorTree`
   (`ResolvedExecutionOptions.MemoryLeak: RetentionOptions`) are both already resolved and sitting on
   `state` by the time this stage runs — `state.ActiveAnalyzers` is even read a few lines further down
   in this same method, just after the index-build call. No new resolution work needed, only passing
   what's already there into the `PrebuildHeapIndex` call two lines above where it's currently omitted.
2. `IHeapIndexBuilder.PrebuildHeapIndex`
   ([IHeapIndexBuilder.cs:20-24](../../../src/DumpDetective.Analysis/Cache/IHeapIndexBuilder.cs#L20-L24)) —
   add two parameters: `IReadOnlyList<IAnalyzer> activeAnalyzers`, `bool enableExactDominatorTree`. Passing
   the single bool rather than the whole `RetentionOptions` keeps this indexing-layer interface from
   depending on option fields it has no other reason to know about.
3. `HeapAnalysisCache.PrebuildHeapIndex`
   ([HeapAnalysisCache.cs:138-146](../../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs#L138-L146))
   and `HeapIndexCache.PrebuildHeapIndex`
   ([HeapIndexCache.cs:31-73](../../../src/DumpDetective.Analysis/Cache/HeapIndexCache.cs#L31-L73)) — pure
   pass-through at both layers, same two new parameters threaded to the next call.
4. `IObjectIndexWriter.Build`/`DiskBackedObjectIndexWriter.Build`
   ([DiskBackedObjectIndexWriter.cs:74-79](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L74-L79)) —
   same two new parameters; §3's pseudocode is implemented here, replacing the current
   `SkipReverseIndexBuild`-only condition around `reverseEdgeExtractor`'s construction. A new
   `SkipDominatorIndexBuild` env-flag field is added here too, mirroring the existing
   `SkipRootIndexBuild`/`SkipReverseIndexBuild`/`SkipForwardIndexBuild` pattern
   ([DiskBackedObjectIndexWriter.cs:37-53](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L37-L53)),
   for the `!SkipDominatorIndexBuild` term in `wantsReachableGraph`. `buildStageA`/`buildStageB` computed
   here feed directly into the unified walker's `reverseEdgeExtractor`/`buildCsr` parameters (§10.1) at
   the existing call site.

**Resolved: landed together with §10.4, per the decision recorded here previously.** `buildStageB` now
feeds directly into the unified walker's `buildCsr` parameter, and a real consumer
(`BuildAndPersistDominatorTree`, §10.4) reads and persists the result — gating is no longer inert.

**Smaller pending item found in the same pass, not blocking either option above:**
`HeapIndexCache.PrebuildHeapIndex`'s early-return fast path
(`if (_heapIndex is not null) return _heapIndex;`,
[HeapIndexCache.cs:37-38](../../../src/DumpDetective.Analysis/Cache/HeapIndexCache.cs#L37-L38)) means a
second `PrebuildHeapIndex` call on the same `HeapAnalysisCache` instance with a *different*
`activeAnalyzers`/`enableExactDominatorTree` silently reuses whatever the first call decided, gating
included. Not reachable through the CLI's normal single-dump-per-cache-instance usage today, but worth a
one-line note or assert if an embedder/test ever reuses a cache instance across differently-gated runs.

### 10.4 Persistence — shipped in full (Batch 2a + Batch 2b)

**Shipped (Batch 2a):** `DiskBackedObjectIndexWriter.Build` now runs Stage B end to end when
`buildStageB` is true — `BuildAndPersistDominatorTree`
([DiskBackedObjectIndexWriter.cs](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs)),
called right after `DominatorReachableAddressWriter.Write`, resolves each reachable node's
`MethodTable`/`ShallowSize` via `ScratchFileObjectMetadataLookup` (falling back to live ClrMD if the
scratch files can't be opened), resolves `GenerationTag` via `ReachableGraphBuilder.ResolveGenerationTag`
(now `internal`, shared with Phase 2's path), builds a `ReachableGraph`, runs
`DominatorTreeComputer.Compute` (which already runs `LeafFolder.Fold` internally), translates `idom[]`
back to addresses (folded leaves resolve to their one real predecessor directly, virtual-root children
to a `0` sentinel), and persists via the now-split `DominatorTreeIndexWriter.WriteImmediateDominatorAddresses`.
The deferred `Address`/`MethodTable`/`Size` scratch files are deleted right after, regardless of
outcome (success or exception).

**Resolved: the Stage-A-corruption risk this section originally flagged here is fixed, not just
documented.** `ExactDominatorTreeBudget` bounded a Phase-2-only walk Stage A never participated in —
tripping it just meant "no exact tree this run." Once `buildCsr: true` and Stage A's
`reverseEdgeExtractor` started running in the *same* walk (this section, Batch 2a), a budget trip
aborted the whole walk while edges already streamed to `reverseEdgeExtractor` stayed recorded, leaving
Stage A's reverse-edge index silently incomplete. The budget review (top-of-doc status summary,
§10.8) concluded the right fix wasn't recalibrating the model but removing it: `ExactDominatorTreeBudget`
is deleted, `DiskBackedObjectIndexWriter.Build`'s walk phase now has its own try/catch (matching every
other satellite section's existing isolation pattern) that discards Stage A's partial state on *any*
walk failure rather than persisting it incomplete, and the one correctness invariant worth keeping —
never silently overflow the `int`-indexed arrays — is enforced at its actual root cause
(`ChunkedBuffer<T>.Add`) instead of a periodic byte-cost estimate.

§5 originally framed the child index and metadata rollup as "new, no prior art to adapt." That
undersold what was actually available — `DominatorTreeComputer.Compute` already built the child CSR
internally (just discarded on return), and the per-type rollup logic already existed in
`DominatorAnalyzer`. Both are now shipped.

**Shipped (Batch 2b): the dominator child index and `DominatorTreeMetadata`.**
New `CacheSectionId` values 23-25: `DominatorChildOffsets`, `DominatorChildAddresses`,
`DominatorTreeMetadata`.

**The re-keying question from earlier drafts — resolved, and cheaper than expected.**
`DominatorTreeComputeResult.ChildOffsets`/`ChildTargets` (Batch 1) are indexed by *reduced* id, not
address — but every other section here is row-aligned with `DominatorReachableAddresses`' sorted
order. Rather than have each writer re-derive that address order independently (which
`DominatorTreeIndexWriter.WriteImmediateDominatorAddresses` used to do, via its own internal
`Array.Sort` of `(Address, DominatorAddress)` tuples), the row mapping is now computed **once**, in
`DominatorRowMapping.Compute` — N binary searches against `ReachableGraphWalkResult.ReachableAddresses`,
which is already sorted, so this doesn't re-sort anything, only locates where each node already sits.
`DominatorTreeIndexWriter` was simplified to take a pre-built `ulong[] dominatorAddressesByRow` and
just write it — no more internal sort. `DominatorChildIndexBuilder.Build` reuses that same row mapping
to merge `tree.ChildOffsets`/`ChildTargets` (real dominator-tree edges) with
`fold.FoldedLeafOffsets`/`FoldedLeafOldIds` (folded leaves, §10.5, included as ordinary children per
§5's original motivation) into one row-ordered CSR, via the same two-phase counting sort used
everywhere else in this pipeline. Net effect: one shared O(N log N) pass instead of two separate ones.
- **The `int` vs. `long` offsets question (§10.8) is resolved as a consequence of the earlier budget
  fix, not new work.** Total child-index entries ≤ reduced edges + folded-leaf count ≤ the original
  edge count, and `ChunkedBuffer<T>.Add`'s overflow guard (from the budget-removal fix) already
  guarantees that count fits safely in `int`. `int[]` offsets are safe by construction here, not by a
  fresh check.
- Folded leaves' addresses appear as ordinary child entries in their surviving parent's row, exactly
  as §5 originally wanted — covered by `DominatorChildIndexBuilderTests`.
- The virtual root itself has no row (no address to key one on) — its direct children (real GC roots)
  are already identifiable via `DominatorImmediateDominatorAddresses`' `0` sentinel.
- `DominatorTreeMetadata` (JSON: whole-tree total + per-`MethodTable` rollup) is computed by a new
  shared helper, `DominatorRetainedBytesRollup.Compute` — extracted from
  `DominatorAnalyzer.TryComputeExactDominatorTree`'s per-type loop so both Phase 1 (this section) and
  Phase 2 (unchanged until §10.7) use the exact same aggregation instead of two copies drifting apart.
- New minimal readers, `DominatorChildIndexReader`/`DominatorTreeMetadataReader` — deliberately
  bare (open the section, binary-search a row, slice/deserialize). The richer query surface
  (`EnumerateRetainedSet`, `TryGetRetainedBytes`'s subtree-sum walk, the `IDominatorTreeProvider`
  facade) is still Batch 3 (§10.6), not built here — this batch is "persist and read back correctly,"
  not "consume."
- New tests: `DominatorChildIndexBuilderTests` (the merge algorithm, `ClrHeap`-independent, same
  synthetic-graph style as `DominatorTreeComputerTests`), `DominatorChildIndexTests`/
  `DominatorTreeMetadataTests` (format round-trip, mirroring `DominatorTreeIndexTests`).

**Prior-art audit that motivated all of the above — still accurate, kept for context:**

**1. `DominatorTreeIndexWriter`/`DominatorTreeIndexReader`/`DominatorTreeIndexTests` already existed and
already worked — they were built for D7's abandoned "compute in Phase 2, append after `Finish()`"
design and never deleted.**
[DominatorTreeIndexWriter.cs](../../../src/DumpDetective.Analysis/Indexing/Dominator/DominatorTreeIndexWriter.cs)
writes `DominatorReachableAddresses` + `DominatorImmediateDominatorAddresses` as a sorted-by-address
pair;
[DominatorTreeIndexReader.cs](../../../src/DumpDetective.Analysis/Indexing/Dominator/DominatorTreeIndexReader.cs)
binary-searches both and already implements `TryGetImmediateDominator(address, out dominatorAddress)`
correctly; [DominatorTreeIndexTests.cs](../../../tests/DumpDetective.Tests/Unit/Indexing/DominatorTreeIndexTests.cs)
round-trip-tests the format. The reader needed **no change at all** across both batches — `TryOpen`
already opens the two sections independently and doesn't care which writer produced which, or how.
- Folded leaves get a `DominatorImmediateDominatorAddresses` entry too: their "dominator address" is
  their one real predecessor's address, resolved directly — a folded leaf's predecessor can never
  itself be folded, since a folded node has out-degree 0 by definition and therefore can't be anyone's
  predecessor, so no chained resolution is needed.
- Nodes seeded directly from a GC root (LT's virtual-root children) have no real dominator address;
  written as a `0` sentinel, consistent with how "no value" is represented elsewhere in this format.
- Two stale doc comments were fixed as part of Batch 2a, both pre-dating Stage A's proof that running
  everything before `Finish()` avoids D7's "needs a container rewrite" premise: `DominatorTreeIndexWriter`'s
  class comment, and `CacheSectionId.DominatorReachableAddresses`'s own XML doc.

**2. The dominator child index was already built in memory by `DominatorTreeComputer.Compute` and thrown
away — it didn't need a new algorithm, only a return value.** `childOffsets`/`childTargets`
([DominatorTreeComputer.cs:110-121](../../../src/DumpDetective.Analysis/Traversal/Dominator/DominatorTreeComputer.cs#L110-L121))
were the exact dominator-tree parent→children CSR §5 asked for — built via the same counting-sort
pattern as everything else in this file, previously used only internally for the preorder
traversal/retained-bytes rollup, then discarded when `Compute` returned. Exposing them on
`DominatorTreeComputeResult` (Batch 1) was the only change needed on the compute side; Batch 2b's
`DominatorChildIndexBuilder` is what actually re-keys them into the persisted form described above.

**Where this runs.** All of this belongs inside `DiskBackedObjectIndexWriter.Build`'s step 4, right
after `ReachableGraphWalker.Walk(..., buildCsr: true, ...)` returns (§10.1/§10.3), before `Finish()`.
`DominatorTreeComputer.Compute`'s retained-bytes rollup needs each reachable node's shallow size — which
`WalkWithCsr` doesn't resolve (it only builds the graph structure); today that resolution happens in
`ReachableGraphBuilder.Build`'s post-walk loop via `cache.TryGetObjectMetadata`, unusable mid-Phase-1-build
for the reasons §10.1 already covers in depth. This is the same problem §10.1 already designed a fix
for (reuse the in-memory `SegmentIndexEntry[]` against the not-yet-deleted per-segment scratch files) —
that fix is a hard prerequisite for §10.4, not a parallel, independently-schedulable piece of work.

**Shipped (Batch 1): the fix itself —
[ScratchFileObjectMetadataLookup.cs](../../../src/DumpDetective.Analysis/Indexing/ScratchFileObjectMetadataLookup.cs) —
same two-level binary search as `ObjectAddressLookup`, but takes an explicit
`IReadOnlyList<ScratchSegmentSource>` (a `SegmentIndexEntry` paired with its own three scratch-file
paths) instead of reopening a finalized container, and searches each segment's own scratch file with
local (0-based) record indices instead of `FirstRecordIndex`-relative ones, since these files were
never concatenated. Uses `MemoryMappedFile` per segment (resolves the mmap-vs-`FileStream.Seek+Read`
question §10.8 had left open, in mmap's favor — consistent with every other measured comparison in this
doc). Per-segment open failures are skipped, not fatal, matching every other optional-satellite
contract. **Not yet wired in**: `DiskBackedObjectIndexWriter.Build` still deletes the per-segment
scratch files immediately after concatenation — deferring that deletion when Stage B wants them, and
building the `ScratchSegmentSource[]` array from the writer's existing per-segment loop, is Batch 2
work (needs `buildStageB` to exist first, per §10.3).

### 10.5 Folded-leaf CSR — shipped

`LeafFoldResult` previously exposed only the aggregate `FoldedBytesByNewId`
([LeafFolder.cs:349](../../../src/DumpDetective.Analysis/Traversal/Dominator/LeafFolder.cs#L349)), not
which old-ids were folded into which surviving parent. Implemented as designed: `LeafFolder.Fold` now
also builds `FoldedLeafOffsets`/`FoldedLeafOldIds`, a `(parentNewId → folded old-ids)` CSR, via the same
two-phase counting-sort shape `Fold` already uses for the reduced forward CSR a few lines later — a
first pass counts foldable children per surviving parent (added to the same loop that already fills
`foldedBytesByNewId`), a second pass fills `FoldedLeafOldIds` via cursor-based redistribution. Needed
for two things: `EnumerateRetainedSet` including folded leaves as children (§5's original motivation),
and §10.4's child-index re-keying pass, which needs every folded leaf's address merged into its
parent's child list.

**Deliberately not done yet:** `FoldedBytesByNewId` is *not* dropped — its one live consumer
(`DominatorTreeComputer`'s shallow-size calculation) isn't touched by this change. Per §6/the original
plan it becomes redundant once something actually reads the new CSR instead; that removal is deferred
to whichever future change is that first real reader (§10.4's child-index writer, in Batch 2), not done
speculatively here. Covered by two new `LeafFolderTests` cases (leaves folded under one parent vs.
several under a shared parent, plus the zero-folds case).

### 10.6 Reader side — shipped in full (Batch 3)

`DominatorTreeIndexReader` (§10.4) already covered the `TryGetImmediateDominator` half; Batch 2b added
the bare `DominatorChildIndexReader`/`DominatorTreeMetadataReader`. **Batch 3 adds the fourth persisted
column and the facade:**
- **New `DominatorRetainedBytes` section** (`CacheSectionId = 26`) — exact retained bytes per row,
  computed in Phase 1 (`tree.RetainedBytes[newId]`, or a folded leaf's own shallow size) and persisted
  so `TryGetRetainedBytes` is a binary search, not a per-query subtree walk. This wasn't in the original
  §10.6 plan — the scoping pass for Batch 3 found that walking the child index per query would be
  needlessly expensive for anything near the tree's root, when the exact value was already sitting in
  memory during Phase 1 and just never written down. `DominatorTreeIndexWriter`/`DominatorTreeIndexReader`
  were extended (not replaced) to carry this as a second scalar column alongside idom, since both are
  the same shape (one value per row) — the dominator child index stays its own class, since it's
  structurally different (variable-length CSR, not a fixed column). Backward-compatible: a cache.bin
  from Batch 2a/2b has idom but not this column, and the reader treats that as "unavailable," not
  corrupt.
- **`IDominatorTreeProvider`** (new interface, `DumpDetective.Core.Abstractions`, mirroring
  `IBackwardReferenceProvider`'s/`IReachableAddressProvider`'s shape) — `TryGetImmediateDominator`,
  `TryGetRetainedBytes`, `EnumerateRetainedSet` (an iterative child-index walk, streaming, no resident
  array — this one *is* a real subtree walk, since listing the whole retained set can't be
  precomputed the way the byte count can), `TotalRetainedBytes`, `TryGetRetainedBytesByMethodTable`.
- **`DominatorTreeReaderProvider`** — the concrete facade wrapping all four readers (via
  `DominatorTreeIndexReader`, `DominatorChildIndexReader`, `DominatorTreeMetadataReader`), so a caller
  has one thing to null-check instead of three classes.
- **`IHeapAnalysisCache.TryGetDominatorTreeProvider()`** + `DominatorTreeIndexCache`, mirroring
  `DominatorReachableIndexCache`'s exact lazy-open-once pattern.

### 10.7 `DominatorAnalyzer` migration — shipped (Batch 3)

`TryComputeExactDominatorTree` (renamed `TryReadExactDominatorTree`) no longer runs
`ReachableGraphBuilder.Build` → `DominatorTreeComputer.Compute` at all — it reads
`cache.TryGetDominatorTreeProvider()` and looks up each report candidate's exact retained bytes via
`TryGetRetainedBytesByMethodTable`. **This is what actually closes the double-computation problem
flagged at the start of this whole thread**: Phase 2 no longer recomputes what Phase 1 already
computed and persisted; it reads it. A missing provider (legacy pre-Stage-B cache.bin, Stage B not
gated on, or a failed persist) degrades exactly like the old cap-exceeded/exception paths did — the
heuristic result is returned unaffected, no fallback recompute.

**Cleanup done alongside the migration**, since removing `DominatorAnalyzer`'s live-compute call site
turned two things into confirmed dead code:
- `ReachableGraphBuilder.Build` had exactly one caller in the whole codebase. Deleted; its
  `ResolveGenerationTag` (purely live-ClrMD, no disk-cache dependency, still needed by
  `BuildAndPersistDominatorTree`'s Phase 1 path) was pulled out into its own
  `GenerationTagResolver`, since a class with no `Build` method left in it called "…Builder" would be
  actively misleading.
- `DominatorTreeResult`/`DominatorTreeMode`/`DominatorNodeSnapshot`/`DominatorTypeRollup` — the §10.8
  pending item asking whether these D7-era models fit `IDominatorTreeProvider` is resolved as **no**:
  the actual shipped report integration already uses a different, working shape
  (`DominatorDomainResult.ExactRetainedBytesByTypeName`), and retrofitting the unused D7 models would
  have meant changing a working report path for no functional gain. Deleted as dead code rather than
  adopted.

### 10.8 Pending — needs measurement before shipping

- **`ExactDominatorTreeBudget` review — resolved: deleted, not recalibrated.** Batch 2a's wiring found
  the model was worse than stale — a budget trip could silently corrupt Stage A's reverse-edge index,
  not just skip Stage B. The review concluded no calibrated byte-cost model was worth keeping: it's
  removed entirely, along with `RetentionOptions.ExactDominatorTreeMemoryBudgetBytes`. What replaced
  it: `DiskBackedObjectIndexWriter.Build`'s walk phase now has its own failure isolation (any exception
  discards Stage A's partial state and lets the rest of the index build continue, matching every other
  satellite section's existing pattern), and `ChunkedBuffer<T>.Add` throws before silently overflowing
  `int.MaxValue` instead of a periodic byte estimate trying to predict that in advance. No memory-usage
  ceiling is enforced anymore — a reachable population large enough to actually exhaust memory now
  fails as an ordinary OOM (caught by the same walk-phase isolation) rather than being pre-emptively
  rejected by a heuristic. Real dumps measured so far peak at 6.42GB (§8) — comfortably below any
  machine this runs on; an untested, much larger dump is the only scenario where this trade would
  matter, and that scenario now fails safely (isolated, warned, rest of the build unaffected) rather
  than either silently corrupting or being rejected by a stale heuristic.
- **Dominator child index hub-overflow (§5) — measured, resolved: no capping needed.** Widest single
  dominator-child-index row, measured via a full-scan of `childOffsetsByRow` right after
  `DominatorChildIndexBuilder.Build` (no second pass — the CSR is already resident):

  | Dump | Widest row's direct-child count | Total rows | Total child entries |
  |---|---|---|---|
  | 3.3GB | 178,804 | 6,686,490 | 6,469,153 |
  | 25.6GB | 645,533 | 58,339,936 | 58,189,663 |

  Both are small relative to total row count, and — unlike the reverse-edge index's
  `MaxParentsPerChild` (§8.3), which had to worry about per-*bucket* sort-phase memory during the
  build — a dominator child index row is just a contiguous slice of one on-disk `ulong[]`; reading it
  back doesn't risk the sort-phase memory blowup §8.3's cap was guarding against. No hub-overflow
  routing needed, consistent with §8.3's conclusion for the reverse-edge index.
- **Perf re-measurement of the unified walker — measured; literal old-vs-new A/B no longer
  reproducible.** The two-walker design §8.4 measured against was already deleted before this
  measurement pass (§10.7 removed `ReachableGraphBuilder.Build`, the old Phase-2-only walker's only
  caller), so there's no code left to run the old "two separate passes" side of the comparison
  without reverting deleted work. What was measured instead — the unified walk's real wall-clock with
  `buildCsr: true`, as part of the actual shipped Phase 1 pass:

  | Dump | Unified walk (`buildCsr=true`) | Rest of `BuildAndPersistDominatorTree` | Total Phase 1 (Stage A+B) |
  |---|---|---|---|
  | 3.3GB (6,686,490 nodes) | 19,561 ms | metadata 8,118 ms + fold/LT 4,699 ms + row-map 1,077 ms + idom/retained persist 1,312 ms + child-index 1,259 ms + rollup 377 ms | 81,345 ms |
  | 25.6GB (58,339,936 nodes) | 197,032 ms | metadata 55,802 ms + fold/LT 21,480 ms + row-map 11,813 ms + idom/retained persist 7,286 ms + child-index 7,129 ms + rollup 2,514 ms | 884,173 ms (~14m44s) |

  The walk is a large but not dominant share of the total build (~24% at 3.3GB, ~22% at 25.6GB) —
  affordable at both scales tested. Object counts: 14,620,162 (3.3GB), 87,104,236 (25.6GB).
- **Mid-build metadata lookup — implemented, wired in (Batch 2a), and now measured.**
  `ScratchFileObjectMetadataLookup` (§10.4) is built, wired into `DiskBackedObjectIndexWriter.Build`,
  and unit-tested against synthetic scratch files; picked mmap over `FileStream.Seek`+`Read` per §8.4's
  precedent. Metadata resolution itself took 8,118 ms (3.3GB) / 55,802 ms (25.6GB), see above.
  **Within-segment address monotonicity — confirmed on both real dumps, no exceptions.** Every segment
  in both dumps' scratch files (34 segments at 3.3GB, 60 at 25.6GB) verified strictly increasing —
  `FindRecord`'s binary-search assumption, carried over from `ObjectAddressLookup`'s merged-column
  case, holds for the per-segment scratch files too, not just synthetic test data.
- **Child-index `int` vs. `long` offsets — resolved, no longer open.** Total child-index entries are
  bounded by the original edge count, which `ChunkedBuffer<T>.Add`'s overflow guard (the budget-removal
  fix, above) already guarantees fits safely in `int`. `int[]` offsets are safe by construction, not by
  a fresh size-tier check.
- **Child-index re-keying cost (§10.4) — measured, cheap.** `DominatorChildIndexBuilder.Build` + write:
  1,259 ms (3.3GB) / 7,129 ms (25.6GB) — well under 10% of total Phase 1 build time at both scales, and
  cheaper than the fold+LT phase it runs right after. The shared-row-mapping design's "one O(N log N)
  pass instead of two" claim holds up in practice, not just in theory.
- **`DominatorTreeResult`/`DominatorTreeMode` fit (§10.7) — resolved: they didn't fit, deleted.** The
  actual shipped report path already uses a different, working shape
  (`DominatorDomainResult.ExactRetainedBytesByTypeName`); the D7-era models were dead code and are gone.
- **`IDominatorTreeProvider` query performance — unmeasured on a real dump.** `TryGetImmediateDominator`/
  `TryGetRetainedBytes` are O(log N) binary searches, cheap by construction; `EnumerateRetainedSet` is a
  genuine subtree walk with no upper bound on result size for an object near the tree's root. Whether
  this matters in practice for `DominatorAnalyzer`'s actual query pattern (a handful of
  `TryGetRetainedBytesByMethodTable` calls per run, no `EnumerateRetainedSet` calls at all yet — no
  caller uses it) is unmeasured; revisit once a real caller for `EnumerateRetainedSet` exists (§9's
  audit lists several candidates).

---

## 11. Open questions

Everything from earlier drafts of this doc that's since been resolved has been folded into §4/§7/§8/§10
above as shipped or decided state. What's actually still open (Stage B-specific pending items now live
in §10.8, not duplicated here):

- [ ] **The rare narrower cost case (§8.2):** for a run with no dominator-tree consumer active, the
      fair 3.3GB comparison (~1.8x) is trustworthy; the 25.6GB comparison (~0.70x, favoring Stage A) is
      not, given the memory-pressure confound (§8.5). Resolving cleanly may need a machine with more
      headroom than this one has, or accepting the 3.3GB figure as the more reliable data point. Note
      this comparison's framing changes somewhat once §10.1 ships — with the walks unified, there's no
      longer a standalone "reverse-edge index only" walk to compare against a standalone "Stage B only"
      walk; both are the same walk with a mode flag.
- [ ] Whether the `DenseIdMap`-vs-`Dictionary` wall-clock gap at 25GB and the reverse-edge index's
      extraction-cost jump are the same underlying memory-pressure effect — both show the same
      "far worse than data growth would predict" shape on this machine (§8.5).
- [ ] The garbage→reachable edge split specifically (§8.5) — separate from garbage→garbage noise —
      would size Stage A.5's useful output, though Stage A.5 itself is no longer believed necessary.

---

## 12. §6's root-attribution idea — implementation plan, two phases

§6 flagged that the dominator tree, once built, can be cross-referenced with `RootIndex`'s
`(TargetAddr, RootAddr, Kind)` triples to answer "how much is `Static` roots holding vs. `Handle`
roots" and "how much would become collectible if thread N exited." These are two very different sized
pieces of work — Phase 1 upgrades an existing report's numbers with data that's already fully built by
the time it runs; Phase 2 adds new on-disk data and a new report surface. They're scoped and planned
separately below.

### 12.1 Phase 1 — exact retained bytes by root kind — shipped

Implemented as designed below, no deviations. `DominatorRetainedSetAggregator`
([DominatorRetainedSetAggregator.cs](../../../src/DumpDetective.Analysis/Traversal/Dominator/DominatorRetainedSetAggregator.cs))
is a new, dependency-free static class, unit-tested directly against a fake `IDominatorTreeProvider`
(`DominatorRetainedSetAggregatorTests.cs`, 7 cases: empty set, single target, independent siblings,
direct and indirect ancestor-exclusion, duplicate-address dedup, unreachable-target skip) — no real
dump or `ReachableGraph` fixture needed, since the aggregator only calls the two interface methods.

`GCRootAnalysisProjection.Build` gained an optional `IDominatorTreeProvider? treeProvider = null`
parameter; `GCRootAnalyzer.Analyze` resolves it once via `cache.TryGetDominatorTreeProvider()` and
passes it through. Three call sites now prefer the exact value, all falling back to today's
shallow-size/BFS behavior unchanged when `treeProvider` is `null`:
- Per-`RootFinding` retained bytes and severity score — direct `TryGetRetainedBytes` per target, no
  aggregation needed (single object).
- Per-kind (`RootKindSummary`) totals — `DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes`
  over each kind's target-address list, avoiding the double-count risk called out in the original plan.
- Step 3's per-top-N-root BFS retained-size estimate — a target `treeProvider.TryGetRetainedBytes`
  already resolved exactly is now skipped as a BFS candidate entirely rather than spending one of
  `RetainedSizeCandidateSelector`'s `maxCandidatesToWalk` slots on it; `maxCandidatesToWalk` is capped
  to the (now possibly smaller) walk-candidate list length rather than the original `pathN`, which is a
  no-op when `treeProvider` is `null` (the list was already `<= pathN` in that case) and a real BFS-cost
  reduction when it isn't.

`RootKindSummary.IsExactRetainedBytes`, `RootFinding.RetainedBytesIsExact`, and
`RootPathFinding`/`RootPath` (Reporting)'s `RetainedSizeIsExact` are new, additive, default-`false`
fields — no existing construction site outside this feature's own code broke. `GCRootIntelligenceSectionBuilder`
gained an "Exact?" column on both the by-kind and by-severity tables, and its confidence-band/caveat
text now reads "Retained bytes are exact — computed from the dominator tree, not a heuristic estimate."
whenever every kind resolved exactly, instead of unconditionally claiming "heuristic estimates."

**Not yet measured on a real dump** — `DominatorAnalyzerExactTreeRealDumpTests.cs` was extended to also
run `GCRootAnalyzer` and print `ByKind` (with each kind's exact/fallback flag) after Stage B builds, and
that test's own `PrebuildHeapIndex` call was fixed to actually pass `activeAnalyzers`/
`enableExactDominatorTree: true` — it previously called `PrebuildHeapIndex` with neither, which meant
Stage B was silently never built and every "exact" comparison that test's docstring promised was in
fact heuristic-vs-heuristic. Running it (real dump, foreground, one at a time per this project's
real-dump-test rule) is the next step to confirm the wiring end-to-end and get real exact-vs-shallow
numbers, but wasn't run as part of landing this code.

Original design (for reference — matches what shipped):

**What exists today.** `GCRootAnalysisProjection.Build`
([GCRootAnalysisProjection.cs:15-79](../../../src/DumpDetective.Analysis/Utilities/GCRootAnalysisProjection.cs#L15-L79))
buckets every root by `Kind` (`Static`, `Handle`, `Stack`, …) and sums each target's *shallow* size
(`cache.TryGetObjectMetadata`'s `size` out-param) into `RootKindSummary.EstimatedRetainedBytes` — a
name that already promises "retained," but the value today is shallow. Separately,
`GCRootAnalyzer.Analyze`'s step 3
([GCRootAnalyzer.cs:34-132](../../../src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs#L34-L132))
runs a capped BFS (`RetainedSizeCandidateSelector.SelectAndCompute`) to *estimate* retained bytes for
only the top `PathSearchTopN` roots by severity — this is exactly the heuristic this whole redesign
exists to replace with an O(log N) exact lookup, for every root, not just the top N.

**The double-counting problem, and why it's solvable exactly.** A `RootKindSummary` bucket, or the
whole-report total, sums retained bytes across *multiple* root targets. If target A's dominator
subtree contains target B (B is a descendant of A in the dominator tree — reachable from A through the
object graph, not just independently rooted), naively summing `TryGetRetainedBytes(A) +
TryGetRetainedBytes(B)` double-counts every byte in B's subtree. This is decidable exactly, cheaply,
using only what `IDominatorTreeProvider` already exposes: dominance is a tree, so for any two nodes,
either one is a strict ancestor of the other or neither dominates the other — never a partial overlap.
So for a set of targets, the exact answer is: for each target, walk `TryGetImmediateDominator` upward;
if that walk reaches another target in the same set before reaching the virtual root (idom `0`), drop
it (it's already counted inside that other target's subtree); sum `TryGetRetainedBytes` over the
survivors. Chain depth is small in practice (§8's dumps show dominator trees are shallow near any given
node — most objects are a handful of hops from a GC root), so this is cheap: O(targets × average chain
depth) `TryGetImmediateDominator` calls, each O(log N).

**New reusable API — not buried in `GCRootAnalysisProjection`.** §9's audit lists several other
candidates for exactly this "sum retained bytes across a set of targets without double-counting"
operation (`EventLeakAnalyzer`'s per-subscriber-group totals, `CollectionAnalyzer`'s wasteful-collection
totals), so it belongs on `IDominatorTreeProvider` itself or a small sibling helper, not inline in one
analyzer's projection code:

```csharp
namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Sums <see cref="IDominatorTreeProvider.TryGetRetainedBytes"/> across a set of targets without
/// double-counting when one target's dominator subtree contains another — see §12.1
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) for why this is decidable
/// exactly rather than approximated.
/// </summary>
internal static class DominatorRetainedSetAggregator
{
    /// <summary>
    /// Returns the exact retained-byte total for the *union* of <paramref name="targets"/>' dominator
    /// subtrees. Targets not reachable when the tree was built are silently skipped (same "not an
    /// error" contract as <see cref="IDominatorTreeProvider.TryGetRetainedBytes"/>).
    /// </summary>
    public static ulong ComputeExclusiveRetainedBytes(
        IDominatorTreeProvider provider, IReadOnlyList<ulong> targets)
    {
        // 1. Build a HashSet<ulong> of targets for O(1) ancestor-chain membership checks.
        // 2. For each target, walk TryGetImmediateDominator upward (bounded by the tree's depth,
        //    stopping at idom == 0) checking membership in that set at each hop; if a hop lands on a
        //    different member of the set, this target is a descendant of another survivor — skip it.
        // 3. Sum TryGetRetainedBytes(survivor) over the targets that survive step 2.
    }
}
```

This is a pure function over an already-open `IDominatorTreeProvider` — no new persisted data, no new
`CacheSectionId`, no gating changes. It's placed in `Traversal.Dominator` (same namespace as
`DominatorTreeComputer`/`DominatorChildIndexBuilder`) since it's dominator-tree machinery, not a
root-specific concept, even though its first caller is root-kind rollup.

**Wiring into `GCRootAnalysisProjection`.** `Build`'s signature gains an optional
`IDominatorTreeProvider? treeProvider` parameter (`cache.TryGetDominatorTreeProvider()`, resolved once
by the caller, same as every other `TryGetXProvider()` call site in this codebase):
- **Per-kind totals:** after the existing per-root loop populates `kindBytes` with shallow sizes,
  when `treeProvider` is non-null, additionally group each kind's *target addresses* into a list and
  call `DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes` once per kind, replacing that
  kind's `kindBytes` entry. When `treeProvider` is null (legacy cache.bin, Stage B not gated on, or
  Stage A/B failed to persist — same degrade contract §10.7 already established for
  `DominatorAnalyzer`), keep today's shallow-size sum unchanged. `RootKindSummary` gains a bool,
  `IsExactRetainedBytes`, so the report can label which numbers are exact vs. shallow-size estimates —
  mirrors `RootPathFinding.RetainedSizeWasWalked`'s existing "was this actually computed or just
  defaulted" precedent in the same model file.
- **Per-`RootFinding` retained bytes (replaces the BFS estimate for the common case):** in
  `GCRootAnalyzer.Analyze`'s step 3, before running `RetainedSizeCandidateSelector.SelectAndCompute`'s
  capped BFS, try `treeProvider?.TryGetRetainedBytes(f.TargetAddress, out ulong exact)` for each of the
  top `PathSearchTopN` roots; only fall through to the BFS heuristic for targets where that returns
  `false` (unreachable-when-built or no provider at all). This removes the `MaxBfsNodes`/`MaxBfsDepth`
  cap and `PathSearchCapped`/`PathSearchCappedCount`'s reason for existing for the exact-tree case —
  those fields stay (still needed for the no-Stage-B fallback path and for `PathTypeNames`'s own
  forward-BFS, which this doesn't replace), but `RootPathFinding.RetainedSizeWasWalked` should be
  joined by (or repurposed as a three-state) marker distinguishing "walked (heuristic)" from "exact"
  from "unavailable," so the report can say which kind of number it's showing.

**Effort and risk.** No new format, no new gating, no new analyzer registration — a signature addition
to an existing utility, a new small static helper class, and a conditional branch in an existing
analyzer's already-existing step. Testable with synthetic `ReachableGraph`/`DominatorTreeComputeResult`
fixtures the same way `DominatorChildIndexBuilderTests` already exercises `DominatorChildIndexBuilder`
— no real dump needed to verify `DominatorRetainedSetAggregator`'s ancestor-exclusion logic, only to
confirm the wiring end-to-end (existing `DominatorAnalyzerExactTreeRealDumpTests`-style test, or a new
one, extended to also print `GCRootDomainResult.ByKind`).

### 12.2 Phase 2 — per-thread retained bytes ("what would thread N's exit free") — shipped (index + provider), report surface deferred

Implemented as designed below, with the deviations noted inline. Not bundled into Phase 1's change,
as originally planned.

**Shipped:**
- New additive `CacheSectionId.RootStackThreadAttribution = 27` — independent section, not a second
  trailer bolted onto `Roots`' single `IndexHeader.Reserved` slot, exactly as designed.
- `RootStackThreadIndexWriter`
  ([RootStackThreadIndexWriter.cs](../../../src/DumpDetective.Analysis/Indexing/Satellite/RootStackThreadIndexWriter.cs)) —
  loops live (`thread.IsAlive`) `heap.Runtime.Threads`, each thread's own `EnumerateStackRoots()`
  (confirmed via reflection against the installed ClrMD 4.0.732401 package that this returns
  `IEnumerable<ClrStackRoot>`, not `ClrRoot` — same `Address`/`RootKind` shape, plus `StackFrame`),
  writing fixed 16-byte `RootAddr(8) | OSThreadId(4) | ManagedThreadId(4)` records — no padding
  needed (unlike the design's original 24-byte guess), since 8+4+4 is already a clean record size.
  Wired into `DiskBackedObjectIndexWriter.WriteSatelliteSections` immediately after `Roots`, same
  `!SkipRootIndexBuild` gate, own try/catch/warning matching every other satellite section.
- `RootStackThreadIndexReader.Read` — loads the whole map into memory, same precedent
  `RootIndexReader.ReadRootIndexFile` already sets. `RootIndexReader.ReadRootIndexFile` gained a
  `CacheContainerReader`-accepting overload (the path-based overload now delegates to it) so a
  caller that already has a container open — `ThreadRetentionReaderProvider`, below — doesn't pay
  for a second, redundant container open.
- `ThreadRetentionReaderProvider`
  ([ThreadRetentionReaderProvider.cs](../../../src/DumpDetective.Analysis/Indexing/Dominator/ThreadRetentionReaderProvider.cs)) —
  cross-references `RootStackThreadAttribution` against `Roots`' Stack-kind entries to build each
  thread's target-address set, then calls §12.1's `DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes`
  once per thread, eagerly, at open time — reused unmodified, exactly as designed; no second
  ancestor-exclusion implementation.
- `IThreadRetentionProvider` (`DumpDetective.Core.Abstractions`) — one method,
  `TryGetRetainedBytesForThread(uint osThreadId, out ulong retainedBytes)`. **Deviation from the
  design's sketch:** `osThreadId` is `uint`, not `int` — matches `ClrThread.OSThreadId`'s actual type
  (confirmed against existing usage in `ThreadAnalyzer.cs`/`ThreadDomainResult.cs`, which already
  treat it as `uint` throughout), avoiding a silent narrowing/sign-mismatch bug the design's
  pseudocode would have introduced.
- `ThreadRetentionIndexCache` (Cache folder) + `IHeapAnalysisCache.TryGetThreadRetentionProvider()` —
  mirrors `DominatorTreeIndexCache`'s lazy-open-once pattern exactly, composing
  `_dominatorTreeCache.TryGetProvider()` as its own dependency (a thread-retention provider can't
  exist without the dominator tree it's built from).
- Tests: `RootStackThreadIndexTests.cs` (reader round-trip, missing-section handling, and an
  end-to-end `ThreadRetentionReaderProvider.TryOpen` case verifying the exact scenario the design's
  caveat called out — one thread's two stack roots where one's target dominates the other's, so the
  thread's total must count it once, not twice) — all against hand-built `cache.bin` fixtures and a
  fake `IDominatorTreeProvider`, no real dump needed.

**Deferred, not shipped:** the "new report surface" (§12.2's original plan suggested a `ThreadAnalyzer`
section). `ThreadAnalyzer` is a substantially more complex, dispatcher-based analyzer
(`IThreadStackScanParticipant`, shared-stack-scan, sampling caps) than `GCRootAnalyzer` was for §12.1,
and threading a cache-backed provider into its per-thread categorization path is a real, separate
change with its own risk — deliberately not bundled into landing the index/provider itself. The
provider is fully usable today via `cache.TryGetThreadRetentionProvider()` for any future caller
(including a `ThreadAnalyzer` section, when someone picks that up), and is validated end-to-end
against a real dump via `DominatorAnalyzerExactTreeRealDumpTests.cs`'s extended §12.2 block, which
prints exact retained bytes for up to 20 live threads that own at least one Stack-kind root.

**Why `RootIndex` alone can't answer this today.** `RootIndexWriter.Write`
([RootIndexWriter.cs:26-186](../../../src/DumpDetective.Analysis/Indexing/Satellite/RootIndexWriter.cs#L26-L186))
persists `(TargetAddr, RootAddr, Kind)` from a single `heap.EnumerateRoots()` pass. For a `Stack`-kind
root, `RootAddr` is the stack slot's address — not a thread identity. Confirmed directly against the
installed ClrMD 4 package (`Microsoft.Diagnostics.Runtime.xml`, `microsoft.diagnostics.runtime`
4.0.732401): `ClrRoot` (the type `EnumerateRoots()` yields) exposes only `Address`/`Object`/
`RootKind`/`IsInterior`/`IsPinned` — no thread back-reference. Thread attribution only exists via
`ClrThread.EnumerateStackRoots()`, called per-thread
(`ClrRuntime.Threads`, reachable from `heap.Runtime` without any new parameter threading through
`DiskBackedObjectIndexWriter.Build`) — confirmed already in use the same way by
`ThreadAnalyzer.CountStackRoots`
([ThreadAnalyzer.cs:603-614](../../../src/DumpDetective.Analysis/Analyzers/ThreadAnalyzer.cs#L603-L614)).

**Cost shape.** Thread counts and per-thread stack-root counts are both small (dozens of threads,
typically well under a thousand stack roots per thread) — nowhere near heap-object-population scale.
This second enumeration is cheap relative to the rest of Phase 1's build, the same way the existing
static-field trailer (`WriteFieldNameTrailer`) is cheap relative to the main columnar object scan.

**On-disk design — a new, independent `CacheSectionId`, not a trailer bolted onto `Roots`.**
`RootIndex`'s own internal format (magic `RTIX`, version, currently 2 for the field-name trailer) has
exactly one `IndexHeader.Reserved` slot, already spent on that trailer's record count — reusing it for
a second, differently-shaped trailer would need either a version bump (invalidating every existing
`Roots` section on next read, same cost the v1→v2 field-name bump already paid once) or a fragile
"try to read past where the last trailer ended, treat short reads as absent" convention. Cleaner:
follow this doc's own established pattern for adding new data (§5, §10.4's Batch 2b) — a brand new,
additive `CacheSectionId` (next available past whatever Batch 3 left off), independent of `Roots`'
internal versioning entirely:

| Section | Shape | Purpose |
|---|---|---|
| `RootStackThreadAttribution` (new) | own `IndexHeader` (magic `RTTA`, version 1) + fixed 24-byte records: `RootAddr(8) \| OSThreadId(8) \| ManagedThreadId(4) \| Pad(4)` | `RootAddr → (OSThreadId, ManagedThreadId)`, built once at Phase 1 build time from `ClrThread.EnumerateStackRoots()`, one pass per thread |

- **Writer** (`RootStackThreadIndexWriter`, sibling to `RootIndexWriter`): after (or alongside)
  `RootIndexWriter.Write`'s existing pass, loop `heap.Runtime.Threads`; for each thread, for each
  `ClrRoot` yielded by `thread.EnumerateStackRoots()`, write `(root.Address, thread.OSThreadId,
  thread.ManagedThreadId)`. No dependency on `RootIndex`'s own record stream — reads a second time from
  ClrMD, not from the just-written `Roots` section, mirroring how `WriteFieldNameTrailer` independently
  re-resolves static fields rather than reusing `Roots`' in-flight state.
- **Reader** (`RootStackThreadIndexReader`, sibling to `RootIndexReader`): loads the whole map into a
  `Dictionary<ulong, (int OSThreadId, int ManagedThreadId)>` at read time — same "fully in memory is
  fine" precedent `RootIndexReader.ReadRootIndexFile` already sets, since this is bounded by root
  count, not object count.
- **Gating:** build unconditionally whenever the `Roots` section itself builds (same
  `!SkipRootIndexBuild` gate, no new marker interface) — this is a cheap satellite index, not something
  that needs its own opt-in the way Stage B's exact tree does.

**Query surface — new method, not a growth of `IDominatorTreeProvider`.** Thread-retained-bytes is a
*root-index* concept layered on top of the dominator tree, not a dominator-tree-native operation the
way `TryGetRetainedBytes`/`EnumerateRetainedSet` are — it belongs on a new small provider, exposed the
same way `IDominatorTreeProvider` is (`IHeapAnalysisCache.TryGetThreadRetentionProvider()`, lazily
opened once, mirroring `DominatorTreeIndexCache`'s pattern):

```csharp
namespace DumpDetective.Core.Abstractions;

public interface IThreadRetentionProvider
{
    /// <summary>
    /// Exact retained bytes for everything reachable only through <paramref name="osThreadId"/>'s own
    /// stack roots — the answer to "how much would become collectible if this thread exited" (modulo
    /// finalization/other GC-root retention keeping some of it alive regardless; see the report-level
    /// caveat below). Computed via DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes (§12.1)
    /// over that thread's Stack-kind root targets — reused unmodified, not a second implementation.
    /// </summary>
    bool TryGetRetainedBytesForThread(int osThreadId, out ulong retainedBytes);
}
```

**The caveat this API must document, not hide.** An object dominated (in the whole-heap dominator tree)
only by thread N's stack roots would indeed become collectible if every one of those roots vanished —
but if the same object is also reachable from a second GC root (another thread's stack, a static field,
a handle), its dominator-tree idom is the virtual root, not any single thread's root, and it's already
correctly excluded from that thread's exclusive retained set by `ComputeExclusiveRetainedBytes`'s own
logic (§12.1) applied to that thread's target set alone — this is why reusing §12.1's aggregator instead
of a bespoke sum is load-bearing, not just convenient. The number this API returns is therefore already
"what's exclusively reachable via this thread" by construction; it does not need (and must not attempt)
extra logic to subtract other threads' contributions.

**New report surface.** A new analyzer or a new section on the existing thread report (`ThreadAnalyzer`
already owns per-thread stack/state reporting) that, for each live thread, calls
`TryGetRetainedBytesForThread` and surfaces "would free ~X MB if this thread exited" alongside existing
stack-trace/blocking findings — natural fit next to `ThreadAnalyzer`'s existing per-thread rows rather
than a standalone analyzer.

**Effort and risk — larger than Phase 1, still bounded.** New on-disk section (additive, no
`FormatVersion` bump — same low-risk shape §5/§10 already used repeatedly), new writer/reader pair
(structurally simple, no sort/merge phase — a per-thread loop, unlike the reverse-edge index's
bucket/sort machinery), new provider interface, new report wiring. No new algorithmic risk — the hard
part (exact, non-double-counting retained bytes for a set of targets) is Phase 1's
`DominatorRetainedSetAggregator`, reused as-is. Recommend building this only once Phase 1 has shipped
and `DominatorRetainedSetAggregator` has a real caller to validate its correctness against.
