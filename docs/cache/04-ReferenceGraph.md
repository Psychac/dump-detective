
# Implementation Specification – ReferenceGraphCache

## Background

Many advanced analyzers repeatedly call ClrObject.EnumerateReferences(). While acceptable individually, repeated traversal across analyzers results in duplicated work and additional page faults on large dumps.

## Goals

- Introduce a reusable object reference graph.
- Build only when first requested.
- Reuse the existing HeapIndex/ObjectId infrastructure.
- Eliminate repeated reference enumeration for graph-heavy analyzers.
- Keep the graph independent of analyzer logic.

## Non Goals

- Reverse graph.
- Dominator tree.
- Retained size.
- Serialization.
- Replacing HeapIndex.

## Existing Architecture

HeapIndex owns metadata only.

Graph relationships are discovered repeatedly by analyzers.

## Target Architecture

HeapAnalysisCache
 ├── HeapIndex
 ├── TypeMetadata
 └── ReferenceGraphCache

ReferenceGraphCache exposes traversal APIs only.

## Public API

EnsureBuilt()
TryGetOutgoing(ObjectId)
EnumerateOutgoing(ObjectId)
NodeCount
EdgeCount

## Data Structures

ReferenceGraph
- uint[] Offsets
- uint[] Edges
- int NodeCount
- int EdgeCount

## Algorithm

1. Ensure HeapIndex exists.
2. Iterate indexed objects.
3. Resolve ObjectId.
4. Enumerate references once using ClrMD.
5. Resolve referenced addresses to ObjectIds.
6. Ignore invalid targets.
7. Populate temporary edge buffers.
8. Publish immutable graph.

## ClrMD Notes

Never retain ClrObject.
Discard each object immediately after reference enumeration.
Treat missing objects as skipped edges.

## Edge Cases

- Self references.
- Cycles.
- Invalid addresses.
- Duplicate references.
- Free objects.
- Corrupted objects.
