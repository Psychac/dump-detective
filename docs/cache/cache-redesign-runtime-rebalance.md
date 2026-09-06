# Cache Redesign — Runtime & Memory Rebalance

The format redesign ([cache-format-clean-slate-redesign.md](cache-format-clean-slate-redesign.md),
[cache-implementation-clean-slate-redesign.md](cache-implementation-clean-slate-redesign.md)) took
`cache.bin` from 1,398.3 MiB to 342.50 MiB on the reference dump — 24.5% of where it started, with
compression (v9) still unwritten. That result stands.

What it did not do is cost the other two axes. Every number in
[cache-redesign-measurements.md](cache-redesign-measurements.md) is a **byte count**. The single
wall-clock figure in the whole series (§7: 51.7 s → 51.4 s) was measured for a different purpose —
the per-open checksum memoisation — and was explicitly reported as inside run-to-run noise. Peak
memory was never measured at all.

So the redesign optimised one axis with instrumentation and moved the other two blind. This document
covers (A) the measurement that closes that gap, and (B) the remediation plan the measurement gates.

**Units:** MiB = 1024², matching the format docs. Elapsed times in seconds.

---

## 0. Environment of record

Every number in this document is from one machine and one dump. Both matter, because the headroom
argument below is a function of installed RAM.

| | |
|---|---|
| Machine | Windows 11 Pro 26200, 8 logical cores, **15.7 GiB RAM** |
| Volume | `D:` — 277 GB, 79 GB free. Dump, caches and scratch all colocated here |
| Runtime | .NET SDK 10.0.400, Release configuration |
| Reference dump | `D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp` |
| Dump size | 3,350.3 MB (3.3 GB) |
| Heap | 14,620,162 objects, 14,003 types |
| Reachable graph | 6,686,490 nodes, 17,367,740 edges (recorded in the v8 commit) |
| Current `cache.bin` | 342.5 MB on disk |

15.7 GiB is the number that turns §B.1 and §B.4 below from an inefficiency into a problem. It is also
why the 27.5 GB dump's projected figures are stated even though this measurement does not cover it —
if the projections hold, that dump no longer fits.

---

## Part A — Measurement

### A.1 Baseline commit: `d1dc4dcc`

The redesign landed as a chain. Chronological order, oldest first:

| Commit | What it changed | Bytes written? |
|---|---|---|
| `7ae6bf58` | §6.4 — per-open checksum memoisation | no (read path only) |
| **`d1dc4dcc`** | **Traced the forward index; docs only** | **no — BASELINE** |
| `ca938bf4` | Stop persisting the write-only forward-edge index | **yes** — first byte change |
| `89e64b7c` | `MethodTable` dictionary encoding (v5) | yes |
| `a4ad874d` | Report-nondeterminism doc | no |
| `1c9d0b5c` | Narrowed `ObjectSizes` column (v6) | yes |
| `ee022330` | Block-delta address columns + section manifest (v6) | yes |
| `8138e1b0` | v6 address-column oracle test | no |
| `ce080bcb` | `EnumerateRetainedSet` frequency measurement | no |
| `966bdeab` | Derive dominator child list on demand (v7) | yes |
| `916d3182` | True CSR reverse edge index (v8) | yes |
| `68b413ad` | Doc trim | no |

`d1dc4dcc` is the last commit before any change to what gets written. Picking it rather than
something earlier is deliberate on two counts:

- It **includes** `7ae6bf58`, so the checksum memoisation win is present in *both* arms and cancels
  out. A baseline before it would credit the redesign with a read-path improvement it did not make.
- It **excludes** `ca938bf4`, so the forward-index removal is charged to the redesign. That commit is
  part of how the file got from 1,398.3 MiB to 342.50 MiB, so it belongs on the same ledger as the
  1.3 GB → 400 MB result being explained.

`cache.bin` at `d1dc4dcc` should measure ≈1,398.3 MiB. If it does not, the baseline is wrong and
everything downstream is suspect — this is the protocol's first assertion, not an afterthought.

### A.2 Arms

Four cells, run strictly in this order, strictly one at a time:

| # | Arm | Cache state | What it isolates |
|---|---|---|---|
| 1 | baseline `d1dc4dcc` | cold (dir deleted) | full index build — the write path |
| 2 | baseline `d1dc4dcc` | warm (cell 1's cache) | analysis over the old format — the read path |
| 3 | current `HEAD` | cold (dir deleted) | full index build — the write path |
| 4 | current `HEAD` | warm (cell 3's cache) | analysis over v8 — the read path |

Cold and warm are separated because the two suspected regressions live in different places. The
reverse-index build (§B.1–B.3) is cold-path only and would be invisible in a warm run; the
per-record decode overhead (§B.5) is warm-path and would be swamped by build time in a cold run.

Baseline runs first in both pairs so that neither arm gets an advantage from a colder OS page cache.

**Isolation.** Neither arm touches the dump's colocated `.dumpindex/`. Each gets its own directory
via `--cache-dir`, both on `D:` so the two arms see identical storage characteristics:

```
D:\DUmps\_ddbench\baseline\<dump>.dumpindex\
D:\DUmps\_ddbench\current\<dump>.dumpindex\
```

The existing 342.5 MB colocated cache is left untouched throughout, and `D:\DUmps\_ddbench` is
deleted when the measurement is finished.

**Build isolation.** The baseline is checked out as a `git worktree`, not a branch switch, so `HEAD`
stays clean and both binaries exist simultaneously. Both built `-c Release`.

### A.3 Metrics per cell

| Metric | Source | Why |
|---|---|---|
| Wall clock | `Stopwatch` around the child process | the headline |
| **Peak private bytes** | `Process.PeakPagedMemorySize64` | **the memory headline** — private commit, excludes the mmap'd dump |
| Peak working set | `Process.PeakWorkingSet64` | reported for completeness; includes dump pages, so it is noisy |
| `cache.bin` size | file length after the run | confirms which format each arm actually wrote |
| Per-phase managed alloc | `DD_PERF_INDEX_MEMORY=1` on stderr | attributes a cold-run delta to a phase |
| Section open/verify tally | `DD_PERF_CACHE_SESSION=1` on stderr | attributes a warm-run delta to a section |

Peak private bytes is the metric that answers the question. `PeakWorkingSet64` on a run that
memory-maps a 3.3 GB dump is dominated by file-backed pages the GC never touched, and moves with
whatever else the OS is doing.

### A.4 Confounds, stated rather than fought

- **OS page cache.** The cold run pulls the dump into the standby list, so the warm run that follows
  reads it back for free. Both arms follow the identical cold→warm sequence, so the *comparison*
  holds even though neither warm number is a true cold-start figure. Not worth fighting with
  `EmptyStandbyList` on a 15.7 GiB machine.
- **n=1 on cold cells.** Cold rebuilds are minutes. The first pass is n=1 to establish magnitude. If
  a delta lands within ~10% it gets repeated before anything is concluded from it — §7 of the
  measurements doc is the precedent for not turning noise into a claim.
- **Warm cells n=3.** They are ~51 s, so repetition is nearly free, and the warm-path deltas under
  suspicion (§B.5) are small enough that n=1 could not resolve them.
- **Different formats do different work.** The arms are not doing byte-identical work — that is the
  point — but they must produce the *same analysis*. Report output is diffed between arms as a
  correctness gate; a runtime win from accidentally skipping work is not a win.

### A.5 Harness

`scripts/bench-cache-rebalance.ps1` (scratch, not committed unless it proves reusable). Per cell:
starts the CLI with `Start-Process -PassThru`, waits, then reads the peak counters off the retained
process handle before releasing it — Windows keeps them valid after exit as long as the handle is
open. Stdout/stderr tee to a per-cell log; the `[PERF]` lines are extracted afterwards.

---

## Part A-R — Results (measured 2026-09-06)

### A-R.1 Headline

**The runtime premise did not reproduce.** Cold rebuild got *faster*; warm analysis is unchanged.
Peak memory did rise, but by ~200 MB, not the ~1 GB an early n=1 pair suggested.

| Arm | `cache.bin` | n | Wall clock (median) | Peak private MB (median) |
|---|---:|---:|---:|---:|
| 1 · baseline cold `d1dc4dcc` | 1,398.3 MB | 6 | 105.5 s | 4,622.5 |
| 5 · fwd-index removed cold `ca938bf4` | 935.9 MB | 2 | 100.0 s | 4,703.1 |
| 3 · current cold `HEAD` | **342.5 MB** | 5 | **97.0 s** | **4,819.9** |
| 2 · baseline warm | 1,398.3 MB | 3 | 54.3 s | 3,051.4 |
| 4 · current warm | 342.5 MB | 3 | **53.0 s** | **3,039.1** |

| Path | Δ time | Δ peak private |
|---|---:|---:|
| **Cold** | **−8.5 s (−8.1%)** | **+197.4 MB (+4.3%)** |
| **Warm** | −1.3 s (−2.4%) — noise | −12.3 MB (−0.4%) — noise |

`cache.bin` at `d1dc4dcc` measured 1,398.3 MB, matching the format doc's stated starting point
exactly. The baseline is the right commit.

### A-R.2 The cold-path speedup is the forward-index removal, not the encodings

Arm 5 exists to split the two effects bundled into "current", and it splits them cleanly:

| Step | Δ time | Δ peak private |
|---|---:|---:|
| `d1dc4dcc` → `ca938bf4` (stop persisting forward index) | −5.5 s | +80.6 MB |
| `ca938bf4` → `HEAD` (v5–v8 encodings) | −3.0 s | +116.8 MB |

The v5–v8 encoding work — dictionary encoding, narrowed sizes, block-delta addresses, derived
dominator children, CSR reverse index — is **runtime-neutral to slightly positive** on the cold path
and free on the warm path. It did not cost time anywhere that this measurement can see.

### A-R.3 The memory delta is B.1, confirmed to within 5%

Predicted from `ReverseEdgeCsrBuilder`'s allocation shape at this dump's E = 17,367,740 edges and
R = 6,686,490 rows:

| Live simultaneously | MB |
|---|---:|
| `resolvedBuckets` — 2 × `int[E]` | 132.5 |
| `children` — `int[E]` | 66.3 |
| `degree` + `offsets` + `cursor` — 3 × `int[R]` | 76.5 |
| `sortedReachableAddresses` — `ulong[R]` | 51.0 |
| **Total** | **326.3** |
| Prior path (4 concurrent raw buckets) | ≈139 |
| **Predicted delta** | **+187** |

**Measured delta: +197.4 MB.** Prediction and measurement agree to 5%. B.1 is the mechanism; nothing
else material is in play on this dump.

### A-R.4 B.5 is dead — the streaming decode costs nothing measurable

Warm path is −1.3 s and −12.3 MB, both inside noise across n=3. The v6 per-record block-delta add,
size sentinel compare and `_typeDictionary` indirection do not show up. The read-traffic reduction
(24 → 8 bytes/record) evidently pays for them. §B.5 is closed with no action.

### A-R.5 Measurement quality — stated honestly

- **Peak private bytes is noisy at this scale.** Baseline cold spanned 4,103–4,773 MB across 6 runs;
  current spanned 4,662–5,147 across 5. The distributions overlap. The +197 MB median delta is real
  — it is corroborated independently by A-R.3's arithmetic — but it is *not* cleanly separated by
  the process-level metric alone. Peak committed heap depends on GC timing, which is nondeterministic.
- **The first cold run of all is ~7% slower** (137.8 s on a genuinely cold OS page cache, then
  111.0 s). All reported cells were run with the dump already resident. Both arms got identical
  treatment.
- **Ordering bias.** Arms were run in blocks, not interleaved. Current's cold times drift downward
  within its block (98.5 → 94.7 s), so some of the −8.5 s may be system warmup rather than the build.
  The direction of the runtime result is safe; the exact magnitude is not.
- **n=2 on arm 5.** Enough to attribute a direction, not enough to defend the ±MB split in A-R.2 to
  better than "roughly half each".
- `DD_PERF_INDEX_MEMORY` totals were checked and are *lower* in current (9.34 vs 9.70 GB allocated;
  reverse-index phase 3,084 vs 3,236 MB). Cumulative allocation is not residency — it moved the
  opposite way from peak, which is exactly why peak private was the metric of record.

### A-R.6 What this changes

The premise this investigation started from — "runtime got slower and memory went higher" — is
**half right, and the half that is right is smaller than it looked**. On the 3.3 GB reference dump:

- Runtime: no regression. Cold is 8% faster.
- Memory: +4.3% on cold, none on warm.

That removes the urgency from C.2 *for this dump*. It does not remove the finding: B.1's arithmetic
scales with E and R, and on the 27.5 GB dump those are ≈12.4× larger — ≈2.3 GB of extra residency on
a machine with 15.7 GiB of RAM. The case for the remediation is now a **large-dump headroom** case,
not a "we regressed the reference dump" case, and Part C is re-gated accordingly.

---

## Part B — What the code review already found

Derived from reading the shipped v6/v7/v8 code plus the node/edge counts in the v8 commit message.
**None of this is measured yet** — Part A exists to confirm or kill it. Ordered by expected size of
the regression.

Projections for the 27.5 GB dump scale by the ratio of raw reverse-edge bytes recorded in
measurements §2 (3,064.2 MB / 245.9 MB ≈ 12.4×), giving ≈191M edges over ≈83M reachable nodes.

### B.1 `ReverseEdgeCsrBuilder` holds every resolved bucket resident — cold path

[`ReverseEdgeCsrBuilder.cs:56`](../../src/DumpDetective.Analysis/Indexing/ReverseIndex/ReverseEdgeCsrBuilder.cs#L56)
allocates `ResolvedBucket[bucketCount]` and keeps all of it alive across the counting/filling
barrier at [line 93](../../src/DumpDetective.Analysis/Indexing/ReverseIndex/ReverseEdgeCsrBuilder.cs#L93).
The retired `ReverseEdgeSorter` loaded one bucket, sorted it, wrote it, and dropped it — bounded at 4
concurrent buckets by design. The replacement is bounded at *all* buckets.

| Component | Reference dump | 27.5 GB dump |
|---|---:|---:|
| `resolvedBuckets` (2 × `int[]`, 8 B/edge) | 139 MB | 1,532 MB |
| `children` (4 B/edge) | 69 MB | 766 MB |
| `degree` + `offsets` + `cursor` (12 B/row) | 80 MB | 996 MB |
| `sortedReachableAddresses` (8 B/row) | 53 MB | 664 MB |
| **Peak** | **≈341 MB** | **≈3.96 GB** |
| Prior (4 concurrent raw buckets) | ≈139 MB | ≈215 MB |

The type's own remarks name the choice — *"this array is kept resident across the barrier below
rather than discarded per bucket"* — and defend it as "a size cap, not a memory-safety requirement,
at the scale measured so far." The scale measured so far was zero.

### B.2 Resolution is two random binary searches per edge — cold path

[`ReverseEdgeCsrBuilder.cs:142-143`](../../src/DumpDetective.Analysis/Indexing/ReverseIndex/ReverseEdgeCsrBuilder.cs#L142-L143)
calls `ResolveRow` twice per edge, each an `Array.BinarySearch` over the full sorted reachable array.

| | Reference dump | 27.5 GB dump |
|---|---:|---:|
| Searches | 34.7M | 383M |
| Probes each (log₂ R) | ~23 | ~27 |
| Random reads | ~800M | **~10.3 billion** |
| Over an array of | 53 MB | 664 MB |

At `MaxDegreeOfParallelism = 4`. Nothing about this is cache-friendly: the buckets are hash-
partitioned by child address, so each bucket's addresses are scattered uniformly across the whole
reachable range.

This project has already measured this exact pattern and rejected it.
[`DiskBackedObjectIndexWriter.cs:1176`](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs#L1176)
carries a §10.8 note that Stage B replaced *"one random-access binary search per node"* with a sort +
sequential merge for exactly this reason, implemented in `ScratchFileObjectMetadataLookup.ResolveBatch`.
v8 reintroduced the abandoned pattern, per edge instead of per node, at roughly 5× the volume.

### B.3 The reverse CSR is built twice — cold path

This is the structural one, and it subsumes B.1 and B.2.

[`ReachableGraphWalker.WalkWithCsr`](../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs#L298)
already builds a complete reverse CSR — `revOffsets` / `revTargets`, exposed on
`ReachableGraphWalkResult` — from the same edge set, keyed by walk node id. In the same loop, at
[line 257](../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs#L257), it
streams every one of those edges to `ReverseEdgeExtractor`, which writes them to disk scratch at 16
B/edge so that Phase B can read them back, resolve them, and rebuild the identical structure keyed by
row instead of node id. `DominatorRowMapping.Compute` already computes the nodeId↔row mapping.

When Stage B runs — `buildCsr: buildStageB`, the normal path — the Phase A scratch write (278 MB on
the reference dump, ~3 GB on the 27.5 GB dump), the Phase B re-read, all the binary searches in B.2
and all the residency in B.1 are **redundant**. The persisted section can be produced by permuting
the in-memory `revTargets` through the row mapping: O(N + E), sequential, no scratch, no search.

`WalkWithoutCsr` has no in-memory CSR, so the existing path has to stay reachable for that case.

### B.4 `_fanoutPerBucket` is dead weight v8 left behind — cold path

[`ReverseEdgeExtractor.cs:30`](../../src/DumpDetective.Analysis/Indexing/ReverseIndex/ReverseEdgeExtractor.cs#L30)
is a `Dictionary<ulong,int>` per bucket holding one entry per distinct child address, updated inside
the bucket lock on **every** edge, and held for the entire heap walk.

Its only consumer is `GetStatistics()`, which fed `ReverseIndexMetadata.TotalEdgesRecorded`. v8
deleted that metadata section and stopped calling it. `GetStatistics()` now has **zero callers in
src or tests** — verified, not assumed.

At ~36 B per `Dictionary<ulong,int>` entry: ≈240 MB on the reference dump, ≈3 GB on the 27.5 GB dump,
held concurrently with the walk's own (necessary) `idMap` of the same cardinality — plus a dictionary
probe and insert per edge, inside a lock.

Free to delete. No format change, no behaviour change.

### B.5 Per-record decode on the streaming read path — warm path, unresolved

v6 added, per object, in
[`ObjectIndexReader.ZeroCopyColumnReader.FillBatch`](../../src/DumpDetective.Analysis/Indexing/ObjectIndexReader.cs#L191-L247):
a block-base add and array index for the address, a sentinel compare for the size, and a
`_typeDictionary[]` indirection for the MethodTable. Against that, it cut read traffic from 24 to 8
bytes per record.

Whether that is a net win depends on whether the loop is bandwidth-bound or CPU-bound, and the
`_typeDictionary` indirection (112 KB, randomly accessed) is the part most likely to hurt. **No
prediction offered** — cells 2 and 4 settle it. Listed here so it is not mistaken for an oversight.

### B.6 Ruled out — v7

`DominatorChildIndexReader`'s in-memory inversion is lazy, and measurements §15 found its only
consumer, `StaticRootLeakDetector`, called zero times on every real dump tested. It allocates
`int[R+1] + int[R] + int[R+1]` when it runs; it does not run. No action.

---

## Part C — Remediation, gated on Part A

Ordered by ratio of expected recovery to risk. **Every item here gives back zero bytes** — the file
stays at 342.50 MiB, and v9 compression remains available on top.

Each item states the gate that must be satisfied for it to be worth doing. If the measurement
contradicts the derivation, the item is dropped rather than argued.

> **Re-gated after Part A-R.** Runtime needs no remedy — there is no runtime regression to fix. The
> memory items stand, but their justification changed from "the reference dump regressed" to "the
> 27.5 GB dump has no headroom on a 15.7 GiB machine". C.4 is dropped outright.

### C.1 Delete `_fanoutPerBucket` and `GetStatistics` — ✅ SHIPPED

**Gate:** none. `GetStatistics()` had no production callers; this was dead code regardless of what
Part A showed.

Removed the dictionary array, the per-edge probe/insert in `RecordEdge` and `RecordEdgesBatch`, and
`ReverseEdgeExtractionStats` / `ReverseEdgeBucketStats` with it. The bucket lock stays — it still
guards the `BinaryWriter`.

`GetStatistics()` was not *entirely* dead: seven unit tests used it as their observation channel.
Rather than delete the coverage, those assertions were re-expressed against the raw scratch bucket
files via a new `ReverseEdgeBucketFileReader` helper. That is a strictly stronger test: the files are
the extractor's only real output — Phase B reads nothing else — so a counter could have agreed with
the assertions while the persisted bytes disagreed. Two assertions were added that the counter could
not express at all: that every edge sharing a child lands in exactly one bucket (the invariant
`ReverseEdgeCsrBuilder`'s lock-free passes depend on), and that concurrent writes never tear.
`RecordEdgesBatch` also gained direct coverage, which it previously had none of.

**Predicted −240 MB. Measured −688.3 MB (−14.3%).**

| Cold, reference dump | n | Wall clock | Peak private | Peak spread |
|---|---:|---:|---:|---:|
| baseline `d1dc4dcc` | 6 | 105.5 s | 4,622.5 MB | 670 MB |
| current `HEAD` | 5 | 97.0 s | 4,819.9 MB | 485 MB |
| **`HEAD` + C.1** | 3 | **94.8 s** | **4,131.6 MB** | **12 MB** |

The overshoot against prediction is real and has a plausible cause: the dictionary held ~240 MB of
live data, but it is also a large, steadily-growing, GC-visible structure being written on every
edge, so the committed heap had to carry it *plus* collection headroom. Removing it took out both.
The collapse in run-to-run spread from 485 MB to **12 MB** is the corroborating evidence — that
dictionary's collection timing was the dominant source of peak-memory variance in every earlier
measurement on this page, which is also why Part A's cold cells needed 5–6 samples to read.

Warm path unchanged, as expected for a build-path-only change: 53.2 s / 3,036.9 MB against `HEAD`'s
53.0 s / 3,039.1 MB.

**Net position against the pre-redesign baseline, after C.1:**

| Axis | `d1dc4dcc` | `HEAD` + C.1 | Change |
|---|---:|---:|---:|
| `cache.bin` | 1,398.3 MB | 342.5 MB | **24.5% of original** |
| Cold rebuild | 105.5 s | 94.8 s | **−10.1%** |
| Cold peak private | 4,622.5 MB | 4,131.6 MB | **−10.6%** |
| Warm analysis | 54.3 s | 53.2 s | −1.1 s (noise) |
| Warm peak private | 3,051.4 MB | 3,036.9 MB | −14.5 MB (noise) |

All three axes are now better than before the redesign. The +197 MB cold-memory regression Part A
found is not merely repaid — peak is 491 MB *below* baseline. Full test suite green (1,161 passed,
0 failed, 25 real-dump tests skipped by their env gate).

### C.2 Feed the persisted CSR from the walk's in-memory reverse CSR

**Gate:** ✅ met, but weaker than expected. Cold peak private is +197.4 MB, matching B.1's arithmetic
to 5% (A-R.3). At 3.3 GB that is a 4.3% regression and not on its own worth a structural change; the
justification is the 12.4× scaling to ≈2.3 GB on the 27.5 GB dump, which this measurement did not
cover. **Decision needed before starting** — see "Open question" below.

When `buildCsr` is true, skip Phase A's scratch write and Phase B entirely: permute
`ReachableGraphWalkResult.RevOffsets` / `RevTargets` from node-id space into row space using the
existing `DominatorRowMapping`, and hand the result to `ReverseEdgeContainerWriter` unchanged. Keep
the extractor + `ReverseEdgeCsrBuilder` path alive for `WalkWithoutCsr`.

This resolves B.1, B.2 and B.3 in one change, and removes 278 MB of scratch write + read-back on the
reference dump.

Correctness gate: the existing `ReverseEdgeCsrRealDumpTests` internal-consistency check
(`EnumerateChildCounts`' own total against the TOC record count) plus the live-heap cross-check must
both still pass, and the produced `ReverseEdgeOffsets`/`ReverseEdgeChildren` must be **byte-identical**
to what the current path produces on the reference dump. That equality is checkable directly and is
the acceptance criterion — not a sampled comparison.

### C.3 Bound `ReverseEdgeCsrBuilder`'s residency — fallback only

**Gate:** C.2 turns out to be impractical, *or* the `WalkWithoutCsr` path is shown to be exercised in
production. (Current reading says it is not, on the Stage-B path.)

Two passes over the scratch files instead of holding all resolved buckets: pass 1 resolves child rows
only and accumulates `degree`; pass 2 re-reads and fills. Additionally, build `offsets` in place over
`degree` and reuse it as the cursor, repairing the shift afterwards — that drops 8 B/row and 8 B/edge
of residency. Resolution cost is unchanged, so this is strictly the memory half of the problem.

Not worth doing if C.2 lands. Recorded so the option is not re-derived later.

### C.4 ~~Warm-path decode~~ — DROPPED

**Gate: not met.** A-R.4 measured the warm path at −1.3 s / −12.3 MB, both inside noise. There is
nothing to fix. Closed.

### C.5 Not in scope

- **Giving back format bytes.** Nothing above trades disk size for speed. If Part A shows a
  regression that *can only* be fixed by reverting an encoding, that is a separate decision with its
  own evidence, brought back rather than taken unilaterally.
- **v9 block compression.** Held last by prior decision (§7.1.1) and unaffected by any of this. It
  should be re-costed on both axes when it happens, using the harness Part A builds — which is the
  standing lesson from this whole exercise.

---

## Part D — The 27.5 GB dump, measured (2026-09-06)

Run on `HEAD` + C.1, one cold rebuild, single process, nothing else running.
`D:\DUmps\21-04\w3wp.exe_260421_175618.dmp`, 26,244 MB, 87,104,236 objects.

### D.1 It fits — with thin margin and visible thrashing

| | |
|---|---:|
| Wall clock | **1,310.5 s** (21.8 min) |
| **Peak private** | **13,276.3 MB (12.97 GB)** |
| Peak working set | 9,262.4 MB (9.05 GB) — capped by physical RAM |
| `cache.bin` | **2,418.1 MB** |
| Exit | clean, no OOM |

The 3.9 GB gap between peak private and peak working set is pagefile. System available memory bottomed
at **356 MB** during the reachability walk, and the walk's throughput visibly decayed while it was
there. The run survived on a 35.1 GB commit limit, not on RAM.

**Disk result confirmed at scale.** The produced `cache.bin` is 2,418.1 MB, byte-for-byte the same
size as the pre-existing v8 cache alongside the dump, whose v4 predecessor is still there as
`cache.bin.bak` at 9,423.7 MB. That is **9,423.7 → 2,418.1 MiB = 25.7%**, tracking the reference
dump's 24.5% closely. The redesign's size result holds at 8× the dump size.

### D.2 Part B's scale projection was 40% too high

| | Part B projected | Measured |
|---|---:|---:|
| Edges (E) | ~191M | **137,033,360** |
| Reachable rows (R) | ~83M | **58,339,936** |

The projection scaled by raw reverse-edge bytes from measurements §2, which over-counted. Re-deriving
B.1's residency at the real E and R:

| Live simultaneously in `ReverseEdgeCsrBuilder` | Size | Under C.2 |
|---|---:|---|
| `resolvedBuckets` — 2 × `int[E]` | 1.02 GB | removed |
| `children` — `int[E]` | 0.51 GB | kept — it is the output |
| `degree` + `offsets` + `cursor` — 3 × `int[R]` | 0.65 GB | `offsets` kept, rest removed |
| `sortedReachableAddresses` — `ulong[R]` | 0.43 GB | removed |
| **Total** | **2.62 GB** | **≈1.89 GB removed** |

So C.2 is worth ≈1.9 GB of a 12.97 GB peak (**−15%**), plus 2.04 GB of scratch write-and-read-back
that disappears entirely. It would take peak to ≈11.1 GB — enough to stop the run relying on the
pagefile, not enough to make it comfortable.

### D.3 The walk, not the builder, is the dominant resident consumer

Sampled mid-run, the process was already at **10.4 GB commit while still inside the reachability
walk** — before `ReverseEdgeCsrBuilder` had started. The builder took it from there to 13.0 GB.

That reorders the remaining work. `ReachableGraphWalker` holds, concurrently: a
`Dictionary<ulong,int>` `idMap` at 58.3M entries (≈2.0 GB), `ChunkedBuffer` `edgeFrom`/`edgeTo`
(≈1.1 GB), the `fwdTargets`/`revTargets`/`fwdOffsets`/`revOffsets` CSR arrays (≈1.6 GB), plus
`addresses`/`outDegree`/`isRoot`. Roughly 6 GB of the 13 GB peak is the walk.

Two consequences:

- C.2 is now **doubly attractive**, because the walk's `revOffsets`/`revTargets` are already paid for.
  Reusing them removes the builder's duplicate without adding anything — the memory is resident
  either way.
- The larger remaining lever is the walk itself, which is out of scope here and belongs in its own
  investigation. Noted, not pursued. §7.3 item 2 of the dominator integration doc already records a
  failed attempt (`DenseIdMap`, 2.6× slower with no peak win) — that history should be read first.

### D.4 Verdict

The current build fits the 27.5 GB dump on a 15.7 GiB machine. It does so by paging ~3.9 GB and
driving available memory to 356 MB, which is a real degradation, not a clean pass. C.1 already
removed ≈3 GB that a previously-successful build was carrying, so today's build has meaningfully more
headroom than the one that produced the `cache.bin` sitting next to that dump.

**C.2 is justified** — 1.9 GB off peak and 2.04 GB of scratch I/O gone, on a run demonstrably short
of memory — but it is a margin improvement, not a rescue. It does not need to be done urgently, and
it should not be sold as making large dumps comfortable. Only attacking the walk would do that.

### D.5 Full instrumentation record

Recorded here because the run's logs live in a session scratchpad that does not survive, and a
27.5 GB cold rebuild is expensive enough that nobody should have to repeat it to recover these.

**Scan shape** (`DD_PERF_INDEX_MEMORY=1`): 63 segments, DOP=8, 87,104,236 objects, per-worker
columnar chunk buffers peaking at 100.0 MB concurrent (524,288 entries/column), zero leaked-live.
For comparison the reference dump was 8 segments, DOP=4, 12.5 MB concurrent.

**Cumulative allocation by phase** — total 116.16 GB, 1,431 B/object. Note this is bytes *allocated*,
which the GC recycles continuously; it is not residency, and on this page it has repeatedly moved in
the opposite direction from peak (see A-R.5):

| Phase | Allocated | % |
|---|---:|---:|
| parallel heap scan (incl. edge extraction) | 51,227.9 MB | 43.1% |
| satellite sections | 40,671.3 MB | 34.2% |
| reverse index (CSR build + write) | 19,333.8 MB | 16.3% |
| reachability walk | 5,468.8 MB | 4.6% |
| forward index (sort + write) + TypeAggregates | 2,239.3 MB | 1.9% |
| columnar scratch concatenation | 4.4 MB | 0.0% |

**GC**: gen0 = 16,644, gen1 = 7,464, gen2 = 108. Managed heap at exit 12,153.5 MB.

**Wall-clock breakdown** (total 1,310.5 s):

| Phase | Time |
|---|---:|
| Load dump (DAC 448 ms) | 0.7 s |
| **Scan + index heap** | **1,105.3 s** |
| ↳ indexing heap | 335.5 s |
| ↳ computing exact dominator tree (tracing heap graph) | 213.7 s |
| ↳ shared heap index scan | 127.5 s |
| ↳ computing exact dominator tree (resolving node metadata) | 46.7 s |
| ↳ **building reverse-index CSR** | **39.6 s** |
| ↳ flushing reverse-index edges | 2.2 s |
| ↳ flushing forward-index edges | 1.0 s |
| Run analyzers — of which `building publisher registry` (EventLeak) | 127.3 s |
| Build report | 81.4 s |

The reverse-index CSR phase is **39.6 s of 1,310.5 s — 3.0% of the run**, which is what caps C.2's
runtime upside regardless of how much work it removes. Inside it: 32.6 s resolving 53 buckets at
DOP 4 (per-bucket 1.8–5.8 s), 7.0 s prefix-sum + fill of 137,033,360 child entries, 8.5 s writing
both sections into the container.

**Checksum session** (`DD_PERF_CACHE_SESSION=1`): 11 container opens, 131 section opens, 24 verified
/ 107 skipped by memoisation, 2,386.9 MiB hashed. `Roots`, `Handles` and `LargeObjects` are each
still verified twice — the residual §6.1 gap, unchanged at this scale.

### D.6 Five sections written and never read on this dump

The same check that condemned the `ForwardEdge*` sections in measurements §9, re-run at 27.5 GB on a
full default analyzer set. Sections the run **never touched**:

| Never read | Note |
|---|---|
| `TypeAggregates` | read on the reference dump, not here |
| `StringDedup`, `StringDedupMeta` | read on the reference dump, not here |
| `ObjectAddressOverflow` | genuinely empty — no address delta escaped at 4 bytes |
| `SectionManifest` | v6 rider; never read on either dump |
| `ForwardEdgeBuckets/Directories/Metadata` | not written since `ca938bf4` |
| `ReverseEdgeBuckets/Directories/Metadata` | not written since v8 |
| `DominatorChildOffsets/ChildAddresses` | not written since v7 |
| `Objects`, `EventCandidates` | never written |
| `RootStackThreadAttribution` | known — §12.2's report wiring is deliberately deferred |

`TypeAggregates`, `StringDedup` and `StringDedupMeta` being read on the 3.3 GB dump but not the
27.5 GB one is the interesting part: it means the touch set is **analyzer-path dependent, not
format dependent**, so "never read" from a single run is not sufficient grounds to stop writing a
section. That is a correction to how measurements §9's method should be applied — §9 happened to be
safe because the `ForwardEdge*` sections had no reader at all, which a source search confirmed
independently of any run. `SectionManifest` is the one entry here that looks like a genuine §9-style
candidate, and it is small enough not to matter.

Not actioned. Recorded so the next person to run this check has a second data point rather than
re-deriving one.

### D.7 What was *not* measured

Only the **after** arm (`HEAD` + C.1) was run on this dump. There is no baseline arm, so:

| Axis | Before | After | Status |
|---|---|---|---|
| Disk index size | 9,423.7 MiB (`cache.bin.bak`, format v4) | 2,418.1 MiB | ✅ both measured |
| Cold runtime | — | 1,310.5 s | ⚠ after only |
| Peak memory | — | 13,276.3 MB | ⚠ after only |

The disk comparison is real: `cache.bin.bak` is a format-v4 container sitting next to the dump, and
the run reproduced the v8 size exactly. Runtime and peak memory have **no before figure on this
dump** — that arm was deliberately skipped, since Part A had already settled "did we regress" on the
reference dump and the 27.5 GB question was only "does the current build fit".

Anyone wanting the full 3×2 grid needs one more cold rebuild at `d1dc4dcc`. Expect it to be slower
(it persists the forward index) and to peak meaningfully higher (it carries the ~3 GB fanout
dictionary C.1 removed, at this dump's 58.3M distinct children) — plausibly 16 GB+ against a 35.1 GB
commit limit and 15.7 GiB of RAM. That is a real OOM risk on this machine, which is why it has not
been run speculatively.

---

## Open question — ~~C.2, after C.1~~ RESOLVED by Part D

C.1 removed the reference dump's entire case for C.2 — cold peak there is now 491 MB *below* the
pre-redesign baseline. The scaling argument was then tested directly in Part D rather than left as
arithmetic, which was the right call: the projection it rested on was 40% too high on both E and R.

**Resolution: build C.2, but not urgently.** Part D measured the 27.5 GB dump at a 12.97 GB peak with
available memory bottoming at 356 MB and 3.9 GB going to the pagefile. C.2 removes ≈1.9 GB of that
plus 2.04 GB of scratch round-trip, and D.3 strengthens the case further — the walk already holds the
`revOffsets`/`revTargets` C.2 would reuse, so the builder's copy is pure duplication of memory that is
resident either way.

It is a margin improvement, not a rescue. Roughly 6 GB of the 13 GB peak is the walk itself (D.3),
which C.2 does not touch. Anyone reaching for "make large dumps comfortable" needs to look there
instead, and should read §7.3 item 2 of the dominator integration doc first — a previous attempt
(`DenseIdMap`) came back 2.6× slower with no peak-memory win.

## Status

| Step | State |
|---|---|
| A.1 baseline selected (`d1dc4dcc`) — verified at 1,398.3 MB | ✅ |
| A.5 harness | ✅ |
| Cell 1 — baseline cold ×6 | ✅ |
| Cell 2 — baseline warm ×3 | ✅ |
| Cell 3 — current cold ×5 | ✅ |
| Cell 4 — current warm ×3 | ✅ |
| Arm 5 — `ca938bf4` cold ×2 (attribution) | ✅ |
| Results (Part A-R) | ✅ |
| B.5 — closed, no regression found | ✅ |
| **C.1 — delete dead `_fanoutPerBucket`** | ✅ **shipped, −688 MB cold peak** |
| C.1 verification — cold ×3, warm ×3, full suite | ✅ |
| 27.5 GB dump measurement (Part D) | ✅ fits at 12.97 GB peak, thrashes |
| C.2 — CSR from the walk's in-memory reverse CSR | ⬜ **justified, ≈1.9 GB + 2.04 GB scratch** |
| C.3 — bound builder residency (fallback) | ⬜ only if C.2 is rejected |
| C.4 — warm-path decode | ✅ dropped, gate not met |
| Walk's own ≈6 GB residency (D.3) | ⬜ out of scope, own investigation |
