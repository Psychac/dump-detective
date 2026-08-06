# Phase 1 Redesigns — Completion Tracker

**Purpose:** Track redesign evaluation status, implementation progress, and measured performance baselines across Phase 1 analyzers.

**Last Updated:** 2026-08-06

---

## Executive Summary

| Metric | Value |
|--------|-------|
| **Redesign Evaluations Complete** | 3 / ~34 |
| **Completion Rate** | 8.8% |
| **Implementation Plans Ready** | 1 / 3 |
| **Performance Baselines Measured** | 2 / 3 |
| **Critical (P0) Redesign Wins** | 3 (crash: bugs, event: scale, dominator: perf) |

---

## Redesign Status

> **Legend:**
> - `✅` COMPLETE — Full redesign evaluation + implementation plan
> - `🟡` IN PROGRESS — Redesign doc written, plan in progress
> - `📊` MEASURED — Performance baseline established
> - `⏳` PENDING — Not started
> - `Perf Baseline` — Measured throughput or runtime data

| # | Component | Status | Doc | Plan | Perf | Impact | Complexity |
|---|-----------|--------|-----|------|------|--------|------------|
| 1 | CrashAnalyzer | 🟡 | ✅ [crash-analyzer.md](crash-analyzer.md) | ⏳ | — | **High** (2 P0 bugs) | **Medium** |
| 2 | CollectionAnalyzer | 🟡 | ✅ [collection-analyzer.md](collection-analyzer.md) | ⏳ | — | **Medium** | **Medium** |
| 3 | DominatorAnalyzer | 🟡 | ✅ [dominator-analyzer.md](dominator-analyzer.md) | ⏳ | — | **Medium** (perf) | **High** |
| 4 | EventLeakAnalyzer | ✅ | ✅ [event-leak-analyzer.md](event-leak-analyzer.md) | ✅ [event-leak-analyzer-implementation-plan.md](event-leak-analyzer-implementation-plan.md) | 📊 | **High** (scale) | **High** |
| 5 | RootPathFinder | 🟡 | ✅ [root-path-finder.md](root-path-finder.md) | ⏳ | 📊 | **High** (foundational) | **High** |
| — | — | — | — | — | — | — | — |
| — | **Remaining 29** | ⏳ | — | — | — | — | — |

---

## Detailed Redesign Evaluations

### ✅ EventLeakAnalyzer

**Status:** Complete — redesign evaluated, implementation plan ready  
**Date:** 2026-08-03

**Redesign Doc:** [event-leak-analyzer.md](event-leak-analyzer.md)  
**Implementation Plan:** [event-leak-analyzer-implementation-plan.md](event-leak-analyzer-implementation-plan.md)

#### Measured Baseline (3.3 GB dump: Crash_IIS_BALTSTPRD)

| Phase | Time | % | Nature |
|-------|------|---|--------|
| `PopulateEvidence` (root-path BFS) | 34.28s | 36% | 3,321 instances × 10.3ms; 229 paths found |
| `BuildFieldLayouts` | 22.80s | 24% | Per-unique-MT ClrMD metadata (~1.6ms × 14,003 MT) |
| `SweepModuleStaticFields` | 19.51s | 21% | Second full walk over module types |
| `BuildRootHintMap` | 15.44s | 16% | GetOrBuildValidRoots (760 roots) |
| `ProcessPublisherEntry` (hot path) | 1.48s | 1.6% | Per-object scan |
| **TOTAL** | **94.74s** | — | — |

#### Measured Scale (25.6 GB dump)

| Phase | 3.3 GB | 25.6 GB | Stability |
|-------|--------|---------|-----------|
| `BuildFieldLayouts` | 22.80s | ~121.6s | Stable |
| `PopulateEvidence` (BFS) | 34.28s (6.9% hit) | ~60.5s (1.4% hit) | Stable, **hit rate degrades with scale** |
| `SweepModuleStaticFields` | 19.51s | ~19.3s | Stable |
| `ProcessPublisherEntry` (hot path) | 1.48s | 3.3s–28.2s | **Highly noisy** |
| **TOTAL** | **94.74s** | **~215s** | — |

#### Key Findings

1. **Hot path is NOT the bottleneck** — only 1.6% of runtime; redesigns targeting per-object allocation waste effort.
2. **BFS evidence enrichment degrades on large dumps** — 6.9% → 1.4% hit rate (3.3GB → 25.6GB). Redesign must support bounded enrichment fallback.
3. **BuildFieldLayouts dominated on 25.6GB** (121.6s, 57% of 215s) — **single-pass MethodTable registry cache would save ~18s** (21% estimated).
4. **Root-set build shared across multiple analyzers** — EventLeak, GCRoot, StaticRootLeakDetector all call `GetOrBuildValidRoots`. Consolidation opportunity.

#### Redesign Proposal

**7-phase implementation, phased delivery:**

| Phase | Focus | Complexity | Gate |
|-------|-------|-----------|------|
| P1 | Bounded evidence enrichment + Tier 1 retained bytes | Medium | Accuracy tests pass |
| P2 | Correctness fixes independent of registry | Medium | Discrepancy tests pass |
| P3 | PublisherRegistry + FieldBackedDelegateShape | High | Perf baseline vs baseline |
| P4 | Registry-driven statics | Medium | GC pressure measured |
| P5 | Correlation phase | High | Cross-analyzer tests |
| P6 | Structured presentation data | Medium | Report quality |
| P7 | EventHandlerListShape / WeakEventShape | Low (additive) | Trend tests pass |

**Expected Outcome:**
- ~50% perf improvement on 3.3GB dumps (50–60s → 25–30s), driven by MethodTable registry caching
- ~40% improvement on 25.6GB dumps (215s → 130s), driven by bounded enrichment + registry
- Hit rate maintenance through fallback strategy for large-dump edge case

**Baseline Tests:**
- `EventLeakAnalyzerAccuracyTests` ✅
- `EventLeakAnalyzerDiscrepancyTests` ✅
- `EventLeakFindingGeneratorTests` ✅
- `HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown` (perf harness)

---

### 🟡 CrashAnalyzer

**Status:** Redesign evaluated; implementation plan pending  
**Date:** 2026-08-03

**Redesign Doc:** [crash-analyzer.md](crash-analyzer.md)

#### Problems Identified

**Two P0 Correctness Bugs:**

1. `BuildCrashThreadSnapshots` **ignores configured options** — analyzer accepts `CrashAnalysisOptions.FilterLevel` but thread-snapshot builder does not consult it; filtering happens only for exception side.
2. `_stackTrace` **byte-buffer misread** — direct field-walking over CLR layout-dependent `_stackTrace` field is fragile and potentially incorrect (architecture-dependent buffer alignment).

**Structural Issues:**

- Dual-path implementation (participant path + fallback scan path) with "must keep in sync manually" — no compiler enforcement, risk of divergence (now realized: two bugs only in one path).
- Five of fifteen improvement items (I-2, I-3, I-9, I-11, E-1) all touch `ExtractExceptionInfo` call site — patching independently risks fresh divergence.
- Unbounded `SampleMessage` design cannot structurally support message-distribution diagnostics without rework.

#### Redesign Proposal

**Collapse dual-path into single accumulator:**

```csharp
private readonly struct ExceptionVisit
{
    public static void Process(
        ClrHeap heap,
        ulong address,
        ulong methodTable,
        uint generation,
        ulong size,
        ActiveExceptionContext? activeContext,
        ExceptionScanAccumulator acc,
        ILogger? logger)
}
```

**Three key changes:**

1. Single per-object extraction function, called from exactly one place per object from both paths — eliminates dual-implementation drift risk as byproduct.
2. Replace field-walking with `ClrException` API — fixes byte-buffer bug, removes five fragile `GetFieldByName` lookups, handles `.Inner` chain with type safety.
3. AggregateException unwrapping inline (first 16 entries, matching existing depth cap) — no second heap scan.

**Expected Outcome:**
- ✅ Bug 1 fixed: options flow to both paths via single accumulator
- ✅ Bug 2 fixed: `ClrException.StackTrace` correctly parsed by ClrMD
- ✅ Five improvement items addressed as side effect of structural pass
- ✅ Message distribution framework enabled for future work

**Complexity:** Medium — one structural refactor, no new algorithms.

**Blockers:** None.

---

### 🟡 CollectionAnalyzer

**Status:** Redesign evaluated; implementation plan pending  
**Date:** — (inferred from doc presence)

**Redesign Doc:** [collection-analyzer.md](collection-analyzer.md)

**Status Details:** Awaiting content review and summary. (File exists but not yet analyzed in detail.)

---

### 🟡 DominatorAnalyzer

**Status:** Redesign evaluated; implementation plan pending  
**Date:** — (inferred from doc presence)

**Redesign Doc:** [dominator-analyzer.md](dominator-analyzer.md)

#### Key Design Points

**Problem:** Current implementation conflates three separate concerns:

1. Hot-path fan-in scan
2. Separately-sourced type-statistics pass
3. Post-scan BFS-based retention estimate (silently disagrees with itself about "retained bytes" meaning)

**Every major weakness traces back** to three passes never sharing data model:
- `HeuristicOnly` always true
- Inconsistent exclusivity semantics
- Admission-ordering bias
- Invisible `gen2Count` (not exposed in output)

#### Redesign Proposal

**One hot-path pass produces everything scan can answer:**

```csharp
private readonly struct TypeAggregate
{
    public ulong TotalSize;
    public ulong LohSize;
    public int Gen2Count;
    public int Count;
}

// keyed by MethodTable (ulong) — no string allocation on hot path
Dictionary<ulong, TypeAggregate>? _typeAggregates;
private FanInSketch? _fanIn; // Space-Saving heavy-hitters sketch
```

**Key improvements:**

1. Per-type stats aggregated on hot path (no second pass)
2. Replace admission-ordered reference dictionary with **Space-Saving streaming algorithm** — admits top-K without order bias
3. Unify data model across scan and retention phases

**Expected Outcome:**
- ~30–50% perf improvement (one pass instead of three partial passes)
- Correct top-N dominator identification (not admission-biased)
- Consistent "retained bytes" semantics across output

**Complexity:** High — algorithm change + multi-pass unification.

---

### 🟡 RootPathFinder

**Status:** Performance baseline measured; implementation plan pending  
**Date:** — (inferred from doc presence)

**Redesign Doc:** [root-path-finder.md](root-path-finder.md)

#### Measured Baseline

**Shared by EventLeakAnalyzer and others** as evidence enrichment foundation.

- Hit rate degrades significantly on 25.6GB+ dumps (6.9% → 1.4%)
- BFS cost stable but hit rate means enrichment returns sparse evidence on large heaps
- Design doc identifies this as foundational bottleneck for EventLeak Phase 1 roadmap

**Status:** Measured, diagnosis complete; implementation plan deferred until EventLeak Phase 6 ships and `EventLeakDomainResult` shape is stable.

---

## Redesign Recommendation Triage

### Critical (P0) — Shipping Requirements

**CrashAnalyzer:** Two P0 correctness bugs block production use
- Implement single-accumulator refactor and `ClrException` swap
- Estimated effort: 1–2 weeks
- Blocker risk: **High** (bugs active in prod)

**EventLeakAnalyzer:** Scale degradation (1.4% BFS hit on 25.6GB)
- Implement bounded enrichment fallback (Phase 1)
- Estimated effort: 3–4 weeks (phased over 7 phases)
- Blocker risk: **Medium** (Phase 1 is independent, later phases build on it)

### High (P1) — Performance Roadmap

**DominatorAnalyzer:** Three-pass design with order-bias bug
- Implement Space-Saving algorithm + one-pass unification
- Estimated effort: 2–3 weeks
- Impact: 30–50% perf gain + correctness fix

**RootPathFinder:** Foundational; EventLeak's Phase 6 depends on it
- Implement after EventLeak Phase 1–5 stabilize `EventLeakDomainResult`
- Estimated effort: 2–3 weeks (deferred)
- Impact: Unlocks EventLeak Phase 6 (Tier 2 retained bytes)

### Medium (P2) — Enhancement Roadmap

**CollectionAnalyzer:** Awaiting detailed analysis

---

## Implementation Status Summary

### Ready for Implementation

| Component | Phase | Effort | Gate | Owner |
|-----------|-------|--------|------|-------|
| **CrashAnalyzer** | Redesign → Implementation | 1–2w | Accuracy + discrepancy tests | — |
| **EventLeakAnalyzer P1** | Phase 1 (bounded enrichment) | 3–4w | Perf harness vs baseline | — |

### In Design/Evaluation

| Component | Next Step | Blocker |
|-----------|-----------|---------|
| **DominatorAnalyzer** | Write implementation plan | None |
| **CollectionAnalyzer** | Full redesign evaluation | None |
| **RootPathFinder** | Defer until EventLeak P6 ready | EventLeak P1–5 stability |

---

## Cross-Analyzer Patterns

### Shared Infrastructure Opportunities

1. **MethodTable-level metadata registry**
   - **Affects:** EventLeakAnalyzer (21% improvement), CollectionAnalyzer, others
   - **Proposal:** Single `TypeMetadataRegistry` consuming cached `GetTypeByMethodTable` results
   - **Effort:** 1 week, high reuse ROI

2. **Space-Saving heavy-hitters sketch**
   - **Affects:** DominatorAnalyzer (reference-count bias fix), any top-K aggregation
   - **Proposal:** Shared `HeavyHittersSketch<K>` utility
   - **Effort:** 1 week, reusable pattern

3. **Bounded BFS with fallback strategy**
   - **Affects:** EventLeakAnalyzer (evidence enrichment), FinalizableObjectAnalyzer (retained bytes), others
   - **Proposal:** Parameterizable `BoundedBfsRetentionEstimator` with depth/node caps
   - **Effort:** 1–2 weeks, foundational

### Platform-Level Learnings

- **Single extraction function pattern** (CrashAnalyzer → `ExceptionVisit.Process`) is proving ground for generalizing to all `IHeapIndexScanParticipant` implementations — reduces dual-path maintenance burden.
- **Measured performance baselines** reveal that common optimization targets (hot-path allocation, LINQ) often contribute <5% runtime — measure before optimizing.
- **BFS hit rate degradation** on large dumps is systematic, not random — requires fallback strategy, not just parameter tuning.

---

## Timeline Recommendations

### Immediate (August 2026)

1. **CrashAnalyzer:** Implement redesign (P0 bug fix)
   - Risk: Medium correctness impact if not shipped
   - Effort: 1–2 weeks

### Near-term (August–September 2026)

2. **EventLeakAnalyzer Phase 1:** Bounded enrichment
   - Risk: Scale regression if Phase 1 not gated on perf harness
   - Effort: 3–4 weeks
3. **Shared infrastructure:** MethodTable registry + Space-Saving sketch
   - Risk: Low (enabler for P2 work)
   - Effort: 2 weeks

### Medium-term (September–October 2026)

4. **EventLeakAnalyzer Phases 2–5:** Registry + correlation + presentation
   - Effort: 8–10 weeks (phased)
5. **DominatorAnalyzer:** One-pass redesign
   - Effort: 2–3 weeks

### Deferred (post-EventLeak Phase 5)

6. **RootPathFinder:** Tier 2 design
   - Dependency: EventLeakDomainResult shape stable
   - Effort: 2–3 weeks

---

## Notes & Observations

- **Redesign format is effective:** Performance baselines + problem diagnosis + phased proposal makes it clear which changes are critical, which are speculative.
- **Dual-path anti-pattern is real:** Both CrashAnalyzer and DominatorAnalyzer have separate codepaths that must stay in sync. EventLeakAnalyzer redesign correctly identifies this as platform-level risk worth solving once.
- **Measurement is mandatory:** EventLeakAnalyzer's baseline data justified P1 prioritization over hot-path optimization (which would've been wasted effort). Dominator and Root-PathFinder diagnostics require similar rigor before committing effort.
- **Phased delivery mitigates risk:** EventLeakAnalyzer's 7-phase plan allows independent test gates and performance validation at each step, vs big-bang refactor.
