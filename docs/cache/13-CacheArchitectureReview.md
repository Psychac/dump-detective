# Cache & Indexing Subsystem — Architecture Review (2026-07-13)

Senior-architect pass over the full cache stack: everything under
`src/DumpDetective.Analysis/Cache/` (12 files) and
`src/DumpDetective.Analysis/Indexing/` (writers, readers, satellite files),
read end to end against the current `upgrade/clrmd-4` branch. This is a fresh
critique, not a restatement — it draws on and cross-checks
[11-CacheAnalysisFindings.md](11-CacheAnalysisFindings.md) and
[12-DiskOnlyCacheMigrationPlan.md](12-DiskOnlyCacheMigrationPlan.md), confirms
which of their findings are still live in the code as of this pass, and adds
findings neither doc covers. It closes with a direct answer to "is disk-only
the right way forward."

## Bottom line

The architecture is fundamentally sound — a facade over single-responsibility
sub-caches, streaming disk writers, `ArrayPool`-backed hot paths, a mostly
unified binary header format — and the recent commit history
(`d5dbd56`, `e386e96`, `6f771c0`, `bf12e65`, `6091b4b`) shows the team finding
and fixing real correctness bugs methodically via a disk-vs-memory
discrepancy test suite. That test suite is the best thing in this subsystem;
keep investing in it.

The single biggest structural problem is that **two independently
hand-written heap-scan implementations exist to produce what should be one
answer.** `DiskBackedObjectIndexWriter` and `MemoryBackedObjectIndexWriter`
duplicate ~150 lines of type-classification logic (`ComputeTypeFlags`,
`IsDelegateType`, `ComputeTypeShape`, segment→generation mapping, string
preview truncation) almost verbatim, and independently implement string-dedup
sampling, satellite candidate collection, and progress reporting. Every one
of Findings 1, 1b, 1c, 5, and 6 in doc 11 — five separate bugs, months of
investigation — is a symptom of this one design decision, not five
unrelated defects. Doc 12's proposal to delete the memory writer and go
disk-only is the correct fix, and this review recommends executing it,
expanded in a few places below.

## What's working well

- **Facade decomposition (ADR-011 mostly honored).** `HeapAnalysisCache` is a
  thin facade delegating to `HeapIndexCache`, `StatisticsCache`, `RootCache`,
  `ThreadCache`, `MethodTableCache`, `TypeMetadataCache` — each with one job
  and its own `CacheMetrics`. This is a real improvement over a single god
  object and makes each piece independently testable.
- **Streaming disk writer.** `DiskBackedObjectIndexWriter` scans segments in
  parallel, writes each segment to its own scratch file (avoiding a
  shared-stream lock that would otherwise make disk-mode entry order
  nondeterministic — a subtle, correct call), then concatenates
  deterministically. `ArrayPool<HeapEntry>`/`ArrayPool<byte>` reuse throughout
  the hot loop honors the project's no-per-object-allocation rule.
- **`IndexHeader` unification effort.** 8 of 10 satellite files share one
  24-byte header struct with `TryRead`/`WriteTo`/`PatchRecordCount` — a good
  instinct that should be finished, not abandoned (see Finding H below).
- **Deterministic `SampleAddress` tie-break** (`TypeIndexBuilder.Add`/`Merge`,
  lowest-address-wins) is a clean, minimal fix for what was a real
  cross-run/cross-mode nondeterminism bug — the kind of fix that's easy to
  get wrong (e.g. by making it order-dependent again under merge) and here
  it isn't.
- **`ReadAtLeast`-per-record migration** (Finding 6) correctly identifies
  that hand-rolled batch+carry-over parsing is a recurring bug class and
  standardizes on the safe idiom everywhere except the one hot path
  (`ObjectIndexReader`) where the perf tradeoff is real and explicitly
  justified in comments — good judgment, not cargo-culting the fix
  everywhere.
- **Disk-vs-memory discrepancy tests**
  (`tests/DumpDetective.Tests/Integration/CacheDiscrepancies/`) running the
  same real dump through both modes and asserting field-for-field equality
  is exactly the right regression net for a dual-implementation cache. It's
  also the reason doc 12 landed on "delete one of the implementations"
  instead of "fix the discrepancies" — the tests kept surfacing new ones.

## Findings

Confirmed against current source; each notes whether it's newly identified
here or corroborates/extends a doc-11/12 finding.

### A. Dual-writer duplication is the root cause, not just a symptom list (extends doc 11/12)

`ComputeTypeFlags`, `IsDelegateType` (inlined differently in each file —
`DiskBackedObjectIndexWriter` factors it into a helper method,
`MemoryBackedObjectIndexWriter` inlines the same 4-level BaseType walk
directly in `ComputeTypeFlags`), `ComputeTypeShape`,
`SegmentKindToGeneration`/`MemorySegmentKindToGeneration` (identical bodies,
different names), `ResolveObjectGeneration`, and `CreatePreview` are each
duplicated between the two writer files. String-dedup sampling is *not*
duplicated — it's divergent by design (disk samples every string, memory
adaptively skips), which is exactly the kind of drift that duplication
invites and did in fact cause Finding 1b. Doc 12 already scopes deleting
`MemoryBackedObjectIndexWriter`; the point worth adding is that this
duplication is the mechanism by which four of doc 11's six findings were
introduced, which should raise this from "nice cleanup" to "the primary
justification for the migration" when presenting the plan.

### B. `GetRootDescription` needs more than the delegation fix doc 11/12 propose

Confirmed still broken: `HeapAnalysisCache._rootDescriptions` (line 21) is
declared, never assigned, and `GetRootDescription` (line 298) reads only that
dead field. Doc 11's suggested fix — delegate to `_rootCache` — is necessary
but not sufficient. Reading `RootCache.GetOrBuildValidRoots`: the **disk**
fast-path (`RootIndexReader.ReadRootTargets` → `RootIndex.bin`) returns
before ever populating `_rootDescriptions`, because `RootIndex.bin`'s
20-byte record (`TargetAddr | RootAddr | Kind`) has no field for the
description string in the first place — only the **memory**-mode path
(`EnsureRootCaches`, which calls `root.ToString()`) ever populates it. So
even after fixing the delegation, disk-mode — the mode this migration is
making universal — would still always return `null`. Once disk-only lands,
this stops being "a symmetric gap in both modes" (doc 11's framing) and
becomes "a feature that never works." Fixing this for real requires either
(a) extending `RootIndex.bin`'s record format to carry (or reference) a
description, or (b) deriving the description lazily on request via
`heap.EnumerateRoots()` filtered to the requested address (acceptable since
`GetRootDescription` looks like a low-volume, on-demand call, not a hot
path — confirm call-site volume before choosing this). Either way, scope
this into the same PR that fixes the delegation, not as a follow-up.

### C. Dead code duplicated verbatim, doc-12 Phase 0 not yet executed

`HeapAnalysisCache.cs:143-249` still contains `SelectPrebuildMode`
(byte-identical to `HeapIndexCache`'s copy, plus a stale
`// TEMP-ADAPTIVE-INDEXING` comment doc 12 already flagged for deletion),
`TryHydrateTypeStatisticsFromIndex`, `ResolveTypeNameFromSample`,
`ResolveModuleNameFromSample`, and `AddClamped` — all unreachable, since
`PrebuildHeapIndex` and `GetOrBuildTypeStatistics` delegate straight to
`HeapIndexCache`/`StatisticsCache`, which carry their own live copies of the
same four hydration helpers. This is ~110 lines of dead, exactly-duplicated
logic sitting next to the real implementation, which is exactly the kind of
thing that gets edited in one place and silently not the other during a
future bugfix. Doc 12 scopes this as "Phase 0 — cleanup, ship first,"
correctly identified as independent and low-risk; as of this pass it has not
been executed. Recommend doing it immediately, decoupled from the larger
migration — there's no reason this waits.

### D. Three incompatible on-disk header formats, not one

Doc 12 already flags `ObjectIndex.bin` and `StringDedupIndex.bin` as
"non-conforming" relative to `IndexHeader`. Worth being precise about how
different they actually are, since it affects how much migration work
Phase 2 really is:

- **`IndexHeader`** (8/10 files): Magic(4) + Version(4) + RecordCount(8,
  `long`) + Reserved(8) = 24 bytes, fixed-width records after.
- **`ObjectIndex.bin`**: Magic(4) + Version(4) + Ticks(8) + RecordCount(8,
  `long`) = 24 bytes — same *size* as `IndexHeader` but a different field at
  offset 8 (a timestamp instead of half of a record count spanning 8-16).
  `TryReadObjectCount` only checks Magic, confirmed still true — `Version`
  is written but never validated on read, so a future breaking format
  change to this file would be silently misparsed rather than rejected.
- **`StringDedupIndex.bin`**: Magic(4) + Version(4) + EntryCount(4, `int`,
  not `long`) = 12 bytes, and its records aren't fixed-width either — each
  record is a 31-byte fixed prefix followed by 0-2 optional 8-byte sample
  addresses and a variable-length UTF-8 preview string, so it can only be
  read sequentially front-to-back, never seeked into. This is the most
  bespoke of the three and the one most likely to grow a subtle bug if
  touched again by hand.

Net effect: three different header layouts, two different record-count
widths, and one file with genuinely variable-length records that the other
nine don't have to deal with. Endorse doc 12's Phase 2 plan to move the
first two onto `IndexHeader` with new version numbers (correctly calling
out that reusing version `1` would let old-format bytes be misparsed as
new-format). Suggest also giving `StringDedupIndex.bin`'s variable-length
records a leading length-prefix per record (they're missing one right now —
the reader infers boundaries by parsing fields in order) so a future reader
could skip/index into the file rather than being forced to parse serially.

### E. Cache-hit fast path validates 2 of 10 files (confirms doc-11 Finding 4)

Confirmed unchanged: `DiskBackedObjectIndexWriter.TryLoadFromCache` checks
only `File.Exists(indexPath)` and `File.Exists(typeAggPath)`. All satellite
writes (`HandleSnapshot.bin`, `RootIndex.bin`, `TaskIndex.bin`,
`EventCandidateIndex.bin`, `LargeObjectIndex.bin`, `LohFreeBlockIndex.bin`,
`StringDedupIndex.bin`) are wrapped in independent try/catch blocks that
degrade to a logged warning on failure — reasonable resilience for a single
run — but nothing re-checks them on the *next* run's cache-hit path, so a
transient failure (disk-full, AV lock, permissions blip) permanently and
silently downgrades every future analysis of that dump until someone deletes
`.dumpindex/` by hand. This is more urgent once disk-only lands: today a
memory-mode run is an escape hatch for a corrupted disk cache (delete or
force `--index-mode memory`); once disk is the *only* path, a bad cache
becomes a hard failure mode with no workaround short of manually deleting a
hidden folder. **Recommend sequencing the `CacheManifest.bin` work
(doc-12 Phase 2) no later than Phase 1**, not strictly after it as currently
ordered — or at minimum, ship a manual `--no-cache` / cache-bypass flag in
Phase 1 as a stopgap so users have an escape hatch before the manifest
exists.

### F. `CacheMetrics`/`GetHealth()` is dead observability code

`HeapAnalysisCache.GetHealth()` and `GetCacheMetrics()` are fully
implemented — every sub-cache reports `EntryCount`, `LastBuildDurationMs`,
`IsHealthy`, `LastError` — but grepping the codebase, the only references to
`GetHealth`/`GetCacheMetrics`/`HeapCacheHealth` are their own definitions.
Nothing in the CLI, JSON output, or report generator ever calls them. This
is a fair amount of bookkeeping (six `CacheMetrics` objects assembled per
run) computed for no consumer. Per this project's own convention ("no
half-finished implementations"), either wire this into something real — a
`--cache-health` CLI flag, or fold a one-line summary into verbose/debug
output — or delete it. Given the manifest/staleness work above will want
*some* way to surface "cache hit vs. rebuilt, and why," this is a natural
thing to resurrect rather than delete outright.

### G. `HeapIndexBuildResult` immutability is shallow (minor, worth flagging not fixing)

ADR-009 says "favor immutable published caches... after publication:
immutable arrays, immutable records, read-only collections." The record
itself is immutable (`sealed record`, init-only via positional params), but
`InMemoryEntries` is `HeapEntry[]?` and `TypeAggregates` is typed as
`IReadOnlyDictionary<...>` over a plain `Dictionary` instance underneath —
both are reference types whose contents remain mutable from any caller that
casts or holds the array reference. This is a reasonable pragmatic choice
given `readonly struct HeapEntry` elements and single-writer/many-reader
usage within one process run, and rewrapping in `ImmutableArray`/
`FrozenDictionary` would cost real allocation/copy time on 25GB-dump entry
counts — not recommending a change here, just noting the ADR's language is
stronger than the implementation and either the ADR should say "immutable at
the API-shape level" explicitly, or a comment on `HeapIndexBuildResult`
should say why arrays were kept over `ImmutableArray`.

### H. Docs and code have drifted from each other

[README.md](README.md) (identical text to
[cache-modernization-spec.md](cache-modernization-spec.md) — these two files
are duplicates of each other, also worth deduplicating) states as a **Design
Goal**: "Preserve `MemoryBackedObjectIndexWriter` and
`DiskBackedObjectIndexWriter`." Doc 12 now proposes deleting the former
outright. That's not a mistake — doc 12 represents a considered, later
decision reached after more evidence (the discrepancy-test findings) than
existed when the original spec was written — but the older doc should be
updated once the disk-only plan is approved, or a future reader/agent will
reasonably treat "preserve both writers" as still-binding guidance and
resist the exact change this review recommends. Similarly, the spec's
"Target Architecture" lists `ReferenceGraphCache (lazy)` and
`Future DiskGraphCache` as facade members; neither exists in
`Cache/` today — `ReferenceGraph` lives in `Traversal/` and isn't wired into
`HeapAnalysisCache` at all. Not a bug, just stale roadmap language that
should either be marked "not yet started" or removed if it's no longer the
plan.

## Is disk-only the right way forward?

**Yes.** Recommend proceeding with doc 12, with findings B and E above
folded into its scope. The core argument:

- **It removes the root cause of the last five bugs**, not just their
  symptoms (Finding A). Patching memory mode to match disk mode
  field-by-field, the alternative doc 12 considered and rejected, would
  leave the duplication in place to cause bug #6.
- **It matches the project's own stated philosophy.** CLAUDE.md's core
  philosophy already says "prefer... disk-backed indices, built in one
  pass" and "never materialize the full heap into memory" as *general*
  rules for this codebase, not rules specific to large dumps. Memory-mode
  indexing is the one place in the codebase that structurally violates that
  rule (it holds `HeapEntry[]` for the entire heap, plus root/task/event/
  handle candidate arrays, resident simultaneously). Going disk-only doesn't
  just fix a bug pattern, it resolves an existing tension between this
  subsystem and the project's architecture doctrine.
- **The manifest only becomes meaningful with one writer.** A completeness
  manifest checked against "whichever of two writers happened to run" is
  weaker than one checked against a single well-defined writer's output set
  — doc 12 makes this point and it's correct.
- **The memory-mode advantage being given up is genuinely small.** For
  dumps under the 4GB threshold, the writers' own code shows disk I/O is
  already happening for the minidump reads themselves (ClrMD reading the
  dump file) — the *incremental* cost of writing `ObjectIndex.bin`
  sequentially alongside that, on any modern SSD, is unlikely to be the
  dominant cost next to the heap walk itself. This should still be measured
  (see below), but the theoretical case is weak.

**Caveats that should gate or shape the rollout, not block it:**

1. **Cache directory location becomes a universal concern, not an edge
   case, the moment disk-only ships.** This has been addressed: the `--cache-dir`
   CLI flag and `CacheDirectory` config-file setting implement a 4-tier fallback
   (explicit dir → colocated → temp folder → error) that allows users on
   read-only/network-mounted storage to specify a writable cache location without
   needing to stay under the 4GB memory-mode threshold. The temp-folder tier
   includes explicit user warning and hash-based isolation per dump to avoid
   collisions. Documented in [architecture.md](../../docs/architecture.md#cache-directory-resolution).
2. **Measure small-dump latency before flipping the default**, don't just
   reason about it. A CLI user re-running analysis on a 50MB dump
   interactively will notice a regression from "instant, in-memory" to
   "small but nonzero disk write + fsync-adjacent flush" even if it's
   objectively cheap. A quick before/after benchmark on a small dump (the
   repo already has `BenchmarkSuite1/HeapIndexBuildBenchmark.cs`) as part of
   Phase 1 acceptance criteria would catch this before it becomes a support
   complaint.
3. **Disk-full mid-write needs to fail loudly for the two required files.**
   Satellite files already degrade gracefully to a logged warning — good.
   `ObjectIndex.bin`/`TypeAggregateIndex.bin` themselves should not: if
   these two required files fail to write, the analysis has no cache at
   all, which is correct today because the *code* falls through to using
   the in-memory result directly for that run — but once disk-only removes
   that fallback path's twin, confirm `Build()` still degrades to
   "analyze successfully this run, just don't persist a cache" rather than
   hard-failing the whole analysis on a full disk. Worth an explicit test
   case in Phase 3's plan.

## Prioritized recommendations

| # | Recommendation | Effort | Why now |
|---|---|---|---|
| 1 | Doc-12 Phase 0: delete dead `HeapAnalysisCache` duplicate code; fix `GetRootDescription` delegation **and** extend it per Finding B (lazy `ToString()` or `RootIndex.bin` format change) | Low | Independent, zero-risk, closes a real correctness gap, unblocks nothing else |
| 2 | Add a `--no-cache` / cache-bypass CLI flag | Low | Stopgap escape hatch before the manifest exists, cheap insurance for Phase 1 |
| 3 | Doc-12 Phase 1: delete `MemoryBackedObjectIndexWriter`, collapse dual-paths, remove `--index-mode` | High | Root-cause fix for 5 historical bugs (Finding A); biggest maintainability win in this review |
| 4 | `--cache-dir` flag with a sane default fallback | Medium | Done — implemented with 4-tier fallback chain (Caveat 1) |
| 5 | Doc-12 Phase 2: unify `ObjectIndex.bin`/`StringDedupIndex.bin` onto `IndexHeader`; ship `CacheManifest.bin` | Medium | Sequence no later than Phase 1 given Finding E's severity change post-migration |
| 6 | Small-dump latency benchmark as Phase 1 acceptance gate | Low | Cheap to run, prevents a UX regression from shipping unnoticed |
| 7 | Wire `CacheMetrics`/`GetHealth()` into a real consumer (verbose output or a `cache-health`/`cache-status` surface), or delete it | Low | Currently dead code either way; the manifest work gives it a natural purpose |
| 8 | Reconcile `README.md`/`cache-modernization-spec.md` (dedupe the two files, update "preserve both writers" goal, mark `ReferenceGraphCache` as not-yet-started) | Low | Prevents a future contributor/agent from citing stale guidance against this migration |
| 9 | `dumpdetective cache clean` manual command (not automatic eviction) | Low, defer until after 3-5 | Only relevant once `.dumpindex/` folders are created unconditionally for every dump size |

## Explicitly not recommending

- **No automatic eviction/TTL/LRU.** This is a one-shot-per-dump CLI, not a
  long-running service; `.dumpindex/` folders accumulating next to dumps a
  user chose to keep is a reasonable default, and building LRU machinery for
  a workload that doesn't have concurrent competing cache entries would be
  the kind of premature abstraction this project's conventions warn against.
  A manual clean command (#9) is enough.
- **No change to `ObjectIndexReader`'s batch+carry-over pattern.** Doc 11's
  own reasoning for keeping it — hottest read path, correctness already
  verified, per-record `ReadAtLeast` overhead is real at 100M+ record scale
  — is sound and this review agrees with leaving it as the one deliberate
  exception to the `ReadAtLeast` standardization.
  are correctly deferred; the disk-only migration is more valuable and
  should land first, and either extension is much easier to design well on
  top of a single-writer, manifest-validated cache than on top of the
  current two-writer split.
