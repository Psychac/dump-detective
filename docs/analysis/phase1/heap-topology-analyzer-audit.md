# HeapTopologyAnalyzer — Phase 1 Audit

> Reviewer roles applied: Principal .NET Runtime Engineer · ClrMD Expert · CLR & GC Specialist ·
> Memory Diagnostics Engineer · Production SRE · Performance Engineer · Software Architect

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`HeapTopologyAnalyzer` is a **heap-layout description analyzer**. Its job is to classify every
`ClrSegment` by kind (SOH / LOH / POH / Frozen), accumulate committed / reserved / used bytes per
kind and per logical GC heap, identify the largest segments, and capture per-type breakdowns for
POH and Frozen segments.

The analyzer operates purely on `ClrHeap.Segments` (segment enumeration) with an optional
per-object scan for LOH, POH, and Frozen. SOH object enumeration is opt-in and off by default.

### Coverage Assessment

| Coverage area | Status |
|---|---|
| Segment kind classification (SOH / LOH / POH / Frozen) | ✅ |
| Committed / reserved / used bytes per kind | ✅ |
| Per logical-heap breakdown | ✅ |
| Top segments by committed size | ✅ |
| POH type breakdown | ✅ |
| Frozen type breakdown | ✅ |
| SOH type breakdown | ❌ (skipped when `CountSohObjects = false`) |
| Generation-level byte distribution (Gen0 / Gen1 / Gen2) | ❌ |
| Ephemeral segment identification and fill % | ❌ (delegated to `SegmentReservationAnalyzer`) |
| Fragmentation per kind | ❌ (committed - used not surfaced per kind) |
| Segment count trend / growth across dumps | ✅ (via `HeapTopologyTrendComparer`) |

### Boundary with `SegmentReservationAnalyzer`

The two analyzers have genuine but partially overlapping scopes:

- `HeapTopologyAnalyzer` — **what lives in each segment** (type topology, object counts, size
  distribution).
- `SegmentReservationAnalyzer` — **how segments use VM** (committed vs. reserved ratios, ephemeral
  fill %, address-space pressure).

Both independently compute `committed` and `reserved` per segment from the same ClrMD fields, and
both accumulate per-logical-heap totals. This is duplicated work with no shared infrastructure
(see Audit Area 3).

### Missing Functionality

1. **Generation-level byte distribution** — knowing that 12 GB is in Gen2 versus Gen0 is
   fundamentally more useful than knowing the SOH total. ClrMD exposes
   `ClrSegment.Generation0/1/2` ranges.
2. **Per-kind fragmentation** — committed minus used is computed globally but not per-kind; an LOH
   that is 60% free is more actionable than a global 30% free figure.
3. **SOH type breakdown when SOH is not counted** — the `CountSohObjects = false` fast-path loses
   all type-level information for the dominant heap portion.
4. **Segment address-space density** — gap bytes between consecutive segment addresses are not
   tracked; large gaps indicate VM reservation pressure not captured elsewhere.

### Expansion Opportunities

- Expose Gen0 / Gen1 / Gen2 byte totals using the `ClrSegment.Generation0/1/2` range properties
  already provided by ClrMD 3+. This is additive and zero-cost for the segment loop.
- Add per-kind `FragmentedBytes` = committed − used to the domain result.
- Share the segment enumeration loop with `SegmentReservationAnalyzer` via a shared pass or by
  having one consume the other's result during aggregation.

### Deliverable

The analyzer is **well-scoped** for a layout-description role. Its boundary is correct and the
split from `SegmentReservationAnalyzer` is defensible. The main opportunity is depth: generation
decomposition and per-kind fragmentation are high-value additions that require no structural change.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- **Quantitative foundation is solid.** The section builder exposes committed, used, reserved,
  reservation gap, LOH/POH/FOH percentages, and the kind summary table. These are the right
  starting metrics for a layout snapshot.
- **Skew warning is actionable.** The logical-heap skew alert (`maxBytes > 2× minBytes`) is a
  genuine heuristic that surfaces Server GC imbalance correctly.
- **POH and FOH type tables** are valuable for pinning and interop investigations. Not common in
  tooling; good differentiation.
- **FindingGenerator LOH thresholds** (warning at 25%, critical at 40%) are reasonable for
  production .NET applications.

### Weaknesses

1. **Fragmentation finding is backwards.** The fragmentation finding in `HeapTopologyFindingGenerator`
   attributes fragmentation to `GCHandle.Alloc(Pinned)` and recommends LOH compaction as the fix,
   but the metric it fires on is total heap `(committed - used) / committed`. A high committed-vs-used
   ratio is normal on large Server GC deployments and does not imply pinning. The recommendation is
   misleading and will fire incorrectly.

2. **Generation-level breakdown absent.** The report has no Gen0 / Gen1 / Gen2 totals. For a
   memory investigation, knowing that 9 GB of 10 GB SOH is Gen2 (long-lived objects, not being
   collected) is the single most important heap topology fact. This is missing entirely.

3. **SOH type breakdown absent in fast mode.** When `CountSohObjects = false` (the default), the
   report contains no per-type information for the dominant heap area. This makes the topology
   section uninformative for SOH-heavy dumps in the common case.

4. **"Used bytes" definition is opaque.** The report surface exposes `TotalUsedBytes` computed as
   the sum of `obj.Size` for all counted objects. But SOH used bytes are zero when `CountSohObjects
   = false`, making the global used-bytes figure silently incomplete. No caveat is surfaced in the
   report.

5. **Top-10 segments table lacks context.** The `TopSegmentsBySize` table shows committed bytes
   and length, but object density (objects per MB) and utilization % (used / committed) per segment
   would make it immediately actionable.

6. **`LohPercent` and `PohPercent` are computed as fractions of committed, but `FrozenPercent`
   is computed identically** — however none of these percent values account for the fact that large
   reserved-but-uncommitted segments would skew committed-based percentages on dumps where GC has
   grown memory but not released it.

7. **No SOH object count in the default profile.** The kind summary table shows "N/A" for SOH
   objects in the standard output. This is a data quality hole that engineers will notice
   immediately and trust less.

8. **Trend comparer omits SOH bytes and reserved bytes.** `HeapTopologyTrendComparer` tracks LOH
   and POH bytes / percentages but not total SOH bytes, total reserved bytes, or reservation gap.
   Trending reserved growth is a leading indicator of address-space pressure.

### Missing Diagnostics

- Per-kind fragmentation percentage.
- Gen0 / Gen1 / Gen2 byte breakdown within SOH.
- Segment utilization % (used / committed) in the top-segments table.
- A note in the report when `CountSohObjects = false` so readers understand the "N/A" entries.
- Reservation gap trend across dumps (currently not in the trend comparer).

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD API Usage

| API | Used | Notes |
|---|---|---|
| `ClrHeap.Segments` | ✅ | Correct iteration pattern |
| `ClrSegment.CommittedMemory` | ✅ | Via `GetCommittedBytes()` helper |
| `ClrSegment.ReservedMemory` | ✅ | Via `GetReservedBytes()` helper |
| `ClrSegment.SubHeap.Index` | ✅ | Logical heap index |
| `ClrSegment.EnumerateObjects()` | ✅ | Per-segment streaming |
| `ClrSegment.Kind` | ✅ (indirect) | Via `SegmentKindMapper` string compare |
| `ClrSegment.Generation0 / Generation1 / Generation2` | ❌ | Not used — generation ranges available |
| `ClrSubHeap.Gc0Size / Gc1Size / Gc2Size` | ❌ | Generation size properties |
| `ClrSegment.GetGeneration(address)` | ❌ | Per-object generation, not consumed |
| `ClrObject.IsFree` | ✅ | Free object filtering |
| `ClrHeap.IsServer` | ❌ | Not surfaced to report |
| `ClrHeap.HeapCount` | ❌ | Not reported |

### `SegmentKindMapper` — String-Based Dispatch

`SegmentKindMapper.Map()` calls `segment.Kind.ToString()` and does substring matching. ClrMD
exposes `ClrSegmentKind` as a proper enum (`GcSegmentKind` in 3.1.x). String-based dispatch is
fragile if enum value names change across ClrMD versions and wastes an allocation per segment.
Direct enum comparison (`segment.Kind == GcSegmentKind.Frozen`) would be correct and zero-alloc.

### Infrastructure Overlap with `SegmentReservationAnalyzer`

Both analyzers independently:
- Iterate `ClrHeap.Segments`.
- Compute `committed = CommittedMemory.End - CommittedMemory.Start`.
- Compute `reserved = ReservedMemory.End - ReservedMemory.Start`.
- Accumulate per-logical-heap committed / reserved totals.
- Call `SegmentKindMapper.Map()` per segment.

This is full duplication of the segment-scan pass. The same committed/reserved numbers appear in
both result types, both section builders, and both finding generators. There is no shared segment
scan infrastructure.

### Index Utilization

`HeapTopologyAnalyzer` does not consume any disk-backed index (it operates directly on ClrMD),
which is correct for a segment-level scan. The discrepancy test confirms the analyzer is
cache-independent. This is appropriate architecture.

### Recommendations

1. Switch `SegmentKindMapper` from `ToString()` substring matching to direct `GcSegmentKind` enum
   comparison to eliminate per-segment string allocation.
2. Add `ClrSegment.Generation0 / 1 / 2` range reads to the existing segment loop — this is three
   property reads per segment and zero additional cost.
3. Expose `ClrHeap.IsServer` and `ClrHeap.HeapCount` in the result / report for Server vs.
   Workstation GC context.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

The following information is available in the dump but not currently extracted.

### High Value

| Opportunity | Source | Effort |
|---|---|---|
| Gen0 / Gen1 / Gen2 byte totals per segment and in aggregate | `ClrSegment.Generation0/1/2` | Low |
| Per-kind fragmentation (committed − used per SOH / LOH / POH) | Already computed; not split | Low |
| Server vs. Workstation GC mode | `ClrHeap.IsServer` | Trivial |
| Logical heap count | `ClrHeap.HeapCount` | Trivial |
| Segment utilization % per row in the top-segments table | `used / committed` | Low |

### Medium Value

| Opportunity | Source | Effort |
|---|---|---|
| SOH type breakdown in the fast-path (segment-level heuristic, not full object scan) | `ClrSegment.EnumerateObjects()` with early cutoff | Medium |
| LOH segment-level fragmentation (free objects by address range) | LOH object scan with `IsFree` tracking | Medium |
| Ephemeral segment promotion pressure (Gen0/Gen1 ratio) | `Generation0.Length / Generation1.Length` | Low |
| Object density per segment (objects per committed MB) | Already have count + committed | Low |
| Pinned count per POH segment (cross-reference with GCHandle index) | `GCHandle` index in cache | Medium |

### Low Value / Long Term

| Opportunity | Source | Effort |
|---|---|---|
| Segment-level allocation rate (cross-dump delta) | Trend comparer extension | Medium |
| VM address gap map (unmapped virtual address ranges between segments) | Segment address sort | Medium |
| Per-generation object count (Gen2 object count without full heap scan) | Gen2 range + segment object scan | High |

---

## Audit Area 5 — Performance, Memory & Scalability

### Current Behavior

The analyzer has two paths:

1. **Fast path (`CountSohObjects = false`)** — iterates `ClrHeap.Segments` once, calls
   `GetCommittedBytes` and `GetReservedBytes` per segment, and calls `CountObjects` only for
   LOH / POH / Frozen. SOH segments return immediately with sentinel `-1`.
2. **Full path (`CountSohObjects = true`)** — enumerates objects in every segment including SOH.
   On a large dump with 87M+ objects this becomes O(N) over the full heap.

### Memory Allocation Analysis

| Allocation | Size at scale | Notes |
|---|---|---|
| `snapshots` List | One `HeapSegmentSnapshot` record per segment; ~50-200 segments | Acceptable |
| `bytesByLogicalHeap` Dictionary | One entry per logical heap (typically ≤16) | Negligible |
| `pohTypes` / `frozenTypes` Dictionaries | One entry per unique type in POH/Frozen | Bounded |
| `SegmentTypeAccumulator` struct values | Stored by value in Dictionary — boxed on write | ⚠ Mutable struct in Dictionary causes per-update copy; struct semantics violated |
| `topBySize` array | `.OrderByDescending().Take(10).ToArray()` | LINQ sort on snapshot list is fine |
| `BuildTopTypeSnapshots` intermediate `List<TypeSnapshot>` | Per unique type in POH/Frozen | Fine at scale |

**Critical issue:** `SegmentTypeAccumulator` is a `struct` stored by value in a `Dictionary`.
The access pattern is:

```csharp
if (!typeStats.TryGetValue(typeName, out SegmentTypeAccumulator acc))
    acc = new SegmentTypeAccumulator();
acc.Count++;
acc.TotalBytes += obj.Size;
typeStats[typeName] = acc;
```

This is a read-modify-write cycle that copies the struct out and back in on every object. For POH
segments with many objects of the same type this is still correct but wastes a dict lookup+write
per object. A `class` accumulator or `ref`-return pattern would eliminate the redundant write.
The struct is also not `readonly` despite being used like a value accumulator.

### Scalability

| Scenario | Expected behavior |
|---|---|
| 1 GB dump, 500 segments, SOH off | Fast; segment loop only |
| 10 GB dump, Server GC, 16 heaps | Still fast for SOH-off path; LOH/POH object scan bounded |
| 25 GB dump with large POH (interop-heavy) | POH object scan may enumerate millions of objects; `pohTypes` dictionary grows |
| 87M+ object dump with `CountSohObjects = true` | Full heap scan; correct but slow; no parallelism |

### Progress Reporting

Progress is reported in the inner loop only for LOH and POH (`reportInner` flag). SOH with
`CountSohObjects = true` fires no per-object progress updates. On a 87M-object SOH scan this
means no progress feedback for potentially minutes. This should flood-limit progress reporting
for SOH the same way it does for other kinds.

### Cancellation

`cancellationToken.ThrowIfCancellationRequested()` is called once at the outer `AnalyzeAsync`
entry. The inner loop has no cancellation check. For a full SOH scan (87M objects) cancellation
is unresponsive.

### Index Opportunity

For the `CountSohObjects = false` fast path the analyzer is already near-optimal. A pre-built
segment-level index (committed / reserved / kind) would allow completely bypassing ClrMD after
index build — currently not warranted because the segment count is small (< 500) and the scan is
cheap.

---

## Audit Area 6 — Correctness & Confidence

### Correct Behaviors

- Sentinel `-1` for uncounted SOH correctly propagates through the kind summary, the per-logical
  heap map, and the segment snapshots. The report displays "N/A" where appropriate.
- `GetCommittedBytes` and `GetReservedBytes` check `End >= Start` before subtracting — guards
  against corrupted segment metadata in damaged dumps.
- `obj.IsValid && !obj.IsFree` filter is correct; free objects inflate counts and sizes without
  representing live data.
- `totalObjectsScanned` accumulation uses a local counter to batch updates before adding to the
  `ref` parameter — avoids false sharing and excess synchronization on a single-threaded pass.

### Risks and False Conclusions

1. **`SegmentKindMapper` defaults unknown kinds to SOH.** The `default` case in `Map()` returns
   `HeapSegmentKind.SmallObjectHeap`. If ClrMD adds a new segment kind (e.g., a future
   `ReadOnlyObjectHeap`), it will silently be counted as SOH. The `Unknown` value exists in the
   enum but is never assigned.

2. **`generation` field is `SubHeap.Index`, not GC generation.** The variable is named `generation`
   in the main loop, but `segment.SubHeap?.Index` is the **logical heap index** (0 = Workstation,
   0-15 = Server GC CPU heaps), not the GC generation (0/1/2). The naming is misleading and has
   caused confusion in earlier pipeline work. `PerLogicalHeapSummary` is correctly named; the
   local variable is not.

3. **`sohObjects = -1` sentinel can be lost.** When `objCount >= 0` for a non-SOH segment but a
   later SOH segment sets `sohObjects = -1`, the aggregate is correctly marked as unknown.
   However, the inverse is not guarded: if `sohObjects` was already set to `-1` from one segment,
   subsequent `sohObjects += countedObj` for later SOH segments with `objCount >= 0` (impossible
   today but possible if SOH mix changes) would add to the sentinel. The current guard:
   ```csharp
   if (objCount >= 0) sohObjects += countedObj; else sohObjects = -1;
   ```
   is correct as written, but the logic is distributed and non-obvious.

4. **`pohObjects` / `frozenObjects` use `int`.** Objects counts use `int`. LOH/POH/Frozen segments
   on very large dumps could exceed `int.MaxValue` if aggregated (unlikely but possible in extreme
   POH usage scenarios). The `HeapSegmentSnapshot.ObjectCount` field is also `int`.

5. **`lohPercent` / `pohPercent` use committed not total heap size.** This means adding more SOH
   segments (e.g., after GC growth) changes the reported LOH percentage even if LOH itself did not
   change. Using total heap committed is the right denominator for this metric; the current
   implementation is technically correct for "LOH share of heap" but should be documented.

6. **`TotalUsedBytes` is misleading when `CountSohObjects = false`.** SOH used bytes are zero
   (the `used` out-parameter in `CountObjects` is only incremented for objects that are enumerated).
   `TotalUsedBytes` therefore significantly understates actual live object bytes in the default
   profile, but the report surfaces it as "used bytes" without caveat.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!eeheap -gc` reports:

- Total GC heap size (committed and reserved).
- Per-logical-heap committed / reserved split (Server GC).
- SOH gen0 / gen1 / gen2 size per logical heap.
- LOH size per logical heap.
- POH and Frozen object heap segments.

`HeapTopologyAnalyzer` matches or exceeds `!eeheap` on most dimensions except **generation
breakdown**. `!eeheap` always reports Gen0 / Gen1 / Gen2 sizes; the analyzer omits these.

### PerfView

PerfView's GC Stats view shows heap size trends over time with generation decomposition. The
trend comparer covers growth trends but omits generation-level data.

### Visual Studio Memory Usage

Provides live heap size by generation in the GC Heap Snapshot view. Generation decomposition is
the default and primary view.

### JetBrains dotMemory

Offers generation histogram, segment map, and per-type generation breakdown. The per-type
generation attribution (which generation holds the most instances of a type) is a
high-differentiation diagnostic not present in this analyzer.

### Competitive Gap Summary

The **universal standard** across all .NET memory tooling is Gen0 / Gen1 / Gen2 byte
decomposition. This is the most glaring competitive gap. Every other major tool treats generation
sizes as a first-class metric; `HeapTopologyAnalyzer` does not expose them at all despite ClrMD
providing the data via `ClrSegment.Generation0/1/2`.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

The analyzer has a correct and well-reasoned design: it avoids materializing the heap, streams
segments, separates concerns from `SegmentReservationAnalyzer`, and handles the SOH-skip fast path
explicitly. The infrastructure and boundary decisions are sound.

However, the **most important heap diagnostic — generation byte decomposition — is absent**, and
several quality issues reduce confidence in the numbers that are present (`TotalUsedBytes`
understatement, misleading fragmentation finding, `generation` variable misnaming).

**Production readiness: Conditional.** Safe to ship for basic topology snapshots. Not production
ready as a standalone memory investigation tool because the missing generation data is the first
question every engineer will ask.

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| 1 | Fix `HeapTopologyFindingGenerator` fragmentation finding — the evidence, attribution, and recommendation are incorrect for a committed-vs-used metric | High: prevents misleading guidance in production reports | Low | High | Improvement | ✅ DONE |
| 2 | Add Gen0 / Gen1 / Gen2 byte totals using `ClrSegment.Generation0/1/2` ranges in the existing segment loop | High: closes the biggest competitive gap; zero extra passes needed | Low | High | Improvement | ✅ DONE |
| 3 | Fix `TotalUsedBytes` report caveat — either exclude it from the report when `CountSohObjects = false` or add a note that it excludes SOH | High: prevents misinterpretation of a key metric | Low | High | Improvement | ✅ DONE |

#### P1 — High

| # | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| 4 | Add per-kind fragmentation (committed − used) to `HeapTopologyDomainResult` and report | High: LOH fragmentation is actionable; SOH fragmentation indicates GC compaction opportunity | Low | High | Improvement | ✅ DONE |
| 5 | Switch `SegmentKindMapper.Map()` from string-based dispatch to `GcSegmentKind` enum comparison | Medium: correctness + zero-alloc; prevents silent misclassification on ClrMD updates | Low | High | Improvement | ✅ DONE |
| 6 | Rename local `generation` variable in the main loop to `logicalHeapIndex` to eliminate conceptual confusion | Low effort, prevents ongoing bugs | Trivial | High | Improvement | ✅ DONE |
| 7 | Add cancellation check inside the `CountObjects` inner loop (currently uncancellable for large SOH scans) | Medium: operator experience on full-scan mode | Low | High | Improvement | ✅ DONE |

#### P2 — Medium

| # | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| 8 | Change `SegmentTypeAccumulator` from mutable struct in Dictionary to a class, or use `CollectionsMarshal.GetValueRefOrAddDefault` to eliminate redundant copy | Low-medium on large POH | Low | High | Improvement | ✅ DONE |
| 9 | Extend `HeapTopologyTrendComparer` to track SOH bytes, total reserved, and reservation gap | Medium: enables VM growth trending | Low | High | Improvement | ✅ DONE |
| 10 | Add segment object-density column (objects / committed MB) to the top-segments table | Medium: distinguishes dense vs. sparse segments | Low | Medium | Improvement | ✅ DONE |
| 11 | Add per-kind fragmentation to `HeapTopologyFindingGenerator` with correct attribution | Medium | Medium | High | Improvement | ✅ DONE |
| 12 | Share the segment enumeration loop with `SegmentReservationAnalyzer` via a shared segment-summary type or merge the passes — scoped and implemented per [heap-segment-shared-pass-plan.md](../../refactor/heap-segment-shared-pass-plan.md) | Medium: eliminates duplicated classification logic; perf win is secondary since segment counts are small | Medium (revised down from High — see plan) | Medium | Evolution | ✅ DONE |

#### P3 — Low

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| 13 | Expose `ClrHeap.IsServer` and `ClrHeap.HeapCount` in the result and section | Low: diagnostic context | Trivial | High | Improvement | ✅ DONE |
| 14 | Change `HeapSegmentSnapshot.ObjectCount` and aggregated counters from `int` to `long` | Low: avoids theoretical overflow on extreme POH | Low | Medium | Improvement | ✅ DONE |
| 15 | Add progress reporting for SOH scan in full mode (currently silent for 87M-object pass) | Low: operator experience only | Low | High | Improvement | ⛔ N/A — superseded, see note below |
| 16 | Use the `Unknown` enum value in `SegmentKindMapper` for unrecognized segment kinds instead of silently defaulting to SOH | Low: future-proofing | Trivial | High | Improvement | ✅ DONE |

> **Note (2026-08-27, item #11):** Implementing SOH/Frozen fragmentation findings surfaced a
> pre-existing data bug: `SohFragmentedBytes` was always equal to `SohBytes` (i.e. reported as
> ~100% fragmented) because SOH is never walked per-object, so `sohUsedBytes` stayed 0. Fixed by
> deriving `sohUsedBytes` exactly from Phase 1's `TypeAggregates.TotalSize` sum minus the
> LOH/POH/Frozen used-byte walks — the same free-derivation pattern already used for `sohObjects`.
> When Phase 1's index is unavailable, `SohFragmentedBytes` now reports 0 (unknown) rather than a
> misleading 100%. `HeapTopologyFindingGenerator` now emits SOH fragmentation findings (attributed
> to pinned-handle compaction blocking, not manual LOH-style compaction) and Frozen fragmentation
> findings (informational — frozen data is never collected, so free space there is address-space
> overhead, not reclaimable garbage).

> **Note (2026-08-27, items #13/#14):** `HeapTopologyDomainResult` now carries `IsServerGc`
> (`ClrHeap.IsServer`) and `LogicalHeapCount` (`ClrHeap.SubHeaps.Length` — there is no
> `ClrHeap.HeapCount`), surfaced as `gc_mode`/`logical_heap_count` key metrics in the report
> section, matching the convention `SegmentReservationSectionBuilder` already used for `gc_mode`.
> The logical-heap skew check — previously an inline `blocks.Add(T(...))` text block with no
> severity/tags/`MetricValue` — is now a real `InsightFinding` in `HeapTopologyFindingGenerator`
> (severity `Warning`, `MetricValue` = skew ratio), so it's trend-tracked and ranked like every
> other finding. Separately, `HeapSegmentSnapshot.ObjectCount`, `SegmentKindSummary.ObjectCount`,
> `PerLogicalHeapSummary.ObjectCount`, and the SOH/LOH/POH/Frozen aggregate counters in
> `HeapTopologyAnalyzer` (`sohObjects`, `lohObjects`, etc.) are now `long` instead of `int`,
> removing the `int.MaxValue` clamp that used to sit on the derived SOH object count. All 361
> `Unit.Analysis` tests pass unchanged.

> **Note (2026-08-27, items #15/#16):** Item #15 is **not applicable** — verified against the
> current codebase there is no `CountSohObjects` flag or any other "full scan" toggle anywhere in
> `HeapTopologyAnalyzer` or its options; `CountObjects` unconditionally returns the `-1` sentinel
> for `HeapSegmentKind.SmallObjectHeap` and SOH's exact object count/used bytes are always derived
> arithmetically from Phase 1's index (see the P0-2/#2 and #11 notes above). The "full mode" this
> item describes was superseded by that architectural decision before this audit pass, so adding
> progress reporting for a SOH walk that can no longer happen would be dead code — declining rather
> than implementing it.
>
> Item #16 is done: `SegmentKindMapper.Map` now explicitly enumerates every known `GCSegmentKind`
> (`Generation0/1/2`, `Ephemeral`, `Large`, `Pinned`, `Frozen`) and only falls back to
> `HeapSegmentKind.Unknown` for a value it doesn't recognize — no longer silently treating a
> corrupted/unrecognized segment as SOH (this was also flagged as Audit Area 6 risk #1). Unknown
> segments now get their own tracked bucket in `HeapTopologyAnalyzer` (count/bytes/reserved/used/
> fragmented, folded into `TotalCommittedBytes`/`TotalUsedBytes`/`TotalReservedBytes` and into the
> SOH-used-bytes derivation so they're no longer double-counted into SOH), appear as a normal row
> in the Kind Summary table, and — if any are present — surface a dedicated Warning finding in
> `HeapTopologyFindingGenerator` calling out likely dump corruption or an unhandled ClrMD version.
> All 361 `Unit.Analysis` tests pass unchanged.

> **Cross-referenced (2026-08-26):** [memory-analyzer-audit.md](memory-analyzer-audit.md)'s
> "Report `ClrHeap.IsServer` and per-heap balance metrics for Server GC" item was marked
> superseded and pointed here — the balance metrics (`PerLogicalHeapSummary` + skew warning)
> already live in this analyzer, so item #13 above is the single tracked place for the remaining
> `IsServer` gap. Note API correction from ClrMD 4 inspection: there is no `ClrHeap.HeapCount`
> property — use `ClrHeap.SubHeaps.Length` (each `ClrSubHeap.Index` is the logical heap index
> already surfaced via `PerLogicalHeapSummary.LogicalHeapIndex`). Also worth folding into #13:
> the skew warning is currently an inline text block (`blocks.Add(T(...))`) rather than a real
> `InsightFinding`, so it has no severity/tags/`MetricValue` and isn't trend-tracked or ranked
> alongside other findings — promoting it would be a natural companion change.

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. It is safe for basic topology snapshots
   and the segment-loop path is correct. The fragmentation finding (P0 #1) and the silent
   `TotalUsedBytes` understatement (P0 #3) must be fixed before the section is trusted in
   production incident reports.

2. **Highest-impact improvements:** Add Gen0/Gen1/Gen2 byte decomposition (P0 #2) and fix the
   fragmentation finding (P0 #1). Both are low-effort changes that dramatically increase diagnostic
   value and report correctness.

3. **Platform evolution opportunities:** A shared segment-pass infrastructure between
   `HeapTopologyAnalyzer` and `SegmentReservationAnalyzer` (P2 #12) would eliminate duplicated
   iteration, reduce ClrMD pressure on large dumps, and enforce a single source of truth for
   committed / reserved figures. This is the highest-value platform evolution from this audit.

4. **Highest engineering return:** P0 #2 (generation breakdown) delivers the most diagnostic value
   per line of code changed. The data is already available in `ClrSegment.Generation0/1/2`; it
   requires only additional accumulation in the existing segment loop and new fields in the domain
   result and section builder.
