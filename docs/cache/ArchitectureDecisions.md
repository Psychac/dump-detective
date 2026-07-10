# ArchitectureDecisions.md

# DumpDetective Cache Modernization -- Architectural Decisions

This document records architectural decisions that are considered
**intentional design choices** rather than implementation details. AI
agents and contributors should treat these as constraints unless there
is a compelling, measured reason to change them.

------------------------------------------------------------------------

# ADR-001 --- HeapIndex remains the source of truth

**Decision**

The HeapIndex continues to be the primary representation of the managed
heap.

**Reasoning**

-   Existing analyzers already depend on it.
-   It scales well for both memory-backed and disk-backed modes.
-   It separates metadata lookup from expensive ClrMD operations.

**Implication**

New caches augment the HeapIndex---they do not replace it.

------------------------------------------------------------------------

# ADR-002 --- Never cache ClrObject or ClrType

**Decision**

Caches may extract immutable information from ClrMD, but must never
retain ClrObject or ClrType instances.

**Reasoning**

-   Avoid hidden memory growth.
-   Prevent long-lived references into ClrMD.
-   Reduce GC pressure.
-   Keep caches serializable and independent.

Instead, cache immutable data or object addresses/ObjectIds.

------------------------------------------------------------------------

# ADR-003 --- Object metadata and graph connectivity are separate

HeapIndex owns:

-   Address
-   MethodTable
-   Size
-   TypeId
-   ObjectId

ReferenceGraph owns:

-   ObjectId relationships only

The graph must never become a second heap representation.

------------------------------------------------------------------------

# ADR-004 --- ObjectId is the canonical internal identifier

Addresses remain the public identifier.

Internally, graph algorithms use ObjectId because:

-   fixed-width integer
-   compact
-   cache-friendly
-   faster comparisons
-   future graph algorithms benefit

Address lookups remain available for compatibility.

------------------------------------------------------------------------

# ADR-005 --- Graph caches are lazy

Building a graph is expensive.

Therefore:

-   never build automatically
-   build only on first request
-   reuse thereafter

Analyzers that never need the graph should never pay for it.

------------------------------------------------------------------------

# ADR-006 --- Forward graph precedes reverse graph

The forward graph supports most traversal scenarios.

The reverse graph exists only for analyses requiring incoming references
(retained size, dominators, leak roots, etc.).

Do not build reverse edges eagerly.

------------------------------------------------------------------------

# ADR-007 --- Heap scanners remain heap scanners

Many analyzers only enumerate objects once.

Examples:

-   String Analyzer
-   WCF Analyzer
-   Type Statistics

These continue using HeapIndex directly.

Only graph-heavy analyzers migrate.

------------------------------------------------------------------------

# ADR-008 --- Disk graph is optional

Persisted graph data is an optimization.

The application must function correctly without it.

Version the graph independently from the heap index.

------------------------------------------------------------------------

# ADR-009 --- Favor immutable published caches

During construction, mutable builders are acceptable.

After publication:

-   immutable arrays
-   immutable records
-   read-only collections

Avoid mutable shared state.

------------------------------------------------------------------------

# ADR-010 --- Optimize memory before CPU

When choosing between implementations:

1.  Correctness
2.  Memory usage
3.  Simplicity
4.  CPU performance

Small CPU regressions are acceptable if they substantially reduce peak
memory or improve scalability.

------------------------------------------------------------------------

# ADR-011 --- Every cache has one responsibility

Avoid "god" cache classes.

Each cache should answer one question well:

-   HeapIndex -\> object metadata
-   TypeMetadata -\> immutable type facts
-   RootCache -\> GC roots
-   StatisticsCache -\> aggregates
-   ReferenceGraph -\> connectivity

This keeps analyzers composable and testable.

------------------------------------------------------------------------

# ADR-012 --- Future features should reuse existing infrastructure

The modernization should enable future analyzers without redesign.

Expected future consumers include:

-   Dominator Tree
-   Retained Size
-   SCC Detection
-   Cycle Detection
-   Root Path Caching
-   Leak Classification

New features should build on HeapIndex + ReferenceGraph rather than
introducing parallel representations.
