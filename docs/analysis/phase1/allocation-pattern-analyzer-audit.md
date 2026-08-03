# AllocationPatternAnalyzer — Phase 1 Audit

**Protocol:** [phase1-analyzer-architecture-review.md](../phase1-analyzer-architecture-review.md)  
**Date:** 2026-08-03  
**Reviewer roles applied:** Principal .NET Runtime Engineer, CLR/GC Specialist, Memory Diagnostics Engineer, Performance Engineer, SRE, Software Architect

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

A pure Phase-2 post-processor. Reads `TypeAggregateIndexEntry` data built during Phase 1 heap scan, computes generation distribution percentages, classifies heap-wide allocation profile (`Transient / Steady / Retained / Mixed`) and GC pressure level, and produces per-type lists bucketed by lifetime (transient / medium / long-lived). No heap scan, no ClrMD enumeration.

**Cohesion:** Good. The role is tightly scoped to "what does the heap's generation distribution tell us about allocation behaviour?" and does not stray into leak detection or root analysis.

### Coverage Gaps

- **Gen1 is a second-class citizen.** `Gen1CountPct` and `Gen1SizePct` appear in key metrics and trend comparers but are never used for classification, selection, or findings. High Gen1 counts indicate objects surviving the first collection — a meaningful signal for object lifetime tuning — that goes completely unexplored.
- **No absolute byte reporting.** The analyzer reports only percentages. An engineer cannot tell whether 3% Gen2 represents 30 MB or 30 GB. Without raw totals, severity is unquantifiable.
- **Finalizable objects ignored.** Finalizable type detection (via GC handles or type metadata) is absent. Finalizable objects that survive to Gen2 are a well-known GC pressure source.
- **LOH treated as a single bucket.** The LOH section shows size%, but no fragmentation estimate, no object count per size band, no pinned-object contribution. All of these are available from `TypeAggregateIndexEntry`.
- **`AllocationProfile.Steady` is unreachable at type level.** `ClassifyProfile` can return `Steady` for the heap-wide signal (gen0Pct > 50%), but the per-type classification inside `Analyze` only assigns `Transient`, `Retained`, or `Mixed`. The `TypeAllocationProfile.Profile` enum value `Steady` is dead code.
- **No ephemeral heap pressure signal.** Gen0 + Gen1 combined (ephemeral segment pressure) is never computed or surfaced.

### Expansion Opportunities

- Compute and expose absolute byte totals alongside percentages (low effort, high value).
- Identify types with high Gen1 counts relative to Gen0 (survival rate heuristic) as a distinct finding.
- Flag types marked finalizable (via `TypeAggregateFlags` or a new flag) as elevated-risk long-lived candidates.
- Surface LOH object count distribution by size band (e.g., 85 KB – 1 MB, 1 MB – 10 MB, > 10 MB).

### Architectural Observations

- The ordering dependency on `GCGenerationAnalyzer` (noted in the XML doc: "Must run immediately after...") is enforced only by `DefaultAnalyzerFactory` list position. There is no runtime guard; if factory order changes, the analyzer silently degrades to an empty result rather than failing fast. A `Debug.Assert` or a cache-presence check with a meaningful exception would be safer.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Key metrics cover both count% and size% for all four generations — this dual view is correct and valuable.
- Trend comparer tracks ten numeric metrics across snapshots, including type-level gen2 counts and ratios.
- The disclaimer ("Allocation-site precision is ETW-dependent") appropriately scopes expectations.
- Three per-type tables (transient / medium / long-lived) are conditionally rendered only when non-empty.

### Weaknesses

1. **One generic finding regardless of profile.** `AllocationPatternFindingGenerator` always emits exactly one `InsightFinding`, even when pressure is `Low`. An informational finding with no actionable content adds noise to the insight feed.
2. **Finding evidence contains no type names.** The `evidence` string lists only aggregate percentages. An engineer reading the finding cannot immediately see which types are responsible. At minimum, the top long-lived type name should be embedded.
3. **`PromotionPressureScore` has no documented scale.** It appears as a bare numeric key metric. The thresholds (0–20 Low, 20–45 Moderate, 45–70 High, >70 Critical) are not surfaced anywhere in the report. An engineer seeing `PromotionPressureScore: 42.1` has no frame of reference.
4. **GC pressure scoring formula mixes count% and size% linearly.** `(gen0CountPct * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2)` produces a misleading composite. A heap with 99% transient Gen0 objects scores 29.7 ("Moderate") despite being well-behaved — Gen0 dominance should reduce, not increase, pressure.
5. **`ClassifyProfile` thresholds are not configurable.** `AllocationPatternAnalysisOptions` exposes `TransientClassificationThreshold`, but `ClassifyProfile` ignores it and uses hard-coded 70% and 50% values.
6. **Section builder "Classification summary" table is redundant.** It shows two rows (`AllocationProfile` and `GCPressureLevel`) that are already visible in key metrics directly above it.
7. **No Gen1-specific table or finding.** Despite Gen1 data being available and trended, nothing surfaces high-Gen1 types to the engineer.
8. **No absolute totals in type tables.** The per-type tables show count and ratio but no size. An engineer cannot rank types by retained bytes.

### Missing Diagnostics

- Total managed heap bytes (absolute) alongside percentage breakdown.
- Per-type size in the three type tables (TotalSize from `TypeAggregateIndexEntry`).
- High-Gen1 survivor table.
- LOH object count and fragmentation estimate.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage

The analyzer calls `context.Runtime?.Heap.GetTypeByMethodTable(mt)?.Name` for every type in the scan window to resolve type names. This is a live ClrMD heap traversal. On a scan window of `TopTypeLimit * ScanMultiplier = 40` entries (default), the cost is negligible; with `FullScan` and `MaxScanItemsAbsolute = 10000`, it becomes 10 000 synchronous ClrMD calls in a single allocation analysis pass.

**Recommendation:** Use `TypeMetadataCache` (already in `HeapAnalysisCache`) for type name lookup. The cache is populated during Phase 1 and provides O(1) lookup by MethodTable, eliminating live heap traversal.

### Infrastructure Utilization

- **`TypeMetadataCache` — not used.** The correct cache for MT→name resolution exists but is bypassed.
- **`SampleAddress` — not used.** `TypeAggregateIndexEntry.SampleAddress` provides a live object address for inspection. This could be used to resolve type names without a heap scan and to provide a concrete object reference in findings.
- **`StatisticsCache` — not used.** Pre-computed heap statistics (if present) are not consulted for total heap size validation.
- **`AnalyzerHelpers.ComputeApproxGenBytes`** — correctly extracted as shared infrastructure. Used consistently by both `GCGenerationAnalyzer` and this analyzer.

### Shared Infrastructure Opportunities

- Expose a `TryGetTypeName(ulong mt)` helper on `HeapAnalysisCache` that tries `TypeMetadataCache` first, then falls back to `GetTypeByMethodTable`.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics

| Opportunity | Value | Difficulty |
|---|---|---|
| Absolute byte totals per generation | High | Trivial — data already in aggregates |
| Per-type TotalSize in type tables | High | Trivial — field already in `TypeAggregateIndexEntry` |
| Gen1 survivor rate (Gen1Count / Gen0Count) | High | Low |
| Top types contributing to LOH size (absolute bytes) | High | Low |
| Finalizable type detection via `TypeAggregateFlags` | High | Medium |
| LOH object count by size band | Medium | Medium — requires adding size-band counters to Phase 1 |
| Ephemeral heap pressure metric (Gen0+Gen1 combined %) | Medium | Low |
| Pinned object count surfacing | Medium | Medium |
| Cross-type correlation (type present in multiple buckets) | Low | Low |

### Evidence Recommendations

- Add `TotalSize` (absolute bytes) to `TypeAllocationProfile` — the data exists and is unused.
- Embed the top long-lived type name in the `InsightFinding.Evidence` string.
- Document pressure score thresholds in the report (e.g., as a footnote in the section builder).

---

## Audit Area 5 — Performance, Memory & Scalability

### Performance Assessment

**Default configuration** (`TopN` strategy, `ScanMultiplier = 2`, `TopTypeLimit = 20`):  
Scans 40 entries, O(n log n) sort over all types. Negligible for typical heaps with < 100K distinct types.

**`FullScan` with `MaxScanItemsAbsolute = 10000`**:  
Sorts all types, then scans 10 000. The sort is O(n log n) over the full type set, which is correct but wasteful — sorting all types to examine only a capped subset does more work than necessary.

**Specific issues:**

1. **`metrics.Sort(comparator)` sorts before applying scan limit.** If there are 200K distinct types, all 200K are sorted even when only 10K will be examined. A partial sort (e.g., `SortedSet` with capacity cap, or `Array.Sort` only on the needed slice) would reduce work.

2. **Live `GetTypeByMethodTable` calls in the scan loop.** Covered in Area 3. 10K ClrMD calls is a measurable pause.

3. **Extra allocations in `ClassificationFirst` / `Mixed` modes.** Three candidate lists, a spill list, and a spill-metrics list are allocated as `List<T>` with default capacity. For large scan windows, these allocations are significant. Pooling or pre-sized allocation would help.

4. **No cancellation in inner loops.** `cancellationToken.ThrowIfCancellationRequested()` is called once at entry. The per-type loops (up to 10K iterations) have no cancellation checks. A FullScan on a huge type set cannot be interrupted.

5. **No progress reporting.** Large FullScan passes emit no progress. `IProgress<AnalyzerProgressReport>` is available on the cache but never used by this analyzer.

6. **Dead variable computation** — `accountedGen` and `nonLohTotal` are computed in the totaling loop but never referenced in any calculation. These add trivial overhead but signal incomplete implementation.

### Scalability Assessment

For dumps in the 1–100 GB range the bottleneck is ClrMD type name resolution, not the arithmetic. With `TypeMetadataCache` substituted, the analyzer should scale linearly with distinct type count (which grows slowly relative to dump size).

---

## Audit Area 6 — Correctness & Confidence

### Correctness Issues

**Critical:**

1. **`TypeAggregateIndexEntry.Gen0Count/Gen1Count/Gen2Count` are `int` (4 bytes).** Per the binary format comment, these are 32-bit signed. On a dump with more than ~2.1 billion objects of a single type, these silently overflow to negative values. The outer `Count` field is `long` (8 bytes), so the overflow is asymmetric. When `Gen0Count` is negative, `AnalyzerHelpers.ComputeApproxGenBytes` performs `(ulong)e.Gen0Count * avgSize` — the cast of a negative `int` to `ulong` produces a ~18 EB value, catastrophically corrupting byte estimates. The `TypeAggregateIndexEntry` binary format should promote these to `long` or `int` should be validated to be non-negative before use.

2. **`ClassifyProfile` ignores `TransientClassificationThreshold` from options.** The threshold is exposed in `AllocationPatternAnalysisOptions` with a documented default of 70.0, but `ClassifyProfile` hard-codes `70.0` and `50.0`. The option has no effect on the heap-level profile classification — only on per-type selection thresholds.

**Moderate:**

3. **Gen0 dominance inflates GC pressure score.** The formula `(gen0CountPct * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2)` can classify a healthy heap with many short-lived Gen0 objects as "Moderate" pressure. Gen0 collection is cheap; high Gen0 count is not inherently problematic. The 0.3 Gen0 weight produces false positives.

4. **`accountedGen` and `nonLohTotal` computed but unused.** These variables are assigned in the totaling loop but never read. Either they represent incomplete analysis or dead code left over from a refactor. If unused, they should be removed; if intended, the missing logic should be implemented.

5. **`TypeAllocationProfile.Gen0Count/Gen1Count` are `int`.** Clamped via `(int)Math.Min(int.MaxValue, mtGen0)` before assignment. For any type with more than 2.1B Gen0 objects the report will show `int.MaxValue` — silently misleading rather than an explicit overflow indicator.

### False Positive Risk

- A heap with 80% Gen0 objects by count but 100% actually collected before dump will score "Moderate" GC pressure and profile as `Transient`. If the dump was taken during a GC pause before collection, the score is temporarily inflated. There is no mechanism to detect or qualify this scenario.

### Confidence Assessment

The analyzer produces defensible approximate signals but cannot be treated as authoritative without the correctness issues addressed. The gen count overflow (item 1) is the only issue that could produce grossly wrong outputs.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

- `!dumpheap -stat` lists every type by count and size. Provides raw totals; no generation breakdown by type.
- `!gcwhere` resolves an object's generation. No per-type aggregate.
- DumpDetective's per-type generation buckets exceed SOS capability, but SOS provides absolute sizes which DumpDetective currently omits.

### PerfView

- Allocation sampling via ETW gives call-site attribution. DumpDetective explicitly disclaims this.
- GC stats view shows gen0/1/2 collection counts and durations — information that requires ETW and is not available from a dump. DumpDetective is correctly scoped.
- **Gap:** PerfView's "Large Object Heap Survival" and "Pinned Object" reports have no DumpDetective equivalent.

### Visual Studio Memory Usage

- Snapshot comparison shows type-level delta between two heap states.
- DumpDetective has a trend comparer that tracks metric deltas across snapshots — comparable capability.
- VS provides a "Dominated by" view (dominator tree). DumpDetective has no dominator analysis.

### JetBrains dotMemory

- "Survived after GC" analysis identifies objects that survived N collections.
- Retention tree and dominator tree are core features.
- Gen-level analysis per type with size and count.
- **Gap:** DumpDetective has no retention tree, dominator analysis, or "survived after N collections" view. These are the two highest-value missing capabilities relative to industry tools.

### Competitive Summary

DumpDetective's generation distribution analysis is on par with or better than SOS/WinDbg. The critical differentiating gap versus dotMemory and VS is the absence of retention/dominator analysis and the omission of absolute byte totals at the per-type level.

---

## Final Executive Summary

### Overall Assessment

**Score: 62/100**  
**Production readiness: Conditional** — functional and non-blocking for standard production use, but contains one latent correctness defect (gen count int overflow) that could corrupt results on extreme heaps.

**Major Strengths:**
- Zero heap scan — pure Phase-2 post-processor with bounded, predictable performance.
- Dual count/size percentage reporting for all four generations.
- Configurable selection algorithm (mode, strategy, priority, thresholds) with preset support.
- Trend comparer with ten tracked metrics.
- Correctly shared gen-byte approximation logic via `AnalyzerHelpers`.

**Major Weaknesses:**
- `Gen0Count/Gen1Count/Gen2Count` stored as `int` in `TypeAggregateIndexEntry` — overflow risk on large heaps with silent data corruption.
- `ClassifyProfile` does not use `TransientClassificationThreshold` from options.
- No absolute byte totals — percentages alone are insufficient for severity assessment.
- Live ClrMD type name resolution instead of `TypeMetadataCache`.
- GC pressure formula assigns positive weight to Gen0%, which inflates pressure for healthy transient-heavy heaps.
- Gen1 survival signal entirely absent from findings and type tables.
- Dead variables (`accountedGen`, `nonLohTotal`) indicate incomplete analysis.

---

### Priority Roadmap

| ID | Recommendation | Area | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|---|
| P0-1 | Promote `Gen0Count/Gen1Count/Gen2Count` in `TypeAggregateIndexEntry` from `int` to `long`; add overflow guard in `ComputeApproxGenBytes` | 6 | Critical — prevents data corruption on large heaps | Medium | High | Improvement |
| P0-2 | Fix `ClassifyProfile` to read `TransientClassificationThreshold` from options instead of hard-coding 70.0 | 6 | High — option is documented and exposed but ignored | Low | High | Improvement |
| P1-1 | Use `TypeMetadataCache` for type name resolution; add `TryGetTypeName(ulong mt)` helper to `HeapAnalysisCache` | 3, 5 | High — eliminates 10K live ClrMD calls in FullScan; enables faster name resolution | Low | High | Improvement |
| P1-2 | Add absolute byte totals (`totalManagedBytes`, per-gen bytes) to `AllocationPatternDomainResult` and surface in section builder | 2, 4 | High — required for severity assessment | Low | High | Improvement |
| P1-3 | Add `TotalSize` to `TypeAllocationProfile`; render in per-type tables | 2, 4 | High — enables ranking types by retained bytes | Low | High | Improvement |
| P1-4 | Revise GC pressure formula — remove or invert Gen0% term; Gen0 dominance should reduce pressure, not increase it | 2, 6 | High — eliminates false-positive "Moderate" findings on healthy transient heaps | Medium | High | Improvement |
| P1-5 | Remove dead variables `accountedGen` and `nonLohTotal`; either implement their intended use or delete them | 6 | Medium — correctness signal; may indicate incomplete analysis | Low | High | Improvement |
| P2-1 | Add Gen1 survivor rate heuristic (Gen1Count / Gen0Count by type); surface as a separate type table or dedicated finding | 1, 4 | High — identifies types that survive first collection | Medium | High | Improvement |
| P2-2 | Add cancellation checks inside inner loops (especially FullScan path) | 5 | Medium — prevents uninterruptible long pauses | Low | High | Improvement |
| P2-3 | Add progress reporting via `IProgress<AnalyzerProgressReport>` for FullScan | 5 | Medium — improves operator visibility on large dumps | Low | Medium | Improvement |
| P2-4 | Replace full `metrics.Sort` + scan-limit pattern with partial sort for `FullScan` strategy | 5 | Medium — reduces wasted sort work on large type sets | Medium | High | Improvement |
| P2-5 | Embed top long-lived type name in `InsightFinding.Evidence`; suppress finding at `Low` pressure | 2 | Medium — reduces noise, improves actionability | Low | High | Improvement |
| P2-6 | Document pressure score thresholds in section builder (footnote or legend) | 2 | Medium — without scale context the numeric score is uninterpretable | Low | High | Improvement |
| P3-1 | Surface `AllocationProfile.Steady` at type level or remove from `TypeAllocationProfile.Profile` enum | 1 | Low — cleanup | Low | High | Improvement |
| P3-2 | Add runtime guard (exception or assertion) when `HeapIndexBuildResult` is absent at analysis time | 1 | Low — improves debuggability when factory order regresses | Low | High | Improvement |
| P3-3 | LOH size-band distribution (85KB–1MB, 1MB–10MB, >10MB) — requires Phase 1 index extension | 4 | High (future) | High | Medium | Evolution |
| P3-4 | Finalizable type detection via `TypeAggregateFlags` or a new Phase 1 flag | 4 | High (future) | High | Medium | Evolution |
| P3-5 | Dominator tree / retention tree analysis | 7 | Critical competitive gap | Very High | High | Evolution |

---

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally yes. It is safe for heaps where per-type Gen0/Gen1/Gen2 object counts fit in 32-bit signed integers (~2.1B objects per type). On extreme heaps this bound can be exceeded, producing silently corrupt byte estimates. P0-1 must be resolved before declaring it unconditionally production-ready.

2. **Highest-impact improvements:** P0-1 (int overflow), P1-2 (absolute bytes), P1-3 (per-type size), P1-4 (pressure formula), P1-1 (TypeMetadataCache).

3. **Platform evolution opportunities:** Adding a `TryGetTypeName` helper to `HeapAnalysisCache` (P1-1) benefits every analyzer that currently calls `GetTypeByMethodTable` live. Promoting gen-count fields to `long` in `TypeAggregateIndexEntry` (P0-1) is a binary format change that affects all Phase 1 serialization and all consumers of the aggregate index.

4. **Highest engineering return:** P0-1 + P1-4 together fix the two correctness issues with lowest implementation cost. P1-1 + P1-2 + P1-3 together transform the report from percentage-only summaries to actionable, quantified diagnostics with minimal additional infrastructure.
