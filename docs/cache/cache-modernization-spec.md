# DumpDetective Cache & Graph Modernization Implementation Specification

> **Looking for what to actually work on?** See
> [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md) — the single
> status-tracked task list. **Phases 0-2 below are done** (facade split,
> immutable `TypeMetadata`) and match current code. **Phases 3-9 (dense
> `ObjectId`, `ReferenceGraphCache`, CSR graph, reverse graph, disk graph)
> were never built and are not on the current roadmap** — Tier 2 replaced
> this graph-based direction with the single-file columnar container
> described in [14](14-CleanSlateCacheRedesign.md). Treat everything past
> Phase 2 as historical design record, not a live plan.

> **Audience:** AI coding agent
>
> **Objective:** Implement the following architecture exactly. Do
> **not** redesign it, simplify it, or substitute different data
> structures unless explicitly instructed. Preserve existing behavior
> unless this specification explicitly requires a change.

## Guiding principles

1.  Correctness over performance.
2.  Low peak memory over CPU.
3.  Streaming over materialization.
4.  Never cache `ClrObject` or `ClrType`.
5.  Keep compatibility with existing analyzers.
6.  Every phase must compile and pass existing tests before continuing.
7.  New infrastructure must be lazily initialized unless explicitly
    stated.
8.  Existing memory-index and disk-index behavior must remain supported.

------------------------------------------------------------------------

# Phase 0 -- Audit

Before modifying code:

-   Identify every consumer of HeapAnalysisCache.
-   Identify every place that calls:
    -   ClrHeap.GetObject
    -   ClrHeap.GetTypeByMethodTable
    -   EnumerateReferences
    -   EnumerateRoots
-   Produce an internal dependency map.
-   Do not change behavior.

------------------------------------------------------------------------

# Phase 1 -- Refactor HeapAnalysisCache

Do not change public APIs.

Convert HeapAnalysisCache into a façade coordinating focused cache
components.

Suggested internal components:

-   HeapIndexCache
-   RootCache
-   TypeMetadataCache
-   StatisticsCache
-   ThreadCache
-   MethodTableCache
-   ReferenceGraphCache (placeholder)

Each component owns: - lazy initialization - synchronization - disposal
(if needed) - metrics

Avoid one giant mutable class.

------------------------------------------------------------------------

# Phase 2 -- Immutable Type Metadata

Replace the simple MethodTable-\>ContainsPointers cache with immutable
metadata records.

Cache:

-   MethodTable
-   ContainsPointers
-   InstanceSize
-   IsArray
-   Component type
-   ArrayContainsPointers
-   ReferenceFieldCount
-   ReferenceFieldOffsets
-   IsString
-   IsDelegate
-   IsException
-   IsFreeObject

Never cache ClrType.

Metadata should be computed once per MethodTable.

------------------------------------------------------------------------

# Phases 3-11 (dropped)

The original spec continued past Phase 2 with a general object reference
graph: dense `ObjectId`s (Phase 3), a lazy `ReferenceGraphCache` storing only
`ObjectId -> ObjectId` connectivity (Phase 4), CSR (offsets/edges) storage
instead of adjacency dictionaries (Phase 5), a lazy reverse graph (Phase 6),
`ObjectId`-based root representation (Phase 7), migrating reference-heavy
analyzers onto the graph (Phase 8), an optional on-disk graph file (Phase 9),
plus general thread-safety and validation passes (Phases 10-11).

None of this was built. Tier 2 ([14](14-CleanSlateCacheRedesign.md))
replaced the whole graph-based direction with the single-file columnar
container instead. See [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md)
for what's actually being built.
