# Analyzer Coverage Analysis
## Professional Tier Report — Section-to-Analyzer Mapping & Capability Gap Analysis

> **Scope**: This document maps every section of `ProfessionalTierReport.md` to the existing
> analyzer set, then provides a per-analyzer deep-dive covering what each one currently produces,
> what is missing, and exactly what must be changed, added, or split to achieve full report coverage.

---

# Part 1 — Section → Analyzer Mapping

## Legend
| Symbol | Meaning |
|--------|---------|
| ✅ | Fully covered by current analyzer output |
| 🟡 | Partially covered — data exists but is incomplete or mis-scoped |
| ❌ | Not covered — no analyzer produces this data |
| ➕ | Requires a new analyzer |
| ✂️ | Requires splitting an existing analyzer |

---

## §1 · Executive Summary

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total managed memory + % of process | 🟡 | `MemoryAnalyzer` — has total managed bytes; no process-total context |
| Top memory consumers by **retained** size | ❌ | No analyzer computes retained size; `MemoryAnalyzer` provides shallow size only |
| Memory leak likelihood score | 🟡 | `MemoryLeakAnalyzer` + `StaticRootLeakDetector` — signals exist but no unified score |
| GC pressure indicator | 🟡 | `GCGenerationAnalyzer` — generation data exists; no single "GC pressure" score |
| Thread contention indicator | 🟡 | `ThreadAnalyzer` + `LockGraphAnalyzer` — data present; no unified contention score |
| Top 3 actionable recommendations | 🟡 | `InsightEngine` — produces ranked findings but no formal "top 3" executive summary |

**Verdict**: ❌ No `ExecutiveSummaryGenerator` exists. The `InsightEngine` is the closest foundation
but it emits granular `InsightFinding` records, not a structured executive summary with scored
indicators. A dedicated report-layer component (not a pipeline analyzer) is needed.

---

## §2 · Memory Topology

### 2.1 Heap Composition

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| SOH / LOH / POH proportions | ✅ | `SegmentAnalyzer` — `SegmentAnalysisDomainResult` with per-kind bytes |
| Frozen segment proportion | ✅ | `SegmentAnalyzer` — includes Frozen kind |
| Object size distribution (histogram) | ❌ | No analyzer produces a size-bucket histogram |

### 2.2 Generation Pressure

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Gen0 / Gen1 / Gen2 distribution (bytes + count) | ✅ | `GCGenerationAnalyzer` — `GCGenerationDomainResult` |
| LOH distribution | ✅ | `GCGenerationAnalyzer` + `LohFragmentationAnalyzer` |
| Promotion patterns (% objects promoted Gen0→1, Gen1→2) | ❌ | Not computable from a single snapshot; single-dump artifact |

### 2.3 Allocation Patterns

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Short-lived vs long-lived object ratio per type | ❌ | ➕ `AllocationPatternAnalyzer` |
| Burst vs steady allocation heuristic | ❌ | ➕ `AllocationPatternAnalyzer` |
| Heuristic classification (GC pressure category) | ❌ | ➕ `AllocationPatternAnalyzer` |

---

## §3 · Type System Analysis

### 3.1 Detailed Type Table

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Count per type | ✅ | `MemoryAnalyzer` — `TypeSnapshot` |
| Shallow size per type | ✅ | `MemoryAnalyzer` — `TypeSnapshot.TotalSize` |
| Average size per type | 🟡 | Derivable from count + total size; not explicitly surfaced |
| **Estimated retained size** per type | ❌ | ➕ `DominatorAnalyzer` |

### 3.2 Dominator Candidates

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| High-retention objects (objects retaining large sub-graphs) | ❌ | ➕ `DominatorAnalyzer` |

### 3.3 Object Shape Analysis

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Reference-heavy vs value-heavy type classification | ❌ | ➕ `ObjectShapeAnalyzer` |
| Field layout profile per type | ❌ | ➕ `ObjectShapeAnalyzer` |

---

## §4 · Retention & Dominator Analysis

### 4.1 Retention Hotspots

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Objects retaining large reference graphs | ❌ | ➕ `DominatorAnalyzer` |

### 4.2 Dominator Tree (Approximate)

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Memory impact if an object is removed | ❌ | ➕ `DominatorAnalyzer` |
| Approximate retained-size per node | ❌ | ➕ `DominatorAnalyzer` |

### 4.3 Retention Patterns

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Cache chain detection | 🟡 | `StaticRootLeakDetector` — static retention only; no cache-pattern heuristic |
| Event chain detection | 🟡 | `EventLeakAnalyzer` — finds event leaks but not chains of publishers |
| General retention pattern classification | ❌ | ➕ `DominatorAnalyzer` |

---

## §5 · GC Root Intelligence

### 5.1 Root Distribution

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Memory retained by root type (Static/Stack/GCHandle/Finalizer) | 🟡 | `StaticRootLeakDetector` covers static; `GCHandleAnalyzer` covers handles — no unified root distribution |
| Per-root-kind retained bytes | ❌ | ➕ `GCRootAnalyzer` |

### 5.2 Root Severity Ranking

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Most impactful roots ranked by retained bytes | 🟡 | `StaticRootLeakDetector` — `TopRootsByRetainedBytes` but static only |
| Cross-kind severity ranking | ❌ | ➕ `GCRootAnalyzer` |

### 5.3 Root Paths

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Root → object chain paths | 🟡 | `ReferenceChainAnalyzer` samples chains; `BoundedRootPathFinder` exists as utility |
| Structured root-path findings per top suspect | ❌ | ➕ `GCRootAnalyzer` (uses `BoundedRootPathFinder` internally) |

---

## §6 · Memory Leak Analysis

### 6.1 Leak Candidates

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Ranked suspicious types by size / count | 🟡 | `MemoryLeakAnalyzer` — `TopHighlyReferencedObjects`; `StaticRootLeakDetector` — `TopRootsByRetainedBytes` |
| Unified ranked leak candidate list with scores | ❌ | No single result aggregates both |

### 6.2 Leak Classification

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Static retention | ✅ | `StaticRootLeakDetector` |
| Event handler leak | ✅ | `EventLeakAnalyzer` |
| Cache retention (collection over-capacity) | 🟡 | `CollectionAnalyzer` — wasteful collections; not labeled "cache leak" |
| Thread retention | 🟡 | `ThreadAnalyzer` — stack-rooted objects; not quantified as retention |
| Finalizer queue backup | ✅ | `MemoryLeakAnalyzer` — `FinalizerQueueCount` + `TopFinalizerTypes` |

### 6.3 Leak Explanation

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Human-readable cause per leak candidate | 🟡 | `InsightEngine` findings have `Evidence` + `Recommendation`; not per-type narrative |
| Per-type root cause narrative | ❌ | Requires enhancement of `InsightEngine` or new report-layer narrative builder |

### 6.4 Leak Impact

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Memory bytes at risk | 🟡 | `StaticRootLeakDetector` — `TotalRetainedBytes`; others lack this |
| Performance effect description | ❌ | Not produced by any analyzer |

---

## §7 · Thread & Concurrency Analysis

### 7.1 Thread Lifecycle

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total / alive / background / threadpool counts | ✅ | `ThreadAnalyzer` — `ThreadDomainResult` |
| Long-lived threads (always-alive indicators) | 🟡 | `ThreadAnalyzer` has alive count; no "long-lived" classification |
| Thread churn (rapid create/destroy pattern) | ❌ | Not detectable from single snapshot |
| Finalizer thread state | ✅ | `ThreadAnalyzer` — `FinalizerIsBlocked`, `FinalizerFrames` |
| Async chain thread count + max depth | ✅ | `ThreadAnalyzer` — `AsyncChainThreadCount`, `MaxAsyncChainDepth` |

### 7.2 Synchronization Patterns

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Lock contention (contested locks, waiting threads) | ✅ | `LockGraphAnalyzer` — `ContestedLocks`, `TopContestedTypes` |
| Stack-based wait pattern distribution | ✅ | `ThreadAnalyzer` — `WaitCategoryDistribution` |
| Thread cluster / contention hotspot grouping | ✅ | `ThreadStackClusterAnalyzer` |

### 7.3 Deadlock Detection

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Circular wait candidates | ✅ | `LockGraphAnalyzer` — `DeadlockCandidates` |

---

## §8 · Async & Task Analysis

### 8.1 Task Summary

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Pending / Faulted / Canceled / Completed task counts | 🟡 | `HangAnalyzer` — `PendingTasks`, `FaultedTasks`, `CanceledTasks` present |
| Task state distribution as first-class result | 🟡 | Buried inside `HangDomainResult`; mixed with blocking-thread data |

### 8.2 Orphaned Tasks

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Tasks with no continuation (never awaited) | ❌ | ✂️ `HangAnalyzer` does not classify orphaned vs awaited |

### 8.3 Continuation Chains

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Top continuation types | 🟡 | `HangAnalyzer` — `TopContinuationTypes` present |
| Async execution depth (chain length) | 🟡 | `ThreadAnalyzer` — `MaxAsyncChainDepth`; not per-task chain detail |
| Continuation chain as structured path | ❌ | ✂️ Split needed |

**Verdict**: §8 needs `AsyncTaskAnalyzer` split from `HangAnalyzer`. Task data is present but
conflated with hang/blocking data and lacks orphan detection.

---

## §9 · GC & Allocation Pressure

### 9.1 Allocation Patterns

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Short-lived vs long-lived per-type ratio | ❌ | ➕ `AllocationPatternAnalyzer` |

### 9.2 GC Efficiency

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Gen0→Gen1 promotion pressure estimate | ❌ | ➕ `AllocationPatternAnalyzer` (derived from Gen % per type) |
| GC efficiency score | ❌ | ➕ `AllocationPatternAnalyzer` |

### 9.3 Pinning Impact

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Pinned handle count | ✅ | `GCHandleAnalyzer` — `PinnedTypes`, `ByHandleKind["Pinned"]` |
| Pinned object types and sizes | 🟡 | `GCHandleAnalyzer` — type counts present; retained bytes not computed |
| GC blocking impact score | ❌ | `GCHandleAnalyzer` needs retained-size estimation for pinned objects |

---

## §10 · LOH / POH Diagnostics

### 10.1 LOH Summary

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| LOH total size + segment count | ✅ | `LohFragmentationAnalyzer` + `GCGenerationAnalyzer` |
| LOH object type distribution | 🟡 | `GCGenerationAnalyzer` — `TopLohTypes` (top by count/size) |
| POH summary | 🟡 | `SegmentAnalyzer` — POH bytes; no per-object POH breakdown |

### 10.2 Fragmentation

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Per-segment fragmentation % | ✅ | `LohFragmentationAnalyzer` — `LohSegmentSnapshot.FragmentationPercent` |
| Largest free block per segment | ✅ | `LohFragmentationAnalyzer` — `LargestFreeBlock` |
| Free gap distribution | 🟡 | `LohFragmentationAnalyzer` — total free bytes only; no gap histogram |

### 10.3 Large Object Lifetimes

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Long-lived LOH allocations | 🟡 | `GCGenerationAnalyzer` reports LOH object count; no per-object lifetime classification |
| LOH objects in Gen2 (pinned long-lived) | ❌ | No analyzer cross-references LOH membership with generation |

---

## §11 · String & Data Analysis

### 11.1 Duplicate Strings

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Duplicate string count + top patterns | ✅ | `MemoryLeakAnalyzer` — `DuplicateStringPatternCount`, `TopDuplicateStrings` |

### 11.2 Memory Waste

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Wasted bytes from duplicate strings | ✅ | `MemoryLeakAnalyzer` — `DuplicateStringWastedBytes` |
| Total string memory + unique ratio | ✅ | `MemoryLeakAnalyzer` — `TotalStringMemoryBytes`, `UniqueStrings` |

**Verdict**: Data is complete but **buried inside `MemoryLeakDomainResult`**. The report requires
§11 as a standalone section. Needs `StringAnalyzer` split.

---

## §12 · Event & Delegate Analysis

### 12.1 Subscription Graph

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Publisher → subscriber mapping | 🟡 | `EventLeakAnalyzer` — `TopLeakGroups` with `PublisherType.EventFieldName` → subscriber types |
| Full subscription graph (all events, not just leaks) | ❌ | `EventLeakAnalyzer` only scans delegates with `MinSubscribers` threshold |

### 12.2 Event Leaks

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Retained subscriber count per event | ✅ | `EventLeakAnalyzer` — `TotalSubscribers`, `EventLeakGroupSnapshot` |
| Static vs instance publisher breakdown | ✅ | `EventLeakAnalyzer` — `IsStatic`, `StaticLeaks`, `InstanceLeaks` |
| Severity score per leak group | ✅ | `EventLeakAnalyzer` — `SeverityScore` |

---

## §13 · Exception Analysis

### 13.1 Exception Frequency

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Most common exception types | ✅ | `CrashAnalyzer` — `ExceptionTypeCounts` |
| Active vs total exception count | ✅ | `CrashAnalyzer` — `ActiveExceptions`, `TotalExceptions` |

### 13.2 Failure Hotspots

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Thread-to-exception mapping | ✅ | `CrashAnalyzer` — `TopExceptionInstances` with `ThreadId`, `CurrentThreadFrames` |
| Stack frame hotspots for exceptions | ✅ | `CrashAnalyzer` — `OriginalStackTrace` per instance |

---

## §14 · Temporal / Diff Analysis

### 14.1 Growth Trends

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Per-analyzer metric deltas across snapshots | ✅ | `TrendAnalyzer` — `CompareAll`, `CompareSeries` |
| Per-metric timeline across N dumps | ✅ | `TrendAnalyzer` — `ExtractTimeline` |

### 14.2 Regression Detection

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| New leaks not present in baseline | 🟡 | `TrendAnalyzer` — general metric deltas; no "new leak" semantic label |
| Regression severity classification | ❌ | `TrendAnalyzer` produces `MetricTrendDirection`; no regression scoring |

---

## §15 · Visualization

> This is a **report rendering concern**, not an analyzer concern.
> Analyzers must expose structured data (histograms, distributions, ranked lists) that
> renderers can consume. Key data gaps that block visualization:
> - Object size histogram (§2.1) — ❌ missing
> - Retention tree / dominator data (§4) — ❌ missing
> - Root distribution chart data (§5.1) — ❌ missing

---

## §16 · Insights & Recommendations

### 16.1 Findings

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Ranked `InsightFinding` records (Critical → Warning → Info) | ✅ | `InsightEngine` |

### 16.2 Root Cause Narratives

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| `Evidence` field per finding | ✅ | `InsightEngine` |
| `Recommendation` field per finding | ✅ | `InsightEngine` |
| Cross-analyzer correlation (e.g., leak + thread + GC together) | 🟡 | `InsightEngine` partially cross-correlates; no `GCRootAnalyzer` / `DominatorAnalyzer` inputs yet |

### 16.3 Suggested Fixes

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Developer action per finding | ✅ | `InsightEngine` — `Recommendation` field |

---

## §17 · Confidence & Limitations

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| `TaskScanLimited` flag | ✅ | `HangAnalyzer` — `TaskScanLimited` |
| `SkippedReferenceAddresses` | ✅ | `MemoryLeakAnalyzer` — `SkippedReferenceAddresses` |
| Cap/budget signals from `RootPathFinder` | ✅ | `BoundedRootPathFinder` — `PathSearchCapReason`, `Capped` |
| Unified confidence model across all analyzers | ❌ | No cross-analyzer confidence/limitation summary |
| Heuristic classification notes per result | ❌ | Ad-hoc per analyzer; no standard `ConfidenceLevel` field on domain results |

---

---

# Part 2 — Per-Analyzer Deep-Dive

---

## 1. `MemoryAnalyzer` *(modify)*

### Currently Produces
- `MemoryDomainResult`: total bytes, LOH bytes, LOH %, total objects, unique types
- `TopTypesBySize` and `TopTypesByCount` — top 20 `TypeSnapshot` records
- `TypeSnapshot` has: `TypeName`, `Count`, `TotalSize`, `LohSize`

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| `AverageSize` per type | §3.1 | Low — derivable but not surfaced |
| **Retained size** per type | §3.1, §3.2 | High — fundamental gap; shallow only today |
| Object size distribution histogram (size-bucket counts) | §2.1 | Medium |
| Process total memory for % calculation | §1 | Medium |

### Required Changes
1. **Add `AverageSize`** to `TypeSnapshot` — trivially `TotalSize / Count`. Zero cost.
2. **Add `EstimatedRetainedBytes`** to `TypeSnapshot` — set to `0` / `null` by `MemoryAnalyzer`;
   populated by `DominatorAnalyzer` in a post-pass. `MemoryAnalyzer` itself must not walk
   references (that would double heap scan time).
3. **Add `SizeBucketHistogram`** to `MemoryDomainResult` — `IReadOnlyList<SizeBucketEntry>` where
   each bucket is `(RangeLabel, ObjectCount, TotalBytes)`. Build during the existing
   `typeStats` iteration — no extra heap scan required.
4. **Consider** exposing `UniqueTypes` more prominently; already present, just needs surfacing
   in the type table section of the report.

---

## 2. `GCGenerationAnalyzer` *(modify)*

### Currently Produces
- `GCGenerationDomainResult`: Gen0/1/2/LOH bytes + object counts
- `TopLohTypes` — top LOH types by count/size
- Uses parallel generation scan via reflection on `ClrHeap.GetGeneration`

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Per-type generation distribution (Gen0 % vs Gen2 % per type) | §2.2, §9.1, §9.2 | High |
| POH object distribution | §10.1 | Medium |
| Gen2 % as a "long-lived pressure" signal | §9.2 | Medium |

### Required Changes
1. **Add `PerTypeGenerationProfile`** — `IReadOnlyList<TypeGenerationProfile>` on
   `GCGenerationDomainResult` for top N types. Each record:
   ```
   TypeGenerationProfile(string TypeName, int Gen0Count, int Gen1Count, int Gen2Count, int LohCount)
   ```
   This is **computed in the existing parallel scan** — just accumulate per type instead of
   discarding generation data after the global counter update. Use the heap index to avoid
   a second heap walk.
2. **Compute `Gen2Pct`** — Gen2 objects / total objects as a signal for `AllocationPatternAnalyzer`
   to consume. Add as a field to `GCGenerationDomainResult`.
3. **`TopLohTypes`** — currently typed as `IReadOnlyList<TypeSnapshot>?` with default null.
   Make this non-nullable and always populated (empty list if no LOH objects). Consumers
   should not guard against null.

---

## 3. `SegmentAnalyzer` *(modify)*

### Currently Produces
- `SegmentAnalysisDomainResult`: per-kind bytes + object counts (SOH/LOH/POH/Frozen)
- `HeapSegmentSnapshot` list — per-segment address, start, end, committed bytes, generation, kind
- `TopSegmentsCount = 10` largest segments surfaced

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Object size distribution per segment kind | §2.1 | Low |
| Committed vs reserved bytes distinction | §2.1 | Low |
| POH per-type breakdown | §10.1 | Medium |

### Required Changes
1. **Add `CommittedVsReserved`** ratio per segment kind to the result — `SegmentAnalyzer` already
   reads `CommittedBytes` via reflection; add `ReservedBytes` if available in `ClrSegment`.
2. **Add `PohTypeDistribution`** — `IReadOnlyList<NameCountEntry>` — top pinned-object types by
   count. Enumerate POH segment objects once (they are typically small in number).
3. The duplicate `SegmentReflectionCache` between `SegmentAnalyzer` and `LohFragmentationAnalyzer`
   should be **deduplicated** into a shared `SegmentReflectionHelper` utility class.

---

## 4. `LohFragmentationAnalyzer` *(modify)*

### Currently Produces
- `LohFragmentationDomainResult`: per-segment fragmentation %, free bytes, largest free block
- `LohSegmentSnapshot` list with address, total/used/free bytes, object/free-object counts
- Overall `FragmentationPercent`, `TotalLohBytes`, `TotalFreeBytes`

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Free-gap histogram (gap size distribution) | §10.2 | Medium |
| Long-lived object classification (Gen2 LOH objects) | §10.3 | High |
| Large objects sorted by size (top N LOH objects) | §10.3 | Medium |

### Required Changes
1. **Add `FreeGapHistogram`** — `IReadOnlyList<FreeGapBucket>` per segment, where each bucket is
   `(GapSizeRange, GapCount)`. Build during the existing per-segment object scan by collecting
   each contiguous free-block size and bucketing it. Zero extra heap scans.
2. **Add `TopLargeObjects`** — `IReadOnlyList<LargeObjectSnapshot>` (top 20 by size). Capture
   `Address`, `TypeName`, `Size` for non-free objects during the existing per-segment object walk.
   Cap at 20 entries — use a min-heap or partial sort pattern. `LargeObjectSnapshot` must be a
   new `internal sealed record`.
3. **Note**: Gen2 LOH cross-reference (§10.3) requires `GCGenerationAnalyzer` data. This is a
   **report-layer join**, not a change to `LohFragmentationAnalyzer` itself. The LOH analyzer
   does not need to re-scan generations.

---

## 5. `MemoryLeakAnalyzer` *(split + modify)*

### Currently Produces
- `MemoryLeakDomainResult`: finalizer queue count, duplicate string stats, highly-referenced objects
- Performs **two logically unrelated tasks** in one heap pass:
  - String deduplication analysis (§11)
  - Incoming-reference counting for highly-referenced objects (§6.1)

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| String data as standalone result | §11.1, §11.2 | High — report section mismatch |
| Unified leak candidate score | §6.1 | High |
| Leak classification label per candidate | §6.2 | High |
| Memory + performance impact estimate | §6.4 | Medium |

### Required Changes — **SPLIT**

#### ✂️ Extract `StringAnalyzer` (new analyzer)
Move all string-related logic and data from `MemoryLeakAnalyzer` into `StringAnalyzer`:
- `ProcessStringObjectByAddress` → `StringAnalyzer`
- `IsStringEntry` helper → `StringAnalyzer`
- `stringStats`, `stringMethodTables` dictionaries → `StringAnalyzer`
- New domain result `StringDomainResult`:
  ```
  StringDomainResult(
      int TotalStrings,
      ulong TotalStringMemoryBytes,
      int UniqueStrings,
      int DuplicatePatternCount,
      ulong DuplicateWastedBytes,
      double DuplicationRatio,
      IReadOnlyList<DuplicateStringSnapshot> TopDuplicates)
  ```
- `DuplicationRatio = (TotalStrings - UniqueStrings) / (double)TotalStrings`

#### Modify `MemoryLeakAnalyzer` (after split)
After string logic is removed, `MemoryLeakAnalyzer` retains:
- Finalizer queue analysis
- Highly-referenced object detection
- Add **`SuspicionScore`** to `HighlyReferencedObjectSnapshot` — integer 0–100 derived from:
  `IncomingReferences`, `Size`, whether the object is in Gen2
- Add **`LeakClassification`** enum value to `HighlyReferencedObjectSnapshot`:
  `Unknown | HighlyRetained | FinalizerBacked | ThreadRetained`
- Update `MemoryLeakDomainResult` to remove string fields (now in `StringDomainResult`)
- Add `IReadOnlyList<LeakCandidateSnapshot>` as top-level field — ranked union of finalizer
  candidates + highly-referenced candidates, sorted by `SuspicionScore` descending

---

## 6. `StaticRootLeakDetector` *(modify)*

### Currently Produces
- `StaticRootDomainResult`: root count, total retained bytes, top roots by retained bytes
- Walks static roots via `cache.GetOrBuildValidRoots(heap)` and BFS-limits each root

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Root type breakdown (field type that holds the root) | §5.1 | Medium |
| Leak classification label ("static retention") | §6.2 | Low — implied but not explicit |
| Confidence/cap signal (when BFS was cut short) | §17 | Medium |

### Required Changes
1. **Add `BfsCappedCount`** — number of roots where BFS hit `MaxRetainedObjectsToScan` budget.
   Already detectable; just not surfaced. Add to `StaticRootDomainResult`.
2. **Add `RetentionPatternHints`** — `IReadOnlyList<string>` — heuristic labels per significant
   root (e.g., `"Dictionary<K,V> cache"`, `"EventHandler chain"`) based on type name pattern
   matching. Pure string analysis — no extra heap scan.
3. **Consolidate** with `GCRootAnalyzer` long-term: static root detection should be one input
   stream to a unified root intelligence layer. `StaticRootLeakDetector` may eventually
   become an internal component of `GCRootAnalyzer`.

---

## 7. `GCHandleAnalyzer` *(modify)*

### Currently Produces
- `GCHandleDomainResult`: total handles by kind, pinned type counts, all-target type counts
- `StrongLikeHandles`, `WeakLikeHandles` summary counts

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Pinned object **retained bytes** estimate | §9.3 | High |
| Per-pinned-object size contribution | §9.3 | Medium |
| Dependent handle relationship (cross-references with `DependentHandleAnalyzer`) | §12 | Low |

### Required Changes
1. **Add retained-size estimation for pinned handles** — during `foreach ClrHandle`, when
   `handle.HandleKind == Pinned`, resolve `handle.Object` → `ClrObject` → accumulate
   `Size` into `pinnedRetainedBytes`. Add `PinnedRetainedBytes` field to
   `GCHandleDomainResult`.
2. **Add `TopPinnedObjectsBySize`** — `IReadOnlyList<NameBytesEntry>` — top pinned types by
   total pinned bytes. Already has `pinnedTypes` dictionary; extend to track bytes per type
   alongside count.
3. **Reuse `methodTableNameCache`** pattern already present — the existing
   `methodTableNameCache` dict is a good pattern; ensure it's applied to size accumulation
   too for the pinned path.

---

## 8. `ThreadAnalyzer` *(modify)*

### Currently Produces
- `ThreadDomainResult`: total/alive/background/GC/threadpool/finalizer thread counts
- Wait category distribution, state distribution, AppDomain distribution
- Threads with locks, blocked threads, threads with exceptions
- Frame hotspots, async chain thread count + max depth

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Long-lived thread classification | §7.1 | Low — single snapshot limitation |
| Thread memory retained (stack-rooted objects per thread) | §7.1 | High |
| Per-thread wait duration (not available from ClrMD static dump) | §7.2 | N/A — not possible |

### Required Changes
1. **Add `ThreadsWithLargeStackRoots`** — `IReadOnlyList<ThreadRootSnapshot>` — threads with
   many stack-rooted objects or high stack-root byte estimate. Use
   `ClrThread.EnumerateStackRoots()` for top N threads (bounded by count threshold).
   New record: `ThreadRootSnapshot(uint ThreadId, int StackRootCount, ulong EstimatedBytes)`.
2. **Add `LongLivedThreadCount`** heuristic — threads with `IsBackground = false` and
   `LockCount == 0` and no wait pattern detected — classified as "always-alive worker".
   This is a naming/classification addition to existing data; no new scan needed.
3. **`AsyncChainThreadCount`** and **`MaxAsyncChainDepth`** are already produced — ensure
   they are prominently exposed in the §8 report path (currently in ThreadAnalyzer, will
   also appear in `AsyncTaskAnalyzer` post-split).

---

## 9. `HangAnalyzer` *(split + modify)*

### Currently Produces
- `HangDomainResult`: waiting threads, threads holding locks, wait category breakdown
- Task data: `TotalTasks`, `PendingTasks`, `FaultedTasks`, `CanceledTasks`
- Continuation types, threadpool info, `HealthScore`

### Problem
`HangDomainResult` conflates two orthogonal concerns:
- **Hang/blocking** — waiting threads, lock holders, health score → §7
- **Async/Task state** — pending/faulted tasks, continuation types → §8

### Required Changes — **SPLIT**

#### ✂️ Extract `AsyncTaskAnalyzer` (new analyzer)
Responsibilities:
- Scan the heap for `Task`, `Task<T>`, `ValueTask`, `IValueTaskSource` instances
- Classify each by state: `Pending | Running | Faulted | Canceled | Completed | RanToCompletion`
- Detect **orphaned tasks** — tasks where the continuation pointer is null or resolved to
  a no-op continuation type (task was never awaited). Heuristic: `m_continuationObject` field
  is null or `typeof(SentinelContinuation)`.
- Build continuation chain depths by following `m_continuationObject` field links
  (BFS, depth-capped at 20)
- New domain result `AsyncTaskDomainResult`:
  ```
  AsyncTaskDomainResult(
      int TotalTasks,
      int PendingTasks,
      int RunningTasks,
      int FaultedTasks,
      int CanceledTasks,
      int CompletedTasks,
      int OrphanedTasks,
      int MaxContinuationDepth,
      double AvgContinuationDepth,
      bool TaskScanLimited,
      IReadOnlyList<NameCountEntry> TopPendingTaskTypes,
      IReadOnlyList<NameCountEntry> TopFaultedTaskTypes,
      IReadOnlyList<NameCountEntry> TopContinuationTypes,
      IReadOnlyList<OrphanedTaskSnapshot> TopOrphanedTasks)
  ```
- `OrphanedTaskSnapshot(ulong Address, string TaskType, string? ResultType, ulong Size)`

#### Modify `HangAnalyzer` (after split)
Remove all task scanning logic. `HangAnalyzer` retains:
- `WaitingThreads` analysis
- `ThreadsHoldingLocks`
- Async-over-sync detection
- `HealthScore` (now thread-blocking only)
- Updated `HangDomainResult` removes task fields (they move to `AsyncTaskDomainResult`)
- Deprecate `TotalTaskContinuations`, `QueuedWorkItems`, `TotalTasks`, `PendingTasks`,
  `FaultedTasks`, `CanceledTasks`, `TaskScanLimited` from `HangDomainResult`

---

## 10. `EventLeakAnalyzer` *(modify)*

### Currently Produces
- `EventLeakDomainResult`: total leaks, subscriber counts, static vs instance split
- `EventLeakGroupSnapshot`: publisher type, event name, severity, subscriber types
- Filters by `MinSubscribers` threshold — only scans leaking events

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Subscription graph for **non-leaking** events (§12.1) | §12.1 | Medium |
| Publisher object count (how many publisher instances exist) | §12.1 | Low |
| `DelegateHelper` usage for non-multicast delegate chains | §12.1 | Low |

### Required Changes
1. **Add `SubscriptionGraphMode` option** — `bool IncludeNonLeakingEvents` on `EventLeakOptions`.
   When enabled, scan all `MulticastDelegate` fields, not just those above `MinSubscribers`.
   This fills §12.1 "full subscription graph". Default off for performance.
2. **Add `TotalEventsScanned`** and `TotalPublisherInstances` to `EventLeakDomainResult` —
   gives context for the subscription graph section even without full mode.
3. **Add `EstimatedSubscriberRetainedBytes`** per `EventLeakGroupSnapshot` — multiply
   subscriber count by average subscriber type size from the heap index. Fills §12.2
   retention impact data.

---

## 11. `CollectionAnalyzer` *(modify)*

### Currently Produces
- `CollectionDomainResult`: counts by collection type, total wasted memory
- `WastefulCollectionSnapshot`: type, capacity, fill rate, wasted memory, element info

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Cache-pattern classification (Dictionary used as unbounded cache) | §4.3, §6.2 | Medium |
| GC generation of wasteful collections (Gen2 oversized = more concerning) | §6.2 | Medium |

### Required Changes
1. **Add `CachePatternScore`** to `WastefulCollectionSnapshot` — heuristic 0–10 scoring whether
   a collection looks like an unbounded cache: high capacity, high count, in Gen2, type name
   contains "Cache/Store/Registry/Pool". Pure field additions; no new scan.
2. **Add `Generation`** field to `WastefulCollectionSnapshot` — capture the generation of the
   collection object during the existing scan. Requires resolving `ClrHeap.GetGeneration(address)`
   for each wasteful collection found (small set, bounded cost).

---

## 12. `LockGraphAnalyzer` *(modify)*

### Currently Produces
- `LockGraphDomainResult`: held locks count, contested locks count, deadlock candidates count
- `TopContestedTypes` — types most frequently contested

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Deadlock cycle path (which threads form the cycle) | §7.3 | High |
| Lock wait duration estimate (not available in static dump) | §7.3 | N/A |
| Thread IDs involved in each deadlock candidate | §7.3 | High |

### Required Changes
1. **Add `DeadlockCandidateDetails`** — `IReadOnlyList<DeadlockCandidateSnapshot>` to
   `LockGraphDomainResult`. Currently `DeadlockCandidates.Count` is surfaced but the
   candidate list itself is not emitted. New record:
   ```
   DeadlockCandidateSnapshot(
       IReadOnlyList<uint> ThreadIds,
       IReadOnlyList<uint> OSThreadIds,
       IReadOnlyList<string> LockObjectTypes,
       string CycleSummary)
   ```
2. **Add `ContestedLockDetails`** — `IReadOnlyList<ContestedLockSnapshot>` — the top contested
   lock objects with address, type name, waiting thread IDs, owner thread ID. The internal
   `ContestedLocks` list already exists in `LockGraphAnalysis`; it just isn't mapped to the
   domain result.

---

## 13. `ThreadStackClusterAnalyzer` *(modify)*

### Currently Produces
*(Inspect via domain result)*
- Groups threads by stack signature hash
- Reports contention hotspot clusters

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Cluster-level memory retained estimate | §7.2 | Low |
| Integration with `LockGraphAnalyzer` (which cluster holds locks?) | §7.2 | Medium |

### Required Changes
1. **Add `LockHolderClusterCount`** — number of clusters where at least one thread holds a
   lock. Cross-reference with `ThreadAnalyzer.ThreadsWithLocks` addresses. No extra scan.
2. **Add `DominantWaitCategory`** per cluster — most common `WaitPattern` in the cluster.
   Derived from existing frame data; zero overhead addition.

---

## 14. `ReferenceChainAnalyzer` *(modify)*

### Currently Produces
- `ReferenceChainDomainResult`: samples reference chains from top N types to GC roots
- `MaxPathSearchObjects = 5000`, `DefaultMaxPathDepth = 25`
- Uses `BoundedRootPathFinder` internally

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Overlap with `GCRootAnalyzer` (once created) | §5.3 | Medium — deduplication concern |
| Confidence / cap signal in domain result | §17 | Medium |

### Required Changes
1. **Add `ChainSearchCapped`** flag and `CappedCount` to `ReferenceChainDomainResult` —
   how many of the sampled types had their path search capped. Already computable from
   `BoundedRootPathFinder` results; not currently surfaced.
2. **Post-`GCRootAnalyzer` creation**: `ReferenceChainAnalyzer` should shift focus from
   general top-N type sampling to **on-demand deep path tracing** for specific flagged
   objects (those identified by `DominatorAnalyzer` or `GCRootAnalyzer`). This makes it
   a depth tool rather than a breadth tool.

---

## 15. `CrashAnalyzer` *(modify)*

### Currently Produces
- `CrashDomainResult`: total/active exception counts, exception type distribution
- `TopExceptionInstances` with thread + stack context
- `TopCrashThreadCandidates`

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Exception frequency over time (needs multi-snapshot) | §13.1 | N/A — single snapshot |
| Exception-to-memory correlation (leaks caused by exceptions) | §13 | Medium |
| `InnerException` chain depth | §13.1 | Low |

### Required Changes
1. **Add `InnerExceptionChainDepth`** to `ExceptionInstanceSnapshot` — already follows
   `InnerExceptionType`; extend to chain depth (how deep the inner chain goes). Pure
   metadata read from `ClrException` — no heap enumeration.
2. **Add `ExceptionMemoryFootprint`** — total bytes held by all exception objects of each type.
   Derive from `ExceptionTypeCounts` keys → look up in heap index for size. Adds
   §13 correlation with §3 type table.
3. **Rename / re-scope**: `CrashAnalyzer` is named for crash scenarios but §13 is simply
   "Exception Analysis". The analyzer name should reflect that it covers all exceptions,
   not just crash-state ones. Consider renaming to `ExceptionAnalyzer` in a future pass.

---

## 16. `ModuleAnalyzer` *(modify)*

### Currently Produces
- `ModuleDomainResult`: module counts, dynamic module count, version conflict groups
- `TopModulesBySize`, `ModuleHeapStats`, `HeavyTypeDensityModules`

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| Module → retained memory (not just type count) | Utility for §3.1 | Low |
| AOT / R2R detection flag | Future | Low |

### Required Changes
1. **Add `TotalRetainedEstimateBytes`** to `ModuleHeapStats` — set to `0` initially; populated
   by `DominatorAnalyzer` in a post-pass (same pattern as `TypeSnapshot.EstimatedRetainedBytes`).
2. This analyzer is otherwise well-scoped; no major structural changes needed.

---

## 17. `DependentHandleAnalyzer` *(modify)*

### Currently Produces
- `DependentHandleDomainResult`: edge counts, source/target type distributions
- Source-target pair type counts

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| ConditionalWeakTable size contribution | §12 | Low |
| Integration with §12.1 subscription graph | §12 | Low |

### Required Changes
1. **Add `EstimatedRetainedBytes`** — sum of target object sizes for all resolved edges.
   The target address is already resolved; adding a size lookup is minimal cost.
2. **Add `IsPotentialEventSource`** flag — heuristic: if source type name ends in
   `"EventSource"` / `"Observable"` / `"Subject"` or target type name contains `"Handler"`.
   Links dependent handle analysis to §12.

---

## 18. `TrendAnalyzer` *(modify)*

### Currently Produces
- `AnalyzerTrendResult`: per-analyzer metric deltas across two snapshots
- `ExtractTimeline`: per-metric values across N snapshots
- `MetricTrendDirection`: `Stable | Increasing | Decreasing | Volatile`

### What Is Missing
| Gap | Report Section | Priority |
|-----|---------------|----------|
| **Regression detection** — semantic "new leak" label | §14.2 | High |
| Severity classification of trend changes | §14.2 | Medium |
| Growth rate (% change per delta, not just absolute) | §14.1 | Medium |

### Required Changes
1. **Add `GrowthRatePercent`** to each `MetricDelta` — `(current - baseline) / baseline * 100`.
   Pure arithmetic on existing values.
2. **Add `RegressionSeverity`** enum — `None | Minor | Moderate | Severe` — applied when a
   metric crosses a threshold in the wrong direction. Thresholds configurable.
3. **Add `NewLeakSignals`** — `IReadOnlyList<NewLeakSignal>` on `AnalyzerTrendResult` — a type
   that appears in `current` leak results but was absent or negligible in `baseline`.
   Requires that `MemoryLeakDomainResult` and `StaticRootDomainResult` expose type-level
   data comparable across snapshots (they partially do via `TopRootsByRetainedBytes`).

---

---

# Part 3 — New Analyzers Required

---

## N1. `GCRootAnalyzer` *(new — HIGH PRIORITY)*

### Report Sections Served
§5.1 Root Distribution, §5.2 Root Severity Ranking, §5.3 Root Paths

### Rationale
`BoundedRootPathFinder` and `StaticRootLeakDetector` exist as utilities and partial detectors.
No pipeline analyzer produces a **unified root intelligence result** covering all root kinds
(Static, Stack, GCHandle, Finalizer) with retention estimates and severity ranking.

### Design

**Domain Result**:
```
GCRootDomainResult(
    int TotalRoots,
    IReadOnlyList<RootKindSummary> ByKind,
    IReadOnlyList<RootFinding> TopRootsBySeverity,
    IReadOnlyList<RootPathFinding> RootPaths,
    bool PathSearchCapped,
    int PathSearchCappedCount)

RootKindSummary(string Kind, int Count, ulong EstimatedRetainedBytes, double PctOfManagedHeap)

RootFinding(
    string RootKind,
    ulong RootAddress,
    string? FieldDescription,
    string TargetTypeName,
    ulong TargetAddress,
    ulong EstimatedRetainedBytes,
    int SeverityScore)

RootPathFinding(
    ulong TargetAddress,
    string TargetTypeName,
    string RootKind,
    IReadOnlyList<string> PathTypeNames,
    int PathLength,
    bool WasCapped)
```

**Implementation Strategy**:
- Use `cache.GetOrBuildValidRoots(heap)` — **do not re-enumerate roots from scratch**
- Group roots by kind using a single pass
- For top N roots by estimated retained bytes (N = 25, configurable), run
  `BoundedRootPathFinder` with tight budget (`MaxNodes = 500`, `MaxDepth = 20`)
- Retained bytes per root = BFS-discovered object count × average type size (from heap index)
- Do **not** build a full reverse graph; scope entirely to sampled roots
- Severity score = `f(RetainedBytes, RootKind, IsStatic)`

---

## N2. `DominatorAnalyzer` *(new — HIGH PRIORITY)*

### Report Sections Served
§3.1 Estimated Retained Size, §3.2 Dominator Candidates, §4.1 Retention Hotspots,
§4.2 Dominator Tree (Approx), §4.3 Retention Patterns

### Rationale
This is the **largest capability gap** in the system. Retained size and dominator tree are
foundational to professional-tier memory analysis and no current analyzer provides them.

### Design

**Domain Result**:
```
DominatorDomainResult(
    int CandidatesAnalyzed,
    ulong TotalRetainedBytesEstimated,
    IReadOnlyList<DominatorCandidate> TopDominatorsByRetainedSize,
    IReadOnlyList<RetentionPatternFinding> DetectedPatterns,
    bool WasBudgetCapped,
    string CapReason)

DominatorCandidate(
    ulong Address,
    string TypeName,
    ulong ShallowSize,
    ulong EstimatedRetainedBytes,
    int RetainedObjectCount,
    RetentionPatternHint PatternHint)

RetentionPatternHint : None | StaticCache | EventChain | ThreadLocal | Singleton | Collection

RetentionPatternFinding(
    RetentionPatternHint Pattern,
    int InstanceCount,
    ulong TotalRetainedBytes,
    string Description)
```

**Implementation Strategy**:
- **Input**: Top 50 types by shallow size from `HeapAnalysisCache` type statistics
- **Per candidate**: Sample 1 representative object of each type; run bounded reverse-BFS
  using `ReverseReferenceIndex` (scoped, not full graph) to estimate retained sub-graph size
- **Budget**: `MaxNodes = 2000` per candidate, `MaxEdges = 5000` total across all candidates
- **Pattern detection**: After BFS, classify by field types in the retained set:
  - `Dictionary/ConcurrentDictionary` → `StaticCache`
  - `EventHandler/MulticastDelegate` → `EventChain`
  - `[ThreadStatic]` or `ThreadLocal<T>` → `ThreadLocal`
- **Post-pass**: Write `EstimatedRetainedBytes` back into a shared result that
  `MemoryAnalyzer` and `ModuleAnalyzer` can expose via their type snapshots

> ⚠️ **Performance constraint**: This analyzer MUST run after `MemoryAnalyzer` and use its
> type statistics to bound the candidate set. It must **never** enumerate the full heap.
> The reverse-reference index must be scoped to candidate addresses only.

---

## N3. `AsyncTaskAnalyzer` *(split from `HangAnalyzer` — MEDIUM PRIORITY)*

### Report Sections Served
§8.1 Task Summary, §8.2 Orphaned Tasks, §8.3 Continuation Chains

### Rationale
Task lifecycle analysis is a first-class §8 report section. It is currently buried inside
`HangDomainResult` alongside thread-blocking data, with no orphan task detection.

### Design

**Domain Result**: see §9 `HangAnalyzer` split section above.

**Implementation Strategy**:
- Scan the heap for objects whose `MethodTable` resolves to `Task`, `Task<T>`,
  `ValueTask`, `IValueTaskSource` (use heap index MT → type name cache for O(1) lookup)
- For each task object, read `m_stateFlags` field to determine state
- For orphan detection: read `m_continuationObject` field — if null or
  `System.Threading.Tasks.Task+<>c` (no-op), classify as orphaned
- For chain depth: BFS following `m_continuationObject` links, depth-capped at 20
- **Bounded by** `MaxTasksToScan` (carry over from `HangAnalyzer`'s existing constant)
- Uses heap index (not raw `heap.EnumerateObjects()`) for the initial type-filtered scan

---

## N4. `AllocationPatternAnalyzer` *(new — MEDIUM PRIORITY)*

### Report Sections Served
§2.3 Allocation Patterns, §9.1 Short/Long-lived Objects, §9.2 GC Efficiency

### Rationale
No current analyzer classifies allocation behavior or GC efficiency. Both §2.3 and §9 require
heuristic classification derived from generation distribution data that `GCGenerationAnalyzer`
already produces — this is a **pure post-processing analyzer** that requires no heap scan.

### Design

**Domain Result**:
```
AllocationPatternDomainResult(
    double Gen0Pct,
    double Gen2Pct,
    double LohPct,
    AllocationProfile Profile,
    GCPressureLevel GCPressure,
    double PromotionPressureScore,
    IReadOnlyList<TypeAllocationProfile> TopShortLivedTypes,
    IReadOnlyList<TypeAllocationProfile> TopLongLivedTypes)

AllocationProfile : Transient | Steady | Retained | Mixed

GCPressureLevel : Low | Moderate | High | Critical

TypeAllocationProfile(
    string TypeName,
    int Gen0Count, int Gen1Count, int Gen2Count,
    double LongLivedRatio,
    AllocationProfile Profile)
```

**Implementation Strategy**:
- **Input**: `GCGenerationDomainResult` + `PerTypeGenerationProfile` (added to
  `GCGenerationAnalyzer` per change §2 above)
- No heap scan. Pure derived computation from already-produced results.
- `Gen0Pct > 70%` → `Transient`; `Gen2Pct > 50%` → `Retained`; mixed otherwise
- `GCPressureLevel` from `(Gen0Pct × 0.3) + (Gen2Pct × 0.5) + (LohPct × 0.2)` normalized score
- **Must run after** `GCGenerationAnalyzer` in pipeline order (`Order` property)

---

## N5. `ObjectShapeAnalyzer` *(new — LOW PRIORITY)*

### Report Sections Served
§3.3 Object Shape Analysis

### Rationale
Type structure (reference-heavy vs value-heavy) affects GC scan cost and memory layout.
This is purely a `ClrType` metadata analysis — no heap object enumeration required.

### Design

**Domain Result**:
```
ObjectShapeAnalyzerDomainResult(
    IReadOnlyList<TypeShapeProfile> TopReferenceHeavyTypes,
    IReadOnlyList<TypeShapeProfile> TopValueHeavyTypes,
    int TotalTypesAnalyzed,
    double AvgRefFieldsPerType)

TypeShapeProfile(
    string TypeName,
    int TotalFields,
    int ReferenceFields,
    int ValueFields,
    double ReferenceFieldRatio,
    ulong InstanceCount,
    ObjectShapeCategory Category)

ObjectShapeCategory : ReferenceHeavy | ValueHeavy | Balanced | Scalar
```

**Implementation Strategy**:
- Enumerate `ClrType` entries from the heap index (already cached — iterate
  `cache.GetOrBuildTypeStatistics(heap).Keys` and resolve each to `ClrType` via
  `heap.GetTypeByMethodTable`)
- For each type, inspect `ClrType.Fields` — count reference vs value fields
- Skip array types and primitive types
- **No per-object scan** — purely type metadata. Very fast.
- Cap at top 200 types by instance count (from heap index) to bound work

---

## N6. `StringAnalyzer` *(split from `MemoryLeakAnalyzer` — MEDIUM PRIORITY)*

### Report Sections Served
§11.1 Duplicate Strings, §11.2 Memory Waste

### Rationale
String analysis is a standalone §11 report section. Its data is currently embedded in
`MemoryLeakDomainResult`, making it inaccessible to §11 report renderers without coupling
them to leak analysis.

### Design

**Domain Result**:
```
StringDomainResult(
    int TotalStrings,
    ulong TotalStringMemoryBytes,
    int UniqueStrings,
    int DuplicatePatternCount,
    ulong DuplicateWastedBytes,
    double DuplicationRatio,
    double PctOfManagedHeap,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByWaste,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByCount)
```

**Implementation Strategy**:
- Lift `ProcessStringObjectByAddress`, `IsStringEntry`, `stringStats`, `stringMethodTables`
  verbatim from `MemoryLeakAnalyzer`
- Uses heap index fast path (`concreteCache.EnumerateIndexedEntriesAsTuples()`) already
  present in `MemoryLeakAnalyzer` — carry it forward exactly
- `DuplicationRatio = (TotalStrings - UniqueStrings) / (double)TotalStrings`
- `PctOfManagedHeap` = `TotalStringMemoryBytes / totalManagedBytes * 100` (use
  `MemoryDomainResult` as input or re-read from cache)
- Add `TopDuplicatesByCount` sorted by `Count` in addition to `TopDuplicatesByWaste`
  sorted by `WastedBytes`

---

---

# Part 4 — `InsightEngine` Enhancements

The `InsightEngine` is the consumer of all analyzer outputs for cross-cutting findings.
As new analyzers are added, the `InsightEngine.Analyze` method must be extended to:

| New Input | New Detections |
|-----------|---------------|
| `GCRootDomainResult` | Large static roots, GCHandle pressure, root distribution anomalies |
| `DominatorDomainResult` | Retention hotspot alerts, cache chain warnings |
| `AsyncTaskDomainResult` | Orphaned task warnings, async-over-sync correlation |
| `AllocationPatternDomainResult` | GC pressure level escalation, transient allocation flood |
| `StringDomainResult` | High duplication ratio alert (> 50% duplication) |

Additionally, `InsightEngine` must gain:
- **`ExecutiveSummary` output**: a `SummaryBlock` record with:
  `TotalManagedBytes, LeakLikelihoodScore (0-100), GCPressureScore (0-100),
  ThreadContentionScore (0-100), TopRecommendations (top 3 InsightFindings)`
- **`ConfidenceSummary` output**: an `IReadOnlyList<AnalyzerConfidenceNote>` — one per analyzer
  that capped its search, flagged limited data, or ran in degraded mode

---

---

# Part 5 — Implementation Priority Order

| Priority | Item | Type | Effort |
|----------|------|------|--------|
| 1 | `StringAnalyzer` split from `MemoryLeakAnalyzer` | ✂️ Split | Low |
| 2 | `AsyncTaskAnalyzer` split from `HangAnalyzer` | ✂️ Split | Medium |
| 3 | `MemoryAnalyzer` — add `AverageSize`, `SizeBucketHistogram` | Modify | Low |
| 4 | `GCGenerationAnalyzer` — add `PerTypeGenerationProfile` | Modify | Medium |
| 5 | `GCHandleAnalyzer` — add `PinnedRetainedBytes` | Modify | Low |
| 6 | `LohFragmentationAnalyzer` — add `TopLargeObjects`, `FreeGapHistogram` | Modify | Low |
| 7 | `LockGraphAnalyzer` — add `DeadlockCandidateDetails` | Modify | Low |
| 8 | `AllocationPatternAnalyzer` (new, zero heap scan) | ➕ New | Low |
| 9 | `ObjectShapeAnalyzer` (new, type metadata only) | ➕ New | Low |
| 10 | `GCRootAnalyzer` (new, uses existing `BoundedRootPathFinder`) | ➕ New | High |
| 11 | `DominatorAnalyzer` (new, bounded reverse-BFS) | ➕ New | Very High |
| 12 | `InsightEngine` — `ExecutiveSummary` + `ConfidenceSummary` + new inputs | Modify | Medium |
| 13 | `EventLeakAnalyzer` — subscription graph mode | Modify | Medium |
| 14 | `TrendAnalyzer` — regression severity + growth rate % | Modify | Low |

> **`DominatorAnalyzer`** is the highest-effort item and carries the most performance risk.
> It must be implemented last, after all other analyzers are in place, with dedicated
> performance testing on 10GB+ dumps before merging. All other items are safe to implement
> incrementally in the order listed.
