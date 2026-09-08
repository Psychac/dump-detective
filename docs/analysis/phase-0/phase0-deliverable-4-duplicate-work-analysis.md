# Phase 0 — Deliverable 4: Duplicate Work Analysis

> Scope: **Deliverable 4 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Builds directly on the Heap Scan Mode / Dependencies columns in
> [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md) and the overlap/hidden-coupling
> findings in [Deliverable 3](phase0-deliverable-3-responsibility-matrix.md). This is an
> architectural cost estimate, not a profiled benchmark — costs are stated as order-of-magnitude
> multipliers to be confirmed empirically in Deliverable 8.

> **26-07-2026 re-evaluation note**: §1's finding below is now **resolved for the 9 index-scanning
> analyzers** — verified directly against source (see §1a). The **~26x multiplier** and "#1
> finding" framing describe the *pre-fix* state and are kept for historical record; do not read
> them as current cost. §5's finding is now **fully resolved** — see §5a. Everything else in this
> document (§§2–4, 6–7) was re-checked only where noted; unmarked sections reflect the original
> analysis and should be treated as unverified against current source.

The verified breakdown (see
[Deliverable 10, Current State](phase0-deliverable-10-platform-roadmap.md#current-state)):
**9 of 35** analyzers stream the on-disk index; a further 5 perform a full
`ClrHeap.EnumerateObjects()` sweep that this section's numbers conflate with index streaming but
which a shared index dispatcher cannot address. The qualitative finding below — this is real,
uncoordinated duplication — holds at this corrected multiplier, **for the state that predates the
dispatcher landing (see §1a)**.

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
count), unless the orchestration pipeline does single-pass fan-out (worth confirming explicitly in Deliverable 7 —
nothing in the catalog or `IAnalyzer` shape suggests it does, since each `AnalyzeAsync` is an
independent, self-contained call).

**Estimated cost**: for a 10GB+ dump with tens of millions of objects, the object index file
itself is large (proportional to object count — see
[binary-format.md](../binary-format.md)). A single sequential pass is the expected baseline cost.
Absent a shared single-pass dispatcher, actual I/O cost is closer to **~26x** that baseline —
this is very likely the single largest architectural cost in the entire platform, and the one most
directly at odds with the project's own "10GB+ dumps, reasonable runtime" definition of done.

**This was the #1 finding of Deliverable 4 — see §1a for its current status.**

## 1a. Heap scans — **resolved for the 9 index-scanning analyzers**

Verified directly against source: `HeapIndexScanDispatcher`
(`src/DumpDetective.Analysis/Pipeline/HeapIndexScanDispatcher.cs`) is exactly the "single-pass
index scan dispatcher" this document's §1 called for. `AnalysisPipeline` invokes it once per run
(`AnalysisPipeline.cs:50`), running one `foreach (HeapEntry entry in cache.EnumerateIndexedEntries())`
loop (`HeapIndexCache.cs:76`) and fanning each entry out to every registered
`IHeapIndexScanParticipant`, with per-participant exception isolation so one analyzer's failure
doesn't blind the others. All 9 previously-redundant index-streaming analyzers
(`DbConnectionAnalyzer`, `CrashAnalyzer`, `CollectionAnalyzer`, `AsyncTaskAnalyzer`,
`HangAnalyzer`, `EventLeakAnalyzer`, `MemoryLeakAnalyzer`, `WcfChannelAnalyzer`, `StringAnalyzer`)
now implement `IHeapIndexScanParticipant` instead of independently opening the index — confirmed by
source inspection, and matching Deliverable 10's "All 9 disk-index-streaming analyzers now
migrated" note.

**What's still open**: the **5 analyzers that call `ClrHeap.EnumerateObjects()` directly**
(`TimerLeakAnalyzer`, `HttpObjectAnalyzer`, `FinalizableObjectAnalyzer`, `LohFragmentationAnalyzer`,
`HeapTopologyAnalyzer`) are outside the dispatcher's scope — it fans out disk-index entries, not
live `ClrObject`s, so it cannot address them without a parallel live-object variant. This residual
cost was not re-estimated in this pass; treat it as the next open item, not as covered by the "26x"
number above.

## 2. Root traversals — **done**

`GCRootAnalyzer`, `DominatorAnalyzer` (now also owning the merged `RetentionAnalyzer`'s
retained-subgraph logic), and `StaticRootLeakDetector` now share `BoundedGraphWalk`
(`DumpDetective.Analysis.Traversal`), the single canonical forward-BFS primitive, resolving the ad
hoc graph-walk duplication originally flagged across these analyzers. `ReferenceChainAnalyzer`'s
bidirectional shortest-root-path search (`RootPathFinder`) remains a deliberately separate
implementation — a different problem shape, not consolidated into `BoundedGraphWalk`. See
[Deliverable 5 § Root / Retention Graph Service](phase0-deliverable-5-shared-infrastructure.md#3-root--retention-graph-service---done)
and [Deliverable 10 P0 item 2](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0)
for the implementation. `EventLeakAnalyzer`'s static-field sweep duplication with
`StaticRootLeakDetector` is a separate, still-open item tracked in Deliverable 5.

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

## 5. Statistics — **fully resolved (§5a)**

Per-type object count/byte aggregation (`sum(size)`, `count` grouped by `MethodTable`/type) was
originally recomputed independently by at least `MemoryAnalyzer`, `ModuleAnalyzer`,
`AppDomainAnalyzer`, and `ObjectShapeAnalyzer` — each folded this reduction into its own index scan
rather than consuming a shared result. **`AppDomainAnalyzer` never existed as a separate class in
current source** — verified via `tokensave_search`: AppDomain analysis is `ModuleAnalyzer`'s own
`AnalyzeAppDomains` private method (`ModuleAnalyzer.cs:103`), producing `AppDomainAnalysisResult`
as part of `ModuleDomainResult`. So the original "4 analyzers" count for this cluster was really
**3 analyzer classes** (`MemoryAnalyzer`, `ModuleAnalyzer`, `ObjectShapeAnalyzer`), with
module/app-domain stats sharing one class rather than two.

**Estimated cost** (historical): this is a second-order cost on top of §1 — even if the 9 verified
redundant index scans were collapsed into one shared pass, each of these analyzers would still
independently re-run the same `TypeId → (count, bytes)` reduction over the shared data unless that
reduction itself is promoted to a single computed artifact.

### 5a. Verified current state

The precomputed artifact this section called for now exists. `TypeIndexBuilder` computes per-type
`TypeAggregateIndexEntry` records (count, total size, LOH count/size, sample address) once during
the Phase 1 index build. `StatisticsCache.GetOrBuildTypeStatistics`
(`src/DumpDetective.Analysis/Cache/StatisticsCache.cs`) hydrates its `CachedTypeStatistics` from
that artifact via `TryHydrateTypeStatisticsFromIndex` — an O(unique types) merge, not an O(objects)
heap walk — and only falls back to a full parallel `heap.Segments` / `EnumerateObjects()` walk if
hydration fails or no index is available. Consumers observed calling
`GetOrBuildTypeStatistics` (and therefore sharing the hydrated result rather than each re-scanning):
`MemoryLeakAnalyzer`, `EventLeakAnalyzer`, `ReferenceChainAnalyzer`, `DominatorAnalyzer`,
`LeakCandidateAnalyzer`, `GCGenerationAnalyzer`.

`ModuleAnalyzer.cs` and `ObjectShapeAnalyzer.cs` do not call `StatisticsCache`, but verified
inspection shows they don't independently re-derive the reduction either — both read the same
`TypeAggregateIndexEntry` artifact directly off the heap index rather than through
`StatisticsCache`'s wrapper:

- `ObjectShapeAnalyzer.Analyze` (`ObjectShapeAnalyzer.cs:31`) reads `idx.TypeAggregates` and
  `idx.TypeShapeCache` straight from `HeapIndexBuildResult` — no heap walk at all, and returns an
  empty result if the index/type-shape cache isn't available (no live-heap fallback).
- `ModuleAnalyzer.AnalyzeAppDomains` (`ModuleAnalyzer.cs:103`) does the same: pulls
  `heapIndex.TypeAggregates` when available and only warns (rather than falling back to a live
  scan) when `PreferIndexOnly` is set and the index is missing.

So all three consumers of this cluster (`MemoryAnalyzer` via `StatisticsCache`, `ModuleAnalyzer`,
`ObjectShapeAnalyzer`) now read from the same `TypeIndexBuilder`-produced `TypeAggregateIndexEntry`
data computed once at Phase 1 index build — **§5 is fully resolved**, not just partially.

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

> Ranking below is the **original, pre-fix** ranking, kept for historical record. Items 1 and 2 are
> now resolved for the analyzers/paths described in §1a and §5a — the residual cost is the 5
> `EnumerateObjects()`-based analyzers noted in §1a, not the ~26x figure.

1. ~~**Redundant full object-index scans (~26x multiplier)**~~ — **resolved** for the 9 index-
   scanning analyzers via `HeapIndexScanDispatcher` (§1a). Residual: 5 analyzers still doing full
   live-heap sweeps, unaddressed by the dispatcher.
2. ~~**Redundant per-type statistics reduction**~~ — **fully resolved**: `MemoryAnalyzer` (via
   `StatisticsCache`), `ModuleAnalyzer`, and `ObjectShapeAnalyzer` all read the same
   `TypeIndexBuilder`-produced `TypeAggregateIndexEntry` artifact (§5a). The original "4 analyzers"
   count was also revised to 3 — `AppDomainAnalyzer` was never a separate class from
   `ModuleAnalyzer`.
3. **Redundant graph traversal (4x on static/retention subgraphs)** — moderate cost, moderate fix
   effort (route through `Traversal`). *(Not re-verified in this pass; §2 already marked "done" in
   the original document.)*
4. **Duplicate report-section rendering** — presentation-layer cost, no correctness risk, but
   wasted maintenance effort. *(Not re-verified in this pass.)*
5. **Duplicate helper logic (samplers, wait-pattern, reflection caches)** — low runtime cost, but
   the highest *bug-surface* cost, since a fix to one copy silently doesn't apply to the others.
   *(Not re-verified in this pass.)*
6. **Duplicate type-classification logic** — lowest cost of all, purely a maintenance concern.
   *(Not re-verified in this pass.)*

## Recommended Shared Infrastructure (preview — expanded in Deliverable 5)

- ~~A **single-pass index scan dispatcher**~~ — **shipped** as `HeapIndexScanDispatcher` (§1a).
  Next infrastructure gap: an equivalent shared fan-out for the 5 remaining
  `EnumerateObjects()`-based analyzers.
- ~~A **precomputed per-type statistics artifact**~~ — **shipped and fully adopted**:
  `TypeIndexBuilder`'s `TypeAggregateIndexEntry`, consumed via `StatisticsCache.GetOrBuildTypeStatistics`
  (`MemoryAnalyzer` and other §5a-listed consumers) and read directly by `ModuleAnalyzer` /
  `ObjectShapeAnalyzer` (§5a).
- Mandatory use of the shared `Traversal` primitive for any analyzer doing graph-walk work.
- A shared **typed resource sampler** for the DbConnection/Wcf/Http/Timer cluster.
- A shared **type-name classifier** registry usable by all 8 analyzers currently rolling their own.
- A shared **reflection field-layout cache** usable by `CollectionAnalyzer` and `EventLeakAnalyzer`.
