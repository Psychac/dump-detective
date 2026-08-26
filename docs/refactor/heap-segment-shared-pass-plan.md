# Shared Segment Pass — `HeapTopologyAnalyzer` / `SegmentReservationAnalyzer`

> Scope-out for P2 item #12 in
> [heap-topology-analyzer-audit.md](../analysis/phase1/heap-topology-analyzer-audit.md#p2--medium).
>
> **Status: implemented (2026-08-27).** The recommended approach below (shared cached
> `SegmentSummary`, not a merged analyzer) was built as scoped: `SegmentSummary` +
> `SegmentSummaryCache` in `src/DumpDetective.Analysis/Cache/`, wired into `HeapAnalysisCache` as
> `GetOrBuildSegmentSummaries(ClrHeap)`, and consumed by both `HeapTopologyAnalyzer` and
> `SegmentReservationAnalyzer` (each falls back to `SegmentSummaryCache.Build(heap)` directly when
> `IHeapAnalysisCache` isn't the concrete `HeapAnalysisCache`, e.g. a test double). Behavior-preserving:
> `SegmentReservationAnalyzer`'s pre-existing `logicalHeap ?? 0` default (vs. `HeapTopologyAnalyzer`'s
> `?? -1`) was kept by clamping at the call site rather than changing `SegmentSummary`'s shared
> `LogicalHeapIndex` field. All 361 existing `Unit.Analysis` tests pass unchanged.

## Problem

Both analyzers independently loop `ClrHeap.Segments` and, per segment, call the same
`SegmentKindMapper` helpers (`Map`, `GetCommittedBytes`, `GetReservedBytes`, `IsEphemeral`,
`MapRegionKind`) to derive kind / committed / reserved / logical-heap-index / region-kind, then
accumulate their own per-kind and per-logical-heap dictionaries from those same values.

- `HeapTopologyAnalyzer` additionally walks objects (`EnumerateObjects`) for LOH/POH/Frozen
  segments — that per-object work is analyzer-specific and **not** in scope here.
- `SegmentReservationAnalyzer` is segment-metadata-only (committed/reserved/ephemeral/region fill%,
  no object walk).

The duplication is in the **per-segment classification step**, not the expensive per-object walk.
Segment counts are small (tens to low thousands even for regions-based GC), so merging the loops
is a correctness/maintainability win (single source of truth for classification), not primarily a
performance one. That reframes the audit's "High difficulty" estimate — a full analyzer merge is
high-risk for a low perf payoff; a shared, cached per-segment summary is low-risk and captures
nearly all the value.

## Recommended approach: shared cached summary (not a merged analyzer)

Follow the existing `HeapAnalysisCache` sub-cache pattern (`ForwardIndexCache`, `ReverseIndexCache`,
etc. in `src/DumpDetective.Analysis/Cache/`) rather than merging the two analyzers into one:

1. Add an internal `SegmentSummary` readonly struct/record (address, start/end/length, kind,
   committed, reserved, logical heap index, region kind, is-ephemeral, gen0/1/2 range lengths for
   SOH). This is the union of fields both analyzers already compute per segment.
2. Add a `SegmentSummaryCache` sub-cache in `HeapAnalysisCache`, built lazily on first access
   (mirrors `TryGetHeapIndex` / `TryGetForwardIndexProvider`): a single pass over `heap.Segments`
   populates an `IReadOnlyList<SegmentSummary>`, memoized for the lifetime of the cache instance.
   Guard the lazy build with a lock — analyzer `IsThreadSafe` defaults to `false` so this is likely
   unnecessary in practice today, but the existing sub-caches already lock defensively and this
   should match that convention.
3. `IHeapAnalysisCache` gets a new `TryGetSegmentSummaries(out IReadOnlyList<SegmentSummary>?)`
   accessor, following the exact shape of `TryGetHeapIndex`.
4. `HeapTopologyAnalyzer` and `SegmentReservationAnalyzer` both consume
   `cache.TryGetSegmentSummaries(...)` for the classification fields instead of recomputing them
   inline. Each analyzer keeps its own accumulation loop (dictionaries, fill%, region buckets,
   fragmentation, per-object walk) — only the per-segment classification is shared.
5. Fall back to the current inline computation when the cache doesn't provide summaries (e.g. a
   test constructs a bare `IHeapAnalysisCache` without the real implementation) — same fallback
   shape already used for `TryGetHeapIndex` failures (`sohObjects = -1` sentinel path).

## Why not a full merge

A single merged analyzer/domain-result was considered and rejected for this scope:

- The two domain results (`HeapTopologyDomainResult`, `SegmentReservationDomainResult`) serve
  different report sections, trend comparers, and finding generators with independently evolving
  shapes (see recent per-analyzer audits). Merging them is a wide blast radius for a
  classification-only duplication problem.
- `IAnalyzer` has no concept of a shared pre-pass across analyzers today — introducing one would
  require ordering/dependency guarantees in the pipeline that don't currently exist. The cache-based
  approach avoids this: whichever analyzer runs first pays the (cheap) build cost, the other gets a
  cache hit, and there is no ordering dependency.
- Segment counts are small enough that the loop itself is not a measured bottleneck (per
  Audit Area 5 of the topology audit and Area 3 of the reservation analyzer's own review); the
  actual expensive work (LOH/POH/Frozen object walk) stays exactly where it is.

## Steps (implementation, once scoped/approved)

1. Add `SegmentSummary` type + `SegmentSummaryCache` (mirrors an existing simple sub-cache, e.g.
   `ForwardIndexCache`).
2. Wire `IHeapAnalysisCache.TryGetSegmentSummaries` and the `HeapAnalysisCache` implementation.
3. Refactor `HeapTopologyAnalyzer`'s segment loop to consume summaries for kind/committed/reserved/
   logical-heap-index/gen-ranges, keeping its own fragmentation/type-accumulation logic.
4. Refactor `SegmentReservationAnalyzer` similarly for kind/committed/reserved/ephemeral/region-kind.
5. Delete the now-redundant inline calls to `SegmentKindMapper` in both analyzers (keep
   `SegmentKindMapper` itself — it becomes the single call site inside `SegmentSummaryCache`).
6. Add/extend unit tests: a `SegmentSummaryCache` test asserting one segment enumeration regardless
   of how many consumers call `TryGetSegmentSummaries`, plus existing analyzer tests should pass
   unchanged (behavior-preserving refactor).
7. Benchmark before/after on a large synthetic segment count (regions-based GC can have thousands)
   to confirm no regression — expected to be a wash or small win, not a target metric.

## Non-goals

- Do not attempt to unify `HeapTopologyDomainResult` and `SegmentReservationDomainResult`.
- Do not change per-object walk behavior (LOH/POH/Frozen type accumulation) in `HeapTopologyAnalyzer`.
- Do not introduce cross-analyzer execution ordering/dependencies.
