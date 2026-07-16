# Clean-Slate Cache Redesign (Unconstrained)

**Status:** this design shipped as Tier 2 of
[15-ImplementationRoadmap.md](15-ImplementationRoadmap.md) — single container
file, atomic write, columnar layout, mmap reader, content-addressed key, and
corruption resilience are all done. Read this doc for *why* each piece is
shaped the way it is; read 15 for *what's actually built* vs. deferred
(schema-driven codegen, analyzer-result caching, telemetry, redaction,
secondary indices, concurrent-writer locking are all still design-only).

Companion to [13-CacheArchitectureReview.md](13-CacheArchitectureReview.md). That
review evaluates the current implementation and recommends an incremental path
(finish `IndexHeader` unification, delete the memory writer, ship a manifest).
This doc answers a different question: **if none of the current files were a
constraint, what would the cache look like, and why is it better?** It's a
design proposal, not a migration plan — see
[Relationship to the incremental plan](#relationship-to-the-incremental-plan)
for how the two connect.

## The core idea

Replace ten independently-formatted files (`ObjectIndex.bin`,
`TypeAggregateIndex.bin`, `RootIndex.bin`, `HandleSnapshot.bin`,
`TaskIndex.bin`, `EventCandidateIndex.bin`, `LargeObjectIndex.bin`,
`LohFreeBlockIndex.bin`, `PartialRefEdgeIndex.bin`, `StringDedupIndex.bin`,
plus a `.meta.json` sidecar) with **one container file**, written by **one
writer**, in **columnar layout**, produced in **one heap pass**.

```
<dump>.dumpindex/cache.bin
```

instead of

```
<dump>.dumpindex/{ObjectIndex,TypeAggregateIndex,RootIndex,HandleSnapshot,
                   TaskIndex,EventCandidateIndex,LargeObjectIndex,
                   LohFreeBlockIndex,PartialRefEdgeIndex,StringDedupIndex}.bin
                  + meta.json
```

Four changes compound to get there, each solving a distinct problem the
current design has:

| Change | Problem it removes |
|---|---|
| Single container + table of contents | "cache-hit path only validates 2 of 10 files" (review Finding E) becomes impossible — completeness is a property of one file, not a coordination problem across ten |
| Atomic write (`.tmp` + rename) | "disk-full mid-write" and "no manifest" (review Finding E, Caveat 3) become the same solved problem instead of two separate ones |
| Columnar (struct-of-arrays) layout | Type-aggregation and size-histogram scans currently read `Address+MethodTable+Size` interleaved even though most consumers only touch one or two of the three fields per pass |
| Single writer, no memory/disk branch | Root cause of review Finding A (five historical bugs from two hand-written implementations drifting) |

## File layout

```
┌─────────────────────────────────────────────┐
│ FileHeader (64 bytes, fixed offset 0)        │
│   Magic "DDCACHE1"  (8)                      │
│   FormatVersion     (4)                      │
│   DumpContentHash   (32)  ← see below         │
│   SectionCount      (4)                      │
│   TocOffset          (8)                      │
│   Reserved            (8)                      │
├─────────────────────────────────────────────┤
│ Table of Contents (SectionCount × 32 bytes)  │
│   SectionId (4) | Offset (8) | Length (8)    │
│   | RecordCount (8) | Checksum (4)           │
├─────────────────────────────────────────────┤
│ Section: Objects.Address[]      (columnar)   │
│ Section: Objects.MethodTable[]  (columnar)   │
│ Section: Objects.Size[]         (columnar)   │
│ Section: Objects.Generation[]   (columnar)   │
│ Section: TypeAggregates[]                    │
│ Section: Roots[]                              │
│ Section: Handles[]                            │
│ Section: Tasks[]                              │
│ Section: EventCandidates[]                   │
│ Section: LargeObjects[]                       │
│ Section: LohFreeBlocks[]                      │
│ Section: StringDedup (blob + offset table)   │
└─────────────────────────────────────────────┘
```

- The TOC is a flat array, not a nested format — any section can be added,
  removed, or resized without touching the others, because every offset is
  absolute and independently addressable. Adding a new analyzer's index
  (e.g. a future `PartialRefEdgeIndex` successor) is "append a TOC entry and a
  section," not "invent a new file with its own header."
- `SectionId` is a stable enum, not a filename — sections can be reordered on
  write without breaking readers, and a reader that doesn't recognize a
  `SectionId` (older client, newer cache) skips it via `Offset`/`Length`
  instead of failing to parse.
- Write path: build the whole file under `cache.bin.tmp`, `FileStream.Flush(true)`,
  then `File.Move(..., overwrite: true)` (atomic on both Windows NTFS and
  POSIX). A reader either sees the old complete file, the new complete file,
  or nothing — never a partial file. This is strictly stronger than the
  planned `CacheManifest.bin` (review recommendation #5), because a manifest
  checked *next to* N files can itself go stale relative to them; a single
  file has no "next to" to drift from.

## Columnar object index

Today `ObjectIndex.bin` is array-of-structs: each 24-byte record is
`Address(8) | MethodTable(8) | Size(8)` written and read together, matching
the in-memory `HeapEntry` struct. That's the right shape for the *writer*
(one record produced per object, in the heap-scan hot loop) but not for every
*reader*:

- `TypeAggregateIndexReader` and analyzers that classify by `MethodTable`
  touch every `MethodTable` value but not every `Address`/`Size`.
- The size-histogram pass (`SizeBucketHelper`) touches every `Size` but
  nothing else.
- Root-path / graph traversal touches `Address` for lookups but not `Size`.

Columnar layout — `Address[]`, `MethodTable[]`, `Size[]`, `Generation[]` as
four separate contiguous arrays instead of one interleaved array — means each
of those passes reads only the bytes it needs, sequentially, with no stride.
On a 25GB heap with ~500M objects that's the difference between reading
12GB (all three fields) and reading 4GB (`MethodTable[]` alone) for a
type-classification pass. It also compresses better if compression is ever
added later (homogeneous `MethodTable` values cluster; interleaved records
don't), though compression itself is out of scope here.

The write side still produces one `HeapEntry` per object from the heap scan
(that doesn't change — ClrMD's enumeration order is what it is), but instead
of serializing each `HeapEntry` immediately, the writer appends to four
`ArrayPool`-backed columnar buffers and flushes each column to its own
section independently. This is a batching change, not an architecture change,
and keeps the "stream, never materialize the full heap" rule intact — each
column buffer is bounded and flushed on the same cadence the current
per-segment scratch-file writer already uses.

## Reader: memory-mapped, not `FileStream` + `ArrayPool`

`ObjectIndexReader` today hand-rolls batch reads with `ArrayPool<byte>` and
carry-over logic across `FileStream.Read` calls — [13](13-CacheArchitectureReview.md)
correctly judges this the right call *for the current format*, since
per-record `ReadAtLeast` would be measurably slower at 100M+ record scale.

With a columnar, single-file format, `MemoryMappedFile` replaces both the
`FileStream` and the `ArrayPool` batching logic:

```csharp
using var mmf = MemoryMappedFile.CreateFromFile(cachePath, FileMode.Open);
using var accessor = mmf.CreateViewAccessor(addressSectionOffset, addressSectionLength);
ReadOnlySpan<ulong> addresses = MemoryMarshal.Cast<byte, ulong>(
    accessor.SafeMemoryMappedViewHandle.AsSpan(...));
```

A column is just a contiguous typed span over mapped memory — no read loop,
no carry-over, no pooled buffer to return. The OS page cache does the
readahead/eviction work that `ArrayPool` batching currently does by hand, and
random-access consumers (root-path lookups jumping to an arbitrary object by
address) get free OS-level caching instead of needing their own index. This
removes an entire category of hand-written buffer-management code, at the
cost of one thing worth flagging honestly: **memory-mapping a 25GB+ file on
Windows needs validation against 32-bit address space concerns (N/A here,
.NET is 64-bit-only for this workload) and against WSL2/network-share
mmap semantics**, which should be a benchmarked spike before committing, not
assumed to be free.

## Schema-driven writer/reader parity

Review Finding D notes three incompatible hand-written header formats today,
and Finding A notes ~150 lines of independently hand-written, drifting logic
between the two writers. Both are instances of the same root problem: nothing
enforces that the code producing bytes and the code consuming bytes agree.

A clean-slate version defines each section's record layout **once**, as a
small source-generator input (a `[BinaryRecord]`-attributed struct, or even
a `.g.cs` T4/Roslyn generator keyed off a single schema description), and
generates both the writer's serialization and the reader's deserialization
from it. Concretely:

```csharp
[BinaryRecord(SectionId.TypeAggregates)]
internal readonly partial struct TypeAggregateRecord
{
    public readonly ulong MethodTable;
    public readonly int ModuleId;
    public readonly long Count;
    public readonly ulong TotalSize;
    // ...
}
```

The generator emits `WriteTo(Span<byte>)` / `ReadFrom(ReadOnlySpan<byte>)`
for the struct, and a `RecordSize` constant used by the TOC. Adding a field
becomes "add it to the struct, bump `FormatVersion`" — a compile-time-checked
change — instead of "remember to update the writer, the reader, and keep them
byte-for-byte in sync by hand," which is exactly how `StringDedupIndex.bin`'s
bespoke 12-byte header and `ObjectIndex.bin`'s divergent 24-byte header
happened in the first place.

## Content-addressed cache key

Today, cache validity is path + mtime (`TypeAggregateIndexReader`'s 32-byte
"ExtraHeader" identity stamp). That's wrong in one specific, real scenario:
a dump copied or moved to a new path with its mtime preserved (common when
dumps are pulled from a share or CI artifact store) silently reuses a cache
built from a *different* file if the new file happens to collide on size —
or, more commonly, correctly invalidates on a benign rename even though nothing
about the dump's content changed, forcing an unnecessary full re-index.

Replace the identity stamp with a fast content hash: dump file size + a
hash of the first/last 1MB + a few sampled interior windows (not a full
SHA-256 of a 25GB file — that would itself dominate load time). Store it in
`FileHeader.DumpContentHash`. This makes the cache key what it should
semantically be — "the dump's content," not "the dump's location" — for
roughly the cost of a few megabytes of extra I/O on cache-check, which is
noise next to the multi-gigabyte heap scan a cache miss would trigger anyway.

## Single writer, always on, no threshold

The current `HeapIndexCache.SelectPrebuildMode` 4GB threshold and the
resulting `MemoryBackedObjectIndexWriter`/`DiskBackedObjectIndexWriter` split
disappear entirely — not deferred to a later phase, not kept as a fallback.
Every dump, regardless of size, goes through the same columnar writer.
This is the review's Finding A fix taken to its conclusion: if the two
writers can never diverge because there is only one, the entire "disk vs.
memory discrepancy" test suite category (currently the project's best test
suite, per 13's "what's working well") becomes structurally unnecessary
rather than something to keep maintaining forever. The tests can be deleted,
not just left passing.

The one caveat 13 already raises (Caveat 2: measure small-dump latency)
applies identically here and isn't solved by this redesign — it should still
be benchmarked before shipping unconditional disk writes for tiny dumps.

## Derived data instead of precomputed satellite files

Review Finding B surfaces a deeper issue than a delegation bug:
`RootIndex.bin`'s record format has no field for a root's description string,
so `GetRootDescription` cannot work in disk mode no matter how the facade
code is fixed — the binary format itself is missing the data.

Rather than growing `RootIndex.bin`'s record to carry a description (more
bytes on disk for every root, most never queried), a clean-slate cache treats
descriptions as **derived, on-demand data**: the cache section stores just
`(TargetAddr, RootAddr, Kind)`, and `GetRootDescription(address)` — a
low-volume, request-driven call, not a hot path — re-walks
`heap.EnumerateRoots()` filtered to the one requested address at call time.
This generalizes: the cache's job is to make the *expensive, whole-heap*
pass (the single enumeration) cheap to have already done, not to
pre-materialize every string anyone might ever ask for. Anything cheap to
compute from a single address on request belongs in the analyzer/facade
layer, not baked into the binary format — it keeps the on-disk schema
smaller and avoids the "extend the format every time someone needs one more
field" pressure that produced three incompatible headers in the first place.

## Analyzer-result caching, not just index caching

Everything above caches the *heap index* (Phase 1). Nothing caches Phase 2:
expensive analyzers (leak detection, root-path BFS over suspect sets) re-run
in full on every invocation even when neither the dump nor the analyzer
config changed since the last run — a common pattern in interactive triage
("run leak detection, look at output, run it again with a different type
filter").

Don't fold this into `cache.bin`. That file's atomicity story (single
write-once-per-build, immutable, atomic rename) is exactly what makes it
simple to reason about; reopening it for append every time an analyzer runs
would reintroduce the "who writes when" problem this whole redesign exists
to remove. Instead, a second, independent artifact:
`.dumpindex/analyzer-results/<AnalyzerName>.<ConfigHash>.bin`, keyed on
`(FileHeader.DumpContentHash, AnalyzerName, AnalyzerSchemaVersion, ConfigHash)`.
Same TOC/section/checksum machinery as `cache.bin`, just a much smaller file,
written independently per analyzer so a stale or corrupt result cache for one
analyzer can't invalidate another's, and so this file's higher write frequency
(once per distinct config, potentially every run) never touches the
expensive-to-build index file at all.

Only worth wiring up for analyzers whose `AnalyzeAsync` cost is dominated by
graph traversal or repeated root-path BFS, not ones that are already a cheap
pass over `TypeAggregateIndex`. `AnalyzerDomainResult` needs its own
`SchemaVersion` for this to be safe — the same "no reader supports more than
one version" policy from the section below applies to it, otherwise a stale
serialized result silently deserializes into a result shape the current
analyzer no longer produces.

## Corruption resilience and format-version migration

Two related but distinct failure modes need separate handling, and today's
design conflates them:

- **Torn/partial writes** (process killed mid-write) — solved by the atomic
  rename in the container-file section above: readers only ever see a
  complete file or no file.
- **Bit rot / truncation / tampering after a successful write** (disk error,
  a copy that got cut short, a hand-edited file) — atomicity doesn't help
  here, because the file *is* complete by the OS's accounting, just wrong.
  This needs a checksum. Store a `Checksum32` (xxHash or CRC32C — pick one
  that's fast enough to run on every section without becoming the new
  bottleneck) per TOC entry, validated lazily the first time that section is
  actually read, not eagerly for the whole file on open — a query that only
  touches `RootIndex` shouldn't pay to checksum `ObjectIndex` too.

On any failure of either kind — bad magic, `FormatVersion` mismatch, or a
checksum miss — the reader's response is always the same: treat it exactly
like a cold cache. Log once at debug level (a rebuild here is routine, not
exceptional, so it shouldn't look like an error to the user), delete or
ignore the bad file, rebuild, atomically replace it. No partial-repair path,
no "read what we can." A rebuild costs the same as a first run, so there's
no correctness or performance reason to special-case corruption beyond
"detect it and start over."

The version-mismatch case doubles as the migration policy: **no reader ever
supports more than one `FormatVersion`.** Every schema change is a hard
break, not a migration to write compatibility code for. The cost is that
bumping `FormatVersion` invalidates every cache on disk everywhere at once;
that's deliberately acceptable, because the alternative — carrying N old
readers forward indefinitely — is exactly the kind of accreted complexity
this redesign is trying to avoid, and is the same failure mode that produced
three incompatible per-file headers in the current design.

## Concurrent writers

Nothing above prevents two processes from analyzing the same dump at once —
a CI matrix job, or a user re-running the tool while a first run is still
mid-scan. Atomic rename already makes this *safe* (whichever writer finishes
last wins, readers never see a torn file), but not *efficient*: two
processes independently doing the same multi-gigabyte heap scan is exactly
the wasted work caching exists to avoid.

Treat this as a best-effort optimization, not a correctness mechanism. A
lock file (`cache.bin.lock`, created with `FileMode.CreateNew`, which throws
if a live one exists) lets a second process detect an in-progress build and
either wait (short poll with backoff) or just proceed independently if the
lock looks stale (mtime older than a generous timeout, covering a crashed
holder). Re-check `cache.bin`'s freshness after any wait, in case the first
writer finished. If the lock mechanism itself fails for any reason, fall
back to "both processes build independently" — same outcome as today, just
slower, never wrong.

## Sensitive data in the cache

The cache inherits its permissions from whatever directory it's created in —
incidental, not enforced. If the dump comes from a customer or production
environment, `.dumpindex/` now holds a second copy of some of the same
sensitive material (string previews, type names, root descriptions) as the
dump itself, potentially left behind after the original dump is deleted per
retention policy. Two concrete gaps to close, not just note:

- **Permissions**: set the cache directory's ACL explicitly at creation time
  rather than relying on inheritance — mirror the source dump's owner-only
  permissions (Windows: restrict to the creating user; POSIX: `0600`/`0700`
  equivalent) instead of whatever the parent directory happens to grant.
- **String redaction**: string previews are the highest-risk section — live
  heap strings routinely contain emails, tokens, or connection strings that
  have nothing to do with the leak or crash being investigated. Add an
  opt-in `--redact-strings` mode (pattern-based stripping of common secret
  shapes, or a hard max-length truncation) applied at cache-write time.
  Off by default, since it reduces analysis fidelity and most local
  debugging doesn't need it — but it should exist before the portability
  idea below ships, since export is exactly the moment this stops being a
  local-machine concern.

## Cache portability for CI and distributed triage (optional)

A side effect of collapsing ten files into one content-addressed artifact:
`cache.bin` becomes small (metadata only) relative to the dump it was built
from, and self-describing (the content hash proves which dump it matches).
That makes it viable as something other than a purely local, throwaway
file — e.g., a CI job that captures a crash dump, indexes it once, and
uploads just `cache.bin` as a build artifact; a developer investigating
later runs `dumpdetective analyze --cache-only cache.bin` and browses
findings without ever pulling down the multi-gigabyte original.

This is deliberately narrower than the "no cross-process cache sharing or a
cache server" exclusion below — there's still no shared mutable service, no
live coordination between processes. It's import/export of an immutable
artifact, the same trust model as archiving a build log. It should ship
*after* the redaction option above exists, not before, since leaving the
originating machine is precisely the scenario where an unredacted string
preview turns into an accidental data leak.

## Secondary indices for query pushdown

`QueryEngine` today has no fast path for "every object of type X" — that's
a linear scan of `ObjectIndex`'s `MethodTable[]` column, since
`TypeAggregateIndex` only stores per-type totals and one `SampleAddress`,
not the full set of instances. For large heaps with a hot suspect type,
this is the difference between a query and a re-scan.

A derived secondary section — `MethodTable → contiguous range of object
indices`, effectively a counting-sort bucketing of the already-materialized
columnar arrays — would make that query a direct lookup. The key design
constraint: build it as a **second, cheap pass over the columnar arrays
already sitting in `cache.bin`**, not a second ClrMD heap enumeration — the
expensive part (walking the actual heap) already happened during the index
build, so this is CPU-bound array sorting, not I/O-bound dump reading.

Same append-after-the-fact tension as analyzer-result caching applies here
too — don't write it back into `cache.bin` after the fact. Keep it a
separate derived-index file, keyed on the same content hash, built lazily
the first time a per-type enumeration query actually needs it. This keeps
the Phase 1 build itself unchanged (single pass, minimal metadata) and only
pays the extra cost for workloads that use it.

## Cache telemetry

Beyond wiring the currently-dead `CacheMetrics` into a `--cache-status`
surface (13's recommendation #7), emit one structured line per run:
`cache: hit | miss (reason=absent|version-mismatch|checksum-fail|
content-hash-mismatch) | built in Xms | N sections | Y bytes`. Cheap, and it
turns "is caching actually paying off" from a guess into something
observable — a CI pipeline silently rebuilding every run because a temp
path changes underneath it would be invisible today and obvious with this
line. It's also the input needed to judge, with data instead of intuition,
whether analyzer-result caching or secondary indices above are worth their
added complexity for a given workload rather than shipping them speculatively.

## Relationship to the incremental plan

**Note: not what happened.** The sequencing below was the original intent,
but [15](15-ImplementationRoadmap.md) deliberately went straight to the
container format instead — Tier 0 (correctness bugs) shipped first as
planned, but the manifest/`IndexHeader`-unification step was skipped rather
than shipped-then-replaced, since it would have been pure throwaway work.
Left here for the original reasoning, which was sound at the time it was
written.

This is deliberately a bigger bet than [13](13-CacheArchitectureReview.md)'s
recommendation, and the two are not both "do this now" — they're sequential:

1. **Ship the incremental plan first** (13's prioritized list #1-9,
   essentially doc 12's phases). It fixes the five live bugs, deletes the
   memory writer, and gets the codebase to "one writer, mostly-unified
   headers, a manifest" — which is most of this redesign's *value*
   (single source of truth, atomic completeness) for a fraction of its
   *risk* (no new container format, no mmap, no code generation to build
   and validate).
2. **Treat this doc as the target for a second, separate pass**, undertaken
   only if profiling after step 1 shows the ten-separate-files structure or
   the array-of-structs layout is an actual bottleneck — not on the
   assumption that it will be. The incremental plan's single writer already
   removes the bug-generating duplication; the container format and columnar
   layout are throughput/architecture-purity improvements on top of a
   codebase that will already be correct.

Skipping straight to this design without step 1 would mean debugging a new
binary format, a new mmap-based reader, and a source generator all at once,
on top of the same correctness bugs 13 already found — strictly worse odds
of landing a working result.

## What this doesn't try to solve

- **No compression.** Columnar layout makes it a cheap follow-on (see above),
  but it's a separate, independently-measurable decision with its own
  CPU/IO tradeoff — not bundled in here.
- **No cross-process cache server or live shared/mutable cache.** Still a
  one-shot CLI tool per [13](13-CacheArchitectureReview.md)'s "explicitly not
  recommending" section; nothing here changes that assumption. The one
  adjacent idea this doc does raise — exporting the immutable `cache.bin` as
  a build artifact for CI/distributed triage — is import/export of a static
  file, not shared infrastructure; see "Cache portability" above.
- **No change to the in-process `Cache/` facade's sub-cache decomposition**
  (`HeapIndexCache`, `StatisticsCache`, `RootCache`, etc.) — that's an
  in-memory API-shape concern, orthogonal to the on-disk format it's backed
  by, and 13 already judges it sound.
