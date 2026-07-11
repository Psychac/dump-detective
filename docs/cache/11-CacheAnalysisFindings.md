# Cache Analysis Findings (2026-07-11)

Analysis-only pass over the current heap-index caching subsystem
(`src/DumpDetective.Analysis/Cache/`, `src/DumpDetective.Analysis/Indexing/`).
No code changes made. Earlier docs in this folder (`01-*` through `10-*`,
`ArchitectureDecisions.md`) are outdated and were intentionally **not** used
as input — everything below is derived directly from current source.

## Architecture as-built

`HeapAnalysisCache` ([HeapAnalysisCache.cs](../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs))
is a facade over six sub-caches — `HeapIndexCache`, `StatisticsCache`, `RootCache`,
`ThreadCache`, `MethodTableCache`, `TypeMetadataCache` — all backed by one
`HeapIndexBuildResult` built once per analysis run by either
`MemoryBackedObjectIndexWriter` or `DiskBackedObjectIndexWriter`, selected by
a 4 GB dump-size threshold. The cache is session-scoped with no eviction,
which is appropriate given the one-shot-per-dump usage pattern.

## Finding 1 — Memory vs. disk indexing produce non-equivalent output

This is the direct cause of the differing HTML report sizes for the same
dump under memory-tier vs. disk-tier indexing.

- **String dedup is exact on disk, sampled in memory.**
  [MemoryBackedObjectIndexWriter.cs:47-51](../../src/DumpDetective.Analysis/Indexing/MemoryBackedObjectIndexWriter.cs#L47-L51)
  adaptively skips `AsString()` calls (down to 1-in-10 or 1-in-50) once the
  duplicate yield rate drops below 5%/1%. `DiskBackedObjectIndexWriter.cs:186`
  samples every string object with no skipping. `StringAnalyzer`'s dedup
  counts/tables will differ between modes on an identical dump.

- **Large-object / LOH-free-block data only exists on disk.** The disk
  writer collects `largeCandidates` and `lohFreeBlockCandidates` and writes
  `LargeObjectIndex.bin` / `LohFreeBlockIndex.bin`
  ([DiskBackedObjectIndexWriter.cs:80-82,176-181](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L80-L181)).
  The memory writer never collects these — there is no
  `InMemoryLargeCandidates` / `InMemoryLohFreeBlockCandidates` field on
  `HeapIndexBuildResult`. Consumers compensate inconsistently:
  - `LohFragmentationAnalyzer` detects the gap and falls back to a full
    segment re-scan in memory mode
    ([LohFragmentationAnalyzer.cs:209-213](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L209-L213)),
    producing correct but redundant output (defeats the purpose of a
    prebuilt index for small dumps).
  - `ArrayAnalyzer` does **not** compensate the same way. It only reads
    `LargeObjectIndex.bin` when `StorageKind == Disk`
    ([ArrayAnalyzer.cs:160-168](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L160-L168));
    in memory mode it falls back to a much weaker "one sample per LOH type
    from TypeAggregates" heuristic instead of a true top-100-by-size list.
    This alone changes the "large arrays" section of the report between
    modes.

**Suggested fix:** add `InMemoryLargeCandidates` /
`InMemoryLohFreeBlockCandidates` to `HeapIndexBuildResult`, collect them in
`MemoryBackedObjectIndexWriter` the same way the disk writer does, and
update `ArrayAnalyzer` / `LohFragmentationAnalyzer` to consume them directly
instead of scan-fallback vs. degraded-heuristic. Separately, make string
dedup sampling mode-symmetric (either apply the same adaptive sampling to
disk, or scale memory-mode sampling deterministically by dump size) so
`StringAnalyzer` output doesn't vary with which writer happened to run.

## Finding 2 — `GetRootDescription` is broken in both modes

`HeapAnalysisCache._rootDescriptions`
([HeapAnalysisCache.cs:21](../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs#L21))
is declared but never assigned. The population logic lives in
`RootCache._rootDescriptions` instead, but
`HeapAnalysisCache.GetRootDescription`
([HeapAnalysisCache.cs:298-305](../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs#L298-L305))
never delegates to `_rootCache` — it always returns `null`.

`CollectionAnalyzer.PopulateRootDescriptions` calls this on the "fast
profile" path for every wasteful-collection item
([CollectionAnalyzer.cs:1046-1054](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs#L1046-L1054)),
so it silently misses every time. Per the surrounding comment, Balanced/Deep
profiles then pay for a full `ReferenceChainAnalyzer` BFS instead — a
correctness gap that also costs performance.

Even after fixing the delegation, root descriptions still won't survive the
**disk** fast-path: `RootCache.GetOrBuildValidRoots`
([RootCache.cs:38-70](../../src/DumpDetective.Analysis/Cache/RootCache.cs#L38-L70))
reads `RootIndex.bin` (target/root/kind only, no description string) and
returns early, never populating `_rootDescriptions` on large dumps. Only the
`EnsureRootCaches` heap-walk fallback fills it in.

**Suggested fix:** delegate `HeapAnalysisCache.GetRootDescription` to
`_rootCache`. Then either persist descriptions into `RootIndex.bin`, or
derive them lazily via `ClrRoot.ToString()` for just the top-N addresses
`CollectionAnalyzer` needs, so disk mode isn't permanently blank either.

This looks like leftover debris from the cache-refactor commits (`cache:
HeapAnalysisCache breakdown and refactor`, etc.) — `HeapAnalysisCache` also
still carries a dead duplicate of `TryHydrateTypeStatisticsFromIndex` /
`ResolveTypeNameFromSample` / `ResolveModuleNameFromSample` / `AddClamped`
now solely owned by `StatisticsCache`. Worth deleting to avoid future edits
landing in the wrong copy.

## Finding 3 — Redundant root enumeration in memory mode

`RootCache.GetOrBuildValidRoots` only fast-paths off disk-backed
`RootIndex.bin`
([RootCache.cs:38-70](../../src/DumpDetective.Analysis/Cache/RootCache.cs#L38-L70)).
It never checks `heapIndex.InMemoryRootCandidates`, even though
`RootIndexReader.ReadRootCandidates` already has a memory-mode branch built
for exactly this purpose
([RootIndexReader.cs:17-24](../../src/DumpDetective.Analysis/Readers/RootIndexReader.cs#L17-L24))
and `MemoryBackedObjectIndexWriter` already collects roots during Phase 1.
Every memory-tier dump (< 4 GB) therefore redundantly re-walks
`heap.EnumerateRoots()` in `EnsureRootCaches`, duplicating work already done
during index build.

**Suggested fix:** branch on `heapIndex.StorageKind == Memory` in
`GetOrBuildValidRoots` and hydrate from `InMemoryRootCandidates` via
`RootIndexReader.ReadRootCandidates`, same as the disk branch does from the
file.

## Finding 4 — Disk fast-path doesn't validate satellite files

`DiskBackedObjectIndexWriter.TryLoadFromCache`
([DiskBackedObjectIndexWriter.cs:633-647](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L633-L647))
only validates presence/stamp of `ObjectIndex.bin` and
`TypeAggregateIndex.bin`. Satellite files (`RootIndex.bin`,
`LargeObjectIndex.bin`, `HandleSnapshot.bin`, etc.) can fail to write on a
prior run — the write is wrapped in try/catch and only logged as a
non-fatal warning ([DiskBackedObjectIndexWriter.cs:478-557](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L478-L557))
— yet `TypeAggregateIndex.bin` is still written last, which is the sole
signal the fast-path uses to decide the build was "complete". A later run
against the same dump will hit the cache fast-path and skip the full scan,
permanently missing whatever satellite file failed to write the first time,
with no retry mechanism.

**Suggested fix:** on cache-hit, check for expected satellite files and
regenerate any that are missing rather than assuming completeness from
`TypeAggregateIndex.bin` alone.

## Summary of suggested changes (unprioritized)

1. Add memory-mode large-object / free-block candidate collection so
   `ArrayAnalyzer` and `LohFragmentationAnalyzer` get parity with disk mode
   without a fallback re-scan. **Directly fixes the reported HTML-size
   divergence.**
2. Make string dedup sampling symmetric between memory and disk writers.
3. Fix `HeapAnalysisCache.GetRootDescription` delegation; persist or
   lazily derive descriptions for disk-backed roots too.
4. Delete the dead duplicate type-statistics hydration code in
   `HeapAnalysisCache`.
5. Hydrate `RootCache` from `InMemoryRootCandidates` in memory mode instead
   of re-walking `heap.EnumerateRoots()`.
6. Validate/repair satellite index files on disk cache-hit instead of
   trusting `TypeAggregateIndex.bin` alone.
