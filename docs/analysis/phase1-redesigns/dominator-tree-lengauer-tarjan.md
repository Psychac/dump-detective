# Dominator Tree Over the Whole Live Heap (Lengauer-Tarjan)

Design doc for audit item P3-2 (`dominator-analyzer-audit.md` roadmap row 426): *"Investigate
Lengauer-Tarjan dominator tree over Gen2+LOH subgraph."* Goal: **exact** retained-byte semantics
matching dotMemory/VS Memory Profiler's "Dominated Memory," not a further-bounded approximation.

Supersedes §4 ("A Bounded True Dominator Computation Over the Candidate Closures Only") in
[dominator-analyzer.md](dominator-analyzer.md), which computes an exact tree but only over the
*union of top-K BFS closures* — correct for those K roots, blind to everything else.

**Why not the current heuristic:** `DominatorAnalyzer`'s existing retained-byte number
(`RetainedSizeCandidateSelector` → `BoundedGraphWalk.ComputeExclusiveRetained`) is a bounded-BFS
heuristic over a shared `visited` set across the top-K reference-count candidates — whichever
candidate's walk reaches a node first "claims" it. Order-dependent, capped at `maxBreadth`/`maxDepth`
(10K/20), and only computed for the current run's top-K: an object that's the *actual* sole
dominator of 50MB of data but isn't itself in the top-K never gets credit. A dominator tree answers
the real question exactly: node `D` dominates `N` iff every path from the root set to `N` passes
through `D`; retained bytes for `D` = sum of shallow sizes in `D`'s dominator-tree subtree.

All numbers below are measured with [tools/DominatorSpike](../../../tools/DominatorSpike) against
two real dumps (3.27GB, 25.63GB), single-threaded, foreground, one dump at a time, unless a
paragraph says otherwise.

**Companion docs:**
[dominator-tree-implementation-plan.md](dominator-tree-implementation-plan.md) (phasing) and
[dominator-tree-memory-profile.md](dominator-tree-memory-profile.md) (where the exact path's memory
actually goes, the `GC.GetTotalMemory` measurement trap that misled two entries in this doc, and the
D6 constant correction — **read that one before trusting any memory figure here**).

---

## Decisions

**D1 — Whole reachable heap, generation is a report filter only.** Original plan restricted graph
*edges* to Gen2/LOH/POH/Frozen objects, expecting that to meaningfully shrink the graph. Rejected by
measurement: on the 25GB dump, 96.4% of live objects were *already* Gen2/LOH, so scoping saved
almost nothing, while the "both endpoints in scope" edge rule silently dropped ~37% of true
retention paths that flow *through* a Gen0/1 intermediary (e.g., a Gen1 `List<T>`'s backing array
holding Gen2 elements) — worse than the existing heuristic, not better. **Decision:** build the tree
over every object reachable from a GC root, any generation. `ObjectGenerations` (free per-node) only
*tags* each node for report-time filtering; it never decides graph membership.

**D2 — Node set is the reachable population, not the raw heap enumeration.**
`ClrHeap.EnumerateObjects()` includes dead-but-not-yet-swept objects — a real, substantial
population at snapshot time (33-54% of raw object count across the two dumps). These have no root
path and no place in a dominator tree; a correct BFS/DFS excludes them automatically. This is the
correct basis for sizing everything downstream (node cap, memory budget) — smaller than either "raw
heap count" or "Gen2/LOH tag count," which earlier drafts of this doc mistakenly used.

**D3 — Predecessors come from a dedicated uncapped edge collection, never the existing capped reverse index.** 
The existing disk-backed reverse-edge index
(`IBackwardReferenceProvider`, [full-reverse-index-plan.md](full-reverse-index-plan.md)) caps every
object's parent list at `MaxParentsPerChild = 10,000`. Real hub objects have in-degree in the
millions (up to 9.05M observed) — exactly the objects dominator analysis exists to identify. A
truncated predecessor list for them would produce wrong `idom` results for precisely the objects an
engineer would care about most. **Decision:** build a dedicated, uncapped forward+reverse edge pair
scoped to the reachable graph (D4) instead. Out-degree doesn't have this problem — an object has a
handful of fields/array elements (avg 2.35 measured), never millions — so a forward-edge structure
needs no cap at all (relevant to D5).

**D4 — Single-pass edge capture + counting-sort CSR build.** The textbook two-pass CSR build (count
out-degree, then walk again to fill) is measurably the wrong choice: the second ClrMD walk costs
more than it saves — 8.71s vs. a single-pass baseline's 5.97s on the 3GB dump (46% *slower*).
**Decision:** walk each reachable node's references exactly once, capturing `(fromId, toId)` pairs
into a flat buffer while incrementing degree counters inline; build the final forward+reverse CSR
via an O(N+E) counting-sort redistribution pass afterward — zero further dump reads. Measured
199.98s (walk) + 3.16s (redistribution) = **203.14s** on the 25GB dump, vs. 495.46s for a naive
`HashSet`/`Dictionary` baseline (2.44x faster).

**D5 — Persist a forward-edge index from Phase 1 (built, wired, and measured for real).** Phase 1's
existing reverse-index build already reads every object's forward references once (see
[full-reverse-index-plan.md § Phase A](full-reverse-index-plan.md#phase-a--edge-extraction)) before
applying the fanout cap. `DominatorAnalyzer`'s own D4 walk re-reads those same fields a second time,
live, for the reachable subgraph. **Decision, now implemented:** extend Phase 1's existing
extraction to also persist an exact, uncapped forward CSR (`ForwardEdgeBuckets`/
`ForwardEdgeDirectories`/`ForwardEdgeMetadata` — `CacheSectionId` 18-20, additive, no
`FormatVersion` bump, mirroring the `SegmentIndex` precedent). Hooks into the *same*
`ClrObject.EnumerateReferences(carefully: true)` loop `DiskBackedObjectIndexWriter` already runs for
the reverse index — the already-enumerated reference gets batched a second time, keyed by parent
instead of child, no additional ClrMD reads. New subsystem:
[`Indexing/ForwardIndex/`](../../../src/DumpDetective.Analysis/Indexing/ForwardIndex/) (extractor,
sorter, container writer, reader — mirrors `ReverseIndex/`'s proven bucket-partition-sort-merge
shape, minus the fanout-cap bookkeeping D3 established this index doesn't need), plus
`IForwardReferenceProvider`/`ForwardIndexCache`, gated by `DD_SKIP_FORWARD_INDEX_BUILD=1`.

*Consumption side confirmed by spike* (`diskindex` mode, an independent forward-then-invert build):
in-memory BFS + reverse-CSR invert (zero ClrMD) measured at 70.32s/25GB dump vs. D4's own live walk
at 203.14s — a 2.9x speedup — with `E`/max-in-degree matching D4's build exactly (a small `N`
mismatch surfaced during this cross-check turned out to be a separate bug in D4's own root-seeding —
fixed; see [Corrections](#corrections-caught-during-spiking)).

*Build side — now measured for real*, not the earlier from-scratch upper bound (588.85s). Ran the
actual production `DiskBackedObjectIndexWriter.Build()` with and without `DD_SKIP_FORWARD_INDEX_BUILD=1`,
same two dumps, redirected to a scratch cache dir to avoid touching each dump's real `cache.bin`:

| | 3GB dump | 25GB dump |
|---|---:|---:|
| With forward-index build | 25.80s / 4817 MB peak | 720.83s (12:00.8) / 7389 MB peak |
| Without (skip flag) | 23.88s / 3881 MB peak | 617.57s (10:17.5) / 7246 MB peak |
| **True incremental cost** | **~1.94s (~8%)** | **~103.26s (~1.72 min, ~16.7%)** |

**The real marginal cost (103.26s) is ~5.7x smaller than the earlier from-scratch upper bound
(588.85s)** — confirming the original "should be nearly free, the reads already happen" intuition
was fundamentally right, even though "nearly free" undersold it a little: 103s is real, not zero,
but it's a one-time-per-dump cost shared across every analyzer that ends up using this index, not
paid per `DominatorAnalyzer` invocation. This resolves what was Open Question 1 with a real number
from the actual (parallel) pipeline, not a single-threaded proxy.

A structural bonus that came along with this for free: the extraction is address/segment-ordered
(walks `heap.EnumerateObjects()` directly, no reachability ordering needed until *after* the CSR
exists) — the same access pattern Phase 1's index build already parallelizes via `Parallel.For`
across segments, so the 103.26s/25GB-dump figure above already reflects that parallelism, not a
single-threaded estimate. (D4's own live-walk fallback stays single-threaded — it's the fallback
path, not worth the engineering.)

**D6 — Node cap, mid-walk, derived from a memory budget.** Reachable count isn't known until the
walk completes, so the cap is enforced mid-walk (abort once the frontier exceeds `Cap` distinct node
ids), not as a pre-check. Over cap: discard the partial walk, fall back to the existing top-K
heuristic, report `DominatorTreeMode.HeuristicFallback(reason: ...)`. Under cap: run LT, report
`DominatorTreeMode.Exact`. Mirrors the existing `WasCapped`/`ObjectScanCapped` honesty pattern
already used elsewhere in `DominatorDomainResult`.

`Cap` itself is derived from a memory budget, not picked as a raw node count — a fixed ceiling
doesn't scale sensibly across dump sizes.

> ⚠️ **Superseded — the bytes-per-node constant described below is gone entirely.** The ~76 figure was
> derived as "4.14GB structural total ÷ 58.34M nodes, 25GB dump" and called *conservative*, but that
> 4.14GB is the **D4 walk-stage structural sum only** — it omits D8's reduced CSR (built while the
> original is still live), the virtual-root-extended reverse CSR, LT's ~12 working arrays, the
> retained-bytes rollup arrays, and all churn. A floor presented as a ceiling: at 76, a 6GB budget
> admitted `Cap ≈ 85M` nodes for a population needing ~18GB.
>
> Correcting the constant to 220 then failed the *other* way, rejecting the 58.34M-node graph this very
> doc records completing successfully in 218.49s. The root cause is that **per-node cost isn't
> constant** — it *falls* as the D8 fold rate rises (140 B/node at 32% folding, 118 B/node at 46%), so
> no single value prices both dumps.
>
> **Now a two-term model — `150 bytes/node + 12 bytes/edge`** — enforced mid-walk on both terms, so a
> dense graph can no longer slip through on a comfortable-looking node count. **Default budget raised
> 6GB → 20GB**, at which the 25.6GB dump projects 9.68GB (48% of budget) and the ceiling sits near
> ~119M reachable nodes, ~2x the largest dump measured. Full derivation, per-stage accounting and the
> validation tests: [dominator-tree-memory-profile.md § 5](dominator-tree-memory-profile.md#5-fixed-the-budget-model-twice).

Whether 20GB is the right budget remains a policy call, not a technical one. The ratio improved with D8
as predicted — the larger dump's higher fold rate is exactly why it costs less per node than the
smaller one.

**Superseded by [dominator-tree-phase1-integration.md](dominator-tree-phase1-integration.md):** the
append blocker below is dissolved, not solved — the tree build moves into Phase 1's index-build job
itself (before the container closes) instead of running in Phase 2. See that doc before acting on
anything below this note.

**D7 — Persist the computed tree too, not just the input graph.** Reversed from an earlier draft,
which argued against this ("only valid for one snapshot, no reuse case"). That doesn't hold up: this
project's cache-hit fast-path philosophy exists precisely because the *same dump* commonly gets
re-analyzed multiple times (`TypeAggregates` already skips a full rescan on repeat runs), and
computing this tree costs real time (D4: 203s, or D5's target ~70s, plus LT and rollup). **Decision:**
persist `idom[]`; retained bytes are a cheap O(N) rollup over `idom[]` + the already-persisted
`ShallowSize` column, recomputable on read without re-running LT.

**Format:** two parallel `ulong[]` columns — `DominatorReachableAddresses[]` (sorted, one entry per
reachable node) and `DominatorImmediateDominatorAddresses[]` (aligned by index) — mirroring the
existing columnar "Object index" pattern
([binary-format.md](../../binary-format.md#object-index-columnar-format-v2v3)) rather than a
dense-id encoding. A reader binary-searches the first array and reads the dominator address
directly, with no dependency on D5's internal id numbering. Costs 16 bytes/node (~933MB at 58.34M
reachable nodes, 25GB dump) — more than the "~4 bytes/node" this decision originally estimated
(which assumed a dense-id encoding that would need its own coupled id↔address mapping to be useful
to a reader anyway). Consistent with this project's existing "disk is cheap" stance elsewhere
([full-reverse-index-plan.md](full-reverse-index-plan.md)), address-keyed and decoupled wins over
denser-but-coupled.

**`CacheSectionId`:** `DominatorReachableAddresses` (21) / `DominatorImmediateDominatorAddresses`
(22), additive past the forward-index sections (18-20), no `FormatVersion` bump — same
`SegmentIndex`-style precedent as D5. Implemented, and confirmed additive: `CacheContainerWriter`'s
TOC sizing is `Enum.GetValues<CacheSectionId>().Length`-derived, not a hardcoded constant.

**Cache-hit validation:** the standard `DumpContentHash` check (already validated once at container
level) is sufficient — no extra options-dependent invalidation needed. A persisted tree's validity
doesn't depend on what cap value was active when it was computed; `Mode == Exact` already means the
cap didn't bind, so the result is unconditionally correct regardless.

**Write policy:** persist unconditionally whenever `Mode == Exact` — computing `idom[]` is already
the expensive part, writing the already-computed arrays to disk is comparatively cheap, no separate
toggle needed. `HeuristicFallback` runs have no `idom[]` to write in the first place (the walk was
aborted before LT ran) — nothing to decide there, it's trivially skipped.

**Real blocker found during implementation, and now deliberately deferred (2026-08-16) rather than
solved speculatively:** `CacheContainerWriter` is write-once — it always creates a brand-new file
and atomically renames it on `Finish()`, with no support for reopening an already-finalized
`cache.bin` to append a section. D7's whole premise (a *second* run finds the persisted tree and
skips recomputation) needs the section written *after* Phase 1's container write already finished,
since `DominatorAnalyzer` runs in Phase 2 — which as designed would require a full container
rewrite (copying the entire existing file, multi-GB on the dumps this doc measures) or a real change
to the writer to support incremental append.

**Decision: don't solve this yet.** Whether it's worth solving depends entirely on how expensive
computing the tree turns out to be once `DominatorAnalyzer` is actually wired up (Phase 5) and
measured on a real dump (Phase 6) — if the real end-to-end cost is cheap, persistence isn't worth
the append-integration work at all and D7 should be dropped, not just deferred; if it's expensive,
*then* the append problem is worth solving deliberately. The section *format* itself (two columnar
`ulong[]` arrays, sorted-address binary search) is already implemented and round-trip tested against
a standalone container
([`DominatorTreeIndexWriter.cs`](../../../src/DumpDetective.Analysis/Indexing/Dominator/DominatorTreeIndexWriter.cs)/
[`DominatorTreeIndexReader.cs`](../../../src/DumpDetective.Analysis/Indexing/Dominator/DominatorTreeIndexReader.cs))
— that work isn't wasted regardless of which way this goes.

**D8 — Fold single-parent leaves out of the LT node set.** Any reachable node with out-degree 0
(can't dominate anything) *and* in-degree 1 (its `idom` is trivially that sole parent) can be
excluded from LT's node set entirely — fold its shallow size directly into its parent's
retained-bytes accumulator. Purely graph-structural, no `MethodTableHasOutgoingRefs`/type-metadata
lookup needed — D4's existing degree arrays already carry both signals. Nodes with out-degree 0 and
in-degree *>1* (shared leaves — interned strings, cached singletons) **cannot** be shortcut this
way; determining their `idom` is exactly what LT's semidominator computation exists to do.

**Refinement found during implementation**: a GC root itself must never be folded, even when it
structurally matches the out-degree-0/in-degree-1 shape (a root can simultaneously be pointed at by
one other real object *and* have no outgoing references of its own). A root has an "invisible"
incoming edge from LT's virtual root (§Architecture step 1) that the CSR doesn't represent — folding
it away would silently lose its directly-rooted status. `LeafFolder.Fold` takes the walk's `IsRoot`
tracking and excludes root nodes from folding eligibility regardless of degree.

| | 3GB dump | 25GB dump |
|---|---:|---:|
| Leaves (out-degree 0) | 36.7% of `N` | 51.4% of `N` |
| **Foldable (out-degree 0, in-degree 1)** | **31.6% of `N`** | **46.5% of `N`** |
| Shared leaves (no shortcut) | 5.1% of `N` | 4.9% of `N` |
| LT-array memory saved if excluded | 56.5 MB | **723.67 MB** |

Nearly half of all reachable nodes on the 25GB dump qualify, and the fraction *grows* with dump
complexity (31.6% → 46.5%). **Decision: build this**, as a post-CSR-build pass (in-degree isn't
fully known until the single-pass walk completes, so this can't be decided mid-walk). Confirmed
saving is limited to LT's own 7-array working set; going further and also excluding these nodes from
the larger id-map/address structures (D2, ~45 bytes/node — a bigger prize, same ~46.5% population)
needs id renumbering and isn't scoped yet.

**D9 — Exact mode is on by default, gated by an independent flag, not by `AnalysisProfile`.**
`RetentionOptions` (`DominatorAnalyzer`'s options class) already exposes
`Preset(AnalysisProfile.Fast|Balanced|Full)` like every other analyzer, but the profile system is
expected to be simplified (likely to two tiers) at some point — tying exact-mode gating to today's
three-way split would need rework then. **Decision, implemented:**
[`RetentionOptions.EnableExactDominatorTree`](../../../src/DumpDetective.Core/Options/RetentionOptions.cs)
(bool, default `true`), independent of whatever `AnalysisProfile` cases exist — not branched in
`Preset()`. Paired with `RetentionOptions.ExactDominatorTreeMemoryBudgetBytes` (default 6GB, §D6's
memory-budget cap value, also now a real field rather than just a design number). Now consumed:
`DominatorAnalyzer.AnalyzeAsync` reads both fields to gate and size-cap the exact path (Phase 5 in
the implementation plan).

---

## Architecture

1. **Root set** — `cache.GetOrBuildValidRoots(heap)`, same set `RootPathFinder` uses. A synthetic
   virtual root gets one edge to each real root.
2. **Reachability + edge capture** (D4, or D5 once built) — dense `int` ids assigned on first
   discovery via a custom open-addressed `ulong→int` map (~13 bytes/slot vs. `Dictionary`'s ~28-32),
   `(fromId, toId)` pairs captured into a flat buffer, generation tag read once per node.
3. **CSR build** — O(N+E) counting-sort redistribution into forward + reverse CSR arrays, then D8's
   leaf-fold pass over the now-known degree data.
4. **Lengauer-Tarjan** — classic iterative, path-compressed. **Implemented and unit-tested**:
   [`LengauerTarjan.ComputeImmediateDominators`](../../../src/DumpDetective.Analysis/Traversal/LengauerTarjan.cs)
   ([tests](../../../tests/DumpDetective.Tests/Unit/Traversal/LengauerTarjanTests.cs)) — heap-agnostic,
   injected successor/predecessor functions. Working set: 7 `int[]` arrays of length `N` (`idom`,
   `semi`, `ancestor`, `label`, `vertex`, `parent`, `dfsNum`). **Not yet run against real CSR input**
   — only tested against ≤7-node hand-built graphs so far; wall-clock/memory at real scale is unknown.
5. **Retained-bytes rollup** — post-order traversal summing shallow sizes per subtree, rolled up by
   `MethodTable`. Generation filtering happens here (report-time), not at graph-time: the report
   shows only Gen2/LOH/POH/Frozen-tagged rows, but each row's retained-bytes number is exact across
   its whole subtree, including any Gen0/1 objects it dominates.

### Output model

```csharp
public sealed record DominatorTreeResult(
    DominatorTreeMode Mode,             // Exact | HeuristicFallback
    string? FallbackReason,
    int NodeCount,                      // reachable population size (excludes dead-not-yet-swept objects)
    long DeadNotSweptCount,             // raw EnumerateObjects() count minus NodeCount, informational only
    long DeadNotSweptBytes,
    IReadOnlyList<DominatorNodeSnapshot> TopByRetainedBytes,
    IReadOnlyList<DominatorTypeRollup> TopTypesByRetainedBytes
);

public enum DominatorTreeMode { Exact, HeuristicFallback }

public readonly record struct DominatorNodeSnapshot(
    ulong Address,
    string TypeName,
    ulong ShallowSize,
    ulong ExactRetainedBytes,
    ulong? ImmediateDominatorAddress,   // null only for direct children of the virtual root
    int DominatorTreeDepth,
    GenerationTag GenerationTag);       // report-filter only, not graph-time

public enum GenerationTag { Gen0, Gen1, Gen2, Loh, Poh, Frozen, Unknown }
```

Replaces the P2-4 Gen2/LOH sub-table's heuristic retained-bytes column with `ExactRetainedBytes`
when `Mode == Exact`; falls back to today's rendering otherwise. The existing top-K
reference-count table (`DominatorAnalyzer`'s current primary output) is untouched by this doc.

**Implemented** (simpler than the record shape above — no separate `DominatorTreeResult`/
`DominatorNodeSnapshot` wiring turned out to be necessary): `DominatorDomainResult` gained one new
field,
[`ExactRetainedBytesByTypeName`](../../../src/DumpDetective.Analysis/Models/DominatorDomainResult.cs)
(`IReadOnlyDictionary<string, ulong>?`, null when the exact path wasn't attempted/capped/threw).
`DominatorAnalyzer` populates it with one O(N) pass over the reachable graph aggregating exact
retained bytes by `MethodTable` (a folded leaf's own retained bytes = its own shallow size, since a
leaf's subtree is just itself — no separate handling needed), then resolves type names only for the
type names the report already shows (the heuristic's top-K candidates), not every reachable type.
[`DominatorSectionBuilder`](../../../src/DumpDetective.Reporting/SectionBuilders/DominatorSectionBuilder.cs)
overrides the Gen2/LOH sub-table's "Retained" column per-row when a match exists, falling back to
the heuristic estimate otherwise, and adds a one-line caveat when any row used exact data. Verified
against the real 3GB dump: exact bytes resolved for 14/15 top dominator types (the 15th's sample
address didn't resolve to a live object at report time — falls back silently, as designed).

---

## Measured Numbers

| Metric | 3.27 GB dump | 25.63 GB dump |
|---|---:|---:|
| Total live objects (raw `EnumerateObjects()`) | 14.62M | 87.10M |
| Gen2/LOH/POH/Frozen-tagged (report-filter share) | 7.20M (49.2%) | 83.95M (96.4%) |
| **Reachable nodes `N`** (real LT sizing basis, D2) | **6.69M** | **58.34M** |
| Edges `E` | 17.37M | 137.03M |
| Avg out-degree | 2.60 | 2.35 |
| Max in-degree observed | 192,940 | 9,052,241 |
| Single-pass walk + CSR build (D4) | 4.81s | 203.14s |
| vs. naive `HashSet`/`Dictionary` baseline | 5.97s | 495.46s (2.44x slower) |
| vs. rejected two-pass count-then-fill | 8.71s (46% slower than baseline) | not run — already rejected on 3GB result |
| Structural memory (D4, analytic sum) | 527 MB | 4.14 GB |
| Managed memory (`GC.GetTotalMemory`) | 371 MB | 2.75 GB |
| Foldable leaves (D8) | 31.6% of `N` | 46.5% of `N` |
| LT-array memory saved if foldable leaves excluded (D8) | 56.5 MB | 723.67 MB |
| D5 consumption cost (in-memory BFS + invert, zero ClrMD) | 4.72s | 70.32s |
| D5 build cost, spike upper bound (from-scratch whole-heap walk) | 16.15s | 588.85s |
| **D5 build cost, real production pipeline (with vs. without `DD_SKIP_FORWARD_INDEX_BUILD=1`)** | **~1.94s (~8%)** | **~103.26s (~16.7%)** |
| **Exact tree computed end-to-end (Phase 5/6, real `DominatorAnalyzer.AnalyzeAsync` run, LT + rollup + fold)** | **13.75s** | **218.49s** |
| Reachable `N` at Phase 6 runtime (confirms no regression vs. row above) | 6,686,490 | 58,339,936 |
| Leaves folded (D8) at Phase 6 runtime | 2,115,540 (31.6%) | 27,100,729 (46.5%) |
| Exact total retained bytes at GC roots vs. heuristic top-K estimate | 1.02GB vs. 6.9MB | 11.0GB vs. 3.0MB |
| `DominatorAnalyzer.AnalyzeAsync` total (heuristic pass + exact-path attempt) | 16.00s | 244.87s |
| ~~Managed memory delta during the analyzer run~~ | ~~0 (net negative, GC reclaimed)~~ | ~~~1.95GB (well under the 6GB D6 budget)~~ |
| **Exact path, bytes allocated** (replaces the row above) | **2.00GB (300 B/node)**, down from 3.18GB (475 B/node) before the allocation fixes | not re-measured |
| **Exact path, peak live** (analytic, stage-by-stage) | **~1.0GB (~150 B/node)** | not re-measured |

> ⚠️ **The struck-through row was a measurement artifact, not a finding.** "0 (net negative, GC
> reclaimed)" is a `GC.GetTotalMemory` before/after delta — net *heap size*, not allocation. It goes
> negative because the exact path's own allocations trigger gen2/LOH collections that reclaim
> *preceding* analyzers' garbage, while the process commits >1GB of pages that .NET never decommits.
> The same run measures -835MB by that metric and **+2.68GB** by
> `GC.GetAllocatedBytesForCurrentThread`. Use allocated-bytes for cost and peak WS for OOM risk; see
> [dominator-tree-memory-profile.md § 1](dominator-tree-memory-profile.md#1-the-measurement-trap).
>
> The `Structural memory (D4, analytic sum)` row above is also **walk-stage only** and should not be
> read as the exact path's total — that misreading is what produced D6's unsafe 76 B/node constant.

**Wall-clock caveat for every timing row in this table:** the same unchanged work measured between
9.9s and 27.3s across runs in a single session on the 3GB dump, depending on OS page-cache state for
the dump file. Allocated bytes, by contrast, was stable to within 0.001%. Don't read small timing
deltas here as regressions or improvements without controlling for cache state.

**Wall-clock split**: ~69% bookkeeping (`HashSet`/`Dictionary`/`Queue` overhead), ~31% unavoidable
ClrMD I/O — isolated via a zero-bookkeeping ablation (383,537 vs. 117,750 nodes/sec on the 25GB
dump). This is what motivates D5.

**LT + rollup at real scale (Phase 6, resolves the "not yet measured" question below):** confirmed
cheap relative to the rest of the pipeline on the 3GB dump (13.75s) but **not** cheap in absolute
terms on the 25GB dump (218.49s, ~3.6 min) — non-trivial if `DominatorAnalyzer` is re-run repeatedly
against the same dump (report regeneration, trend runs). This is the real cost data D7's
drop-or-solve decision (§D7, Open Question 5) was deliberately waiting on. **Decision: still holding
off on D7** despite the 25GB number leaning toward "worth solving" — revisit when there's bandwidth
for the `CacheContainerWriter` append-integration work.

### Corrections caught during spiking

- An early iteration of the spike tool walked references manually (`type.Fields` + array-element
  iteration) instead of via `ClrObject.EnumerateReferences(carefully: true)`, silently skipping
  struct-typed array elements (e.g. `Dictionary<K,V>`'s internal `Entry[]`) — undercounted real edges
  by roughly half. Caught by cross-checking against an independent prior measurement in
  [root-path-finder.md § 4](root-path-finder.md#41-measured-standalone-prototype-toolsprofilerootpathbackfill)
  (byte-for-byte agreement after the fix). Fixed by switching to the same API
  [`ObjectGraphTraversal.TryFindByPredicate`](../../../src/DumpDetective.Analysis/Traversal/ObjectGraphTraversal.cs)
  already uses correctly elsewhere in this codebase.
- D4's root-seeding accepted any nonzero root address without checking it resolved to a real live
  object, over-counting a handful of phantom nodes (5 on the 3GB dump, 4 on the 25GB dump) — caught
  by D5's independent forward-then-invert cross-check producing a slightly different `N`. Fixed.

---

## Open Questions

1. **D6's memory budget.** Is 20GB the right transient-footprint budget for this analyzer on the
   machines this actually runs on? A risk-tolerance call, not something further measurement resolves
   on its own. **Now a much better-posed question**: the budget is enforced against a validated
   two-term model (`150 B/node + 12 B/edge`) instead of a single constant that was wrong in both
   directions, so the number means something. At 20GB the 25.6GB dump fits at 48% of budget and the
   ceiling is ~119M reachable nodes (~2x anything measured); real peak at that ceiling would be ~13GB.
   The deliberate policy is that **large dumps are not excluded from the exact path** — constrained
   machines should lower the budget rather than have the model lie. See
   [dominator-tree-memory-profile.md § 5](dominator-tree-memory-profile.md#5-fixed-the-budget-model-twice).
2. **D8 extended to the id-map/address structures.** Confirmed savings are LT-array-only (28
   bytes/node). Extending the same fold to D2's larger structures (~45 bytes/node, same ~46.5%
   population) needs id renumbering — a real complexity increase over "id assigned once, stable" —
   and isn't scoped.
3. ~~**LT's own performance at real scale.**~~ **Resolved (Phase 6):** measured against the real
   58.34M-node/25GB CSR input end-to-end (LT + rollup + fold) at 218.49s, and 13.75s at 6.69M
   nodes/3GB — both cheap relative to Phase 1's index-build cost, and no evidence of a scaling
   cliff. Parallel-LT design work is not warranted by this data.
4. **Real-dump correctness comparison against dotMemory/VS Memory Profiler.** Still open — best-effort
   only, needs an external tool run outside this project's control. Phase 6 did confirm `N` matches
   the independently-measured D4/D5 baseline exactly on both dumps (6,686,490 / 58,339,936), and that
   the exact total retained bytes (1.02GB / 11.0GB) is, as expected, dramatically larger than the
   heuristic's top-K-only estimate (6.9MB / 3.0MB) — but a true external-tool comparison is still
   outstanding.
5. **D7's persistence can't just "append a section" — `CacheContainerWriter` is write-once.**
   Discovered during implementation: the container always creates a brand-new file on `Finish()`,
   with no "reopen an already-finalized `cache.bin` and add a section" support. D7's premise — a
   *second* pipeline run finds the persisted tree and skips recomputation — needs the section written
   *after* Phase 1's container write already completed (`DominatorAnalyzer` runs in Phase 2), which
   as designed would require either a full container rewrite (multi-GB copy) or a writer redesign.
   **Phase 6 data is in**: 13.75s on the 3GB dump (cheap) but 218.49s (~3.6 min) on the 25GB dump —
   non-trivial if `DominatorAnalyzer` is re-run repeatedly against the same dump. This leans toward
   "worth solving," but **the decision remains deliberately held off** rather than acted on
   automatically — revisit when there's bandwidth for the append-integration work. The section format
   itself is already implemented and round-trip tested independent of this question.

---

## Rollout Status

All decisions D1-D9 are made. Implementation is tracked phase-by-phase in
[dominator-tree-implementation-plan.md](dominator-tree-implementation-plan.md), not duplicated here
— brief summary:

- ✅ Phase 1 (D5, persisted forward index) — built, wired into `DiskBackedObjectIndexWriter`,
  real production build-cost measured (~1.94s/3GB, ~103.26s/25GB).
- ✅ Phase 2 (D2/D4/D6 reachable-graph builder + D8 leaf-folding) — built, unit-tested.
- ✅ Phase 3 (LT wiring + retained-bytes rollup) — built, unit-tested. Caught and fixed two real
  correctness gaps (virtual-root construction, roots-must-never-fold) before they became bugs.
- ✅ Phase 4 (D9 flag, D7 section format) — `RetentionOptions` fields added;
  `DominatorTreeIndexWriter`/`Reader` built and round-trip tested. **Found a real blocker**:
  `CacheContainerWriter` is write-once, so D7's "persist after Phase 1 already finished" premise
  needs either a full container rewrite or a writer design change — deliberately deferred pending
  Phase 5/6 cost data, see Open Question 5.
- ✅ Phase 5 — wired into `DominatorAnalyzer.AnalyzeAsync` (ship dark: computes and logs the exact
  path via `ILogger<DominatorAnalyzer>?`, gated by `EnableExactDominatorTree`, capped via
  `ExactDominatorTreeMemoryBudgetBytes`; never changes `DominatorDomainResult`'s report-visible
  output; any exception in the exact path is caught and logged, never propagated). One real bug
  (`IndexOutOfRangeException` in the retained-bytes comparison loop) caught and fixed by the Phase 6
  real-dump run below — "ship dark" worked exactly as designed (heuristic result unaffected, bug
  fully contained).
- ✅ Phase 6 — real-dump end-to-end validation, both dumps, one at a time, foreground. `N` matches
  the D4/D5 baseline exactly on both (regression guard passed); exact tree computed in 13.75s (3GB)
  and 218.49s (25GB), well inside the memory budget (~1.95GB vs. 6GB on the 25GB dump); resolves
  Open Question 3 (LT is cheap at real scale). D7's drop-or-solve decision (Open Question 5) has
  real data now but is **deliberately still on hold**. dotMemory/VS Memory Profiler comparison
  (Open Question 4) remains outstanding — best-effort, external-tool-dependent.
- ✅ Report integration — `DominatorDomainResult.ExactRetainedBytesByTypeName` populated by
  `DominatorAnalyzer`, consumed by `DominatorSectionBuilder` to override the Gen2/LOH sub-table's
  "Retained" column when exact data is available; falls back to the heuristic otherwise. Verified
  against the real 3GB dump. D7 persistence remains deliberately on hold (see Open Question 5).
- ⬜ Retiring this as a P3 audit item — still open; worth doing now that report integration has
  landed, but not done as part of this pass.
