# Phase 0 — Deliverable 4: Duplicate Work Analysis

> Scope: **Deliverable 4 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Builds directly on the Heap Scan Mode / Dependencies columns in
> [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md) and the overlap/hidden-coupling
> findings in [Deliverable 3](phase0-deliverable-3-responsibility-matrix.md). This is an
> architectural cost estimate, not a profiled benchmark — costs are stated as order-of-magnitude
> multipliers to be confirmed empirically in Deliverable 8.

> **Correction (2026-07-21)**: the "26 of 36" / "~26x" figures in this section were verified
> against the actual `IAnalyzer` implementations and found to overstate the on-disk index-scan
> count. See the
> [Deliverable 10 correction note](phase0-deliverable-10-platform-roadmap.md#correction--2026-07-21-verified-heap-scan-analyzer-count)
> for the verified breakdown: **9 of 35** analyzers stream the on-disk index (not 26 of 36); a
> further 5 perform a full `ClrHeap.EnumerateObjects()` sweep that this section's numbers
> conflate with index streaming but which a shared index dispatcher cannot address. The
> qualitative finding below (this is real, uncoordinated duplication) still holds — only the
> multiplier is corrected.

## 1. Heap scans — the dominant cost

`IAnalyzer.AnalyzeAsync(AnalysisContext, CancellationToken)` is invoked once per registered
module by the pipeline. Per [CLAUDE.md](../../CLAUDE.md)'s own "never materialize the full heap"
rule, there is no shared in-memory heap snapshot to hand to every analyzer — each analyzer that
needs object-index data must stream it itself via its own `ObjectIndexReader`. Per Deliverable 1's
Heap Scan Mode column:

- **22 analyzers** use `Index` mode (open and stream the full on-disk object index independently)
- **4 analyzers** use `Index+Container` (full object index **plus** a satellite/container index)
- **7 analyzers** use `Cache-only` (no index read — cheap)
- **3 analyzers** use `Direct ClrMD` (segment/JIT/thread APIs — cheap, bounded by segment/thread
  count, not object count)

**9 of 35 analyzers independently open and fully stream the on-disk object index** (verified
count; originally estimated as "26 of 36" — see 2026-07-21 correction above), unless the
orchestration pipeline does single-pass fan-out (worth confirming explicitly in Deliverable 7 —
nothing in the catalog or `IAnalyzer` shape suggests it does, since each `AnalyzeAsync` is an
independent, self-contained call).

**Estimated cost**: for a 10GB+ dump with tens of millions of objects, the object index file
itself is large (proportional to object count — see
[binary-format.md](../binary-format.md)). A single sequential pass is the expected baseline cost.
Absent a shared single-pass dispatcher, actual I/O cost is closer to **~26x** that baseline —
this is very likely the single largest architectural cost in the entire platform, and the one most
directly at odds with the project's own "10GB+ dumps, reasonable runtime" definition of done.

**This is the #1 finding of Deliverable 4.**

## 2. Root traversals

Three analyzers correctly share the `DumpDetective.Analysis.Traversal` BFS primitive:
`GCRootAnalyzer`, `AsyncTaskAnalyzer`, `ReferenceChainAnalyzer`.

Four analyzers perform graph-walk-like work (retained-subgraph size, dominance, static-field
reachability) **without** importing `Traversal` (per Deliverable 1's Dependencies column):
`RetentionAnalyzer`, `DominatorAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer`. Each has
grown its own ad hoc graph-walk logic instead.

**Estimated cost**: each of these 4 does an independent O(V+E) walk over overlapping subgraphs
(static roots and their retained objects are exactly the subgraphs `StaticRootLeakDetector` and
`EventLeakAnalyzer` both walk). Smaller in absolute terms than the index-scan cost above, but
still a 4x multiplier on graph-walk work that is largely the same traversal over the same nodes.

## 3. Type lookups

Raw `MethodTable → ClrType` resolution is **not** duplicated — nearly every analyzer depends on
`HeapAnalysisCache`, which is the correct shared cache per CLAUDE.md's caching rules.

What **is** duplicated is *type classification* logic layered on top of that lookup — matching a
resolved type name against a known pattern (is this `Dictionary<,>`, `Task`, `WeakReference<T>`,
`System.Threading.Timer`, `DbConnection`-derived, etc.). At least 8 analyzers
(`CollectionAnalyzer`, `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer`, `WeakReferenceAnalyzer`,
`DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`, `TimerLeakAnalyzer`) each
implement their own string/regex-based type-name classifier rather than sharing one.

**Estimated cost**: low I/O cost (this runs against already-cached type metadata, not the disk
index), but real maintenance cost — a new BCL/framework type pattern must be taught to 8 different
places instead of one.

## 4. String enumeration

No significant duplication found. `StringAnalyzer` is the sole owner of full string-content
enumeration and fingerprinting. Other analyzers that read individual string field values (e.g.
connection-state field names) do targeted field reads, not heap-wide string enumeration — a
different, cheap operation. Note that `StringAnalyzer`'s own full pass is still one of the 9
verified index scans counted in §1.

## 5. Statistics

Per-type object count/byte aggregation (`sum(size)`, `count` grouped by `MethodTable`/type) is
recomputed independently by at least `MemoryAnalyzer`, `ModuleAnalyzer`, `AppDomainAnalyzer`, and
`ObjectShapeAnalyzer` — each folds this reduction into its own index scan rather than consuming a
shared result.

**Estimated cost**: this is a second-order cost on top of §1 — even if the 9 verified redundant
index scans were collapsed into one shared pass, each of these 4 analyzers would still independently
re-run the same `TypeId → (count, bytes)` reduction over the shared data unless that reduction
itself is promoted to a single computed artifact. Given `TypeIndexBuilder` already exists as part
of the Phase 1 index build (per [architecture.md](../architecture.md)), per-type count/bytes is a
natural candidate to compute **once**, during index build, and persist as a queryable artifact
rather than being re-derived by every consumer.

## 6. Report sections

Four analyzers (`DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`,
`TimerLeakAnalyzer`) each have their own `SectionBuilder` rendering the same shape — a
state/category histogram table — from independently-computed data. This mirrors the "resource
state sampler" duplication in §7 one layer up, in the reporting layer.

Separately, four global/per-analyzer builders touch overlapping "top types by size" ground:
`MemoryAnalysisSectionBuilder`, `ModuleSectionBuilder`, `AppDomainSectionBuilder`, and the global
`TypeSystemSectionBuilder`. Worth confirming in Deliverable 7 whether the global
`TypeSystemSectionBuilder` already subsumes what the per-analyzer builders render, in which case
some of those per-analyzer sections may be redundant output, not just redundant computation.

## 7. Helper logic

Consolidates the duplicate-logic clusters already identified in Deliverables 1 and 3, framed here
by estimated cost and fix:

| Cluster | Analyzers | Duplicated logic | Cost | Fix |
|---|---|---|---|---|
| Resource state sampler | `DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`, `TimerLeakAnalyzer` | classify-by-type-name → sample state field → bucket | Low runtime cost, high maintenance cost (4 copies of one pattern) | Extract one configurable sampler; each analyzer becomes a thin config |
| Static-field sweep | `StaticRootLeakDetector`, `EventLeakAnalyzer` | static-field enumeration + retained-subgraph walk | Medium (duplicate O(V+E) walk over the same statics, counted in §2) | `EventLeakAnalyzer` consumes `StaticRootLeakDetector`'s sweep instead of re-walking |
| Wait-pattern detection | `ThreadAnalyzer`, `HangAnalyzer` | `DetectWaitPattern`-style classification over the same thread/stack data | Low-medium (thread count is small vs. object count, but still a duplicate stack walk) | `HangAnalyzer` consumes `ThreadAnalyzer`'s classification |
| Reflection field-layout cache | `CollectionAnalyzer` (confirmed), likely `EventLeakAnalyzer` (field probing) | ad hoc reflection-based field layout caching | Low runtime, real correctness risk (two caches can drift) | Shared field-layout cache service |

## Cost Summary (ranked)

1. **Redundant full object-index scans (~26x multiplier)** — by far the largest cost; directly
   threatens the project's 10GB+ dump performance goal.
2. **Redundant per-type statistics reduction** — second-order cost layered on #1; cheap to fix
   once #1's shared pass exists.
3. **Redundant graph traversal (4x on static/retention subgraphs)** — moderate cost, moderate fix
   effort (route through `Traversal`).
4. **Duplicate report-section rendering** — presentation-layer cost, no correctness risk, but
   wasted maintenance effort.
5. **Duplicate helper logic (samplers, wait-pattern, reflection caches)** — low runtime cost, but
   the highest *bug-surface* cost, since a fix to one copy silently doesn't apply to the others.
6. **Duplicate type-classification logic** — lowest cost of all, purely a maintenance concern.

## Recommended Shared Infrastructure (preview — expanded in Deliverable 5)

- A **single-pass index scan dispatcher**: one sequential read of the object index per analysis
  run, fanning out each record to registered per-object visitor callbacks. This is the highest-
  priority infrastructure investment in the whole review.
- A **precomputed per-type statistics artifact** produced once by `TypeIndexBuilder` during Phase
  1 index build, consumed (not recomputed) by `MemoryAnalyzer`/`ModuleAnalyzer`/
  `AppDomainAnalyzer`/`ObjectShapeAnalyzer`.
- Mandatory use of the shared `Traversal` primitive for any analyzer doing graph-walk work.
- A shared **typed resource sampler** for the DbConnection/Wcf/Http/Timer cluster.
- A shared **type-name classifier** registry usable by all 8 analyzers currently rolling their own.
- A shared **reflection field-layout cache** usable by `CollectionAnalyzer` and `EventLeakAnalyzer`.
