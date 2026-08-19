# Building the exact dominator tree during Phase 1 index build

Status: **design discussion, with one piece already shipped** (§8 — `ReachableGraphWalker`'s
`DenseIdMap` → `Dictionary<ulong,int>` swap). The rest — building the tree during Phase 1, and
whether its byproduct can replace the reverse-edge index — is still a design, not started.

**Terminology, fixed for the rest of this doc:**

- **Dominator child index** — parent → children in the *dominance* tree (`idom[]` inverted). Answers
  "what does removing X free" (`EnumerateRetainedSet`, `TryGetRetainedBytes`).
- **Walk reverse index** — child → parents in the *raw object graph*, sourced from the reachability
  walk, uncapped by construction (§4). Answers "who points at X" for reachable objects. Complementary
  to, not built from, the reverse-edge index below.
- **Reverse-edge index** — the existing, disk-backed structure built during the raw heap scan,
  currently capped at `MaxParentsPerChild = 10,000`. Nothing in this doc requires touching it, though
  §7 explores replacing it and §7.4 notes a cheaper in-place alternative.

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
is the single orchestrator for the whole Phase 1 build. Relevant order today:

1. Columnar object scan — `ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations`.
2. `WriteSatelliteSections` — `Handles`, **`Roots`** (`RootIndexWriter.Write`), `Tasks`, `LargeObjects`,
   `LohFreeBlocks`.
3. Reverse-edge index: extract (during step 1, same object pass) → sort → merge into container.
4. **Forward-edge index**: extract (during step 1) → sort (`ForwardEdgeSorter.SortBucketsAsync`, Phase
   B, writes per-bucket loose `.dat`/`.idx` scratch files) → merge into container
   (`ForwardEdgeContainerWriter.Write`, Phase C — streams the loose files into
   `ForwardEdgeBuckets`/`ForwardEdgeDirectories` verbatim, then deletes them).
5. `TypeAggregates`.
6. `containerWriter.Finish()`.

**New stage: between 4's Phase B (sort) and Phase C (merge + delete scratch).** At that point, GC
roots already exist (step 2), and forward edges already exist as sorted, directory-indexed loose files
on disk (Phase B's output). Build the reachable graph by walking from the roots using a new loose-file
point-lookup reader over those scratch files (mirrors `ForwardEdgeIndexReader`'s binary-search-over-
directory logic, but reads via `FileStream`+`ArrayPool` against loose files instead of a
memory-mapped container section). Write the dominator sections (§5) before letting Phase C run as
normal and calling `Finish()`.

A rerun against the same dump then gets the dominator sections for free on a cache hit, the same way
`TypeAggregates`'s presence already marks a build complete — no special-casing needed.

---

## 3. The staged build

Three independently-gated stages — gating "build the dominator tree" as one all-or-nothing unit would
force every path-search-only consumer to pay for the full, expensive pipeline just for a byproduct
they don't need dominance for.

- **Stage A — reachability walk, incremental and bounded by construction.** BFS from roots, builds the
  walk reverse index. Bounded memory throughout (§4), and uncapped (§4 — no `MaxParentsPerChild`-
  equivalent truncation). Persists on completion, independent of whether Stage B ever runs.
- **Stage A.5 — close the reachable-only scope gap (optional, separately gated).** The walk never
  visits garbage (unreachable) objects, so the walk reverse index has no entry for edges whose
  *source* is garbage. One more sequential pass over the already-captured forward-edge data (no new
  ClrMD reads) closes that gap. **Determined not required** — see §7.
- **Stage B — fold + LT + rollup.** Only runs if an analyzer wants `idom[]`/retained bytes
  specifically. Gated on top of Stage A succeeding.

### Gating

Two marker interfaces (same shape as the existing `IParallelHeapIndexScanParticipant`), checked
against the already-resolved `activeAnalyzers` list
([DumpAnalysisService.cs:53-64](../../../src/DumpDetective.Cli/Execution/DumpAnalysisService.cs#L53-L64)
confirms this list exists before the index build runs):

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
`FinalizableObjectAnalyzer` (all also want Stage B, so implement both). If total replacement of the
reverse-edge index (§7) is ever pursued, every current `IBackwardReferenceProvider` consumer
(`EventLeakAnalyzer`, `ReferenceChainAnalyzer`, `TimerLeakAnalyzer`, `StaticRootLeakDetector`,
`CollectionAnalyzer`) would implement this too, since Stage A would back
`IBackwardReferenceProvider` for everyone.

**`IRequiresDominatorTreeIndex`** — stays narrow regardless of §7's outcome: `DominatorAnalyzer`,
`GCRootAnalyzer`, `StaticRootLeakAnalyzer`, `FinalizableObjectAnalyzer`.

---

## 4. Stage A's construction: incremental and uncapped, by design

Two separate problems, both solved by changing *how* Stage A is built, not by tuning a budget number.

### 4.1 Bounded memory during the walk

The naive implementation (hold every discovered edge in one in-memory array, build the final CSR only
once BFS completes) needs the whole reachable graph resident — forcing a hard memory budget and a
mid-walk abort on the largest dumps. Two changes remove that:

1. **"Have I visited this node" tracking is a bitset, not a hash map.** Instead of a `DenseIdMap`
   (~13 bytes/reachable node), use a plain bitset over the already-sorted `ObjectAddresses` column — 1
   bit per object, located via the same binary-search infrastructure `SegmentIndex`/
   `ObjectAddressLookup` already use. A bitset over the *whole* heap's object count stays small
   regardless of scale. Trade-off: binary search is slower per lookup than a hash probe, but still
   fast in absolute terms.
2. **Discovered edges are written to disk as found, not accumulated in memory** — same shape as
   `ReverseEdgeExtractor` already uses (hash-partition by child address, stream to the bucket's
   scratch file immediately). The BFS frontier only ever holds the current wavefront.

Together, resident memory drops from "the whole reachable graph, edges included" to roughly "one bit
per heap object." **§8 measured this line of attack in practice and found the wall-clock cost of the
bitset approach outweighed the (unproven) memory benefit — the actual shipped fix ended up being
simpler than this section originally proposed.** See §8 for what was actually built and measured.

### 4.2 Uncapped, without reintroducing the bucket-sort risk

Reusing `ReverseEdgeExtractor`'s write path unmodified would reintroduce `MaxParentsPerChild`'s reason
for existing: a hub object's parent list still lands in one bucket regardless of bucket count, and can
still blow the sort phase's in-memory ceiling. The cap was always protecting the *sort* phase, not the
*write* phase — writing more data to a scratch file is just disk I/O, not a memory risk.

1. **Write phase: remove the cap entirely, keep the per-child counting.** `ReverseEdgeExtractor`
   already tracks a live per-child count (`_fanoutPerBucket`) as a side effect of writing — delete only
   the `count >= MaxParentsPerChild` truncation branch. Every edge gets written, unconditionally.
2. **Sort phase: detect hubs from the counts already collected, pull them out *before* the in-memory
   sort.** Any child whose count crosses a hub threshold is excluded from the normal sort and streamed
   directly into a dedicated overflow file instead (parent order is irrelevant within one child's own
   group, so no sort is needed there). What's left is safely bounded and gets the normal sort.
3. **Merge phase: one more small directory** mapping hub child address → overflow file/section. A
   reader checks the normal bucket first, falling back to the hub-overflow entry if present.

Same three-phase shape (`extract → sort → merge`) the reverse-edge index already uses — a
modification of that pipeline, not a new one. **Not yet built** — see §8 and §10.

### 4.3 This mechanism isn't specific to the walk reverse index

Because it's a change to a generic pipeline shape, the same hub-overflow mechanism could — as a
separate, later decision — retroactively remove `MaxParentsPerChild` from the *existing* reverse-edge
index too, independent of anything else in this doc. See §7.4.

---

## 5. On-disk format

Everything persisted, by stage. All additive `CacheSectionId` values (next available past
`ForwardEdgeMetadata` = 20), no `FormatVersion` bump.

**Stage A — reachable graph:**

| Section | Shape | Purpose |
|---|---|---|
| `DominatorReachableAddresses` | sorted `ulong[]` | already reserved (21), D7 |
| `DominatorReachableInDegree` | `int[]`, aligned to the above | exact, uncapped fan-in count per reachable object — a Stage A byproduct, nearly free |
| Walk reverse index buckets/directories (name TBD) | bucket/directory, mirrors the reverse-edge index's shape, scoped to the reachable subgraph | exact, uncapped parent enumeration for reachable objects (§4) |
| Walk reverse index hub-overflow directory (name TBD) | small directory, hub child address → overflow section offset | §4.2's mechanism |

**Stage A.5** — determined unnecessary (§7); no format entries needed.

**Stage B — dominance tree:**

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

**Open:** whether the walk reverse index and dominator child index end up as genuinely separate
section pairs, and whether the dominator child index needs its own hub-overflow treatment (a single
dominance-tree parent could, in principle, have an enormous number of direct children).

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

## 7. Total replacement of the reverse-edge index

The goal explored here is not a targeted upgrade for one analyzer — it's **deleting
`ReverseEdgeExtractor`/`ReverseEdgeSorter`/`ReverseEdgeContainerWriter`/`ReverseEdgeIndexReader`
outright** and having every current consumer read from Stage A's walk reverse index instead. (Aside,
uncontested: `idom[]`/dominance itself can never do this — `idom[v]` isn't required to be a real
predecessor of `v`. The walk reverse index, a real-edges byproduct of building the tree, is a
different thing and is what this section is about.)

### 7.1 Why this looked promising

1. **Interface-level, this is a clean swap.** Every current consumer talks to
   `IBackwardReferenceProvider` (`TryGetParents`, `EnumerateChildCounts`), never directly to
   `ReverseEdgeIndexReader`. `HeapAnalysisCache.TryGetReverseIndexProvider()` is the only place that
   decides which implementation backs that interface.
2. **The scope gap (garbage objects as children) doesn't matter — proven, not assumed.**
   `RootPathFinder`'s backward search does call `TryGetParents` on garbage nodes discovered mid-search
   (`BidirectionalGraphSearch.ExpandLevel`, [:132-162](../../../src/DumpDetective.Analysis/Traversal/BidirectionalGraphSearch.cs#L132-L162)
   expands multiple hops, not just the always-reachable target). This is harmless: if garbage object G
   were reachable via any real parent P, G wouldn't be garbage — contradiction. So every one of G's
   real parents is also garbage, transitively. Continuing the search past any garbage node can
   therefore never reach a root, under either index — the reverse-edge index burns a few more
   expansion levels discovering the same dead end the walk reverse index reports immediately (no entry
   at all). Same final search outcome either way.
3. **Stage A.5 turned out unnecessary.** Any reachable non-root object has at least one *reachable*
   parent by construction (that's how BFS found it). A garbage-sourced parent can never itself
   continue toward a root, so it's a dead end for path-finding regardless of whether Stage A.5
   recovers it; for fan-in ranking, including garbage-sourced references would arguably *inflate* the
   signal with objects about to be collected. Confirmed exhaustively, not just plausibly: `TryGetParents`
   has exactly one caller in the whole codebase
   ([IndexBackedBidirectionalSearch.cs:114](../../../src/DumpDetective.Analysis/Traversal/IndexBackedBidirectionalSearch.cs#L114),
   path-finding), and `EnumerateChildCounts` has exactly one
   (`DominatorAnalyzer.BuildLeakSignalsFromReverseIndex`, fan-in ranking) — no third usage shape to
   account for. Stage A alone gives full behavioral parity with today's reverse-edge index.
4. **No new root-enumeration dependency.** `ReachableGraphBuilder.Build` already sources roots via
   `cache.GetOrBuildValidRoots(heap)`
   ([ReachableGraphBuilder.cs:29](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphBuilder.cs#L29)) —
   the same `RootSetCache` call the reverse-edge index's own path-finding consumers already use. Two
   specific gaps were suspected and ruled out: the 256-frame stack cap
   (`RootSetCache.MaxFramesPerThread`, [RootSetCache.cs:189](../../../src/DumpDetective.Analysis/Cache/RootSetCache.cs#L189))
   only bounds a cosmetic root-labeling feature, not root discovery (`heap.EnumerateRoots()` never
   touches it); and `GCRootKind` coverage is complete
   ([RootIndexReader.cs:146-160](../../../src/DumpDetective.Analysis/Indexing/RootIndexReader.cs#L146-L160)
   maps every value ClrMD 4 defines). Whatever incompleteness `heap.EnumerateRoots()` itself has is a
   ClrMD-level property equally true for today's reverse-edge-index consumers — not new to replacement.

### 7.2 The cost case — measured, and it's a hard wall

Wall-clock was measured across five implementation rounds (full history and numbers in §8). Summary:
Stage A's own cost, isolated from Stage B (fold/LT/rollup), settled at **~2.87x the existing
reverse-edge index's build cost** on a 3.3GB dump and **~2.98x on a 25.6GB dump** — the same ratio at
nearly 8x the scale, so this is not a small-dump artifact. Each implementation round's improvement
came from *removing* complexity (a hand-rolled ordinal cache, then a bitset, were both replaced by
plain `HashSet<ulong>`, which won on both speed and simplicity), not from finding a faster clever
structure — a strong signal that no further data-structure substitution will close the remaining gap.
The gap is the BFS's own irreducible cost (successors() lookups and per-edge bookkeeping that
correctness requires, not an implementation accident) — work the reverse-edge index's raw per-object
field scan never has to do, because it isn't a graph traversal.

**Verdict: total replacement is not free.** Whether it's still worth doing depends on valuing "pay the
BFS cost once, shared with the dominator tree" against "never pay it, keep two structures." See §7.4
for a cheaper alternative that sidesteps this cost question entirely.

### 7.3 The correctness question this framing raises

The existing reverse-edge index is built by scanning every object's field directly — its correctness
is independent of root enumeration. The walk reverse index's correctness is not: an object only gets a
parent-list entry if BFS actually reaches it. §7.1 item 4 shows this isn't a *new* dependency (the
exact tree already has it), but it does mean a second consumer would now share a dependency it
previously didn't have. Not a blocker, given item 4's findings, but worth remembering as the one
genuine trade-off total replacement introduces.

### 7.4 The cheaper alternative, if total replacement isn't worth its cost

§4.2's hub-overflow mechanism could be applied directly to the *existing* reverse-edge index instead
of building Stage A at all — no BFS, no root-enumeration dependency, just the write/sort-phase change
applied to the pipeline that already exists. If `MaxParentsPerChild` is cheap to remove this way, most
of the motivation for total replacement (rather than a smaller in-place fix) disappears, and what's
left of §7 becomes purely about consolidating two structures into one — a weaker argument on its own.
**Not yet built or measured** — worth doing before investing further in §7.2's cost problem.

**Non-negotiable constraint:** `MaxParentsPerChild = 10,000` is not an acceptable permanent end state
under either path. Every option in this section removes the cap entirely; keeping it is not a
cost/accuracy trade-off available to negotiate, regardless of which path (total replacement or this
in-place fix) turns out cheaper.

### 7.5 A smaller, independent win either way

Even without any of §7 shipping, the dominator tree can already independently confirm reachability and
give an exact retained-bytes answer for an object whose reverse-edge-index entry is truncated. A
`searchTruncated` confidence penalty (`Evidence.cs`, 0.8→0.6) shouldn't necessarily also suppress a
retained-bytes figure that's exact for unrelated reasons.

---

## 8. Measurements and what shipped

All measurements below are from real dumps, foreground, single run at a time, per this project's
real-dump-test rules — a 3.3GB dump (`Crash_IIS_BALTSTPRD`) and, for the scale-verification round, a
25.6GB dump (`w3wp.exe_260421_175618.dmp`, 58.3M reachable nodes / 137M edges — ~8.7x/~7.9x the
3.3GB dump's 6.69M nodes / 17.4M edges).

### 8.1 Stage A prototype: four implementation rounds

The question driving all four rounds: can a redesigned Stage A (§4.1's bitset idea, or anything else)
match the existing reverse-edge index's build cost closely enough to justify §7's total replacement?

| Round | Design | Walk-only | Full pipeline (walk+extract+sort+merge) | vs. reverse-edge index (3,503 ms) |
|---|---|---|---|---|
| Baseline | `DenseIdMap` (current production `ReachableGraphWalker`, also builds a CSR the others don't) | 11,511 ms | — | — |
| v1 | Ordinal id via `ObjectAddressLookup.TryGetOrdinal` (binary search) + bitset | 14,994 ms | 16,601 ms | 4.74x |
| v2 | v1 + a bounded direct-mapped ordinal cache (45.7% hit rate) | 11,397 ms | 12,887 ms | 3.68x |
| v3 | Plain `HashSet<ulong>` — no ordinal, no bitset, no `ObjectAddressLookup` dependency | 8,156 ms | **10,045 ms** | **2.87x** |

**v1 → v2:** the ordinal binary search cost more per call than `DenseIdMap`'s O(1) hash probe, at the
same call frequency (both check "have I seen this" once per edge — "resolve once per node" was a
mistaken premise, since neither walker can know a child is a duplicate without checking). A bounded
cache absorbing repeat lookups (real heaps have a power-law in-degree distribution) closed the
walk-only gap.

**v2 → v3:** a same-shape ablation showed plain `HashSet<ulong>` beating the cached-ordinal approach
by ~37% despite allocating more — `ObjectAddressLookup`'s `MemoryMappedViewAccessor` reads cost more
per call than `HashSet`'s pure in-process hashing even on a cache hit path. v3 became the shipped
prototype; the ordinal/bitset code and `ObjectAddressLookup.TryGetOrdinal`/`TotalObjectCount` (added
for v1/v2) were deleted, not kept as a second path.

**Peak memory, measured separately (isolated walk only, no metadata resolution/fold/LT/rollup):**
peak working set was a wash across `DenseIdMap`, the ordinal+cache approach, and `HashSet` — within
±17MB of each other on this dump, no meaningful difference. Allocated-bytes churn *did* differ (v3's
`HashSet` allocated ~66% more than the ordinal+cache approach, which allocated ~34% less than
`DenseIdMap`), but allocation churn isn't peak memory. **§4.1's original motivation — avoiding
`DenseIdMap`'s peak-memory cost — was not demonstrated on this dump at any point in this
investigation.** The eventual win was wall-clock, not memory.

### 8.2 Why v3 didn't go faster: RecordEdge, not the walk

A v4 attempt batched edges per bucket via `ReverseEdgeExtractor.RecordEdgesBatch` (its own doc comment
recommends this to amortize lock overhead) — measured **slower** (11,033 ms total), not faster.
`IncrementalReachableWalker` runs single-threaded, so there's no lock contention to amortize; batching
only added `List<T>` bookkeeping on top of the same per-edge work. Reverted.

A same-shape split of the walk isolated `RecordEdge`'s own cost: **~55% of the walk** (4,305 ms of
7,759 ms), larger than successors()-lookup and visited-tracking combined. A further split, replaying
the real captured edge stream through minimal variants, found the fanout-cap dictionary lookup
(~1,401 ms) costs roughly 2x the raw `BinaryWriter` writes (~707 ms) — and, notably, doing both
together cost *more* than their sum (3,252 ms vs. 2,108 ms), plausibly cache/pipeline interference
between the dictionary's random access and the writer's sequential buffer.

This closes the investigation: the fanout-cap dictionary is required for correctness (knowing each
child's live parent count) and isn't removed by §4.2's hub-overflow redesign either, which keeps
per-child counting regardless. **Stage A's ~2.87x cost against the reverse-edge index is structural**
— the BFS's own irreducible work — not a fixable inefficiency in how it's implemented. See §7.2's
verdict.

### 8.3 25GB verification

The same v3 prototype and the same production baseline were re-measured on the 25.6GB dump (single
foreground run, `DD_SCRATCH_DIR` on a drive with ample free space; ~18 minutes wall-clock total).

| | 3.3GB dump | 25.6GB dump | Growth |
|---|---|---|---|
| Nodes / edges | 6.69M / 17.4M | 58.3M / 137M | ~8.7x / ~7.9x |
| `DenseIdMap` walk (production) | 11,511 ms | 240,985 ms | **20.9x** |
| v3 (`HashSet`) walk | 8,156 ms | 91,528 ms | **11.2x** |
| v3 full pipeline vs. reverse-edge index | 2.87x | 2.98x | — |

Two findings:

1. **§7.2's hard-wall ratio holds at scale** (2.87x → 2.98x) — not a 3.3GB artifact.
2. **`DenseIdMap`'s wall-clock cost scales far worse than `HashSet<ulong>`'s** — 20.9x time growth
   against ~8x data growth, vs. v3's 11.2x. At 3.3GB the two walks were roughly on par; at 25GB, v3 is
   2.6x faster than the current production walk. This is a wall-clock scaling problem, not the
   peak-memory one this design originally worried about (peak working set was still not dramatically
   different between the two at 25GB either).

**A confound, stated plainly:** system memory dropped to 0.5GB free partway through the 25GB run
(observed directly). Under that pressure, Windows can compress or page out portions of `DenseIdMap`'s
larger backing arrays (~1.74GB at 25GB scale vs. ~218MB at 3.3GB), which would produce this exact
result independent of any property of the algorithm itself. A code review of `DenseIdMap`'s resize
path found nothing algorithmically wrong — standard amortized-doubling open addressing, same
final-capacity-to-count ratio at either scale. Whether the 20.9x figure reflects genuine superlinear
scaling or this run's memory pressure was not, and could not cheaply be, resolved — that would need a
re-run with comfortable headroom throughout, a real cost/risk against a 25GB dump on this machine.

### 8.4 Decision: shipped

The confound didn't block shipping, because the fix is safer independent of which explanation is
true. `ReachableGraphWalker.cs`'s `DenseIdMap` was replaced with plain `Dictionary<ulong,int>` — a
one-line constructor swap, since `DenseIdMap`'s `TryGetValue`/`Add` API already matched `Dictionary`'s
exactly, and this walker (unlike `IncrementalReachableWalker`) still needs dense id assignment for its
CSR build, so `HashSet<ulong>` alone wasn't a valid substitute here. `DenseIdMap.cs` was deleted (zero
remaining callers).

Verified: all 42 `Traversal.Dominator` unit tests pass unchanged, and a fresh 3.3GB real-dump run
produced identical results (same node/edge counts, same exact retained-bytes per type) with wall-clock
within the normal run-to-run variance already observed across many earlier runs — no regression at
the scale this analyzer already runs at daily.

This is a real, immediate win independent of §7's total-replacement question — it doesn't require any
of the rest of this doc to ship.

### 8.5 Other measured facts, not yet acted on

- **Garbage-sourced-edge ceiling:** whole-heap forward edges = 33,757,072; reachable-subgraph edges =
  17,367,740 (3.3GB dump). The difference (48.6%) is a *ceiling* on Stage A.5's scope gap, not the gap
  itself — splitting into garbage→garbage (noise) vs. garbage→reachable (the actual gap) was never
  measured, and Stage A.5 was subsequently determined unnecessary (§7.1) regardless.

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

- [ ] Exact insertion point for the loose-file successor reader (§2): in-memory scan buffers vs. the
      just-written Phase-B scratch files — which is cheaper?
- [ ] §4.2's hub-overflow mechanism itself hasn't been built — needed before §7.4's cheaper
      alternative can be evaluated, and before Stage A can honestly call itself uncapped.
- [ ] §4.2's hub threshold — what value, detected how precisely? No data yet on real hub-object
      population sizes on any dump measured so far.
- [ ] §7.4 — check whether §4.2's hub-overflow mechanism is cheap to apply directly to the existing
      reverse-edge index before investing further in total replacement's cost problem (§7.2).
- [ ] Whether the `DenseIdMap`-vs-`Dictionary` wall-clock gap at 25GB reproduces under comfortable
      memory headroom (§8.3's confound) — would clarify whether it's an inherent scaling problem or a
      memory-pressure artifact, though it doesn't change §8.4's shipped decision either way.
- [ ] Walk reverse index vs. dominator child index — confirm these are genuinely two separate
      bucket/directory structures (§5), and whether the dominator child index needs its own
      hub-overflow handling.
- [ ] `DominatorAnalyzer` should stop owning its own `TryComputeExactDominatorTree` build path and
      become a normal reader-consumer like everyone else.
- [ ] Progress/UX: this work's cost currently attributes to `DominatorAnalyzer` (Phase 2). Moving it
      into Phase 1 means it becomes part of "Scan + Index heap" instead — needs its own progress
      sub-phase label so a slow build is attributable.
- [ ] Whether Stage A ships as its own workstream ahead of Stage B, given they're independently gated
      (§3) and Stage A alone already delivers value (exact fan-in counts, uncapped walk reverse index)
      without needing LT to exist yet.
- [ ] The garbage→reachable split specifically (§8.5) — separate from garbage→garbage noise — would
      tell us Stage A.5's useful-output size, though Stage A.5 itself is no longer believed necessary.
