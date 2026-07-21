# DumpDetective Phase 0 --- Cross-Analyzer Architecture Review Protocol

> **Purpose**
>
> Phase 0 is performed **once** before reviewing individual analyzers.
>
> Its goal is to understand DumpDetective as a complete diagnostics
> platform rather than as a collection of independent analyzers.
>
> Do not assume the current architecture is optimal.

------------------------------------------------------------------------

# Reviewer Mindset

Review as:

-   Principal .NET Runtime Engineer
-   CLR/GC Expert
-   Production Memory Diagnostics Architect
-   Product Architect
-   Performance Engineer

Challenge every architectural decision.

------------------------------------------------------------------------

# Primary Objectives

Determine:

-   Whether every analyzer has a clear purpose.
-   Whether responsibilities overlap.
-   Whether important capabilities are missing.
-   Whether common infrastructure should be extracted.
-   Whether analyzers should be merged, split or removed.
-   Whether the overall architecture scales to dozens of analyzers.

------------------------------------------------------------------------

# Inputs

Review the entire solution, including:

-   All analyzers
-   Domain models
-   Shared infrastructure
-   Indexes
-   Heap caches
-   Root graph
-   Formatter pipeline
-   Report generation
-   Analyzer interfaces
-   Options
-   Tests
-   Dependency graph

Do not review implementations in depth yet. Focus on architecture.

------------------------------------------------------------------------

# Deliverable 1 --- Analyzer Catalog

Produce a table:

  -----------------------------------------------------------------------------------
  Analyzer   Primary             Inputs   Outputs   Dependencies   Heap      Status
             Responsibility                                        Scans     
  ---------- ------------------- -------- --------- -------------- --------- --------

  -----------------------------------------------------------------------------------

For every analyzer determine:

-   Single responsibility
-   Major diagnostics
-   Major statistics
-   Evidence produced
-   Whether its purpose is obvious

Flag:

-   Scope creep
-   Mixed responsibilities
-   Unclear ownership

------------------------------------------------------------------------

# Deliverable 2 --- Capability Matrix

Ignore current analyzers.

List every capability expected from a production-grade .NET dump
analysis platform.

Suggested categories:

## Memory

-   Heap summary
-   Type statistics
-   Object statistics
-   Object ownership
-   Duplicate objects
-   Strings
-   LOH
-   POH
-   SOH
-   Fragmentation
-   Free objects
-   GC generations

## Retention

-   Root analysis
-   Root categorization
-   Reference chains
-   Dominators
-   Retention graphs
-   Largest retainers
-   Object ownership

## GC

-   Handles
-   Finalizer queue
-   Pinned objects
-   Weak references
-   Resurrection
-   Finalizable objects

## Threads

-   Managed threads
-   Native threads
-   Deadlocks
-   Blocking
-   ThreadPool
-   Async state machines

## Exceptions

-   Active exceptions
-   Historical crash evidence
-   Exception pressure
-   Aggregate exceptions

## Collections

-   Dictionaries
-   Lists
-   Queues
-   Concurrent collections
-   Immutable collections

## Framework-specific

-   ASP.NET
-   WCF
-   EF Core
-   HttpClient
-   Timers
-   Tasks
-   Channels
-   Events
-   Dependency Injection
-   Reflection
-   Assembly loading

## Platform Health

-   Memory pressure
-   Allocation hotspots
-   Cache health
-   Leak indicators
-   Runtime configuration

For each capability record:

-   Covered?
-   Which analyzer owns it?
-   Quality (Excellent / Good / Partial / Missing)
-   Overlap?
-   Future candidate?

------------------------------------------------------------------------

# Deliverable 3 --- Responsibility Matrix

For every analyzer answer:

-   What problem does it solve?
-   What problem should it never solve?
-   What diagnostics belong elsewhere?
-   What statistics belong elsewhere?

Detect:

-   Responsibility overlap
-   Responsibility gaps
-   Hidden coupling

------------------------------------------------------------------------

# Deliverable 4 --- Duplicate Work Analysis

Identify duplicated:

-   Heap scans
-   Root traversals
-   Type lookups
-   String enumeration
-   Statistics
-   Report sections
-   Helper logic

Estimate cost.

Recommend shared infrastructure.

------------------------------------------------------------------------

# Deliverable 5 --- Shared Infrastructure Opportunities

Identify reusable services such as:

-   Heap indexes
-   Root graph
-   Type metadata
-   Object metadata
-   Statistics engine
-   Evidence builder
-   Sampling framework
-   Ranking engine
-   Confidence scoring
-   Reporting helpers

For each:

-   Current duplication
-   Estimated impact
-   Difficulty
-   Priority

------------------------------------------------------------------------

# Deliverable 6 --- Analyzer Boundary Review

For every analyzer determine whether it should be:

-   Kept
-   Merged
-   Split
-   Replaced
-   Removed

Justify every recommendation.

------------------------------------------------------------------------

# Deliverable 7 --- Dependency Graph Review

Map dependencies.

Identify:

-   Cycles
-   Tight coupling
-   Infrastructure leakage
-   Cross-layer violations
-   Feature entanglement

Recommend an ideal dependency direction.

------------------------------------------------------------------------

# Deliverable 8 --- Performance Architecture Review

Review globally:

-   Number of full heap scans
-   Repeated index construction
-   Repeated root enumeration
-   Duplicate caching
-   Duplicate allocations

Recommend opportunities to consolidate expensive work.

------------------------------------------------------------------------

# Deliverable 9 --- Industry Benchmark

Compare overall platform architecture with:

-   WinDbg + SOS
-   PerfView
-   Visual Studio Memory Usage
-   JetBrains dotMemory

Evaluate:

-   Missing capabilities
-   Better investigation workflows
-   Better evidence
-   Better UX
-   Better extensibility

Do not seek feature parity blindly.

------------------------------------------------------------------------

# Deliverable 10 --- Platform Roadmap

Produce:

## Current Architecture Assessment

Strengths

Weaknesses

Biggest risks

------------------------------------------------------------------------

## Immediate Priorities (P0)

Critical architectural changes.

------------------------------------------------------------------------

## Near-term (P1)

High-impact improvements.

------------------------------------------------------------------------

## Medium-term (P2)

Infrastructure investments.

------------------------------------------------------------------------

## Long-term (P3)

Visionary capabilities.

------------------------------------------------------------------------

# Success Criteria

At the end of Phase 0, the reviewer should be able to answer:

1.  Does every analyzer have a clearly defined owner and responsibility?
2.  Are any analyzers redundant?
3.  Which analyzers should merge or split?
4.  Which platform capabilities are missing?
5.  Which expensive operations should become shared infrastructure?
6.  What architectural changes would most improve correctness,
    scalability, and maintainability?
7.  If DumpDetective were redesigned today, what would its analyzer
    architecture look like?
