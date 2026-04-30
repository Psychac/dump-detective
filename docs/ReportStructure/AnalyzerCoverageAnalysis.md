****# Analyzer Coverage Analysis
## Professional Tier Report — Section-to-Analyzer Mapping & Capability Gap Analysis

> **Scope**: This document maps every section of `ProfessionalTierReport.md` to the existing
> analyzer set, then provides a per-analyzer deep-dive covering what each one currently produces,
> what is missing, and exactly what must be changed, added, or split to achieve full report coverage.

---

> **Implementation Status** — Phase 1 infrastructure complete.
> All satellite index files, in-memory caches, and supporting helpers are implemented.
> Analyzer-level work (Phase 2) is next — see [Part 5](#part-5--implementation-priority-order)
> for the ordered work list and [Part 6](#part-6--phase-1-implementation-log) for the change log.

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
| 🔵 | Phase 1 infrastructure implemented; Phase 2 analyzer work pending |

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
| Object size distribution (histogram) | ✅ | `MemoryAnalyzer` — `SizeBucketHistogram` (8-bucket `SizeBucketEntry` list) |

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
| Average size per type | ✅ | `MemoryAnalyzer` — `TypeSnapshot.AverageSize` (populated in `ToSnapshot`) |
| **Estimated retained size** per type | ❌ | ➕ `DominatorAnalyzer` |
| GC generation distribution per type (Gen0/1/2/LOH %) | ✅ | `GCGenerationAnalyzer` — `PerTypeGenerationProfiles` (top 20 by count, built from Phase-1 `TypeAggregates`) |
| `IsFinalizable` flag | ❌ | ➕ `ObjectShapeAnalyzer` — `ClrType.IsFinalizable` |
| `IsValueType` flag | ❌ | ➕ `ObjectShapeAnalyzer` — `ClrType.IsValueType` |
| `IsArray` flag + component type | ❌ | ➕ `ArrayAnalyzer` — `ClrType.IsArray`, `ClrType.ComponentType` |
| Base type chain depth | ❌ | ➕ `ObjectShapeAnalyzer` — `ClrType.BaseType` traversal |
| Interface count | ❌ | ➕ `ObjectShapeAnalyzer` — `ClrType.Interfaces.Count` |
| Field count (ref / value) | ❌ | ➕ `ObjectShapeAnalyzer` — `ClrType.Fields` |
| Module / owning assembly | ✅ | `ModuleAnalyzer` — `ModuleHeapStats` links types to modules |
| Method table address | 🟡 | Available from heap index key; not surfaced in report output |

### 3.2 Dominator Candidates

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| High-retention objects (objects retaining large sub-graphs) | ❌ | ➕ `DominatorAnalyzer` |
| Nomination criteria (size > 1%, Gen2 > 80%, known containers) | ❌ | ➕ `DominatorAnalyzer` |
| Largest single instance per candidate type | ❌ | ➕ `DominatorAnalyzer` — `ClrObject.Size` max |
| GC root reachability per candidate | ❌ | ➕ `GCRootAnalyzer` cross-reference |

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
| Pending / Faulted / Canceled / Completed task counts | ✅ | `AsyncTaskAnalyzer` — `AsyncTaskDomainResult` with full state breakdown |
| Task state distribution as first-class result | ✅ | `AsyncTaskAnalyzer` — standalone result, no longer buried in `HangDomainResult` |

### 8.2 Orphaned Tasks

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Tasks with no continuation (never awaited) | ✅ | `AsyncTaskAnalyzer` — `OrphanedTasks` count + `TopOrphanedTasks` snapshots |

### 8.3 Continuation Chains

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Top continuation types | ✅ | `AsyncTaskAnalyzer` — `TopContinuationTypes` |
| Async execution depth (chain length) | ✅ | `AsyncTaskAnalyzer` — `MaxContinuationDepth`, `AvgContinuationDepth` |
| Continuation chain as structured path | ✅ | `AsyncTaskAnalyzer` — BFS depth-20 per task, top orphaned snapshots |

**Verdict**: §8 is now fully covered by `AsyncTaskAnalyzer` (split from `HangAnalyzer`).

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
| Pinned object types and sizes | ✅ | `GCHandleAnalyzer` — `TopPinnedObjectsBySize` with retained bytes per type |
| GC blocking impact score | 🟡 | `GCHandleAnalyzer` — `PinnedRetainedBytes` now computed; no formal GC-pressure score yet |

---

## §10 · LOH / POH Diagnostics

### 10.1 LOH Summary

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| LOH total size + segment count | ✅ | `LohFragmentationAnalyzer` + `GCGenerationAnalyzer` |
| LOH object type distribution | ✅ | `GCGenerationAnalyzer` — `TopLohTypes` (top 15 by LOH bytes; non-nullable) |
| POH summary | 🟡 | `SegmentAnalyzer` — POH bytes; no per-object POH breakdown |

### 10.2 Fragmentation

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Per-segment fragmentation % | ✅ | `LohFragmentationAnalyzer` — `LohSegmentSnapshot.FragmentationPercent` |
| Largest free block per segment | ✅ | `LohFragmentationAnalyzer` — `LargestFreeBlock` |
| Free gap distribution | ✅ | `LohFragmentationAnalyzer` — `FreeGapHistogram` bucketed by gap size range |

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
| Duplicate string count + top patterns | ✅ | `StringAnalyzer` — `DuplicatePatternCount`, `TopDuplicatesByWaste`, `TopDuplicatesByCount` |
| Total string object count + total bytes | ✅ | `StringAnalyzer` — `TotalStrings`, `TotalStringMemoryBytes` |
| Unique string count + deduplication ratio | ✅ | `StringAnalyzer` — `UniqueStrings`, `DuplicationRatio` |
| String length histogram | ❌ | ➕ `StringAnalyzer` — bucketed distribution by char length |
| Very long strings (> 85 KB, LOH residents) | ✅ | `StringAnalyzer` — `VeryLongStrings` list with address + char length + size |
| Interned strings in FOH | ✅ | `StringAnalyzer` — `InternedStringCount`, `InternedStringBytes` (FOH segment scan) |
| Strings surviving to Gen2 | ✅ | `StringAnalyzer` — `Gen2StringCount`, `Gen2StringBytes` (derived from `TypeAggregates`) |

### 11.2 Memory Waste & Optimisation Potential

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Wasted bytes from duplicate strings | ✅ | `StringAnalyzer` — `DuplicateWastedBytes` |
| Total string memory + unique ratio | ✅ | `StringAnalyzer` — `TotalStringMemoryBytes`, `UniqueStrings`, `DuplicationRatio` |
| LOH string pressure (total size > 85 KB) | ✅ | `StringAnalyzer` — `LohStringBytes` |
| Encoding waste (ASCII content in UTF-16) | ❌ | ➕ `StringAnalyzer` — heuristic byte-range scan |
| Per-finding recommended approach (intern / pool / slice) | ❌ | Report-layer rule engine |

**Verdict**: §11 is now substantially covered by `StringAnalyzer`. String data has been split out of `MemoryLeakDomainResult` into a standalone `StringDomainResult`. Remaining gaps are the string-length histogram and ASCII-in-UTF-16 encoding waste heuristic.

---

## §12 · Event & Delegate Analysis

### 12.1 Subscription Graph

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Publisher → subscriber mapping | 🟡 | `EventLeakAnalyzer` — `TopLeakGroups` with `PublisherType.EventFieldName` → subscriber types |
| Full subscription graph (all events, not just leaks) | ❌ | `EventLeakAnalyzer` only scans delegates with `MinSubscribers` threshold |
| `_target` field inspection per delegate | 🟡 | `EventLeakAnalyzer` reads target indirectly; not explicitly surfaced |
| `_invocationList` depth / nested multicast | ❌ | ➕ `EventLeakAnalyzer` (subscription graph mode) |
| Total publisher instances per event type | ❌ | ➕ `EventLeakAnalyzer` — `TotalPublisherInstances` (requires §10 change) |
| Subscriber shallow size sum per event | ❌ | ➕ `EventLeakAnalyzer` — `EstimatedSubscriberRetainedBytes` (requires §10 change) |

### 12.2 Event Leaks

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Retained subscriber count per event | ✅ | `EventLeakAnalyzer` — `TotalSubscribers`, `EventLeakGroupSnapshot` |
| Static vs instance publisher breakdown | ✅ | `EventLeakAnalyzer` — `IsStatic`, `StaticLeaks`, `InstanceLeaks` |
| Severity score per leak group | ✅ | `EventLeakAnalyzer` — `SeverityScore` |
| Publisher lifetime (Gen0/1 vs Gen2/static) | ❌ | ➕ `EventLeakAnalyzer` — generation lookup per publisher address |
| `EventHandler<T>` vs `Action` vs custom delegate classification | ❌ | ➕ `EventLeakAnalyzer` — type name classification |

---

## §13 · Exception Analysis

### 13.1 Exception Frequency

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Most common exception types | ✅ | `CrashAnalyzer` — `ExceptionTypeCounts` |
| Active vs total exception count | ✅ | `CrashAnalyzer` — `ActiveExceptions`, `TotalExceptions` |
| Exception memory footprint | ❌ | ➕ `CrashAnalyzer` — `ExceptionMemoryFootprint` per type (heap index lookup) |
| `InnerException` chain depth histogram | ❌ | ➕ `CrashAnalyzer` — `InnerExceptionChainDepth` on `ExceptionInstanceSnapshot` |

### 13.2 Failure Hotspots

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Thread-to-exception mapping | ✅ | `CrashAnalyzer` — `TopExceptionInstances` with `ThreadId`, `CurrentThreadFrames` |
| Stack frame hotspots for exceptions | ✅ | `CrashAnalyzer` — `OriginalStackTrace` per instance |
| Exception-specific frame hotspot aggregation (top N frames across exception threads) | ❌ | ➕ `CrashAnalyzer` — reuse `TopFrameHotspots` pattern, scoped to exception threads |
| Exception origin classification (UserCode / Framework / ThirdParty) | ❌ | ➕ `CrashAnalyzer` — `ClrModule` name prefix matching via `ModuleAnalyzer` module list |

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
- Object size histogram (§2.1) — ✅ `MemoryAnalyzer` `SizeBucketHistogram`
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

## §18 · AppDomain & Assembly Analysis

### 18.1 AppDomain Inventory

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Domain name, address, numeric ID | ❌ | ➕ `AppDomainAnalyzer` — `ClrRuntime.AppDomains` |
| Module list per domain | 🟡 | `ModuleAnalyzer` — lists modules globally, not per `AppDomain` |
| Managed memory attributable per domain | ❌ | ➕ `AppDomainAnalyzer` — cross-reference `ClrType.Module.AppDomain` with heap index |
| Multi-domain type collision detection | ❌ | ➕ `AppDomainAnalyzer` |

### 18.2 Assembly Version Conflicts

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Same name, different path / `MetadataToken` | ✅ | `ModuleAnalyzer` — `VersionConflictGroups`, `ConflictDetails` |
| Dynamic assembly count and size | ✅ | `ModuleAnalyzer` — `DynamicModules` count |
| Anonymous hosted modules (no file path) | ❌ | ➕ `AppDomainAnalyzer` — `ClrModule.FileName == null` detection |

### 18.3 Type Density per Module

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Type count per module from `EnumerateTypes()` | ❌ | ➕ `AppDomainAnalyzer` — `ClrModule.EnumerateTypes()` |
| Heap footprint per module | ✅ | `ModuleAnalyzer` — `ModuleHeapStats.TotalBytes` |
| Type-to-object ratio (load overhead vs live usage) | 🟡 | `ModuleAnalyzer` — `ModuleTypeDensity` covers partially; no `EnumerateTypes()` count |
| Modules with > 5 000 types (source gen / reflection heavy) | ❌ | ➕ `AppDomainAnalyzer` |

**Verdict**: §18.2 is largely covered by `ModuleAnalyzer`. §18.1 (per-domain breakdown) and §18.3 (type count from `EnumerateTypes()`) require `AppDomainAnalyzer`.

---

## §19 · JIT & Native Code Footprint

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total JIT code heap size | ❌ | ➕ `JitAnalyzer` — `ClrRuntime.GetJitManagers()` |
| JIT heap as % of total process memory | ❌ | ➕ `JitAnalyzer` |
| Active method hotspot map (methods on most thread stacks) | 🟡 | `ThreadAnalyzer` — `TopFrameHotspots`; no `ClrMethod.NativeCode` size or `Signature` |
| Native code range size per method (`HotColdInfo`) | ❌ | ➕ `JitAnalyzer` — `ClrMethod.HotColdInfo` |
| Unmanaged frame ratio per thread | ❌ | ➕ `JitAnalyzer` — `ClrStackFrame.Kind` distribution |
| Tiered compilation detection (Tier0 → Tier1) | ❌ | ➕ `JitAnalyzer` — same `MetadataToken`, two `NativeCode` addresses |
| ReadyToRun pre-compiled methods | ❌ | ➕ `JitAnalyzer` — `ClrModule.IsPEFile` + R2R header |

**Verdict**: §19 is entirely uncovered. Requires new `JitAnalyzer`.

---

## §20 · Boxing & Value Type Pressure

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Boxed value type object count and total size | ❌ | ➕ `BoxingAnalyzer` — `ClrObject.AsBoxedValue()` |
| Top value types most frequently boxed | ❌ | ➕ `BoxingAnalyzer` |
| Boxed enum detection | ❌ | ➕ `BoxingAnalyzer` — `ClrType.IsEnum` on inner type |
| Boxed structs in `object[]` / `IEnumerable<object>` collections | ❌ | ➕ `BoxingAnalyzer` |
| Struct field padding waste | ❌ | ➕ `BoxingAnalyzer` — `ClrInstanceField.Offset` gap analysis |
| Large structs frequently on stack | ❌ | ➕ `BoxingAnalyzer` — `ClrThread.EnumerateStackObjects()` |
| Oversized value types by `ClrType.StaticSize` | ❌ | ➕ `ObjectShapeAnalyzer` or `BoxingAnalyzer` |

**Verdict**: §20 is entirely uncovered. Requires new `BoxingAnalyzer`.

---

## §21 · Finalizable Object Lifecycle

### 21.1 Finalizable Object Population

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| All `IsFinalizable` objects across heap (not just queue) | ❌ | ➕ `FinalizableObjectAnalyzer` — `ClrType.IsFinalizable` flag on all heap objects |
| Finalizable objects by generation | ❌ | ➕ `FinalizableObjectAnalyzer` — generation correlation |
| Top finalizable types by Gen2 count and size | ❌ | ➕ `FinalizableObjectAnalyzer` |
| `IsFinalizable` + `IDisposable` + undisposed detection | ❌ | ➕ `FinalizableObjectAnalyzer` — `_disposed` field heuristic |

### 21.2 Finalizer Queue Analysis

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Finalizer queue depth (total count) | ✅ | `MemoryLeakAnalyzer` — `FinalizerQueueCount` |
| Top types in finalizer queue | ✅ | `MemoryLeakAnalyzer` — `TopFinalizerTypes` |
| Queue objects retaining large sub-graphs | ❌ | ➕ `FinalizableObjectAnalyzer` — bounded BFS from finalizer root |
| Resurrection detection (`GC.ReRegisterForFinalize`) | ❌ | ➕ `FinalizableObjectAnalyzer` — heuristic pattern |

### 21.3 Finalizer Thread Health

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Finalizer thread alive / OS thread ID | ✅ | `ThreadAnalyzer` — `FinalizerManagedThreadId`, `FinalizerOsThreadId` |
| Finalizer thread blocked | ✅ | `ThreadAnalyzer` — `FinalizerIsBlocked`, `FinalizerLockCount` |
| Blocking frame on finalizer thread | ✅ | `ThreadAnalyzer` — `FinalizerFrames` |

**Verdict**: §21.3 is fully covered. §21.1 (population beyond queue) and §21.2 sub-graph retention require new `FinalizableObjectAnalyzer`.

---

## §22 · Array Deep Analysis

### 22.1 Array Population Overview

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total array object count and size | 🟡 | `MemoryAnalyzer` — arrays appear in type table; no array-specific aggregation |
| By element type (`ClrType.ComponentType.Name`) | ❌ | ➕ `ArrayAnalyzer` |
| By rank (`ClrObject.AsArray().Rank`) | ❌ | ➕ `ArrayAnalyzer` |
| By generation (Gen0/1/2/LOH) | ❌ | ➕ `ArrayAnalyzer` — generation correlation |

### 22.2 Large Array Analysis

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Top 10 largest individual LOH arrays | 🟡 | `LohFragmentationAnalyzer` — `TopLargeObjects` (top 20 by size, from Phase 1 index); no element type detail |
| `byte[]` > 1 MB (pooling candidates) | ❌ | ➕ `ArrayAnalyzer` |
| Multi-dimensional arrays > 85 KB | ❌ | ➕ `ArrayAnalyzer` |

### 22.3 Sparse & Wasteful Arrays

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Null density in reference-type arrays | ❌ | ➕ `ArrayAnalyzer` — bounded element sampling |
| Zero density in value-type arrays | ❌ | ➕ `ArrayAnalyzer` |
| Backing arrays of over-capacity collections | 🟡 | `CollectionAnalyzer` — fill rate exists; no null-element sampling |

### 22.4 Jagged vs Multi-Dimensional

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Jagged array count and inner-array distribution | ❌ | ➕ `ArrayAnalyzer` |
| Multi-dim (`T[,]`, `T[,,]`) flag and recommendation | ❌ | ➕ `ArrayAnalyzer` — `ClrArray.Rank > 1` |

**Verdict**: §22 requires new `ArrayAnalyzer`. `LohFragmentationAnalyzer` partially covers large objects post-change §6.

---

## §23 · Async State Machine Objects

### 23.1 State Machine Population

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total state machine object count and size | ❌ | ➕ `AsyncStateMachineAnalyzer` — `IAsyncStateMachine` interface detection |
| Top 20 state machine types by count and size | ❌ | ➕ `AsyncStateMachineAnalyzer` |
| `<>1__state` field distribution (suspended at which await) | ❌ | ➕ `AsyncStateMachineAnalyzer` — `ClrType.Fields` field name pattern |

### 23.2 Captured Closure Analysis

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Reference fields on state machine types (captured locals) | ❌ | ➕ `AsyncStateMachineAnalyzer` — `ClrType.Fields` |
| State machines with total captured ref size > 1 MB | ❌ | ➕ `AsyncStateMachineAnalyzer` |
| Common problematic captures (`HttpClient`, `DbContext`, `Stream`) | ❌ | ➕ `AsyncStateMachineAnalyzer` — type name pattern match |

### 23.3 Suspended Method Map

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Originating method name per state machine type | ❌ | ➕ `AsyncStateMachineAnalyzer` — decode `<MethodName>d__N` pattern |
| Suspended methods grouped by declaring type | ❌ | ➕ `AsyncStateMachineAnalyzer` |
| Cross-reference with faulted `Task` objects | ❌ | ➕ `AsyncStateMachineAnalyzer` + `AsyncTaskAnalyzer` |

**Verdict**: §23 is entirely uncovered. Requires new `AsyncStateMachineAnalyzer`.

---

## §24 · Weak Reference & ConditionalWeakTable Analysis

### 24.1 Weak GC Handle Population

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Weak GC handle count (Weak / WeakLong / SizedRef) | 🟡 | `GCHandleAnalyzer` — `WeakLikeHandles` total count; no per-kind breakdown |
| Alive vs collected target analysis | ❌ | ➕ `WeakReferenceAnalyzer` — `ClrHeap.GetObject(address).IsValid` per handle |
| Top target types by weak handle count | 🟡 | `GCHandleAnalyzer` — `TopTargetTypes` present but not weak-filtered |
| `WeakLong` vs `Weak` handle distinction | ❌ | ➕ `WeakReferenceAnalyzer` — `HandleKind` enum classification |

### 24.2 `WeakReference<T>` Object Analysis

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total `WeakReference<T>` object count and size | ❌ | ➕ `WeakReferenceAnalyzer` — type name scan |
| Stale `WeakReference` wrapper detection | ❌ | ➕ `WeakReferenceAnalyzer` — `m_handle` field inspection |
| Types holding large counts of stale wrappers | ❌ | ➕ `WeakReferenceAnalyzer` |

### 24.3 `ConditionalWeakTable<TKey, TValue>` Analysis

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total `DependentHandle` count | ✅ | `DependentHandleAnalyzer` — `DependentHandleCount` |
| Source → target type distribution | ✅ | `DependentHandleAnalyzer` — `SourceTypeCounts`, `TargetTypeCounts` |
| Live vs dead key analysis | ❌ | ➕ `WeakReferenceAnalyzer` — strong reachability check per dependent handle source |
| Large CWT instances (> 10 000 entries) | ❌ | ➕ `WeakReferenceAnalyzer` |

**Verdict**: §24.3 is partially covered by `DependentHandleAnalyzer`. §24.1 and §24.2 require new `WeakReferenceAnalyzer`.

---

## §25 · Virtual Memory & Segment Reservation

### 25.1 Committed vs Reserved Memory

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total committed managed memory | 🟡 | `SegmentAnalyzer` — `CommittedBytes` per segment in `HeapSegmentSnapshot`; no global total |
| Total reserved managed memory | ❌ | ➕ `SegmentReservationAnalyzer` — `ClrSegment.ReservedMemory` |
| Reservation gap (`Reserved - Committed`) | ❌ | ➕ `SegmentReservationAnalyzer` |
| Reserved-to-committed ratio | ❌ | ➕ `SegmentReservationAnalyzer` |

### 25.2 Segment Lifecycle

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Segment count by kind (SOH ephemeral / non-ephemeral / LOH / POH / FOH) | ✅ | `SegmentAnalyzer` — `HeapSegmentSnapshot` with `Kind` |
| Ephemeral segment fill % | ❌ | ➕ `SegmentReservationAnalyzer` — `ClrSegment.IsEphemeral` + `CommittedMemory ÷ Length` |
| Non-ephemeral SOH segment count (compaction health) | ❌ | ➕ `SegmentReservationAnalyzer` |
| Logical heap assignment per segment | ❌ | ➕ `SegmentReservationAnalyzer` — `ClrSegment.LogicalHeap` |

### 25.3 Address Space Pressure

| Sub-item | Status | Analyzer(s) |
|----------|--------|-------------|
| Total virtual address space by managed heap | ❌ | ➕ `SegmentReservationAnalyzer` |
| 32-bit address space exhaustion risk | ❌ | ➕ `SegmentReservationAnalyzer` — `total reserved > 1.5 GB` threshold |
| Fragmented address space detection | ❌ | ➕ `SegmentReservationAnalyzer` — non-contiguous segment range analysis |

**Verdict**: §25 requires new `SegmentReservationAnalyzer`. `SegmentAnalyzer` has per-segment committed bytes in its snapshot list but no reserved memory, no ephemeral fill %, and no logical heap grouping.

---

---


---

---

# Analyzer Deep-Dive Index

Per-analyzer specs live in `docs/ReportStructure/Analyzers/`. Each file is self-sufficient:
it contains **Currently Produces**, **What Is Missing**, **Required Changes**, **Phase Assignment**,
and **Related Analyzers** for that single analyzer.

> The Phase 1 vs Phase 2 breakdown for every work item is in the per-analyzer file, not here.
> The architecture primer for the two-phase model is in Part 7 below.

## Existing Analyzers — Modify

| Priority | Analyzer | File | Key Gap |
|----------|----------|------|---------|
| 3 | `MemoryAnalyzer` | [MemoryAnalyzer.md](Analyzers/MemoryAnalyzer.md) | `AverageSize`, `SizeBucketHistogram` | ✅ **Completed** |
| 4 | `GCGenerationAnalyzer` | [GCGenerationAnalyzer.md](Analyzers/GCGenerationAnalyzer.md) | `PerTypeGenerationProfile`, eliminate Phase 2 re-scan |
| — | `SegmentAnalyzer` | [SegmentAnalyzer.md](Analyzers/SegmentAnalyzer.md) | POH type distribution, reserved memory |
| 6 | `LohFragmentationAnalyzer` | [LohFragmentationAnalyzer.md](Analyzers/LohFragmentationAnalyzer.md) | `TopLargeObjects`, `FreeGapHistogram` | ✅ **Completed** |
| 1 | `MemoryLeakAnalyzer` | [MemoryLeakAnalyzer.md](Analyzers/MemoryLeakAnalyzer.md) | ✂️ Split — extract `StringAnalyzer`; add `SuspicionScore` |
| — | `StaticRootLeakDetector` | [StaticRootLeakDetector.md](Analyzers/StaticRootLeakDetector.md) | `BfsCappedCount`, `RetentionPatternHints` |
| 5 | `GCHandleAnalyzer` | [GCHandleAnalyzer.md](Analyzers/GCHandleAnalyzer.md) | `PinnedRetainedBytes`, `HandleSnapshot.bin` Phase 1 | ✅ **Completed** |
| — | `ThreadAnalyzer` | [ThreadAnalyzer.md](Analyzers/ThreadAnalyzer.md) | `ThreadsWithLargeStackRoots`, `LongLivedThreadCount` |
| 2 | `HangAnalyzer` | [HangAnalyzer.md](Analyzers/HangAnalyzer.md) | ✂️ Split — extract `AsyncTaskAnalyzer` |
| 13 | `EventLeakAnalyzer` | [EventLeakAnalyzer.md](Analyzers/EventLeakAnalyzer.md) | Subscription graph mode, `EventCandidateIndex.bin` |
| — | `CollectionAnalyzer` | [CollectionAnalyzer.md](Analyzers/CollectionAnalyzer.md) | `CachePatternScore`, `Generation` field |
| 7 | `LockGraphAnalyzer` | [LockGraphAnalyzer.md](Analyzers/LockGraphAnalyzer.md) | `DeadlockCandidateDetails`, `ContestedLockDetails` |
| — | `ThreadStackClusterAnalyzer` | [ThreadStackClusterAnalyzer.md](Analyzers/ThreadStackClusterAnalyzer.md) | `LockHolderClusterCount`, `DominantWaitCategory` |
| — | `ReferenceChainAnalyzer` | [ReferenceChainAnalyzer.md](Analyzers/ReferenceChainAnalyzer.md) | `ChainSearchCapped`, shift to depth tool |
| — | `CrashAnalyzer` | [CrashAnalyzer.md](Analyzers/CrashAnalyzer.md) | Exception frame hotspots, origin classification |
| — | `ModuleAnalyzer` | [ModuleAnalyzer.md](Analyzers/ModuleAnalyzer.md) | `TotalRetainedEstimateBytes` post-pass |
| — | `DependentHandleAnalyzer` | [DependentHandleAnalyzer.md](Analyzers/DependentHandleAnalyzer.md) | `EstimatedRetainedBytes`, `IsPotentialEventSource` |
| 14 | `TrendAnalyzer` | [TrendAnalyzer.md](Analyzers/TrendAnalyzer.md) | `GrowthRatePercent`, `RegressionSeverity`, `NewLeakSignals` |

## New Analyzers — Add

| Priority | Analyzer | File | Report Sections |
|----------|----------|------|-----------------|
| 10 | `GCRootAnalyzer` | [GCRootAnalyzer.md](Analyzers/GCRootAnalyzer.md) | §5.1–5.3 |
| 11 | `DominatorAnalyzer` | [DominatorAnalyzer.md](Analyzers/DominatorAnalyzer.md) | §3.1–3.2, §4.1–4.3 |
| 2 | `AsyncTaskAnalyzer` | [AsyncTaskAnalyzer.md](Analyzers/AsyncTaskAnalyzer.md) | §8.1–8.3 | ✅ **Completed** |
| 8 | `AllocationPatternAnalyzer` | [AllocationPatternAnalyzer.md](Analyzers/AllocationPatternAnalyzer.md) | §2.3, §9.1–9.2 |
| 9 | `ObjectShapeAnalyzer` | [ObjectShapeAnalyzer.md](Analyzers/ObjectShapeAnalyzer.md) | §3.3 |
| 1 | `StringAnalyzer` | [StringAnalyzer.md](Analyzers/StringAnalyzer.md) | §11.1–11.2 | ✅ **Completed** |
| 18 | `AppDomainAnalyzer` | [AppDomainAnalyzer.md](Analyzers/AppDomainAnalyzer.md) | §18.1, §18.3 |
| 22 | `JitAnalyzer` | [JitAnalyzer.md](Analyzers/JitAnalyzer.md) | §19.1–19.3 |
| 21 | `BoxingAnalyzer` | [BoxingAnalyzer.md](Analyzers/BoxingAnalyzer.md) | §20.1–20.2 |
| 15 | `FinalizableObjectAnalyzer` | [FinalizableObjectAnalyzer.md](Analyzers/FinalizableObjectAnalyzer.md) | §21.1–21.2 |
| 17 | `ArrayAnalyzer` | [ArrayAnalyzer.md](Analyzers/ArrayAnalyzer.md) | §22.1–22.4 |
| 16 | `AsyncStateMachineAnalyzer` | [AsyncStateMachineAnalyzer.md](Analyzers/AsyncStateMachineAnalyzer.md) | §23.1–23.3 |
| 20 | `WeakReferenceAnalyzer` | [WeakReferenceAnalyzer.md](Analyzers/WeakReferenceAnalyzer.md) | §24.1–24.3 |
| 19 | `SegmentReservationAnalyzer` | [SegmentReservationAnalyzer.md](Analyzers/SegmentReservationAnalyzer.md) | §25.1–25.3 |

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
| `FinalizableObjectDomainResult` | Finalizer starvation risk (large queue + blocked finalizer thread) |
| `ArrayDomainResult` | LOH array pressure, multi-dim array anti-pattern |
| `AsyncStateMachineDomainResult` | Fire-and-forget leak (> 100 suspended instances of same method) |
| `WeakReferenceDomainResult` | Stale wrapper accumulation (dead target ratio > 50 %) |
| `SegmentReservationDomainResult` | Address space pressure warning, ephemeral fill critical |
| `AppDomainDomainResult` | Dynamic assembly accumulation, anonymous module detection |
| `JitDomainResult` | JIT heap bloat (> 500 MB), high unmanaged frame ratio |
| `BoxingDomainResult` | Boxed enum anti-pattern count, struct padding waste |

Additionally, `InsightEngine` must gain:
- **`ExecutiveSummary` output**: a `SummaryBlock` record with:
  `TotalManagedBytes, LeakLikelihoodScore (0-100), GCPressureScore (0-100),
  ThreadContentionScore (0-100), TopRecommendations (top 3 InsightFindings)`
- **`ConfidenceSummary` output**: an `IReadOnlyList<AnalyzerConfidenceNote>` — one per analyzer
  that capped its search, flagged limited data, or ran in degraded mode

---

---

# Part 5 — Implementation Priority Order

| Priority | Item | Type | Effort | Phase 1 Prereq |
|----------|------|------|--------|----------------|
| 1 | `StringAnalyzer` split from `MemoryLeakAnalyzer` | ✂️ Split | Low | ✅ `IsStringType` flag ready | ✅ **Completed** |
| 2 | `AsyncTaskAnalyzer` split from `HangAnalyzer` | ✂️ Split | Medium | ✅ `TaskIndex.bin` written | ✅ **Completed** |
| 3 | `MemoryAnalyzer` — add `AverageSize`, `SizeBucketHistogram` | Modify | Low | ✅ `GlobalSizeBuckets` ready | ✅ **Completed** |
| 4 | `GCGenerationAnalyzer` — add `PerTypeGenerationProfile` | Modify | Medium | ✅ `Gen0/1/2Count` in `TypeAggregateIndexEntry` |
| 5 | `GCHandleAnalyzer` — add `PinnedRetainedBytes` | Modify | Low | ✅ `HandleSnapshot.bin` written | ✅ **Completed** |
| 6 | `LohFragmentationAnalyzer` — add `TopLargeObjects`, `FreeGapHistogram` | Modify | Low | ✅ `LargeObjectIndex.bin` + `LohFreeBlockIndex.bin` written | ✅ **Completed** |
| 7 | `LockGraphAnalyzer` — add `DeadlockCandidateDetails` | Modify | Low | ⬜ No Phase 1 prereqs |
| 8 | `AllocationPatternAnalyzer` (new, zero heap scan) | ➕ New | Low | ✅ `GlobalSizeBuckets` + gen counts ready |
| 9 | `ObjectShapeAnalyzer` (new, type metadata only) | ➕ New | Low | ✅ `TypeShapeCache` ready |
| 10 | `GCRootAnalyzer` (new, uses existing `BoundedRootPathFinder`) | ➕ New | High | ✅ `RootIndex.bin` written |
| 11 | `DominatorAnalyzer` (new, bounded reverse-BFS) | ➕ New | Very High | 🟡 `IBoundedReferenceEdgeBuilder` interface only |
| 12 | `InsightEngine` — `ExecutiveSummary` + `ConfidenceSummary` + new inputs | Modify | Medium | ⬜ No Phase 1 prereqs |
| 13 | `EventLeakAnalyzer` — subscription graph mode | Modify | Medium | ✅ `EventCandidateIndex.bin` written |
| 14 | `TrendAnalyzer` — regression severity + growth rate % | Modify | Low | ⬜ No Phase 1 prereqs |
| 15 | `FinalizableObjectAnalyzer` (new, Phase 1 flag + Phase 2 sweep) | ➕ New | Medium | ✅ `IsFinalizableType` flag + `RootIndex.bin` ready |
| 16 | `AsyncStateMachineAnalyzer` (new, type name pattern + field walk) | ➕ New | Medium | ✅ `TypeAggregates` name scan ready |
| 17 | `ArrayAnalyzer` (new, Phase 1 flag + bounded element sampling) | ➕ New | Medium | ✅ `IsArrayType` flag + `LargeObjectIndex.bin` ready |
| 18 | `AppDomainAnalyzer` (new, `ClrModule.EnumerateTypes()` + TypeAggregates join) | ➕ New | Low | ✅ `TypeAggregates` join ready |
| 19 | `SegmentReservationAnalyzer` (new, `ClrHeap.Segments` iteration only) | ➕ New | Low | ⬜ No Phase 1 prereqs |
| 20 | `WeakReferenceAnalyzer` (new, `HandleSnapshot.bin` + `m_handle` field) | ➕ New | Low | ✅ `HandleSnapshot.bin` written |
| 21 | `BoxingAnalyzer` (new, TypeAggregates scan + TypeShapeCache) | ➕ New | Low | ✅ `TypeShapeCache` ready |
| 22 | `JitAnalyzer` (new, `GetJitManagers()` + thread stack walk) | ➕ New | Low | ⬜ No Phase 1 prereqs |

> **`DominatorAnalyzer`** is the highest-effort item and carries the most performance risk.
> It must be implemented last, after all other analyzers are in place, with dedicated
> performance testing on 10GB+ dumps before merging. All other items are safe to implement
> incrementally in the order listed.
>
> Items 15–22 are new §18–25 report sections. All are **Phase 2 only** or use existing Phase 1
> indices — none require new disk files except `FinalizableObjectAnalyzer` which reuses the
> `TypeAggregateIndexEntry.Flags` bit extension pattern already defined for items 1–9.

---

---

# Part 6 — Phase 1 Implementation Log

> Phase 1 infrastructure was completed in one batch. All items below are on branch `optimize`.

## Supporting Infrastructure (new files)

| File | Description |
|------|-------------|
| `Indexing/TypeAggregateFlags.cs` | `[Flags]` byte enum — `IsStringType`, `IsTaskType`, `IsDelegateType`, `IsFinalizableType`, `IsArrayType` |
| `Indexing/TypeShapeEntry.cs` | `readonly struct` — `RefFields`, `ValFields`, `TotalFields` per MT (~800 KB for 50 K types) |
| `Indexing/SizeBucketHelper.cs` | 8 logarithmic size-bucket boundaries + `GetBucketIndex(ulong size)` |
| `Indexing/IndexHeader.cs` | Shared 24-byte binary header (Magic / Version / RecordCount) with `WriteTo`, `TryRead`, `PatchRecordCount` |
| `Indexing/DumpIndexPaths.cs` | Canonical path resolver for all index files under `{dump}.dumpindex/` |

## Satellite Index Writers (new files)

| File | Writes | Record Size |
|------|--------|-------------|
| `Indexing/Satellite/HandleSnapshotWriter.cs` | `HandleSnapshot.bin` | 20 B (ObjAddr·8 \| MT·8 \| Kind·1 \| Pad·3) |
| `Indexing/Satellite/RootIndexWriter.cs` | `RootIndex.bin` | 20 B (TargetAddr·8 \| RootAddr·8 \| Kind·1 \| Pad·3) |
| `Indexing/Satellite/TaskIndexWriter.cs` | `TaskIndex.bin` | 20 B (Addr·8 \| MT·8 \| StateFlags·4) |
| `Indexing/Satellite/EventCandidateIndexWriter.cs` | `EventCandidateIndex.bin` | 16 B (Addr·8 \| MT·8) |
| `Indexing/Satellite/LargeObjectTracker.cs` | `LargeObjectIndex.bin` | 24 B (Addr·8 \| MT·8 \| Size·8) — top-100 only |
| `Indexing/Satellite/LohFreeBlockWriter.cs` | `LohFreeBlockIndex.bin` | 24 B (SegAddr·8 \| Offset·8 \| Size·8) |
| `Indexing/Satellite/IBoundedReferenceEdgeBuilder.cs` | `PartialRefEdgeIndex.bin` | Interface stub only — writer pending |

## Modified Files

| File | Changes |
|------|---------|
| `Indexing/TypeAggregateIndexEntry.cs` | Added `Gen0Count`, `Gen1Count`, `Gen2Count` (int), `Flags` (`TypeAggregateFlags`) |
| `Indexing/TypeIndexBuilder.cs` | `Add()` accepts `flags` + `generation`; tracks gen counts, size buckets; new `BuildSizeBuckets()` |
| `Indexing/HeapIndexBuildResult.cs` | Added `GlobalSizeBuckets: long[8]`, `TypeShapeCache: IReadOnlyDictionary<ulong, TypeShapeEntry>`, and `InMemoryTaskCandidates: (ulong Addr, ulong Mt)[]` |
| `Indexing/DiskBackedObjectIndexWriter.cs` | Per-segment type-flag + shape-cache population; satellite writers wired in; `DumpIndexPaths`-based paths replace the old `%TEMP%` path |
| `Indexing/MemoryBackedObjectIndexWriter.cs` | Same flags / gen / bucket population; `GlobalSizeBuckets` + `TypeShapeCache` + `InMemoryTaskCandidates` returned in `HeapIndexBuildResult`; `InMemoryTaskCandidates` mirrors `TaskIndex.bin` content for full memory-mode parity |

---

---

## Complete File Inventory

```
{DumpDir}/.dumpindex/
│
├── ObjectIndex.bin              ✅ EXISTING — core object address/MT/size table
│                                  Header (24 bytes): Magic|Version|ObjectCount|Reserved
│                                  Per record (24 bytes): Address(8)|MT(8)|Size(8)
│                                  For 80M objects: ~1.92GB
│
├── TypeAggregateIndex.bin       ✅ EXTENDED (in-memory) — per-MT aggregate stats
│                                  Gen0Count/Gen1Count/Gen2Count + Flags byte populated by
│                                  TypeIndexBuilder during Phase 1 heap scan.
│                                  Disk serialisation for cross-session reuse: pending.
│                                  Per record (64 bytes, padded):
│                                    MT(8)|ModuleId(4)|Count(8)|TotalSize(8)|
│                                    LohCount(8)|LohSize(8)|SampleAddress(8)|
│                                    Gen0Count(4)|Gen1Count(4)|Gen2Count(4)|
│                                    Flags(1)|Pad(3)
│                                  Flags byte usage:
│                                    bit 0 = IsStringType     (→ StringAnalyzer)
│                                    bit 1 = IsTaskType       (→ AsyncTaskAnalyzer)
│                                    bit 2 = IsDelegateType   (→ EventLeakAnalyzer)
│                                    bit 3 = IsFinalizableType (→ FinalizableObjectAnalyzer)
│                                    bit 4 = IsArrayType      (→ ArrayAnalyzer)
│                                    bits 5–7 = reserved
│                                  For 50K types: ~3.2MB
│
├── HandleSnapshot.bin           ✅ IMPLEMENTED — GC handle enumeration snapshot
│                                  Writer: HandleSnapshotWriter.cs
│                                  Per record (20 bytes): ObjectAddress(8)|MT(8)|Kind(1)|Pad(3)
│                                  Consumers: WeakReferenceAnalyzer (future, §24)
│                                  Note: GCHandleAnalyzer does NOT read this file — it calls
│                                  runtime.EnumerateHandles() directly in both memory and disk
│                                  modes. HandleSnapshot.bin is reserved for WeakReferenceAnalyzer.
│                                  Typical size: ~1MB
│
├── RootIndex.bin                ✅ IMPLEMENTED — GC root enumeration snapshot
│                                  Writer: RootIndexWriter.cs
│                                  Per record (20 bytes): TargetAddress(8)|RootAddress(8)|Kind(1)|Pad(3)
│                                  Consumers: GCRootAnalyzer, StaticRootLeakDetector, FinalizableObjectAnalyzer
│                                  Typical size: ~2MB
│
├── TaskIndex.bin                ✅ IMPLEMENTED — Task/ValueTask object snapshot (disk mode only)
│                                  Writer: TaskIndexWriter.cs
│                                  Per record (20 bytes): Address(8)|MT(8)|StateFlags(4)
│                                  Note: StateFlags written as 0; resolved in Phase 2 by AsyncTaskAnalyzer
│                                  Memory-mode equivalent: HeapIndexBuildResult.InMemoryTaskCandidates
│                                  (ulong Addr, ulong Mt)[] — collected during Phase 1 scan by
│                                  MemoryBackedObjectIndexWriter at zero extra cost.
│                                  AsyncTaskAnalyzer prefers InMemoryTaskCandidates (memory mode)
│                                  or TaskIndex.bin (disk mode) — both produce identical output.
│                                  Consumers: AsyncTaskAnalyzer
│                                  Typical size: ~20MB (worst case 1M tasks)
│
├── EventCandidateIndex.bin      ✅ IMPLEMENTED — MulticastDelegate/EventHandler object addresses
│                                  Writer: EventCandidateIndexWriter.cs
│                                  Per record (16 bytes): Address(8)|MT(8)
│                                  Consumers: EventLeakAnalyzer (FUTURE — Priority 13, not yet consumed)
│                                  Current state: EventLeakAnalyzer uses heapCache.EnumerateIndexedEntries()
│                                  in both modes — equal parity achieved without reading this file.
│                                  When Priority 13 is implemented: a memory-mode equivalent
│                                  InMemoryEventCandidates must be added to MemoryBackedObjectIndexWriter
│                                  (same pattern as InMemoryTaskCandidates) to maintain parity.
│                                  Typical size: ~8MB
│
├── LohFreeBlockIndex.bin        ✅ IMPLEMENTED — Free blocks inside LOH segments
│                                  Writer: LohFreeBlockWriter.cs
│                                  Per record (24 bytes): SegmentAddress(8)|Offset(8)|Size(8)
│                                  Consumers: LohFragmentationAnalyzer
│                                  Typical size: < 1MB
│
├── LargeObjectIndex.bin         ✅ IMPLEMENTED — Top-100 LOH objects by size
│                                  Writer: LargeObjectTracker.cs
│                                  Per record (24 bytes): Address(8)|MT(8)|Size(8)
│                                  Consumers: LohFragmentationAnalyzer, ArrayAnalyzer
│                                  Fixed size: 2.4KB (always exactly 100 entries or fewer)
│
└── PartialRefEdgeIndex.bin      🟡 INTERFACE ONLY — Reference edges for top-50 candidate types
                                    Interface: IBoundedReferenceEdgeBuilder.cs (stub)
                                    Concrete writer: pending (blocked until DominatorAnalyzer design)
                                    Per record (16 bytes): SourceAddress(8)|TargetAddress(8)
                                    Consumers: DominatorAnalyzer
                                    Capped at 500K edges: max 8MB
```

### New Analyzers — No Additional Disk Files Required

The 8 new analyzers from §18–25 are designed to use **existing Phase 1 indices** or run
entirely from Phase 2 ClrMD metadata:

| Analyzer | Index Used | Phase 2 ClrMD Calls |
|---|---|---|
| `AppDomainAnalyzer` | `TypeAggregates` (join only) | `ClrRuntime.AppDomains`, `ClrModule.EnumerateTypes()` |
| `JitAnalyzer` | None | `ClrRuntime.GetJitManagers()`, `ClrStackFrame.Method`, `ClrMethod.HotColdInfo` |
| `BoxingAnalyzer` | `TypeAggregates`, `TypeShapeCache` | `ClrType.BaseType`, `ClrThread.EnumerateStackObjects()` |
| `FinalizableObjectAnalyzer` | `ObjectIndex.bin` (Flags filter), `RootIndex.bin` | `ClrInstanceField.Read<bool>()` for `_disposed` |
| `ArrayAnalyzer` | `ObjectIndex.bin` (Flags filter) | `ClrObject.AsArray()`, element sampling |
| `AsyncStateMachineAnalyzer` | `TypeAggregates` (name scan) | `ClrType.Interfaces`, `ClrType.Fields`, `ClrInstanceField` reads |
| `WeakReferenceAnalyzer` | `HandleSnapshot.bin` | `ClrHeap.GetObject().IsValid`, `ClrInstanceField` (`m_handle`) |
| `SegmentReservationAnalyzer` | None | `ClrHeap.Segments`, `ClrSegment.CommittedMemory`, `ReservedMemory`, `LogicalHeap` |
                                    Per record (16 bytes): SourceAddress(8)|TargetAddress(8)
                                    Capped at 500K edges: max 8MB
```

## In-Memory Phase 1 Structures (not persisted to disk)

These are built during Phase 1, stored in `HeapIndexBuildResult`, and held in memory for
the lifetime of the analysis session. They are rebuilt if the session restarts.

```
HeapIndexBuildResult extensions:

  GlobalSizeBuckets: long[8]                                    ✅ IMPLEMENTED
    → 8 size-bucket object counts built by TypeIndexBuilder
    → Bucket boundaries defined in SizeBucketHelper.BucketLabels
    → 64 bytes. Always in memory.

  TypeShapeCache: IReadOnlyDictionary<ulong, TypeShapeEntry>    ✅ IMPLEMENTED
    → Per-MT field layout (RefFields, ValFields, TotalFields)
    → TypeShapeEntry = readonly struct (6 bytes per entry, padded to 8)
    → 50K types × 16 bytes (key + value) = ~800KB. Always in memory.

  InMemoryTaskCandidates: (ulong Addr, ulong Mt)[]                             ✅ IMPLEMENTED
    → Collected during Phase 1 parallel scan in MemoryBackedObjectIndexWriter (same pass as InMemoryEntries).
    → Contains all Task/ValueTask addresses — mirrors TaskIndex.bin content for memory-mode parity.
    → Stored in HeapIndexBuildResult.InMemoryTaskCandidates.
    → AsyncTaskAnalyzer reads this directly (O(N_tasks)) instead of scanning InMemoryEntries (O(N_total)).
    → Falls back to TypeAggregates TaskMtSet scan when unavailable (old index files, benchmarks).

  StringMtSet: derived on first access from TypeAggregates.Flags.IsStringType
    → Computed lazily from TypeAggregates — not stored separately.

  TaskMtSet: derived on first access from TypeAggregates.Flags.IsTaskType
    → Fallback only. Used by ScanHeapIndexForTasks when InMemoryTaskCandidates is unavailable.
```

## Phase 1 Memory Footprint Summary

| Structure | Size (50K types, 80M objects) | Storage |
|-----------|-------------------------------|---------|
| `TypeAggregates` dictionary (extended) | ~3.2MB | Memory |
| `GlobalSizeBuckets` | 64 bytes | Memory (in HeapIndexBuildResult) |
| `TypeShapeCache` | ~800KB | Memory (in HeapIndexBuildResult) |
| `ObjectIndex.bin` write buffer | 4MB (Large tier) | Memory (transient, released after write) |
| `TaskIndex.bin` write buffer | 256KB | Memory (transient) |
| `EventCandidateIndex.bin` write buffer | 256KB | Memory (transient) |
| **Total peak Phase 1 memory** | **~8.5MB** | — |

> The heap-streaming working set stays near-constant regardless of dump size because
> per-object allocations are zero (struct-only `HeapEntry` in `ArrayPool<HeapEntry>` buffers)
> and all index files are written sequentially via `FileStream` with configurable buffer sizes.

## Phase 1.5 — Bounded Reference Edge Collection

> **Status**: 🟡 `IBoundedReferenceEdgeBuilder` interface defined. Concrete writer pending.

This is a distinct step that runs **after Phase 1 completes** but **before Phase 2 begins**.
It is only executed if `DominatorAnalyzer` is enabled.

```
Trigger condition:   DominatorAnalyzer in analyzer set AND ObjectIndex.bin exists
Input:               ObjectIndex.bin (sequential read) + TypeAggregates (candidate selection)
Output:              PartialRefEdgeIndex.bin
Memory budget:       ≤ 64MB for the in-progress edge buffer (flushed every 32K edges)
Time budget:         Configurable timeout (default 60 seconds)
Capping:             Edge count cap (500K), time cap, both independently enforced
```

The step is implemented as a separate `IBoundedReferenceEdgeBuilder` service, not as part of
`IObjectIndexWriter`, to keep the Phase 1 index writers focused and simple.

## Record Format Notes

- All multi-byte integers are **little-endian** — consistent with existing `ObjectIndex.bin`
  (matches `BinaryPrimitives.ReadUInt64LittleEndian` usage in `ObjectIndexReader`).
- All files use a **24-byte header minimum** — 4-byte magic, 4-byte version, 8-byte record count,
  8 bytes reserved/flags — ensuring forward compatibility without breaking readers.
- All files are **append-only during Phase 1** — no random writes, no seeking.
- All Phase 2 reads use **`FileOptions.SequentialScan`** and `ArrayPool<byte>` buffers
  consistent with the existing `ObjectIndexReader` pattern.
- Index files reside in a **per-dump `.dumpindex/` subdirectory** alongside the dump file,
  named `{DumpFileName}.dumpindex/`. This avoids polluting the dump directory and enables
  atomic cleanup (delete the folder = full cache invalidation).

## Cache Invalidation

Index files are valid only for the exact dump that generated them. Validity is checked by:

```
ObjectIndex.bin header:  contains DumpFileHash(8) = FNV-64 of first 64KB of dump file
                         + DumpFileSizeBytes(8)
On open:                 verify both fields match current dump file — if not, rebuild
```

This same validation is applied to all satellite index files (HandleSnapshot, RootIndex, etc.)
via a shared `IndexHeader` struct that all writers produce and all readers verify.

---

---

# Part 7 — Developer Guide: Adding a New Analyzer

> This is the **canonical checklist** for adding any new analyzer after the reporting refactor
> (Phases A–H complete). Every step is mandatory. Follow in order — each step builds on the previous.
> See the per-analyzer spec files under `docs/ReportStructure/Analyzers/` for domain-specific decisions.

---

## Step 1 · Domain Result — `DumpDetective.Analysis`

**File**: `src/DumpDetective.Analysis/Models/AnalyzerDomainModels.cs`

Add a `public sealed record XxxDomainResult(...) : AnalyzerDomainResult` at the end of the file.

Rules:
- All fields must be **plain CLR types** (`int`, `ulong`, `string`, `IReadOnlyList<T>`) — no ClrMD objects.
- Optional / expensive fields use `IReadOnlyList<T>? Property = null` — null until the analyzer populates them.
- Cap/limit signals (`bool XxxCapped`, `int SkippedXxx`) must be explicit fields — `ReportSerializer` reads them for `ConfidenceNote` generation.
- Use existing snapshot sub-types where possible (e.g. `TypeSnapshot`, `NameCountEntry`, `LohSegmentSnapshot`).

```csharp
// Example
public sealed record XxxDomainResult(
    int TotalFoo,
    ulong TotalFooBytes,
    bool FooCapped = false,
    IReadOnlyList<NameCountEntry>? TopFooTypes = null) : AnalyzerDomainResult;
```

---

## Step 2 · Analyzer — `DumpDetective.Analysis`

**File**: `src/DumpDetective.Analysis/Analyzers/XxxAnalyzer.cs`

Implement `IAnalyzer`. Follow the `// Copilot instructions` performance rules strictly:

- `Name` must match the string used in `DefaultAnalyzerFactory` and `IAnalyzerSectionBuilder.AnalyzerName`.
- Stream heap objects via `foreach (var obj in heap.EnumerateObjects())` — never `.ToList()`.
- Use `ArrayPool<T>` for any temporary buffers.
- Call `result.Stamp(this)` at the end of `AnalyzeAsync` to attach `AnalyzerName`/`Category`.
- If using a satellite index (e.g. `HandleSnapshot.bin`), read it via `FileOptions.SequentialScan` + `ArrayPool<byte>`.

### Progress Reporting (mandatory)

Every analyzer **must** emit progress so the CLI shows live activity.
The sink is `context.Progress` (`IProgress<AnalyzerProgressReport>`).
`AnalyzerProgressReport` has four fields: `ScannedCount`, `Phase`, `Detail?`, `Elapsed?`.

Three patterns are used — choose the right one per work unit:

---

#### Pattern 1 — Phase announce (no count)

Use at the **start of any named work phase** before the loop begins, so the CLI label
changes immediately even if the loop is slow to start.

```csharp
progress?.Report(new(0, "resolving roots"));
progress?.Report(new(0, "building type index"));
progress?.Report(new(0, "analyzing threads"));
```

- `ScannedCount = 0` — no objects processed yet.
- `Phase` — short present-participle label, lowercase, e.g. `"classifying heap segments"`.
- No `Detail` needed unless a known total is available upfront
  (e.g. `$"0 / {totalSegments} segments"`).

---

#### Pattern 2 — `ObjectScanCounter` (O(N) heap / index scans)

Use for any loop that iterates the full heap, `ObjectIndex.bin`, or any large satellite index.
`ObjectScanCounter` auto-throttles: it fires at most once every 250 000 objects **or** 2 seconds,
whichever comes first. Override both thresholds for small datasets (e.g. thread scans).

```csharp
var scanCounter = new ObjectScanCounter("scanning foo objects", progress);
foreach (var entry in cache.EnumerateIndexedEntriesAsTuples())
{
    scanCounter.Tick();          // call on EVERY iteration, not just matching ones
    if (!IsMatch(entry)) continue;
    // ... process ...
}
scanCounter.Complete();          // always call — emits final count
```

Rules:
- Instantiate **one counter per loop**.
- `Tick()` on **every** iteration so throughput reflects actual scan rate, not match rate.
- `Complete()` immediately after the loop regardless of early exit.
- For small bounded loops (e.g. thread count < 1 000), reduce cadence:
  ```csharp
  new ObjectScanCounter("scanning threads", progress,
      reportEveryObjects: 100,
      reportEveryElapsed: TimeSpan.FromSeconds(1));
  ```
- Use distinct labels when the same analyzer has two enumeration paths:
  `"scanning foo objects (indexed)"` vs `"scanning foo objects"`.

---

#### Pattern 3 — Manual `progress?.Report` (structured multi-phase work)

Use when an analyzer has **discrete named phases** with known totals or meaningful intermediate
results — segment classification, root tracing, aggregation finalization.

```csharp
// Phase start with known total
progress?.Report(new(0, "classifying heap segments", $"0 / {totalSegments} segments"));

// Mid-loop with running total and detail
progress?.Report(new(
    ScannedCount: totalObjectsScanned,
    Phase: "classifying heap segments",
    Detail: $"{segmentsProcessed} / {totalSegments} segments, {totalObjectsScanned:N0} objects"));

// Post-processing phase complete
progress?.Report(new(
    ScannedCount: totalObjectsScanned,
    Phase: "aggregating results",
    Detail: $"{snapshots.Count} segments, {totalObjectsScanned:N0} objects total"));
```

Rules:
- Fire at phase transitions (start, completion) unconditionally.
- For interval firing inside a loop, gate on a modulus to avoid flooding:
  ```csharp
  if (rootsScanned % 50 == 0)
      progress?.Report(new(rootsScanned, "scanning static roots", $"{results.Count} significant"));
  ```
- `Detail` must be short — it appears inline next to the scan rate on a single console line.
- `Elapsed` is optional; omit unless you have a meaningful `Stopwatch` to pass.

---

#### Combined example (all three patterns in one analyzer)

```csharp
public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken ct)
{
    // Pattern 1 — phase announce
    context.Progress?.Report(new(0, "resolving candidate types"));
    var candidates = BuildCandidates(context.Cache);

    // Pattern 2 — ObjectScanCounter for the main heap scan
    var scanCounter = new ObjectScanCounter("scanning xxx objects", context.Progress);
    foreach (var (address, mt, size) in context.Cache.EnumerateIndexedEntriesAsTuples())
    {
        ct.ThrowIfCancellationRequested();
        scanCounter.Tick();
        if (!candidates.Contains(mt)) continue;
        Process(address, size);
    }
    scanCounter.Complete();

    // Pattern 3 — manual report for the aggregation phase
    context.Progress?.Report(new(scanCounter.Scanned, "aggregating results",
        $"{_hits:N0} matches from {scanCounter.Scanned:N0} objects"));

    return ValueTask.FromResult(BuildResult().Stamp(this));
}
```

---

#### What NOT to do

| Anti-pattern | Fix |
|---|---|
| No progress at all | Add at least Pattern 1 at entry + Pattern 2 for the main loop |
| `Tick()` only on matching objects | Call on every iteration |
| Missing `Complete()` | Always call after the loop |
| Progress inside a static helper with no `progress` param | Thread `progress` through as a parameter |
| Flooding with `Report()` on every object | Use `ObjectScanCounter` or modulus gate |

```csharp
internal sealed class XxxAnalyzer : IAnalyzer
{
    public string Name     => "Xxx Analysis";
    public string Category => "Xxx";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context, ct).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(AnalysisContext context, CancellationToken ct)
    {
        context.Progress?.Report(new(0, "scanning xxx objects"));
        var scanCounter = new ObjectScanCounter("scanning xxx objects", context.Progress);
        foreach (var obj in context.Heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            scanCounter.Tick();
            if (!obj.IsValid || obj.Type is null) continue;
            // ... processing ...
        }
        scanCounter.Complete();
        return new XxxDomainResult(...);
    }
}
```

---

## Step 3 · Register Analyzer — `DumpDetective.Analysis`

**File**: `src/DumpDetective.Analysis/Analyzers/DefaultAnalyzerFactory.cs`

Add `new XxxAnalyzer()` to `CreateAnalyzers()`. The `Name` string here must be **identical** to `IAnalyzer.Name`.

---

## Step 4 · Finding Generator — `DumpDetective.Reporting`

**File**: `src/DumpDetective.Reporting/FindingGenerators/XxxFindingGenerator.cs`

Implement `IFindingGenerator`. Maps `XxxDomainResult` → `IReadOnlyList<InsightFinding>`.

Rules:
- `AnalyzerName` property must match `XxxAnalyzer.Name` exactly — this is the routing key in `FindingGenerationPipeline`.
- Emit `Critical` / `Warning` / `Info` findings only when evidence is substantial.
- Each `InsightFinding.Fingerprint` must be deterministic (no GUIDs, no timestamps) — duplicates are deduplicated by `ReportSerializer` using this key.
- `Evidence` must be a complete self-contained sentence — no placeholder strings.

**Register in ServiceRegistration.cs**:
```csharp
services.AddSingleton<IFindingGenerator, XxxFindingGenerator>();
```

---

## Step 5 · Trend Comparer — `DumpDetective.Analysis`

**File**: `src/DumpDetective.Analysis/Trend/XxxTrendComparer.cs`

Implement `IAnalyzerTrendComparer`. Defines which numeric fields from `XxxDomainResult` are tracked across dumps.

Rules:
- `AnalyzerName` must match `XxxAnalyzer.Name` exactly.
- Only expose **stable scalar metrics** as trend points — not lists, not per-object data.
- Set `MetricTrendDirection` correctly: `HigherIsWorse` for memory/count metrics, `LowerIsWorse` for coverage/efficiency metrics.

**Register in ServiceRegistration.cs**:
```csharp
services.AddSingleton<IAnalyzerTrendComparer, XxxTrendComparer>();
```

---

## Step 6 · Section Builder — `DumpDetective.Reporting`

**File**: `src/DumpDetective.Reporting/SectionBuilders/XxxSectionBuilder.cs`

Implement `IAnalyzerSectionBuilder`. Converts `XxxDomainResult` → `AnalyzerDetailSection` (a list of `SectionBlock`s).

Rules:
- `AnalyzerName` must match `XxxAnalyzer.Name` exactly — this is the routing key in `ReportSerializer`.
- `CanHandle` must be `result is XxxDomainResult` — no duck typing.
- `SortOrder` must be unique; consult the builder inventory table in this document.
- Derive from `SectionBuilderBase` to use `H()`, `M()`, `T()`, `Li()`, `Divider()`, `Blank()`, `Cell()` helpers.
- Use `CollapsibleSectionBeginBlock` / `CollapsibleSectionEndBlock` pairs for any list > 5 entries.
- Provide `RawValue` on `MetricBlock` and `TableCell` whenever a numeric sort or chart is meaningful.

```csharp
internal sealed class XxxSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName  => "Xxx Analysis";
    public string DisplayTitle  => "Xxx Analysis";
    public int    SortOrder     => 90;   // pick a unique value

    public bool CanHandle(AnalyzerDomainResult result) => result is XxxDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var domain = (XxxDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("SUMMARY"));
        blocks.Add(M("Total Foo", $"{domain.TotalFoo:N0}", domain.TotalFoo));
        // ...

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks);
    }
}
```

**Register in `DefaultSectionBuilderFactory.CreateBuilders()`**:
```csharp
new XxxSectionBuilder(),
```

---

## Step 7 · Confidence Notes — `ReportSerializer`

**File**: `src/DumpDetective.Reporting/Services/ReportSerializer.cs`

If `XxxDomainResult` exposes any cap/limit signal, add it to `BuildConfidenceNotes()`:

```csharp
case XxxDomainResult xxx when xxx.FooCapped:
    notes.Add(new ConfidenceNote(
        Analyzer: run.AnalyzerName,
        Capped:   true,
        Reason:   "Foo search was capped; results may be incomplete."));
    break;
```

---

## Step 8 · InsightEngine Inputs (if cross-cutting)

**File**: `src/DumpDetective.Reporting/InsightEngine.cs` (or equivalent)

If `XxxDomainResult` enables new **cross-cutting detections** (e.g. correlating with memory or thread results), add input processing to `InsightEngine.Analyze()` per the table in Part 4 above.

This step is optional for analyzers whose findings are fully self-contained.

---

## Step 9 · Tests

Add at minimum:

| Test file | What to add |
|-----------|-------------|
| `SectionBuilderTests.cs` | 2–3 tests: `CanHandle` routing, block structure, key metric values |
| `ReportDocumentSchemaTests.cs` | 1 test: round-trip with `XxxDomainResult`-populated `AnalyzerDetailSection` |

Golden files (Text / Markdown / JSON) regenerate automatically via `UPDATE_GOLDENS=1` — no manual baseline updates needed unless the fixture data changes.

---

## Step 10 · Update Coverage Docs

| File | Update |
|------|--------|
| `docs/ReportStructure/AnalyzerCoverageAnalysis.md` | Mark covered report sections ✅ in Part 1; add row to Part 5 priority table with `Completed`; update `Analyzer Deep-Dive Index` table |
| `docs/ReportStructure/Analyzers/XxxAnalyzer.md` | Create the per-analyzer spec file (see existing files for format) |

---

## Checklist Summary

```
□ 1. XxxDomainResult in AnalyzerDomainModels.cs
□ 2. XxxAnalyzer.cs implements IAnalyzer + calls result.Stamp(this)
     └─ Progress: Pattern 1 (phase announce) at entry of each named phase
     └─ Progress: Pattern 2 (ObjectScanCounter) for every O(N) heap/index loop
     └─ Progress: Pattern 3 (manual Report) for structured multi-phase work with known totals
□ 3. DefaultAnalyzerFactory — add new XxxAnalyzer()
□ 4. XxxFindingGenerator.cs + ServiceRegistration.cs
□ 5. XxxTrendComparer.cs + ServiceRegistration.cs
□ 6. XxxSectionBuilder.cs + DefaultSectionBuilderFactory.CreateBuilders()
□ 7. ReportSerializer.BuildConfidenceNotes() — add cap signal if present
□ 8. InsightEngine — add cross-cutting input if needed
□ 9. SectionBuilderTests.cs + ReportDocumentSchemaTests.cs
□ 10. AnalyzerCoverageAnalysis.md + Analyzers/XxxAnalyzer.md
```

> **No other files require changes.**
> `IAnalyzer`, `AnalysisContext`, `ReportSerializer` core logic, `HtmlReportRenderer`,
> `IReportFormatter`, `TextCanonicalReportFormatter`, `MarkdownCanonicalReportFormatter`,
> and all golden test baselines are fully stable after Step 9 runs with `UPDATE_GOLDENS=1`.
