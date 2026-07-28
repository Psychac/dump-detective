# 17 — Disk Index Build: Phase Breakdown Findings

## Trigger

Cold `DiskBackedObjectIndexWriter.Build` on a 25GB dump took ~250-300s in the field
(heap scan + roots + "whatever else"). Before optimizing anything, we measured
per-phase cold-build timing on two real dumps to find out where the time
actually goes.

## Method

New opt-in test:
[tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DiskIndexBuildPhaseBreakdownPerfTests.cs](../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DiskIndexBuildPhaseBreakdownPerfTests.cs)

- Moves any existing `.dumpindex` cache dir aside (`.perfbak`) before the run and restores it in a
  `finally` block, so the measured build is always a real cold scan, never a cache-hit fast path.
- Reconstructs phase timings from `DiskBackedObjectIndexWriter`'s existing
  `IProgress<AnalyzerProgressReport>` callback — no production code was instrumented or modified.
- Gated by `[DiscrepancyFact]` (`DD_RUN_DISCREPANCY_TESTS=1`), dump path via `DD_BENCHMARK_DUMP`.
- Run in **Release** config — Debug materially understates throughput and should not be used for
  these numbers.
- **Attribution correction**: each `IProgress` mark timestamps when a phase *started*, and a new
  mark is only recorded when the phase name changes. A phase's true duration is therefore
  `(next mark's Elapsed − this mark's Elapsed)`, not the delta printed next to its own start line.
  An earlier draft of this test (and doc) mislabeled deltas as belonging to the phase they were
  printed next to, which wrongly implicated `TaskIndexWriter` as the bottleneck (~130s) when that
  duration actually belonged to the *preceding* phase (GC root enumeration). The test now prints
  `duration` computed against the *next* mark, and the numbers below are the corrected values.

## Results

### 3.3GB dump (standard reference dump, tier=Medium)

`Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp`, threads=135, heap
segments=8, ~14.6M objects.

| Phase | Duration |
|---|---|
| Heap scan (indexing heap) | 13.28s |
| Handles | 0.06s |
| **GC roots enumeration** | **4.80s** |
| Tasks section write | 0.02s |
| EventCandidates section write | 0.01s |
| LargeObjects section write | 0.01s |
| LohFreeBlocks section write + tail | 1.00s |
| **Total** | **20.31s** |

### 25GB dump (tier=Large)

`D:\DUmps\21-04\w3wp.exe_260421_175618.dmp`, 26244.0 MB, threads=158, heap segments=63,
87,104,236 objects.

| Phase | Duration |
|---|---|
| Heap scan (indexing heap) | 127.29s |
| Handles | 2.35s |
| **GC roots enumeration** | **169.46s** |
| Tasks section write | 0.02s |
| EventCandidates section write | 0.02s |
| LargeObjects section write | 0.05s |
| LohFreeBlocks section write + tail | 0.86s |
| **Total** | **301.29s** |

Matches the field-reported ~250-300s.

## Analysis

Growth factors, 3.3GB → 25GB:

| Metric | Growth |
|---|---|
| Dump size | 7.6x |
| Object count | 6.0x |
| Thread count | 1.17x |
| Heap segment count | 7.9x |
| Heap scan duration | 9.6x |
| **GC roots duration** | **35.3x** |

- **GC root enumeration is the actual bottleneck at 25GB scale**: 4.80s (24% of total) on the
  3.3GB dump vs. **169.46s (56% of total)** on the 25GB dump — a **35.3x** increase, worse than
  linear against every size metric we have except heap segment count.
- **Thread count is not the driver**: 135 → 158 threads is only 1.17x, ruling out "more threads →
  proportionally more stack walking" as the primary explanation.
- **Heap segment count growth (7.9x) is the closest match** to the scaling behavior. A plausible
  mechanism: `ClrHeap.EnumerateRoots()` has to resolve/validate each candidate root pointer
  (thread-stack slot, static field, handle) against the heap's segment list to determine whether it
  points at a live object. If that per-candidate lookup is a linear scan over segments rather than
  a binary search over sorted segment ranges, a heap with ~8x more segments makes *every single*
  candidate check ~8x costlier — compounding with somewhat more root candidates at this scale to
  produce the observed ~35x blowup. This is a hypothesis, not yet confirmed — see below.
- **All satellite-section writes remain negligible at both scales** (Handles, Tasks,
  EventCandidates, LargeObjects, LohFreeBlocks: all ≤2.35s, most ≤0.1s). None of them are worth
  optimizing; the original "Tasks section" attribution in the first draft of this doc was wrong
  (see Method above) and is retracted.
- The heap scan itself does scale close to expected (9.6x duration for 7.6x size / 6x objects),
  consistent with `DiskBackedObjectIndexWriter`'s Large-tier DOP (up to 8 segments) and buffer
  sizing (4MB) — see
  [DiskBackedObjectIndexWriter.cs:55-68](../../src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs).
  It is not the primary problem, though a ~10x slowdown for a ~6-8x size increase leaves some room
  too.

## Source-read hypothesis (retracted — see profiling below)

`RootIndexWriter.Write` ([RootIndexWriter.cs](../../src/DumpDetective.Analysis/Indexing/Satellite/RootIndexWriter.cs))
itself does trivial O(1) per-root work (struct pack into a pooled buffer, batched stream write) —
it is not the source of the 169s. `Microsoft.Diagnostics.Runtime` (ClrMD 4.0.732401) ships as a
compiled DLL only, so a first pass read the ClrMD source on GitHub
([microsoft/clrmd](https://github.com/microsoft/clrmd), `main` branch — not pinned to the exact
4.0.732401 tag). That read found `ClrHeap.GetSegmentByAddress` is a binary search (not a linear
scan, ruling out the original segment-scan theory), and instead pointed at
`ClrHeap.EnumerateAdditionalRoots()` → `GetContainingObject()` doing a from-scratch
`segment.EnumerateObjects()` walk on every interior-pointer bucket cache miss, as the likely
culprit for static/thread-static root resolution.

**This hypothesis was checked against a real sampling profile and is wrong.** See below.

## Root cause (confirmed via dotnet-trace on the 3.3GB dump)

Built a standalone profiling harness,
[tools/ProfileRootEnumeration](../../tools/ProfileRootEnumeration/Program.cs), that forces a cold
index build (same cache-aside/restore pattern as the phase-breakdown test) and ran it under
`dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler` on the 3.3GB reference dump,
then converted to speedscope and aggregated self/inclusive time per frame across all threads.

Findings (inclusive time, aggregated across all sampled threads):

| Frame | Inclusive time | % of sampled time |
|---|---|---|
| `RootIndexWriter.Write` → `ClrHeap.EnumerateRoots()` | 5.55s | 3.20% |
| — of which `ClrThread.EnumerateStackRoots()` | 5.53s | 3.20% |
| — of which `DacThreadHelpers.EnumerateStackRoots` (native DAC call) | 5.26s | 3.04% |
| `ClrHeap.EnumerateAdditionalRoots()` (the source-read hypothesis) | 0.006s | 0.00% |
| `GetContainingObject`/`FindPreviousObjectOnSegment` | 0.062s | 0.00% |
| `GetSegmentByAddress` | 0.002s | 0.00% |

`EnumerateRoots`'s ~5.55s lines up closely with the doc's independently-measured 4.80s GC-roots
phase on this dump. Essentially **all** of it is inside `ClrThread.EnumerateStackRoots()`, which
delegates almost entirely to a native DAC call (`DacThreadHelpers.EnumerateStackRoots`) that walks
each thread's stack. `EnumerateAdditionalRoots()`, `GetContainingObject`, and `GetSegmentByAddress`
— the managed-code paths the source read flagged — are all negligible in practice.

73.75% of all sampled time across the whole build is `UNMANAGED_CODE_TIME` — opaque native
code the .NET sample profiler cannot see into. That's exactly where per-thread stack unwinding and
(likely) per-candidate-root heap-membership validation happens, inside the DAC/dbgeng native
layer, not in any managed ClrMD code that source-reading could inspect. This explains why reading
the managed source pointed at the wrong place: the real cost is in native code with no visible
managed frames.

## Re-verification on the 25GB dump

Repeated the identical `dotnet-trace` capture against the 25GB dump
(`D:\DUmps\21-04\w3wp.exe_260421_175618.dmp`) to check whether the mechanism holds at the scale
where the 35x blowup actually hurts, and to resolve the "still open" scaling question below.

| Frame | 3.3GB inclusive | 25GB inclusive | Growth |
|---|---|---|---|
| `RootIndexWriter.Write` → `EnumerateRoots()` | 5.55s | 168.31s | 30.3x |
| — `ClrThread.EnumerateStackRoots()` | 5.53s | 168.28s | 30.4x |
| — `DacThreadHelpers.EnumerateStackRoots` (native) | 5.26s | 167.38s | 31.8x |
| — of which `DacDataTarget.ReadVirtual` (native memory read) | *(not broken out at 3.3GB scale)* | 161.93s | — |
| `EnumerateAdditionalRoots()` | 0.006s | 0.016s | negligible at both scales |
| `GetContainingObject`/`FindPreviousObjectOnSegment` | 0.062s | 0.010s | negligible at both scales |
| `GetSegmentByAddress` | 0.002s | 0.002s | negligible at both scales |

`EnumerateRoots`'s 168.31s at 25GB lines up almost exactly with the independently-measured 169.46s
GC-roots phase for this dump, and the 30–32x growth across all three levels of the call chain is
consistent with the originally observed 35.3x GC-root-duration growth (some variance expected —
different profiling run, sampling noise, and this harness's index build isn't byte-identical to
the production `DiskIndexBuildPhaseBreakdownPerfTests` run that produced the original 169.46s).

**New finding at 25GB scale**: within `DacThreadHelpers.EnumerateStackRoots`, 96.6% of the time
(161.93s of 167.38s) is `DacDataTarget.ReadVirtual` — raw memory reads issued by the native DAC
while unwinding each thread's stack. Checked against ClrMD source
([DacDataTarget.cs](https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/DacInterface/DacDataTarget.cs)):
`ReadVirtual` calls `_dataReader.Read(address, span)` directly — `_dataReader` is the same
`IDataReader` (backed by `CachedMemoryReader`) used for ordinary heap-object reads elsewhere, not a
separate uncached path. So this is **not a caching bug or a missed-cache code path** — it's the
sheer volume and per-call overhead of the many small, pointer-sized reads the native DAC issues
while walking every live thread's stack frame-by-frame, and that volume/overhead scales with dump
size (more memory ranges in the underlying dump, more segments backing the cache, larger/more
numerous stacks to walk) even though thread *count* barely changed (135 → 158, 1.17x).

## Conclusion (confirmed on both dumps — no further action needed to identify the bottleneck)

Do **not** optimize `RootIndexWriter`, `EnumerateAdditionalRoots`, `GetContainingObject`, or
`GetSegmentByAddress` — all confirmed negligible on both the 3.3GB and 25GB dumps. The bottleneck
is genuinely native: per-thread stack unwinding inside ClrMD's DAC layer
(`DacThreadHelpers.EnumerateStackRoots`), and within that, raw memory reads
(`DacDataTarget.ReadVirtual` → the same cached `IDataReader` used elsewhere) dominate at scale.
This is intrinsic ClrMD/DAC behavior, not a DumpDetective code defect.

Options if this needs to get faster (none attempted yet):
1. Investigate whether `CachedMemoryReader`'s page/segment cache size or page granularity can be
   tuned to reduce per-`ReadVirtual`-call overhead at large dump sizes (would need to check
   `HeapAnalysisCache`/`ArrayPoolBasedCacheEntry` sizing against 25GB-scale memory-range counts).
2. Consider whether stack-root enumeration can be skipped or deferred for size tiers/use cases
   where it isn't strictly required (e.g. defer GC-root indexing to an on-demand Phase 2 step
   rather than always doing it during the cold Phase 1 build), trading a slower on-demand root
   query for a faster upfront index build on very large dumps.
3. Accept this as an intrinsic cost of ClrMD's root enumeration on very large dumps and set
   progress-reporting/user expectations accordingly (e.g. surface "GC roots" as its own visible
   phase with an ETA, which `BuildHeapIndexStage`'s progress reporting already supports).
