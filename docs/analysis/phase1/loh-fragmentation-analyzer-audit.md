# LOH Fragmentation Analyzer — Audit Report

**Analyzer:** `LohFragmentationAnalyzer`
**Protocol:** Phase 1 Analyzer Architecture Review
**Date:** 2026-08-03

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`LohFragmentationAnalyzer` measures heap fragmentation within Large Object Heap (LOH) segments.
It reports global and per-segment fragmentation percentage, free-block statistics, a free-gap size
histogram, and a top-N large object table. It has two execution paths: an index-based fast path
consuming Phase 1 satellite files (`LohFreeBlockIndex.bin`, `LargeObjectIndex.bin`), and a
full heap-scan fallback for memory-only mode or benchmarks.

### Coverage Assessment

**Well-covered:**
- Global fragmentation percentage and per-segment breakdown
- Free-block count and largest free block
- Free-gap histogram (size distribution of gaps)
- Top-N large objects by size
- Disk/memory parity (verified by `LohFragmentationAnalyzerDiscrepancyTests`)
- Trend metrics for fragmentation percent, free bytes, total bytes, segment count

**Coverage gaps:**
- **POH (Pinned Object Heap) is indexed but ignored.** `LohFreeBlockWriter` scans both
  `GCSegmentKind.Large` and `GCSegmentKind.Pinned` and writes their free blocks into
  `LohFreeBlockIndex.bin`. `IsLohSegment` uses `segment.Kind.ToString().Contains("Large")`
  which only matches Large, not Pinned. POH free blocks from the index are silently orphaned
  — their segment addresses are never found in `segmentTotalBytes`, so they inflate
  `allFreeSizes` (the histogram source) without contributing to any segment's stats.
  This causes a correctness gap in the histogram and conceals a real fragmentation source.
- No allocation velocity — cannot indicate whether LOH is growing.
- No type-level breakdown of LOH consumption beyond top-20 individual objects.
- No compactability estimate (what allocation sizes could actually be satisfied by current gaps).
- No LOH object lifetime classification (Gen2 vs. ephemeral entry).

### Expansion Opportunities

- Separate POH reporting or an explicit "POH excluded" note with optional flag.
- Type-grouped LOH consumption summary (top types by total bytes, not individual objects).
- Allocation size vs. gap size compatibility analysis (can a 1 MB allocation be satisfied?).
- Pinned-object handle correlation for POH — identify what forces objects into POH.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- `SectionLeadFinding` gives a clear actionable summary with severity, free bytes, block count,
  and recommendation (ArrayPool, GC.Collect compaction) — actionable for an SRE.
- Heatmap visualization of top fragmented segments gives spatial intuition at a glance.
- Free-gap histogram provides the distribution insight needed to evaluate defragmentation
  strategies (i.e., whether gaps are too small to be useful).
- Severity band in `key_metrics` (`severity_band`) gives a quick filter for triage.

### Weaknesses

**W1 — Severity threshold inconsistency.**
`LohFragmentationFindingGenerator` maps ≥30% → Critical, ≥15% → Warning. The
`LohFragmentationSectionBuilder` lead finding maps >60% → Critical, >30% → Warning. Same
analyzer, two different threshold systems. A 45% fragmentation dump yields "Critical" in the
global findings but "Warning" in its own section lead.

**W2 — Per-segment size column is wrong.**
`LohFragmentationSectionBuilder` renders each segment's size as:
```csharp
Cell(FormatHelper.FormatBytes(d.TotalBytes / (ulong)Math.Max(1, d.SegmentCount)))
```
This is the *global average* size, applied uniformly to every row. A dump with one 512 MB
and nine 32 MB segments will display "~80 MB" for every segment. `LohSegmentSnapshot` does
not carry `TotalBytes`, so there is no correct value to display from the model without a model
change.

**W3 — FreeGapHistogram suppresses empty buckets.**
Buckets with zero count are dropped from the output. This creates visual gaps in the
distribution and prevents readers from seeing that, say, the "1 MB – 10 MB" range has zero
gaps (which itself is informative).

**W4 — Large objects table lacks count per type.**
Twenty individual object rows are shown. If 19 of them are `byte[]`, the table communicates
nothing about the root cause. A type-aggregated view would be far more actionable.

**W5 — No contextual interpretation of gap distribution.**
The histogram is presented without any guidance. An engineer unfamiliar with LOH may not know
what to infer from "50% of free gaps are < 1 KB". Adding a note (e.g., "highly fragmented
with many small gaps — allocation requests of any meaningful size cannot be satisfied without
compaction") would significantly increase diagnostic value.

**W6 — Missing per-segment total bytes in `LohSegmentSnapshot`.**
The model carries `FreeBytes` but not `TotalBytes`, so downstream consumers cannot compute
per-segment utilization or independent fragmentation percentage without access to the full
`LohFragmentationDomainResult` total (which they don't have per-segment).

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD API Usage

**C1 — `IsLohSegment` uses `ToString()` for segment kind check.**
```csharp
private static bool IsLohSegment(ClrSegment segment)
    => segment.Kind.ToString().Contains("Large", StringComparison.OrdinalIgnoreCase);
```
This allocates a string per segment for an enum comparison. `LohFreeBlockWriter` uses the
correct pattern:
```csharp
if (segment.Kind != GCSegmentKind.Large && segment.Kind != GCSegmentKind.Pinned)
    continue;
```
The analyzer should use direct enum comparisons.

**C2 — Redundant `heap.GetObject(objectAddress)` in heap-scan fallback.**
`AnalyzeFromHeap` iterates via `segment.EnumerateObjects()` which yields valid `ClrObject`
values. The address is extracted and immediately passed to `AccumulateSegmentObjectByAddress`
which calls `heap.GetObject(objectAddress)` again — reconstructing the same object. The
`ClrObject` from the iteration should be passed through directly.

**C3 — `obj.IsFree` vs `obj.Type.Name == "Free"` inconsistency.**
The heap-scan fallback uses `obj.IsFree` (the ClrMD-native check). `LohFreeBlockWriter` uses
`string.Equals(obj.Type.Name, FreeTypeName, StringComparison.Ordinal)`. These should be
equivalent, but relying on `IsFree` in the analyzer is the correct approach.

**C4 — MethodTable field in `LargeObjectIndex.bin` is never used.**
`LargeObjectTracker` writes Address (8) | MT (8) | Size (8). `ReadTopLargeObjects` ignores the
MT field entirely and calls `heap.GetObject(address)` to resolve the type name at analysis time.
The MT could be used via `heap.GetTypeByMethodTable(mt)` to avoid a full `GetObject` call, or
alternatively the record could omit MT to save 8 bytes per entry. Currently the field wastes
space and the comment in the reader says `// MT field (rec[8..]) unused — resolve via heap`.

**C5 — Offset field in `LohFreeBlockIndex.bin` is written but never read.**
`LohFreeBlockWriter` records `SegmentAddress (8) | Offset (8) | Size (8)`. The analyzer reads
`rec[0..8]` (segment address) and `rec[16..24]` (size), skipping `rec[8..16]` (offset). The
offset field is never used for any purpose. This 8-byte-per-record overhead could be eliminated.

### Infrastructure

**I1 — No cancellation in `BuildFreeGapHistogram`.**
The method iterates `allFreeSizes` which can contain millions of entries from large dumps.
There is no `CancellationToken` parameter. Cancellation requests during the histogram build
phase will not be respected until the method returns.

**I2 — `LohSegmentStats` inner class is a mutable class, not `readonly struct`.**
Used only in the heap-scan fallback as a local accumulation structure — should be a record or
readonly struct to reduce heap allocations.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Missing Diagnostics

**D1 — POH analysis (High value)**
Pinned Object Heap (.NET 5+) is a major source of fragmentation from pinned memory. `LohFreeBlockWriter`
already indexes POH free blocks. An explicit POH section (or at minimum a merged/flagged view)
would surface these issues.

**D2 — Type-grouped LOH consumption (High value)**
Instead of 20 individual objects, provide a type-aggregated table:

| Type | Object Count | Total Bytes | % of LOH |
|------|-------------|-------------|----------|

`LargeObjectTracker` only tracks top-100 by size; building a type-count index during Phase 1
would enable this.

**D3 — Allocation satisfiability estimate (Medium value)**
Given the gap histogram and a configurable allocation size threshold (e.g., 1 MB), produce:
"There are N contiguous free blocks of ≥ 1 MB; the heap can satisfy large allocations without
compaction." This gives actionable guidance without requiring compaction.

**D4 — LOH growth rate (Medium value — requires trend data)**
If two dumps are available (trend mode), report total LOH size delta, fragmentation delta,
and object count delta together. Currently only global metrics are trended; per-segment trends
are absent.

**D5 — Largest free block vs. typical allocation size (Medium value)**
If `LargestFreeBlock` < 85 KB (the LOH threshold), all gaps are sub-threshold and the LOH is
pathologically fragmented.

**D6 — Pinned GC handle correlation (Low-to-medium value)**
Objects in the LOH may be there due to pinned GC handles. Correlating the top large objects
against pinned handle roots would help distinguish "allocated big" from "pinned here".

**D7 — Per-type histogram (Low-to-medium value)**
A free-gap histogram scoped to "gaps adjacent to a specific type" would reveal whether, for
example, all gaps are caused by `byte[]` churn.

---

## Audit Area 5 — Performance, Memory & Scalability

### Index Path (Primary)

**Scale:** `LohFreeBlockIndex.bin` is noted as "< 1 MB typical". On a 25 GB dump with
severe fragmentation, there could be tens of thousands of free blocks. At 24 bytes per record,
50,000 blocks = 1.2 MB; still small. The index-based path is O(free-block count) and does not
walk the full heap — this scales well.

**P1 — Histogram bucket lookup is O(N × B).**
```csharp
for (int b = 0; b < s_gapBuckets.Length; b++)
    if (size >= s_gapBuckets[b].Min && size < s_gapBuckets[b].Max)
```
With B=7 buckets and N potentially large, a simple switch or binary search would be faster and
cleaner. Not a bottleneck at typical sizes, but worth noting.

### Heap-Scan Fallback

**P2 — `largeObjectCandidates` accumulates all LOH objects above threshold.**
On a dump with 10,000 large objects, this list will hold 10,000 `(ulong, string, ulong)` tuples
with their type name strings. A bounded approach (tracking only top-N using the same min-heap
strategy as `LargeObjectTracker`) would reduce memory pressure.

**P3 — `allFreeSizes` is unbounded.**
The fallback accumulates every free size into a `List<ulong>`. On a highly fragmented LOH with
hundreds of thousands of free blocks, this list can grow large. For the histogram, only the
counts per bucket are needed — the raw list could be replaced with direct bucket-increment
logic.

**P4 — Redundant `heap.GetObject` call (also noted in Area 3-C2).**
Each object address from `segment.EnumerateObjects()` is passed to `AccumulateSegmentObjectByAddress`
which calls `heap.GetObject(objectAddress)` again. This doubles the ClrMD object-resolution
work in the fallback path.

### Cancellation

**P5 — No cancellation inside `BuildFreeGapHistogram`** (also noted in Area 3-I1). On a
large dump the iteration can take measurable time.

**P6 — Per-segment loop in fallback has no progress reporting per segment.**
The `scanCounter` ticks per object, but there is no segment-level progress marker. On a 10 GB
dump with multiple large LOH segments, no intermediate progress is reported beyond the object tick.

---

## Audit Area 6 — Correctness & Confidence

### C1 — POH segment mismatch (Correctness defect)

`LohFreeBlockWriter` indexes free blocks from both `GCSegmentKind.Large` and
`GCSegmentKind.Pinned` segments. `AnalyzeFromIndex` builds `segmentTotalBytes` using
`IsLohSegment` which only matches Large. POH segment addresses will be absent from
`segmentTotalBytes`.

Consequence: In `ReadFreeBlocks`, POH free blocks are added to `freeBySegment` with their POH
segment address. In the aggregation loop, `freeBySegment.TryGetValue(addr, ...)` for those
addresses always fails, so POH free space contributes to `allFreeSizes` (histogram) but to no
segment's `segFree`, `segLargest`, or `segFreeCount`. Global `totalFreeBytes` is thus
understated by the total POH free space, and the histogram contains POH gaps that are not
reflected in the summary stats. The magnitude depends on how much POH fragmentation exists.

### C2 — Severity threshold split (Diagnostic confidence defect)

Finding generator and section builder use different thresholds (see Area 2, W1). The same dump
produces inconsistent severity signals across the report.

### C3 — LargeObjectIndex.bin hardcap at 100 objects

`LargeObjectTracker.MaxEntries = 100`. If `options.TopLargeObjectsCount > 100` (possible with
`Full` profile, currently capped at 60, but still), the index silently caps results.
`ReadTopLargeObjects` already guards `if (result.Count >= topLargeObjectsCount) break`, so in
practice the effective cap is `min(index records, topLargeObjectsCount)`. No documentation or
warning is emitted when the index cap is hit.

### C4 — `LargestFreeBlock` may be 0 when there are free blocks

If all free blocks are recorded in the index with size 0 (which should not happen in practice,
but is not guarded), `maxFreeBlock` remains 0. Not a real concern but worth a defensive check.

### C5 — `CommittedMemory` may return an empty range

`GetSegmentTotalBytes` returns `mem.End - mem.Start`. For some dump formats or corrupt segments,
`mem.Start == mem.End`. The code handles this (`totalBytes == 0 ? 0 : ...`) but does not skip
or log such segments.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!loh` and `!dumpheap -stat -min 85000` provide type-aggregated LOH counts and free-object
breakdown. SOS surfacing of object retention paths directly from the LOH is something
DumpDetective currently lacks for large objects. DumpDetective's heatmap and histogram go
**beyond** WinDbg in presentation quality.

**Gap:** DumpDetective lacks type-aggregated LOH stats (total bytes per type, not just top-20
individual objects).

### PerfView

PerfView's GC heap analysis provides LOH allocation event traces (not dump-based), with
allocation site attribution. DumpDetective operates on static dumps and cannot provide
allocation velocity data — but it could surface "large object allocation churn indicators"
via LOH free-block density and object age signals.

**Gap:** No allocation site or code-path attribution for large objects.

### Visual Studio Memory Usage

VS Memory Usage shows LOH objects grouped by type with sizes and reference graphs.
DumpDetective matches on fragmentation metrics but lacks the type-grouped summary and
reference graph integration.

**Gap:** No reference graph integration for large objects (what keeps them alive).

### JetBrains dotMemory

dotMemory provides "LOH fragmentation" and "inefficient allocation patterns" categories. It
flags types with high allocation/deallocation rates as fragmentation contributors.
DumpDetective has no equivalent pattern analysis.

**Gap:** No LOH allocation pattern analysis (high-churn type identification).

### Summary

DumpDetective is competitive on static fragmentation metrics. It lags on type-aggregated
views, allocation attribution, and live-object reference paths.

---

## Final Executive Summary

### Overall Assessment

**Score: 68/100**

**Production readiness:** Conditionally ready. The index-based fast path is efficient and
well-structured. The POH segment mismatch (Area 6-C1) is a correctness defect that can
silently misreport fragmentation on .NET 5+ dumps with POH usage. The severity threshold
inconsistency (Area 2-W1) and wrong per-segment size column (Area 2-W2) are user-visible bugs.

**Major strengths:**
- Dual-path architecture (fast index vs. heap scan) with proven parity validation
- Clear actionable lead finding with compaction/pooling recommendations
- Free-gap histogram is unique among comparable tools
- Trend metrics pipeline in place

**Major weaknesses:**
- POH free blocks from the index corrupt histogram data and understate global free bytes
- Per-segment "Size" column in the report shows average, not actual
- Severity thresholds differ between finding generator and section builder
- No type-aggregated LOH view — top-20 individual objects rarely identify root causes
- Minimal unit test coverage (one integration test requiring an actual dump path)

---

### Priority Roadmap

| # | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---------------|--------|-----------|-----------|----------------|--------|
| **P0** | Fix `IsLohSegment` to use enum comparison and align index-path segment filter with what `LohFreeBlockWriter` writes (include/exclude Pinned consistently) | High | Low | High | Improvement | ✅ DONE |
| **P0** | Align severity thresholds between `LohFragmentationFindingGenerator` and `LohFragmentationSectionBuilder` | Medium | Low | High | Improvement | ✅ DONE |
| **P1** | Add `TotalBytes` to `LohSegmentSnapshot`; fix per-segment Size column in section builder | High | Low | High | Improvement | ✅ DONE |
| **P1** | Replace unbounded `largeObjectCandidates` list in fallback with a top-N bounded accumulator (same pattern as `LargeObjectTracker`) | Medium | Low | High | Improvement | ✅ DONE |
| **P1** | Replace unbounded `allFreeSizes` list with direct bucket accumulation to eliminate the intermediate list | Medium | Low | High | Improvement | ✅ DONE |
| **P1** | Fix redundant `heap.GetObject(objectAddress)` in `AccumulateSegmentObjectByAddress` — pass `ClrObject` directly | Medium | Low | High | Improvement | ✅ DONE |
| **P1** | Add type-aggregated LOH table (top types by total bytes) to domain result and section builder | High | Medium | High | Improvement | ✅ DONE |
| **P2** | Add `CancellationToken` to `BuildFreeGapHistogram` | Low | Low | High | Improvement | ✅ DONE |
| **P2** | Change `loh.largest.free.block` metric trend direction from `Neutral` to `LowerIsWorse` in `LohFragmentationTrendComparer` | Low | Low | High | Improvement | ✅ DONE |
| **P2** | Document or remove the unused Offset field in `LohFreeBlockIndex.bin`; reclaim 8 bytes/record or expose offset for gap-adjacency analysis | Low | Low | Medium | Improvement | ✅ DONE |
| **P2** | Expose POH as either a separate analyzer section or an explicit excluded/included flag with reporting | High | Medium | Medium | Evolution | ✅ DONE |
| **P2** | Add unit tests for `BuildFreeGapHistogram`, `IsLohSegment`, and the index aggregation path using synthetic data | Medium | Medium | High | Improvement | ✅ DONE |
| **P3** | Replace `LohSegmentStats` inner class with a readonly record struct | Low | Low | High | Improvement | ✅ DONE |
| **P3** | Interpret histogram output in the section builder — add a text note when gap distribution is severely small (e.g., > 80% gaps < 1 KB) | Medium | Low | Medium | Improvement | ✅ DONE |
| **P3** | Add a Phase 1 type-aggregated LOH writer to enable type-grouped reporting without a fallback heap scan | High | High | Medium | Evolution | ✅ DONE (via existing `TypeAggregateIndexEntry.LohCount`/`LohSize`, not a new writer) |
| **P3** | Evaluate exposing MT field in `LargeObjectIndex.bin` for type resolution without `heap.GetObject` overhead | Low | Low | Medium | Improvement | ✅ DONE (already resolved — `ReadTopLargeObjects` no longer exists; `LargeObjectTracker.ReadRecords` already passes `mt` to every consumer, and `LohFragmentationAnalyzer` already resolves types via `heap.GetTypeByMethodTable(mt)` with no `heap.GetObject` call) |

---

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. It produces accurate results for
   SOH-only or pre-.NET 5 dumps. On .NET 5+ workloads with POH usage, the mismatch between
   what `LohFreeBlockWriter` indexes and what `AnalyzeFromIndex` queries causes silently
   incorrect fragmentation stats. The per-segment size display bug is user-visible.

2. **Highest-impact improvements:** Fix the POH segment filter mismatch (P0); add
   `TotalBytes` to `LohSegmentSnapshot` (P1); add type-aggregated LOH table (P1).

3. **Platform evolution opportunities:** A Phase 1 type-aggregated LOH writer would enable
   a rich type-grouped view without a fallback heap scan, and would benefit any future analyzer
   that needs LOH type distribution data.

4. **Highest engineering ROI:** P0 correctness fixes are trivial code changes with immediate
   diagnostic accuracy impact. The type-aggregated table (P1) requires a modest Phase 1 writer
   addition but would lift the analyzer from "fragmentation meter" to "root cause identifier".
