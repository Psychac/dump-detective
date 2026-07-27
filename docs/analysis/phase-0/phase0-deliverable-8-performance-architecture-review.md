# Phase 0 — Deliverable 8: Performance Architecture Review

> Scope: **Deliverable 8 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Reviewed as a Performance Engineer against DumpDetective's own stated bar in
> [CLAUDE.md](../../CLAUDE.md): "works on 10GB+ dumps, bounded memory usage, reasonable runtime,
> no unnecessary allocations." This is an architectural-level review (no profiler run, no
> benchmark numbers) — it globalizes and quantifies the per-item findings from
> [Deliverable 4](phase0-deliverable-4-duplicate-work-analysis.md) and states what should be
> confirmed empirically once these are actionable.
>
> **Re-evaluated against current source** (verified via code-graph query, not re-estimated). Most
> of the consolidation work this review originally recommended has since **landed** — see
> [Deliverable 10, Near-term (P1)](phase0-deliverable-10-platform-roadmap.md#near-term-p1), which
> is the authoritative, continuously-updated status tracker. This document has been rewritten in
> place to reflect what's actually resolved, what's newly discovered as open (from the dispatcher
> migration's own architect-review findings), and what small number of items are still genuinely
> outstanding. Original per-item numbering is preserved for cross-reference; each section states
> its current status up front.

## 1. Number of Full Heap Scans — counts unchanged, framing updated

Direct verification against the actual `IAnalyzer` implementations (see
[Deliverable 10, Current State](phase0-deliverable-10-platform-roadmap.md#current-state)) found:
**9 of 35** analyzers stream the on-disk `HeapEntry` index; a separate **5** perform a full live
`ClrHeap.EnumerateObjects()` sweep with no index path at all (architecturally distinct — not
addressable by an index-scan dispatcher: `TimerLeakAnalyzer`, `HttpObjectAnalyzer`,
`FinalizableObjectAnalyzer`, `LohFragmentationAnalyzer`, `HeapTopologyAnalyzer` — the last two
per-segment, not whole-heap). These counts are unchanged from the original review.

What has changed is the *cost model* for the 9 index-streaming analyzers: they are no longer 9
independent full index reads on their primary path (see §2 — the single-pass dispatcher is now
implemented and fully migrated). The "~9x multiplier" framing below is now the **worst case**
(fallback path, when the dispatcher isn't wired or the shared scan fails), not the typical case.
The 5 `EnumerateObjects()`-based analyzers remain a distinct, unaddressed cost exactly as
originally described — no mechanism exists or is planned for that population.

## 2. Repeated Index Construction — largely resolved

- **The on-disk Phase 1 object index itself** is built once, correctly. Confirmed directly against
  `HeapIndexCache.PrebuildHeapIndex`: `DiskBackedObjectIndexWriter` is only ever constructed inside
  that method, guarded by `if (_heapIndex is not null) return _heapIndex;`, and `TryLoadFromCache`
  short-circuits to the on-disk `cache.bin` when the dump-content hash matches. No rebuild path
  exists.
- **In-memory secondary indexes built by each analyzer while consuming that stream** — this was
  the real duplication, and it is now **resolved on the primary path**. `HeapIndexScanDispatcher`
  (`Pipeline/HeapIndexScanDispatcher.cs`) performs one shared `foreach` over
  `EnumerateIndexedEntries()` and fans each `HeapEntry` out to every registered
  `IHeapIndexScanParticipant`. All 9 of the verified index-streaming analyzers
  (`DbConnectionAnalyzer`, `CrashAnalyzer`, `CollectionAnalyzer`, `HangAnalyzer`,
  `WcfChannelAnalyzer`, `StringAnalyzer`, `DominatorAnalyzer`, `AsyncTaskAnalyzer`,
  `EventLeakAnalyzer`) implement this interface and are wired through `AnalysisPipeline`. This
  closes Consolidation item 1 from the original table below.
  - **Three caveats surfaced by the migration's own architect review, not yet fixed** (see
    [Deliverable 10 P1 item 2](phase0-deliverable-10-platform-roadmap.md#near-term-p1)):
    1. `AnalysisPipeline` only wires the dispatcher when `context.Cache is HeapAnalysisCache`
       (concrete-class check, not `IHeapAnalysisCache`). Any alternate cache implementation
       silently falls back to N independent scans with no error or diagnostic — correctness-safe,
       but an invisible perf cliff.
    2. The core premise — one sequential shared pass beats N independent (previously often
       parallel, e.g. `CrashAnalyzer`'s `Parallel.ForEach(heap.Segments, ...)`) scans — is
       **unverified**. This trades full-core parallelism for a single-threaded shared pass, which
       is only a net win if disk I/O rather than CPU is the bottleneck on large dumps. Not yet
       measured against a representative 10GB+ dump.
    3. Each migrated analyzer now carries up to three parallel implementations of the same logic:
       the dispatcher-participant path, a parallel-segment no-index fallback, and (for
       `AsyncTaskAnalyzer`) a third raw-heap fallback, reconciled only by `*DiscrepancyTests`. This
       is a maintainability/correctness-drift risk that the original single-scan duplication did
       not have.
- **Container/satellite indexes** (`Indexing.Container` for arrays/LOH/tasks, `Indexing.Satellite`
  for weak references) — **confirmed safe, no fix required.** `HeapIndexCache.PrebuildHeapIndex`'s
  single `Build` call writes the container index and all satellite sections (task index, event
  index, large-object index, LOH-free-block index) together in one pass, with `TypeAggregates`
  written last as the completion marker. There is no separate deferred build path per satellite
  section, so the "lazily rebuilt per invocation" risk flagged in the original review does not
  exist.

## 3. Repeated Root Enumeration — resolved

`RootSetCache` (`Cache/RootSetCache.cs`) is now the canonical root-set artifact, replacing the
older `RootCache`. It is memoized per `HeapAnalysisCache` instance and exposes
`GetOrBuildValidRoots(ClrHeap)`. Confirmed by direct call-site verification, all previously
independent root enumerators now go through it: `GCRootAnalyzer`, `StaticRootLeakDetector`,
`EventLeakAnalyzer`, `DominatorAnalyzer` (post `RetentionAnalyzer` merge — see
[Deliverable 10 P0 item 3](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0)),
`ReferenceChainAnalyzer`, and `TimerLeakAnalyzer`. Root enumeration now happens at most once per
analysis run and is shared across all six consumers, closing Consolidation item 2 below.

`BoundedGraphWalk` (`Traversal/BoundedGraphWalk.cs`) similarly replaced the three previously
separate forward-BFS implementations (`HeapTypePathTraversal`, `BoundedRetainedSizeBfs`,
`HeapAnalysisCache.GetRetainedObjects` — all deleted), consolidating the 20-depth bounded traversal
logic used by `GCRootAnalyzer`, `DominatorAnalyzer`, and `StaticRootLeakDetector`. (`RootPathFinder`
/ `ReferenceChainAnalyzer`'s bidirectional search was intentionally left separate — different
problem shape, not a duplicate.)

## 4. Duplicate Caching — mostly resolved, two items still open

Raw `MethodTable → ClrType` resolution is correctly centralized in `HeapAnalysisCache` (confirmed
in Deliverable 4 §3). Of the three duplication vectors originally flagged here, two are now
resolved and one remains open:

- **Type-name classification caches — resolved for 8 of the affected analyzers.**
  `TypeNamePatternMatcher` (`Analyzers/TypeNamePatternMatcher.cs`) is now the shared type-name
  matcher (`HasAnyPrefix`, `ContainsAny`, `HasPrefixAndSuffixOrContains`, `GetShortName`), adopted
  by `DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`, `TimerLeakAnalyzer`,
  `CollectionAnalyzer`, and `AsyncTaskAnalyzer`. `WeakReferenceAnalyzer` and
  `AsyncStateMachineAnalyzer` are intentionally left on their own `StartsWith` checks (different
  matching needs). Separately, `TypeAggregateNameResolver` was added as the single MT→name/module
  resolution point, adopted by `StatisticsCache`, `HttpObjectAnalyzer`, `TimerLeakAnalyzer`, and
  `FinalizableObjectAnalyzer`, closing the drift risk between `TypeAggregates` (MT-keyed) and
  `StatisticsCache`'s `CachedTypeStatistics` (name-keyed).
- **Object metadata classification (generation/segment) — resolved**, and scope was narrower than
  originally framed. `SegmentKindMapper.ResolveGeneration(ClrHeap, ulong)` is now the single
  per-address generation resolver, adopted by `FinalizableObjectAnalyzer` and `EventLeakAnalyzer`.
  Migrating this surfaced a real bug: `CollectionAnalyzer`'s independent copy called ClrMD APIs
  that don't exist on ClrMD 4's public surface (`typeof(ClrObject).GetProperty("Generation")`,
  `typeof(ClrHeap).GetMethod("GetGeneration", ...)`), so it silently fell through to a hardcoded
  `return 2` — every collection was reported as Gen2 regardless of actual generation. Fixed as a
  side effect of the consolidation.
- **`CollectionAnalyzer`'s reflection-based field-layout cache — still open.** Confirmed via
  [Deliverable 6](phase0-deliverable-6-analyzer-boundary-review.md)'s remaining-work summary: this
  extraction has not landed. Note Phase 1 index build now produces a per-MethodTable field-layout
  cache as part of `HeapIndexBuildResult` (see its "Per-MethodTable field layout cache built during
  Phase 1" note) — but it is not yet confirmed that `CollectionAnalyzer` actually consumes that
  cache instead of its own reflection-based one. Treat as open until verified against
  `CollectionAnalyzer`'s source directly.
- **Handle target resolution — still open.** `GCHandleAnalyzer` (owning the merged
  `DependentHandleAnalyzer`'s target resolution, per
  [Deliverable 6](phase0-deliverable-6-analyzer-boundary-review.md#dependenthandleanalyzer--gchandleanalyzer))
  and `WeakReferenceAnalyzer` still independently walk overlapping parts of the handle table.
  Deliverable 6 explicitly still lists this as open ("must de-duplicate raw handle counting against
  `GCHandleAnalyzer`"), and notes `WeakReferenceAnalyzer` has a real reason to stay a separate
  analyzer (it reads a distinct satellite index for target-liveness resolution) — the ask is
  de-duplicating the raw counting, not merging the analyzers.

**Cost note**: caching duplication is a smaller memory-pressure concern than the heap-scan
duplication above (these caches are bounded by type/handle count, not object count), but the two
remaining items still work against CLAUDE.md's explicit caching rule and remain a correctness risk
if the independent caches can drift.

## 5. Duplicate Allocations — mostly resolved

- **Per-analyzer aggregation structures — resolved.** Per-type aggregation (count/size/LOH/gen
  buckets) is now built exactly once during Phase 1 via `TypeIndexBuilder`
  (`Indexing/TypeIndexBuilder.cs`), producing `TypeAggregateIndexEntry` records consumed through
  `StatisticsCache.GetOrBuildTypeStatistics`. Analyzers that need per-type stats (e.g.
  `GCGenerationAnalyzer`) read from this shared structure instead of re-deriving it from a raw
  object walk. This was resolved by the Phase 1 index-build work, not the dispatcher itself.
- **Redundant `ArrayPool` rent/return cycles — reduced to a single pass for the 9 migrated
  analyzers.** `ObjectIndexReader` is a static singleton (`ObjectIndexReader.Instance`), so the
  original framing of "each analyzer's own instance" was imprecise — the *pool* was always shared.
  What has changed is the call pattern: `HeapIndexScanDispatcher` now issues a single
  `EnumerateIndexedEntries()` read per run for its 9 participants, rather than each participant
  calling `ReadEntries` independently. The redundant-CPU-cost concern (re-deserializing the same
  bytes N times) is resolved for the dispatcher's primary path; it still applies in full on the
  fallback path described in §2's caveats.
- **Sample-buffer duplication — resolved.** `TypedResourceSampler.cs` now provides
  `TypedResourceCandidateScanner` (candidate discovery) and `InstanceStateSampler<TSnapshot>`
  (bounded per-type-capped sampling), wired through `ITypedResourceCandidateSource` /
  `ITypedResourceInstanceSampler<TSnapshot>` / `TypedResourceScanDriver` so the call order is
  compiler-enforced, not just conventional. Adopted by all four of `DbConnectionAnalyzer`,
  `WcfChannelAnalyzer`, `HttpObjectAnalyzer`, and `TimerLeakAnalyzer`. An analogous pattern
  (`IThreadStackScanParticipant` / `ThreadStackScanDispatcher`) now covers the equivalent
  thread-domain quartet (`ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`,
  `LockGraphAnalyzer`), which the original review didn't separately call out but is the same
  pattern applied to a second domain.
- **String-interning duplication — not separately re-verified**, but the unification of type-name
  classification under `TypeNamePatternMatcher` (§4) reduces the surface area where independent
  local string keys could have been introduced. No evidence either way from this pass; low
  priority given the other closures above.

## Consolidation Opportunities (ranked by expected impact) — status update

| # | Consolidation | Addresses | Status |
|---|---|---|---|
| 1 | Single-pass index scan dispatcher, with per-type statistics computed once inside the same pass | §1 heap scans, §2 in-memory index construction, §5 aggregation-structure allocations | **Done.** `HeapIndexScanDispatcher` / `IHeapIndexScanParticipant`, all 9 analyzers migrated. Three open caveats — see §2: concrete-type coupling, unverified sequential-vs-parallel perf premise, triplicated fallback logic. |
| 2 | Canonical root-set artifact from `GCRootAnalyzer`, consumed instead of each analyzer re-enumerating | §3 repeated root enumeration | **Done.** `RootSetCache` + `BoundedGraphWalk`, 6 consumers confirmed. |
| 3 | Confirm container/satellite index build-once-per-session behavior | §2 container index construction | **Done — confirmed safe, no code change needed.** Single `Build` call writes container + all satellite sections together. |
| 4 | Shared type-classification cache and shared reflection field-layout cache | §4 duplicate caching | **Half done.** Type-classification (`TypeNamePatternMatcher`) and generation/segment classification (`SegmentKindMapper`) resolved. `CollectionAnalyzer`'s reflection field-layout cache extraction **still open**. |
| 5 | One handle-table walk shared by `GCHandleAnalyzer` and `WeakReferenceAnalyzer` | §3/§4 (handle enumeration and caching) | **Still open.** Confirmed via Deliverable 6 as the last outstanding item in this area. |
| 6 | Shared typed-resource sampler for the DbConnection/Wcf/Http/Timer quartet | §5 sample-buffer duplication | **Done.** `InstanceStateSampler<T>` / `TypedResourceScanDriver`, all 4 analyzers migrated. |

Only **items 5 and the field-layout half of item 4** remain genuinely open from the original
six-item list.

## What This Review Could Not Determine — updated

The original three open empirical questions have been substantially narrowed:

- ~~Whether container/satellite indexes are rebuilt per-analyzer-invocation~~ — **answered**:
  confirmed built once (item 3 above).
- ~~Peak memory contribution from duplicate per-analyzer aggregation structures~~ — **largely
  moot**: the aggregation duplication itself is resolved (§5), so this no longer needs separate
  measurement.
- **New primary open question, surfaced by the dispatcher migration's own architect review**:
  whether one sequential shared index-scan pass actually outperforms the N independent
  (previously often parallel) scans it replaced on a representative 10GB+ dump. This is not a
  restatement of the original "~9x I/O cost" question — it's the opposite risk: the dispatcher
  trades full-core parallelism (e.g. `CrashAnalyzer`'s prior `Parallel.ForEach(heap.Segments, ...)`)
  for a single-threaded shared pass, which is a net win only if disk I/O, not CPU, is the
  bottleneck. Unmeasured.

Recommend a profiling pass (dotnet-trace/dotMemory against a representative large dump) focused
specifically on this sequential-vs-parallel question before any further dispatcher hardening or
broader rollout (e.g. onto the 5 `EnumerateObjects()`-based analyzers from §1, which the dispatcher
does not currently cover).
