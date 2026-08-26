# MemoryAnalyzer Audit

> **Protocol**: phase1-analyzer-architecture-review.md
> **Subject**: `MemoryAnalyzer` + `MemoryAnalysisProjection` + `MemoryDomainResult` +
> `MemoryAnalysisOptions` + `MemoryAnalysisSectionBuilder` + `MemoryFindingGenerator` +
> `MemoryAnalyzerTrendComparer` + `BoundedGraphWalk`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`MemoryAnalyzer` produces a managed-heap composition snapshot from cached type statistics.
It does not scan the heap directly; it delegates to `IHeapAnalysisCache.GetOrBuildTypeStatistics`
and reads pre-built `GlobalSizeBuckets` from `HeapIndexBuildResult` when available.
`MemoryAnalysisProjection` performs multi-dimensional type ranking (size, count, LOH, average
instance size, composite pressure score) and computes aggregate metrics passed downstream to
`MemoryDomainResult`.

The role is well-defined and cohesive: generate a prioritized type-level memory snapshot in
zero heap-scan time (Phase 2 cost is O(T log T) in unique type count, not O(N) in objects).

### Coverage

**Covered:**
- Total heap bytes, LOH bytes, LOH%
- Total object count and unique type count
- Top types ranked by composite pressure (weighted: size 40%, count 35%, LOH 15%, avg-size 10%)
- Size-bucket histogram (from Phase 1 global buckets or type-average fallback)
- Top 1/5/10 byte concentration metrics
- Small object count and byte percentages
- Objects-per-MB density
- Composite memory pressure score (0–100)
- Estimated retained bytes per top type (single-sample BoundedGraphWalk BFS)
- Sample instance address per top type
- Module name per top type
- Trend comparison across snapshots
- Three profile presets (Fast, Balanced, Full)

**Not covered:**
- GC generation breakdown per type (Gen0/Gen1/Gen2/LOH object distribution)
- Committed vs reserved heap memory (segment-level)
- LOH fragmentation (free-list size and fragmentation ratio)
- Pinned object count and bytes per type
- Native/unmanaged memory footprint
- String duplication summary (string count, unique value count, estimated savings)
- Array oversizing patterns (byte[], char[], object[] with high capacity waste)
- Finalization queue contribution per type
- Per-heap-segment breakdown for Server GC with multiple heaps

### Missing Functionality

- No analysis of `heap.Segments` — committed vs reserved bytes, segment count, segment
  fragmentation are entirely absent despite being available at zero extra heap scan cost.
- `MemoryPressureScore` has hardcoded calibration thresholds undocumented in code or output.
- `IAnalyzer.Tags` returns the default empty collection — the analyzer is undiscoverable
  by tag-based queries.
- `IAnalyzer.Order` uses the default `0`, placing MemoryAnalyzer without intentional
  pipeline position relative to analyzers that depend on its output.
- `IsThreadSafe` is not declared (inherits `false` from interface default). For an analyzer
  that only reads from cache this could safely be `true`.

### Adjacent Opportunities

- Segment-level committed/reserved/fragmentation is a natural extension requiring only
  `heap.Segments` enumeration at the analyzer level; no new index is needed.
- Native memory reporting (`ClrRuntime.NativeHeap`, if available) would complete the
  memory picture alongside managed heap size.
- Cross-referencing `MemoryDomainResult.TopTypes` with `GCGenerationDomainResult` type
  profiles in `InsightEngine` is already done, but the two results are never merged into a
  unified per-type view visible to users.

### Architectural Observations

- The analyzer correctly avoids heap scanning and operates on cached data — this is a strong
  architectural choice consistent with project principles.
- The public `Analyze(ClrHeap heap, IHeapAnalysisCache cache)` overload with default options
  is useful for tests but exposes an internal API surface; a test-specific factory method
  would be cleaner.
- `MemoryAnalysisProjection` is internal and well-encapsulated.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- `MemoryAnalysisSectionBuilder` produces a ranked-bar chart of top types by bytes and a
  size-bucket histogram chart — both are immediately useful at a glance.
- The compact table includes Count, Total Bytes, LOH Bytes, Avg Size, Est. Retained, Sample
  Address, and Module — a comprehensive per-type row.
- Key metrics dictionary (`memory_pressure_score`, `total_bytes`, `loh_pct`, `top5_share`,
  `objects_per_mb`, etc.) feeds dashboards and trend views well.
- `MemoryAnalyzerTrendComparer` tracks per-type byte and count deltas across snapshots,
  including histogram bucket deltas — strong regression-detection capability.
- The `InsightEngine` uses `TopTypes` to detect DataTable/DataRow accumulation and SqlClient
  TdsParser closure accumulation — valuable cross-analyzer signal.

### Weaknesses

- **`MemoryFindingGenerator` produces exactly one finding** regardless of heap state.
  It only gates on `LohPercent >= 40` for Warning vs Info. There is no finding for:
  - High memory pressure score (e.g., score ≥ 70)
  - Memory concentration (top-5 types > 80% of heap)
  - High object density (objects/MB > some threshold indicating micro-allocation pressure)
  - Dominant single type consuming majority of heap
  - Suspicious memory-only metrics like zero LOH on a heap > 2 GB (unusual for real workloads)

- **Report narrative is minimal.** The section opens with one sentence
  (`"Memory pressure score: X/100 (Band)."`) and then goes directly to charts. There is no
  interpretation or explanation of what the pressure score components mean, no callout of the
  dominant type, no LOH fragmentation risk statement.

- **`EstimatedRetainedBytes` is presented without qualification.** The column appears
  alongside exact TotalBytes without any indication it is a heuristic single-sample estimate
  from a bounded BFS walk. Engineers may over-trust this column for large, structurally
  complex types (e.g., `Dictionary<K,V>`, `List<T>`, `ConcurrentQueue<T>`).

- **No finding for small-object pressure.** `SmallObjectCountPercent` and
  `SmallObjectBytesPercent` are computed but never generate a finding. High small-object
  density (> 85% of objects < 85 bytes) is a GC throughput red flag.

- **Histogram buckets use type-average size** when `GlobalSizeBuckets` is unavailable (the
  fallback path in `MemoryAnalysisProjection`). The resulting histogram silently misrepresents
  size distribution for heterogeneous types, and there is no indication in the report of which
  path was taken.

  > **Correction + resolved (2026-08-26):** the fallback path does not actually build a
  > second, approximate `SizeBucketHistogram` — when `GlobalSizeBuckets` is unavailable,
  > `MemoryAnalysisProjection.Build` leaves `histogram` as `null` entirely (verified by reading
  > the current code); only `SmallObjectCountPercent`/`SmallObjectBytesPercent` have a real dual
  > computation path (exact, from the bottom 3 Phase-1 buckets, vs. approximate, from per-type
  > average size). The genuinely missing signal was that the section silently rendered nothing
  > when the histogram was absent, with no indication of *why* or that the small-object
  > percentages shown were the approximate variant. `MemoryAnalysisSectionBuilder` now adds an
  > explanatory note ("Object size histogram unavailable for this run... 'Small object %' above
  > is approximated...") whenever the histogram is null on a non-empty heap.

- **Sample addresses are shown but not actionable.** The `0x{addr:X}` column in the table
  has no WinDbg-ready `!do` command hint or copy-paste assistance — a minor friction point
  for incident engineers.

### Missing Diagnostics

- Per-type generation distribution (what fraction of a type lives in Gen2 vs Gen0)
- LOH fragmentation ratio (free list / LOH total)
- Pinning hotspots (types with high GCHandle pin counts)
- Native heap size alongside managed heap size
- String heap summary (total string bytes, top string values by count)

### Missing Statistics

- `Top1BytesPercent` is computed but not surfaced as a key metric in the section builder
- `MemoryPressureScore` sub-component breakdown (LOH pressure, concentration pressure,
  small-object pressure, density pressure) — useful for explaining why the score is high

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage

The analyzer contains **zero direct ClrMD API calls** in its main path. All heap data flows
through `IHeapAnalysisCache`, which is architecturally correct. BFS retained estimation in
`BuildDomainResult` calls `heap.GetObject(sampleAddress)` and delegates to
`BoundedGraphWalk.ComputeExclusiveRetained` — appropriate shared infrastructure.

### Available ClrMD Information Not Used

- `heap.Segments` — exposes per-segment committed, reserved, and object bytes; generation
  assignments; fragmentation ratio. This is available without any heap scan.
- `heap.TotalHeapSize` — a single property summarizing total managed heap bytes (cross-check
  against sum-of-types).
- `runtime.Heap.CanWalkHeap` — not checked before the retained BFS is attempted; if the heap
  is not walkable the BFS silently returns 0 via the `catch`.
- `ClrHeap.IsServer` / `ClrHeap.HeapCount` — relevant for multi-heap Server GC environments
  where memory analysis per-heap would be valuable.

### Infrastructure Usage

- `cache.GetOrBuildTypeStatistics` — correct primary path.
- `cache.GetSampleInstanceAddress` — correct secondary path for retained estimation.
- `heapCache.TryGetHeapIndex` — correctly reads `GlobalSizeBuckets` from Phase 1 result to
  avoid a second heap scan for histogram data.
- `BoundedGraphWalk.ComputeExclusiveRetained` — correct use of shared BFS primitive with
  the shared `claimedAddresses` set for exclusive retained semantics across top types.

### Infrastructure Gaps

- No use of `TypeShapeCache` from `HeapIndexBuildResult` — field layout data is available
  but unused by this analyzer. For average-size validation or per-field breakdown it would
  be relevant.
- No use of the `InMemoryEventCandidates` or `InMemoryTaskCandidates` pre-filtered lists —
  not applicable here, correct.
- The `IHeapAnalysisCache` abstraction is the right boundary; the analyzer does not need
  to reach into the concrete `HeapAnalysisCache` except for the `TryGetHeapIndex` call,
  which is cast-guarded (`heapCache is HeapAnalysisCache heapCache`). This cast breaks the
  abstraction and could fail silently if a test passes a non-`HeapAnalysisCache` implementation.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Additions

**1. GC Segment Summary (high impact, zero extra scan)**
`heap.Segments` is enumerable with no heap object scan. For each segment, ClrMD provides:
committed bytes, reserved bytes, FirstObject, LastObjectAddress, generation. A segment table
in the report (size, generation, committed, free bytes) is the first thing SREs look at to
understand LOH fragmentation and server-GC heap imbalance.

**2. LOH Fragmentation Ratio**
Free bytes in the LOH are available from `heap.Segments` where
`segment.Kind == GCSegmentKind.Large`. The ratio `free / committed` for the LOH is a
critical metric for diagnosing allocation failures and GCHandle-induced fragmentation.

**3. Per-Type Generation Profile for Top Types**
The GCGenerationAnalyzer computes `PerTypeGenerationProfiles` but this data is never
correlated into the memory snapshot. Knowing that 95% of `System.Byte[]` bytes live in Gen2
(pinned or long-lived) vs Gen0 (transient) is diagnostic for both leak detection and GC
throughput analysis. `GCGenerationDomainResult` is available at the point `InsightEngine`
runs — the correlation could be embedded there.

**4. String Heap Summary**
Total string bytes and top string values by count are already partially captured by
`StringDedupIndex` but not surfaced in `MemoryDomainResult`. A compact "string heap"
summary (total bytes, duplicate %, top 5 content previews) would be high-value for many
real-world incidents.

> **Resolved (2026-08-26):** `StringAnalyzer` already owns a complete, dedicated section
> (total bytes, `PctOfManagedHeap`, `DuplicationRatio`, top-5 duplicate previews) — duplicating
> that data inside `MemoryDomainResult`/`MemoryAnalysisSectionBuilder` would be redundant
> storage of an already-computed fact. Instead, `InsightEngine.DetectStringMemoryConcentration`
> cross-references the Memory section's own top-types-by-size ranking against
> `StringDomainResult`: when `System.String` ranks in the memory section's top 10 types **and**
> duplication waste is significant (≥ 5 MB), it emits a finding with an evidence table of the
> top duplicate string values — giving the memory-section reader "String is your #N largest
> type, and here's why" without re-deriving or re-storing String Analysis's own data.

**5. Pinned Object Count**
GCHandle-pinned objects contribute to LOH fragmentation and can prevent compaction.
`ClrRuntime.EnumerateHandles()` with `HandleKind.Pinned` is already used by the GCHandle
analyzer, but the count and bytes of pinned objects per type is not reflected in the memory
snapshot.

**6. `MemoryPressureScore` Sub-Score Breakdown**
The composite score is composed of four sub-signals (LOH pressure, top-5 concentration,
small-object pressure, density). Exposing these individually as key metrics would allow
engineers to diagnose which dimension is elevated rather than just reacting to the aggregate.

**7. Array Capacity Waste**
Large `byte[]`, `char[]`, and `object[]` instances frequently have significant unused
capacity. ClrMD can read the array length field; comparing `Length * elementSize` against
the object's actual size reveals the capacity. This is a high-value detection for buffer
pool over-allocation.

**8. Heap Imbalance (Server GC)**
For `heap.IsServer == true`, per-heap object counts and bytes (iterating segments by their
`IsLargeObjectSegment` and heap index) would identify unbalanced allocation across CPU heaps
— a common root cause of elevated GC pause times.

> **Superseded (2026-08-26):** This already exists — `HeapTopologyAnalyzer` computes
> `PerLogicalHeapSummary` (per-`ClrSubHeap.Index` committed bytes, % of total, object count,
> segment count) and `HeapTopologySectionBuilder` renders it as a "Per logical heap" table with
> a skew warning (`maxBytes > 2× minBytes`). Building a second copy into `MemoryDomainResult`
> would duplicate already-owned data (same anti-pattern flagged and avoided on the P2-4 string
> heap summary item). The one genuinely open gap — exposing `ClrHeap.IsServer` itself as an
> explicit fact, plus promoting the skew warning from an inline text block to a real
> `InsightFinding` (severity/tags/trend-tracked) — is tracked in
> [heap-topology-analyzer-audit.md](heap-topology-analyzer-audit.md) roadmap item #13, not here.

---

## Audit Area 5 — Performance, Memory & Scalability

### Heap Scan Behavior

`MemoryAnalyzer` itself performs **no heap scan** — all data flows from cache. This is
correct and scalable to arbitrarily large dumps.

### Projection Allocation Profile

`MemoryAnalysisProjection.Build` creates **five separate `List<CachedTypeStatistics>`**
copies from `typeStats.Values`, then sorts each independently:

```csharp
var bySize = new List<CachedTypeStatistics>(typeStats.Values);   // copy 1
var byCount = new List<CachedTypeStatistics>(typeStats.Values);  // copy 2
var byLoh = new List<CachedTypeStatistics>(typeStats.Values);    // copy 3
var byAverageSize = new List<CachedTypeStatistics>(typeStats.Values); // copy 4
var byCompositePressure = new List<CachedTypeStatistics>(typeStats.Values); // copy 5
```

For a heap with 50,000 unique types, this allocates 5 × 50,000 reference-sized list entries
(~2 MB overhead) plus five O(T log T) sort operations. Only `byCompositePressure` and
`bySize` (for top-N bytes metric) are ultimately used in the output; `byCount`, `byLoh`,
and `byAverageSize` are sorted but their results are not used at all in the current code —
the selection is done entirely via the composite pressure sort.

**Optimization**: Drop the four mono-dimensional sorts entirely; use `byCompositePressure`
(already containing all types) as the single ranked list. Use `bySize` only for the
`top1Bytes`/`top5Bytes`/`top10Bytes` computation (or compute inline with a linear pass).

### Retained Estimation Cost

`EstimateRetained` runs `BoundedGraphWalk.ComputeExclusiveRetained` per top type,
defaulting to `maxBreadth = 10,000` and `maxDepth = 20`. For 20 top types this is up to
200,000 `heap.GetObject()` calls — each a potential disk I/O on large dumps. This is the
analyzer's only O(M × BFS) cost.

No progress is reported between the initial `0%` report and completion, so for very large
dumps the analyzer appears hung during retained estimation.

> **Resolved (2026-08-26):** `RetainedSizeCandidateSelector.SelectAndCompute` (shared by
> `MemoryAnalyzer`, `DominatorAnalyzer` ×2, and `GCRootAnalyzer`) now accepts an optional
> `IProgress<AnalyzerProgressReport>?` and reports once per completed walk — cheap since it's
> bounded by the same small `maxCandidatesToWalk` cap that already limits BFS cost.
> `MemoryAnalyzer` now threads its own `progress` through `BuildDomainResult` into this call
> instead of dropping it. While wiring this through, `MemoryAnalyzer` was also passing no
> `CancellationToken` into this call at all (separate from the BFS-inner-loop gap noted below,
> which is still open) — fixed to pass its real token, and the surrounding `catch` (which
> previously swallowed every exception, including `OperationCanceledException`, into an empty
> result) now rethrows cancellation instead of silently absorbing it.

### Cancellation

`CancellationToken` is checked once per candidate in `RetainedSizeCandidateSelector.SelectAndCompute`
(and, as of the above fix, is now actually passed in from `MemoryAnalyzer`). The BFS inner loop in
`BoundedGraphWalk.ComputeExclusiveRetained` itself still does not check the token. On a 100 GB dump with
a 10,000-breadth BFS over 20 types, cancellation may be delayed by seconds.

> **Resolved (2026-08-26):** `ComputeExclusiveRetained` now takes an optional
> `CancellationToken` and checks it once per dequeued BFS node (same pattern already used by
> `CollectForwardTypeNames` in the same file), so cancellation during a single type's 10,000-node
> walk is no longer delayed until the whole batch finishes. `RetainedSizeCandidateSelector`
> (its only caller) now forwards its own token through. Added
> `BoundedGraphWalkTests.ComputeExclusiveRetained_PreCancelledToken_ThrowsOperationCanceled`.

### Scalability on Large Dumps (1 GB – 100 GB)

| Scale        | Behavior |
|---|---|
| 1 GB         | No issues. O(T log T) with T ≈ 5,000 types is negligible. |
| 10 GB        | Retained BFS is the bottleneck: up to 200K `GetObject` calls, each potentially disk-backed. Expected: seconds. |
| 100 GB       | BFS cost is linear in breadth × types; may take tens of seconds. The 5-list allocation pattern remains memory-bounded (T ≈ 50K–100K types, ≈ 10–20 MB allocation). |

The scalability bottleneck is **retained BFS**, not the projection math.

---

## Audit Area 6 — Correctness & Confidence

### Integer Overflow Risk

`MemoryDomainResult.TotalObjects` and `LohObjects` are both `int`. For dumps with more than
~2.1 billion live objects (theoretically possible on a 100 GB Server GC heap with millions
of small objects), these fields silently overflow. `MemoryAnalysisProjection` accumulates
`totalObjects += stat.Count` where `stat.Count` is also `int` — overflow is additive.
Consider promoting to `long`.

### Histogram Approximation

When `GlobalSizeBuckets` is unavailable, the histogram uses **average object size per type**
to assign each type's total bytes to a single bucket. This is incorrect for types with high
size variance (e.g., `System.String`, `System.Byte[]`, jagged arrays). The resulting
histogram may show 90% of objects in a single bucket based on the dominant average rather
than the actual distribution. The report does not distinguish between the exact (Phase 1)
histogram and the approximated (average-based) histogram.

### Retained Estimate Accuracy

`EstimatedRetainedBytes` is computed from a single sample instance per type. For types
with structurally variable instance graphs (containers, caches, connection pools),
the estimate may be off by one or two orders of magnitude. The `claimedAddresses` set
is shared across types in ranked order, so retained estimates for lower-ranked types are
systematically under-reported (objects already "claimed" by a higher-ranked type cannot
be counted again). This exclusive-retained semantics is intentional but undocumented in
the report.

### Confidence of `MemoryPressureScore`

The composite pressure score uses four hardcoded normalization denominators:
- LOH pressure: 35% → 1.0
- Concentration: top-5 = 70% → 1.0
- Small-object pressure: combined (85% by count, 45% by bytes) → 1.0
- Density: 12,000 objects/MB → 1.0

These thresholds are empirically-derived and not validated against a reference dataset.
The score is useful for relative trend comparison but should not be used as an absolute
health indicator without calibration. No confidence band or uncertainty is reported.

### Edge Cases

- **Empty heap** (TotalObjects = 0): handled — all percentages return 0, no division by
  zero.
- **Single type heap**: projection handles correctly; top-1 = 100%, pressure score is
  computed without issue.
- **Heap with no LOH**: LohPercent = 0, pressure correctly low — handled.
- **`heap.GetObject(sampleAddress)` on stale address**: caught by the top-level `catch`
  in `EstimateRetained`; silently returns 0. This is acceptable but the failure is
  unobservable in the output.
- **`CanWalkHeap = false`**: the `catch` around the BFS handles walk failures, but the
  analyzer does not proactively check `heap.CanWalkHeap` before attempting BFS, resulting
  in noisy exception handling.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

| SOS Command | DumpDetective Coverage |
|---|---|
| `!dumpheap -stat` | Covered — TopTypes table covers type count + size. |
| `!dumpheap -min 85000` | Covered — LohBytes, LohObjects, TopTypes includes LOH column. |
| `!loh` (LOH fragmentation) | **Not covered** — free list size and fragmentation ratio absent. |
| `!gcroot <addr>` | Partially — single-sample BFS provides a retention estimate, not full root chain. |
| `!address -summary` (segments) | **Not covered** — no segment-level committed/reserved breakdown. |

### PerfView

PerfView's GC heap snapshot provides per-generation retention trees (dominator-based
retained size). DumpDetective's single-sample BFS is a coarse approximation. The
`DominatorAnalyzer` is a closer analog to PerfView's retention tree, but its output is
not correlated into the memory section.

### Visual Studio Memory Usage

VS Memory Usage provides a type → retained size view driven by a full dominator tree.
This is not replicated by `EstimatedRetainedBytes` (single-sample, bounded BFS).

### JetBrains dotMemory

dotMemory shows:
- Per-generation object count and bytes per type — **missing** from MemoryAnalyzer
- Heap fragmentation view — **missing**
- Dominators panel with full retained-size tree — approximated only

### Competitive Opportunities

- **Segment view** (committed, reserved, free per generation): zero additional cost, high
  diagnostic value; differentiates DumpDetective from WinDbg in accessibility.
- **LOH fragmentation ratio**: a single metric that is absent from most automated tools
  despite being critical for diagnosing `OutOfMemoryException` in large heaps.
- **Per-type generation heat**: knowing whether a type's objects are predominantly in Gen2
  vs Gen0 is invaluable and is already almost achievable by joining with
  `GCGenerationDomainResult`.

---

## Final Executive Summary

### Overall Assessment

**Score: 68/100**

**Production Readiness: Conditional** — sufficient for basic memory triage on dumps up to
~10 GB; the findings layer is too thin for autonomous diagnosis of non-trivial heap problems.

**Major Strengths:**
- Zero heap-scan design — scales correctly to very large dumps
- Composite pressure ranking correctly surfaces heterogeneous memory pressure signals
- Trend comparisons and histogram bucket deltas provide regression-detection capability
- `InsightEngine` cross-analyzer integration (DataTable, SqlClient patterns) adds
  material value on top of raw metrics
- `BoundedGraphWalk` reuse is architecturally correct

**Major Weaknesses:**
- `MemoryFindingGenerator` generates one undifferentiated finding — inadequate for
  automatic diagnosis
- LOH fragmentation, segment-level data, and generation breakdowns are absent despite
  being zero-cost extensions
- `EstimatedRetainedBytes` accuracy is poor and the approximation is unlabeled
- Five redundant sort copies in `MemoryAnalysisProjection` — four go unused
- `int` object count overflow risk on very large heaps
- Histogram accuracy degrades silently without `GlobalSizeBuckets`

### Priority Roadmap

| Priority | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| P0 | Expand `MemoryFindingGenerator` with findings for high pressure score (≥ 70), top-type concentration (> 80%), small-object pressure | High | Low | High | Improvement | ✅ **DONE** |
| P0 | Guard `heap.CanWalkHeap` before BFS retained estimation; propagate `MemoryAnalyzer.IsThreadSafe = true` | Medium | Low | High | Improvement | ✅ **DONE** |
| P1 | Add GC segment summary to `MemoryDomainResult` (`heap.Segments`) — committed, reserved, free bytes per generation | High | Low | High | Improvement | ✅ **DONE** |
| P1 | Add LOH fragmentation ratio (free-list bytes / LOH committed bytes) | High | Low | High | Improvement | ✅ **DONE** |
| P1 | Promote `TotalObjects` and `LohObjects` from `int` to `long` | Medium | Low | High | Improvement | ✅ **DONE** |
| P1 | Remove 4 redundant sort copies from `MemoryAnalysisProjection`; use composite sort for selection, linear scan for top-N bytes | Medium | Low | High | Improvement | ✅ **DONE** |
| P1 | Label `EstimatedRetainedBytes` as approximate in section builder; add tooltip or footnote | Medium | Low | High | Improvement | ✅ **DONE** |
| P2 | Expose `MemoryPressureScore` sub-components (LOH, concentration, small-object, density) as separate key metrics | Medium | Low | Medium | Improvement | ✅ **DONE** |
| P2 | Break the `heapCache is HeapAnalysisCache` cast — surface `TryGetHeapIndex` on the `IHeapAnalysisCache` abstraction | Low | Medium | High | Improvement | ✅ **DONE** |
| P2 | Add per-type generation distribution cross-reference with `GCGenerationDomainResult` in `InsightEngine` | High | Medium | High | Evolution | ✅ **DONE** |
| P2 | Add string heap summary (total string bytes, top duplicates) to memory section | Medium | Medium | High | Evolution | ✅ **DONE** (via `InsightEngine` cross-reference — see note below) |
| P2 | Report `Top1BytesPercent` as a named key metric; add finding when top-1 type > 40% of heap | Medium | Low | High | Improvement | ✅ **DONE** |
| P3 | Report `ClrHeap.IsServer` and per-heap balance metrics for Server GC | Medium | Medium | Medium | Evolution | ⚠️ **SUPERSEDED** — see note below |
| P3 | Instrument BFS retained estimation with mid-analysis progress reports | Low | Low | High | Improvement | ✅ **DONE** |
| P3 | Add `CancellationToken` check inside `BoundedGraphWalk.ComputeExclusiveRetained` | Low | Low | High | Improvement | ✅ **DONE** |
| P3 | Distinguish Phase-1 (exact) vs fallback (approximate) histogram in section builder | Low | Low | High | Improvement | ✅ **DONE** |

### Final Verdict

1. **Production-ready?** For read-only memory triage — yes. For autonomous diagnostic
   conclusions — no. The single-finding reporting layer does not alert engineers to many
   actionable heap conditions that the underlying data already captures.

2. **Highest-impact improvements:** Expand `MemoryFindingGenerator` (P0), add GC segment
   summary and LOH fragmentation (P1). Both require little code but materially improve
   incident triage.

3. **Platform evolution opportunities:** Per-type generation correlation with
   `GCGenerationDomainResult` and a server-GC heap-balance report are natural evolutions
   that leverage existing infrastructure.

4. **Highest engineering return:** The P0/P1 finding expansions and the projection
   deduplication (removing 4 wasted sorts) are low-effort, high-value changes that can
   ship together. The `int → long` object count promotion prevents a latent correctness
   defect that is difficult to diagnose in production.
