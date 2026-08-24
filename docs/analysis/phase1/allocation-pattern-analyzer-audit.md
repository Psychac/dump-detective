# AllocationPatternAnalyzer Audit Report

> **Scope**: `AllocationPatternAnalyzer.cs`, `AllocationPatternDomainResult.cs`,
> `AllocationPatternAnalysisOptions.cs`, `AllocationPatternSectionBuilder.cs`,
> `AllocationPatternFindingGenerator.cs`, `AllocationPatternTrendComparer.cs`,
> `AnalyzerHelpers.cs` (shared gen-byte helpers), `SizeBucketHelper.cs` (shared Phase 1
> histogram), `AllocationPatternAnalyzerTests.cs`
>
> **Protocol**: [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)
>
> **Date**: 2026-08-15 (re-audit)
>
> **History**: This is a full re-audit against the current codebase. The original audit's
> P0–P3 roadmap (18 items) is fully resolved — see [Resolved History](#resolved-history) at the
> bottom. This document supersedes the previous roadmap; only findings that are new or still
> outstanding as of this re-audit are tracked below.
>
> **Superseded note (2026-08-24):** the `AnalysisProfile` tier system (Fast/Balanced/Full) referenced
> below no longer exists — see §9.30 of
> [analysis-profile-removal-plan.md](../../refactor/analysis-profile-removal-plan.md).
> `SelectionMode`/`ScanStrategy`/`SelectionPriority` collapsed to one algorithm (`CompositeScore`
> ranking, classify-every-candidate-first), so every dump now produces the same selection regardless
> of any tier a user might still configure — the "two reports differ because of a tier" concern this
> doc raises below is moot. `TryGetTypeName` is now called once per distinct type across the *entire*
> population (not a `scanLimit`-bounded prefix), matching the same O(distinct-types), one-cached-lookup
> shape already accepted as negligible for Boxing/ObjectShape elsewhere in this codebase — the
> "resolves names before final selection" issue below is superseded, not fixed as originally
> recommended.

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`AllocationPatternAnalyzer` (`Order = 120` in `DefaultAnalyzerFeatureModuleCatalog`, scheduled
right after `GCGenerationAnalyzer` at `Order = 110`) is a Phase-2 post-processor over the
Phase 1 `TypeAggregateIndexEntry` dictionary (`HeapIndexBuildResult.TypeAggregates`). It:

1. **Heap-wide classification** — aggregates Gen0/Gen1/Gen2/LOH object-count and byte-size
   percentages across all types, classifies the heap into an `AllocationProfile`
   (Transient/Steady/Retained/Mixed) and a `GCPressureLevel` (Low/Moderate/High/Critical).
2. **Per-type classification and ranking** — scores and ranks individual types into three
   configurable buckets (`TopTransientTypes`, `TopShortishTypes`, `TopLongLivedTypes`) using a
   pluggable `SelectionMode`/`ScanStrategy`/`SelectionPriority` combination, plus a fourth
   cross-cutting bucket (`TopHighGen1SurvivorTypes`) for types with high Gen0→Gen1 survival.
3. **LOH size-band distribution** *(new since last audit, P3-3)* — surfaces object counts and
   approximate byte totals for the 85 KB–1 MB, 1 MB–10 MB, and ≥10 MB bands, sourced from the
   shared Phase 1 `GlobalSizeBuckets` histogram (`SizeBucketHelper`, now 9 buckets).
4. **Finalizable-type detection** *(new since last audit, P3-4)* — surfaces `IsFinalizable` per
   type (from `TypeAggregateFlags.IsFinalizableType`, computed during Phase 1) and heap-wide
   `FinalizableTypeCount`/`FinalizableBytes` aggregates.

The doc comment ("Pure Phase-2 post-processor: no heap scan, no ClrMD enumeration") is now only
**partially accurate** — see Area 3 for two live ClrMD call sites that exist in the current
implementation.

### Coverage Assessment

| Responsibility | Status | Notes |
|---|---|---|
| Heap-wide gen/LOH percentage breakdown (count + size) | ✓ Present | Complete since P1-2 |
| Absolute byte totals per generation | ✓ Present | Since P1-2 |
| GC pressure classification with documented thresholds | ✓ Present | Formula fixed (P1-4), thresholds documented (P2-6) |
| Configurable, pluggable type-selection algorithm | ✓ Present | Mode/Strategy/Priority combinatorial, preset-driven |
| Gen1 survivor detection | ✓ Present | Since P2-1 |
| LOH size-band histogram | ✓ Present | New — reuses Phase 1 `GlobalSizeBuckets` |
| Finalizable-type surfacing | ✓ Present | New — reuses Phase 1 `TypeAggregateFlags` |
| Cancellation / progress reporting | ✓ Present | Since P2-2/P2-3 |
| True heap-scan-free operation | ✗ Partial | `ComputeExactGenBytes` walks `heap.Segments`; `TryGetTypeName` calls `heap.GetTypeByMethodTable` per scanned candidate |
| Per-type retained/dominator linkage | ✗ Missing | By design — owned by `DominatorAnalyzer` |
| Allocation-site attribution | ✗ Out of scope | Correctly disclaimed (ETW-only capability) |

### Missing Functionality

- `AllocationPatternAnalysisOptions.LohThresholdBytes` (default `85_000`) is declared, documented,
  and even threaded through the CLI options builder (`AnalyzerOptionsBuilder.cs`), but is **never
  read** by `AllocationPatternAnalyzer`. The actual LOH cut used to populate `e.LohCount`/
  `e.LohSize` is a separate, unrelated hard-coded constant in `TypeIndexBuilder.cs`
  (`LohThresholdBytes = 85_000`), baked in at Phase 1 index-build time. Changing the option has
  zero effect on this analyzer's output — this is the same class of bug P0-2 fixed for
  `TransientClassificationThreshold`, just not yet caught for this option. Because the value is
  baked into the Phase 1 binary format rather than read at Phase 2 time, fixing it properly
  requires either (a) documenting that the option is Phase-1-scoped and removing it from this
  analyzer's options class, or (b) threading a configurable threshold into `TypeIndexBuilder`
  itself (an Evolution-scale change affecting the binary format).
- No cross-analyzer correlation with `DominatorAnalyzer`'s finalizable/retained findings — a type
  flagged `IsFinalizable: true` and `Retained` here has no link to `DominatorAnalyzer`'s retained-
  size estimate for the same type. Same "ownership gap" the `DominatorAnalyzer` audit already
  documents from the other side.

### Expansion Opportunities

- Two independently-tuned "pressure" scores exist side by side: `pressureScore` (drives
  `GCPressureLevel`, formula `((100-gen0%)*0.3)+(gen2%*0.5)+(lohSize%*0.2)`, documented 0–100+
  scale via the P2-6 footnote) and `PromotionPressureScore` (`gen2%+(lohSize%*2.0)`, unbounded
  above 100, exposed as `MetricValue` in the finding and as a trend metric). They measure similar
  concepts with different formulas and different scales, and nothing in the report explains the
  relationship between them. Reconciling into one score (or clearly documenting why two exist)
  would reduce reader confusion.
- The doc comment "Must run immediately after GCGenerationAnalyzer" describes an *ordering*
  convention (both are thematically GC-related, `Order = 110`/`120`), not an actual *data*
  dependency — `HeapIndexBuildResult` comes from `HeapAnalysisCache`'s Phase 1 index cache, which
  is populated independently of any Phase 2 analyzer's execution. The comment should say what it
  actually depends on (Phase 1 index having been built) rather than implying a hard analyzer-order
  coupling that doesn't exist in code.

### Architectural Observations

- The four-bucket type-selection architecture (transient/shortish/longLived/highGen1Survivors)
  with independent `Mode`/`Strategy`/`Priority` axes is more configurable than any comparable
  analyzer in the codebase, at the cost of the `Analyze` method's length (~300 lines with two
  near-duplicate scan loops for `ClassificationFirst` vs `LongLivedFirst`/default priority). This
  duplication was pre-existing before this re-audit and is not newly introduced, but is worth
  flagging as a maintainability cost each time a new field is added to `TypeAllocationProfile`
  (as P3-4's `IsFinalizable` addition required touching both loops).
- `IsThreadSafe` is not overridden and defaults to `false` (`IAnalyzer.IsThreadSafe => false`).
  This is correct, not an oversight: the analyzer touches `context.Heap` via
  `AnalyzerHelpers.ComputeExactGenBytes` and `HeapAnalysisCache.TryGetTypeName`, both of which
  call live ClrMD APIs that are not documented as thread-safe for concurrent multi-analyzer
  execution.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Four type-level tables (transient/medium/long-lived/high-Gen1-survivor) plus the new LOH
  size-band table and the classification summary give a genuinely layered view from heap-wide
  percentages down to per-type detail.
- `IsFinalizable` is now a column on every type table (P3-4), directly answering "is this
  long-lived type contributing to GC pressure because it's finalizable?" without cross-referencing
  another analyzer's output.
- The P2-6 footnote documents the GC Pressure Score scale (0–20/20–45/45–70/>70) and its
  contributing factors directly in the report — a reader no longer has to guess what "42.3" means.
- The finalizable-types footnote and finding evidence
  (`"{count} finalizable type(s) hold {bytes}"`) make the existing Critical-pressure
  recommendation ("investigate finalizable types") concrete instead of generic advice.
- `TotalManagedBytes`/per-gen byte totals (P1-2) mean severity can be judged in absolute terms,
  not just percentages.

### Weaknesses

1. **Two un-reconciled pressure scores** (see Area 1) — `GCPressureLevel`'s underlying
   `pressureScore` and the separately-reported `PromotionPressureScore` can diverge (e.g. a heap
   with `gen2%=10, lohSize%=45` gives `pressureScore=((90)*0.3)+(10*0.5)+(45*0.2)=41` (Moderate)
   but `PromotionPressureScore=10+(45*2)=100` — a reader seeing "100" next to "Moderate" has no
   documented reason to trust either number over the other.
2. **LOH size-band bytes are approximate, not exact, and this isn't disclosed in the table** —
   the section builder's "LOH size-band distribution" table presents `TotalBytes` without any
   caveat that these are derived from each type's *average* object size, not summed per-object
   sizes (the per-object sizes aren't retained past Phase 1 aggregation). A type with highly
   variable object sizes straddling a band boundary will show skewed bucket totals. This mirrors
   the same approximation `MemoryAnalysisProjection` already uses, but that section has no
   documented caveat either — a shared gap, not new to this analyzer, but visible here as of
   P3-3.
3. **`LohThresholdBytes` option is silently ignored** (Area 1) — a user tuning this option via
   the CLI options builder gets no error, no warning, and no effect. This is a silent-no-op
   footgun.
4. **Finding evidence doesn't mention LOH size bands** — `AllocationPatternFindingGenerator`'s
   evidence string still only reports `LohSizePct`; it doesn't surface which band (85KB–1MB vs
   ≥10MB) is driving that percentage, even though the domain result now has that data. A Critical
   finding driven by many 90MB objects reads identically to one driven by many 90KB objects.

### Missing Diagnostics

- No indication in the report of which `SelectionMode`/`Strategy`/`Priority` combination produced
  the current type tables — an engineer comparing two reports from different `AnalysisProfile`
  presets (Fast vs Full) has no way to tell from the report alone that different selection
  algorithms were used, only that the numbers differ.
- No band-level trend delta in `Compare()` — `ExtractMetrics` now emits
  `alloc.loh.band.bytes` per band (keyed by `RangeLabel`), but `AllocationPatternTrendComparer
  .Compare()` doesn't include per-band deltas, so LOH band regressions across snapshots aren't
  directly comparable (matches the pre-existing pattern of not diffing per-type lists either, so
  this is consistent rather than a new gap, but worth noting since the underlying data now
  exists).

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD API Usage

- `AnalyzerHelpers.ComputeExactGenBytes(context.Heap, ...)` enumerates `heap.Segments` (not
  objects) to get exact committed-byte totals per generation from `SegmentKindMapper`. This is a
  **live ClrMD call**, wrapped in try/catch with `ComputeApproxGenBytes` (aggregate-based,
  `[Obsolete]`) as fallback. Cost is bounded by segment count (typically tens to low hundreds,
  even on 100 GB heaps), so this does not violate the "no heap scan" spirit in practice, but it
  does contradict the literal doc-comment claim of "no ClrMD enumeration." No test exercises this
  code path — every unit test passes `Runtime = null!`, so `context.Heap` throws and the
  `ComputeApproxGenBytes` fallback is the only path ever tested (see Area 6).
- `HeapAnalysisCache.TryGetTypeName(heap, mt, ...)` — **both branches of this method call
  `heap.GetTypeByMethodTable(mt)` unconditionally.** The "cache hit" branch's comment
  ("it will be fast, already in CLR metadata cache") relies on ClrMD's own internal cache being
  fast on repeat lookups, not on `HeapAnalysisCache` short-circuiting the call. `AllocationPatternAnalyzer`
  calls this once per **scanned candidate** inside the `scanLimit` loop — not once per emitted
  result. For the `Full` preset (`Strategy = FullScan`, `MaxScanItemsAbsolute = 20_000`), this
  means up to 20,000 live `GetTypeByMethodTable` calls per run, the overwhelming majority of which
  are for candidates that don't survive into the final top-N tables and are discarded. This is the
  same class of problem P1-1 fixed (eliminating unnecessary live ClrMD calls in the FullScan path)
  — it has crept back in via type-name resolution specifically.

### Infrastructure Utilization

| Infrastructure | Used | Notes |
|---|---|---|
| `HeapAnalysisCache.TryGetHeapIndex` | ✓ | Sole data source for classification |
| `HeapAnalysisCache.TryGetTypeName` | ✓ (over-called) | See above — called per scanned candidate, not per emitted result |
| `AnalyzerHelpers.ComputeExactGenBytes` / `ComputeApproxGenBytes` | ✓ | Shared with `GCGenerationAnalyzer`; try/catch fallback |
| `SizeBucketHelper` / `HeapIndexBuildResult.GlobalSizeBuckets` | ✓ *(new)* | Reused from `MemoryAnalyzer`'s existing Phase 1 histogram for P3-3, no new Phase 1 work needed |
| `TypeAggregateFlags.IsFinalizableType` | ✓ *(new)* | Reused from Phase 1, already computed for `FinalizableObjectAnalyzer`/`LeakCandidateAnalyzer` |
| `IParallelHeapIndexScanParticipant` | N/A | Correctly not implemented — this analyzer never scans the heap index itself |

### Issues Found

1. **`TryGetTypeName` resolves names before final selection, not after** — see above. The fix is
   local to `AllocationPatternAnalyzer`: defer the `heapCache.TryGetTypeName` call until after
   `transCandidates`/`shortCandidates`/`longCandidates` are sorted and `.Take(options.TopTypeLimit)`
   is applied, resolving names only for the survivors. This requires restructuring
   `TypeAllocationProfile` construction to happen after selection rather than during the scan (a
   moderate refactor of both scan-loop branches, but does not require any new infrastructure).
2. **`ComputeExactGenBytes` untested** — no unit test constructs a real (or fake) `ClrHeap` to
   exercise the segment-based exact-bytes path; all coverage is on the `ComputeApproxGenBytes`
   fallback via `Runtime = null!`. See Area 6.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Missing Diagnostics

1. **Selection algorithm provenance in the report** — surfacing which `SelectionMode`/`Strategy`/
   `Priority` produced the current tables (e.g. as a key metric) would let engineers correctly
   interpret why two reports from different `AnalysisProfile` presets show different top types for
   the same dump.
2. **LOH band context in finding evidence** — embedding the dominant LOH band (e.g. "LOH
   dominated by ≥10 MB objects") in `AllocationPatternFindingGenerator`'s evidence string, mirroring
   how P2-5 already embeds the top long-lived type name.
3. **Reconciled or single pressure score** — either drop `PromotionPressureScore` in favor of the
   documented `pressureScore`/`GCPressureLevel` pair, or document the distinction and intended use
   of each explicitly in the report (currently only `pressureScore`'s scale is documented via the
   P2-6 footnote).
4. **Exact-bytes vs approximate-bytes indicator** — the domain result doesn't record whether
   `Gen0Bytes`/`Gen1Bytes`/`Gen2Bytes` came from the exact segment-based path or the approximate
   fallback. On a dump where segment enumeration fails (unusual but possible on corrupted/partial
   dumps), byte totals silently degrade to the approximation with no signal in the report.

---

## Audit Area 5 — Performance, Memory & Scalability

### Performance Assessment

| Concern | Assessment |
|---|---|
| Heap-wide aggregation loop (totals, finalizable, bucket-bytes) | O(type count), single pass over `aggregates`; cheap even at 50K+ types |
| Metrics list build + sort | O(type count log type count); partial-sort optimization already applied for `FullScan` (P2-4) |
| `ComputeExactGenBytes` | O(segment count), bounded, cheap |
| `TryGetTypeName` per scanned candidate | **O(scanLimit) live ClrMD calls** — up to 20,000 for `Full` preset; the main remaining cost center (see Area 3) |
| Cancellation checks | Present in both scan loops and the metrics-build loop (P2-2) |
| Progress reporting | Present, throttled to every 100 items (P2-3) |

### Memory Assessment

- No heap materialization; all working sets are bounded by `TopTypeLimit`, `ScanMultiplier`, and
  `MaxScanItemsAbsolute`.
- `bucketBytes` array (new, P3-3): fixed 9-`ulong` array, negligible.
- `metrics` list: one tuple per type in `aggregates` (bounded by distinct type count, not object
  count) — acceptable even at 50K+ distinct types.

### Scalability Bottleneck

The scalability bottleneck is no longer the heap-wide aggregation (bounded by type count, not
object count) — it is the `TryGetTypeName` over-resolution identified in Area 3, which scales
with `scanLimit` (up to `MaxScanItemsAbsolute = 20_000` for `Full`) rather than with the number of
types actually surfaced in the report (`TopTypeLimit`, default 20–50). Fixing the resolve-after-
select ordering would cut ClrMD calls by roughly `scanLimit / (TopTypeLimit * 4)` — e.g. from
20,000 down to roughly 200 for the `Full` preset.

### Optimization Opportunities

1. Defer `TryGetTypeName` resolution to post-selection (Area 3, Area 5).
2. Consider exposing whether `ComputeExactGenBytes` succeeded as a boolean in the domain result
   (cheap, and resolves the Area 4 exact-vs-approximate visibility gap at the same time).

---

## Audit Area 6 — Correctness & Confidence

### Correctness Issues

1. **`LohThresholdBytes` option is a no-op** (Area 1/2) — not a numeric-correctness bug (the
   Phase-1-baked 85,000-byte threshold is itself correct and matches the .NET LOH threshold), but
   a configuration-surface correctness bug: the option promises tunability it doesn't deliver.
2. **Two un-reconciled pressure scores** (Area 1/2) — not incorrect individually, but the lack of
   a documented relationship between `pressureScore` (drives the enum) and `PromotionPressureScore`
   (separately reported number) creates a risk of an engineer trusting the wrong one during
   triage, or assuming they're the same value scaled differently when they aren't.
3. **`ComputeExactGenBytes` path is untested** (Area 3) — the try/catch fallback behavior itself
   is exercised by every existing test (since `Runtime = null!` always throws), but the "happy
   path" — exact segment-based byte computation succeeding — has zero test coverage in this
   analyzer's test file. A regression in `AnalyzerHelpers.ComputeExactGenBytes` or
   `SegmentKindMapper.GetCommittedBytes` would not be caught by any `AllocationPatternAnalyzer`
   test, only (if at all) by `GCGenerationAnalyzer`'s own tests, which share the helper.

### Confidence Assessment

| Concern | Risk Level | Notes |
|---|---|---|
| LOH size-band byte approximation | Low | Same approximation already used elsewhere (`MemoryAnalysisProjection`); undisclosed in the table but not a new risk class |
| `LohThresholdBytes` no-op | Low | Silent but not incorrect — the hard-coded 85,000-byte value it fails to override is itself correct |
| Two pressure scores | Low–Medium | No incorrect output, but genuine risk of reader misinterpretation during incident triage |
| `TryGetTypeName` over-resolution | Low (correctness), Medium (performance) | No incorrect output — every resolved name is correct, just resolved wastefully |
| Untested exact-gen-bytes path | Low–Medium | No known bug, but a real coverage gap for the more "authoritative" of the two byte-computation paths |

---

## Audit Area 7 — Industry Benchmark

### Comparison with Leading Tools

| Capability | WinDbg + SOS | PerfView | VS Memory Profiler | dotMemory | DumpDetective |
|---|---|---|---|---|---|
| Gen0/1/2/LOH distribution by count & size | Partial (`!eeheap -gc`) | Partial | ✓ | ✓ | ✓ |
| Absolute byte totals per generation | ✓ | ✓ | ✓ | ✓ | ✓ (since P1-2) |
| Object-size histogram / size-band distribution | ✗ | Partial | Partial | ✓ | ✓ *(new, P3-3)* |
| Finalizable-type visibility in allocation context | ✗ | ✗ | Partial | ✓ | ✓ *(new, P3-4)* |
| Gen1 survivor rate per type | ✗ | ✗ | ✗ | ✗ | ✓ (since P2-1) — no direct competitor equivalent found |
| Configurable/tunable type-ranking algorithm | ✗ | ✗ | ✗ | ✗ | ✓ — unusually flexible relative to every benchmarked tool |
| GC pressure score with documented scale | ✗ | ✗ | ✗ | ✗ | ✓ (since P2-6) |
| Allocation-site (call-stack) attribution | ✗ | ✓ (ETW) | ✗ | ✓ (profiling mode) | ✗ Correctly disclaimed (dump-only, no ETW) |

### Competitive Observations

- The LOH size-band and finalizable-type additions close two of the gaps the previous audit
  identified relative to dotMemory, at effectively zero new Phase 1 cost (both reused existing
  infrastructure built for other analyzers).
- The Gen1-survivor-rate table and the configurable selection algorithm remain differentiators
  with no direct equivalent found in any of the four benchmarked tools.
- The dump-only allocation-site limitation is correctly and explicitly disclaimed in the report
  footnote ("Allocation-site precision is ETW-dependent...") rather than silently omitted.

---

## Final Executive Summary

### Overall Assessment

**Score: 82 / 100** (up from 62/100 at the previous audit — 18/18 prior roadmap items resolved,
including the P0-1 int-overflow correctness defect; two new capabilities added since that closed
two of the three remaining industry-benchmark gaps).

**Production Readiness**: Yes, unconditionally. The correctness defects that gated the previous
"conditional" verdict (int overflow, ignored option, inverted pressure formula) are all resolved.
Remaining findings in this re-audit are efficiency and clarity improvements, not correctness
defects that could produce wrong numbers.

**Major Strengths:**
- Zero heap-object scan for its core classification (Phase 1 aggregate-only), with two small,
  bounded, documented exceptions for exact byte totals and type-name resolution.
- Dual count/size percentage + absolute byte reporting for all four generations.
- Highly configurable selection algorithm (mode × strategy × priority × thresholds) with
  preset-driven defaults and Fast/Balanced/Full presets that meaningfully differ in cost/quality
  tradeoff.
- LOH size-band distribution and finalizable-type surfacing now reuse existing Phase 1
  infrastructure rather than requiring new heap-scan capability — both delivered as low-difficulty
  improvements rather than the "Evolution/Very High difficulty" the previous audit assumed.
- GC pressure score scale is documented in-report (P2-6); finding evidence embeds concrete
  type names and finalizable counts rather than generic advice.

**Major Weaknesses:**
- `TryGetTypeName` resolves names for every scanned candidate (up to 20,000 for `Full`), not just
  the emitted top-N — a performance regression of the same class P1-1 already fixed once.
- `LohThresholdBytes` option is silently ignored (documented but has zero effect).
- Two independently-tuned, unreconciled "pressure" scores reported side by side with no explained
  relationship.
- `ComputeExactGenBytes`'s live-ClrMD, segment-based happy path has zero test coverage in this
  analyzer's test suite.

### Priority Roadmap

| Priority | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P1 | Defer `TryGetTypeName` resolution until after top-N selection in both scan-loop branches | High — cuts wasted ClrMD calls by ~100x on `Full` preset | Medium | High | Improvement |
| P1 | Add a test that exercises the `ComputeExactGenBytes` happy path (fake/mock `ClrHeap` or shared test fixture with real segments) | Medium — closes a real coverage gap on the "authoritative" byte-computation path | Medium | High | Improvement |
| P2 | Remove `LohThresholdBytes` from `AllocationPatternAnalysisOptions`, or document it as inert and redirect users to the Phase-1-level constant | Medium — removes a silent-no-op footgun | Low | High | Improvement |
| P2 | Reconcile `pressureScore` and `PromotionPressureScore` into one score, or document their distinct purposes in the report | Medium — reduces risk of triage misinterpretation | Low–Medium | Medium | Improvement |
| P2 | Embed dominant LOH band in finding evidence (mirrors P2-5's top-long-lived-type embedding) | Medium — makes LOH-driven Critical findings immediately actionable | Low | High | Improvement |
| P3 | Surface which `SelectionMode`/`Strategy`/`Priority` combination produced the current report as a key metric | Low — clarity/reproducibility improvement when comparing reports across presets | Low | High | Improvement |
| P3 | Disclose the average-size approximation caveat on the LOH size-band table (and, ideally, on `MemoryAnalysisProjection`'s equivalent table) | Low | Low | High | Improvement |
| P3 | Add per-band trend deltas to `AllocationPatternTrendComparer.Compare()` | Low — data already exists in `ExtractMetrics`, just not diffed | Low | High | Improvement |
| P3 | Expose whether `Gen0Bytes`/`Gen1Bytes`/`Gen2Bytes` came from the exact or approximate path | Low | Low | Medium | Improvement |
| P3 | Correct the "Must run immediately after GCGenerationAnalyzer" doc comment to describe the actual Phase-1-index dependency | Low — documentation accuracy only | Trivial | High | Improvement |

### Final Verdict

1. **Is the analyzer production-ready?** Yes, unconditionally. All correctness defects identified
   in the previous audit are resolved and verified by tests; remaining findings here are
   efficiency (wasted ClrMD calls) and clarity (dual pressure scores, undocumented option) issues,
   not defects that produce incorrect numbers.

2. **Highest-impact improvements** — Deferring `TryGetTypeName` resolution until after top-N
   selection is the single highest-return item: it's a local, well-understood fix (no new
   infrastructure) that removes the analyzer's only remaining scalability concern.

3. **Platform evolution opportunities** — None identified as strictly necessary in this re-audit.
   Both new capabilities added since the last audit (LOH size bands, finalizable detection)
   successfully avoided platform evolution by reusing existing Phase 1 infrastructure built for
   other analyzers — a pattern worth replicating: before scoping a "requires Phase 1 index
   extension" item as Evolution-class work, check whether `HeapIndexBuildResult` already carries
   the needed data for a different analyzer.

4. **Highest engineering return** — The P1 items (defer type-name resolution; test the exact-bytes
   path) together are a half-day of focused work that closes the analyzer's only remaining
   performance concern and its only remaining correctness-coverage gap, with no new
   infrastructure required.

---

## Resolved History

The previous audit (2026-08-14 baseline) tracked 18 roadmap items, all now resolved:

- **P0** (2): int-overflow correctness defect (`Gen0/1/2Count` promoted to `long`, binary format
  v2→v3); `TransientClassificationThreshold` option ignored, now read correctly.
- **P1** (5): live ClrMD type-name resolution replaced with `TypeMetadataCache`-backed helper;
  absolute byte totals added; per-type `TotalSize` added; GC pressure formula inverted-Gen0 fix;
  dead variables removed.
- **P2** (6): Gen1 survivor-rate heuristic; cancellation checks in inner loops; progress reporting
  for `FullScan`; partial-sort optimization; top-long-lived-type embedded in finding evidence;
  pressure-score thresholds documented in the section builder.
- **P3** (5): `AllocationProfile.Steady` split into heap-level-only `AllocationProfile` vs.
  type-level-only `TypeProfile` enum; runtime guard added for missing `HeapIndexBuildResult`;
  LOH size-band distribution added (P3-3, this re-audit's predecessor scope); finalizable-type
  detection added (P3-4, same); dominator/retention-tree item (former P3-5) moved to
  [dominator-analyzer-audit.md](dominator-analyzer-audit.md), which already tracks the equivalent
  work as its own P3 item.
