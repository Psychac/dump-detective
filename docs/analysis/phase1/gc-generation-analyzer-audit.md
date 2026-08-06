# GCGenerationAnalyzer Audit

**Analyzer:** `GCGenerationAnalyzer`
**Category:** GC
**Protocol:** [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)
**Audited:** 2026-08-03

---

## Components Reviewed

| Component | File |
|---|---|
| Analyzer | `src/DumpDetective.Analysis/Analyzers/GCGenerationAnalyzer.cs` |
| Domain model | `src/DumpDetective.Analysis/Models/GCGenerationDomainResult.cs` |
| Options | `src/DumpDetective.Core/Options/GCGenerationAnalysisOptions.cs` |
| Finding generator | `src/DumpDetective.Reporting/FindingGenerators/GCGenerationFindingGenerator.cs` |
| Section builder | `src/DumpDetective.Reporting/SectionBuilders/GCPressureSectionBuilder.cs` |
| Trend comparer | `src/DumpDetective.Analysis/Trend/Comparers/GCGenerationTrendComparer.cs` |
| Shared helper | `src/DumpDetective.Analysis/Analyzers/AnalyzerHelpers.cs` |
| Tests | `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/GCGenerationAnalyzerDiscrepancyTests.cs` |
| Benchmark | `src/BenchmarkSuite1/GCGenerationAnalyzerBenchmark.cs` |

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`GCGenerationAnalyzer` answers one structural question: how is managed memory distributed across GC generations (Gen0, Gen1, Gen2, LOH) at snapshot time? It produces per-generation object counts and approximate byte totals, a top-N LOH types list, and per-type generation profiles. It feeds the GC Pressure section (B1), the Executive Summary, the Type System section, and trend comparisons.

The role is **well-scoped and cohesive**. The analyzer is focused, single-purpose, and has clear downstream consumers.

### Coverage Gaps

1. **No POH (Pinned Object Heap) awareness.** .NET 5+ introduced the POH. The domain model and analysis have no awareness of it — `LohBytes`, `LohObjects`, and `LohPercent` silently conflate LOH and POH, potentially misrepresenting the GC structure on modern runtimes.

2. **No gen byte accuracy flag in the result.** `Gen0Bytes`/`Gen1Bytes`/`Gen2Bytes` are computed via `AnalyzerHelpers.ComputeApproxGenBytes`, which multiplies per-generation counts by the per-MT average non-LOH object size. The result is an approximation. Nothing in `GCGenerationDomainResult` or the report signals this to the consumer. An engineer looking at the numbers has no visibility into the approximation error.

3. **No Gen0/Gen1 LOH split.** The `TypeAggregateIndexEntry` already distinguishes `Gen0Count`, `Gen1Count`, `Gen2Count` separately from `LohCount`. The domain model exposes these correctly via `TypeGenerationProfile`. However the top-level `GCGenerationDomainResult` only has `LohBytes` and `LohObjects` — no LOH size breakdown per generation (which would be zero in a well-formed heap since LOH is always collected as Gen2, but the model does not communicate this).

4. **No SOH/LOH size breakdown.** The result exposes `LohBytes` but has no explicit SOH total (`Gen0Bytes + Gen1Bytes + Gen2Bytes`). Downstream code in `GCPressureSectionBuilder` must sum them manually and never does — `loh_pct` is computed from `memory.LohPercent` in `ExecutiveSummarySectionBuilder`, not from the GC generation result.

5. **LOH fragmentation not captured.** LOH fragmentation is a critical GC concern. The analyzer knows LOH size and top LOH types but does not capture free-list bytes, fragmentation ratio, or pinned handles contributing to fragmentation.

6. **No finalizer pressure cross-reference.** `TypeAggregateFlags.IsFinalizableType` is surfaced via `TypeGenerationProfile.IsFinalizable` in the section builder, but the total count of finalizable objects in Gen2/LOH that are awaiting finalization is not computed or reported.

### Unexpected Functionality

None. The analyzer boundary is clean.

### Expansion Opportunities

- Absorb POH awareness as a natural extension of the generation model.
- Add LOH fragmentation metrics (requires walking LOH segments for free objects).
- Compute SOH vs LOH total as a first-class metric.
- Emit a data-quality flag indicating whether gen byte values are exact or approximate.

### Architectural Observations

- The `Analyze(ClrHeap, IHeapAnalysisCache)` public overload on the concrete class (not part of `IAnalyzer`) bypasses the progress reporting parameter. It exists for tests; consider whether this warrants a dedicated test helper instead.
- The analyzer does not implement `Tags`, `Order`, or `IsThreadSafe` — it relies on interface defaults. Other analyzers in the suite have these populated, creating inconsistency in how the module catalog drives the pipeline. In this case the defaults are harmless but should be explicit.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

1. **Two-tier findings are well-calibrated.** LOH >= 35% triggers Warning, Gen2 >= 50% triggers Warning (Critical at >= 75%). These thresholds are reasonable production heuristics.
2. **App-domain type prioritization in Gen2 finding.** The finding generator deliberately shows non-framework types first for actionability — a sound engineering decision.
3. **`GCPressureSectionBuilder` is rich.** The section includes key metrics, threshold-aware narrative blocks, top LOH types with full gen profile columns (Gen0, Gen1, Gen2, LOH Count, Total Bytes), per-type survival ratios, and finalizable flags. This is significantly more useful than a raw number dump.
4. **Trend comparer tracks meaningful metrics.** `gc.gen2.bytes`, `gc.loh.bytes`, `gc.loh.percent`, `gc.total.objects`, `gc.loh.objects` are the right signals for cross-dump trend analysis.

### Weaknesses

1. **Gen0 finding is absent.** A very high Gen0 object count is a signal of allocation pressure and potential throughput degradation. There is no finding for this case. The LOH and Gen2 findings are correct but the picture is incomplete.

2. **Gen2 byte approximation is silently presented.** The finding titles present `Gen2Bytes` as fact (e.g. `"Gen2 holds X GB"`). But `Gen2Bytes` is a heuristic estimate. An engineer may act on an inaccurate byte figure without knowing it is approximate.

3. **LOH finding threshold is fixed at 35%.** For applications with intentional large buffer pools (e.g. ASP.NET Kestrel, SignalR), 35% LOH may be completely expected. The finding has no mechanism to acknowledge an application's expected LOH profile, so it generates noise. `GCGenerationAnalysisOptions` could carry a configurable threshold.

4. **`LohThresholdBytes` option is declared but never used.** `GCGenerationAnalysisOptions.LohThresholdBytes` (default 85,000) is defined but there is no code path in the analyzer or finding generator that consults it. Dead option.

5. **The LOH finding is always emitted.** Even when LOH is healthy (e.g. 2%), a low-severity `Info` finding is unconditionally created. This adds noise to the findings list for healthy dumps.

6. **Survival ratio in the section is computed inline.** `GCPressureSectionBuilder` computes survival ratio (`(Gen2Count + LohCount) / totalCount`) on the fly for display. This is valuable enough to be a first-class field in `TypeGenerationProfile` or computed once in the analyzer rather than repeated in presentation.

7. **No LOH fragmentation metric in the report.** See Area 1 — the section cannot report LOH fragmentation because it is not extracted.

### Missing Diagnostics

- Gen0 allocation pressure signal.
- LOH fragmentation ratio.
- Pinned object count contributing to gen compaction pressure.
- Approximate vs. exact label for gen byte values.
- Cross-reference to `FinalizableObjectAnalyzer` for the finalizable-in-Gen2 count.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD API Usage

The fast path (`BuildFromIndex`) does not call ClrMD directly during analysis — it reads from the pre-built `TypeAggregates` dictionary. It calls `heap.GetTypeByMethodTable(mt)` only for the top-N types to resolve names, which is correct and efficient.

The fallback path (`BuildFromTypeStatistics`) reads `CachedTypeStatistics` from the cache rather than re-scanning the heap. This is correct — it avoids a redundant heap walk.

**ClrMD opportunity missed:** `ClrHeap.GetGcSegments()` (or `Segments` property on the runtime's heap) exposes raw segment data including per-segment size, generation ranges, and whether a segment is LOH or POH. The analyzer never calls this. Segment data would enable:
- Exact gen byte totals (not approximate).
- LOH fragmentation ratio (free space in LOH segments).
- POH detection.

**The gen byte approximation is the single most significant correctness risk.** `ComputeApproxGenBytes` multiplies per-MT gen counts by the per-MT average non-LOH object size. This is wrong when a type has high size variance (e.g. arrays of variable length, strings). The error compounds when many large variable-length objects are split across generations. Using `ClrHeap.Segments` with `ClrSegment.LogicalHeap`, `FirstObjectAddress`, and `CommittedMemory` would give exact bytes per generation without a heap rescan.

### Infrastructure Utilization

- `TypeAggregates` from `HeapIndexBuildResult` is well-utilized: per-MT gen counts, flags, sizes, sample addresses.
- `TypeAggregateFlags.IsFinalizableType` is surfaced in the profile. Good.
- The `SampleAddress` field in `TypeAggregateIndexEntry` is available but never used by this analyzer. It could enable fast type-name resolution without a full `GetTypeByMethodTable` call in some cases.

### Shared Infrastructure Opportunities

- `AnalyzerHelpers.ComputeApproxGenBytes` is shared with `AllocationPatternAnalyzer`. If an exact segment-based implementation replaces it, both analyzers benefit simultaneously.
- The fallback path `BuildFromTypeStatistics` assigns all non-LOH objects to Gen2 — this is the maximum pessimism assumption and is likely inaccurate for any live-process dump. If segment data were used in the fallback, it would be more precise.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics Not Currently Extracted

| Diagnostic | Value | Source |
|---|---|---|
| **POH size and object count** | Critical on .NET 5+ — confusing LOH/POH causes misdiagnosis | `ClrSegment.Kind == GCSegmentKind.Pinned` |
| **LOH fragmentation ratio** | Fragmented LOH causes `OutOfMemoryException` even with available committed memory | LOH free list: `obj.IsNull` / `obj.IsFreeObject` on LOH walk |
| **Exact gen byte totals** | Current values are approximate — can mislead | `ClrHeap.Segments` committed/used per generation |
| **Gen0 allocation pressure** | Very high Gen0 → high allocation rate → GC throughput degradation | Gen0Count / TotalObjects ratio |
| **Ephemeral segment utilization** | Gen0+Gen1 near SOH segment limit → imminent Gen2 collection | Segment committed vs. reserved |
| **Types with highest Gen2 byte share** | More actionable than count-based ranking for memory investigation | Already available — rank by Gen2Count * avgSize |
| **Pinned handle count by type** | Pinned objects block compaction | `ClrRuntime.EnumerateHandles()` filtered for pinned kind |
| **Count of GC segments** | Fragmented segment map indicates long-running memory pressure | `ClrHeap.Segments.Count` |
| **Finalizable objects in Gen2/LOH awaiting collection** | Finalizer storm risk | Filter `IsFinalizableType` × Gen2/LOH count |

### High-Value Statistics Not Currently Captured

- `Gen2ByteShare` = Gen2Bytes / TotalManagedBytes (not just Gen2Pct by count).
- `LohFragmentationRatio` = LOH free bytes / LOH total bytes.
- `SohTotal` = Gen0Bytes + Gen1Bytes + Gen2Bytes.
- `PromotionCandidates` = count of Gen1 objects near promotion threshold.

### Investigation Workflow Opportunities

- A "generation pressure timeline" across multiple dumps (already partial via trend comparer, but missing segment-level data).
- Filterable per-type gen profile for investigations with thousands of types.

---

## Audit Area 5 — Performance, Memory & Scalability

### Fast Path (With Heap Index)

The fast path operates entirely on `TypeAggregates`, a pre-built in-memory dictionary. It performs:
- A single pass over `aggregates` (O(types)) for totals and candidate list building.
- A `List.Sort` on candidates (O(T log T)).
- `GetTypeByMethodTable` calls for top-N types only.

**This is efficient and scales well.** On a 25 GB dump with 10,000 types, the pass takes microseconds. Memory allocation is bounded: two temporary `List<>` objects, one for LOH candidates and one for gen candidates, each bounded by `aggregates.Count`.

**One unnecessary double allocation:** `lohCandidates` and `genCandidates` are built in the same loop but `genCandidates` is a full copy of all aggregates (every entry is added). On a dump with 50,000 types this is 50,000 tuples × ~24 bytes ≈ 1.2 MB. Not critical but avoidable — since profiling by `Count` is already done, `genCandidates` could reuse the sorted original list.

### Fallback Path (Without Heap Index)

The fallback path reads `CachedTypeStatistics` and assigns all non-LOH objects to Gen2. It allocates `lohList` and sorts it. This is fine — same complexity, bounded size. The correctness issue (all non-LOH → Gen2) is not a performance issue.

### Cancellation

`cancellationToken.ThrowIfCancellationRequested()` is called once at the entry point. For a purely index-based analysis this is acceptable since the inner loops are fast (sub-millisecond). However, for consistency with analyzers that perform heap walks, a cancellation check inside the main loop would be more robust.

### Progress Reporting

`progress?.Report(new(0, "reading type aggregates"))` is called at start. There is no progress increment during the main loop. For the GCGenerationAnalyzer's sub-millisecond fast path this is acceptable, but would be misleading if the analyzer ever performs a segment walk (which could take seconds on large dumps).

### Scale Assessment

- **1–25 GB dumps:** Fast path is effectively instant. No scalability concern.
- **25–100 GB dumps:** Index-based fast path scales with the number of unique types, not object count. No regression expected.
- **Fallback path:** Scales with the type statistics dictionary, not heap size. No regression.

---

## Audit Area 6 — Correctness & Confidence

### Gen Byte Approximation

**This is the most significant correctness risk.** `ComputeApproxGenBytes` computes:
```
gen0Bytes += Gen0Count * (nonLohSize / nonLohCount)
```
The per-MT average is computed over all non-LOH objects regardless of generation. For fixed-size types (most value wrappers, small objects) this is accurate. For variable-size types — `byte[]`, `string`, `object[]`, `List<T>._items` — objects with wildly different sizes accumulate across generations, and the per-MT average is a poor proxy for the per-generation slice. In workloads with large Gen0 short-lived buffers (common in networking code), `Gen0Bytes` can be significantly overestimated and `Gen2Bytes` underestimated.

**No consumer is currently warned about this.** The finding generator emits a title like `"Gen2 holds 4.72 GB"` — the word "approximately" is absent.

### Count Overflow (int Truncation)

`Gen0Objects`, `Gen1Objects`, `Gen2Objects`, `LohObjects`, and `TotalObjects` are all `int` in `GCGenerationDomainResult`. The analyzer guards these with `Math.Min(int.MaxValue, ...)`. For a 100 GB dump with hundreds of millions of objects this silently truncates. The truncation propagates to:
- `GCPressureSectionBuilder` where `gen2Pct` and `survivalR` are computed from the (potentially truncated) counts.
- `GCGenerationFindingGenerator` where `Gen2Objects` is formatted and emitted.

Gen0Count and Gen1Count within `TypeAggregateIndexEntry` are stored as `int`, so this constraint exists at the index level too — but the truncation should be explicit and documented.

### Fallback Path False Positives

In the fallback path, all non-LOH objects are attributed to Gen2. On a live-process dump where Gen0 dominates (e.g. a high-throughput service at a snapshot point), this will generate a spurious Gen2 dominance warning. This is a known limitation, but it is silent — no finding flag or report note indicates that the fallback path was taken.

### `LohThresholdBytes` Not Applied

The option `LohThresholdBytes = 85_000` exists but is never read. This is a dead configuration surface. Engineers who set it will observe no effect.

### Path-Dependent Analysis Output

The same dump analyzed via disk cache vs. in-memory cache is validated to agree by `GCGenerationAnalyzerDiscrepancyTests`. This test is well-constructed and provides meaningful regression coverage.

### Edge Cases

| Edge Case | Behavior | Risk |
|---|---|---|
| Zero objects on heap | `totalObjects == 0` → `lohPct` and `gen2Pct` are 0.0 (guarded) | Safe |
| All objects in LOH | `lohObjects == totalObjects` → `gen2Pct = 100%` falsely (LOH objects counted in total) | Misleading |
| MethodTable name resolution failure | `heap.GetTypeByMethodTable(mt)` returns null → fallback `MT:0x{mt:x}` | Acceptable |
| Gen counts stored as `int` but Count as `long` in index | `e.Gen0Count` is `int` in the index — if type count > int.MaxValue the index layer is the prior bottleneck | Acceptable |
| No heap index (fallback path) | Gen0/Gen1 bytes reported as 0, all non-LOH → Gen2 | Documented risk, not communicated to report consumer |

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!eeheap -gc` provides exact committed/reserved memory per GC segment and per generation. `!dumpheap -stat` provides per-type counts and sizes. SOS gives exact generation bytes from segment metadata — the approximation risk in DumpDetective has no equivalent in SOS.

**Gap:** DumpDetective does not expose GC segment topology (segment count, committed vs. reserved, ephemeral segment utilization). SOS `!eeheap -gc` gives this immediately. This is diagnostic value SOS provides that DumpDetective currently lacks.

**DumpDetective advantage:** Per-type generation profiles, survival ratios, and finalizable flags are not directly surfaced by `!eeheap`. DumpDetective's section builder is more organized and actionable.

### PerfView

PerfView's GC heap view shows object counts and sizes by generation, pinned object counts, and generation segment boundaries. It also shows LOH fragmentation as free bytes. The visualizations are interactive and filterable.

**Gap:** DumpDetective lacks LOH fragmentation data and POH awareness. PerfView surfaces both.

### Visual Studio Memory Usage

VS Memory Usage shows heap snapshots with type-level breakdowns but does not show per-generation distribution at the type level. DumpDetective's `TypeGenerationProfile` is **more detailed** than VS Memory Usage for generation analysis.

### JetBrains dotMemory

dotMemory shows generation distribution, LOH objects, survivor counts, and fragmentation. It surfaces "objects that survived X GC cycles" as a first-class diagnostic. DumpDetective has no equivalent — it knows Gen2 count (which implies survival) but cannot show how many GC cycles an object has survived (which would require GC age tracking not available in a static dump).

**dotMemory notable capability DumpDetective lacks:** LOH fragmentation, pinned object contribution to fragmentation, and a per-type dominance tree. All feasible from ClrMD data.

---

## Final Executive Summary

### Overall Assessment

**Score: 68 / 100**

**Production readiness:** Qualified yes — for dumps where the heap index is available. The fallback path gives results so misleading (all non-LOH → Gen2) that it should not be used in production reporting without a quality flag.

**Major strengths:**
- Correct two-path architecture (fast index path, graceful fallback).
- Well-integrated with the reporting pipeline (section builder, findings, trends, executive summary).
- Per-type generation profiles with survival ratios and finalizable flags are class-leading compared to competing tools for this specific view.
- Shared `ComputeApproxGenBytes` helper prevents code duplication across analyzers.
- Discrepancy test validates cache consistency.

**Major weaknesses:**
- Gen byte values are approximations presented as fact.
- No POH awareness on modern runtimes.
- `LohThresholdBytes` option is dead code.
- Fallback path silently misclassifies Gen0/Gen1 as Gen2.
- No LOH fragmentation data.
- No Gen0 allocation pressure finding.
- `int` overflow truncation is silent.

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| P0-1 | **Label gen byte values as approximate in domain result and report.** Add a `GenBytesAreApproximate` flag to `GCGenerationDomainResult`; surface it in the section builder and findings. | High — prevents engineer acting on inaccurate memory figures | Low | High | Improvement | ✅ DONE (commit bc83e77) |
| P0-2 | **Suppress or flag the fallback path result in reports.** When `BuildFromTypeStatistics` is used, add a `FallbackMode = true` indicator to the domain result; `GCPressureSectionBuilder` and the finding generator should note that Gen0/Gen1 are unknown and Gen2% is unreliable. | High — prevents false Critical/Warning findings | Low | High | Improvement | ✅ DONE (commit 5b0d188) |
| P0-3 | **Remove or implement `LohThresholdBytes`.** The dead option is a correctness trap. Either apply it (e.g. filter the top LOH types list to objects above the threshold) or remove it. | Medium — eliminates misleading configuration surface | Low | High | Improvement | ✅ DONE (commit 993c462) |

#### P1 — High

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P1-1 | **Replace `ComputeApproxGenBytes` with segment-based exact gen bytes.** Use `ClrHeap.Segments` to get per-generation committed bytes directly, eliminating the approximation. This also fixes the `AllocationPatternAnalyzer` which shares the same helper. | High — correct memory figures, removes approximation propagation | Medium | High | Improvement | ✅ DONE (commit 8234499) |
| P1-2 | **Add POH detection.** Check `ClrSegment.Kind` for `GCSegmentKind.Pinned`; add `PohBytes`/`PohObjects` to `GCGenerationDomainResult`. LOH and POH are different heaps on .NET 5+ and conflating them is a diagnostic error. | High — correctness on .NET 5+ | Medium | High | Improvement |
| P1-3 | **Add LOH fragmentation ratio.** Walk LOH segments for free list objects; compute `LohFreeBytes / LohCommittedBytes`. Add to domain result and section. | High — LOH fragmentation is a leading cause of OOM | Medium | High | Improvement |
| P1-4 | **Suppress the unconditional LOH `Info` finding for healthy dumps.** Only emit it when LOH > 20% (configurable) or when something meaningful can be said. | Medium — reduces noise for healthy dumps | Low | High | Improvement | ✅ DONE (commit 3bf3868) |

#### P2 — Medium

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P2-1 | **Add Gen0 allocation pressure finding.** When Gen0Objects > 40% of TotalObjects, emit a Warning with top Gen0 types. This signals high allocation rate that may degrade GC throughput. | Medium — actionable for throughput investigations | Low | High | Improvement | ✅ DONE (commit 9947f2c) |
| P2-2 | **Add `IsThreadSafe`, `Tags`, and `Order` explicitly.** Current defaults work but are inconsistent with other analyzers in the suite. | Low | Low | High | Improvement | ✅ DONE (commit ee670c5) |
| P2-3 | **Add trend metrics for Gen0 and Gen1 bytes** to `GCGenerationTrendComparer`. Currently only Gen2 and LOH are tracked. The absence of Gen0 trend makes allocation pressure invisible in multi-dump comparisons. | Medium | Low | High | Improvement | ✅ DONE (commit 732ec5e) |
| P2-4 | **Rank TypeGenerationProfile by Gen2 bytes (not Count) when byte data is available.** Count-based ranking can over-represent small high-count types. Byte-based ranking surfaces memory-heavy accumulators first. | Medium — better actionability for memory investigations | Low | Medium | Improvement |
| P2-5 | **Document `LohThresholdBytes` removal/implementation outcome in options class.** | Low | Low | High | Improvement | ✅ DONE (commit 5dcbcf2) |

#### P3 — Low

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P3-1 | **Compute `SohTotal` (Gen0+Gen1+Gen2 bytes) as a named metric.** Makes LOH-vs-SOH ratio explicit without requiring callers to sum three fields. | Low | Low | High | Improvement |
| P3-2 | **Remove the public `Analyze(ClrHeap, IHeapAnalysisCache)` overload.** It bypasses progress reporting and exists only for the test path. Replace with a test-scoped helper. | Low — cleanup | Low | Medium | Improvement |
| P3-3 | **Add a GC segment map diagnostic** showing segment count, committed memory, and ephemeral segment utilization. | Medium | High | Medium | Evolution |
| P3-4 | **Emit finalizable-in-Gen2 count as a cross-reference** to `FinalizableObjectAnalyzer`. This connects a signal that currently exists silently in the profiles. | Low | Low | High | Evolution |
| P3-5 | **Optimize `genCandidates` list.** Reuse the already-populated list instead of building a full copy of all aggregates for the gen profile sort. | Negligible in practice | Low | High | Improvement |

---

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. Fast path (heap index) produces accurate counts and useful diagnostics. Fallback path is not production-safe without a quality flag, and gen byte approximations are silently misleading.

2. **Highest-impact improvements:** P0-1 (label approximations), P0-2 (flag fallback mode), P1-1 (exact gen bytes via segments), P1-2 (POH), P1-3 (LOH fragmentation).

3. **Platform evolution opportunities:** Replacing `ComputeApproxGenBytes` with segment-based computation is a cross-cutting improvement benefiting at minimum GCGenerationAnalyzer and AllocationPatternAnalyzer. Adding POH segment kind awareness to the index builder would benefit any future pinned-memory analyzer.

4. **Highest engineering return:** P0-2 + P1-2 together address both a silent correctness failure (fallback silent misclassification) and a structural gap (.NET 5+ POH) for moderate effort. P1-1 eliminates the approximation risk shared across two analyzers.
