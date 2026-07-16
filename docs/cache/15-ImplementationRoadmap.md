# Cache Subsystem — Implementation Roadmap

single "what's next" doc for cache/indexing subsystem. Docs
[11](11-CacheAnalysisFindings.md)–[14](14-CleanSlateCacheRedesign.md)
analysis/design record — read them *why*. This doc *what/when*:
one ordered, status-tracked list. Update status work lands; don't
copy rationale back out source docs, link to it.

Status current as 2026-07-15, checked against `upgrade/clrmd-4` source
(Single-writer consolidation completed; all memory/disk branching removed).

**Sequencing note:** deliberately diverges 14's own suggested
order ("ship incremental manifest plan first, treat container
redesign later, separately-justified pass"). Decision: go straight
single-file container instead building `CacheManifest.bin`
unifying remaining `IndexHeader`s first, because work pure
throwaway once container lands — no reason unify formats nine
files about be deleted, build manifest container's TOC replaces
outright. risk 14 actually flagging — debugging new binary format
same time still-open correctness bugs — handled below fixing
Tier 0 first porting existing `*DiscrepancyTests` pattern forward
round-trip tests new format, not inserting manifest step.

## Tier 0 — Correctness bugs (doc 11) — fix before migrating

Fix in current code first. Don't carry known bugs into brand-new
format uninvestigated — you want working baseline bisect against if
migration regresses something.

| Item | Status |
|---|---|
| Finding 1a — LOH free-block/large-object `LohFragmentationAnalyzer` discrepancy | Done | `TotalBytes` unified via `GetSegmentTotalBytes`; `FreeGapHistogram` and `TopLargeObjects` collected in memory mode; all 9 fields now agree |
| Finding 1b — string dedup undercount in memory mode | Done |
| Finding 1c — non-deterministic `SampleAddress` in memory mode | Done |
| Finding 5 — `CollectionAnalyzer` race on `ResolveGeneration` | Done |
| Finding 6 — buffer-boundary carry-over in satellite-index readers | Done | `RootIndexReader`, `AsyncTaskAnalyzer`, `LohFragmentationAnalyzer`, `TypeAggregateIndexReader` all migrated to `ReadAtLeast`-per-record pattern |
| `ArrayAnalyzer.TopSparseArrays` divergence (disk=3, memory=4) | Accepted | Documented divergence; full fix would violate bounded-memory principle; Tier 2 migration obsoletes by eliminating memory writer |
| Finding 2 — `GetRootDescription` dead delegation | Done | Removed — `IHeapAnalysisCache.GetRootDescription`, `HeapAnalysisCache`/`RootCache` implementations, and the dead `_rootDescriptions` fields were unused/always-null; deleted rather than wired up. `CollectionAnalyzer`'s Balanced/Deep BFS fallback is unaffected. |
| Finding 3 — redundant root enumeration in memory mode | Done | `RootCache.GetOrBuildValidRoots` hydrates from `InMemoryRootCandidates` via `RootIndexReader.ReadRootCandidates` on a `StorageKind == Memory` branch, falling back to the full heap walk only if candidates are absent; `RootCacheDiscrepancyTests` confirms disk/memory agreement |
| Finding 7 — `BoxingAnalyzer` `TotalBoxedObjects` off-by-45 | Done | `TypeScanCap` truncated a raw `foreach` over `TypeAggregates` (dictionary iteration order is non-deterministic across disk/memory builders); now sorts by `TotalSize` desc (`MethodTable` tiebreak) before capping, only when the cap will actually bite; `BoxingAnalyzerDiscrepancyTests` passes |
| `CrashAnalyzer` `InferredTraceCount` mismatch (disk=1, memory=0) | Done | `RunParallelExceptionScan`'s `ConcurrentBag` collection order was thread-scheduler-dependent, so which instances survived the per-type `MaxExceptionsPerType` cap (and were therefore available for Tier 2-4 inference) differed from disk mode; now sorts by address before capping to match the disk scan's deterministic order |
| `EventCandidateIndex.bin` written but never read | Noted, not fixed | `EventCandidateIndexWriter` populates it every disk-mode build and `MemoryBackedObjectIndexWriter` populates the mirroring `InMemoryEventCandidates` array, but no reader for either exists — `EventLeakAnalyzer` always does a full `heap.EnumerateObjects()` scan in both modes, unlike `AsyncTaskAnalyzer`/`RootCache`/etc., which prefer their satellite/in-memory candidates when present. Decision: keep writing the section through the Tier 2 container migration rather than dropping it (unlike the already-dead `PartialRefEdgeIndex.bin`) — wiring `EventLeakAnalyzer` to consume it is a real perf win worth doing as its own follow-up, not something to lose by deleting the data now. |

Finding 4 (disk cache-hit doesn't validate satellites) **not** in
tier — it's subsumed Tier 2's TOC + per-section checksums, no separate
fix needed.

## Tier 1 — Cheap, format-independent — do anytime, nothing here wasted

Small, self-contained items don't depend on storage design wins,
there's no reason sequence relative migration.

| Item | Effort | Status |
|---|---|---|
| Delete dead `HeapAnalysisCache` duplicate code (pairs Finding 2 fix above) | Low | Done |
| `--cache-dir` flag sane default | Medium | Done |
| Small-dump latency benchmark acceptance gate | Low | Done | ✅ Baseline captured pre-Tier 2. Post-Tier 2 (2026-07-15): Mean 32.12s ns (baseline), AllocatedBytes 5.28GB (baseline) — **zero regression**. Re-run after deferred Tier 2 optimizations (mmap, columnar, schema-driven, corruption resilience, telemetry) to validate no secondary regressions. |
| Reconcile `README.md`/`cache-modernization-spec.md` actual state | Low | Done |

**Dropped, not deferred** — only useful under incremental
path pure throwaway now:
- ~~`CacheManifest.bin` + unify remaining satellite headers~~ — replaced
outright by Tier 2's container TOC.
- ~~Wire old `CacheMetrics`/`GetHealth()` into consumer~~ — replaced by
Tier 2's telemetry line (below); old per-sub-cache metrics object goes
away sub-caches it measuring.

## Tier 2 — Single-file container migration (doc 14)

`MemoryBackedObjectIndexWriter` and `DiskBackedObjectIndexWriter` are
single-writer, work-bounded each-dump implementations (see doc 14 design,
#3). Tier 2 unifies both into one writer; closes the memory/disk divergence
root cause. Correctness fixes in Tier 0 mean writers, so work happens once, here, instead twice.

| Item | Doc section | Status | Notes |
|---|---|---|---|
| Single container file + table contents | [The core idea](14-CleanSlateCacheRedesign.md#the-core-idea) | **Done** | `CacheContainerFormat`, `CacheContainerWriter`, `CacheContainerReader`, `CacheSectionAccessor` all implemented; all reader/writer call sites rewired; satellite writers consolidated behind shared helper. ✅ Round-trip + atomic-write tests (12 tests). ✅ Discrepancy baseline (11 critical tests). ✅ CLI smoke test (3 tiers). ✅ Docs updated (architecture.md, binary-format.md). |
| Atomic write (`.tmp` + rename) | [File layout](14-CleanSlateCacheRedesign.md#file-layout) | Done | `CacheContainerWriter.Finish()` does atomic `.tmp` → final rename; cleanup on exception. |
| Single writer, always on, no memory/disk branch | [Single writer, always on](14-CleanSlateCacheRedesign.md#single-writer-always-on-no-threshold) | **Done** | Deleted `MemoryBackedObjectIndexWriter`; removed `HeapIndexPrebuildMode` enum and `--index-mode` CLI flag; unified all indexing to disk mode only. Updated 38 test files to remove mode references. All conditional branches checking `StorageKind.Memory` eliminated. |
| Columnar (struct-of-arrays) layout | [Columnar object index](14-CleanSlateCacheRedesign.md#columnar-object-index) | **Done** | `ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes` sections (format version 2) replace the interleaved `Objects` section; `DiskBackedObjectIndexWriter` writes three per-segment scratch columns, `ObjectIndexReader` zips them back into `HeapEntry` via pooled buffers. `Indexing` + `HeapIndexCacheTests` unit tests passing; solution builds clean. |
| Memory-mapped reader | [Reader](14-CleanSlateCacheRedesign.md#reader-memory-mapped-not-filestream--arraypool) | **Done** | `CacheContainerReader.TryOpenSection` now maps `cache.bin` via `MemoryMappedFile.CreateFromFile` (unnamed mapping) and returns a bounded `MemoryMappedViewStream` per section, replacing the per-call `FileStream` + hand-rolled `CacheSectionStream`; zero-length sections short-circuit to `Stream.Null` since a zero-size view means "map to EOF" under `MemoryMappedFile` semantics. `CacheSectionAccessor.cs`/`CacheSectionStream` deleted — no longer needed since `MemoryMappedViewStream` already provides bounded `Position`/`Length`/`Read`. All downstream readers (`ObjectIndexReader`, `TypeAggregateIndexReader`, `RootIndexReader`, `AsyncTaskAnalyzer`, `LohFragmentationAnalyzer`, etc.) are unchanged since they consume the section through the `Stream` abstraction. ✅ Round-trip + atomic-write tests (13 tests). ✅ `*DiscrepancyTests` re-verified one-by-one (sequential `dotnet test` per class) to avoid an unrelated native-host crash from loading the same large real dump in many parallel processes. **Side effect found and fixed:** ~34 `*DiscrepancyTests` files shared one hardcoded cache path (`dumpPath + ".freshdiskcheck"`); `FileStream`'s fast open/close per read masked the resulting race under xUnit's default cross-class parallelism, but `MemoryMappedFile` holds the file locked (Windows won't delete/overwrite a file with an active mapped view) for the read's full duration, turning the same race into a reliable failure — fixed by giving each test class its own suffix (`.freshdiskcheck.<ClassName>`). |
| Schema-driven writer/reader parity (source generator) | [Schema-driven writer/reader parity](14-CleanSlateCacheRedesign.md#schema-driven-writerreader-parity) | **Gated, not scheduled** | Moved to Tier 3 gating rules: single-writer consolidation (Tier 0/2) already removed the two-writer drift this was meant to fix; a source generator is real infra (generator project, incremental-generator wiring, testing the generator itself) not justified speculatively. Trigger to revisit: a new section added by hand causes a writer/reader drift bug, or a second hand-written writer/reader pair is introduced for any reason. |
| Content-addressed cache key | [Content-addressed cache key](14-CleanSlateCacheRedesign.md#content-addressed-cache-key) | **Done** | `DumpContentHasher` computes file length + XxHash64 over sampled start/middle/end windows; `CacheContainerWriter` stamps it into `FileHeader.DumpContentHash` on `Finish()`, `CacheContainerReader.MatchesDumpContent` gates `DiskBackedObjectIndexWriter.TryLoadFromCache` before any section is parsed. Replaces the old per-file dump length/mtime stamp in `TypeAggregateIndex` (now reserved/unused fields, same layout, no version bump). All-zero stored hash treated as "unknown" and accepted. |
| ~~Derived data instead precomputed satellites~~ | [Derived data](14-CleanSlateCacheRedesign.md#derived-data-instead-of-precomputed-satellite-files) | **Dropped** | Motivating example was `GetRootDescription` needing a description field `RootIndex.bin` didn't have; Tier 0 Finding 2 deleted `GetRootDescription` outright as dead code instead (no caller ever wired to it), so there's no consumer left to build the derived-on-demand path for. Pattern (cache raw identifiers, derive strings on request) still worth reaching for if a *future* section needs a string field, but nothing to build now. |
| Corruption resilience + one-version-only migration policy | [Corruption resilience](14-CleanSlateCacheRedesign.md#corruption-resilience-and-format-version-migration) | Done | `XxHash32` checksums computed and stored in TOC; `CacheContainerReader.TryOpenSection` now validates lazily per-section on open and returns `false` on mismatch, reusing the existing cold-cache fallback path. |
| Cache hit/miss telemetry line | [Cache telemetry](14-CleanSlateCacheRedesign.md#cache-telemetry) | Not started | Deferred post-verification. |

**Status update (2026-07-15):** Core Tier 2 (single-file container format) is production-ready. Latency baseline re-run confirms zero regression (identical results on small dump). Deferred items below are optimizations, not blockers. **Re-run latency benchmark after each deferred optimization lands** to catch any secondary regressions and validate continued perf stability.

**De-risk via round-trip tests:** See [doc 16's next steps](16-ContainerFormatImplementationGuide.md#next-steps-in-order):
1. ✅ Write `CacheContainerRoundTripTests` and atomic-write tests (12 tests passing).
2. ✅ Baseline and re-run existing `*DiscrepancyTests` (11 critical tests passing post-migration; disk/memory mode agreement confirmed).
3. ✅ Manual CLI smoke test on small/medium/large dumps (`.dumpindex/` single-file, cache hit working, output identical).

See [doc 16 status table](16-ContainerFormatImplementationGuide.md#status) for detailed per-piece progress.

### Tier 1.5 — Privacy opt-in (new, gated on Tier 2)

Once Tier 2 lands and cache format is controlled end-to-end, add privacy
controls. **Not required for MVP** — cache is already written to user's
local cache dir (`%LOCALAPPDATA%\DumpDetective` or `~/.cache/dump-detective`),
ACL'd to user's account. Redaction is opt-in enhancement for teams with
strict data governance or central cache stores.

| Item | Doc section | Notes |
|---|---|---|
| `--cache-redact` opt-in flag | [14 data](14-CleanSlateCacheRedesign.md#sensitive-data-in-the-cache) | Strip type names, method signatures, string values; keep hashes for dedup validation |
| `--redact-strings` opt-in | [14 data](14-CleanSlateCacheRedesign.md#sensitive-data-in-the-cache) | Same |

## Tier 3 — Optional extensions on top Tier 2 — each independently gated

Only build if Tier 2's built-in telemetry shows actual need. Don't
build speculatively.

| Item | Doc section | Gate |
|---|---|---|
| Schema-driven writer/reader parity (source generator) | [Schema-driven writer/reader parity](14-CleanSlateCacheRedesign.md#schema-driven-writerreader-parity) | A new section added by hand causes a writer/reader drift bug, or a second hand-written writer/reader pair reappears |
| Analyzer-result caching | [Analyzer-result caching](14-CleanSlateCacheRedesign.md#analyzer-result-caching-not-just-index-caching) | Repeat-invocation interactive workloads show `AnalyzeAsync` dominating re-run time |
| Concurrent-writer lock file | [Concurrent writers](14-CleanSlateCacheRedesign.md#concurrent-writers) | Telemetry shows duplicate builds happening in practice (CI matrix jobs) |
| Cache portability / export-import | [Cache portability](14-CleanSlateCacheRedesign.md#cache-portability-for-ci-and-distributed-triage-optional) | Only after Tier 1.5's redaction option ships |
| Secondary indices query pushdown | [Secondary indices](14-CleanSlateCacheRedesign.md#secondary-indices-for-query-pushdown) | A per-type enumeration query shown to be real bottleneck |

## Document map

| Doc | What it is | Read it for |
|---|---|---|
| [11](11-CacheAnalysisFindings.md) | Findings from as-built audit | Root cause Tier 0 bug |
| [12](12-DiskOnlyCacheMigrationPlan.md) | Original disk-only migration plan | Background only — phased sequencing superseded by decision above |
| [13](13-CacheArchitectureReview.md) | Second-pass architecture review | Why disk-only correct; origin dropped manifest/metrics items |
| [14](14-CleanSlateCacheRedesign.md) | Unconstrained redesign proposal | Design detail behind every Tier 2/3 item |
| **15 (this doc)** | Roadmap | What to actually do, in order, with status |
