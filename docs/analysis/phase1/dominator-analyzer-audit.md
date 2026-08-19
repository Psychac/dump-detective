# DominatorAnalyzer Audit Report

> **Scope**: `DominatorAnalyzer.cs`, `DominatorDomainResult.cs`, `RetentionOptions.cs`,
> `DominatorSectionBuilder.cs`, `DominatorFindingGenerator.cs`, `DominatorTrendComparer.cs`,
> `DominatorAnalyzerHeapIndexScanTests.cs`, `DominatorFindingGeneratorTests.cs`,
> `DominatorAnalyzerDiscrepancyTests.cs`
>
> **Protocol**: [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)
>
> **Date**: 2026-07-29
>
> **P0 Completion**: ✅ All 3 P0 items implemented (2026-08-09)
> **P1 Completion**: ✅ All 5 P1 items implemented (P1-1 BFS exclusivity, P1-2 gen2Count surface, P1-3 Take option, P1-4 remove HeuristicOnly, P1-5 root path limits)
> **P3 "Investigate Lengauer-Tarjan dominator tree" — ✅ RETIRED (2026-08-16)**: implemented, validated
> end-to-end against real 3GB/25GB dumps, and wired into the report (Gen2/LOH sub-table's "Retained"
> column now uses exact bytes when the exact path succeeds). Scope ended up broader than this item's
> original "Gen2+LOH subgraph" framing — the exact tree covers the whole reachable heap, with
> generation used only as a report filter (see the design doc's D1). Gated behind
> `RetentionOptions.EnableExactDominatorTree` (default on), capped by a memory budget, and falls back
> to this analyzer's existing BFS heuristic on cap-exceeded or failure. See
> [dominator-tree-lengauer-tarjan.md](../phase1-redesigns/dominator-tree-lengauer-tarjan.md) (design)
> and [dominator-tree-implementation-plan.md](../phase1-redesigns/dominator-tree-implementation-plan.md)
> (phase-by-phase build log with real measurements). Persisting the computed tree across runs (D7)
> remains deliberately deferred — see that design doc's Open Question 5.

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`DominatorAnalyzer` is the retained-memory estimation and reference-hotspot analyzer. It is the
product of merging the former `RetentionAnalyzer` into itself per Phase 0 Deliverable 6. Its
current responsibilities are:

1. **Incoming-reference counting pass** — scans every heap object's outgoing fields to build a
   `Dictionary<ulong, int>` of incoming-reference counts per target address. Identifies "highly
   referenced" objects (fan-in > `HighReferenceThreshold`) as retention hotspots.
2. **Type scoring and ranking** — builds a candidate list from `HeapAnalysisCache` type statistics,
   scores each type by a heuristic formula (`totalSize + lohSize + gen2Count × avgSize + bonus`),
   ranks candidates, and selects a top-K set.
3. **BFS retained-size estimation** — walks each top-K type via `BoundedGraphWalk.ComputeExclusiveRetained`
   to estimate how many bytes that type's sample instance transitively holds.
4. **Root-path evidence population** — feeds highly-referenced objects through `RootPathFinder` to
   produce a single root chain per object.

**2026-08-17 update**: the paragraph below described the analyzer as of the original 2026-07-29
audit date. As of the P3 item retired 2026-08-16, this is no longer accurate — a real
Lengauer-Tarjan dominator tree (`LengauerTarjan.cs`, `DominatorTreeComputer.cs`) is now wired in and
produces exact retained bytes for the Gen2/LOH sub-table when the exact path succeeds (see the audit
header). Left here for history; see [Audit Area 8](#audit-area-8—report-clarity-vs-comparable-tools-added-2026-08-17)
below for what's still missing now that the exact-bytes number exists but isn't presented as clearly
as it could be.

~~The name "Dominator Analysis" is aspirational: no Lengauer-Tarjan dominator tree is computed. The
`HeuristicOnly` flag is hard-coded to `true` at every result-creation site, and the section builder
acknowledges this in its confidence caveat.~~

### Coverage Assessment

| Responsibility | Status | Notes |
|---|---|---|
| Retention-size estimation for top types | ✓ Present | Bounded BFS per sample instance |
| Incoming-reference hotspot detection | ✓ Present | Full heap scan, dictionary-bounded |
| Root-path evidence for hotspot objects | ✓ Present | Via `RootPathFinder` |
| True dominator tree | ✗ Missing | Only BFS heuristic; `HeuristicOnly` always true |
| Per-instance retained bytes | ✗ Missing | Only per-sample instance walk, not aggregated |
| GC generation context in results | ✗ Missing | `gen2Count` computed but not surfaced in output |
| LOH dominator section | ✗ Missing | `lohSize` fed into score but no LOH-specific output |
| Finalization dominator coupling | ✗ Missing | No interaction with `FinalizableObjectAnalyzer` |

### Missing Functionality

- The `HeuristicOnly` flag on `DominatorDomainResult` implies a non-heuristic path could exist.
  No such path is implemented; the flag can never be `false`. It is misleading.
- The `gen2Count` field is fetched from `TypeAggregateIndexEntry` and used in scoring but is never
  included in `TypeSnapshot` or exported to the report. An engineer reading the report has no
  visibility into why a type scored highly relative to another.
- No "dominator chain" detection: the analyzer cannot identify that type A retains type B which
  retains type C and that the full chain is responsible for N MB.

### Expansion Opportunities

- Expose `gen2Count` and `lohSize` per type in the report table.
- Replace `HeuristicOnly = true` with a configurable BFS-depth/breadth threshold that, when
  generous enough, earns a higher-confidence label ("deep estimate" vs "shallow estimate").
- Add a `ByGeneration` breakdown to the result so the section builder can highlight Gen2/LOH
  dominators separately from Gen0/1 noise.
- Couple with `FinalizableObjectAnalyzer` to flag types whose finalizer suppression is responsible
  for long-lived retained objects.

### Architectural Observations

- The merger of `RetentionAnalyzer` into `DominatorAnalyzer` is architecturally sound: both
  operated on the same heap-scan pass and shared domain concepts. The merged analyzer is larger but
  cohesive.
- `DominatorAnalyzer.Order = 110` places it second in the pipeline. The reference-counting pass
  then runs after `MemoryAnalyzer` but before GC, thread, and leak analyzers. The order is
  appropriate since later analyzers can consume its findings.
- There is still an ownership gap between `DominatorAnalyzer` (retained-size heuristic),
  `LeakCandidateAnalyzer` (leak scoring), and `ReferenceChainAnalyzer` (root paths). An engineer
  diagnosing "why is this object alive?" must consult three separate sections with no cross-reference.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- The section builder produces a confidence band that honestly reports caveats about scan caps,
  skipped addresses, and the heuristic-only nature of results.
- Four tables are produced: dominator type suspects, per-mille impact, highly-referenced objects,
  and top retention types by incoming-reference aggregation. The layered view (per-type → per-object
  → per-type-aggregate) is genuinely useful.
- `FindingGenerator` maps severity thresholds to concrete byte values (100 MB = Warning,
  500 MB = Critical), making triage decisions transparent.
- The trend comparer tracks six metrics including per-type byte deltas, enabling regression
  detection across dumps.

### Weaknesses

1. **`HeuristicOnly` confidence deduction is always applied** — the section builder deducts 0.10
   confidence for `d.HeuristicOnly`, but this is always `true`. Every report has a permanent
   unexplained 10-point confidence penalty that can never be resolved by the user.

2. **Section builder hardcodes `.Take(20)`** on all four tables regardless of
   `TopHighlyReferencedObjectsToShow` (Balanced: 15, Fast: 8, Full: 40). The configured value is
   ignored at the presentation layer.

3. **`gen2Count` not shown** — scored into candidates but absent from every output surface
   (result record, section builder, trend comparer, finding generator).

4. **Mismatched option name in finding text** — `DominatorFindingGenerator` references
   `MaxReferenceAddressesToTrack` in recommendation text; the actual `RetentionOptions` property is
   `MaxReferenceAddresses`. Engineers following this advice cannot find the setting.

5. **Shallow bytes for top highly-referenced objects** — the "top_retained_total" key metric sums
   shallow `Size` values from `TopHighlyReferencedObjects`, not retained bytes. The metric label
   says "bytes" but the semantic is shallow footprint, not retained footprint. This can be
   misleading since highly-referenced objects are typically small hubs (e.g., a shared
   `CancellationTokenSource`) whose shallow size tells little about their impact.

6. **`FindingGenerator` severity boundary for `HighlyReferencedObjectCount`** — the threshold is
   `>= 10` for Critical. On a large heap with millions of objects the count of hub objects > 50
   references will routinely exceed 10, making every large-heap report Critical regardless of
   whether the hubs are actually problematic.

7. **No root-path evidence surfaced in the main dominator-type table** — `TopDominatorTypes`
   contains `SampleAddress` but the section builder only renders it as a hex address. No root
   chain, owner type, or GC root kind is shown. Root paths exist for highly-referenced objects
   only.

8. **Trend comparer missing per-type retained bytes** — `DominatorTrendComparer.ExtractMetrics`
   exports `dominator.type.bytes` (shallow total) per type but not `dominator.type.retained.bytes`
   (estimated retained). Regressions in retained size are not trackable across dumps on a per-type
   basis.

### Missing Diagnostics

- Gen2 count and LOH fraction per dominator type.
- Average incoming-reference count for highly-referenced objects vs. threshold (so the engineer
  can judge how far above the threshold the objects sit).
- Indication of whether the BFS walk was capped for a specific type (no per-type `wasCapped`
  surface in `TypeSnapshot`).

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD API Usage

- `heap.GetObject(address)` with `IsValid` guard is correct and consistent.
- `obj.AsArray()` / `arr.GetObjectValue(i)` for 1-D reference arrays is correct.
- Indexed `for` loops over `type.Fields` (instead of `foreach` over the interface) correctly
  avoids `SZGenericArrayEnumerator` boxing, as documented in the FIX-2 comment.
- `heap.GetTypeByMethodTable(mt)` in the local `MethodTableHasOutgoingRefs` fallback is correct
  but see the dead-code observation below.
- `BoundedGraphWalk.ComputeExclusiveRetained` internally calls
  `obj.EnumerateReferences(carefully: true)` — the idiomatic ClrMD forward-reference path —
  while `CountIncomingReferencesByAddress` manually iterates `type.Fields`. This divergence is
  intentional: the hot-path reference counting must avoid the per-call enumerator allocations that
  `EnumerateReferences` creates when called hundreds of millions of times.

### Infrastructure Utilization

| Infrastructure | Used | Notes |
|---|---|---|
| `IHeapAnalysisCache.MethodTableHasOutgoingRefs` | ✓ | Hot-path MT skip in `OnHeapEntry` |
| `HeapAnalysisCache.TryGetHeapIndex` | ✓ | Reads `TypeAggregates` for gen2/size |
| `HeapAnalysisCache.GetOrBuildTypeStatistics` | ✓ | Candidate population |
| `HeapAnalysisCache.GetSampleInstanceAddress` | ✓ | Per-type sample for BFS walk |
| `HeapAnalysisCache.GetOrBuildValidRoots` | ✓ | Root-path evidence |
| `HeapAnalysisCache.EnumerateIndexedEntries` | ✓ (fallback only) | Used in `AnalyzeObjectsPass` |
| `IParallelHeapIndexScanParticipant` | ✓ | Parallel index scan |
| `BoundedGraphWalk.ComputeExclusiveRetained` | ✓ | Two call sites: PopulateRetainedBytes, top-K loop |
| `RootPathFinder` | ✓ | Evidence population |
| `ObjectScanCounter` | ✓ | Progress reporting in both scan paths |

### Issues Found

1. **Dead code: local `methodTableHasRefs` dictionary in `AnalyzeObjectsPass`** — the branch
   `if (cache is not null)` dispatches to `cache.MethodTableHasOutgoingRefs`; the
   `else` branch builds its own `Dictionary<ulong, bool>` and calls `MethodTableHasOutgoingRefs`
   (private static). Since production always passes a non-null cache, this `else` branch and the
   private `MethodTableHasOutgoingRefs`/`TypeHasOutgoingRefs` static methods are dead code.
   They increase maintenance surface without benefit.

2. **`AnalyzeObjectsPass` also calls `heapCache.EnumerateIndexedEntries()`** — this path is still
   present as the inner disk-index route inside `EnumerateLeakEntries`, but the caller
   (`AnalyzeObjectsPass`) is itself only invoked when `_participantScanSucceeded == false`, i.e.,
   when there was no shared scan. If the disk index exists, the dispatcher runs `OnHeapEntry`
   directly via the participant path, so this fallback inside `EnumerateLeakEntries` is
   redundant with the participant mechanism.

3. **`RootPathSearchLimits` in `PopulateEvidence` are hardcoded** — the `MaxCandidateNodes: 5_000`,
   `MaxCandidateDepth: 8`, `MaxRootExpansionDepth: 12`, `LargeFanoutThreshold: 100` values are
   inline constants not sourced from `RetentionOptions` or `ExecutionPolicy`. Users cannot tune
   evidence quality separately from the main scan limits.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Missing Diagnostics

1. **Gen2/LOH dominator table** — types with the largest Gen2 footprint or LOH allocation that are
   top scorers could be shown in a dedicated sub-table. Currently `gen2Count` is scored but invisible.

2. **Dominator chain detection** — track paths of the form `A → B → C` where the chain collectively
   retains N MB. This is the difference between "B is highly referenced" and "B is highly referenced
   because A holds 1,000 references to it from a static list." The section builder could render
   a short chain summary alongside each highly-referenced object.

3. **Retained-size confidence tier per type** — tag each `TypeSnapshot` with whether its BFS
   walk was capped (`wasCapped` from `BoundedGraphWalk.CollectForwardTypeNames` — though
   `ComputeExclusiveRetained` doesn't expose a capped flag). This would let engineers know
   which retained-size estimates are lower bounds.

4. **Cross-type retained overlap** — when type A and type B both refer to the same large
   subgraph, the current exclusive-BFS semantics show 0 retained for the second type scanned.
   A "shared subgraph size" metric would help explain why two types show up together as
   high-score candidates but neither has large exclusive retained bytes.

5. **Retention pressure ratio** — `total_retained_est / total_heap_size` expressed as a
   percentage. A 500 MB retained estimate means something very different on a 600 MB dump vs.
   a 20 GB dump.

6. **Fan-in distribution histogram** — a histogram of incoming-reference counts (e.g., 0–10,
   10–50, 50–200, 200+) would let engineers quickly see whether there are one or two extreme
   hubs vs. a broad population of moderately-referenced objects.

7. **Object-to-size fan-in ratio** — for each highly-referenced object, the ratio
   `IncomingReferences / Size` identifies cases where a tiny object (e.g., a sentinel string)
   is disproportionately referenced, vs. large objects with few references.

---

## Audit Area 5 — Performance, Memory & Scalability

### Performance Assessment

The reference-counting pass is the primary bottleneck. Each traced object requires:
- One `heap.GetObject()` call (random read into the dump file)
- Iteration of all reference-typed fields

On a 25 GB dump with 30 M objects and an average of 10 reference fields each, this is up to
300 M ClrMD field reads. The `MaxLeakScanObjects` cap (default 2 M) prevents worst-case
unbounded runtime but caps coverage at ~7% of a 30 M object heap.

| Concern | Assessment |
|---|---|
| Reference-counting pass | Bounded by `MaxLeakScanObjects`; `IParallelHeapIndexScanParticipant` helps on multi-core |
| `_referenceCount` dictionary | Bounded by `MaxReferenceAddresses` (1 M default); acceptable |
| `ExtractHighlyReferencedObjects` | Min-heap fast path for large dictionaries; good |
| Top-K BFS walk | ≤20 types × `maxBreadth` objects each; bounded and fast |
| `PopulateEvidence` root search | ≤15 items; `RootPathFinder` limits applied; acceptable |
| `BuildTopRetentionTypes` | LINQ over ≤15 items; acceptable |
| Debug perf logging | **8 `Console.Error.WriteLine("[PERF]...")` calls remain in production code** — unconditional stderr noise on every analysis run |

### Memory Assessment

- `_referenceCount` dictionary: 1 M entries × (8 + 4 + 8) bytes ≈ 20 MB; acceptable.
- `candidates` list: ≤ `typeStats.Count` entries (typically < 50 K types); ≤ ~5 MB.
- `topHighlyReferencedObjects`: ≤ 40 items (Full profile); negligible.
- `visited` HashSet in `PopulateRetainedBytes`: shared across ≤15 objects, size bounded by
  `maxBreadth × 15`; at most a few MB.
- Per-worker `DominatorAnalyzer` allocations in parallel mode: each worker holds its own
  `_referenceCount` dictionary. With K workers and 1 M cap each, total memory is K × 20 MB
  before `MergePartial` reduces to one. This can be significant at high parallelism. See
  [p1-item-2-parallel-dispatcher-design-sketch.md § Follow-up idea](../phase-0/p1-item-2-parallel-dispatcher-design-sketch.md#follow-up-idea-not-yet-scoped-per-worker-accumulator-memory-for-large-caps)
  for a proposed direction (per-worker cap division or disk-spill) — not yet scoped as work.

### Scalability Bottleneck

The scalability limit is not the algorithm but the scan cap. At 2 M object limit, a 100 GB dump
with 200 M objects is sampled at 1%. The `IParallelHeapIndexScanParticipant` implementation
helps throughput but does not increase coverage. Increasing `MaxLeakScanObjects` for large dumps
requires proportionally more RAM (larger reference-count dictionaries across workers).

### Optimization Opportunities

1. **Remove debug `Console.Error.WriteLine` calls** — unconditional production stderr output on
   every run is a correctness and operational quality issue.
2. **Dead-code removal** — the local `methodTableHasRefs` dictionary and private
   `MethodTableHasOutgoingRefs` / `TypeHasOutgoingRefs` methods (see Area 3) add no value and
   can be deleted.
3. **Expose BFS `wasCapped` from `ComputeExclusiveRetained`** — would allow per-type confidence
   tagging without extra cost.
4. **Skip already-visited method tables early** — the participant `OnHeapEntry` path checks
   `MethodTableHasOutgoingRefs` correctly but still calls `_scanCounter.Tick()` before the early
   `_objectScanCapped` return. Minor reordering would avoid unnecessary counter increments when
   already capped.

---

## Audit Area 6 — Correctness & Confidence

### Correctness Issues

1. **`HeuristicOnly` is always `true`** — hardcoded at both `DominatorDomainResult` default
   (`HeuristicOnly = true`) and explicitly set at both result-creation sites in `Analyze`. There
   is no code path that would ever set it to `false`. The field communicates nothing.

2. **Inconsistent BFS exclusivity semantics** — `PopulateRetainedBytes` uses a **shared**
   `visited` HashSet across all highly-referenced objects, producing exclusive (non-overlapping)
   retained sizes. The top-K dominator type BFS loop uses a **fresh** `new HashSet<ulong>` per
   type, producing non-exclusive (potentially overlapping) retained sizes. An engineer comparing
   "retained bytes" from the highly-referenced-objects table with the dominator-types table will
   see numbers computed on incompatible exclusivity assumptions.

3. **Reference-count dictionary admission ordering affects results** — `AccumulateReference`
   admits new addresses first-come-first-served up to `MaxReferenceAddresses`. On a large heap
   where the cap is reached early, later-processed objects (those at higher addresses in the
   on-disk index) can never enter the dictionary even if they have very high incoming-reference
   counts. This is a systematic bias toward early-index objects, not a random sample.

4. **`GetSampleInstanceAddress` returning `null` silently drops types** — if the sample address
   is missing from the cache, the type is silently excluded from the candidate list. There is no
   diagnostic signal when this happens. On dumps with partial or truncated heaps this could cause
   large types to disappear from the report without explanation.

5. **Score formula mixes two data sources without a guard** — in `Analyze`, the `count`,
   `totalSize`, and `lohSize` values are initially taken from `CachedTypeStatistics` then
   potentially overwritten from `TypeAggregateIndexEntry` if `aggregates` is non-null. The
   `sampleAddress` is always from `GetSampleInstanceAddress` (cache), never from the aggregate.
   If the aggregate's `MethodTable` lookup fails (sample object is not valid or `Type` is null),
   the result falls back silently to the non-aggregate values — mixing data sources within a
   single candidate without any indication in the output.

### Confidence Assessment

| Concern | Risk Level | Notes |
|---|---|---|
| Highly-referenced-object counts on capped scan | Medium | `ObjectScanCapped = true` is surfaced but the true count is unknown |
| BFS retained-size estimate | Medium | Cap at depth 20 / breadth 10K means distant retaining objects are missed |
| Admission ordering bias in ref-count dictionary | Medium | Systematic, not random — large late-index objects silently excluded |
| `GetSampleInstanceAddress` silent drops | Low | Rare on healthy dumps; more likely on partial/crash dumps |
| `HeuristicOnly` flag semantics | Low | Misleading but does not cause incorrect numeric output |

---

## Audit Area 7 — Industry Benchmark

### Comparison with Leading Tools

| Capability | WinDbg + SOS | PerfView | VS Memory Profiler | dotMemory | DumpDetective |
|---|---|---|---|---|---|
| True dominator tree | `!gcroot` + manual | GC Heap Graph | ✓ Full | ✓ Full | ✓ Full (Lengauer-Tarjan, exact — see 2026-08-17 update above) |
| Per-type retained bytes | ✗ Manual | Partial | ✓ | ✓ | ✓ (bounded) |
| Root path to GC root | `!gcroot` | ✗ | ✓ | ✓ | ✓ (for top hotspots) |
| Gen2 / LOH dominator focus | Manual | Partial | ✓ | ✓ | ✗ Not surfaced |
| Fan-in / reference hotspot | ✗ | ✗ | ✗ | Partial | ✓ |
| Automated severity triage | ✗ | ✗ | ✗ | Partial | ✓ |
| Retention trend across dumps | ✗ | ✗ | ✗ | ✗ | ✓ |

### Competitive Observations

- **2026-08-17**: the exact Lengauer-Tarjan gap described below is closed (see the audit header and
  Audit Area 8). The remaining gap vs. dotMemory/VS Memory Profiler is presentation of the
  "Dominated Memory" grouping concept (last bullet below), not the underlying algorithm — and,
  separately, a report-clarity gap vs. a less sophisticated sibling tool's narrative chain rendering
  (Audit Area 8).
- **dotMemory and VS Memory Profiler** implement Lengauer-Tarjan and present accurate per-type
  retained bytes with no BFS cap. The absence of a true dominator tree is the most significant
  gap vs. these tools.
- **PerfView's GC Heap Graph** shows fan-in counts for a selected object. DumpDetective's
  fan-in scan over the full heap is more automated but lacks the interactive drill-down that
  makes PerfView useful.
- **WinDbg `!gcroot`** produces single root chains without retained-size estimates.
  DumpDetective's `RootPathFinder` integration is competitive here.
- **No equivalent to dotMemory "Dominated Memory" grouping** — where all objects exclusively
  dominated by a given type are shown as a collapsed group. This would require a true dominator
  tree but is the most actionable view for production triage.

### High-Value Feature Opportunities

1. Implement Lengauer-Tarjan dominator tree over a sampled or filtered subgraph (e.g., Gen2+LOH
   only) to produce accurate retained-size numbers for the highest-impact objects. This would
   be an Evolution-class platform addition, not purely an analyzer improvement.
2. Add GC generation annotation to every type row in the dominator table.
3. Expose the fan-in distribution histogram as a chart-ready data structure in the result.

---

## Audit Area 8 — Report clarity vs. comparable tools (added 2026-08-17)

Prompted by a direct comparison against a sibling tool's `memory-leak` command
(`MemoryLeakReport.cs`) — a competitor whose author reports its "what's keeping this alive" output
reads more clearly than this analyzer's, even though this tool now computes *more accurate* retained
bytes (exact Lengauer-Tarjan vs. their BFS approximation). The gap is presentation, not algorithm,
and every recommendation below was checked against this codebase's actual source before being
written — none of it requires inventing new data:

1. **The dominator-suspects tables show a bare hex address; no chain, no root kind, no narrative.**
   `DominatorSectionBuilder`'s "Top dominator suspects" and "Gen2/LOH dominator suspects" tables
   render `type.SampleAddress` as `$"0x{type.SampleAddress:X}"` and nothing else. The other tool's
   equivalent (`RenderRootChains`) renders, inline, an actual box-drawing chain from the sample
   instance to its GC root, with the terminal step explicitly labeled `ROOT` and its kind (static
   field / thread stack / handle). This is the single highest-leverage change: an engineer looking
   at "why is `CacheEntry[]` retained?" currently has to leave the Dominator Analysis section, find
   the sample address, and cross-reference it against a *different* analyzer's output (GC Root
   Intelligence) with no automatic link between the two. Recommendation: for each row in the
   Gen2/LOH dominator sub-table (already the most actionable table — this is deliberate scoping, not
   "do it everywhere"), populate a short root-chain summary using the same `RootPathFinder`
   machinery `GCRootIntelligenceSectionBuilder` already uses, and add it either as a new column
   (short form: `RootKind → ... → TypeName`, capped to ~4 hops) or as a `SectionBlock` narrative
   line per top-N row (long form, matching the other tool's per-suspect chain block). Note the scope
   caveat already recorded in [architecture-comparison.md](../../discrepancy/architecture-comparison.md)
   §8: `RootPathFinding.PathTypeNames` today is a forward BFS from target outward (the target's
   *owned* subgraph), not a root→target chain — producing an actual root-to-target chain for the
   dominator table's sample address requires either running `RootPathFinder` against that address
   directly (it's designed for exactly this — see `PopulateEvidence`'s existing use for highly-referenced
   objects) or reusing the reverse-direction search it already performs there. This is a moderate,
   not a small, change — treat as its own P2/P3 item, not folded into a quick columns tweak.

2. **No narrative synthesis of what a retained/shallow ratio means.** The other tool explicitly
   states, in prose, next to its retained-size table: "Retained >> Own → the object holds
   references to large external graphs... Retained ≈ Own → the object is self-contained; its own
   data IS the leak." This section builder already computes the ratio (`RatioValue`,
   rendered as a permille column) but never explains what a high vs. low ratio *means* to the
   reader — it's a number with no interpretation attached, unlike the AsyncStateMachine analyzer's
   own "state value interpretation guidance" pattern (see
   [project_p16-async-state-machine-state-guidance.md] design precedent in this repo's own history).
   Recommendation: add one or two `T(...)` narrative blocks — reusing the existing `BuildConfidenceBand`/`T`
   helper pattern already in this file — interpreting the ratio thresholds already implicit in the
   Gen2/LOH sub-table, at negligible implementation cost (no new data, just prose keyed off numbers
   already computed).

3. **No step-by-step framing connecting this section to the rest of the report.** The other tool's
   report reads as a guided investigation ("Step 2 → Step 3 → Step 4"), explicitly pointing the
   reader at the next command/section to run. This analyzer's section is one of many
   `AnalyzerDetailSection`s assembled in `SortOrder`-based sequence with no equivalent "what to look
   at next" pointer. This is a report-assembly-level pattern, not specific to this analyzer — if
   adopted, it should be a shared `SectionBlock` convention (e.g. a standard "Next steps" block type)
   rather than something `DominatorSectionBuilder` invents alone, so it's proposed here as a
   candidate for the shared reporting layer, not a per-analyzer fix.

4. **Chain deduplication by shape is a good, cheap idea worth adopting regardless of item 1's
   scope.** The other tool groups identical chains by `string.Join("|", chain.Select(s => s.Line))`
   and shows `×N instances — same chain` instead of repeating the same chain N times. If/when item 1
   is implemented, this dedup-by-shape technique should be applied from the start — showing the same
   3-hop chain 40 times for 40 near-identical cache-entry instances would be worse than the current
   bare-hex-address baseline, not better.

None of items 1–4 change the underlying computation (the exact-vs-BFS retained-bytes correctness
work from the original P3 item is done); they're entirely about making an already-more-accurate
number easier to act on, which is precisely where this analyzer currently loses to a less accurate
competitor.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

**Production Readiness**: Conditional. The analyzer produces useful signals for most investigations
but contains production-quality defects (debug logging in every run, consistently misleading
`HeuristicOnly` flag, inconsistent BFS exclusivity semantics) that should be resolved before the
output is treated as authoritative.

**Major Strengths**:
- Effective use of `IParallelHeapIndexScanParticipant` for parallelized index scanning.
- Correctly bounds memory usage at every phase (reference-count dict, BFS walk, top-K set).
- Four-table report layout gives layered visibility from type aggregate down to per-object evidence.
- Confidence band and caveat system in the section builder is honest and informative.
- Disk-vs-memory discrepancy integration test provides strong parity coverage.

**Major Weaknesses**:
- 8 unconditional `Console.Error.WriteLine("[PERF]...")` calls in production code.
- `HeuristicOnly` is always `true` — the flag is meaningless dead state.
- Inconsistent BFS exclusivity semantics between the two retained-size call sites.
- `gen2Count` computed but never surfaced in any output.
- Reference-count admission bias systematically disadvantages late-index high-reference objects.
- Section builder `.Take(20)` hardcode ignores configured `TopHighlyReferencedObjectsToShow`.
- Wrong option name in finding generator recommendation text.

### Priority Roadmap

| Priority | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---|---|---|---|---|---|
| P0 | Remove 8 `Console.Error.WriteLine("[PERF]...")` calls | High — production stderr noise on every run | Trivial | Certain | Improvement | ✅ DONE |
| P0 | Delete dead code: local `methodTableHasRefs` dict, `MethodTableHasOutgoingRefs` / `TypeHasOutgoingRefs` private statics | Medium — maintenance debt | Trivial | Certain | Improvement | ✅ DONE |
| P0 | Fix finding generator recommendation: `MaxReferenceAddressesToTrack` → `MaxReferenceAddresses` | Low — engineer confusion | Trivial | Certain | Improvement | ✅ DONE |
| P1 | Standardize BFS exclusivity semantics: use shared `visited` set in both `PopulateRetainedBytes` and the top-K loop, or document the intentional divergence | High — incorrect comparison between tables | Low | High | Improvement | ✅ DONE |
| P1 | Surface `gen2Count` and `lohSize` in `TypeSnapshot` and the section builder table | High — major scoring factor invisible to engineers | Low | High | Improvement | ✅ DONE |
| P1 | Fix section builder `.Take(20)` hardcode — respect `TopHighlyReferencedObjectsToShow` from options | Medium — configured value silently ignored | Low | High | Improvement | ✅ DONE |
| P1 | Remove or rename `HeuristicOnly` flag — either always omit it or replace with a graduated confidence enum (`ShallowEstimate` / `DeepEstimate`) | Medium — currently misleads with a permanent confidence deduction | Medium | High | Improvement | ✅ DONE |
| P1 | Source `RootPathSearchLimits` in `PopulateEvidence` from `RetentionOptions` or `ExecutionPolicy` | Medium — evidence quality not tunable | Low | High | Improvement | ✅ DONE |
| P2 | Add retention pressure ratio (`total_retained_est / total_heap_size`) as a key metric and trend metric | Medium — context for absolute byte values | Low | High | Improvement | ✅ DONE |
| P2 | Add `wasCapped` per-type to `TypeSnapshot` and the section builder | Medium — indicates estimate reliability | Low | Medium | Improvement | ✅ DONE |
| P2 | Add fan-in distribution histogram to `DominatorDomainResult` | Medium — useful for cluster analysis | Medium | High | Improvement | ✅ DONE |
| P2 | Add Gen2/LOH dominator sub-table to section builder | Medium — immediately actionable for GC investigations | Medium | High | Improvement | ✅ DONE |
| P2 | Export `dominator.type.retained.bytes` per type in `DominatorTrendComparer` | Medium — enables retained-size regression tracking | Low | High | Improvement | ✅ DONE |
| P3 | Address reference-count admission ordering bias (e.g., reservoir sampling, or two-pass min-heap admission) | Medium — systematic bias on large heaps | High | Medium | Improvement |
| P3 | Investigate Lengauer-Tarjan dominator tree over Gen2+LOH subgraph | Very High — true retained bytes, competitive with dotMemory | Very High | High | Evolution | ✅ DONE — see [dominator-tree-lengauer-tarjan.md](../phase1-redesigns/dominator-tree-lengauer-tarjan.md) / [dominator-tree-implementation-plan.md](../phase1-redesigns/dominator-tree-implementation-plan.md) |
| P3 | Dominator chain detection (A → B → C with cumulative retained bytes) | High — root cause identification | High | Medium | Evolution |
| P3 | Cross-type retained-overlap metric ("shared subgraph size") | Medium — explains why exclusive retained bytes are 0 for co-dominating types | High | Medium | Evolution |
| P3 | Cap or disk-spill per-worker `_referenceCount` in parallel mode (see design sketch follow-up) | Medium — bounds K × 20 MB peak RSS at high parallelism | Medium | Medium | Improvement |
| P2 | Render an inline root-chain summary (kind + capped hop list) per row in the Gen2/LOH dominator sub-table, reusing `RootPathFinder`/`RootPathGroup` already used by `GCRootIntelligenceSectionBuilder` | High — closes the report-clarity gap vs. comparable tools (Audit Area 8 item 1); today an engineer must cross-reference a separate section by hand | Medium — `PathTypeNames` is target-outward today, not root-to-target; needs a direct `RootPathFinder` call against the sample address, not just reuse of existing evidence | Medium | Improvement |
| P2 | Add narrative interpretation text for the retained/shallow ratio (e.g. "retained >> shallow → holds an external graph" vs. "retained ≈ shallow → self-contained") next to the Gen2/LOH sub-table | Medium — turns an uninterpreted number into an actionable read, negligible cost (no new data) | Trivial | High | Improvement |
| P3 | Dedup rendered root chains by identical shape with a "×N instances — same chain" marker, once item 1 above exists | Medium — prevents the improved chain rendering from becoming noisier than today's bare-address baseline at scale | Low (once item 1 lands) | High | Improvement |
| P3 | Shared "Next steps" `SectionBlock` convention pointing from this section to `GCRootAnalyzer`/`ReferenceChainAnalyzer` output, matching the other tool's step-numbered investigation flow | Medium — reporting-layer change, not analyzer-specific; benefits every analyzer's section, not just this one | Medium | Medium | Evolution |

### Final Verdict

1. **Production-ready?** — Partially. The core signal (reference hotspots, heuristic retained
   sizes) is useful in production investigations. The debug logging, flag inconsistencies, and
   exclusivity divergence should be treated as defects before the output is cited as authoritative
   in incident post-mortems.

2. **Highest-impact improvements** — Remove debug logging (P0, trivial), fix BFS exclusivity
   divergence (P1, low effort, high correctness impact), surface `gen2Count` in output (P1,
   immediately actionable diagnostic value).

3. **Platform evolution opportunities** — Done: a true Lengauer-Tarjan dominator tree over the
   reachable heap now moves DumpDetective to feature parity with dotMemory and VS Memory Profiler on
   "which type is responsible for this memory?" (see the audit header and Audit Area 8). The
   remaining highest-return investment is presentation, not algorithm: rendering the exact retained
   bytes this analyzer now computes as an inline root-chain narrative (Audit Area 8 item 1) is what
   would move the *report*, not just the number, to parity with — and past — comparable tools.

4. **Highest engineering return** — P0 and P1 items together require one day of work and address
   the most misleading and confusing aspects of the current output. The P2 items
   (gen2/LOH table, fan-in histogram, retention pressure ratio) would significantly improve
   diagnostic value for GC-pressure investigations with minimal implementation cost.

---