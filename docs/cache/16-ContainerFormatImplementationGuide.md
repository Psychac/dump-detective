# Single Container File + Table of Contents (`cache.bin`) — Implementation Guide

Implementation record for the first row of Tier 2 in
[15-ImplementationRoadmap.md](15-ImplementationRoadmap.md) ("Single container file +
table contents"), scoped per
[14-CleanSlateCacheRedesign.md § The core idea](14-CleanSlateCacheRedesign.md#the-core-idea)
and [§ File layout](14-CleanSlateCacheRedesign.md#file-layout). This doc is the
working plan for that row — update it as the migration lands, don't copy rationale
back into 14/15.

## Context

Today `DiskBackedObjectIndexWriter` (disk-mode only — memory-mode is untouched by
this change, per the roadmap's own tier sequencing) writes 8 independently-formatted
files into `.dumpindex/` (`ObjectIndex.bin`, `TypeAggregateIndex.bin`, `RootIndex.bin`,
`HandleSnapshot.bin`, `TaskIndex.bin`, `EventCandidateIndex.bin`, `LargeObjectIndex.bin`,
`LohFreeBlockIndex.bin`), plus a `StringDedupIndex.bin` + `StringDedupIndex.meta.json`
sidecar pair. `PartialRefEdgeIndex.bin` is declared in `DumpIndexPaths` but nothing
currently writes it — dead, dropped rather than carried forward. Cache-hit validity
today is "does `TypeAggregateIndex.bin` (written last) exist and match the dump's
size/mtime stamp" — a completeness proxy across 9 files that doc 11 already flagged
as fragile (only 2 of the files get validated).

**Verified during prep-read (source, not doc 11):** `EventLeakAnalyzer` has no inline
`FileStream` read at all — it always does a full `heap.EnumerateObjects()` scan in
both disk and memory mode, and `EventCandidateIndex.bin` has no reader anywhere in the
codebase (nor does anything consume the mirroring `InMemoryEventCandidates` array).
That section is written but currently dead. Decision: keep writing it through this
migration anyway (unlike the already-dead `PartialRefEdgeIndex.bin`, which stays
dropped) — it's real perf-win data worth preserving for a follow-up that wires
`EventLeakAnalyzer` up to prefer it. `EventLeakAnalyzer.cs` is therefore **not** one
of the analyzer files touched in this migration.

`WeakReferenceAnalyzer.cs` (`ReadWeakHandlesFromFile`, `CountDependentHandleDeadKeys`)
has the same inline-`FileStream`-over-`HandleSnapshot.bin` pattern as the other
consolidated readers and is in scope — leaving it on a direct
`new FileStream(DumpIndexPaths.HandleSnapshot(...))` open would break once
`HandleSnapshot.bin` stops existing as a standalone file.

Scope: replace all of that with one file, `cache.bin`, holding a fixed 64-byte
`FileHeader` + a table of contents, with each current file's payload becoming one
TOC-addressed section. This is deliberately **not** the columnar layout, schema-driven
codegen, content-addressed hash, or checksum-validation rows — those are separate,
already-sequenced roadmap items. Atomic `.tmp` + rename **is** included (cheap, and
doc 14 treats it as inherent to the container design). Section checksums **are**
computed and stored (unused by readers until the later corruption-resilience row).
The currently-scattered inline-`FileStream` readers in `AsyncTaskAnalyzer`,
`WeakReferenceAnalyzer`, `LohFragmentationAnalyzer`, and `ArrayAnalyzer` **are**
consolidated behind a shared helper `CacheSectionHelper.TryOpenCacheSection` rather than patched individually.

De-risking approach (per doc 14's own recommendation): the existing
`*DiscrepancyTests` in `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/`
already assert disk-mode/memory-mode agreement per analyzer. Run them before touching
anything (baseline), then again after migration — any test that passes before and
fails after isolates a container-format bug, not a pre-existing one.

## Format

New file `src/DumpDetective.Analysis/Indexing/Container/CacheContainerFormat.cs`:

```
FileHeader (64 bytes, offset 0):
  Magic          8 bytes  "DDCACHE1"
  FormatVersion  4 bytes  int, = 1
  DumpContentHash 32 bytes  reserved, zero-filled (content-addressed key is a later
                            row — cache validity for now stays the existing dump
                            size+mtime stamp, carried in the TypeAggregates section
                            exactly as today)
  SectionCount   4 bytes  int
  TocOffset      8 bytes  long, = 64
  Reserved       8 bytes  zero

TOC entry (32 bytes each, SectionCount of them, starting at TocOffset):
  SectionId    4 bytes  int (stable CacheSectionId enum)
  Offset       8 bytes  long, absolute offset into cache.bin
  Length       8 bytes  long, byte length of the section
  RecordCount  8 bytes  long
  Checksum     4 bytes  uint, XxHash32 of the section's bytes (System.IO.Hashing,
                         already a project dependency via XxHash64 usage in
                         DiskBackedObjectIndexWriter) — written now, not yet
                         validated on read (that's the corruption-resilience row)
```

`CacheSectionId` enum: `Objects`, `TypeAggregates`, `Roots`, `Handles`, `Tasks`,
`EventCandidates`, `LargeObjects`, `LohFreeBlocks`, `StringDedup`, `StringDedupMeta`
(the JSON distribution summary folds into the container as its own opaque UTF-8-bytes
section instead of `StringDedupIndex.meta.json` — no format change to its content,
just where the bytes live).

**Section payload = exactly today's per-file bytes, unchanged.** Each section still
starts with its existing 24-byte `IndexHeader` (or `TypeAggregateIndexWriter`'s
richer header, or the ad hoc 12-byte `StringDedupIndex.bin` header) followed by the
same records in the same layout. The TOC's `Offset`/`Length`/`RecordCount` are
strictly additive metadata — every reader's parsing logic (offset math, record
layout, per-record deserialization) stays untouched; the whole change is confined to
"how do I get a `Stream` for this section" instead of "how do I parse it." Stripping
the now-redundant per-section magic/version is the kind of cleanup the roadmap defers
to the **schema-driven writer/reader parity** row — out of scope here.

## Writer — `CacheContainerWriter`

`src/DumpDetective.Analysis/Indexing/Container/CacheContainerWriter.cs`:

- Opens `cache.bin.tmp` (`FileMode.Create`, seekable `FileStream`).
- Writes a zeroed placeholder `FileHeader` + TOC region, advances to the first data
  offset (`64 + SectionCount * 32`) — mirrors the existing "placeholder header, patch
  later" idiom already used by `RootIndexWriter`/`TaskIndexWriter`/etc.
- `BeginSection(CacheSectionId id)` returns the current absolute stream position;
  callers write directly to the writer's underlying `Stream` (exposed as a property)
  using their existing per-format write logic, wrapped by a small
  `HashingWriteStream` (incremental `XxHash32`, no re-read pass) so the checksum is
  computed for free as bytes are written.
- `EndSection(CacheSectionId id, long startOffset, long recordCount)` records
  `(id, startOffset, currentPosition - startOffset, recordCount, hash)` in an
  in-memory `List<CacheTocEntry>`.
- `Finish()`: seek to `TocOffset`, write the real TOC; seek to 0, write the real
  `FileHeader` (`SectionCount`, `TocOffset`); `Flush(flushToDisk: true)`; dispose;
  `File.Move(tmpPath, finalPath, overwrite: true)`.
- On any exception during build, delete the `.tmp` file in a `finally`/`catch` (same
  cleanup discipline `DiskBackedObjectIndexWriter` already applies to its per-segment
  scratch files) — never leave a stray `.tmp` behind.

### Writer call-site changes

`DiskBackedObjectIndexWriter.cs` and `Satellite/*.cs`. Every satellite writer
currently does `string filePath` → own `new FileStream(filePath, FileMode.Create,
...)`. Change each to accept a `Stream` (the container's stream, positioned at the
section start) instead of a path:

- `RootIndexWriter.Write`, `TaskIndexWriter` ctor, `EventCandidateIndexWriter` ctor,
  `LohFreeBlockWriter.Write`/`WriteFromCandidates`, `HandleSnapshotWriter.Write`,
  `LargeObjectTracker.Write`, and the inline `ObjectIndex`/`StringDedupIndex`/
  `TypeAggregateIndex` writing logic in `DiskBackedObjectIndexWriter.Build` — drop
  the `new FileStream(...)` line, take the shared `Stream` parameter instead.
- `IndexHeader.PatchRecordCount(Stream stream, long recordCount)` gets a
  `long baseOffset = 0` parameter; every "patch the placeholder header" call site
  (`RootIndexWriter`, `TaskIndexWriter.Flush`, `EventCandidateIndexWriter.Flush`,
  `LohFreeBlockWriter`, `HandleSnapshotWriter`, `DiskBackedObjectIndexWriter`'s
  `WriteObjIndexHeader` rewind) passes the section's `BeginSection` offset instead
  of implicitly seeking to absolute 0. `LargeObjectTracker` needs no patch (record
  count — capped at 100 — is known before writing).
- `DiskBackedObjectIndexWriter.Build` becomes the container's sole orchestrator:
  open one `CacheContainerWriter`, call each satellite writer in sequence against
  it (same order as today's `WriteSatelliteFiles`), call `Finish()` once at the end.
  The per-section try/catch-and-collect-`SatelliteWarnings` behavior is unchanged
  in spirit — a failed section still means "skip it, keep going," just recorded
  against a `CacheSectionId` instead of a filename in the warning string.

## Reader — `CacheContainerReader` / `CacheSectionAccessor`

`src/DumpDetective.Analysis/Indexing/Container/CacheSectionAccessor.cs`:

- `CacheContainerReader.TryOpen(string dumpPath, out CacheContainerReader? reader)`:
  opens `cache.bin`, validates `Magic`/`FormatVersion`, reads the TOC into memory
  (small — 32 bytes × ~10 sections). Returns `false` on missing file, bad magic, or
  version mismatch (treated as cold cache, same as today's missing/invalid
  `TypeAggregateIndex.bin`).
- `TryOpenSection(CacheSectionId id, out Stream? sectionStream)`: opens a **new**
  read-only `FileStream` handle onto `cache.bin` (`FileShare.Read`, own handle per
  call — matches today's one-handle-per-read-call model, avoids introducing
  shared-stream concurrency between analyzers that may run in parallel per
  `IAnalyzer.IsThreadSafe`), seeks to the TOC entry's `Offset`, and wraps it in a
  small bounded `Stream` (`SubStream`) whose `Length`/`Read`/`Seek` are clamped to
  `[0, TocEntry.Length)` relative to that offset — so existing reader code that does
  `stream.Position = 0` / checks `stream.Length` behaves identically to reading a
  standalone file.
- This is the single call site every reader and analyzer switches to, replacing
  their own `new FileStream(DumpIndexPaths.XxxFile(dumpPath), ...)` construction:
  `ObjectIndexReader.ReadDiskEntries`, `RootIndexReader.ReadRootIndexFile`,
  `DiskHandleSnapshotReader` ctor, `TypeAggregateIndexReader.TryLoadCore` (both the
  main section and the `StringDedupIndex`/meta reads folded into it), and the
  inline reads inside `AsyncTaskAnalyzer`, `WeakReferenceAnalyzer`,
  `LohFragmentationAnalyzer`, `ArrayAnalyzer`.
- `DiskBackedObjectIndexWriter.TryLoadFromCache`/`TryReadObjectCount` become
  "does `cache.bin` exist, open, and contain a non-empty `Objects` + `TypeAggregates`
  section" instead of checking two separate file paths.

### `CacheSectionHelper`

`src/DumpDetective.Analysis/Indexing/Container/CacheSectionHelper.cs`:

- `TryOpenCacheSection(string containerPath, CacheSectionId id, out Stream? stream)`: 
  shared helper that encapsulates the common two-step pattern: `CacheContainerReader.TryOpen` 
  followed by `TryOpenSection`. Returns `false` if the container is missing/invalid or the 
  section doesn't exist. Used by all four analyzers (`AsyncTaskAnalyzer`, 
  `WeakReferenceAnalyzer`, `LohFragmentationAnalyzer`, `ArrayAnalyzer`) to eliminate 
  boilerplate and ensure consistent error handling.

## `DumpIndexPaths.cs`

- Replace the per-file constants/path methods (`ObjectIndexFile`, `RootIndexFile`,
  … `StringDedupIndexMetadataFile`) with a single
  `public const string CacheContainerFile = "cache.bin";` and
  `public static string CacheContainer(string dumpPath)`.
  `GetIndexDirectory`/`ResolveCacheDirectory`/`EnsureDirectory` are untouched — only
  the per-file leaf methods go away.
- `MemoryBackedObjectIndexWriter` doesn't call any of these (in-memory mode writes
  nothing to disk), so it's unaffected.

## Files touched

- New: `src/DumpDetective.Analysis/Indexing/Container/CacheContainerFormat.cs`,
  `CacheContainerWriter.cs`, `CacheContainerReader.cs`, `CacheSectionAccessor.cs`,
  `CacheSectionHelper.cs`
- `src/DumpDetective.Analysis/Indexing/DumpIndexPaths.cs`
- `src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs`
- `src/DumpDetective.Analysis/Indexing/IndexHeader.cs` (`PatchRecordCount` base-offset param)
- `src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs`
- `src/DumpDetective.Analysis/Indexing/TypeAggregateIndexReader.cs`
- `src/DumpDetective.Analysis/Readers/RootIndexReader.cs`
- `src/DumpDetective.Analysis/Indexing/Satellite/RootIndexWriter.cs`, `TaskIndexWriter.cs`,
  `EventCandidateIndexWriter.cs`, `LargeObjectTracker.cs`, `LohFreeBlockWriter.cs`,
  `HandleSnapshotWriter.cs`, `DiskHandleSnapshotReader.cs`, `HandleSnapshotProvider.cs`
- `src/DumpDetective.Analysis/Cache/RootCache.cs`
- `src/DumpDetective.Analysis/Analyzers/AsyncTaskAnalyzer.cs`, `WeakReferenceAnalyzer.cs`,
  `LohFragmentationAnalyzer.cs`, `ArrayAnalyzer.cs` (swap inline `FileStream` opens
  for `CacheSectionHelper.TryOpenCacheSection`; `EventLeakAnalyzer.cs` has none and is 
  untouched by this step)
- `docs/binary-format.md`, `docs/cache/15-ImplementationRoadmap.md` (mark row done)

Not touched: `MemoryBackedObjectIndexWriter.cs`, `HeapIndexCache.cs`'s disk/memory
branch selection, `HeapIndexBuildResult`'s shape, any analyzer's memory-mode path.

## Verification

1. `dotnet build` — compiles clean.
2. Run the existing `*DiscrepancyTests` suite in
   `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/` before making changes
   to capture a baseline (`dotnet test --filter FullyQualifiedName~CacheDiscrepancies`),
   then again after — every test green before must stay green after; this is the
   round-trip regression net doc 14 recommends.
3. Add a focused `CacheContainerRoundTripTests`: build a container against a small
   sample dump, assert every `CacheSectionId` is present with the right
   `RecordCount`, and that re-reading each section byte-for-byte matches what the
   pre-migration per-file writer would have produced.
4. Add an atomic-write test: kill the writer mid-`Build` (or simulate by not calling
   `Finish()`) and assert no `cache.bin` (only a `.tmp` or nothing) is left, and that
   a subsequent run treats it as a clean cache-miss.
5. Manually run the CLI against one real dump in each size tier (small/medium/large)
   with `--cache-dir` pointed at a scratch folder, confirm `.dumpindex/` now contains
   only `cache.bin` (no more nine files + json sidecar), and that a second run against
   the same dump is a cache hit (fast path, no re-scan) with identical analyzer output
   to the first run.
6. Delete the old per-file constants from `DumpIndexPaths` and grep the repo for any
   remaining reference to the old filenames (`ObjectIndex.bin`, etc.) to make sure
   nothing was missed (docs aside).

## Status

**As of 2026-07-15:** Code implementation **100% complete, compiles clean**. Round-trip + atomic-write tests **passing**.

All reader/writer rewiring is done and tested to compile:
- ✅ Container format (`CacheContainerFormat`, enums, TOC/header constants)
- ✅ Writer (`CacheContainerWriter`, `IndexHeader.PatchRecordCount`, satellite writers, orchestration)
- ✅ Reader (`CacheContainerReader`, `CacheSectionAccessor`, `CacheSectionHelper`)
- ✅ `DumpIndexPaths` consolidated to single-file paths
- ✅ All reader call sites rewired: `ObjectIndexReader`, `RootIndexReader`, `TypeAggregateIndexReader`, `DiskHandleSnapshotReader`
- ✅ All analyzer inline reads rewired: `AsyncTaskAnalyzer`, `WeakReferenceAnalyzer`, `LohFragmentationAnalyzer`, `ArrayAnalyzer`
- ✅ High-level caches (`RootCache`, `HandleSnapshotProvider`) rewired
- ✅ `dotnet build` clean: 0 errors, 0 warnings
- ✅ Round-trip tests: 5 passing (sections write/read, record counts match, corrupted containers rejected, multiple reads work)
- ✅ Atomic-write tests: 7 passing (incomplete writes clean up, exceptions trigger cleanup, concurrent writes safe)

**Remaining:** discrepancy test baselines, manual smoke test, docs sync
(see [**⚠️ Remaining work**](#-remaining-work-blocking-tier-2-sign-off) section below).

Update the checklist below as test/doc work lands; keep [15](15-ImplementationRoadmap.md)'s
row in sync.

| Step | Status |
|---|---|
| `CacheContainerFormat.cs` (header/TOC constants, `CacheSectionId`) | Done |
| `CacheContainerWriter.cs` | Done |
| `CacheContainerReader.cs` / `CacheSectionAccessor.cs` | Done |
| `IndexHeader.PatchRecordCount` base-offset param | Done |
| `DumpIndexPaths.cs` single-file paths | Done |
| Satellite writers rewired to take `Stream` | Done |
| `TypeAggregateIndexWriter`/`TypeAggregateIndexReader` rewired (incl. folded-in `StringDedup`/`StringDedupMeta`) | Done |
| `DiskBackedObjectIndexWriter.Build` / `WriteSatelliteSections` orchestration | Done |
| `ObjectIndexReader.ReadDiskEntries` rewired to `CacheSectionAccessor` | Done — opens `Objects` section via `CacheContainerReader`/`CacheSectionStream` |
| `RootIndexReader.ReadRootIndexFile` rewired to `CacheSectionAccessor` | Done — opens `Roots` section via `CacheContainerReader`/`CacheSectionStream` |
| `DiskHandleSnapshotReader` rewired to `CacheSectionAccessor` | Done — opens `Handles` section via `CacheContainerReader`/`CacheSectionStream`; `HandleSnapshotProvider.CreateFromDiskIfExists` + `WeakReferenceAnalyzer` rewired to use container path |
| Analyzer inline reads rewired (`AsyncTaskAnalyzer`, `WeakReferenceAnalyzer`, `LohFragmentationAnalyzer`, `ArrayAnalyzer`) | Done — all four analyzers' fast-path reads now via shared helper `CacheSectionHelper.TryOpenCacheSection`; `WeakReferenceAnalyzer` dead methods removed |
| `RootCache.cs` / `HandleSnapshotProvider.cs` rewired | Done — `RootCache.GetOrBuildValidRoots` calls `RootIndexReader.ReadRootTargets`; `HandleSnapshotProvider.CreateFromDiskIfExists` checks container via `CacheContainerReader.TryOpen` + `ContainsSection`; both flow through container path |
| Full `dotnet build` clean | Done — 0 errors, 0 warnings |
| Round-trip + atomic-write tests | Done — 12 tests passing (`CacheContainerRoundTripTests`: 5 tests; `CacheContainerAtomicWriteTests`: 7 tests covering crash simulation, cleanup, concurrent writes) |
| `*DiscrepancyTests` baseline/post-migration run | Not started |
| Docs updated (`binary-format.md`, roadmap row) | Not started |

## ⚠️ Remaining work (blocking Tier 2 sign-off)

All code implementation is **complete and building clean**. The following must complete
before this row can move from "Done (code)" to fully "Done" in the roadmap:

### 1. Round-trip + atomic-write tests (required)

**File:** `tests/DumpDetective.Tests/Cache/` (create if needed)

- **`CacheContainerRoundTripTests`:** write a small test dump to cache.bin, re-read each
  section, confirm `RecordCount` and byte-for-byte contents match pre-migration per-file
  output. Must cover all 10 `CacheSectionId` values.
- **Atomic-write test:** simulate writer crash (don't call `Finish()`) and verify no
  `cache.bin` is left (only `.tmp` or nothing); next run must treat cache as miss.

**Acceptance:** both tests pass consistently.

### 2. Integration baseline + discrepancy tests (required)

**Command:** 
```bash
dotnet test --filter FullyQualifiedName~CacheDiscrepancies
```

- Run baseline on current branch **before** any verification changes — capture pre-migration
  disk/memory agreement on `*DiscrepancyTests`.
- Run again post-implementation; every test that passed before **must** still pass.
- Any new failure isolates a container-format bug, not a pre-existing one.

**Acceptance:** all `*DiscrepancyTests` green pre- and post-implementation.

### 3. Manual CLI smoke test (required)

Run the tool on one real dump per tier (small/medium/large) with a fresh `--cache-dir`:

```bash
dumpdetective analyze --dump <small-dump> --cache-dir C:\scratch\cache-test --json report-small.json
dumpdetective analyze --dump <small-dump> --cache-dir C:\scratch\cache-test --json report-small-2.json
# Compare report-small.json vs report-small-2.json — identical?
# Check C:\scratch\cache-test\<dump-name>.dumpindex\ — contains only cache.bin?
```

**Acceptance:**
- `.dumpindex/` contains **only** `cache.bin` (no nine per-file artifacts, no json sidecar).
- Second run is fast (cache hit, no re-scan).
- Analyzer output identical to first run (report-small.json ≈ report-small-2.json).

### 4. Documentation sync (required)

- [ ] Update `docs/binary-format.md` to document single-file layout (today still describes
      nine independent files).
- [ ] Mark container-format row in `docs/cache/15-ImplementationRoadmap.md` as "Done".
- [ ] `grep` repo for lingering per-file references (`ObjectIndex.bin`, `RootIndex.bin`, etc.)
      in code comments and docs; remove all.

**Entry point:** [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md) Tier 2 table —
update status cell once all four items above pass.

### Next steps (in order)

1. **Write round-trip + atomic-write tests**
   - `CacheContainerRoundTripTests`: per-section write→read parity test confirming
     `RecordCount` and byte-for-byte section contents match what the pre-migration
     per-file writer produced.
   - Atomic-write test: kill writer mid-`Build` (or simulate not calling `Finish()`)
     and assert no `cache.bin` is left (only `.tmp` or nothing), so a subsequent
     run treats it as a clean cache miss.

2. **Run integration tests**
   - Baseline all `*DiscrepancyTests` in `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/`
     (`--filter FullyQualifiedName~CacheDiscrepancies`) to confirm disk-mode/memory-mode
     analyzer output agreement pre- and post-rewire.
   - Manual CLI smoke test: run against one real dump in each size tier (small/medium/large)
     with `--cache-dir` pointed at a scratch folder. Confirm `.dumpindex/` contains only
     `cache.bin` (nine old files + json sidecar gone), second run is fast (cache hit), and
     analyzer output identical to first run.

3. **Documentation**
   - Update `docs/binary-format.md` to reflect single-file layout.
   - Mark the container-format row in `docs/cache/15-ImplementationRoadmap.md` done.
   - `grep` repo for lingering per-file constant references (`ObjectIndex.bin`, etc.) in
     docs and remove all.
