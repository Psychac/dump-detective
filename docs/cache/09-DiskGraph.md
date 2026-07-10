
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
