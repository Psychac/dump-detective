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

Finding 4 (disk cache-hit doesn't validate satellites) **not** in
tier — it's subsumed Tier 2's TOC + per-section checksums, no separate
fix needed.

## Tier 1 — Cheap, format-independent — do anytime, nothing here wasted

Small, self-contained items don't depend on storage design wins,
there's no reason sequence relative migration.

| Item | Effort | Status |
|---|---|---|
| Delete dead `HeapAnalysisCache` duplicate code (pairs Finding 2 fix above) | Low | Done |
| `--no-cache` / cache-bypass CLI flag | Low | Not started |
| `--cache-dir` flag sane default | Medium | Not started |
| Small-dump latency benchmark acceptance gate | Low | Not started — run both current code again Tier 2 lands |
| Reconcile `README.md`/`cache-modernization-spec.md` actual state | Low | Not started |

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

| Item | Doc section | Notes |
|---|---|---|
| Single container file + table contents | [The core idea](14-CleanSlateCacheRedesign.md#the-core-idea) | |
| Atomic write (`.tmp` + rename) | [File layout](14-CleanSlateCacheRedesign.md#file-layout) | |
| Single writer, always on, no memory/disk branch | [Single writer, always on](14-CleanSlateCacheRedesign.md#single-writer-always-on-no-threshold) | Deletes `MemoryBackedObjectIndexWriter`, `HeapIndexingMode`, `--index-mode`; closes Finding 1 root cause and obsoletes the Finding 3 workaround entirely (no more memory/disk branch to hydrate) |
| Columnar (struct-of-arrays) layout | [Columnar object index](14-CleanSlateCacheRedesign.md#columnar-object-index) | |
| Memory-mapped reader | [Reader](14-CleanSlateCacheRedesign.md#reader-memory-mapped-not-filestream--arraypool) | |
| Schema-driven writer/reader parity (source generator) | [Schema-driven writer/reader parity](14-CleanSlateCacheRedesign.md#schema-driven-writerreader-parity) | |
| Content-addressed cache key | [Content-addressed cache key](14-CleanSlateCacheRedesign.md#content-addressed-cache-key) | |
| Derived data instead precomputed satellites | [Derived data](14-CleanSlateCacheRedesign.md#derived-data-instead-of-precomputed-satellite-files) | |
| Corruption resilience + one-version-only migration policy | [Corruption resilience](14-CleanSlateCacheRedesign.md#corruption-resilience-and-format-version-migration) | Core to shipping new binary format at all, not optional; also closes Finding 4 |
| Cache hit/miss telemetry line | [Cache telemetry](14-CleanSlateCacheRedesign.md#cache-telemetry) | Natural fit here — feeds back into Tier 3 decisions, per doc 14 #9 |

**De-risk via round-trip tests:** expand existing
`*DiscrepancyTests` (`tests/DumpDetective.Tests/Integration/CacheDiscrepancies/`)
to round-trip the new container format
(pre-migration baseline vs. post-migration actual output from same dump).
This de-risks the big-bang format swap.

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
