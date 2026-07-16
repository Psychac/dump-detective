# Disk-Only Cache Migration Plan (2026-07-11)

**Superseded.** This was the original disk-only migration plan. It shipped, but
not via the mechanism designed below — [14-CleanSlateCacheRedesign.md](14-CleanSlateCacheRedesign.md)'s
single-file container replaced the planned `CacheManifest.bin` + per-file
`IndexHeader` unification outright, before that work was built. For current
status see [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md). Kept
here for the motivation/rationale only.

## Motivation

Follow-up to [11-CacheAnalysisFindings.md](11-CacheAnalysisFindings.md), which
established two problems in the (now removed) dual-backend cache
(`MemoryBackedObjectIndexWriter` for dumps <4GB, `DiskBackedObjectIndexWriter`
for dumps ≥4GB, selected by a 4GB threshold):

- **Finding 1**: memory and disk indexing produced non-equivalent output for
  an identical dump — string dedup sampling differed, and large-object/LOH
  data only existed on disk, so `ArrayAnalyzer` fell back to a materially
  weaker heuristic in memory mode. Root cause of the reported HTML report
  divergence.
- **Finding 4**: the disk cache-hit fast path only validated `ObjectIndex.bin`
  and `TypeAggregateIndex.bin`; satellite files could silently fail to write
  yet the run was still treated as a complete, valid cache on the next hit.

Doc 11's suggested fix for Finding 1 was to bring memory mode up to parity
with disk mode. This plan concluded that was backwards: disk mode already had
full feature parity, and none of memory mode's remaining advantages (avoiding
disk I/O for small dumps) were large relative to the ClrMD heap-walk itself.
Removing memory mode entirely was simpler, eliminated Finding 1 at the root
instead of patching each analyzer's dual-path branch, and created a single
writer output whose completeness could be meaningfully validated.

## Outcome

Both goals were achieved, by different means than originally planned:

- **Single disk-backed indexing path**: `MemoryBackedObjectIndexWriter` and
  `HeapIndexPrebuildMode`/`--index-mode` are gone. Every dump goes through one
  writer. See [15](15-ImplementationRoadmap.md) Tier 0/Tier 2.
- **Completeness validation**: rather than the versioned per-file headers +
  `CacheManifest.bin` scheme designed in this doc, the shipped design is a
  single container file (`cache.bin`) with a table of contents and
  per-section checksums — completeness and integrity are properties of one
  file, not a coordination problem across ten. See
  [14](14-CleanSlateCacheRedesign.md#the-core-idea) for the design and
  [15](15-ImplementationRoadmap.md) Tier 2 for status.

The per-file blast-radius inventory, `IndexHeader` migration plan, and
`CacheManifest.bin` layout originally in this doc are no longer accurate to
the codebase and have been removed; see doc 14 for the format that actually
shipped.
