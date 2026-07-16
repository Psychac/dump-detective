> **Historical design record — not built, not on the current roadmap.**
> Tier 2 ([14-CleanSlateCacheRedesign.md](14-CleanSlateCacheRedesign.md))
> replaced this graph-based direction with the single-file columnar
> container (`cache.bin` with a TOC — see
> [docs/binary-format.md](../binary-format.md)), which already covers
> disk-backed persistence of the object index. See
> [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md) for what's
> actually being built.

# Implementation Specification – Disk-backed Graph

## Background

Large dumps benefit from persisting expensive graph construction.

## Goals

- Persist graph independently from heap index.
- Support versioning.
- Avoid rebuilding when compatible.

## Non Goals

- Automatic persistence.
- Compression.
- Cross-version compatibility guarantees.

## Suggested Layout

dump.heap.idx
dump.root.idx
dump.graph.idx

Graph versioning must be independent of heap index versioning.

## Data

Persist:

- Offsets[]
- Edges[]
- Graph metadata
- Object count
- Edge count
- Version

Do not persist ClrMD objects or runtime metadata.

## Loading

1. Validate version.
2. Validate object count.
3. Load arrays.
4. Publish immutable graph.

Otherwise rebuild.

## Edge Cases

- Corrupt file.
- Partial writes.
- Version mismatch.
- Missing graph.
