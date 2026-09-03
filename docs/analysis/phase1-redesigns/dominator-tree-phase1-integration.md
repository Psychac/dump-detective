# Building the exact dominator tree during Phase 1 index build

Computes the exact dominator tree ("what would freeing X free?") during the same Phase 1 pass that
builds the heap index, instead of recomputing it live in Phase 2 per analyzer. Supersedes
[analysis-profile-removal-plan.md §10a](../../refactor/analysis-profile-removal-plan.md#10a-b2-design-the-dominator-tree-retention-provider)'s
in-memory cache-provider idea and [dominator-tree-lengauer-tarjan.md §D7](dominator-tree-lengauer-tarjan.md)'s
"defer to Phase 2, append after `Finish()`" stance — both wrong because everything the tree needs (GC
roots, forward edges, per-object shallow sizes) already exists inside the Phase 1 job, before the
container closes.

## Status: shipped, except

- **§7.1 rare-case cost ratio** — for a run with *no* dominator-tree consumer active, whether Stage A
  or a standalone reverse-edge index is cheaper is unresolved at 25.6GB scale (confounded by memory
  pressure during measurement); the 3.3GB figure (~1.8x, Stage A costlier) is the only trustworthy one.
  Doesn't matter in the common case — every standard, always-registered analyzer wants Stage A anyway.
- **`IDominatorTreeProvider.EnumerateRetainedSet`** has no caller yet (§9's five consumers all only
  needed `TryGetRetainedBytes`, a byte count) — its unbounded subtree-walk cost near the tree's root
  is unmeasured on a real dump. Revisit once a feature needs the member list itself, not just a count.
- **§10 (root-attribution) Phase 2 report surface** — the per-thread retained-bytes index/provider
  (`IThreadRetentionProvider`) is shipped and validated against a real dump, but wiring it into
  `ThreadAnalyzer`'s report output is deliberately deferred to whoever picks it up next.
- Garbage→reachable edge count was never isolated from garbage→garbage noise — moot, since Stage A.5
  (below) was determined unnecessary regardless.
- **§8 cross-type retained overlap** — all three sub-questions shipped 2026-09-03: §8a
  (single-sample containment), §8b (full-population instance counts), §8c (exact shared bytes over
  each pair's topmost instances). No open follow-up scoped under §8.

## Terminology

- **Dominator child index** — parent → children in the *dominance* tree (`idom[]` inverted). Answers
  "what would freeing X free?" (`EnumerateRetainedSet`, `TryGetRetainedBytes`).
- **Reverse-edge index** — child → parents in the raw object graph
  (`ReverseEdgeExtractor`/`Sorter`/`ContainerWriter`/`IndexReader`). Fed by the reachability walk
  (§2), not a separate raw heap scan — there is no other "reverse index."

## 1. Pipeline placement

[DiskBackedObjectIndexWriter.Build](../../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs)
is the single orchestrator. Shipped order:

1. Columnar object scan (`ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations`).
2. Satellite sections: `Handles`, `Roots` (+ `RootStackThreadAttribution`, §10.2), `Tasks`,
   `LargeObjects`, `LohFreeBlocks`.
3. Forward-edge index extract+sort (loose scratch files, not yet merged).
4. **The unified walk** — BFS from GC roots, reading successors from step 3's loose files
   (`ForwardEdgeLooseFileReader`, falling back to live ClrMD via `DD_FORCE_LIVE_CLRMD_WALK=1` or when
   unavailable). Feeds the reverse-edge extractor unconditionally; when `buildStageB` is true (§4),
   also builds the dense-id CSR and runs Stage B end to end (§5) before returning.
5. Reverse-edge index sort+merge.
6. Forward-edge index merge (deletes step 3's loose files — unless Stage B is running and still
   needs the per-segment `Address`/`MethodTable`/`Size` scratch files for metadata resolution; those
   are deleted after Stage B completes instead).
7. `TypeAggregates`.
8. `containerWriter.Finish()`.

## 2. Gating

Two empty marker interfaces (`DumpDetective.Analysis.Pipeline`), checked against `activeAnalyzers`:

- `IRequiresReachableGraphIndex` — implemented by every current `IBackwardReferenceProvider` consumer
  plus the four dominator-tree analyzers (Stage A backs both). Declared but not yet consumed — Stage
  A's construction stays unconditional (gated only by `SkipReverseIndexBuild`), not
  analyzer-driven; making it skippable is a deliberately deferred, separate change.
- `IRequiresDominatorTreeIndex` — `DominatorAnalyzer`, `GCRootAnalyzer`, `StaticRootLeakDetector`,
  `FinalizableObjectAnalyzer`.

```csharp
bool buildStageB =
    reverseEdgeExtractor is not null       // Stage A actually running
    && !SkipDominatorIndexBuild
    && enableExactDominatorTree
    && (activeAnalyzers?.Any(a => a is IRequiresDominatorTreeIndex) ?? false);
```

Plumbed through `BuildHeapIndexStage` → `IHeapIndexBuilder.PrebuildHeapIndex` →
`HeapAnalysisCache`/`HeapIndexCache.PrebuildHeapIndex` → `DiskBackedObjectIndexWriter.Build`, with
safe `null`/`false` defaults so every non-production caller (benchmarks, discrepancy tests) is
unaffected.

## 3. Stage A — reachability walk (shipped)

BFS from GC roots, feeds the reverse-edge index directly, bounded memory throughout, uncapped. Three
decisions, each settled by measurement on a 3.3GB and a 25.6GB real dump:

- **Visited tracking: plain `HashSet<ulong>`.** A bitset/`DenseIdMap` design was built expecting a
  peak-memory win; measured peak working set was within noise (±17MB) in every variant, so the
  decision came down to wall-clock, where `HashSet<ulong>` beat every alternative (~1.4x faster than
  `DenseIdMap` at 3.3GB). `DenseIdMap.cs` deleted, zero remaining callers.
- **`MaxParentsPerChild` deleted outright**, not just raised. Real worst-case hub fan-in measured at
  346K (3.3GB) and 10.76M (25.6GB) — both far under the sort phase's 600MB-per-bucket ceiling (would
  need ~37.5M fan-in alone in one bucket to threaten it). `ReverseIndexMetadata.MaxParentsPerChild`
  kept for on-disk format stability, always written as `int.MaxValue`.
- **Successors: `ForwardEdgeLooseFileReader`** (mmap over the not-yet-merged forward-edge loose
  files) by default, ~2x faster than live ClrMD at 25.6GB after three iterations (naive
  `FileStream.Seek+Read` was 7.5x *slower*; mmap + a decoded-array directory fixed it).
  `DD_FORCE_LIVE_CLRMD_WALK=1` forces the live-ClrMD fallback.

**Stage A.5 (closing garbage-sourced reverse-edge entries) — determined unnecessary.** Every reachable
non-root object has at least one *reachable* parent by construction (BFS found it that way); a
garbage-sourced parent is a dead end for both path-finding and fan-in ranking regardless of which
index style is used. This is also why the reverse-edge index could be **totally replaced** by the
walk-fed version — every current `IBackwardReferenceProvider` consumer now reads from it, with the old
scan-fed build path deleted.

## 4. On-disk format

Additive `CacheSectionId` values, no `FormatVersion` bump.

| Section | Shape | Purpose |
|---|---|---|
| `DominatorReachableAddresses` | sorted `ulong[]` | "Is X reachable from a GC root?" — `IReachableAddressProvider`. |
| `DominatorImmediateDominatorAddresses` | `ulong[]`, row-aligned to the above | `idom[]`, address-resolved; `0` sentinel = direct child of the virtual root. |
| `DominatorRetainedBytes` | `ulong[]`, same row alignment | Exact retained bytes per row — precomputed so `TryGetRetainedBytes` is a binary search, not a per-query subtree walk. |
| `DominatorChildOffsets`/`DominatorChildAddresses` | CSR keyed by dominator-tree parent | `EnumerateRetainedSet` — streaming subtree walk, no resident array. Folded leaves appear as ordinary children. |
| `DominatorTreeMetadata` | JSON | Whole-tree total + per-`MethodTable` retained-bytes rollup. |
| `RootStackThreadAttribution` | own header + fixed 16-byte records | `RootAddr → (OSThreadId, ManagedThreadId)`, built from `ClrThread.EnumerateStackRoots()` per live thread. |

Deliberately not persisted: `graph.MethodTables`/`ShallowSizes` (redundant with `ObjectMethodTables`/
`ObjectSizes`), the raw pre-fold forward CSR, `IsRoot` (redundant with `Roots`), per-node dominator
depth (cheap to add later), `FoldedBytesByNewId` (superseded by the folded-leaf CSR).

`DominatorReachableInDegree` was considered and skipped — redundant with the reverse-edge index's
`EnumerateChildCounts`.

## 5. Stage B — fold + LT + rollup (shipped)

Only runs when `buildStageB` (§2). Reuses one walk with Stage A (`ReachableGraphWalker`, unified from
what were previously two separate walkers — `IncrementalReachableWalker` for Stage A,
`ReachableGraphBuilder`'s standalone walk for Stage B — so `successors()` is called once per reachable
node, not once per stage). Within that pass:

1. Resolve each reachable node's `MethodTable`/`ShallowSize` via `ScratchFileObjectMetadataLookup` — a
   two-level binary search (segment table, then per-segment mmap) against the per-segment
   `Address`/`MethodTable`/`Size` scratch files, whose deletion is deferred until Stage B finishes
   (they'd otherwise be deleted at forward-index-merge time, before Stage B could read them). Falls
   back to live ClrMD only if the scratch files can't be opened.
2. `DominatorTreeComputer.Compute` — fold (`LeafFolder.Fold`, now also emitting a
   `(parentNewId → folded old-ids)` CSR so folded leaves appear as ordinary children in
   `EnumerateRetainedSet`) → Lengauer-Tarjan `idom[]` → child CSR + retained-bytes-per-node + per-type
   rollup, all previously computed internally and discarded — now returned and persisted.
3. Re-key from reduced id to row-aligned address order once, via `DominatorRowMapping.Compute` (N
   binary searches against the already-sorted reachable-address list — no re-sorting).
4. Persist all four sections (§4), delete the deferred scratch files.

**Budget removed, not recalibrated.** `ExactDominatorTreeBudget` (a calibrated byte-cost model, 20 GiB
default) was deleted outright, along with `RetentionOptions.ExactDominatorTreeMemoryBudgetBytes` —
tripping it mid-walk risked leaving Stage A's reverse-edge index silently incomplete once the two
walks were unified. Replaced by two root-cause fixes instead of a heuristic: the walk phase now has
its own try/catch (a failure discards Stage A's partial state and lets the rest of the index build
continue, matching every other satellite section's isolation pattern), and `ChunkedBuffer<T>.Add`
throws before silently overflowing `int.MaxValue` instead of estimating in advance. Real dumps
measured so far peak at 6.42GB; an untested, much larger dump now fails safely instead of either
corrupting or being pre-emptively rejected.

**2026-09-03 correctness fix — per-type rollup double-counted same-typed nesting.** The per-type
rollup above (`DominatorRetainedBytesRollup.Compute`) originally summed each reachable node's own
`RetainedBytes` grouped by `MethodTable`, with no exclusion when one node of type T dominated
another node of the *same* type T — since a node's `RetainedBytes` already sums its whole subtree,
that double-counted every self-referential same-typed chain (a linked list, a tree of same-typed
nodes referencing each other) from O(bytes) up to O(bytes × depth). Fixed by walking the dominator
tree via its own child CSR (`ChildOffsets`/`ChildTargets`, same as `EnumerateRetainedSet`) with a
depth-ordered ancestor stack, crediting a node's bytes to its type's bucket only when no ancestor
already claimed that type — O(1) amortized per node instead of an O(depth) `Idom` walk. Folded
leaves are walked via `LeafFoldResult.FoldedLeafOffsets`/`FoldedLeafOldIds` (§10.5, until now
unused) alongside their surviving parent, getting the same exclusion check against the parent's
ancestor chain. Regression tests: `DominatorRetainedBytesRollupTests` (same-type chain, sibling
non-exclusion, folded-leaf-same-type-as-parent).

`DominatorAnalyzer.TryComputeExactDominatorTree` (renamed `TryReadExactDominatorTree`) no longer
recomputes any of this in Phase 2 — it reads `IHeapAnalysisCache.TryGetDominatorTreeProvider()`
instead, closing the double-computation problem this whole redesign started from. This also made
`ReachableGraphBuilder.Build` (its only caller) and the D7-era `DominatorTreeResult`/`DominatorTreeMode`
models dead code; both deleted.

**Measured (§10.8), both real dumps (3.3GB / 25.6GB):** dominator child index's widest row is 178K /
645K direct children — small relative to ~6.7M / ~58.3M total rows, so no hub-overflow routing was
needed (unlike the reverse-edge index, reading back a CSR slice doesn't risk the sort-phase memory
blowup that guard was for). Unified walk is ~24% / ~22% of total Phase 1+Stage-B build time (19.6s /
197s out of 81.3s / 884s); the rest (metadata resolution, fold+LT, row-mapping, persistence,
child-index build, rollup) is comparably cheap at both scales. Child-index re-keying specifically:
1.3s / 7.1s, under 10% of total.

## 6. §9 — retained-bytes consumers beyond the four dominator analyzers (shipped)

Every consumer of retained-size-shaped data, wired to `TryGetRetainedBytes`, each degrading to its
pre-existing shallow-size heuristic when the tree is unavailable:

- **`EventLeakAnalyzer`** — exact per-subscriber retained bytes for the capped `TopInstances` list
  (real addresses only exist there); the whole-group `EstimateGroupRetainedBytes` fold, which only has
  type counts, stays on the shallow-size average.
- **`GCHandleAnalyzer`** — a correctness fix, not an enhancement: `totalPinnedRetainedBytes`/
  `totalAsyncPinnedRetainedBytes` were previously the pinned target's own shallow size, mislabeled as
  "retained." Now exact per handle when available; `PinnedRetainedBytesIsExact`/
  `AsyncPinnedRetainedBytesIsExact` are true only when every contributing handle resolved exactly.
- **`WeakReferenceAnalyzer`** — additive `AliveWeakTargetsRetainedBytes`, alongside the unchanged
  (honestly-shallow) `WeakReferenceObjectBytes`.
- **`CollectionAnalyzer`** — nullable `RetainedBytes` on the top-N wasteful-collection snapshots.
- **`WcfChannelAnalyzer`/`DbConnectionAnalyzer`** — the biggest gap found: their snapshots previously
  carried no size field at all. Nullable `RetainedBytes` added to both, populated for the capped
  top-N sample list only.

Not part of this pass: the "retention roots" report concept (cross-referencing the tree against
`RootIndex`'s `(TargetAddr, RootAddr, Kind)` triples for a "Static vs. Handle roots" breakdown) — free
once needed, but no analyzer surfaces it yet.

## 7. Root-attribution (§9's idea, two phases)

**Phase 1 — exact retained bytes by root kind (shipped).** `DominatorRetainedSetAggregator` sums
`TryGetRetainedBytes` across a set of targets without double-counting when one target's subtree
contains another (walk `TryGetImmediateDominator` upward from each target; if it lands on another
member of the set before the virtual root, it's already counted — drop it). Wired into
`GCRootAnalysisProjection.Build` for per-kind totals and into `GCRootAnalyzer`'s top-N BFS step (an
exact hit skips the BFS candidate list entirely). `RootKindSummary.IsExactRetainedBytes`/
`RootFinding.RetainedBytesIsExact` mark which numbers are exact vs. heuristic.

**Phase 2 — per-thread retained bytes ("what would thread N's exit free") — index + provider shipped,
report surface deferred.** `RootStackThreadAttribution` (§4) + `ThreadRetentionReaderProvider` cross-
reference stack-kind roots against their owning thread, then reuse Phase 1's aggregator unchanged per
thread. `IThreadRetentionProvider.TryGetRetainedBytesForThread(uint osThreadId, ...)` is exposed via
`IHeapAnalysisCache.TryGetThreadRetentionProvider()` and validated against a real dump, but no
`ThreadAnalyzer` report section consumes it yet — deliberately deferred, since threading a cache-backed
provider into `ThreadAnalyzer`'s more complex, dispatcher-based categorization path is its own
separate-risk change.

## 8. Cross-type retained overlap ("shared subgraph size")

From [dominator-analyzer-audit.md](../phase1/dominator-analyzer-audit.md) P3: "explains why
exclusive retained bytes are 0 for co-dominating types." Written pre-exact-tree, when the answer
came from BFS with a shared visited set across type scans (an ordering artifact, not a real
answer). With the exact tree this is a real, well-defined question — but a different one at the
object level vs. the type level.

**§8a — object level (sample-based) — ✅ shipped 2026-09-03.** Dominance is a tree, so for any two
*specific* objects, one strictly dominates the other or neither does — never partial overlap
(`DominatorRetainedSetAggregator`'s own reasoning, §7 above). `DominatorAnalyzer.FindContainingCandidateTypeName`
scans each Gen2/LOH candidate's already-built dominance chain (§7's `BuildDominatorChain` — no
extra provider calls) for another candidate's sample address; the nearest such ancestor wins.
Exposed as `DominatorDomainResult.ContainingTypeNameByTypeName`, rendered as a new "Shared subgraph
overlap (sample-based)" compact table (`DominatorSectionBuilder`), listing only rows where a
containing candidate was actually found. Explicitly sample-based like every other per-type field on
this path: proves "these two types' *sampled* instances overlap," not "every instance overlaps" — a
missing entry means no candidate was found on the chain within `MaxDominatorChainDepth`, not that no
overlap exists. Tests: `DominatorAnalyzerChainTests` (nearest-ancestor-wins, no-match, self-only-chain,
sentinel-hop-never-matched), `DominatorSectionBuilderTests` (render/omit, Gen2/LOH scoping).

**§8b — type level (full population), instance counts — ✅ shipped 2026-09-03.** A type is a population of objects; two types' combined retained sets (union of each
instance's subtree) can have real *partial* overlap — object O counts toward both A's and B's
retained set iff O's root-to-O ancestor chain contains at least one A-instance and at least one
B-instance, regardless of which is closer. That's a population-level question §8a's single-sample
check can't answer.

**Why the original algorithm sketch (above, now superseded) didn't fit.** It assumed reuse of §5's
build-time depth-ordered tree walk over `DominatorTreeComputeResult.ChildOffsets`/`ChildTargets` —
but that structure only exists in memory during the one-time Phase 1 build
(`DiskBackedObjectIndexWriter.Build`). `DominatorAnalyzer` runs in Phase 2 against the query-time
`IDominatorTreeProvider` facade, which has no whole-tree top-down walk — only point queries
(`TryGetImmediateDominator`, `TryGetRetainedBytes`, `TryGetRetainedBytesByMethodTable`) and
`EnumerateRetainedSet(address)` (subtree-from-one-address, explicitly documented as unbounded near
the tree's root). The candidate-type set itself isn't known until Phase 2 either (it depends on
the heuristic leak-signal pass), so moving the computation earlier wasn't an option. Also dropped:
the K² pairwise-credit-per-node design, since it doesn't arise with the shape actually built below.

**What was built instead.** `DominatorAnalyzer.ComputeCrossTypeOverlap`:
1. Streams the *existing* disk-backed heap index once (`IHeapAnalysisCache.EnumerateIndexedEntriesAsTuples`,
   no ClrMD field walks, no format change) filtering to the Gen2/LOH candidates' `MethodTable`s,
   collecting every instance address of every candidate type — bounded by the new
   `RetentionOptions.MaxCrossTypeOverlapInstancesScanned` (default 2,000,000; an honest safety cap,
   not a silent sample — hitting it sets `DominatorDomainResult.CrossTypeOverlapInstanceScanCapped`,
   folded into the section's confidence band like `ObjectScanCapped` already is).
2. For each collected instance, `FindContainingInstanceTypeName` walks `TryGetImmediateDominator`
   upward (bounded by the existing `MaxDominatorChainDepth`), skipping same-type ancestors (that's
   same-type nesting, not cross-type overlap — see §5) to find the nearest ancestor of a *different*
   candidate type.
3. Aggregates a `(TypeName, ContainingTypeName) -> instance count` — exposed as
   `DominatorDomainResult.CrossTypeOverlapPairs`, rendered as the "Cross-type retained overlap"
   table, scoped to Gen2/LOH candidates same as every other table on this path.

**§8c — exact shared bytes — ✅ shipped 2026-09-03, same change as §8b.** A *byte* total ("A
contributes N bytes to B's retained set") needs the same same-type-double-counting care as §5's
fix — summing every A-instance's own retained bytes toward the (A, B) pair would double-count when
one A-instance dominates another A-instance that's also inside B. Folded into the same walk as §8b
rather than a second pass over the instances: `WalkInstanceAncestry` (renamed from
`FindContainingInstanceTypeName`) now returns *both* the nearest different-type ancestor (§8b's
question) and whether *any* ancestor anywhere in the chain — not just the nearest one, since a
same-type ancestor can sit beyond the nearest different-type one — is also this instance's own
type (§8c's "topmost" question; the two can't share an early-exit since same-type-ancestor
detection needs to scan the whole chain up to `MaxDominatorChainDepth`, not stop at the first
different-type match). `ComputeCrossTypeOverlap` sums `TryGetRetainedBytes` for a pair only over
its topmost instances, alongside the existing per-instance count. Exposed as
`CrossTypeOverlapPair.ContainedRetainedBytes` (0 is a real, honest outcome when every contained
instance for a pair happens to be non-topmost — the doc comment on the record explains why),
rendered as the table's new "Retained" column (an absent cell, not a literal 0, when zero).

Tests: `DominatorAnalyzerChainTests` (`WalkInstanceAncestry`'s same-type-skip-for-containing-type/
same-type-beyond-nearest-different-type/no-match/depth-cap cases, `ComputeCrossTypeOverlap`'s
sibling-both-counted vs. nested-only-topmost-counted byte cases against a fake `IHeapAnalysisCache`
+ `IDominatorTreeProvider`), `DominatorSectionBuilderTests` (render/omit, Gen2/LOH scoping,
capped-flag caveat, zero-bytes-renders-as-absent-not-zero).

No further follow-up scoped under §8 — §8a (single-sample), §8b (instance counts), and §8c (exact
topmost-instance bytes) together cover object-level, population-level, and byte-exact answers to
the audit item's original "shared subgraph size" ask.
