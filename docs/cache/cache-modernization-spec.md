# DumpDetective Cache & Graph Modernization Implementation Specification

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

# Phase 3 -- Dense Object IDs

Extend heap indexing.

Each HeapEntry receives a stable dense ObjectId.

Maintain:

Address -\> ObjectId

ObjectId -\> HeapEntry

All future graph algorithms use ObjectId internally.

Existing address APIs remain unchanged.

------------------------------------------------------------------------

# Phase 4 -- ReferenceGraphCache

Introduce a completely separate cache.

Purpose:

Store only object connectivity.

Do NOT store:

-   ClrObject
-   ClrType
-   field values
-   names
-   strings

Store only:

ObjectId -\> referenced ObjectIds

Construct lazily.

------------------------------------------------------------------------

# Phase 5 -- CSR Graph

Represent graph using CSR.

Do NOT use:

Dictionary\<ObjectId,List`<ObjectId>`{=html}\>

Instead:

Offsets\[\] Edges\[\]

Edges store ObjectIds.

Advantages required:

-   contiguous memory
-   minimal allocations
-   sequential traversal
-   scalable to tens of millions of objects

------------------------------------------------------------------------

# Phase 6 -- Reverse Graph

Do NOT build by default.

Build only when first requested.

Cache independently.

------------------------------------------------------------------------

# Phase 7 -- Root Modernization

Represent roots internally as:

-   ObjectId
-   Root kind
-   Optional thread id/address
-   Lazy description

Convert to addresses only when exposing existing APIs.

------------------------------------------------------------------------

# Phase 8 -- Graph Consumers

Update only analyzers that repeatedly enumerate references.

Examples:

-   Reference chain
-   Event leak
-   Retained object traversal
-   Delegate analysis
-   Dominator groundwork

Analyzers that only scan heap should continue using the heap index.

------------------------------------------------------------------------

# Phase 9 -- Disk Graph (Optional)

Design a versioned graph file beside the heap index.

Possible layout:

heap.idx root.idx graph.idx

Loading an existing graph should avoid rebuilding connectivity.

Keep graph format independent from heap index version.

------------------------------------------------------------------------

# Phase 10 -- Thread Safety

Replace unsafe lazy initialization.

Use:

-   Lazy`<T>`{=html}
-   immutable snapshots
-   double-check locking
-   ConcurrentDictionary only where justified

Avoid global locks during long-running scans.

------------------------------------------------------------------------

# Phase 11 -- Validation

After every phase measure:

-   build time
-   peak managed memory
-   heap accesses
-   graph construction time
-   analyzer runtime

Verify:

-   identical functional output
-   no regression
-   no additional ClrMD object caching

------------------------------------------------------------------------

# Important Architectural Decisions

These are mandatory.

## 1. Graph is not a Root Graph

Build a general object reference graph.

Roots are separate metadata.

## 2. Graph stores ObjectIds

Never store ClrObject.

Never store ClrType.

Prefer ObjectIds over addresses.

Addresses are reporting concerns.

## 3. Graph is lazy

Never build automatically.

## 4. Forward graph first

Reverse graph is optional and lazy.

## 5. Existing analyzers must continue working

Introduce adapters if necessary.

## 6. Memory remains the primary optimization target

Do not trade significant memory increases for modest CPU gains.

## Deliverables

At the end of each phase provide:

-   design summary
-   changed classes
-   compatibility notes
-   performance implications
-   remaining work

Do not begin the next phase until the current phase is complete and
compiling.
