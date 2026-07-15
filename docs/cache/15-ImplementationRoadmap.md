# Cache Subsystem — Implementation Roadmap

single "what's next" doc for cache/indexing subsystem. Docs
[11](11-CacheAnalysisFindings.md)–[14](14-CleanSlateCacheRedesign.md)
analysis/design record — read them *why*. This doc *what/when*:
one ordered, status-tracked list. Update status work lands; don't
copy rationale back out source docs, link to it.

Status current as 2026-07-13, checked against `upgrade/clrmd-4` source
(`MemoryBackedObjectIndexWriter`, `HeapIndexingMode`, `--index-mode`
all still present — nothing below started).

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
| Small-dump latency benchmark acceptance gate | Low | Baseline captured (pre-Tier 2) — `SmallDumpLatencyBenchmark.RunEndToEnd` MeanNs/AllocatedBytes in `perf-baselines.json`, gated via `compare-benchmarks.ps1`. Re-run once Tier 2 lands to confirm no regression. |
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
| Single container file + table contents | [The core idea](14-CleanSlateCacheRedesign.md#the-core-idea) | **Done (code + tests)** | `CacheContainerFormat`, `CacheContainerWriter`, `CacheContainerReader`, `CacheSectionAccessor` all implemented; all reader/writer call sites rewired; satellite writers consolidated behind shared helper. ✅ Round-trip + atomic-write tests passing (12 tests). Pending: discrepancy test baseline and manual smoke test in [doc 16](16-ContainerFormatImplementationGuide.md). |
| Atomic write (`.tmp` + rename) | [File layout](14-CleanSlateCacheRedesign.md#file-layout) | Done | `CacheContainerWriter.Finish()` does atomic `.tmp` → final rename; cleanup on exception. |
| Single writer, always on, no memory/disk branch | [Single writer, always on](14-CleanSlateCacheRedesign.md#single-writer-always-on-no-threshold) | Not started | `MemoryBackedObjectIndexWriter` still present; `HeapIndexingMode` and `--index-mode` still exist. Deferred to post-Tier-2-verification (after integration tests pass). |
| Columnar (struct-of-arrays) layout | [Columnar object index](14-CleanSlateCacheRedesign.md#columnar-object-index) | Not started | Deferred to later Tier 2 row. |
| Memory-mapped reader | [Reader](14-CleanSlateCacheRedesign.md#reader-memory-mapped-not-filestream--arraypool) | Not started | Current: `CacheContainerReader` uses one-handle-per-call `FileStream` model (matches pre-migration); mmap deferred. |
| Schema-driven writer/reader parity (source generator) | [Schema-driven writer/reader parity](14-CleanSlateCacheRedesign.md#schema-driven-writerreader-parity) | Not started | Deferred to later Tier 2 row. |
| Content-addressed cache key | [Content-addressed cache key](14-CleanSlateCacheRedesign.md#content-addressed-cache-key) | Not started | `FileHeader.DumpContentHash` reserved, zero-filled; validation deferred to later row. |
| Derived data instead precomputed satellites | [Derived data](14-CleanSlateCacheRedesign.md#derived-data-instead-of-precomputed-satellite-files) | Not started | Deferred; all nine sections still written pre-computed. |
| Corruption resilience + one-version-only migration policy | [Corruption resilience](14-CleanSlateCacheRedesign.md#corruption-resilience-and-format-version-migration) | Not started | `XxHash32` checksums computed and stored in TOC; validation deferred to later row. |
| Cache hit/miss telemetry line | [Cache telemetry](14-CleanSlateCacheRedesign.md#cache-telemetry) | Not started | Deferred post-verification. |

**De-risk via round-trip tests:** See [doc 16's next steps](16-ContainerFormatImplementationGuide.md#next-steps-in-order):
1. ✅ Write `CacheContainerRoundTripTests` and atomic-write tests (12 tests passing).
2. Baseline and re-run existing `*DiscrepancyTests` (`tests/DumpDetective.Tests/Integration/CacheDiscrepancies/`) pre- and post-migration.
3. Manual CLI smoke test on small/medium/large dumps.

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
