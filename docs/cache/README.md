
# DumpDetective Cache Modernization – Architecture Overview

> **Looking for what to actually work on?** See
> [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md) — the single
> status-tracked task list. This file and the numbered docs below are the
> design/analysis history behind it, not a to-do list.

## Purpose

This modernization improves cache reuse, reduces repeated ClrMD work, and lays the foundation for graph-based analysis while preserving the existing HeapIndex architecture.

This is an evolution of the current implementation—not a rewrite.

## Current Architecture

Today HeapAnalysisCache owns multiple responsibilities:

- Heap index lifecycle
- Type statistics
- Root cache
- Method-table cache
- Sample instances
- Thread caches
- Miscellaneous lookup helpers

The heap index is already optimized through memory-backed and disk-backed implementations.

Analyzers primarily operate on HeapEntry but graph-oriented analyzers still repeatedly query ClrMD.

## Design Goals

- Preserve existing analyzer behavior.
- ~~Preserve MemoryBackedObjectIndexWriter and DiskBackedObjectIndexWriter.~~
  Superseded: [Tier 2](15-ImplementationRoadmap.md#tier-2--single-file-container-migration-doc-14)
  deletes both in favor of a single always-on writer.
- Keep HeapIndex as the primary source of object metadata.
- Eliminate repeated reference enumeration.
- Never cache ClrObject or ClrType.
- Prefer immutable caches.
- Continue scaling to 25GB+ dumps.

## Non Goals

- Replace HeapIndex.
- Rewrite analyzers.
- Compute dominators or retained size.
- Introduce eager graph building.
- Change report formats.

## Target Architecture

HeapAnalysisCache (Facade) — **built**, matches code
 ├── HeapIndex
 ├── StatisticsCache
 ├── RootCache
 ├── TypeMetadataCache
 ├── ThreadCache
 ├── ReferenceGraphCache (lazy) — **not built, not on current roadmap**
 └── Future DiskGraphCache — **not built, not on current roadmap**

Heap scanners continue using HeapIndex.
Graph analyzers would consume ReferenceGraphCache, if it existed — see
[cache-modernization-spec.md](cache-modernization-spec.md) for why this
direction was superseded.

## Guiding Rules

- Object metadata belongs in HeapIndex.
- Type metadata belongs in TypeMetadataCache.
- Connectivity belongs in ReferenceGraphCache.
- Graph caches store ObjectIds only.
- Expensive caches are lazy.
- Public APIs remain compatible unless explicitly stated.
