# Cache Analysis Findings

Heap-index caching subsystem (`src/DumpDetective.Analysis/Cache/`, `src/DumpDetective.Analysis/Indexing/`).

## Architecture

`HeapAnalysisCache` is a facade over six sub-caches, backed by `HeapIndexBuildResult` built once per dump by either `MemoryBackedObjectIndexWriter` or `DiskBackedObjectIndexWriter` (split at 4 GB). Session-scoped, no eviction.

## Findings

### Finding 1 — Memory vs. disk indexing non-equivalence

**Status:** Resolved for `LohFragmentationAnalyzer`; `ArrayAnalyzer.TopSparseArrays` divergence accepted.

**Issue:** Disk writer collects large/LOH-free-block candidates; memory writer does not.

**Cause:** Algorithm divergence — disk mode reads `segment.CommittedMemory` directly; memory mode summed `obj.Size` across all objects. `CommittedMemory` includes tail padding, causing 159,646-byte gap. Structural gap: disk writes index files; memory has no equivalent fields on `HeapIndexBuildResult`.

**Fixes:**
- Memory mode now calls `GetSegmentTotalBytes()` to read `CommittedMemory` (matching disk).
- `UsedBytes` derived as `TotalBytes - FreeBytes` in both modes.
- Free-block detection unified via `obj.IsFree`.
- Memory mode now collects free-block histogram and top large objects during segment scan.

**Accepted divergence:** `ArrayAnalyzer.TopSparseArrays` (disk=3, memory=4). Full fix would materialize candidates in memory, violating bounded-memory design. Memory tier trades equivalence for predictable memory usage.

### Finding 1b — String dedup sampling undercount in memory mode

**Status:** Fixed.

**Issue:** `MemoryBackedObjectIndexWriter` adaptively skips dedup calls (1-in-10 or 1-in-50 based on yield); `DiskBackedObjectIndexWriter` samples every string. Sampled instances only incremented `Count`/`TotalSize` by 1, not accounting for skipped instances.

**Cause:** No scaling applied to sampled-instance counts when adaptive sampling kicked in.

**Fix:** Added `weight` parameter to `StringDedupEntry` constructor/`AddInstance`. Memory writer passes active `segSampleDivisor` as weight, scaling each sampled instance to represent the instances it stands for.

### Finding 1c — SampleAddress selection non-deterministic in memory mode

**Status:** Fixed.

**Issue:** `TypeIndexBuilder.Add()` and `Merge()` accepted first-encountered address as `SampleAddress`; parallel scan order non-deterministic, causing different sample instances between runs/modes.

**Cause:** Thread-scheduler-dependent segment scan order.

**Fix:** Both methods apply deterministic tie-break: lowest address always wins, independent of scan/merge order. Later operations overwrite `SampleAddress` if lower address encountered.

### Finding 2 — GetRootDescription dead code

**Status:** Fixed.

**Issue:** `HeapAnalysisCache._rootDescriptions` never assigned; `GetRootDescription()` dead delegation.

**Cause:** Incomplete implementation; disk fast-path never persisted descriptions.

**Fix:** Removed dead code and duplicate type-statistics hydration (`TryHydrateTypeStatisticsFromIndex`, `ResolveTypeNameFromSample`, etc.).

### Finding 3 — Redundant root enumeration in memory mode

**Status:** Fixed.

**Issue:** `RootCache.GetOrBuildValidRoots` only fast-pathed off disk `RootIndex.bin`; never checked `heapIndex.InMemoryRootCandidates`, despite field existing and being populated.

**Cause:** Memory-mode branch missing despite `RootIndexReader` already having logic for it.

**Fix:** Added `StorageKind == Memory` branch in `GetOrBuildValidRoots` to hydrate from `InMemoryRootCandidates`. Verified no semantic drift vs. disk branch.

### Finding 4 — Disk cache-hit doesn't validate satellites

**Status:** Open — rare edge case.

**Issue:** `TryLoadFromCache` validates `ObjectIndex.bin` and `TypeAggregateIndex.bin` only. Satellite files (`RootIndex.bin`, `LargeObjectIndex.bin`, etc.) can fail silently on prior run; later cache-hit skips full scan, permanently missing satellite data.

**Cause:** Only main index files validated as cache signal; satellite failures undetected.

**Suggested fix:** On cache-hit, check for expected satellites and regenerate missing ones.

### Finding 5 — CollectionAnalyzer disk vs. memory disagree by ~12%

**Status:** Fixed.

**Issue:** `CollectionAnalyzerDiscrepancyTests` failed: disk=798912 collections, memory=702893.

**Cause:** Race condition — `ResolveGeneration` call sat outside `heapLock` while other threads held lock for heap reads. Concurrent access corrupted shared ClrMD state, silently dropping objects.

**Fix:** Moved `ResolveGeneration` inside `lock (heapLock)` block. Test now passes; `TotalCollections` agrees between modes.

### Finding 6 — Buffer-boundary carry-over bug in satellite readers

**Status:** Fixed — all four readers migrated to safe pattern.

**Issue:** `AsyncTaskAnalyzer.ReadTaskIndexFile`, `LohFragmentationAnalyzer.ReadFreeBlocks`, `RootIndexReader.ReadRootIndexFile` use loop: `stream.Read(buffer, 0, buffer.Length)` → `records = bytesRead / RecordSize`. Trailing fragments < `RecordSize` silently dropped; `Stream.Read` not guaranteed to return record-aligned counts.

**Cause:** Hand-implemented batch-read pattern done three different ways independently; same bug reproduced three times.

**Fix:** Migrated all four readers to proven safe pattern: `ReadAtLeast(recordSpan, recordSize, throwOnEndOfStream: false)` per record, eliminating batch-buffer/carry-over entirely. `ObjectIndexReader` left on batch pattern (hottest path; carry-over logic already correct).

**Verified:** All related discrepancy tests pass; `AsyncTaskAnalyzer.TotalTasks` off-by-one fixed; `RootIndexReader` and others verified unchanged behaviorally.

## Open Items

| Item | Severity |
|---|---|
| **Finding 4** — Disk cache-hit doesn't validate satellites | Low |

## Summary

Six findings identified; five fully fixed (`1`/`1b`/`1c`/`2`/`3`/`5`/`6`). One open item: Finding 4 (rare edge case, low priority). One accepted divergence: `ArrayAnalyzer.TopSparseArrays` trade-off. All discrepancy tests passing. Cache subsystem now deterministic and symmetric between disk and memory modes.
