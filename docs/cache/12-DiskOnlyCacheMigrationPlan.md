# Disk-Only Cache Migration Plan (2026-07-11)

Follow-up to [11-CacheAnalysisFindings.md](11-CacheAnalysisFindings.md). Analysis/design
only — no code changes made in this pass.

## Motivation

Doc 11 established two problems in the current dual-backend cache
(`MemoryBackedObjectIndexWriter` for dumps <4GB, `DiskBackedObjectIndexWriter` for dumps
≥4GB, selected by `HeapIndexCache.SelectPrebuildMode`):

- **Finding 1**: memory and disk indexing produce non-equivalent output for an identical
  dump — string dedup sampling differs, and large-object/LOH-free-block data only exists
  on disk, so `ArrayAnalyzer` falls back to a materially weaker heuristic in memory mode.
  This is the direct cause of the reported HTML report divergence.
- **Finding 4**: the disk cache-hit fast path only validates `ObjectIndex.bin` and
  `TypeAggregateIndex.bin`. Satellite files can silently fail to write (caught,
  logged as a non-fatal warning) yet the run is still treated as a complete, valid cache
  on the next hit — with no repair mechanism.

Doc 11's suggested fix for Finding 1 was to bring memory mode up to parity with disk mode
(add missing candidate collection, symmetric string sampling). Discussion in this session
reached a different, cheaper conclusion: **disk mode already has full feature parity with
memory mode, and none of memory mode's remaining advantages (avoiding disk I/O for small
dumps) are large relative to the ClrMD heap-walk itself.** Rather than fixing memory mode
to match disk mode, removing memory mode entirely is simpler, eliminates Finding 1 at the
root instead of patching each analyzer's dual-path branch, and creates a single writer
output whose completeness a manifest (fixing Finding 4) can meaningfully validate.

**In scope**: collapsing to a single disk-backed indexing path, and a minimal versioned
manifest so cache-completeness is no longer inferred from one file's existence.

**Explicitly out of scope** (deferred — see [Deferred work](#deferred--future-work)):
configurable cache directory, cache eviction/cleanup, standalone per-satellite-file
repair writers. `--index-mode memory` is removed outright, not deprecated.

## Current-state inventory (blast radius)

Every `HeapIndexStorageKind`/memory-path branch that must collapse to a single disk-only
path:

**Mode selection**
- [HeapIndexingMode.cs](../../src/DumpDetective.Analysis/Indexing/HeapIndexingMode.cs) —
  defines `HeapIndexPrebuildMode{Auto,Memory,Disk}` and `HeapIndexStorageKind{Memory,Disk}`.
- [HeapIndexCache.cs](../../src/DumpDetective.Analysis/Cache/HeapIndexCache.cs) —
  `SelectPrebuildMode` (live, 4GB threshold), writer construction, `EnumerateIndexedEntries`
  branch (`InMemoryEntries` vs `ObjectIndexReader.ReadEntries`).
- `HeapAnalysisCache.cs` carries a second, **dead** `SelectPrebuildMode` (never called —
  `PrebuildHeapIndex` delegates straight to `HeapIndexCache`'s copy) plus a named
  threshold const with a stale `// TEMP-ADAPTIVE-INDEXING` comment — delete both.
- **Not in scope**: `HeapIndexCache._sizeTier` (`DumpSizeTier` Small/Medium/Large) tunes
  scan *parallelism*, not the memory/disk choice — leave unchanged.

**Analyzer dual-paths** (each has a parallel in-memory-array-scan vs disk-I/O branch, or
an `InMemory*` presence check with no disk equivalent):
[RootCache.cs](../../src/DumpDetective.Analysis/Cache/RootCache.cs) (`GetOrBuildValidRoots`
— also doc-11 Finding 3),
[RootIndexReader.cs](../../src/DumpDetective.Analysis/Readers/RootIndexReader.cs),
[HangAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/HangAnalyzer.cs),
[CollectionAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs),
[CrashAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/CrashAnalyzer.cs),
[ArrayAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs) (the
Finding-1 divergence site),
[WeakReferenceAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/WeakReferenceAnalyzer.cs),
[EventLeakAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs),
[AsyncTaskAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/AsyncTaskAnalyzer.cs).
`DependentHandleAnalyzer.cs`/`GCHandleAnalyzer.cs` have unwired TODO comments referencing
`InMemoryHandleSnapshot` that were never implemented — delete the comments along with the
field.

**Data carrier**
- [HeapIndexBuildResult.cs](../../src/DumpDetective.Analysis/Indexing/HeapIndexBuildResult.cs) —
  remove `StorageKind` and the 5 `InMemory*` fields (`InMemoryEntries`,
  `InMemoryTaskCandidates`, `InMemoryEventCandidates`, `InMemoryRootCandidates`,
  `InMemoryHandleSnapshot`).

**Writer to delete**
- [MemoryBackedObjectIndexWriter.cs](../../src/DumpDetective.Analysis/Indexing/MemoryBackedObjectIndexWriter.cs) —
  sole production construction site is `HeapIndexCache.cs`; also referenced by
  `src/BenchmarkSuite1/HeapIndexBuildBenchmark.cs`. No DI registration exists for any of
  `HeapAnalysisCache`/`IHeapAnalysisCache`/`HeapIndexCache` (always constructed via plain
  `new()` in `BuildHeapIndexStage.cs` and `PerDumpExecutionService.cs`) — no service
  registration cleanup needed.

**CLI/config plumbing for `--index-mode`** (removed, not just defaulted): `RootCommandBuilder.cs`
(option + `ParseHeapIndexMode`), `ConfigurationParseHelpers.cs` (duplicate parse helper),
`CliConfigurationModels.cs` (two overlapping keys `Indexing.Mode` /
`ExecutionPolicy.IndexPrebuildMode`), `ConfigurationResolver.cs`, `ResolvedExecutionOptions.cs`,
`AnalysisCommandRequest.cs`, `AnalyzerOptionsBuilder.cs`, `IncidentContextFactory.cs`,
`config.sample.json`, `docs/architecture.md`. `AnalyzerBenchmarkBase.cs` and
`FullPipelineBenchmark.cs` already hardcode `mode: Auto` — update to whatever the
post-removal default value/type becomes.

**Tests**: `tests/DumpDetective.Tests/Unit/Analysis/AllocationPatternAnalyzerTests.cs` has
5 call sites hand-building `HeapIndexBuildResult(HeapIndexStorageKind.Memory, ...)` and
injecting via reflection into `HeapAnalysisCache`'s private `_heapIndex` field — update to
the new constructor signature (confirm each of the 5 only reads
`TypeAggregates`/`ObjectCount`/`Elapsed`/`IndexPath` before treating as a blanket
mechanical change). `StartupValidatorTests.cs` and `Helpers/ResolvedExecutionOptionsFactory.cs`
just default `IndexPrebuildMode: Auto` — update the type/default, no behavioral change.

**Orphaned code, ignore**: `src/DumpDetective.Cli/Pipeline/Stages/ExecutePerDumpPipelineStage.cs`
is never instantiated anywhere in the repo — its `StorageKind` log-read there is dead
either way, no action needed.

## Versioned on-disk format

8 of 10 satellite index files already use the shared
[IndexHeader.cs](../../src/DumpDetective.Analysis/Indexing/IndexHeader.cs) struct
(Magic(4)+Version(4)+RecordCount(8)+Reserved(8)) with per-file `(Magic, Version)` pairs
validated on read (e.g. `TypeAggregateIndex.bin` via
[TypeAggregateIndexReader.cs](../../src/DumpDetective.Analysis/Readers/TypeAggregateIndexReader.cs)).
Two files are non-conforming and need migration:

- **`ObjectIndex.bin`** ([DiskBackedObjectIndexWriter.cs](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs))
  — custom 24-byte header (Magic+Version+Ticks+RecordCount). Its Version field is
  currently write-only: `TryReadObjectCount` only checks Magic, never Version — a live
  dead-code bug, fixed as a side effect of moving to `IndexHeader`.
- **`StringDedupIndex.bin`** — separate hand-rolled 12-byte header (Magic+Version, no
  RecordCount/Reserved). A Version mismatch today only nulls the dedup fields rather than
  invalidating the whole cache hit — decoupled from the main gate, silently degrading
  `StringAnalyzer` output. Migrating onto `IndexHeader` and making a mismatch
  manifest-aware (see below) fixes this.

> **Caveat that must be respected during implementation**: both migrations must use a
> **new, previously-unused Version number** for their file's magic (not reuse `1`).
> Old-format files have different bytes at the same header offsets than the new
> `IndexHeader` layout expects — reusing `1` would let a stale pre-migration file pass
> validation and then be misparsed downstream.

### New: `CacheManifest.bin`

Closes Finding 4. Written last, after `TypeAggregateIndex.bin`, so its mere presence
signals "the previous run completed writing everything it intended to write." Proposed
layout:

```
CacheManifest.bin
  IndexHeader (Magic="CMAN", Version=1, RecordCount=fileCount)
  DumpFileLength (8) | DumpLastWriteUtcTicks (8)   -- promoted from TypeAggregateIndex.bin's ExtraHeader
  per-file records (fileCount x 12 bytes):
      FileNameId (2, index into a fixed enum of known satellite files)
      ExpectedMagic (4)
      ExpectedVersion (4)
      IsRequired (1)   -- ObjectIndex.bin/TypeAggregateIndex.bin = required; satellites = optional-but-tracked
      Pad (1)
```

`FileNameId`'s enum mapping is a permanent wire contract once real caches exist in the
field — assign it deliberately once, don't renumber later without a manifest version bump.

**Repair behavior**: manifest absence is the primary staleness signal for pre-migration
`.dumpindex/` folders (forces a full rebuild — no explicit migration script needed, the
version bump *is* the migration). For a missing or version-mismatched satellite file
against an otherwise-valid manifest, this plan uses **full-rescan fallback** — rebuild the
whole index rather than repairing that one file — matching today's de-facto behavior and
keeping the diff small. Standalone per-file repair is deferred (see below).

Version-bump policy: bump each file's own `(Magic, Version)` independently when its byte
layout changes. No single global "cache format epoch" — that would force a full
invalidation on every unrelated satellite tweak.

## Sequencing

Recommended as 3 separate PRs, in this order:

1. **Phase 0 — cleanup** (independent, low-risk, ship first): fix
   `HeapAnalysisCache.GetRootDescription`'s missing delegation to `_rootCache` (doc-11
   Finding 2), delete the dead duplicate `TryHydrateTypeStatisticsFromIndex`/
   `ResolveTypeNameFromSample`/`ResolveModuleNameFromSample`/`AddClamped` in
   `HeapAnalysisCache.cs`, delete the dead `SelectPrebuildMode` copy there too.
2. **Phase 1 — writer/reader consolidation** (the correctness fix; must precede Phase 2
   because the manifest needs a single writer's well-defined output-file-set): delete
   `MemoryBackedObjectIndexWriter.cs`, collapse mode selection to disk-only, collapse
   every analyzer dual-path branch listed above, remove `StorageKind`/`InMemory*` fields
   from `HeapIndexBuildResult`, remove `--index-mode memory` from CLI/config end to end,
   update tests/benchmarks. Independently shippable and testable — closes Finding 1
   immediately, before any format work.
3. **Phase 2 — format unification**: migrate `ObjectIndex.bin`/`StringDedupIndex.bin`
   onto `IndexHeader` with new version numbers, introduce `CacheManifest.bin`
   writer/reader, wire the cache-hit fast path (`TryLoadFromCache`) to require and
   validate the manifest before trusting a cache hit.
4. **Phase 3 — hardening tests**: land the new test categories below. Round-trip tests
   for the *existing* mechanism (`IndexHeader`, dump-identity staleness) can land right
   after Phase 1; manifest-specific tests follow Phase 2.

## Testing plan

New test categories: `IndexHeader` round-trip; `ObjectIndex.bin` round-trip plus a
Version-rejection regression test (guards the caveat above — an old-format file with the
old Version must be rejected, not misparsed); `StringDedupIndex.bin` round-trip;
`CacheManifest.bin` round-trip including required-missing vs optional-missing behavior;
dump-identity staleness positive/negative; a full writer-to-reader integration round-trip
against a real/fixture dump.

Existing-test updates: mechanical signature updates to `AllocationPatternAnalyzerTests.cs`'s
5 call sites (verify each against the analyzer's actual field usage rather than assuming
blanket safety); a repo-wide grep sweep for `HeapIndexStorageKind` / `InMemoryEntries` /
`MemoryBackedObjectIndexWriter` immediately before implementation starts, as a
completeness check against anything outside this inventory (e.g. report-generation or
JSON-serialization code that might reflect over `HeapIndexBuildResult`'s fields).

## Deferred / future work

Not designed in this pass — call out explicitly rather than address now:

- **Configurable cache directory.** Implemented as the `--cache-dir` CLI flag and
  `CacheDirectory` config-file setting, with a 4-tier fallback chain:
  explicit `--cache-dir` → colocated `{dumpPath}.dumpindex/` → temp folder
  `%TEMP%/dumpdetective-cache/<hash-of-dump-path>/` → throw error asking for
  `--cache-dir`. See [architecture.md](../../docs/architecture.md#cache-directory-resolution)
  for full details.
- **Cache eviction/cleanup.** No mechanism exists today (confirmed — no `Directory.Delete`/
  TTL/LRU/max-age logic anywhere); `.dumpindex/` folders accumulate indefinitely, manual
  deletion is the only invalidation path. Becomes more relevant once every dump gets a
  cache folder, not just large ones.
- **Standalone per-satellite-file repair writers.** Would let a single missing/stale file
  (e.g. just `TaskIndex.bin`) be repaired without a full heap rescan. Higher-value once
  the dump corpus is large/rotating; not worth the added surface area for this migration.

## Files to read before implementing

`HeapIndexingMode.cs`, `HeapIndexCache.cs`, `HeapAnalysisCache.cs`, `HeapIndexBuildResult.cs`,
`MemoryBackedObjectIndexWriter.cs`, `DiskBackedObjectIndexWriter.cs`, `IndexHeader.cs`,
`TypeAggregateIndexWriter.cs`, `TypeAggregateIndexReader.cs`, `RootCache.cs`,
`DumpIndexPaths.cs`.
