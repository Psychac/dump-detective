# Building the exact dominator tree during Phase 1 index build

Status: **design discussion, with two pieces already shipped** (§8 — `ReachableGraphWalker`'s
`DenseIdMap` → `Dictionary<ulong,int>` swap, and §4.2/§7.4 — the reverse-edge index's
`MaxParentsPerChild` cap deleted outright, no hub-overflow routing needed). Building the tree during
Phase 1 (§1-6) and totally replacing the reverse-edge index with its byproduct (§7) are still
designs, not started — but §7's verdict is now favorable: replacement is close to a pure win in
practice (§7.2), since the case where
it would cost more (no dominator-tree consumer active in a run) is confirmed extremely rare.

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

### 4.2 Uncapped — shipped, and simpler than originally planned

The original plan here was hub-overflow routing: keep the cap's per-child counting but reroute any
child whose count crosses a threshold into a dedicated overflow path, so a single hub object could
never blow the sort phase's 600MB-per-bucket in-memory ceiling. **Before building that, real dumps
were measured to check whether the risk it protects against actually occurs.** It doesn't, at least
not on either dump available: on the 3.3GB dump, real hubs exist (worst case 346,470 true fan-in) but
the whole bucket containing them stays under 77MB; on the 25.6GB dump, the worst real hub measured
**10,757,536 true fan-in** — a genuinely extreme, pathological object — and its bucket still totals
only ~233MB, comfortably under the 600MB ceiling. Working backward, a single object would need
roughly 37.5 million fan-in, alone in its own bucket, to actually threaten the limit — about 3.5x
larger than the most extreme real hub observed. The bucket-count formula
(`ceil(dumpSizeMB / 500)`) scales bucket count with dump size specifically to keep average bucket
size roughly constant, and empirically it absorbs severe real-world skew with real headroom to spare.

**Shipped:** the cap (`MaxParentsPerChild = 10_000`) was deleted outright from
`ReverseEdgeExtractor`/`ReverseEdgeSorter` — no hub-overflow routing, no new pipeline stage. The
existing `MaxBucketSize` (600MB) check remains as the safety net it already was; if it's ever
actually triggered on a real dump, that's the signal to build the overflow mechanism, not before.
This applies to the *existing* reverse-edge index directly (§7.4), and `IncrementalReachableWalker`
(§8.1's v3 prototype) inherited the same fix automatically, since it reuses the same extractor
unmodified. Verified: all affected unit tests updated and passing, and a real 3.3GB-dump run produces
identical dominator-tree results with the now-uncapped index. See §8.6 for the measurement and
`ReverseIndexConstants.MaxParentsPerChild` (now deleted, along with the extractor's now-dead
truncation-tracking fields) for what changed.

### 4.3 This mechanism isn't specific to the walk reverse index

Because it's a change to a generic pipeline shape, the reasoning above (measure real hub sizes before
building routing complexity) would apply the same way to any other structure with the same shape —
not something coupled to reachability walking specifically.

---

## 5. On-disk format

Everything persisted, by stage. All additive `CacheSectionId` values (next available past
`ForwardEdgeMetadata` = 20), no `FormatVersion` bump.

**Stage A — reachable graph:**

| Section | Shape | Purpose |
|---|---|---|
| `DominatorReachableAddresses` | sorted `ulong[]` | already reserved (21), D7 |
| `DominatorReachableInDegree` | `int[]`, aligned to the above | exact, uncapped fan-in count per reachable object — a Stage A byproduct, nearly free |
| Walk reverse index buckets/directories (name TBD) | bucket/directory, mirrors the reverse-edge index's shape (including its now-uncapped write path, §4.2), scoped to the reachable subgraph | exact, uncapped parent enumeration for reachable objects (§4) |

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

**Shipped** (see §7.2a). The goal explored here was not a targeted upgrade for one analyzer — it
was replacing the raw-scan-built reverse-edge index with Stage A's walk-built one, so every
current consumer reads from the walk reverse index instead. (Aside, uncontested: `idom[]`/
dominance itself can never do this — `idom[v]` isn't required to be a real predecessor of `v`. The
walk reverse index, a real-edges byproduct of building the tree, is a different thing and is what
this section is about.)

**Correction to how this shipped, vs. how this section originally phrased it:** the plan below
talks about "deleting `ReverseEdgeExtractor`/`ReverseEdgeSorter`/`ReverseEdgeContainerWriter`/
`ReverseEdgeIndexReader` outright." That turned out to be the wrong framing once
`IncrementalReachableWalker` was actually wired into production
(`DiskBackedObjectIndexWriter.Build`): the walker already wrote into a `ReverseEdgeExtractor`
directly, so those four classes, the on-disk section format, and every consumer-facing type
(`IBackwardReferenceProvider`, `ReverseIndexBackwardReferenceProvider`,
`HeapAnalysisCache.TryGetReverseIndexProvider()`) needed **no changes at all**. What actually
shipped was smaller: the raw per-object scan stopped feeding the extractor, and a BFS walk from
the GC roots (live ClrMD successors, same pattern as `ReachableGraphBuilder.LiveSuccessorsInto`)
feeds it instead, in the same place in the build pipeline where the extractor was already being
sorted and written to disk. The rest of this section is kept as the original decision record.

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

### 7.2a Decision: replaced — shipped

Given §7.1's parity findings and §7.2's verdict below, the decision was to proceed with total
replacement, conditioned on §7.3's one flagged trade-off (walk reverse index correctness depends
on root-enumeration/BFS completeness, unlike the direct-scan index) being an accepted, not a new,
risk — §7.1 item 4 already shows the exact dominator tree carries this dependency today, so no
consumer took on a risk that wasn't already live elsewhere in the system.

**Shipped as:** the raw per-object scan in `DiskBackedObjectIndexWriter.Build` no longer feeds
`ReverseEdgeExtractor`. Instead, right before the extractor's buckets are sorted and written
(where `WriteReverseIndexSections` is called), a BFS walk from the GC roots
(`IncrementalReachableWalker.Walk`, using live ClrMD successors) feeds it — see §7's correction
note above for why `ReverseEdgeExtractor`/`Sorter`/`ContainerWriter`/`IndexReader` and every
consumer-facing type were reused unchanged rather than deleted and rebuilt. Verified via
`IncrementalReachableWalkerTests.Walk_UnreachableSubgraph_GetsNoReverseIndexEntries` (a garbage
subgraph gets no reverse-index entry, a reachable object's parents are recorded exactly) plus the
existing extractor/sorter/reader suites and the full unit test run, all green.

### 7.2 The cost case — depends on which analyzers are active, not a single ratio

Every wall-clock figure measured in this doc (§8.1-8.4) compares Stage A's cost against the
reverse-edge index's cost as if both are always built independently, on every run. **That framing is
wrong given §3's gating design.** `RetentionOptions.EnableExactDominatorTree` defaults to `true`, and
`DominatorAnalyzer` (along with `GCRootAnalyzer`, `StaticRootLeakAnalyzer`, `FinalizableObjectAnalyzer`)
is a standard, always-registered analyzer, not behind an opt-in flag — so in a typical run, Stage A
(and usually Stage B) already has to run *regardless of what this doc decides about the reverse-edge
index*, because something else already wants the dominator tree. The cost question only has one real
shape once that's accounted for:

- **Dominator tree wanted (the default, common case): replacement is close to a pure win.** Stage A's
  cost is already sunk — it's being paid for `idom[]`/retained bytes regardless. The walk reverse
  index is then a free byproduct, and total replacement means `ReverseEdgeExtractor`/`Sorter`/
  `ContainerWriter`/`IndexReader` can be deleted outright, saving their *entire* cost (5,570 ms at
  3.3GB, 178,419 ms at 25.6GB, per §8.4) with nothing paid in exchange. This is not "Stage A costs
  1.8x more" — in this case Stage A costs nothing extra at all, because it was never optional to begin
  with.
- **Dominator tree not wanted (a narrower, non-default case — e.g., analyzers filtered via CLI so only
  path-finding/fan-in consumers like `EventLeakAnalyzer`/`ReferenceChainAnalyzer` run): replacement
  genuinely costs more.** Here, and only here, do the §8.1-8.4 ratios actually apply — total
  replacement would force Stage A's BFS to run *solely* to serve those consumers, instead of the
  cheaper standalone reverse-edge index (~1.8x at 3.3GB, measured with comfortable memory margins; the
  25.6GB figure favoring Stage A is present but confounded — see §8.4).

**Verdict: replacement is close to a pure win in practice.** The narrower case (dominator-tree
consumers excluded from a run) is extremely rare — confirmed, not just assumed — so the §8.1-8.4 cost
ratios that motivated most of this investigation's caution turn out to apply to a corner case that
essentially doesn't happen. In the overwhelming common case, Stage A's cost is already sunk, the walk
reverse index is a free byproduct, and total replacement means deleting
`ReverseEdgeExtractor`/`Sorter`/`ContainerWriter`/`IndexReader` outright with nothing paid in exchange.
§7.4's in-place alternative is still worth having as a fallback (it's cheap and has no BFS/root-
completeness dependency either way), but it's no longer carrying the weight of "the safe choice if
replacement's cost doesn't pan out" — replacement's cost has, for practical purposes, panned out.

### 7.3 The correctness question this framing raises

The existing reverse-edge index is built by scanning every object's field directly — its correctness
is independent of root enumeration. The walk reverse index's correctness is not: an object only gets a
parent-list entry if BFS actually reaches it. §7.1 item 4 shows this isn't a *new* dependency (the
exact tree already has it), but it does mean a second consumer would now share a dependency it
previously didn't have. Not a blocker, given item 4's findings, but worth remembering as the one
genuine trade-off total replacement introduces.

### 7.4 The cheaper alternative — shipped

§4.2's finding (real hub sizes don't threaten the sort-phase memory ceiling) applied directly to the
*existing* reverse-edge index: **`MaxParentsPerChild` is deleted, no hub-overflow routing needed, no
BFS or root-enumeration dependency introduced.** This was the fastest path to an uncapped structure
regardless of how §7.2's total-replacement cost question ever resolves, so it shipped independent of
that decision — see §4.2/§8.6 for what changed and how it was verified.

**Non-negotiable constraint, now satisfied:** `MaxParentsPerChild = 10,000` was never acceptable as a
permanent end state under any framing in this doc. It no longer exists — every consumer of the
reverse-edge index (`EventLeakAnalyzer`, `ReferenceChainAnalyzer`, `TimerLeakAnalyzer`,
`StaticRootLeakDetector`, `CollectionAnalyzer`, `DominatorAnalyzer`) now gets exact, uncapped parent
lists today, regardless of whether §7's total replacement ever happens.

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

This closes the investigation: the fanout-count dictionary is required for correctness (knowing each
child's live parent count for reporting/diagnostics) regardless of any cap-removal strategy —
consistent with §4.2's eventual finding that hub-overflow routing wasn't needed at all; the dictionary
was never the cap's mechanism, just its bookkeeping, and that bookkeeping stays either way.
**Stage A's ~2.87x cost against the reverse-edge index is structural** — the BFS's own irreducible
work — not a fixable inefficiency in how it's implemented. See §7.2's verdict.

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

### 8.4 The reverse-edge index comparison was unfair — corrected

Every ratio in §7.2/§8.1-8.3 compared Stage A's *full* cost against only the reverse-edge index's
sort+merge phase (`WriteReverseIndexSections`), omitting the `RecordEdge` cost it also pays inline
during the shared per-object heap scan that Phase 1 runs regardless. The reverse-edge index has no
separate "walk" the way Stage A does — it piggybacks entirely on that mandatory scan — but its
`RecordEdge` calls during that scan are real, additional, marginal work, not free.

Measured via `DD_SKIP_REVERSE_INDEX_BUILD=1` (an existing escape hatch — no code changes needed),
diffing total Phase 1 build time with and without reverse-edge extraction:

| | Phase 1, with reverse index | Phase 1, without | Reverse-edge index total (fair) | vs. Stage A |
|---|---|---|---|---|
| 3.3GB | 26,192 ms | 20,622 ms | **5,570 ms** | Stage A (10,045 ms) is **~1.8x** |
| 25.6GB | 631,431 ms | 453,012 ms | **178,419 ms** | Stage A (125,081 ms) is **~0.70x — cheaper** |

At 3.3GB this alone nearly halves the previously-reported 2.87x. At 25.6GB it flips the comparison
entirely: subtracting the already-known sort+merge cost (41,911 ms) leaves ~136,508 ms for extraction
alone — a ~66x jump from the 3.3GB extraction cost (~2,067 ms) against only ~7-8x growth in edge
count. That's the same shape of result as `DenseIdMap`'s scaling problem above (§8.3), and plausibly
the same cause: the reverse-edge index's own per-bucket `Dictionary<ulong,int>` fanout counters are
exposed to the same memory-pressure-under-scale effect. Both 25.6GB runs in this comparison were taken
on a machine under real memory pressure (free memory fell to single-digit GB or below during each),
so this specific number is the least trustworthy figure in this investigation — a genuine improvement
in methodology over the number it replaces, but not confound-free. The 3.3GB figure (measured with
comfortable memory margins throughout) is more trustworthy on its own, and it already reopens §7.2's
verdict by roughly halving the gap.

**Practical note on resolving this cleanly:** doing so would need a 25.6GB re-run pair under genuinely
comfortable headroom throughout, not just above the immediate-crash line. On this machine (16GB total
RAM), that may not be achievable at all for a dump this size — the dump itself needs multi-GB resident
state regardless of which design wins, leaving little room to spare. If a cleaner run isn't possible
here, this comparison may need a larger-memory machine to settle, or should be treated as
directionally suggestive (favoring closing the gap, possibly reversing it) rather than conclusive.

### 8.5 Decision: shipped (the `DenseIdMap` → `Dictionary` swap, independent of §8.4)

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

### 8.6 §4.2/§7.4 shipped: the cap is gone, no hub-overflow routing needed

Before building hub-overflow routing (§4.2's original plan), real hub sizes were measured on both
dumps to check whether the risk it protects against actually occurs:

| Dump | Worst real hub (true fan-in) | Its bucket's total size | 600MB ceiling? |
|---|---|---|---|
| 3.3GB | 346,470 | ~77MB (bucket average; hub is a small fraction) | Not close |
| 25.6GB | **10,757,536** | ~233MB | Not close |

Even a genuinely extreme, 10.7-million-parent hub doesn't threaten the ceiling — a single object
would need roughly 37.5 million fan-in, alone in its own bucket, to actually blow it. Measuring this
required temporarily patching `ReverseEdgeExtractor` to keep counting past the cap without changing
its on-disk output (safe, non-invasive), then reverting once the numbers were captured.

**Shipped as a result:** `MaxParentsPerChild` deleted from `ReverseEdgeExtractor.RecordEdge`/
`RecordEdgesBatch` (every edge written, unconditionally) and from `ReverseEdgeSorter` (the `truncated`
on-disk byte is now always `false` — kept for format/reader compatibility, not because anything is
ever incomplete). `ReverseIndexConstants.MaxParentsPerChild` and the extractor's now-dead
truncation-tracking (`_truncatedPerBucket`, `GetTruncatedChildren`) were deleted rather than left as
unused plumbing. `ReverseIndexMetadata.MaxParentsPerChild` is kept for on-disk format stability but
now always written as `int.MaxValue` (an explicit "uncapped" sentinel). No `IBackwardReferenceProvider`
interface or consumer changes were needed — `truncated` flowing through as permanently `false` is
already the correct behavior for every existing caller (confirmed via `ConfidenceScoring.Compute`,
which simply skips an inactive flag with no code change required).

Existing tests asserting the old capped/truncated behavior were rewritten to assert the new uncapped
behavior instead of deleted outright, so the "a hub child's full parent list survives" invariant stays
covered. Verified via the standard 3.3GB real-dump test: identical dominator-tree results, and Phase 1
index build legitimately does modestly more work now (real edge count rose from ~29M to ~33.76M once
nothing is dropped), consistent with correctness rather than a regression.

### 8.7 Other measured facts, not yet acted on

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
- [x] §4.2/§7.4 — done. Real hub sizes measured on both dumps (worst case: 10.7M true fan-in, still
      well under the sort-phase memory ceiling), hub-overflow routing determined unnecessary, and the
      cap deleted outright from the existing reverse-edge index. See §8.6.
- [x] **How often does this tool actually run without any dominator-tree consumer active** (§7.2)?
      Confirmed extremely rare. Total replacement's cost case is close to moot in practice — most runs
      land in the near-free-win case, not the narrower one the §8.1-8.4 ratios apply to.
- [ ] For the rare narrower case where it does still matter: the fair 3.3GB comparison (~1.8x) is reasonably
      trustworthy; the 25.6GB comparison (~0.70x, favoring Stage A) is not, given the memory-pressure
      confound in §8.3/§8.4. Resolving this cleanly may require a machine with more memory than this
      one has, or accepting the 3.3GB figure as the more reliable data point available.
- [ ] Whether the `DenseIdMap`-vs-`Dictionary` wall-clock gap at 25GB (§8.3) and the reverse-edge
      index's extraction-cost jump (§8.4) are the same underlying memory-pressure effect — both show
      the same "far worse than data-growth would predict" shape on this machine.
- [ ] Walk reverse index vs. dominator child index — confirm these are genuinely two separate
      bucket/directory structures (§5), and whether the dominator child index needs its own
      hub-overflow handling.
- [ ] `DominatorAnalyzer` should stop owning its own `TryComputeExactDominatorTree` build path and
      become a normal reader-consumer like everyone else.
- [ ] Progress/UX: this work's cost currently attributes to `DominatorAnalyzer` (Phase 2). Moving it
      into Phase 1 means it becomes part of "Scan + Index heap" instead — needs its own progress
      sub-phase label so a slow build is attributable.
- [x] Whether to totally replace the reverse-edge index (§7) — decided and shipped, see §7.2a.
      `HeapAnalysisCache.TryGetReverseIndexProvider()` needed no change — it already reads from
      the (now walk-fed) `ReverseEdgeExtractor`/`Sorter`/`ContainerWriter`/`IndexReader` pipeline.
- [ ] Whether Stage A ships as its own workstream ahead of Stage B, given they're independently gated
      (§3) and Stage A alone already delivers value (exact fan-in counts, uncapped walk reverse index)
      without needing LT to exist yet.
- [ ] The garbage→reachable split specifically (§8.7) — separate from garbage→garbage noise — would
      tell us Stage A.5's useful-output size, though Stage A.5 itself is no longer believed necessary.
