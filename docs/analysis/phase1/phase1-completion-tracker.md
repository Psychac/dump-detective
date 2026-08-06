# Phase 1 Analyzer Audit — Completion Tracker

**Purpose:** Track audit completion status, coverage, and priority recommendations across all Phase 1 analyzers.

**Last Updated:** 2026-08-06

---

## Executive Summary

| Metric | Value |
|--------|-------|
| **Audits Completed** | 2 / 34 |
| **Completion Rate** | 5.9% |
| **Critical (P0) Recommendations** | TBD |
| **High (P1) Recommendations** | TBD |
| **Medium (P2) Recommendations** | TBD |
| **Blockers** | None |

---

## Analyzer Audit Status

> **Legend:**
> - `✅` COMPLETE — All 7 audit areas + executive summary
> - `🟡` IN PROGRESS — 1-6 audit areas
> - `⏳` PENDING — Not started
> - `Coverage` — # of audit areas completed
> - `P0/P1` — Critical and High priority recommendations identified

| # | Analyzer | Status | Coverage | P0 | P1 | Notes |
|---|----------|--------|----------|----|----|-------|
| 1 | AsyncStateMachineAnalyzer | ✅ | 7/7 | 0 | 6 | IsAsyncStateMachineType flag, GC gen data, state histogram |
| 2 | AsyncTaskAnalyzer | ⏳ | 0/7 | — | — | |
| 3 | ArrayAnalyzer | ⏳ | 0/7 | — | — | |
| 4 | AllocationPatternAnalyzer | ⏳ | 0/7 | — | — | |
| 5 | BoxingAnalyzer | ⏳ | 0/7 | — | — | |
| 6 | CollectionAnalyzer | ⏳ | 0/7 | — | — | |
| 7 | CrashAnalyzer | ⏳ | 0/7 | — | — | |
| 8 | DbConnectionAnalyzer | ⏳ | 0/7 | — | — | |
| 9 | DominatorAnalyzer | 🟡 | 2/7 | — | — | [dominator-analyzer-audit.md](dominator-analyzer-audit.md) in progress |
| 10 | EventLeakAnalyzer | ⏳ | 0/7 | — | — | |
| 11 | FinalizableObjectAnalyzer | ✅ | 7/7 | 0 | 8+ | Per-type queue count, undisposed detection, LOH tracking |
| 12 | GCGenerationAnalyzer | ⏳ | 0/7 | — | — | |
| 13 | GCHandleAnalyzer | ⏳ | 0/7 | — | — | |
| 14 | GCRootAnalyzer | ⏳ | 0/7 | — | — | |
| 15 | HangAnalyzer | ⏳ | 0/7 | — | — | |
| 16 | HeapTopologyAnalyzer | ⏳ | 0/7 | — | — | |
| 17 | HttpObjectAnalyzer | ⏳ | 0/7 | — | — | |
| 18 | JitAnalyzer | ⏳ | 0/7 | — | — | |
| 19 | LeakCandidateAnalyzer | ⏳ | 0/7 | — | — | |
| 20 | LockGraphAnalyzer | ⏳ | 0/7 | — | — | |
| 21 | LohFragmentationAnalyzer | ⏳ | 0/7 | — | — | |
| 22 | ModuleAnalyzer | ⏳ | 0/7 | — | — | |
| 23 | ObjectShapeAnalyzer | ⏳ | 0/7 | — | — | |
| 24 | ReferenceChainAnalyzer | ⏳ | 0/7 | — | — | |
| 25 | SegmentReservationAnalyzer | ⏳ | 0/7 | — | — | |
| 26 | StaticRootLeakDetector | ⏳ | 0/7 | — | — | |
| 27 | StringAnalyzer | ⏳ | 0/7 | — | — | |
| 28 | ThreadAnalyzer | ⏳ | 0/7 | — | — | |
| 29 | ThreadStackClusterAnalyzer | ⏳ | 0/7 | — | — | |
| 30 | TimerLeakAnalyzer | ⏳ | 0/7 | — | — | |
| 31 | WeakReferenceAnalyzer | ⏳ | 0/7 | — | — | |
| 32 | WcfChannelAnalyzer | ✅ | 7/7 | 0 | 6+ | Opening/Closing states, ChannelFactory detection, endpoint extraction |
| 33 | MemoryAnalyzer | ⏳ | 0/7 | — | — | |
| 34 | Phase1ReviewFramework | — | — | — | — | [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md) — audit protocol/framework |

---

## Recommendation Summary by Analyzer

### ✅ AsyncStateMachineAnalyzer

**Overall Score:** 72/100 | **Production Readiness:** Ready with known gaps  
**Date Completed:** 2026-08-03

**Major Strengths:**
- Correct delegation to `TypeAggregates` index
- Cohesive role (population, closure, suspended map)
- Per-method aggregation useful for debugging

**Major Weaknesses:**
- No `IsAsyncStateMachineType` flag (O(types) scan vs O(1) flag check)
- No GC generation data (cannot distinguish ephemeral vs long-lived suspensions)
- Single `AvgStateValue` sample vs full state histogram
- No Task linkage or `async void` distinction

**P0 Recommendations:** None

**P1 Recommendations (6):**
1. Add `IsAsyncStateMachineType` flag to `TypeAggregateFlags` — Difficulty: **Low**, Impact: **High** (100%+ perf improvement on type-heavy apps)
2. Expose Gen0/Gen1/Gen2 counts in `StateMachineTypeProfile` — Difficulty: **Low**, Impact: **Medium** (diagnostic clarity)
3. Capture state value histogram instead of single average — Difficulty: **Medium**, Impact: **High** (identifies stuck await points)
4. Link state machines to backing `Task` instances — Difficulty: **High**, Impact: **Medium** (cross-analyzer correlation)
5. Flag `async void` methods explicitly — Difficulty: **Low**, Impact: **Medium** (risk awareness)
6. Track capture-depth for nested closures — Difficulty: **Medium**, Impact: **Medium** (retention accuracy)

**Related Memories:**
- [[P0-1 AsyncStateMachine flag]]
- [[P0-2 AsyncStateMachine Gen2 exposed]]
- [[P0-3 AsyncStateMachine SampleState rename]]
- [[P1-1 AsyncStateMachine ClrMD defer]]
- [[P1-2 AsyncStateMachine regex safety]]
- [[P1-4 AsyncStateMachine severity escalation]]

---

### ✅ FinalizableObjectAnalyzer

**Overall Score:** 78/100 | **Production Readiness:** Ready, active improvements ongoing

**Date Completed:** 2026-08-03

**Major Strengths:**
- Efficient `TypeAggregates` index usage
- Correct `EnumerateFinalizableObjects()` API usage
- Bounded BFS constraints
- Four cross-analyzer correlation rules in InsightEngine
- Per-type queue count breakdown **[FIXED]**
- Undisposed IDisposable detection **[FIXED]**

**Major Weaknesses (Pre-Fix):**
- ~~Opaque "queue pressure" metric~~ **[FIXED]**
- ~~No per-type queue count breakdown~~ **[FIXED]**
- Missing generation breakdown per queue entry
- BFS retained-size terminology not explained (can significantly under-count)
- Per-entry disposed field data not aggregated in report
- Double-counted retained size (BFS shared refs)

**P0 Recommendations:** None

**P1 Recommendations (8+):**
1. Add generation breakdown to queue entries — Difficulty: **Low**, Impact: **Medium**
2. Clarify BFS depth/node cap in report (maxBfsNodes/maxBfsDepth per profile) — Difficulty: **Low**, Impact: **High** (clarity)
3. Aggregate disposed field findings ("N of M entries undisposed") — Difficulty: **Low**, Impact: **Medium**
4. Remove LINQ import (line 7 of SectionBuilder) — Difficulty: **Trivial**, Impact: **Consistency**
5. Cache `IsDisposableType` by MethodTable — Difficulty: **Low**, Impact: **High** (O(types) → O(1) per type)
6. Cache `FindDisposedField` by MethodTable — Difficulty: **Low**, Impact: **High** (eliminates per-entry re-enumeration)
7. Reuse BFS buffers across entries — Difficulty: **Medium**, Impact: **Low** (GC pressure reduction)
8. Correlate with RootIndex for queue entry root paths — Difficulty: **Medium**, Impact: **Medium** (retention evidence)

---

### ✅ WcfChannelAnalyzer

**Overall Score:** 75/100 | **Production Readiness:** Ready with expansion gaps

**Date Completed:** 2026-08-03

**Major Strengths:**
- Parallel heap scan infrastructure (faster than sequential `DbConnectionAnalyzer`)
- Per-type table comprehensive and actionable
- Correct faulted channel detection
- Cross-correlation with ObjectDisposedException in InsightEngine
- Trend comparer covers key metrics

**Major Weaknesses:**
- `OtherChannels` opaque (merges Opening, Closing, Created)
- No `OpeningChannels` or `ClosingChannels` fields in domain model (P0-1 **[FIXED]**)
- ChannelFactory detection absent (detects channel misuse but not factory anti-pattern)
- Endpoint address not extracted (engineers must manual `!do <addr>`)
- Duplex and session channels not differentiated
- No binding-type inference from type name suffix
- Total bytes not in KeyMetrics aggregation

**P0 Recommendations:** None

**P1 Recommendations (6+):**
1. Expose `OpeningChannels` and `ClosingChannels` as first-class metrics **[FIXED]** — Difficulty: **Low**, Impact: **High** (diagnostic value)
2. Add endpoint address extraction — Difficulty: **Medium**, Impact: **High** (investigation workflow)
3. Detect ChannelFactory<T> creation per-call — Difficulty: **Medium**, Impact: **High** (anti-pattern detection)
4. Distinguish duplex/session channels — Difficulty: **Low**, Impact: **Medium**
5. Infer binding type from type name suffix — Difficulty: **Low**, Impact: **Medium** (failure mode correlation)
6. Aggregate total bytes in domain result and KeyMetrics — Difficulty: **Low**, Impact: **Medium**

**Related Memories:**
- [[P0-1 WCF Channel opening/closing states]]

---

## Audit Area Completion Matrix

| Audit Area | Completed | %Complete |
|------------|-----------|-----------|
| **1. Role & Opportunity Assessment** | 3/34 | 8.8% |
| **2. Diagnostic & Report Quality** | 3/34 | 8.8% |
| **3. ClrMD & Platform Utilization** | 3/34 | 8.8% |
| **4. Diagnostic Opportunity Analysis** | 3/34 | 8.8% |
| **5. Performance, Memory & Scalability** | 3/34 | 8.8% |
| **6. Correctness & Confidence** | 3/34 | 8.8% |
| **7. Industry Benchmark** | 3/34 | 8.8% |

---

## Recommendation Priority Triage

### Critical (P0)

**None identified so far.** (First 3 audits yielded no blockers or show-stoppers.)

### High (P1) — Recommended for Phase 1 Roadmap

**AsyncStateMachineAnalyzer:**
- Add `IsAsyncStateMachineType` flag to `TypeAggregateFlags` [**Difficulty: Low, Impact: High**]
- Expose Gen0/Gen1/Gen2 counts in model [**Difficulty: Low, Impact: Medium**]
- Capture state value histogram [**Difficulty: Medium, Impact: High**]

**FinalizableObjectAnalyzer:**
- Cache `IsDisposableType` by MethodTable [**Difficulty: Low, Impact: High**]
- Cache `FindDisposedField` by MethodTable [**Difficulty: Low, Impact: High**]
- Clarify BFS depth/cap in report [**Difficulty: Low, Impact: High**]

**WcfChannelAnalyzer:**
- Expose `OpeningChannels`/`ClosingChannels` states [**Difficulty: Low, Impact: High**] [**✅ DONE**]
- Extract endpoint address [**Difficulty: Medium, Impact: High**]
- Detect ChannelFactory per-call anti-pattern [**Difficulty: Medium, Impact: High**]

### Medium (P2)

**Cross-Analyzer Opportunities:**
- Link AsyncStateMachine to backing Task (AsyncStateMachine ↔ AsyncTask correlation)
- Correlate FinalizableObject queue entries with RootIndex for path evidence
- Promote DetectKnownFinalizerQueuePatterns from InsightEngine to FinalizableObjectFindingGenerator

### Low (P3)

**Nice-to-Have Improvements:**
- Nested closure depth tracking (AsyncStateMachine)
- GC pressure ratio metric (FinalizableObject)
- Binding type inference (WcfChannel)

---

## Next Steps

1. **Continue audits for remaining 31 analyzers** — Prioritize high-impact ones (AsyncTaskAnalyzer, LeakCandidateAnalyzer, ThreadAnalyzer, DominatorAnalyzer).
2. **Cross-reference recommendations** — Some P1 items (IsAsyncStateMachineType flag, index recommendations) will impact multiple analyzers.
3. **Batch platform-level improvements** — Group infrastructure changes (new indexes, new flags, ClrMD optimizations) into single sweep.
4. **Implement highest-impact P1s first** — IsAsyncStateMachineType flag + MethodTable caching likely yield 10-50% perf gains across multiple analyzers.

---

## Notes & Observations

- **Audit format working well:** Seven areas provide comprehensive coverage without overwhelming detail.
- **Recommendations are implementable:** Most P1 items are Low-to-Medium difficulty with clear acceptance criteria.
- **Cross-analyzer patterns emerging:**
  - Multiple analyzers need type-level caching (IsDisposable, IsFinalizableType, etc.) → candidate for shared `TypeMetadataCache`
  - BFS-based retained size estimation used in multiple analyzers → standardize with clear depth/cap documentation
  - Task/state machine correlation is natural → should be first-class platform feature, not analyzer-specific workaround
- **Infrastructure debt:** `IsThreadSafe` property missing from `IAnalyzer` interface (documentation references it but interface does not declare it).
