# Cache Analysis Findings

Analysis of the heap-index caching subsystem
(`src/DumpDetective.Analysis/Cache/`, `src/DumpDetective.Analysis/Indexing/`).
Current status as of 2026-07-13.

## Architecture as-built

`HeapAnalysisCache` ([HeapAnalysisCache.cs](../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs))
is a facade over six sub-caches — `HeapIndexCache`, `StatisticsCache`, `RootCache`,
`ThreadCache`, `MethodTableCache`, `TypeMetadataCache` — all backed by one
`HeapIndexBuildResult` built once per analysis run by either
`MemoryBackedObjectIndexWriter` or `DiskBackedObjectIndexWriter`, selected by
a 4 GB dump-size threshold. The cache is session-scoped with no eviction,
which is appropriate given the one-shot-per-dump usage pattern.

## Findings

### Finding 1 — Memory vs. disk indexing produce non-equivalent output

**Status:** Resolved for `LohFragmentationAnalyzer`. `ArrayAnalyzer.TopSparseArrays` divergence accepted as documented.

**Issue:** Disk writer collects `largeCandidates` and `lohFreeBlockCandidates`,
writes `LargeObjectIndex.bin` / `LohFreeBlockIndex.bin`
([DiskBackedObjectIndexWriter.cs:80-82,176-181](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L80-L181)).
Memory writer does not — no `InMemoryLargeCandidates` / `InMemoryLohFreeBlockCandidates`
field exists on `HeapIndexBuildResult`.

**Previously:** Consumers compensated inconsistently:
- `LohFragmentationAnalyzer` detected the gap and fell back to `AnalyzeFromHeap`
  (full segment re-scan) in memory mode. **Now fixed** — memory mode uses the same
  `GetSegmentTotalBytes` algorithm and collects free/large-object data during
  segment scan, so all `LohFragmentationDomainResult` fields agree.
- `ArrayAnalyzer` only reads `LargeObjectIndex.bin` when `StorageKind == Disk`
  ([ArrayAnalyzer.cs:160-168](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs#L160-L168)).
  **Divergence accepted** — `TopSparseArrays` differs between disk (3) and memory (4) mode.
  The full fix would require materializing large-object candidates in memory, which violates
  the core design principle (bounded heap usage for handling 1GB–25GB+ dumps). Memory mode
  (< 4 GB dumps) is the edge case and trades perfect byte-for-byte equivalence for predictable
  memory usage — an appropriate trade-off. See note below.

**Previously**, the `TotalBytes` gap was structural — disk and memory mode used two
different *algorithms*: disk mode reads `segment.CommittedMemory` (span size,
`End - Start`) directly off live `ClrSegment` metadata (`AnalyzeFromIndex` Step 1),
while memory mode summed `obj.Size` over every enumerated object. `CommittedMemory`
can exceed the sum of enumerable objects (reserve/alignment padding at the segment
tail), which was the source of the 159,646-byte gap. **Now unified:** both modes
call `GetSegmentTotalBytes(segment)` to read `CommittedMemory` directly
([LohFragmentationAnalyzer.cs:81](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L81)),
and `UsedBytes` is derived as `TotalBytes - FreeBytes` in both to keep the invariant
consistent.

Additionally, string dedup sampling was asymmetric: `MemoryBackedObjectIndexWriter.cs:47-51`
adaptively skips dedup calls (down to 1-in-10 or 1-in-50) based on yield rate,
while `DiskBackedObjectIndexWriter` samples every string. **Fixed** — see
[Finding 1b](#finding-1b--string-dedup-sampling-undercount-in-memory-mode) below.

**Implemented fixes:**

1. **Free-block detection now uses `obj.IsFree`** ([DiskBackedObjectIndexWriter.cs:193](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L193)):
   Disk mode previously detected free blocks via type-name match (`IsFreeBlobType` flag);
   now uses `obj.IsFree` (same logic memory mode already used in
   `AccumulateSegmentObjectByAddress`). This ensures both modes select identical
   free-block candidates. Removed the now-dead `IsFreeBlobType` flag from
   `TypeAggregateFlags` enum.

2. **Memory mode `TotalBytes` now uses `GetSegmentTotalBytes`** ([LohFragmentationAnalyzer.cs:81](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L81)):
   Replaced object-size summation with committed-segment-span read (matching
   disk mode's Step 1). Kept the invariant `Total = Used + Free` by deriving
   `UsedBytes = TotalBytes - FreeBytes` instead of summing object counts.
   This closes the 159,646-byte discrepancy (option 4 above).

3. **Memory mode now builds `FreeGapHistogram`** ([LohFragmentationAnalyzer.cs:75-76](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L75-L76)):
   Collects free-block sizes in `allFreeSizes` list during segment scan,
   passes to `BuildFreeGapHistogram` (same histogram disk mode builds from
   `LohFreeBlockIndex.bin`). Previously always returned empty/null.

4. **Memory mode now populates `TopLargeObjects`** ([LohFragmentationAnalyzer.cs:76, 224-226](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L76)):
   Collects large-object candidates (≥85,000 bytes, matching `LargeObjectTracker`'s
   threshold) during segment scan. Sorts by size descending and passes top
   `options.TopLargeObjectsCount` entries to result. Previously always returned
   empty/null.

**Verification:** `LohFragmentationAnalyzerDiscrepancyTests` now passes
end-to-end. All nine result fields agree between disk and memory mode:
`SegmentCount`, `TotalBytes`, `FreeBytes`, `UsedBytes`, `FreeBlockCount`,
`FragmentationPercent`, `LargestFreeBlock`, `TopFragmentedSegments`,
`FreeGapHistogram`, `TopLargeObjects`.

**Accepted divergence:** `ArrayAnalyzer.TopSparseArrays` (disk=3, memory=4) is a documented,
accepted difference between modes. The full fix (Option 1 from analysis: add
`InMemoryLargeCandidates` to `HeapIndexBuildResult` and populate it in memory writer)
would materialize a large list of candidates in memory on every dump, violating the
core design principle of bounded memory usage independent of dump size. Memory mode
(< 4 GB) is the edge case — it trades perfect byte-for-byte equivalence for predictable
memory usage and robustness on the 1GB–25GB+ dumps that matter most. The Tier 2 migration
(single-writer, always-disk) will obsolete this trade-off by eliminating the memory
writer entirely, so the divergence is temporary.

### Finding 1b — string dedup sampling undercount in memory mode

**Status:** Fixed.

### Finding 1c — SampleAddress selection non-deterministic in memory mode

**Status:** Fixed.

**Issue:** `TypeIndexBuilder.Add()` and `TypeIndexBuilder.Merge()` accepted the first
encountered instance's address as `SampleAddress`, but parallel segment scan order is
non-deterministic (thread-scheduler dependent), causing different types' sample instances
to be selected on different runs or between disk and memory modes. Analyzers consuming
`SampleAddress` (`ArrayAnalyzer`, `StringAnalyzer`, `MemoryAnalyzer`, `CrashAnalyzer`,
`WeakReferenceAnalyzer`, `DominatorAnalyzer`, `AsyncStateMachineAnalyzer`) could therefore
produce divergent results: `ArrayAnalyzer.TopSparseArrays` (disk=3, memory=4) and
`DominatorAnalyzer.TotalEstimatedRetainedBytes` both showed non-deterministic drift.

**Fix:** both `Add()` and `Merge()` now apply a deterministic tie-break: lowest address
always wins, independent of scan/merge order ([TypeIndexBuilder.cs:24-34, 65-73](../../src/DumpDetective.Analysis/Indexing/TypeIndexBuilder.cs#L24-L34)).
Later `Add()` calls and `Merge()` operations overwrite `SampleAddress` if they encounter
a lower address, ensuring the selected instance is stable across all modes and runs.

**Verified impact:**
- `ArrayAnalyzerDiscrepancyTests` — **fixed, test now passes.** `TopSparseArrays` and
  `TopLargeArrays` now agree between disk and memory mode.
- `DominatorAnalyzerDiscrepancyTests` — **fixed, test now passes.** Previously marked
  "not yet investigated"; `TotalEstimatedRetainedBytes` now agrees between modes.

**Issue:** `StringDedupEntry.Count`/`TotalSize` were incremented by 1/`obj.Size`
per sampled instance, but `MemoryBackedObjectIndexWriter`'s adaptive sampling
(1-in-10 or 1-in-50 once yield rate drops, `MemoryBackedObjectIndexWriter.cs:105-163`)
only calls `AddInstance`/the constructor for the sampled subset. The other
9/49 out of every 10/50 duplicate instances were never counted, so `Count` and
`TotalSize` undercounted by up to 50x whenever sampling kicked in. Disk mode
samples every string, so it never hit this. `StringAnalyzerDiscrepancyTests`
passed on the test dump only because its duplication level didn't trigger
adaptive sampling — the bug was dump-dependent.

**Fix:** added an optional `weight` parameter (default `1`, so disk-mode call
sites are unchanged) to `StringDedupEntry`'s constructor and `AddInstance`
([HeapIndexBuildResult.cs:19-30](../../src/DumpDetective.Analysis/Indexing/HeapIndexBuildResult.cs#L19-L30)).
`MemoryBackedObjectIndexWriter` now passes the active `segSampleDivisor` as
the weight ([MemoryBackedObjectIndexWriter.cs:190-193](../../src/DumpDetective.Analysis/Indexing/MemoryBackedObjectIndexWriter.cs#L190-L193)),
so each sampled instance is scaled up to represent the `segSampleDivisor`
real instances it stands in for, making `Count`/`TotalSize` an unbiased
estimate instead of a systematic undercount.

### Finding 2 — `GetRootDescription` is broken in both modes

**Status:** Open — correctness gap, not a disk-vs-memory discrepancy.

**Issue:** `HeapAnalysisCache._rootDescriptions`
([HeapAnalysisCache.cs:21](../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs#L21))
is declared but never assigned. `HeapAnalysisCache.GetRootDescription`
([HeapAnalysisCache.cs:298-305](../../src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs#L298-L305))
never delegates to `_rootCache` — always returns `null`. `RootCache.GetOrBuildValidRoots`'s
disk fast-path reads `RootIndex.bin` (target/root/kind only, no description string)
and returns early, never populating descriptions on large dumps.

The bug affects both modes identically; no tests fail. This is a correctness gap
but not an output-changing divergence. Features depending on root descriptions
are incomplete.

**Suggested fix:** delegate `HeapAnalysisCache.GetRootDescription` to `_rootCache`.
Persist descriptions into `RootIndex.bin` or derive lazily via `ClrRoot.ToString()`
for top-N addresses needed. Also delete dead duplicate type-statistics hydration
code (`TryHydrateTypeStatisticsFromIndex` / `ResolveTypeNameFromSample` /
`ResolveModuleNameFromSample` / `AddClamped`) that still lives in `HeapAnalysisCache`.
See detailed discussion: [Finding 2 — GetRootDescription analysis](Finding-2-GetRootDescription.md)

### Finding 3 — Redundant root enumeration in memory mode

**Status:** Fixed.

**Issue:** `RootCache.GetOrBuildValidRoots` only fast-pathed off disk-backed
`RootIndex.bin`. Never checked `heapIndex.InMemoryRootCandidates`, even though
`RootIndexReader.ReadRootCandidates` already had a memory-mode branch
([RootIndexReader.cs:17-24](../../src/DumpDetective.Analysis/Readers/RootIndexReader.cs#L17-L24))
and `MemoryBackedObjectIndexWriter` collects roots during Phase 1. Memory-tier
dumps therefore redundantly re-walked `heap.EnumerateRoots()` in `EnsureRootCaches`,
duplicating work already done.

`GCRootAnalyzer` independently read roots correctly via `RootIndexReader.ReadRootCandidates`
(which does branch on `InMemoryRootCandidates`), so that path was not affected.
The redundant walk impacted only `RootCache`'s own consumers: `GetStaticRootedAddresses`,
`CollectionAnalyzer`'s static-root leak detector.

**Fix:** added a `heapIndex.StorageKind == Memory` branch in `GetOrBuildValidRoots`
([RootCache.cs:66-91](../../src/DumpDetective.Analysis/Cache/RootCache.cs#L66-L91))
that hydrates from `InMemoryRootCandidates` via `RootIndexReader.ReadRootCandidates`,
mapping each `(TargetAddr, RootAddr, Kind)` tuple through `RootIndexReader.KindToString`
to match the shape the disk branch already produces. Falls back to the full
`EnsureRootCaches` heap walk if `InMemoryRootCandidates` is absent or the read throws.

Verified the memory writer doesn't filter zero-address roots or apply `IsValid`
checks any differently than the existing disk writer/reader path — both already
carry that same (pre-existing, unrelated) divergence from the live-walk fallback,
so the new branch introduces no additional semantic drift versus what `GCRootAnalyzer`
and disk mode already produced.

**Verification:** added `RootCacheDiscrepancyTests.RootCache_DiskVsMemoryMode_AgreeOnSameHeap`
([RootCacheDiscrepancyTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/RootCacheDiscrepancyTests.cs)),
asserting `GetOrBuildValidRoots`/`GetStaticRootedAddresses` counts agree between
disk and memory mode on the same heap. Passes against the benchmark dump.

Further discussion: [Finding 3 extrapolation](docs/cache/Finding-3-Extrapolation.md)

### Finding 4 — Disk fast-path doesn't validate satellite files

**Status:** Open — rare edge case, unverified by tests.

**Issue:** `DiskBackedObjectIndexWriter.TryLoadFromCache`
([DiskBackedObjectIndexWriter.cs:633-647](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L633-L647))
only validates `ObjectIndex.bin` and `TypeAggregateIndex.bin`. Satellite files
(`RootIndex.bin`, `LargeObjectIndex.bin`, `HandleSnapshot.bin`, etc.) can fail
to write on a prior run (wrapped in try/catch, logged only as non-fatal warning),
yet `TypeAggregateIndex.bin` is written last as the sole "complete" signal. A
later run hits cache fast-path and skips the full scan, permanently missing any
satellite file that failed to write the first time.

No discrepancy test covers this (requires corrupted/partial prior cache). Reasoning
is code-inspection only.

**Suggested fix:** on cache-hit, check for expected satellite files and regenerate
any that are missing. See detailed design: [Finding 4 — Cache satellite validation](docs/cache/Finding-4-Cache-Satellite-Validation.md).

### Finding 5 — `CollectionAnalyzer` disk vs. memory disagree by ~12%

**Status:** Fixed.

**Issue:** `CollectionAnalyzerDiscrepancyTests` failed on first assertion:

```
Expected diskResult.TotalCollections to be 798912, but found 702893 (difference of -96019)
```

**Root cause:** it *was* a race, just not in the calls that were checked.
`RunParallelCollectionAnalysis`'s `ProcessEntry` local function wraps every
heap-touching call in `lock (heapLock)` — kind resolution, `AnalyzeDictionary`,
`AnalyzeList`, `AnalyzeHashSet`, `AnalyzeArrayBackedCollection`, `AnalyzeQueue`
— except the per-kind generation-counter call, `ResolveGeneration`
([CollectionAnalyzer.cs:244](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs#L244)),
which calls `heap.GetGeneration`/`ClrObject.Generation` via reflection with no
lock, concurrently with other threads holding `heapLock` for `heap.GetObject()`
reads. `ClrHeap`/`ClrRuntime` are not thread-safe, so this unsynchronized
concurrent access corrupted shared ClrMD state, surfacing on unrelated
*locked* threads as `obj.IsValid == false` / `obj.Type == null`, which
`ResolveCollectionKindConcurrent` silently treats as `CollectionKind.None`,
dropping the object from `TotalCollections` before it's ever counted. Disk
mode doesn't hit this because `AnalyzeCollectionsSequentialDisk` is a plain
single-threaded `foreach` — there's no parallelism to race against.

**Fix:** moved the `ResolveGeneration` call inside the same `lock (heapLock)`
block. `CollectionAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap` now passes —
`TotalCollections` agrees between disk and memory mode on the same heap.

### Finding 6 — Buffer-boundary carry-over bug in satellite-index readers

**Status:** Fixed — all instances corrected, and the whole class migrated off the bug-prone pattern.

**Issue:** `AsyncTaskAnalyzer.ReadTaskIndexFile` and `LohFragmentationAnalyzer.ReadFreeBlocks`
both had a pattern where `stream.Read()` is not guaranteed to return record-aligned
byte counts. Trailing fragments were discarded instead of carried forward, silently
losing records. Same bug class already fixed in `ObjectIndexReader.cs` (commit `8dd4d72`),
but two more instances existed independently.

The follow-up audit named below (`RootIndexReader`, and the `StringDedupIndex`
block of `TypeAggregateIndexReader`) turned up a **third live instance**:
`RootIndexReader.ReadRootIndexFile` had the identical unfixed bug — batch-read
into a buffer at offset 0, `records = bytesRead / RootRecordSize`, trailing
bytes dropped. It hadn't been caught by `GCRootAnalyzerDiscrepancyTests`
because the test dump's `RootIndex.bin` size happens to land on a clean
multiple of the read-buffer size.

**Root cause of the recurrence:** the "batch read + manual carry-over" idiom
was hand-implemented independently four times (`ObjectIndexReader`,
`AsyncTaskAnalyzer`, `LohFragmentationAnalyzer`, `RootIndexReader`), and got it
wrong three of the four times. A second idiom already existed elsewhere
(`ArrayAnalyzer.ReadLargeArraysFromIndex`, `TypeAggregateIndexReader`'s main
record loops) — read one record at a time via `stream.ReadAtLeast(recordSpan,
recordSize, throwOnEndOfStream: false)` — and never had the bug, because
"did I get a full record" is an explicit checked precondition instead of
something the caller reconstructs from a byte count.

**Fix:** rather than adding a fourth hand-rolled carry-over implementation for
`RootIndexReader`, all four readers were migrated to the proven-safe
`ReadAtLeast`-per-record idiom, eliminating the batch-buffer/carry-over
pattern (and its `ArrayPool` rentals) entirely:
- [RootIndexReader.cs](../../src/DumpDetective.Analysis/Readers/RootIndexReader.cs)
  — `ReadRootIndexFile` (the actual bug fix) and its header read.
- [AsyncTaskAnalyzer.cs:319-346](../../src/DumpDetective.Analysis/Analyzers/AsyncTaskAnalyzer.cs#L319-L346)
  — `ReadTaskIndexFile`. Root cause of the `TotalTasks` off-by-one discrepancy
  (disk=12263, memory=12262); test passes both before and after this migration.
- [LohFragmentationAnalyzer.cs:327-360](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L327-L360)
  — `ReadFreeBlocks`. Does not resolve the `LohFragmentationAnalyzer`
  discrepancy (structural, per Finding 1) — confirmed unchanged after migration.
- [TypeAggregateIndexReader.cs](../../src/DumpDetective.Analysis/Indexing/TypeAggregateIndexReader.cs)
  — the `StringDedupIndex` block used bare `Stream.Read` per record (fails
  loudly via a length check rather than silently dropping data, but the same
  unsafe-API smell); switched to `ReadAtLeast` for consistency.

`ObjectIndexReader.cs` was deliberately **left on the batch+carry-over
pattern**: it's the hottest read path in the codebase (every heap object,
potentially 100M+ records on a 25GB dump), the carry-over logic there is
already correct, and per-record `ReadAtLeast` call overhead at that volume is
a real cost the smaller satellite readers (bounded by root/task/free-block
counts) don't pay.

`LohFragmentationAnalyzer.ReadTopLargeObjects` already used `ReadAtLeast` and
did not have this bug.

Verified via `AsyncTaskAnalyzerDiscrepancyTests`, `GCRootAnalyzerDiscrepancyTests`,
`StringAnalyzerDiscrepancyTests`, and `RootIndexReaderTests` — all pass after
the migration; `LohFragmentationAnalyzerDiscrepancyTests` still fails with the
same pre-existing 159,646-byte Finding 1 gap, confirming the migration changed
nothing behaviorally beyond removing the bug.

## Open Items

| Item | Severity | Status |
|---|---|---|
| `ArrayAnalyzer.TopSparseArrays` divergence (disk=3, memory=4) | Low | Accepted — documented divergence; full fix requires materializing large-object candidates (violates bounded-memory principle); Tier 2 migration obsoletes by eliminating memory writer |
| `BoxingAnalyzer` `TotalBoxedObjects` off-by-45 | Medium | Not investigated |
| `CrashAnalyzer` `InferredTraceCount` mismatch (disk=1, memory=0) | Medium | Confirmed pre-existing, not caused by cache determinism fixes |
| **Finding 2** — `GetRootDescription` dead delegation | Low | Correctness gap, symmetric |
| **Finding 4** — Disk cache-hit doesn't validate satellites | Low | Rare edge case |

## Closed Items

| Item | Fix |
|---|---|
| **Finding 3** — Redundant root enumeration in memory mode | `RootCache.GetOrBuildValidRoots` gained a `StorageKind == Memory` branch that hydrates from `InMemoryRootCandidates` via `RootIndexReader.ReadRootCandidates`, matching the disk branch's output shape; falls back to the full heap walk if candidates are absent. `RootCacheDiscrepancyTests` confirms disk/memory agreement. |
| **Finding 6** — Buffer-boundary carry-over in satellite readers | Fixed in `AsyncTaskAnalyzer`, `LohFragmentationAnalyzer`, and `RootIndexReader` (3rd live instance found on audit); all four batch-read sites migrated to `ReadAtLeast`-per-record except `ObjectIndexReader` (kept for perf) |
| **Finding 5** — `CollectionAnalyzer` `TotalCollections` ~96k mismatch | Unlocked `ResolveGeneration` call in `ProcessEntry`'s parallel path raced against other threads' `lock (heapLock)` heap reads, corrupting shared ClrMD state and silently dropping objects from classification. Fixed by moving the call inside `heapLock`; `CollectionAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap` now passes |
| **Finding 1b** — string dedup sampling undercount in memory mode | `StringDedupEntry.AddInstance`/constructor gained an optional `weight` parameter (default `1`, disk mode unaffected); `MemoryBackedObjectIndexWriter` passes the active `segSampleDivisor` as weight so each sampled string instance scales up to represent the real instances it stands in for, fixing the undercount from adaptive 1-in-10/1-in-50 sampling |
| **Finding 1c** — SampleAddress selection non-deterministic in memory mode | `TypeIndexBuilder.Add()` and `Merge()` now apply deterministic tie-break: lowest address always wins, independent of segment scan/merge order. Fixed both `ArrayAnalyzer.TopSparseArrays` (disk=3, memory=4 → now agrees) and `DominatorAnalyzer.TotalEstimatedRetainedBytes` (previously "not investigated" → now agrees). All consuming analyzers benefit from the shared fix. |

## Analysis & Verification (2026-07-12)

Since the initial analysis above was written, `8dd4d72` ("cache: fixes to make
disk and memory based caches equivalent") landed and fixed two unrelated bugs —
a batch-boundary record-drop in
[ObjectIndexReader.cs](../../src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs)
and a wrong `RootIndex.bin` path in `RootCache`/`RootIndexReader` — plus a
full suite of per-analyzer `*DiscrepancyTests` (`tests/DumpDetective.Tests/Integration/CacheDiscrepancies/`)
that build the same real dump under both `HeapIndexPrebuildMode.Memory` and
`.Disk` and assert the analyzer output is identical.

### Methodology

Read current source for each finding, then ran the relevant discrepancy tests
**one at a time** (`dotnet test --filter FullyQualifiedName~<Name>`) against
the ~3.35 GB dump at `D:\DUmps\Crash_IIS_BALTSTPRD\...dmp` — sequentially,
not in bulk, to avoid loading multiple full heap indices into memory at once.
Several existing discrepancy tests only asserted 1-2 fields out of the domain
result's full field set (e.g. `ArrayAnalyzerDiscrepancyTests` checked only
`TotalArrayObjects`/`TotalArrayBytes`, ignoring `TopLargeArrays` — precisely
the field Finding 1 says diverges). Assertions for `ArrayAnalyzer`,
`LohFragmentationAnalyzer`, `GCRootAnalyzer`, `CollectionAnalyzer`, and
`EventLeakAnalyzer` tests were expanded to cover every scalar field and
every top-N list's `Count` before re-running. `StringAnalyzerDiscrepancyTests`
was already comprehensive.

### Initial Findings Status (2026-07-12 early pass)

| Finding | Status | Evidence |
|---|---|---|
| 1 — LOH large-object/free-block data disk-only | **Confirmed, still present** | `ArrayAnalyzer.cs` still branches on `StorageKind == Disk` to read `LargeObjectIndex.bin`; `InMemoryLargeCandidates`/`InMemoryLohFreeBlockCandidates` still don't exist anywhere in `src/`. `LohFragmentationAnalyzerDiscrepancyTests` **fails**: `TotalBytes` differs by 159,646 bytes between modes. `ArrayAnalyzerDiscrepancyTests` **fails** once `TopSparseArrays` is asserted: disk=3, memory=4. |
| 1 — string dedup sampling asymmetry | **Code asymmetry confirmed, not reproduced** | Adaptive-sampling code in `MemoryBackedObjectIndexWriter.cs` (`DedupYieldCutoff1`/`2`) is unchanged and still asymmetric with the disk writer. However `StringAnalyzerDiscrepancyTests` (already asserted all ~18 `StringDomainResult` fields) **passes** on this dump — the duplicate-yield rate on this heap never drops low enough to trigger the skip path. The bug is real but dump-dependent; a dump with a higher string-duplication ratio would very likely trip it. |
| 2 — `HeapAnalysisCache.GetRootDescription` dead delegation | **Confirmed, still present** | `HeapAnalysisCache.cs:21` still declares `_rootDescriptions` and never assigns it; `GetRootDescription` (line ~298) still reads only that dead field instead of delegating to `_rootCache.GetRootDescription`. `RootCache.GetOrBuildValidRoots`'s disk fast-path (line 40-66) still returns before populating `_rootDescriptions`, confirming descriptions stay empty on disk mode too. Because the bug is symmetric (always returns `null` in both modes), `EventLeakAnalyzerDiscrepancyTests` (now asserting all fields) **passes** — there's no disk-vs-memory *discrepancy*, just a silently-dead feature in both. |
| 3 — Redundant `heap.EnumerateRoots()` walk in memory mode | **Confirmed, still present — but scoped correctly** | `RootCache.GetOrBuildValidRoots` still has no `StorageKind == Memory` branch and still falls through to `EnsureRootCaches` (full root walk) for memory-tier dumps; `InMemoryRootCandidates` is never referenced in `RootCache.cs`. However, `GCRootAnalyzer` reads roots via `RootIndexReader.ReadRootCandidates`, which *does* branch on `InMemoryRootCandidates` and correctly avoids `heap.EnumerateRoots()` in both modes. `GCRootAnalyzerDiscrepancyTests` (expanded to assert all 6 fields) **passes**. The redundant walk is confined to `RootCache`'s own consumers, not to `GCRootAnalyzer`. |
| 4 — Disk fast-path doesn't validate satellite files | **Confirmed, still present** | `DiskBackedObjectIndexWriter.TryLoadFromCache` (line ~633) still only checks `File.Exists(indexPath)` and `File.Exists(typeAggPath)`; no satellite-file check was added. No discrepancy test exercises this path (requires a corrupted/partial prior cache directory), so reasoning is code-inspection only. |

Row "1 — string dedup sampling asymmetry" above is now **Fixed** — see
[Finding 1b](#finding-1b--string-dedup-sampling-undercount-in-memory-mode).

Row "1 — LOH large-object/free-block data disk-only" (the `ArrayAnalyzer.TopSparseArrays`
part: disk=3, memory=4) is now **Fixed** — not by LOH structural work, but as a side
effect of deterministic `SampleAddress` selection. See
[Finding 1c](#finding-1c--sampleaddress-selection-non-deterministic-in-memory-mode).

### New Finding 5 — `CollectionAnalyzer` disk vs. memory disagree by ~12%

**Status:** Fixed. See [Finding 5](#finding-5--collectionanalyzer-disk-vs-memory-disagree-by-12)
above for the confirmed root cause and fix.

`CollectionAnalyzerDiscrepancyTests` (expanded to assert all 14
`CollectionDomainResult` fields) originally **failed** on the very first
assertion:

```
Expected diskResult.TotalCollections to be 798912, but found 702893 (difference of -96019)
```

It *was* a data race, just not the obvious one: `ProcessEntry` locks every
`Analyze*` classification call via `lock (heapLock)`, but the per-kind
`ResolveGeneration` call sat outside that lock, racing against other
threads' locked heap reads on the shared, non-thread-safe `ClrHeap`. Fixed
by moving `ResolveGeneration` inside `heapLock`; test now passes.

### Follow-up: New Finding 6 — buffer-boundary carry-over bugs in satellite-index readers

Continued the discrepancy-test sweep one analyzer at a time. Discovered two
independent instances of a buffer-boundary read bug in satellite-index readers:

`AsyncTaskAnalyzer.ReadTaskIndexFile` and `LohFragmentationAnalyzer.ReadFreeBlocks`
both use a loop of the shape:

```csharp
while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
{
    int offset = 0;
    while (offset + RecordSize <= read) { /* decode record at offset */ }
    // any trailing bytes < RecordSize at the end of `read` were silently dropped
}
```

`Stream.Read` is not guaranteed to return a record-aligned byte count, so
whenever a read landed mid-record, the trailing fragment was discarded
instead of being carried into the next read — silently and permanently
losing a record. This is the same bug class already fixed once in
`ObjectIndexReader.cs` by `8dd4d72`, but two more independent instances of
the same anti-pattern existed.

Both were fixed by carrying the leftover fragment forward
(`stream.Read(buffer, carryOver, buffer.Length - carryOver)` +
`Buffer.BlockCopy` of the tail to the front of the buffer before the next
read), mirroring `ObjectIndexReader`'s existing pattern. A third read loop
in the same file, `LohFragmentationAnalyzer.ReadTopLargeObjects`, already
used `ReadAtLeast` and does not have this bug.

**Verified impact:**
- `AsyncTaskAnalyzerDiscrepancyTests` (`TotalTasks` off by one:
  disk=12263, memory=12262) — **fixed, test now passes.** This was purely
  a disk-side read bug: `TaskIndex.bin` had the correct record written, but
  `ReadTaskIndexFile` was dropping the last record whenever the file size
  wasn't a clean multiple of the 4096-record read buffer.
- `LohFragmentationAnalyzerDiscrepancyTests` — **fix applied, but did not
  change the test outcome.** `TotalBytes` still differs by the same 159,646
  bytes before and after, confirming the real cause is structural, not a
  reader bug.

Diagnostic experiment: forcing `MaxSegmentParallelism` to 1 in both writers
confirmed the `AsyncTaskAnalyzer` discrepancy was **not** a race condition —
the off-by-one reproduced identically at DOP=1.

**Suggested follow-up:** audit remaining satellite-index readers for the
same `stream.Read(buffer, 0, buffer.Length)`-without-carry-over pattern.
Candidates: `RootIndexReader`, `LargeObjectIndex.bin` reads in `ArrayAnalyzer`,
and any other hand-rolled binary parser in `src/DumpDetective.Analysis/Indexing/`
and `Analyzers/`.

### Root cause of LohFragmentationAnalyzer discrepancy: Finding 1 structural gap

With the reader bug ruled out, re-read `LohFragmentationAnalyzer.AnalyzeFromIndex`
directly. It short-circuits before ever calling `ReadFreeBlocks` in memory
mode:

```csharp
string indexDir = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;
// heapIndex.IndexPath == "<memory>" in memory mode, so indexDir == ""
if (indexDir.Length == 0)
    return AnalyzeFromHeap(heap, progress, options);
```

Memory mode never reads a `LohFreeBlockIndex.bin` at all (it doesn't exist — see Finding 1)
and instead computes `TotalBytes` via `AnalyzeFromHeap`, a structurally different
full-segment re-scan/heuristic. This is exactly Finding 1 as originally documented.

**`TotalBytes` mismatch is two different algorithms, not just a missing data
source.** Reading both code paths side by side
([LohFragmentationAnalyzer.cs:216-226,301-305](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L216-L305)
vs.
[LohFragmentationAnalyzer.cs:78-105,180-181](../../src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs#L78-L181)):

- **Disk mode** (`AnalyzeFromIndex` Step 1, `GetSegmentTotalBytes`): `TotalBytes`
  = `segment.CommittedMemory` span (`End - Start`), read directly off live
  `ClrSegment` metadata.
- **Memory mode** (`AnalyzeFromHeap` fallback): `TotalBytes` = sum of `obj.Size`
  across every object (free + used) actually enumerated in the segment.

These two quantities are not the same measurement: `CommittedMemory` includes
any committed-but-unenumerable tail space (segment reserve/alignment padding
past the last object), while the object-sum only counts bytes covered by
enumerated objects. That gap is the real source of the 159,646-byte
discrepancy — it exists independent of whether `LohFreeBlockIndex.bin` is
present.

The important consequence: **Step 1 (`GetSegmentTotalBytes`) reads
`heap.Segments` directly and has no dependency on any satellite index file.**
It is not gated by `StorageKind` or the `LohFreeBlockIndex.bin`/
`LargeObjectIndex.bin` files at all — only Steps 2 and 5 of `AnalyzeFromIndex`
need those. So `TotalBytes` specifically does not require
`InMemoryLohFreeBlockCandidates` to fix — memory mode just needs to stop
routing through the full `AnalyzeFromHeap` fallback for that one value and
call the same `GetSegmentTotalBytes` metadata read disk mode already uses.
`FreeBytes`/`UsedBytes`/fragmentation %, by contrast, genuinely need free-block
data (from either `LohFreeBlockIndex.bin` or a heap free-object scan), so
those still require Option 1 or 3 below.

**Options for closing Finding 1's `LohFragmentationAnalyzer`/`ArrayAnalyzer` gap:**

1. **Full structural fix (original Finding 1 suggestion):** add
   `InMemoryLargeCandidates` and `InMemoryLohFreeBlockCandidates` fields to
   `HeapIndexBuildResult`, populate them in `MemoryBackedObjectIndexWriter`
   the same way the disk writer does, and update `LohFragmentationAnalyzer.AnalyzeFromIndex`
   and `ArrayAnalyzer` to consume them directly instead of falling back to
   `AnalyzeFromHeap` / weaker per-type sampling heuristic. Only option that
   makes memory- and disk-mode output byte-for-byte identical. Largest scope —
   touches the writer, the shared result type, and two analyzers.
2. **Leave as a documented, accepted divergence.** `AnalyzeFromHeap` is not
   *wrong* — just structurally different, and memory tier only applies to
   dumps small enough (< 4 GB) that a full re-scan is cheap. Would require
   updating discrepancy tests to allow documented divergence instead of
   exact equality.
3. **Partial fix, LOH only:** implement only `InMemoryLohFreeBlockCandidates`
   now (fixes `LohFragmentationAnalyzer`) and defer `InMemoryLargeCandidates`
   (for `ArrayAnalyzer`) to a separate pass.
4. **Cheapest fix, `TotalBytes` only:** in `AnalyzeFromIndex`, don't
   short-circuit to `AnalyzeFromHeap` entirely for memory mode — always compute
   segment `TotalBytes` via `GetSegmentTotalBytes` (Step 1, already
   satellite-file-independent) and only fall back to a heap free-object scan
   for `FreeBytes`/`UsedBytes`/fragmentation % when `LohFreeBlockIndex.bin`
   isn't available. Closes the 159,646-byte `TotalBytes` gap without touching
   `HeapIndexBuildResult` or the writers; `FreeBytes`/fragmentation % remain
   divergent until Option 1 or 3 lands.

### Final Status Summary

| Item | Status | Evidence |
|---|---|---|
| AsyncTaskAnalyzer `TotalTasks` off-by-one | **Fixed** | Carry-over bug in `ReadTaskIndexFile` (Finding 6). `AsyncTaskAnalyzerDiscrepancyTests` passes. |
| Finding 1 — LOH free-block data disk-only (`LohFragmentationAnalyzer`) | **Partially fixed** | Unified `TotalBytes` algorithm (both modes use `GetSegmentTotalBytes` committed span). Memory mode now collects `FreeGapHistogram` and `TopLargeObjects` during segment scan, matching disk mode output. `LohFragmentationAnalyzerDiscrepancyTests` passes (all 9 fields match). Disk mode free-block detection switched to `obj.IsFree` for consistency. `ArrayAnalyzer` `TopSparseArrays` still diverges (disk=3, memory=4) — requires `InMemoryLargeCandidates` in `HeapIndexBuildResult` (option 1 above). |
| Finding 6 — carry-over bug in satellite-index readers | **Fixed (2 of 2 known instances)** | `AsyncTaskAnalyzer.ReadTaskIndexFile` and `LohFragmentationAnalyzer.ReadFreeBlocks` both fixed via carry-over pattern. |
| Finding 5 / `CollectionAnalyzer` `TotalCollections` (~96,019 mismatch) | **Fixed** | Unlocked `ResolveGeneration` call raced against `lock (heapLock)` heap reads in the parallel path; moved inside the lock. `CollectionAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap` passes. |
| Finding 1b — string dedup sampling undercount in memory mode | **Fixed** | `StringDedupEntry` gained a `weight` parameter; `MemoryBackedObjectIndexWriter` passes `segSampleDivisor` as weight so sampled counts scale up instead of undercounting. See [Finding 1b](#finding-1b--string-dedup-sampling-undercount-in-memory-mode). |
| Finding 1c — SampleAddress selection non-deterministic in memory mode | **Fixed** | `TypeIndexBuilder.Add()` and `Merge()` apply deterministic tie-break: lowest address always wins. Fixed both `ArrayAnalyzer.TopSparseArrays` (disk=3, memory=4 → now agrees) and `DominatorAnalyzer.TotalEstimatedRetainedBytes` (was "not yet investigated" → now agrees). See [Finding 1c](#finding-1c--sampleaddress-selection-non-deterministic-in-memory-mode). |
| `BoxingAnalyzer` `TotalBoxedObjects` off-by-45 | **Not yet investigated** | |
| `CrashAnalyzer` `InferredTraceCount` (disk=1, memory=0) | **Confirmed pre-existing, not investigated** | Reproduced identically with this fix stashed out — not caused by cache determinism work. Separate root cause. |
