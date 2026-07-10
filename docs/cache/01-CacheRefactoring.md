
# Implementation Specification – Cache Refactoring

## Background

HeapAnalysisCache has become a coordinator, cache container and utility class simultaneously. It should evolve into a façade while preserving its public API.

## Goals

- Separate responsibilities.
- Reduce coupling.
- Keep analyzers unchanged.
- Avoid behavioral changes.

## Non Goals

- No graph implementation.
- No ObjectIds.
- No serialization changes.

## Existing Classes

- HeapAnalysisCache
- HeapIndexBuildResult
- MemoryBackedObjectIndexWriter
- DiskBackedObjectIndexWriter

## Target Design

HeapAnalysisCache
 ├── HeapIndexCache
 ├── StatisticsCache
 ├── RootCache
 ├── TypeMetadataCache
 ├── ThreadCache
 └── MethodTableCache

HeapAnalysisCache becomes the composition root.

## Responsibilities

HeapIndexCache
- Own HeapIndexBuildResult.
- Build/load indexes.
- Expose readers.

StatisticsCache
- Own type aggregates.
- Hydrate from index when available.

RootCache
- Own valid roots, descriptions and static root set.

ThreadCache
- Own thread-root counts.

TypeMetadataCache
- Placeholder until Phase 2.

MethodTableCache
- Temporary compatibility layer.

## Public API

Do not change signatures exposed by HeapAnalysisCache.

Forward calls internally to specialized caches.

## Internal API

Each cache exposes:
- EnsureBuilt()
- Clear() if applicable
- Metrics()

Avoid cross-cache dependencies.

## Migration Strategy

Move one responsibility at a time.

After each move, HeapAnalysisCache delegates to the new component.

## Edge Cases

- Existing disk index.
- Existing memory index.
- Concurrent analyzer startup.
- Cache initialization failures.

## Complexity

No meaningful CPU change.
No meaningful memory increase.
