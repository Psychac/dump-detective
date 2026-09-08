# Cache Implementation — Clean-Slate Redesign

Companion to [cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md).
That document is about the **bytes on disk**. This one is about the **code that produces and
consumes them** — the container reader/writer, the sub-cache layer, and the build orchestration.
The two are independent: either could ship without the other.

Every claim below is grounded in current `upgrade/clrmd-4` source, with file:line references.
Numbers labelled **measured** come from the 14.6M-object real dump already profiled in
[docs/discrepancy/cache-footprint-comparison.md](../discrepancy/cache-footprint-comparison.md).
Numbers labelled **derived** are arithmetic on those measurements, not separate measurements.
§ 6.0(a), § 6.1, § 6.2 and § 6.4 are implemented; § 6.1 has been verified end-to-end against a real
dump (see [measurements § 7](cache-redesign-measurements.md)). § 6.3 remains unbuilt.

---

## Executive summary

Seven findings, in descending order of how much they actually cost. Costs are measured —
see [cache-redesign-measurements.md](cache-redesign-measurements.md).

| # | Finding | Kind | Measured cost |
|---|---|---|---|
| 1 | Every section open re-hashes the entire section; nothing shares an open container | Real repeated work | **68.8 ms/open, 197% of the 34.8 ms scan it gates**; ≈20 opens/run ≈ **1.38 s** (≈8.2 s on the 27.5 GB dump) |
| 1a | The parallel dispatcher opens a range enumeration **per worker**, and the open checksums the *whole* section regardless of range | Real repeated work, **scales with core count** | 8 of the ≈20 opens on an 8-core box; 32 on a 32-core one. Hashes 2.9 GB to read 365 MB |
| 1b | Four analyzers call `.Any()` on the enumeration purely to test "does a disk index exist?" | Trivially fixable waste | 4 × 68.8 ms ≈ **275 ms/run to compute four booleans**, plus four redundant re-opens |
| 2 | The columnar split's stated benefit is unreachable through the only enumeration API | Real wasted I/O | 131.6 MB of every 365.5 MB enumeration unused by MT-filtering callers — but secondary to #1, see § 6.3 |
| 3 | Cache-hit validation checks 2 of 25 sections | Correctness gap | `TryLoadFromCache` @ `DiskBackedObjectIndexWriter.cs:1684` |
| 4 | Five structurally identical lazy-provider caches; 25 hand-rolled section-write blocks | Maintainability | ~425 lines of copy-paste; 13 duplicated try/catch/abort blocks |
| 5 | `EventCandidates` is a phantom section — the backlog entry for it was stale | Stale doc | No writer/reader/collection in current code (present in v3 caches; writer removed before v4) |

**1b is free and should just be done** — four one-line changes, no design work, no dependency on
anything else here (§ 6.0). 1 and 1a are the same root cause and are fixed together by § 6.1.
2 is real but re-measure after § 6.1 rather than assuming its full value. 3 is a correctness gap
already in the backlog. 4 is tidiness — worth doing *as the vehicle* for 1–3, not on its own.

---

## 1. Section opens are unshared, and each one re-hashes the whole section

`CacheContainerReader.TryOpen` re-reads and re-parses the header + TOC from a fresh
`FileStream` ([CacheContainerReader.cs:46-83](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L46-L83)).
There are **12 independent call sites** doing this, plus a 13th via `CacheSectionHelper`:

```
Cache/ForwardIndexCache.cs              Indexing/ObjectIndexReader.cs
Cache/ReverseIndexCache.cs              Indexing/ObjectAddressLookup.cs
Cache/DominatorTreeIndexCache.cs        Indexing/RootIndexReader.cs        (×2)
Cache/DominatorReachableIndexCache.cs   Indexing/TaskIndexReader.cs
Cache/ThreadRetentionIndexCache.cs      Indexing/Satellite/DiskHandleSnapshotReader.cs
Indexing/DiskBackedObjectIndexWriter.cs Indexing/Satellite/HandleSnapshotProvider.cs
```

Parsing a ~25-entry TOC is cheap. The expensive part is what happens next: **both**
`TryOpenSection` and `TryOpenSectionAccessor` create a *new* `MemoryMappedFile` and then
verify the section's XxHash32 over its entire byte range before returning
([CacheContainerReader.cs:113-126](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L113-L126),
[:169-182](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L169-L182)).
There is no memoization — the checksum is recomputed on *every* open of the *same* section
within the *same* process, on a file that hasn't changed since `Finish()` renamed it into place.

The comment at [CacheContainerReader.cs:9-16](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L9-L16)
says the TOC "is read once into memory by `TryOpen`" — true per reader instance, but nothing
shares a reader instance across the run, so in practice it's read 13 times.

### What this costs

`HeapIndexCache.EnumerateIndexedEntries` → `ObjectIndexReader.ReadDiskEntries` →
`TryOpenColumns` opens all four object columns
([ObjectIndexReader.cs:85-121](../../src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs#L85-L121)),
each with a full checksum pass.

**Measured** section sizes on the 14.6M-object dump:

| Column | Bytes |
|---|---|
| `ObjectAddresses` | 116,961,296 |
| `ObjectMethodTables` | 116,961,296 |
| `ObjectSizes` | 116,961,296 |
| `ObjectGenerations` | 14,620,162 |
| **Total per enumeration** | **365,504,050 (348.6 MiB / 365.5 MB)** |

> **Units.** 365,504,050 bytes is 348.6 MiB (1024²) or 365.5 MB (10⁶) — the same quantity appears
> both ways across these docs. Timings and throughputs below are computed from the byte counts, so
> they are unaffected. See the units note in
> [cache-redesign-measurements.md](cache-redesign-measurements.md).

**Measured** ([cache-redesign-measurements.md](cache-redesign-measurements.md) § 5), replicating
`VerifyChecksumZeroCopy` and the column scan against the reference `cache.bin` with warm pages —
so this is CPU cost, not first-touch I/O, which is the honest figure for the repeated-open case:

| Column | MB (10⁶) | Verify | Verify throughput | Raw scan |
|---|---:|---:|---:|---:|
| `ObjectAddresses` | 117.0 | 16.9 ms | 6.92 GB/s | 9.8 ms |
| `ObjectMethodTables` | 117.0 | 24.3 ms | 4.81 GB/s | 9.2 ms |
| `ObjectSizes` | 117.0 | 25.2 ms | 4.64 GB/s | 14.2 ms |
| `ObjectGenerations` | 14.6 | 2.3 ms | 6.30 GB/s | 1.7 ms |
| **Total per open** | **365.5** | **68.8 ms** | | **34.8 ms** |

**Every `EnumerateIndexedEntries()` call spends 68.8 ms verifying a checksum — 197% of the 34.8 ms
scan that verification gates.** Verification isn't overhead on the read; it is twice the read. On
the 27.5 GB dump's cache the same four columns total 2,177.6 MB, scaling to roughly 410 ms per open.

**Derived**: that 365,504,050 bytes is re-hashed on every call to `EnumerateIndexedEntries()`,
`EnumerateIndexedEntriesRange()`, or `EnumerateIndexedEntriesAsTuples()`. Current distinct
consumer sites: `AsyncStateMachineAnalyzer`, `DominatorAnalyzer`, `EventLeakAnalyzer`,
`EventLeak/PublisherRegistry`, `ReferenceChainAnalyzer`, `TimerLeakAnalyzer`,
`WeakReferenceAnalyzer`, `HeapIndexScanDispatcher`, `QueryEngine` — several of which
enumerate more than once.

A worse instance of the same pattern: `ObjectIndexReader.TryGetEntry`
([:32-44](../../src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs#L32-L44)) opens
and disposes an `ObjectAddressLookup` — container open + `SegmentIndex` checksum —
**per single-address lookup**. `HeapIndexCache.TryGetObjectMetadata` avoids this by caching
its own `_addressLookup` ([HeapIndexCache.cs:110-142](../../src/DumpDetective.Analysis/Cache/HeapIndexCache.cs#L110-L142));
the `IObjectIndexReader` path does not.

### Why the current design got here

The per-open verify is the right *default* for a first read: it's what makes
"corruption is treated exactly like a missing section" work without special-casing at
13 call sites. The bug isn't verifying — it's verifying *repeatedly* because there's no
object whose lifetime is "this run" to remember that it already passed.

---

## 2. The columnar split's benefit is unreachable through the public API

The four `Object*` sections were deliberately split so that, in the words of the code:

> keeps each column contiguous in the container so a reader that only needs MethodTable (type
> aggregation) or Size (histograms) doesn't pay to read Address too
> — [DiskBackedObjectIndexWriter.cs:560-564](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L560-L564)

But the only enumeration API returns `HeapEntry`, which carries all four fields — so
`TryOpenColumns` opens and checksums all four columns unconditionally, and bails if any one
is missing. There is no column-projection API anywhere. The stated benefit is real in the
*format* and unrealized in the *implementation*.

**Derived**, for the several consumers that filter by `MethodTable` against a candidate set
and then use `Address` (`AsyncStateMachineAnalyzer`, `TimerLeakAnalyzer`,
`EventLeak/PublisherRegistry`, `WeakReferenceAnalyzer`): `Sizes` + `Generations` =
131,581,458 bytes of the 365,504,050 — **36% of every such enumeration's checksum work and
page-cache traffic is discarded**.

---

## 3. Cache-hit validation checks 2 of 25 sections

Already in [backlog.md](backlog.md); quantified here.
`TryLoadFromCache` ([DiskBackedObjectIndexWriter.cs:1684-1704](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L1684-L1704))
checks exactly three things: the dump content hash, that `ObjectAddresses` has a TOC entry
with `RecordCount > 0`, and that `TypeAggregates` parses.

25 sections are written across the codebase (`BeginSection` call sites in
`DiskBackedObjectIndexWriter`, `ForwardEdgeContainerWriter`, `ReverseEdgeContainerWriter`,
`DominatorTreeIndexWriter`). A transient failure in any of the other 23 — each of which is
caught and downgraded to a `satelliteWarnings` string, by design — produces a `cache.bin`
that passes the fast path forever. The comment at
[DiskBackedObjectIndexWriter.cs:971-972](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L971-L972)
("TypeAggregates section LAST so its presence confirms complete build") is only true for
*ordering*, not for *completeness*: every satellite section is individually skippable and
`TypeAggregates` still gets written.

The redesign in § 5 closes this as a side effect rather than as a separate feature.

---

## 4. The same idea, expressed five times and twenty-five times

**Five lazy-provider caches.** `ForwardIndexCache`, `ReverseIndexCache`,
`DominatorReachableIndexCache`, `DominatorTreeIndexCache`, and `ThreadRetentionIndexCache`
are structurally identical — verified by diff, they vary only in type names, doc comments,
and (for `ThreadRetentionIndexCache`) one extra constructor dependency. Each is ~85 lines of:

```csharp
if (_attempted) return _provider;
_attempted = true;
HeapIndexBuildResult? heapIndex = _getHeapIndex();
if (heapIndex is null || string.IsNullOrEmpty(heapIndex.IndexPath)) return null;
try {
    if (!CacheContainerReader.TryOpen(heapIndex.IndexPath, out var container) || container is null) return null;
    if (!XxxReader.TryOpen(container, out var reader) || reader is null) return null;
    _reader = reader; _provider = new XxxProvider(reader);
    _lastBuildTime = DateTime.UtcNow; _lastBuildError = null;
} catch (Exception ex) { _lastBuildError = $"{ex.GetType().Name}: {ex.Message}"; _provider = null; }
return _provider;
```

…plus an identical `GetMetrics()` and `Dispose()`. ~425 lines total.

**Twenty-five section-write blocks.** `DiskBackedObjectIndexWriter.Build` is a ~1,030-line
straight-line method. Thirteen of its sections are written as hand-rolled

```csharp
try {
    progress?.Report(new(0, "writing Xxx section", ...));
    containerWriter.BeginSection(CacheSectionId.Xxx);
    long n = XxxWriter.Write(containerWriter.Stream, ...);
    containerWriter.EndSection(n);
} catch (OperationCanceledException) { throw; }
catch (Exception ex) { containerWriter.AbortSection(); warnings.Add($"Xxx: {ex.GetType().Name}: {ex.Message}"); }
```

Writer/reader parity is maintained purely by convention. The backlog already gates a source
generator for this behind "a new section added by hand causes a drift bug" — the proposal in
§ 5 is deliberately *not* a source generator; it's a plain descriptor list, which needs no
build-time machinery and is the thing that makes § 3 fixable.

---

## 5. `EventCandidates` is a phantom section in current code — the backlog entry was stale

The backlog said:

> **`EventCandidateIndex` section is written every build but never read.** … The data is
> already collected and paid for during the write pass; wiring `EventLeakAnalyzer` to prefer
> it … is a real, scoped, **zero-new-infrastructure** perf win.

**This is not accurate.** A full-source search for `EventCandidate` returns three hits:

- `CacheContainerFormat.cs:18` — the enum member `EventCandidates = 5`
- `HeapIndexBuildResult.cs:73` — a doc comment referring to a no-longer-existent
  `EventCandidateIndex.bin`
- `HeapIndexBuildResult.cs:77` — the `InMemoryEventCandidates` optional parameter

There is no `BeginSection(CacheSectionId.EventCandidates)` anywhere, no candidate collection
in the scan loop (unlike `taskCandidates` / `largeCandidates` / `lohFreeBlockCandidates`),
no `EventCandidateIndexWriter`, and no reader. `DiskBackedObjectIndexWriter` never passes
`InMemoryEventCandidates`, so it is always `null`.

The enum slot is reserved-but-unused, which is correct and must stay (renumbering would
break existing caches). But the work item is **not** "wire up a reader" — it is "add
collection, add a writer, add a reader," which is a materially bigger and differently-shaped
piece of work. The backlog entry has been rewritten accordingly.

**Provenance, established later by measurement.** The backlog entry was not invented: all three
**v3** `cache.bin` files on disk *do* contain an `EventCandidates` section (365,496 / 4,440 /
4,226,952 bytes), and both **v4** files do not
([measurements § 1.1](cache-redesign-measurements.md)). So v3-era code wrote it and the writer was
removed before v4. The finding above is correct for current code; the entry was simply describing a
prior release. The v3 files are dead regardless — `CacheFileHeader.TryRead` rejects any version ≠ 4.

---

## 6. Proposed redesign

Four pieces, in dependency order. Each is independently shippable and each is small.

### 6.0 Do first — no design work, no dependencies

Two items fall out of [measurements § 6](cache-redesign-measurements.md) that need none of the
redesign below. They should land regardless of whether anything else here is ever approved.

**(a) Replace the four `.Any()` disk-index probes with the existing guard. ✅ DONE.**
All four sites converted; `dotnet build` clean, full unit suite green (1136 passed, 0 failed,
20 skipped — the real-dump `DiscrepancyFact` tests, which self-skip without
`DD_RUN_DISCREPANCY_TESTS=1`). A repo-wide search confirms no
`EnumerateIndexedEntries*().Any()` probe remains.

| Site | Was |
|---|---|
| `AsyncStateMachineAnalyzer:215` | `cache.EnumerateIndexedEntriesAsTuples().Any()` |
| `TimerLeakAnalyzer:270` | `cache != null && cache.EnumerateIndexedEntriesAsTuples().Any()` |
| `WeakReferenceAnalyzer:392` | `cache.EnumerateIndexedEntriesAsTuples().Any()` |
| `ReferenceChainAnalyzer:586` | `cache.EnumerateIndexedEntriesAsTuples().Any()` |

each cost a full container open, four column maps, and a 365.5 MB checksum — to read one record and
answer a boolean. The correct test already exists in this codebase and is used by
`EventLeakAnalyzer:869`, `EventLeak/PublisherRegistry:143`, and `DominatorAnalyzer:782`:

```csharp
cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _)
```

`PublisherRegistry` even carries a comment explaining why the looser check is wrong. This was four
one-line changes worth ≈275 ms/run, and it removes four of the ≈20 opens outright.

Two implementation notes for anyone reading the diff: `TryGetHeapIndex` is on the concrete
`HeapAnalysisCache`, **not** on `IHeapAnalysisCache`, so the pattern-match cast is required rather
than stylistic — all four analyzers already imported `DumpDetective.Analysis.Cache`. And in
`AsyncStateMachineAnalyzer` / `WeakReferenceAnalyzer` the pattern variable is named `indexedCache`,
not `heapCache`, because an enclosing scope in both files already binds `heapCache` (CS0136).

Note the two idioms are not quite equivalent and the difference matters in the right direction:
`.Any()` answers "did the enumeration yield anything," `TryGetHeapIndex` answers "is there a heap
index." `HeapIndexCache.EnumerateIndexedEntries` returns empty when `_heapIndex is null`, so the
guard is the *more* precise test of the condition these call sites actually branch on.

**(b) Make range enumeration checksum only the range.** `HeapIndexScanDispatcher:441` opens a
per-worker range enumeration; `TryOpenColumns` verifies the entire section every time, ignoring
`startRecord`/`recordCount` completely. On an 8-core box that hashes 2.9 GB to read 365 MB, and it
gets worse with core count.

Strictly this is subsumed by § 6.1 (a session-scoped memoized verify makes workers 2..N free), so if
§ 6.1 is going ahead, do it there instead of twice. Listed separately because it is the single
largest contributor to the ≈20 and because § 6.1 is a larger change that may not be approved.

### 6.1 `CacheSession` — one open container per run ✅ DONE (subsuming § 6.0(b))

Shipped as session semantics on `CacheContainerReader` itself rather than a new type — it already
owned the TOC and the verification, so a separate object would have been a wrapper around it.
`HeapIndexCache` holds one instance for the run and routes both enumeration APIs through it via new
`ObjectIndexReader.ReadDiskEntries(CacheContainerReader)` / `ReadDiskEntriesRange(…)` overloads. The
`containerPath` overloads remain for one-shot callers and tests. Build clean, **1139 passed / 0
failed / 20 skipped**, +240/−20 across 4 files.

What it does:

- parses the TOC once per instance (unchanged)
- **memoizes checksum verification per section id** — verify on first open, remember the
  result (including failure), skip on subsequent opens
- gates verification **per section**, so N workers first-touching the same section collapse to one
  hash while different sections still verify concurrently
- hands out section views; keeps the existing "corruption == missing section" contract

> **⚠ One item from the original design was dropped, and the reason matters.** This section used to
> also require holding **one `MemoryMappedFile` for the whole file** instead of one per section
> open. Implementing that broke 70 tests with
> `IOException: cache.bin is being used by another process` — a session-lifetime mapping **locks the
> file on Windows**, so any later attempt to delete or replace the index directory fails. That is
> § 6.5(d)'s risk, and it turned out to bite immediately rather than theoretically.
>
> The mapping was reverted to per-call; only the memoization was kept. The full measured win is
> retained regardless, because the measured cost was the **checksum** (68.8 ms), never the mapping —
> a `CreateFileMapping` syscall is microseconds. The original bullet asserted the shared mapping as
> though it were part of the win; it never was, and it carried a real cost that was not identified
> until it was built.

This is the fix for § 1 **and § 1a** — both are the same root cause — and the enabler for
everything else. The per-open verify semantics callers rely on are preserved exactly; only the
*repetition* goes away.

**Verified end-to-end** on the reference 3.3 GB dump, same build, memoization on vs. bypassed
([measurements § 7](cache-redesign-measurements.md)): 82 section opens either way; **redundant
hashing down 78.5%** (5,904.3 → 1,270.8 MiB), which is **≈0.88 s of CPU** at the measured verify
throughput; a follow-up routing `ObjectAddressLookup` through the session took it further, to
**936.2 MiB — the design's floor**, with only 0.26 MiB of redundancy left across three tiny sections
([measurements § 7.1](cache-redesign-measurements.md)). The ≈1.38 s predicted here was ~36% high — it assumed all ≈20 opens were full
four-column groups; some were smaller satellite sections. **Wall clock moved 0.3 s on a 51 s run,
i.e. within noise at n=1** — the eliminated work is real and precisely measured, its user-visible
effect on this dump is not yet distinguishable from variance.

Non-obvious constraint to respect: the current unnamed-mapping-per-open design was chosen
for thread safety across concurrent analyzers
([CacheContainerReader.cs:9-16](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L9-L16)).
A shared session must keep `IAnalyzer.IsThreadSafe` honest — the memoization needs to be
concurrent-safe, and view creation from one shared `MemoryMappedFile` needs checking against
the same guarantee.

That constraint is load-bearing rather than theoretical: § 1a means the session's *first* real
workload is `HeapIndexScanDispatcher`'s `Parallel.For`, where 8 workers (32 on a big build agent)
open range enumerations simultaneously. The memoized-verify path must therefore handle N concurrent
first-touches of the same section without either double-verifying or serializing them behind one
lock for the duration of a 68.8 ms hash. This is the one part of § 6.1 that needs real design
attention rather than being mechanical.

### 6.2 `CacheSectionDescriptor` — one list, both directions ✅ DONE (partially — see the § 3 note)

Shipped as `CacheSectionCatalog` (all 28 ids, each with a `CacheSectionRequirement` of
`Required` / `Conditional` / `Unused`) plus `CacheContainerWriter.TryWriteSection(...)`.
Build clean, **1144 passed / 0 failed / 20 skipped**.

- **Write side**: five satellite sections (`Handles`, `Roots`, `Tasks`, `LargeObjects`,
  `LohFreeBlocks`) now route through `TryWriteSection`, which owns the progress report,
  `BeginSection`/`EndSection`, abort-on-failure and warning formatting. The four columnar sections
  keep explicit code because they close via the precomputed-checksum `EndSection` overload, and
  `TypeAggregates` keeps its own because it must be written last. Per § 6.5(b) the *order* stays
  explicit; only the wrapper is shared.
- **Fast path**: `TryLoadFromCache` now asserts every `Required` section is present, instead of
  checking two. Presence only, never checksums — see § 6.5(a).
- **Drift guard**: `CacheSectionCatalog.MissingFromCatalog()` plus a unit test fails the build if a
  new `CacheSectionId` is added without a catalog entry. That is the mechanism the backlog gated a
  source generator behind, achieved without build-time machinery.

> **⚠ § 3 is only partially closed, and the reason is structural.** § 3 and the original § 6.2
> wording called for confirming that "every section the previous build recorded is still present."
> Implementing it exposed the premise as vacuous: **the TOC only ever lists sections that were
> successfully closed**, so a section lost to a transient write failure leaves nothing behind to
> compare against. There is no record of intent to diff.
>
> What shipped instead is the achievable half — sections are classified, and the fast path enforces
> the five that *every* successful build writes unconditionally (`ObjectAddresses`,
> `ObjectMethodTables`, `ObjectSizes`, `ObjectGenerations`, `TypeAggregates`). That catches a core
> section lost to a disk-full or AV blip, which is the case that permanently degrades a dump.
> It does **not** catch a lost *conditional* section (`Roots`, `SegmentIndex`, the edge indices, the
> dominator sections), because absence is indistinguishable from "this build wasn't configured to
> produce it" — every one of those is behind a `DD_SKIP_*` escape hatch or Stage B gating.
>
> Fully closing § 3 requires the writer to persist a manifest of *intended* sections for the fast
> path to diff against. That is an additive format change and was not attempted here.

#### 6.2.1 The five `DD_SKIP_*` build toggles — ✅ DELETED

**Decision (2026-09-04):** all five were removed. They existed to A/B-isolate features during their
development; those features are now core to what DumpDetective produces, so a build that silently
omits one isn't a supported configuration. Removed: `DD_SKIP_ROOT_INDEX_BUILD`,
`DD_SKIP_REVERSE_INDEX_BUILD`, `DD_SKIP_FORWARD_INDEX_BUILD`, `DD_SKIP_SEGMENT_INDEX_BUILD`,
`DD_SKIP_DOMINATOR_INDEX_BUILD` — fields, branches, and skip-path progress messages.
Net −169/+113 in `DiskBackedObjectIndexWriter`; build clean, 1144 passed.

Deliberately **kept**: `DD_FORCE_LIVE_CLRMD_WALK` (selects the walk source, retained as a fallback
and doesn't gate any section) and `DD_PERF_DOMINATOR_STAGEB` (diagnostics only).

**Removing the toggles is not the same as promoting the sections to `Required`,** and the difference
turned out to matter. Only `Roots` and `SegmentIndex` were promoted. Three groups stay `Conditional`
for reasons no toggle removal touches:

- **`RootStackThreadAttribution` — backwards compatibility.** It was added additively *without* a
  `CacheFileHeader.CurrentFormatVersion` bump, so v4 containers written before it existed lack it
  legitimately. Checking the real caches on disk found exactly that: the 27.5 GB dump's v4 cache has
  no such section. Promoting it would have silently invalidated that cache and forced a full cold
  re-index of a 27.5 GB dump. Promotion has to ride along with a format-version bump.
- **`ReverseEdge*` / `ForwardEdge*` — deterministic failure at scale.** These depend on the
  reachability walk and bucket sorts, which can fail *repeatably* on a very large heap (the
  `ChunkedBuffer` int-overflow guard, or OOM) and are caught and downgraded to a warning by design.
  Marking them `Required` would convert that into an unbreakable loop — cache rejected → full
  rebuild → same failure → cache rejected — paying a cold build on every single run. Silent
  degradation is the lesser evil here.
- **`Dominator*` — genuine runtime gating.** Stage B is gated on an analyzer implementing
  `IRequiresDominatorTreeIndex`; that condition is real and unaffected.

The general lesson, worth carrying into any future promotion: **`Required` is a claim about every
container already on disk, not just about the current writer.** Any section introduced additively
since the last format-version bump cannot be promoted without one.

#### 6.2.2 Original analysis (superseded by the decision above)

Kept because it records why the toggles were removable and which had discharged their stated gate.

Three of the six `Conditional` classifications exist **only** because of a `DD_SKIP_*` escape hatch,
and two of those hatches are explicitly marked temporary in the source. Removing them would move
those sections to `Required` and close most of the gap above without any manifest or format change.

| Toggle | Sections it makes Conditional | Source says | Gate discharged? |
|---|---|---|---|
| `DD_SKIP_ROOT_INDEX_BUILD` | `Roots`, `RootStackThreadAttribution` | "TEMPORARY perf A/B toggle… **Remove once the A/B comparison picks a winner**" | **No** — that A/B *is* backlog item "GC-root enumeration, option 2", listed under "Three unattempted options, none started" |
| `DD_SKIP_SEGMENT_INDEX_BUILD` | `SegmentIndex` | "cheap insurance rather than an anticipated need — **remove once validated**" | **No** — the `ObjectAddressLookup` perf question the backlog tracks is still open, and its BenchmarkDotNet harness has never been run |
| `DD_SKIP_REVERSE_INDEX_BUILD` | `ReverseEdge*` | "Escape hatch… if it regresses build time on a given dump" | n/a — framed as a permanent operational lever, no removal language |
| `DD_SKIP_FORWARD_INDEX_BUILD` | `ForwardEdge*` | "Escape hatch… same graceful-degradation contract as every other optional satellite section" | n/a — same |
| `DD_SKIP_DOMINATOR_INDEX_BUILD` | `Dominator*` | "same A/B-isolation contract as the other `Skip*Build` flags" | Nominally temporary, but these sections are *also* gated by `IRequiresDominatorTreeIndex`/Stage B, so they stay Conditional regardless |

So the picture is: **`Roots`, `RootStackThreadAttribution` and `SegmentIndex` are Conditional purely
because of two toggles that were always meant to be deleted, and neither toggle's stated exit
condition has been met.** The reverse/forward edge hatches read as deliberately permanent, and the
dominator sections have a genuine analyzer-driven condition underneath the toggle.

`SegmentIndex` is the one to prioritise, because the same removal is load-bearing for a second
plan: [cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md) § 2.6 needs
`SegmentIndex` promoted from optional to required before CSR can rely on address→index resolution.
One deletion serves both.

Two adjacent toggles whose stated purpose *has* been discharged, listed for completeness since they
don't affect section classification: `DD_FORCE_LIVE_CLRMD_WALK`'s measurement concluded (loose-file
reader measured ~2x faster on a 25 GB dump) but it is deliberately retained as a fallback; and
`DD_PERF_DOMINATOR_STAGEB` exists for a § 10.8 measurement pass that has since been completed, which
makes it a straightforward deletion candidate.

The original design, for reference — the read-side half was not implemented:

- **Write side**: `Build` iterates descriptors instead of inlining 13 near-identical
  try/catch/abort blocks. The progress report, the abort-on-failure, and the
  `warnings.Add($"{name}: ...")` formatting live in one place.
- **Read side**: ✅ **DONE.** § 4's five copy-paste caches are now one generic,
  `LazyContainerIndex<TProvider>` — **−450 lines deleted, +100 added**. See § 6.4.
- **Fast path (§ 3)**: `TryLoadFromCache` iterates the descriptor list and confirms every
  section the previous build recorded is **present** in the TOC, instead of hard-coding two.
  Presence only — **not** checksum validity; see § 6.5(a), where validating integrity here
  would hash the whole file and undo § 6.1. Integrity stays lazy, on first open, memoized.

Deliberately *not* a source generator. A hand-written list in one file is enough to make
drift visible, and adds no build-time machinery.

### 6.3 Column-projection read API — ❌ CLOSED UNBUILT (gated)

**Decision (2026-09-05): § 6 is closed here and this item is not being built.** The evidence
turned against it. § 6.1 eliminated 4.97 GB of per-run hashing (5,904.3 → 936.2 MiB) and that did
not register above wall-clock noise on the reference dump. What § 6.3 would save is strictly
smaller — page-cache traffic and materialization of two unused columns out of four, with the
hashing already gone — so it cannot plausibly be measurable where the larger change wasn't.

Reopen only if a real workload shows base-column enumeration cost as a bottleneck. The analysis
below is kept because it is still correct about *what* is wasted; only the priority changed.



Add projection entry points alongside the existing `HeapEntry` enumeration — e.g. an
address+method-table pair stream — so the ~36% of column traffic that MT-filtering analyzers
discard (§ 2) is never opened, mapped, or checksummed. `HeapEntry` enumeration stays for the
consumers that genuinely need all four fields.

This must land *after* 6.1, and its value is now known to be smaller than this doc originally
implied. Measurement showed verification, not page-fault I/O, dominates a warm-cache open —
68.8 ms of checksum against 34.8 ms of actual scan. Once § 6.1 memoizes verification, what's left
for projection to save is the page-cache traffic and materialization of two unused columns, not
the hashing. Re-measure that delta before deciding how many projections to add; adding exactly one
(address + method table) covers every current consumer, and none should be added on spec.

### 6.4 `LazyContainerIndex<TProvider>` — the five provider caches collapsed ✅ DONE

`ForwardIndexCache`, `ReverseIndexCache`, `DominatorReachableIndexCache`, `DominatorTreeIndexCache`
and `ThreadRetentionIndexCache` are deleted and replaced by one generic. **−450 lines removed,
+100 added**; build clean, 1144 passed / 0 failed / 20 skipped.

Two things fell out that the § 4 write-up hadn't anticipated:

- **The five were less identical than they looked.** The doc said they "vary only in type names".
  In fact the *ownership* shape varies three ways: the edge indices expose a provider wrapping a
  disposable reader, the dominator readers **are** their own provider, and the thread-retention
  provider owns nothing and its class wasn't even `IDisposable`. A generic that only carried
  `TProvider` would have leaked the edge readers. The factory therefore returns
  `(TProvider? Provider, IDisposable? Owns)` — the one place the variation is real.
- **They now share § 6.1's container session.** Each used to call `CacheContainerReader.TryOpen`
  itself, so the five accounted for five of the ~13 independent opens catalogued in § 1. They now
  take `Func<CacheContainerReader?>` pointing at `HeapIndexCache.GetOrOpenContainerSession()`, so
  they reuse the run's session and inherit its verify-once memoization. This wasn't part of § 4's
  scope; it fell out of doing § 6.1 first, and is a further reason to have sequenced it that way.

`ThreadRetentionIndexCache`'s second constructor delegate (`Func<IDominatorTreeProvider?>`) became a
closure over `_dominatorTreeCache` inside the factory, preserving the dependency exactly. Its cache
is now disposed alongside the other four — it owns nothing today, but it is `IDisposable` like its
siblings, and disposing it stops that from becoming a silent leak if that ever changes.

### 6.5 Design scrutiny — problems with § 6.1 and § 6.2 as written

Found by pressure-testing the proposals, not by measurement. § 6.2 has a real contradiction.

**(a) ⚠ § 6.2's fast-path validation contradicts § 6.1.** § 6.2 proposes that `TryLoadFromCache`
"confirms every section the TOC claims to contain is present and checksum-valid." Checksum-validating
every section at startup means hashing the entire file — 1,398.3 MiB on the reference dump, ~2–3
seconds — which is precisely the cost § 6.1 exists to eliminate, paid up front and unconditionally.

The fix separates two things § 6.2 conflates: **presence** is a TOC read (free, and the actual gap
§ 3 describes — a section silently missing after a failed write); **integrity** is the checksum,
which § 6.1 already verifies lazily on first use and memoizes. So the fast path should assert
presence for every section the previous build recorded, and leave integrity to § 6.1's
verify-on-first-open. § 6.2 should be reworded accordingly — as written it would make cache hits
dramatically worse.

**(b) § 6.2's "one descriptor list, one write loop" doesn't fit the actual build.** Sections are
written at four different pipeline stages with different state available: the columnar sections need
the scratch files, the dominator sections need `walkResult`, the reverse-index sections need the
extractor, and `TypeAggregates` must be written **last** because its presence is the
build-completed signal. A uniform `foreach (descriptor) descriptor.Write(...)` cannot express that
without a context object carrying everything, which trades the duplication for a different mess.

The achievable version is narrower and should be stated as such: descriptors carry *metadata*
(id, required/optional, reader factory) and drive the **read** side and the fast-path check, while
the write side keeps its explicit ordered sequence but routes every section through one shared
`WriteSection(id, writeAction)` helper that owns the progress report, the abort-on-failure, and the
warning formatting. That still removes the 13 copy-pasted try/catch blocks and still gives one list
to validate against — it just doesn't pretend the write order is arbitrary.

**(c) § 6.1's session must not become process-global state.** `ObjectIndexReader` is a static
singleton whose methods take a `containerPath` — it is stateless today, which is why that is safe.
A session cached statically by path would leak across dumps, and this tool analyses two dumps in one
process for baseline/trend comparison. The session must be owned by the `HeapAnalysisCache` instance
(one per dump), not reachable from a static.

**(d) ✅ CONFIRMED, and resolved by dropping the shared mapping.** This warned that a run-lifetime
`MemoryMappedFile` would collide with `CacheContainerWriter.Finish()`'s
`File.Move(tmp, final, overwrite: true)`, and asked for it to be verified rather than assumed.
Verification was immediate: implementing the shared mapping failed 70 tests with
`IOException: cache.bin is being used by another process`, from directory cleanup rather than from
`File.Move` — a broader blast radius than this caveat anticipated, since *any* delete/replace of the
index directory is affected, not just a rebuild.

Resolved by keeping the mapping per-call and memoizing only the verification, which is where the
entire measured cost was (§ 6.1). No `IDisposable` on the reader, no file lock, full win retained.

**(e) § 6.1 weakens the corruption guarantee, deliberately.** Today every open re-verifies, so
corruption appearing mid-run is caught. Memoized, the guarantee becomes "verified once per run."
That is almost certainly the right trade for a file that is renamed into place and never rewritten,
but it is a real change in contract and should be a stated decision rather than a side effect.

**(f) § 6.0(a) changes behaviour in one degraded case.** `.Any()` asks "does the enumeration yield
anything"; `TryGetHeapIndex` asks "does a heap index exist." These diverge when the index exists but
its columns are unreadable — `TryOpenColumns` returns false, the enumeration yields nothing, and the
analyzer silently reports zero objects instead of falling back to a live heap walk. This is the same
exposure the three existing correct-idiom call sites already accept, and § 3's fast-path fix is what
actually prevents that state — but it should be a known trade, not a surprise. It is an argument for
sequencing § 3 alongside § 6.0 rather than long after it.

---

## 7. Considered and rejected: splitting `cache.bin` back into separate files

The natural objection to § 6.1 is that a single container forces us to hold more than we
need, and that going back to one file per section — the nine-file layout the container
replaced — would shrink the memory footprint by letting parts load and unload as the run
progresses. It would not, for three reasons.

**Per-section granularity already exists.** Nothing ever maps the whole file. Both open
paths create a view bounded to the section's byte range —
`CreateViewStream(entry.Offset, entry.Length, …)`
([CacheContainerReader.cs:115](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L115))
and `CreateViewAccessor(entry.Offset, entry.Length, …)`
([:171](../../src/DumpDetective.Analysis/Indexing/Container/CacheContainerReader.cs#L171)).
Twenty-five files would give exactly the same twenty-five bounded regions, plus
twenty-five file handles.

**Mapped pages are not process memory in the sense that matters.** They're file-backed
page cache: resident per 4 KB page actually touched, and reclaimable by the OS under
pressure with no action from us. That behaviour is identical whether the bytes live in one
file or twenty. Disposing a view releases virtual address space; it does not decide whether
the pages stay cached, and closing a file handle wouldn't either. VA reservation is per
view length regardless of file count, which on x64 is not a constraint.

**The real over-materialization has a different cause.** Two of them, and splitting fixes
neither:

- `VerifyChecksumZeroCopy` walks every byte of a section before returning the accessor, so
  opening `ObjectAddresses` faults in all 117 MB even for a ten-record read. Separate files
  would checksum the whole file instead — same bytes, same faults. The fix is § 6.1's
  per-session memoization, or finer-grained checksums (which is where this meets the format
  redesign's block-compression idea).
- `TryOpenColumns` opens all four columns when two are wanted (§ 2). Splitting turns that
  into four files instead of four sections. Same traffic.

**What splitting would cost.** `Finish()` is one `File.Move`, so a build either fully lands
or doesn't. There is no atomic multi-file commit, so partial-build state becomes
representable on disk — § 3's validation gap made structurally worse rather than better.
The content-addressed `DumpContentHash` is also one header checked once before any section
is touched; per-file it would have to be replicated or re-derived.

**What splitting would genuinely win**, stated so the trade is honest: selective reclamation
(delete the edge-index files to recover 762 MB of *disk* while keeping the object index,
where today the container must be rewritten), and the ability to add a section after the
initial build without a rewrite — which the backlog's "defer GC-root indexing to Phase 2"
option would need. Both are disk-footprint and flexibility wins. Neither is a memory win,
and neither currently has a caller.

---

## 8. Explicitly out of scope

- **The on-disk format.** CSR, dictionary encoding, and block compression are
  [cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md)'s subject.
  Nothing here changes a byte layout, and none of § 6 blocks or is blocked by that work.
- **Consolidating the seven `DD_*` env-var build toggles into an explicit build plan.** Still out of
  scope — but the earlier claim here that "each [is] documented as a temporary A/B toggle" was too
  broad, and § 6.2.1 now sets out what each one actually says. Only two are marked for removal
  (`DD_SKIP_ROOT_INDEX_BUILD`, `DD_SKIP_SEGMENT_INDEX_BUILD`) and neither's exit condition has been
  met; two read as permanent escape hatches; one is doubly-gated. Building an abstraction over them
  remains premature, but **deleting the two temporary ones is now on the critical path for two
  separate items** — § 3's remaining gap here, and § 2.6 of the format redesign — so it is no longer
  merely "cheaper to resolve first."
- **`CacheMetrics`/`GetHealth()` dead code.** Already a backlog item; § 6.2 would make the
  metrics plumbing collapse along with the five caches, but the wire-it-up-or-delete-it
  decision is separate.

---

## 9. Measurement status — what is settled and what is not

Closed since this doc was written — see
[cache-redesign-measurements.md](cache-redesign-measurements.md). The run-level multiplier turned
out to be derivable statically rather than needing an instrumented run, because every analyzer runs
by default: `CreateAnalyzers()` takes no filter and the `AnalysisProfile` tiers are gone.
(Analyzer selection *does* exist — `AnalyzerFilterService` honours `--include-analyzers` /
`--exclude-analyzers` — but it is opt-in and empty by default, so the count below is the
default-configuration count and also the worst case.)

**≈20 full-column opens per run × 68.8 ms ≈ 1.38 s of redundant checksum verification**, rising to
≈8.2 s on the 27.5 GB dump's cache. Two structural surprises fell out of that count, both in
[measurements § 6](cache-redesign-measurements.md):

- `HeapIndexScanDispatcher:441` opens a range enumeration **per worker** (8 on an 8-core box, 32 on
  a 32-core one), and `TryOpenColumns` checksums the *whole* section regardless of the range asked
  for — so the parallel scan hashes 2.9 GB to read 365.5 MB of disjoint ranges. This gets worse on
  bigger machines.
- Four analyzers spend a full 68.8 ms open on `.Any()` purely to test whether a disk index exists,
  then immediately re-open to enumerate. The correct guard (`hc.TryGetHeapIndex(out _)`) is already
  used by three other call sites in the same codebase.

What remains untested:

- **Whether one shared `MemoryMappedFile` across concurrent analyzers performs the same as
  today's per-open mappings.** This matters more than it did before § 6.2 surfaced the per-worker
  opens, since the shared session must serve 8+ concurrent readers.

Now settled: § 6.3's column-projection win is real but secondary — verification, not page-fault
I/O, dominates a warm-cache open (68.8 ms vs. 34.8 ms of actual scan), so § 6.1 should land first
and § 6.3's remaining benefit should be re-measured afterwards rather than assumed to be the
full 36%.
