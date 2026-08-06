# ObjectShapeAnalyzer — Phase 1 Audit

> Protocol: `phase1-analyzer-architecture-review.md`
> Reviewer mindset: Principal .NET Runtime Engineer, ClrMD Expert, CLR/GC Specialist,
> Memory Diagnostics Engineer, Production SRE, Performance Engineer, Software Architect.

---

## Components Reviewed

| Component | Path |
|---|---|
| Analyzer | `src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs` |
| Domain model | `src/DumpDetective.Analysis/Models/ObjectShapeAnalyzerDomainResult.cs` |
| Options | `src/DumpDetective.Core/Options/ObjectShapeAnalysisOptions.cs` |
| Section builder | `src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs` |
| Finding generator | `src/DumpDetective.Reporting/FindingGenerators/ObjectShapeFindingGenerator.cs` |
| Trend comparer | `src/DumpDetective.Analysis/Trend/Comparers/ObjectShapeTrendComparer.cs` |
| Shared index | `HeapIndexBuildResult.TypeShapeCache`, `HeapIndexBuildResult.TypeAggregates`, `TypeShapeEntry` |
| Tests | `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/ObjectShapeAnalyzerDiscrepancyTests.cs` |

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`ObjectShapeAnalyzer` is a pure Phase-2 type-metadata analyzer with no heap object enumeration.
It joins `TypeShapeCache` (field layout per MethodTable, built during Phase 1) with
`TypeAggregates` (instance counts per MethodTable) to classify types by their
reference-to-total-field ratio and rank the top instances as GC-scan-cost hotspots.

The role is coherent and the Phase-2-only design is architecturally sound.

### Coverage Gaps

| Gap | Impact |
|---|---|
| **Balanced category absent from output** | The `ObjectShapeCategory.Balanced` bucket (refRatio 0.2–0.6) is computed but never collected into a ranked list and never reported. Most application types fall here; silently dropping this bucket means the report's coverage of dominant heap residents is structurally incomplete. | High |
| **Scalar category absent from output** | `ObjectShapeCategory.Scalar` types (zero fields) are also classified but never surfaced. Arrays and marker types fall here; the array shape specifically is never reported despite `IsArray` being captured. | Medium |
| **GC generation distribution ignored** | `TypeAggregates` contains `TypeAggregateIndexEntry` with count data; however, generation-bucketed counts (Gen0/Gen1/Gen2/LOH) are not currently cross-referenced. A type with 100,000 Gen2 reference-heavy instances is far more significant than the same count in Gen0. The ranking by `refRatio × instanceCount` therefore misrepresents promotion pressure. | High |
| **Total retained size not included** | Only instance count is used in ranking and output. `TypeAggregateIndexEntry` carries aggregate byte size — the combination of reference-heavy + large total bytes is the true GC scan budget signal; the current ranking misses types with few, very large instances. | High |
| **Array shape analysis absent** | `IsArray` is captured in `TypeShapeProfile` but the section builder does not produce an Array-shape list and the finding generator ignores it. Large arrays of reference types (e.g., `object[]`, `string[]`, `SomeClass[]`) impose the highest single-object GC scan cost but are invisible in the report. | High |
| **Static and thread-local field weight ignored** | `TypeShapeEntry.RefFields` counts only *instance* fields. Static reference fields are permanent GC roots that bypass generational collection entirely; their weight per type is not considered. | Medium |
| **No per-type byte-size average** | Average object size per type is not included in `TypeShapeProfile`. Large value types used as fields-within-fields affect LOH promotion; without size-per-instance, the shape profile has no allocation-budget context. | Medium |
| **Interface count sourced from `EnumerateInterfaces()`** | The interface count is computed live by calling `type.EnumerateInterfaces().Count()` inside the main loop, wrapped in a silent `catch { ifaceCount = 0; }`. This is both a correctness concern (exceptions silently zeroed) and a performance concern (unvetted allocation on the hot path per type). | Medium |

### Unexpected Functionality

None. The classifier and ranker are well-scoped.

### Adjacent Capabilities

- **Finalizable shape correlation** — the analyzer already captures `IsFinalizable`. Types that
  are both reference-heavy *and* finalizable impose a double GC cost (scan + finalization
  queue). A dedicated finding for this combination would be high value.
- **Inheritance chain as complexity signal** — `BaseTypeChainDepth` is captured but never
  referenced in any finding or ranking. Deep chains combined with many reference fields produce
  the highest field-resolution cost for GC; using this as a secondary sort key would improve
  actionability.
- **ValueHeavy large-struct detection** — captured but finding threshold is only by instance
  count. A type with 500 instances of 10 KB struct size can be more important than 500,000
  instances of 8-byte struct.
- **Per-analyzer tagging** — `ObjectShapeAnalyzer` does not override `IAnalyzer.Tags`,
  leaving the default empty collection. Tags like `["gc", "object-shape", "memory"]` would
  improve InsightEngine correlation.

### Architectural Observations

- `TypeShapeCache` is not persisted to disk. The `TypeAggregateIndexReader` (disk path)
  deserialises `TypeShapeEntry` values from the cached columnar data. The in-memory and
  disk paths are symmetric and the architecture is sound.
- The analyzer has no `Order` override, so it runs in default insertion order. For a
  pure-metadata analyzer (very fast), early ordering would allow downstream analyzers to
  consume its output sooner during streaming pipelines.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- The two-table layout (reference-heavy / value-heavy) is clear and scannable.
- All twelve columns per row provide sufficient raw data for manual investigation.
- `AvgRefFieldsPerType` and type counts as key metrics give a quick heap-character snapshot.
- The explanatory text block ("Reference-heavy types … are candidates for GC root retention")
  is accurate and useful.

### Weaknesses

| Weakness | Impact |
|---|---|
| **No ranking column** | Rows are output in the order they are added (`refHeavy.Count < options.TopListLimit`), which is the original instance-count sort order, not the stated GC-scan-cost rank of `refRatio × instanceCount`. The comment in the code promises this ranking but does not implement it — `refHeavy` and `valHeavy` are populated by simple list-capping, not a ranked sort. | High |
| **GC scan cost score absent from report** | The `refRatio × instanceCount` score that is the declared ranking criterion is never materialized as a column. Engineers cannot verify or act on what drove the ordering. | High |
| **Balanced and Scalar lists missing** | The two most numerically dominant categories are entirely absent from the report. An engineer looking at a heap dominated by `Balanced` types gets zero signal. | High |
| **ValueHeavy finding threshold is coarse** | The finding fires only if `TopValueHeavyTypes[0].InstanceCount >= 10_000`, with no size consideration. A 200-byte struct with 5,000 instances is ignored; a 16-byte struct with 10,001 instances triggers the finding. | Medium |
| **Finding severity ceiling is `Info`** | Both findings are at most `Info` or `Warning`. A dump where the top reference-heavy type has 10 million instances and 20 reference fields represents substantial GC scan pressure; this should be `Critical`. | Medium |
| **Recommendation for value-heavy types references `BoxingAnalyzer`** | The recommendation says "Consider using BoxingAnalyzer for struct-layout optimization." `BoxingAnalyzer` does not optimize layouts — it detects boxed instances. The recommendation is misleading; padding analysis belongs to struct field ordering and `[StructLayout]`. | Low |
| **`AvgRefFieldsPerType` computed over analyzed cap, not all types** | The key metric is described as a heap-wide average but is capped at `InstanceCountCap` (default 200) types. On a heap with 5,000 types, this average reflects only the 200 highest-instance-count types. The metric label does not disclose this. | Medium |
| **Missing total GC scan cost estimate** | The report has no aggregate signal: how many total reference-field pointer traces will the GC perform across all reference-heavy types? This is `Σ(RefFields × InstanceCount)` per type — a single number that characterises the heap's GC scan budget. | High |

### Missing Diagnostics

- Per-type GC scan cost score column: `RefFields × InstanceCount`.
- Aggregate GC scan work estimate: `Σ(RefFields × InstanceCount)` across all analyzed types.
- Array shape table: top reference-type arrays with element-count distribution.
- Finalizable + reference-heavy intersection list.
- Top Balanced types list (the silent majority).

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage

- `heap.GetTypeByMethodTable(mt)` per candidate is correct and necessary to access live
  `ClrType` metadata not stored in `TypeShapeEntry`.
- `type.IsFinalizable`, `type.IsValueType`, `type.IsArray`, `type.Name`, `type.BaseType` —
  all are appropriate and correctly guarded.
- `type.EnumerateInterfaces().Count()` — allocates an enumerator per type in the loop.
  ClrMD 3.x returns an `IEnumerable<ClrInterface>` whose implementation is typically
  list-backed. Using `Count()` is correct but the silent `catch` on exception is
  a smell: if ClrMD throws, the correct response is to log and propagate, not zero the count.
  This should use the `ILogger` pattern established for heap analyzers.

### Platform Infrastructure Utilization

- `TypeShapeCache` — consumed correctly; the null-guard early return is clean.
- `TypeAggregates` — only `Count` is used. `TypeAggregateIndexEntry.TotalSize` is ignored,
  which misses a high-value ranking dimension.
- `GlobalSizeBuckets` — not consumed. For a shape analyzer this is acceptable, but
  cross-referencing bucket distribution with reference-heavy type population would strengthen
  findings.
- `HeapAnalysisCache.TryGetHeapIndex` — the cast `cache is not HeapAnalysisCache heapCache`
  silently produces an empty result for alternative cache implementations. This is consistent
  with the rest of the codebase but should be documented.

### Missing Infrastructure Opportunities

| Opportunity | Notes |
|---|---|
| `TypeAggregateIndexEntry.TotalSize` | Available in the index; add to `TypeShapeProfile` and ranking. |
| Generation-bucketed stats | If a generation-aware type aggregate index is ever built, this analyzer should cross-reference it for Gen2 retention signals. |
| `ILogger` injection | Silent `catch { ifaceCount = 0; }` should use the logger pattern; `ActivatorUtilities` resolution is already established in `DefaultAnalyzerFactory`. |

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Opportunities

| Opportunity | Impact | Notes |
|---|---|---|
| **GC scan cost score per type** (`RefFields × InstanceCount`) | Very High | Directly quantifies GC traversal work attributable to each type. Sort and rank by this value rather than instance count alone. |
| **Aggregate GC scan work** (`Σ RefFields × InstanceCount`) | Very High | Single heap-wide number characterising total GC scan budget. Enables trend analysis across dump versions. |
| **Balanced type list** (refRatio 0.2–0.6) | High | The numerically dominant category — currently invisible. Top 20 by instance count should be reported. |
| **Array shape table** | High | Reference-type arrays are scanned element-by-element by GC; a `string[]` with 5M elements costs 5M pointer reads per GC. Currently invisible. |
| **Finalizable × reference-heavy intersection** | High | Double GC cost: scan overhead *and* finalization queue pressure. Trivially derivable from existing `TypeShapeProfile` fields. |
| **Per-type byte size from `TotalSize` aggregate** | High | Average bytes per instance = `TotalSize / Count`; immediately improves LOH and large-allocation diagnosis. |
| **Deep inheritance + reference-heavy correlation** | Medium | `BaseTypeChainDepth` is captured but never used. Types with deep chains and many ref fields are candidates for refactoring. |
| **Struct padding estimate for value-heavy types** | Medium | `ValueHeavy` types with large `TotalFields` counts may carry alignment padding; a rough estimate via `StaticSize` (from `ClrType`) would complement the BoxingAnalyzer. |
| **Trend acceleration for GC scan cost** | Medium | If scan cost score grows faster than instance count, new reference fields were added — a structural regression. |
| **Interface-density signal** | Low | High `InterfaceCount` with reference-heavy types indicates virtual-dispatch complexity; useful in architecture reviews. |

---

## Audit Area 5 — Performance, Memory & Scalability

### Assessment

The analyzer performs no heap object enumeration. Its cost is bounded by `InstanceCountCap`
(default 200) dictionary lookups in `TypeAggregates` and `TypeShapeCache`, plus at most 200
`heap.GetTypeByMethodTable` calls.

On a 25 GB dump with 50,000 types and 500 million objects:
- The shapes and aggregates dictionaries are populated during Phase 1 (already paid).
- Phase 2 cost: 200 dictionary lookups + 200 ClrMD metadata resolutions. Negligible.

### Memory Usage

- `candidates` list: at most `shapes.Count` tuples. With 50,000 types × ~40 bytes ≈ 2 MB.
  Fine for `InstanceCountCap = 200` (only 200 entries in output lists).
- The `candidates.Sort(...)` operates in-place on the list; for 50,000 entries this is
  O(n log n) ≈ ~800,000 comparisons. Acceptable but avoidable for large type counts.

### Scalability Issue: Full Candidate List Sort

```csharp
candidates.Sort(static (a, b) => b.Count.CompareTo(a.Count));
int cap = Math.Min(candidates.Count, options.InstanceCountCap);
```

The code sorts the entire `candidates` list (all types in `TypeShapeCache`) before capping at
`InstanceCountCap`. For a heap with 100,000 types, this is an O(n log n) sort to select the
top 200. A min-heap / partial-sort (O(n log k)) with `k = InstanceCountCap` would be
asymptotically better for large type counts.

For current workloads (≤50 K types, 2 MB list) this is not a practical bottleneck, but it
is worth noting as the obvious optimization target if type counts grow.

### Cancellation

`cancellationToken.ThrowIfCancellationRequested()` is called once at entry. Because the
Phase-2 loop is O(InstanceCountCap) iterations — at most 200 — no mid-loop cancellation is
necessary. Correct.

### Progress Reporting

Not applicable: the analyzer completes in milliseconds.

---

## Audit Area 6 — Correctness & Confidence

### Assumptions & Risks

| Risk | Severity | Notes |
|---|---|---|
| **Ranking does not match stated criterion** | High | The docstring and code comment say ranking is by `refRatio × instanceCount`. In practice, `refHeavy` and `valHeavy` are populated in instance-count order (the pre-sort) with no secondary sort by GC scan cost score. The lists are therefore ordered by instance count, not by the declared composite metric. | 
| **`AvgRefFieldsPerType` is a capped sample** | Medium | Computed over at most `InstanceCountCap` types. Marketed as a heap-wide average but is a top-N sample average. On heaps with diverse type distributions the two values diverge significantly. |
| **Silent `catch` on `EnumerateInterfaces()`** | Medium | An exception from ClrMD is silently zeroed. If the exception indicates corrupted heap metadata, downstream findings relying on `InterfaceCount` are silently wrong. |
| **`count` cast to `ulong` with `Math.Max(0, count)`** | Low | `TypeAggregateIndexEntry.Count` is a `long`; clamping negatives is defensive but should not occur in a valid index. If it does occur, it signals an index build bug that should be surfaced rather than silenced. |
| **Balanced and Scalar categories dropped silently** | High | Types in the two majority categories produce no output and no diagnostic. An engineer analysing a balanced-dominant heap receives a report that says "0 reference-heavy types, 0 value-heavy types" with no explanation that 5,000 Balanced types were seen and omitted. |
| **Cap at 200 (default) is arbitrary** | Low | In `Full` profile the cap rises to 1,000. No guidance is given for when to change the cap, and the effect on report accuracy is not described in options documentation. |

### False Positive Risk

Low. The field count metadata from `TypeShapeEntry` is built directly from ClrMD
`ClrInstanceField` enumeration during Phase 1. Category thresholds (>0.6 / <0.2) are
deterministic. Misclassification can only occur from corrupt ClrMD metadata, not from
the analyzer logic.

### False Negative Risk

High due to the Balanced/Scalar drop. The dominant category of types in most production
.NET heaps is Balanced. Dropping this category means the analyzer will consistently
miss the most populous types in the heap.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!dumpheap -stat` provides a flat count/size table for all types but no field-layout
classification or GC scan cost ranking. `!dumptype` gives field detail for a single type.
ObjectShapeAnalyzer's pre-ranked multi-type layout summary is more actionable for
triage — a clear advantage.

**Gap**: SOS's `!gcroot` can attribute retention paths; ObjectShapeAnalyzer provides no
retention evidence at all, only structural metadata.

### PerfView

PerfView's GC heap snapshot classifies types by total retained size. It has no equivalent
to the reference-field ratio or GC scan cost score. DumpDetective's shape classification
is complementary and differentiated.

**Gap**: PerfView's heap snapshot also shows object graph paths and retention trees.
ObjectShapeAnalyzer has no object-graph integration.

### Visual Studio Memory Usage

VS Memory Usage provides "top types by size" and individual object inspection. No
field-ratio or GC cost ranking. ObjectShapeAnalyzer's structural classification is
richer.

**Gap**: VS Memory Usage shows allocation call stacks, helping attribute boxing/allocation
sites to specific code paths. ObjectShapeAnalyzer has no attribution capability.

### JetBrains dotMemory

dotMemory's "Inspections" surface boxing leaks, large arrays, and finalizable objects
automatically. Its "Group by type" view includes field counts for struct types. Most
significantly, dotMemory's "Dominators" and "Retention" paths show exactly why large
reference-heavy types are retained.

**Competitive Gap**: dotMemory's biggest advantage here is actionable path-to-root for
every flagged type. ObjectShapeAnalyzer identifies *what* is expensive but not *why* it
is retained.

### Competitive Opportunities

1. **GC scan cost score** (`RefFields × InstanceCount`) — no tool surfaces this directly.
   A first-mover advantage for DumpDetective.
2. **Cross-analyzer correlation** — linking ObjectShapeAnalyzer findings to LeakCandidateAnalyzer
   retention paths is a workflow no off-the-shelf tool provides end-to-end.
3. **Trend analysis over dump series** — the TrendComparer infrastructure is in place; adding
   scan-cost-score trend would differentiate DumpDetective significantly.

---

## Recommendation Classification

### Improvements

| ID | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| I-1 | **Fix ranking**: sort `refHeavy` and `valHeavy` by `refRatio × instanceCount` descending, not by insertion order from the instance-count pre-sort. | High | Low | High | Improvement | ✅ DONE |
| I-2 | **Add GC scan cost score column** (`RefFields × InstanceCount`) to the section builder tables and as a sort key. | High | Low | High | Improvement | — |
| I-3 | **Add aggregate GC scan work metric** (`Σ RefFields × InstanceCount`) as a key metric and trend-comparer entry. | High | Low | High | Improvement | — |
| I-4 | **Add Balanced type list** (top 20 by instance count, refRatio 0.2–0.6) to the domain result and section builder. | High | Medium | High | Improvement | — |
| I-5 | **Include `TotalSize` from `TypeAggregateIndexEntry`** in `TypeShapeProfile` and ranking. | High | Low | High | Improvement | — |
| I-6 | **Add Array shape table**: top reference-type arrays ranked by `InstanceCount`. | High | Medium | High | Improvement | — |
| I-7 | **Add Finalizable × ReferenceHeavy finding**: fire at Warning severity when a type is both finalizable and reference-heavy with ≥10K instances. | High | Low | High | Improvement | — |
| I-8 | **Fix `AvgRefFieldsPerType` label**: disclose in the metric description that it is computed over at most `InstanceCountCap` types, not all types in the heap. | Medium | Low | High | Improvement | — |
| I-9 | **Replace silent `catch` on `EnumerateInterfaces()`** with `ILogger`-based diagnostics, consistent with the pattern in `DefaultAnalyzerFactory`. | Medium | Low | High | Improvement | — |
| I-10 | **Upgrade finding severity**: add `Critical` tier when `Σ(RefFields × InstanceCount)` exceeds a configurable threshold, indicating material GC scan pressure. | Medium | Low | High | Improvement | — |
| I-11 | **Fix misleading recommendation** in value-heavy finding: replace "BoxingAnalyzer for struct-layout optimization" with accurate guidance on field ordering and `[StructLayout(LayoutKind.Sequential)]`. | Low | Low | High | Improvement | — |
| I-12 | **Add `IAnalyzer.Tags` override**: `["gc", "object-shape", "memory", "gc-scan"]`. | Low | Low | High | Improvement | — |
| I-13 | **Replace full sort with partial sort** for `candidates` when `shapes.Count` is large: use a min-heap approach to select top-`InstanceCountCap` entries in O(n log k). | Low | Medium | Medium | Improvement | — |

### Evolutions

| ID | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| E-1 | **Cross-analyzer retention correlation**: surface ObjectShapeAnalyzer's top reference-heavy types as input candidates to LeakCandidateAnalyzer or ReferenceChainAnalyzer for automatic root-path attribution. | Very High | High | Medium | Evolution |
| E-2 | **Generation-aware shape ranking**: extend `TypeAggregateIndexEntry` with per-generation instance counts so ObjectShapeAnalyzer can rank by `RefFields × Gen2Count` — the retention-adjusted GC scan cost. | High | High | Medium | Evolution |
| E-3 | **Struct padding estimation**: for value-heavy types, compute `ClrType.StaticSize - Σ field.Size` as a padding estimate and surface it alongside `BoxingAnalyzer`'s struct analysis. | Medium | Medium | Medium | Evolution |

---

## Final Executive Summary

### Overall Assessment

**Score: 58 / 100**

**Production readiness**: Conditional. Correct as far as it goes; safe to run; produces no
false positives. However, the ranking does not implement its stated criterion, the two
most common categories of types are silently dropped, and the GC cost signal is
incomplete. The report is useful for quick triage but insufficient for confident diagnosis.

**Major Strengths**:
- Zero heap enumeration: pure Phase-2 metadata join — negligibly fast on any dump size.
- `TypeShapeEntry` and `TypeAggregates` join architecture is correct and well-designed.
- Twelve-column profile captures all key structural metadata.
- TrendComparer and FindingGenerator are present and coherent.
- Disk-vs-memory discrepancy test validates index symmetry.

**Major Weaknesses**:
- Stated ranking criterion (`refRatio × instanceCount`) is not implemented.
- Balanced and Scalar categories are computed and then silently discarded.
- Total retained size is not used in ranking or output.
- No aggregate GC scan cost metric.
- Finding severity is capped at `Warning`; production-scale pressure scenarios would warrant `Critical`.

### Priority Roadmap

| Priority | ID | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|---|
| P0 | I-1 | Fix ranking: sort by `refRatio × instanceCount` | High | Low | High | Improvement | ✅ DONE |
| P0 | I-2 | Add GC scan cost score column | High | Low | High | Improvement | — |
| P0 | I-4 | Add Balanced type list | High | Medium | High | Improvement | — |
| P1 | I-3 | Aggregate GC scan work key metric | High | Low | High | Improvement | — |
| P1 | I-5 | Include `TotalSize` in profile and ranking | High | Low | High | Improvement | — |
| P1 | I-6 | Array shape table | High | Medium | High | Improvement | — |
| P1 | I-7 | Finalizable × ReferenceHeavy finding | High | Low | High | Improvement | — |
| P1 | E-1 | Cross-analyzer retention correlation | Very High | High | Medium | Evolution | — |
| P2 | I-8 | Disclose cap scope in `AvgRefFieldsPerType` label | Medium | Low | High | Improvement | — |
| P2 | I-9 | Replace silent catch with ILogger | Medium | Low | High | Improvement | — |
| P2 | I-10 | Add Critical severity tier for GC scan pressure | Medium | Low | High | Improvement | — |
| P2 | E-2 | Generation-aware shape ranking | High | High | Medium | Evolution | — |
| P3 | I-11 | Fix misleading BoxingAnalyzer recommendation | Low | Low | High | Improvement | — |
| P3 | I-12 | Add `IAnalyzer.Tags` override | Low | Low | High | Improvement | — |
| P3 | I-13 | Partial sort for large type counts | Low | Medium | Medium | Improvement | — |
| P3 | E-3 | Struct padding estimation for value-heavy types | Medium | Medium | Medium | Evolution | — |

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. It is safe, non-destructive, and
   fast. However, the ranking defect (I-1) and the silent Balanced-category drop (I-4)
   mean the report misrepresents heap character in the majority of production workloads.
   Fix I-1 and I-4 before treating output as definitive.

2. **Highest-impact improvements**: I-1 (fix ranking), I-2 (GC scan cost score), I-3
   (aggregate GC scan work metric), I-4 (Balanced type list), I-5 (include TotalSize).
   All five are low-difficulty with high confidence and together transform the report
   from a structural curiosity into a decision-grade GC cost surface.

3. **Platform evolution opportunities**: E-1 (cross-analyzer retention correlation) is the
   highest-value evolution — linking shape analysis to root-path evidence closes the
   "what is retained and why" gap that every competing tool exposes as DumpDetective's
   blind spot. E-2 (generation-aware shape ranking) is the second evolution that would
   materially separate DumpDetective from WinDbg/SOS.

4. **Highest engineering return**: I-1 + I-2 + I-3 together require one to two hours of
   implementation, cost nothing in performance, and deliver the core GC scan cost signal
   that is currently computed but never materialized. The return-to-effort ratio is
   exceptional.
