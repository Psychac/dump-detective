> **Historical design record — not built, not on the current roadmap.**
> Tier 2 ([14-CleanSlateCacheRedesign.md](14-CleanSlateCacheRedesign.md))
> replaced this graph-based direction with the single-file columnar
> container. See [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md)
> for what's actually being built.

# Implementation Specification – CSR Graph Storage

## Background

List-based adjacency structures create many allocations and poor locality.

## Goals

- Contiguous memory.
- Low allocation count.
- Fast sequential traversal.

## Non Goals

- Compression.
- Persistence.

## Layout

Offsets[]
Edges[]

Outgoing edges for node N:

Edges[Offsets[N]..Offsets[N+1])

## Algorithm

1. Count outgoing edges.
2. Prefix-sum counts into Offsets.
3. Allocate Edges once.
4. Populate Edges.
5. Publish immutable arrays.

## Complexity

Time: O(V+E)

Memory:
Offsets = 4*(V+1)
Edges = 4*E

## Why CSR

- Better cache locality.
- No List<T> allocations.
- Predictable memory.
- Future disk serialization friendly.

## Edge Cases

Nodes with zero edges.
Very high fan-out.
Duplicate edges.
