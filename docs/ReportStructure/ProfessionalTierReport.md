# Dump Analyzer Report — Professional Tier

## Report Contract
- All outputs (JSON, markdown, HTML) render from the same `AnalysisReportDocument`; no format may add extra semantics.
- Single-dump and trend-mode share the same schema; trend mode adds snapshot metadata, deltas, and comparative findings.
- Every traversal, scan, and ranking must be capped by explicit top-N, breadth, or depth limits.
- Every Critical/Warning finding must carry: source analyzer, metric key, evidence references, address list; trend mode adds snapshot index.
- Status: `Implemented` = wired + validated; `Partial` = bounded/heuristic; `Planned` = not yet wired.

The authoritative single-dump format, field inventory, and section ordering rules live in [SingleDumpReportFormat.md](SingleDumpReportFormat.md). Use this document for status tracking and gap notes only; do not treat it as the schema source of truth.

Implementation status should distinguish between `Surfaced in report` and `In model only` when a field exists but is not rendered yet.

## Quality Rules
- Emit schema/version near the top of every serialized document.
- Normalize analyzer run statuses everywhere: `Completed`, `Failed`, `Skipped`, `TimedOut`.
- Same findings, confidence values, and analyzer statuses must appear across all renderers.
- Keep top-N, breadth, and depth limits visible in prose so large-dump behavior is reproducible.
- Golden report tests must validate content and status coverage for small and large dumps.

## Trend Report Contract

Trend mode composition, narrative order, and acceptance criteria are specified in [TrendReportBlueprint.md](TrendReportBlueprint.md). The authoritative trend document schema — section map, field-to-source mapping, rendering rules, and stable section anchors (`T0`–`T7`) — is in [TrendReportFormat.md](TrendReportFormat.md). The detailed implementation plan (steps T1–T11, dependency order, test checklist) is in [TrendReportImplementationPlan2.md](TrendReportImplementationPlan2.md). Per-section data availability for trend-specific signals (snapshot scope, deltas, lifecycle) follows the same ✅/⚠️/❌ notation used throughout this document.

---

# 1. Executive Summary

- Total managed memory + % of process
  - ✅ `MemoryDomainResult.TotalBytes` — sum of all type aggregate sizes from Phase 1 index
  - ❌ "% of process" — no process-total-memory field for the target dump; `AnalysisIncidentContext` carries only `GcMode`/`HeapCount`/runtime metadata; `MemoryDiagnostic` tracks the *analyzer* process's working set, not the target; no `WorkingSet64` or `PrivateMemorySize64` of the dump target is surfaced in any domain result or report model
- Top memory consumers by retained size
  - ✅ `MemoryDomainResult.TopTypesBySize` (`IReadOnlyList<TypeSnapshot>`) — sorted by `TotalSize` (shallow); configurable `TopBySizeCount`
  - ❌ "by retained size" — `TypeSnapshot.EstimatedRetainedBytes` is always 0 from `MemoryAnalyzer` (same §4.1 gap); no BFS retained-size here; section builder must use shallow size as proxy
- Key anomalies: leak likelihood, GC pressure, thread contention
  - ✅ `InsightEngine.Analyze()` returns `IReadOnlyList<InsightFinding>` sorted by `FindingSeverity` descending; includes `DetectLeakSuspicion`, `DetectAllocationPressureCrossCorrelation` (cross-refs `GCPressureLevel`), and `DetectThreadContention`
  - ✅ `AllocationPatternDomainResult.GCPressure` (`GCPressureLevel` enum: `Low`/`Moderate`/`High`/`Critical`) — direct GC pressure signal
  - ⚠️ "Leak likelihood" is heuristic-only (`RetentionDomainResult` signals + string growth); no `LeakLikelihoodScore` scalar — §6.1 gap
- Top 3 actionable recommendations
  - ✅ Each `InsightFinding` carries `Recommendation` (string); list is severity-sorted; section builder takes top 3 by severity
  - ❌ No dedicated `ExecutiveSummary` model or pre-assembled top-3 field — section builder must assemble from `InsightEngine` output

---

# 2. Memory Topology

## 2.1 Heap Composition

- SOH / LOH / POH / FOH proportions (`HeapSegmentKind.Frozen` for FOH)
  - ✅ `SegmentAnalyzer` → `SegmentAnalysisDomainResult`: `SohBytes`, `LohBytes`, `PohBytes`, `FrozenBytes`, `TotalCommittedBytes`, `LohPercent`, `PohPercent`
  - ✅ `FrozenPercent` now a field in `SegmentAnalysisDomainResult` (`FrozenBytes / TotalCommittedBytes`)
  - API: `ClrHeap.Segments` → `SegmentKindMapper.Map(segment)` → accumulate per kind
- Object size distribution histogram (bucketed by size range)
  - ✅ `MemoryAnalyzer` → `MemoryDomainResult.SizeBucketHistogram` (`IReadOnlyList<SizeBucketEntry>`)
  - Source: Phase 1 index `HeapIndexBuildResult.GlobalSizeBuckets` (8-bucket `long[]`, zero extra heap walk)
  - ⚠️ Byte totals per bucket are approximate (uses per-type average size, not per-object); counts are exact
- GC mode: Workstation vs Server (`ClrHeap.IsServerGC`)
  - ✅ `IncidentContextFactory` → `AnalysisIncidentContext.GcMode` (string: `"Server GC"` / `"Workstation GC"` / null)
  - ⚠️ Fetched via reflection (`GetBoolProperty(heap, "IsServerGC")`); property name varies across ClrMD versions — check if `ClrHeap.IsServerGC` is directly accessible in ClrMD 3.1.5
- Server GC heap count (`ClrHeap.HeapCount`) — one per CPU
  - ✅ `IncidentContextFactory` → `AnalysisIncidentContext.HeapCount` (`int?`); rendered in all formatters
  - ⚠️ Also fetched via reflection — consider `ClrHeap.SubHeaps.Count()` as a more stable alternative
- Per-logical-heap segment breakdown: size and object count per heap index (imbalance = thread affinity or allocation hotspot)
  - ✅ `SegmentAnalysisDomainResult.PerLogicalHeapSummaries: IReadOnlyList<PerLogicalHeapSummary>` now implemented; each entry carries `LogicalHeapIndex`, `Bytes`, `ObjectCount`, `SegmentCount`; aggregated in existing segment loop at zero extra cost
  - `SegmentReservationAnalyzer` also builds `ReservedByLogicalHeap: IReadOnlyDictionary<int, ulong>` (reserved bytes per heap)

## 2.2 Generation Pressure

- Gen0 / Gen1 / Gen2 distribution
  - ✅ `GCGenerationAnalyzer` → `GCGenerationDomainResult`: `Gen0Bytes`, `Gen0Objects`, `Gen1Bytes`, `Gen1Objects`, `Gen2Bytes`, `Gen2Objects`, `LohBytes`, `Gen2Pct`
  - Source: Phase 1 `TypeAggregateIndexEntry` (`Gen0Count`, `Gen1Count`, `Gen2Count`) — zero extra heap scan
  - Per-type breakdown: `PerTypeGenerationProfiles` (`IReadOnlyList<TypeGenerationProfile>`) — top-N by count
  - ⚠️ Gen byte totals are approximate (`AnalyzerHelpers.ComputeApproxGenBytes` uses average non-LOH size × per-type gen count, not per-object)
- Promotion patterns
  - ✅ `AllocationPatternAnalyzer` → `AllocationPatternDomainResult`: `PromotionPressureScore` (double), `TopTransientTypes`, `TopShortishTypes`, `TopLongLivedTypes` (per-type `AllocationProfile`: `Transient`/`Steady`/`Retained`/`Mixed`)
  - ⚠️ No per-type survival rate (Gen1÷total) in the model — only composite pressure score; add if per-type promotion rate is needed in report

## 2.3 Allocation Patterns

- Gen0 object count (proxy for recent allocation pressure)
  - ✅ `GCGenerationDomainResult.Gen0Objects` (from `GCGenerationAnalyzer`)
  - Also: `AllocationPatternDomainResult.Gen0CountPct` — Gen0 objects as % of total
- Gen0 : Gen2 ratio — high = churn, low = accumulation
  - ✅ Derivable from `AllocationPatternDomainResult.Gen0CountPct` / `Gen2CountPct` (both available)
  - Not emitted as a named ratio field — ⚠️ add `Gen0ToGen2Ratio` to model or compute in section builder
- Ephemeral segment fill % (`ClrSegment.IsEphemeral`) — above 80 % = imminent GC trigger
  - ✅ `SegmentReservationAnalyzer` → `SegmentReservationDomainResult.AvgEphemeralFillPct`
  - Also per-segment in `SegmentReservationEntry.FillPct` where `IsEphemeral = true`
  - API: `ClrSegment.CommittedMemory ÷ ClrSegment.Length` on ephemeral segments (`SegmentKindMapper.IsEphemeral`)
- Heuristic classification: **Accumulating** (large Gen2, low Gen0) / **Churning** (large Gen0, high promotion) / **Balanced**
  - ⚠️ `AllocationPatternAnalyzer` uses `AllocationProfile` enum: `Transient`/`Steady`/`Retained`/`Mixed` — semantically equivalent but naming doesn't match doc
  - `ClassifyProfile(gen0CountPct, gen2CountPct)` drives the enum; `GCPressureLevel` (`Low`/`Moderate`/`High`/`Critical`) is also emitted
  - Either rename enum values to match doc or map in section builder

> Allocation sites require ETW traces; these classifications are dump-snapshot heuristics only.

---

# 3. Type System Analysis

## 3.1 Detailed Type Table

| Column | Available | Source / Analyzer | Notes |
|---|---|---|---|
| Object count | ✅ | `TypeAggregateIndexEntry.Count` (Phase 1 index) | |
| Shallow size (total) | ✅ | `TypeAggregateIndexEntry.TotalSize` (Phase 1 index) | |
| Shallow size (avg) | ✅ | `TotalSize / Count`; exposed as `TypeSnapshot.AverageSize` in `MemoryDomainResult` | |
| Estimated retained size | ✅ | `TypeSnapshot.EstimatedRetainedBytes` populated by `MemoryAnalyzer` via `BoundedRetainedSizeBfs.ComputeExclusiveRetained` for top-N types; bounded (breadth 10 000, depth 20) | Heuristic; not true dominator |
| GC generation distribution | ✅ | `TypeAggregateIndexEntry.Gen0Count/Gen1Count/Gen2Count/LohCount` → `GCGenerationAnalyzer.PerTypeGenerationProfiles` | Per-type Gen% derivable |
| Is finalizable | ✅ | `TypeShapeProfile.IsFinalizable` via `ClrType.IsFinalizable`; resolved by `ObjectShapeAnalyzer` for top-N types | Only for types in shape analysis cap |
| Is value type | ✅ | `TypeShapeProfile.IsValueType` via `ClrType.IsValueType` | Same cap |
| Is array | ✅ | `TypeShapeProfile.IsArray` via `ClrType.IsArray`; resolved by `ObjectShapeAnalyzer`; `TypeAggregateFlags.IsArrayType` also set during Phase 1 indexing | Same shape-analysis cap |
| Base type chain depth | ✅ | `TypeShapeProfile.BaseTypeChainDepth` (`ComputeBaseTypeDepth` via `ClrType.BaseType` traversal) | |
| Interface count | ✅ | `TypeShapeProfile.InterfaceCount` (`ClrType.EnumerateInterfaces().Count()`) | |
| Field count (ref / value) | ✅ | `TypeShapeProfile.ReferenceFields`, `ValueFields`, `TotalFields` from `TypeShapeEntry` (Phase 1 cache) | |
| Module | ⚠️ | `TypeAggregateIndexEntry.ModuleId` (int) → name lookup via `ModuleAnalyzer`; not directly in type table | Add name resolution in section builder |
| Method table address | ✅ | `TypeAggregateIndexEntry.MethodTable` (ulong) — available directly from the index |

## 3.2 Dominator Candidates

Nomination criteria (any one qualifies):
1. Type total size > 1 % of total heap
2. > 80 % of instances in Gen2
3. `IsFinalizable = true` and instance count > 500
4. Known container (`Dictionary`, `List`, `ConcurrentQueue`, arrays) with total size > 50 MB

- Criteria 1 & 2: ✅ all signals available from `TypeAggregateIndexEntry` (`TotalSize`, `Gen2Count`, `Count`) — zero extra scan
- Criteria 3: ✅ `IsFinalizable` in `TypeShapeProfile` (from `ObjectShapeAnalyzer`); `Count` from index
- Criteria 4: ✅ type name pattern match on `TypeAggregateIndexEntry` key (type name string from `heap.GetTypeByMethodTable`)

Per candidate fields:
- Instance count, total shallow size: ✅ `TypeAggregateIndexEntry`
- Largest instance (address + size): ✅ `TypeAggregateIndexEntry.SampleAddress` (one sample captured during Phase 1); size from `TotalSize/Count`
- Gen2 %: ✅ `Gen2Count / Count` from index
- Estimated retained size: ✅ `DominatorAnalyzer` (Phase 2) calls `BoundedRetainedSizeBfs.ComputeExclusiveRetained` per candidate; result stored in `TypeSnapshot.EstimatedRetainedBytes` inside `DominatorDomainResult.TopDominatorTypes`
- GC root reachability: ⚠️ not available per type from index; requires cross-referencing `GCRootAnalyzer` output by type name

Top 30: ✅ `DominatorAnalyzer` → `DominatorDomainResult.TopDominatorTypes` (`IReadOnlyList<TypeSnapshot>`) sorted by `EstimatedRetainedBytes`; candidate count capped via `TopHighlyReferencedObjectsToShow` option

> `DominatorAnalyzer` replaced the suggestion — no separate `DominatorCandidateBuilder` needed; nomination criteria are applied inside the analyzer.

## 3.3 Object Shape Analysis

Via `ClrType.Fields` for each type in the index:
- Reference field count vs value-type field count
  - ✅ `TypeShapeEntry.RefFields`, `ValFields`, `TotalFields` (Phase 1 cache `HeapIndexBuildResult.TypeShapeCache`; ~50K types × 8 bytes ≈ 400 KB)
  - Full profiles in `TypeShapeProfile` (`ObjectShapeAnalyzer`): `ReferenceFields`, `ValueFields`, `ReferenceFieldRatio`
- Classification: `ReferenceHeavy` (>=50 % ref fields) / `ValueHeavy` (0 ref fields) / `Mixed`
  - ⚠️ `ObjectShapeCategory` enum uses `ReferenceHeavy` (ratio > 0.6) / `ValueHeavy` (ratio < 0.2) / `Balanced` / `Scalar` — thresholds differ from doc spec; doc says `Mixed` but code uses `Balanced`
  - Align thresholds or remap in section builder
- Pure value containers: zero ref fields
  - ✅ filter `TypeShapeEntry.RefFields == 0` from index — no extra ClrMD calls needed
- Oversized value types: structs with unexpectedly large shallow size
  - ✅ `IsValueType` in `TypeShapeProfile` + `TotalSize/Count` from `TypeAggregateIndexEntry`
  - No explicit oversized struct list in current model — section builder filters `IsValueType && avgSize > threshold`
- Top 20 by reference field density (ref field count / total field count)
  - ✅ `ObjectShapeAnalyzerDomainResult.TopReferenceHeavyTypes` — already ranked by `ReferenceFieldRatio × InstanceCount`
  - ⚠️ ranked by GC-scan-cost composite (ratio × count), not pure density ratio — adjust sort or add separate list

---

# 4. Retention & Dominator Analysis

## 4.1 Retention Hotspots

Scoped to top-N types by total shallow size:
- Per-type estimated retained size: bounded BFS via `ClrObject.EnumerateReferences()`
  - ✅ `BoundedRetainedSizeBfs.ComputeExclusiveRetained` implemented in `DumpDetective.Analysis.Utilities`; called by `MemoryAnalyzer` for top-N types and by `DominatorAnalyzer` for dominator candidates
  - ✅ `TypeSnapshot.EstimatedRetainedBytes` is now populated for top types in `MemoryDomainResult.TopTypesBySize` and `DominatorDomainResult.TopDominatorTypes`
  - `ReferenceChainAnalyzer` still does BFS from sample instances to GC roots (path finding), not retained-size computation — not changed
- Retention ratio: retained / shallow
  - ⚠️ Derivable from `TypeSnapshot.EstimatedRetainedBytes / TotalSize`; not pre-computed as a named field
- Top 20 by retention ratio
  - ⚠️ Data available; `DominatorDomainResult.TopDominatorTypes` is sorted by `EstimatedRetainedBytes` (not by retention ratio); section builder must re-sort by ratio if needed
- Limits: breadth 10 000 objects/candidate, depth 20 hops
  - ✅ `BoundedRetainedSizeBfs` uses configurable `maxBreadth` (default 10 000 via `RetentionOptions.MaxLeakScanObjects`) and `maxDepth = 20`; `ReferenceChainAnalyzer` uses separate path-search limits

## 4.2 Dominator Tree (Approx)

Full Lengauer-Tarjan is unsafe for 25 GB+ dumps.
- Per candidate from section 4.1: bounded BFS, exclusively-reachable objects
  - ✅ `DominatorAnalyzer` implements bounded forward BFS (`BoundedRetainedSizeBfs.ComputeExclusiveRetained`) per top-N candidate; depends on Phase 1 index being present
- Exclusive retained bytes: memory freed if this object were removed
  - ✅ Computed per candidate; stored in `TypeSnapshot.EstimatedRetainedBytes` inside `DominatorDomainResult.TopDominatorTypes`; marked `HeuristicOnly = true`
- Dominator impact score = exclusive retained / total heap x 1000 (per-mille)
  - ⚠️ Not pre-computed as a per-mille field; retained bytes are available; section builder can derive `(EstimatedRetainedBytes / TotalHeapBytes) * 1000`
- Top 15 by exclusive retained bytes
  - ✅ `DominatorDomainResult.TopDominatorTypes` sorted by `EstimatedRetainedBytes` descending; top-N cap via `TopHighlyReferencedObjectsToShow` (default 15-20)
- Overlapping reachable sets flagged as shared dominators
  - ❌ Not implemented; would require comparing reachable sets across candidates

## 4.3 Retention Patterns

Pattern-matched from section 4.1, cross-referenced with analyzer outputs:
- Cache chains: `Dictionary`/`ConcurrentDictionary` -> Gen2 value chain
  - ⚠️ Partially available: `StaticRootLeakDetector` surfaces `RetentionPatternHints` (planned, not yet emitted) via type-name pattern matching; `CollectionAnalyzer` covers over-capacity collections
  - No structured "cache chain" record with root type + chain depth + retained bytes
- Event chains: `EventHandler`/`Delegate` target lists (from `EventLeakAnalyzer`)
  - ⚠️ `EventLeakAnalyzer` exists and detects event leaks; its results are not cross-referenced into a unified retention pattern record for this section
- Static chains: static field root -> long object chain (from `StaticRootLeakDetector`)
  - ✅ `StaticRootLeakDetector` → `StaticRootDomainResult`: `SignificantRootCount`, `TotalRetainedBytes`, `TopRootsByBytes` (`IReadOnlyList<NameBytesEntry>`)
  - ⚠️ Chain depth is not captured; only total memory impact per root
- Thread-local chains: `ThreadLocal<T>` holding Gen2 objects
  - ❌ No dedicated detection; would require type-name pattern match on `ThreadLocal` in root walk results
- Finalizer chains: queued objects retaining large sub-graphs
  - ⚠️ `FinalizableObjectAnalyzer` covers finalizer queue depth and top types; bounded BFS retained bytes per queued object not computed
- Per pattern: root type, chain depth, total retained bytes
  - ❌ Unified pattern record with all three fields does not exist; assembly required from multiple analyzers in section builder

---

# 5. GC Root Intelligence

## 5.1 Root Distribution

Via `ClrHeap.EnumerateRoots()` and `ClrRuntime.EnumerateHandles()`:

| Root Kind | Source API | Shallow Size Retained |
|---|---|---|
| Static fields | `ClrStaticField` | Yes |
| Stack variables | `ClrRoot` (stack) | Yes (shallow) |
| Strong GC handles | `ClrHandle` (Strong, Pinned, RefCounted) | Yes |
| Weak GC handles | `ClrHandle` (Weak, WeakLong) | counted only |
| Finalizer queue | `ClrRoot` (finalizer) | Yes |
| Dependent handles | `ClrHandle` (Dependent) | Yes |

Total memory retained per root kind; root kind count distribution.
- `GCRootAnalyzer` → `GCRootDomainResult.ByKind` (`IReadOnlyList<RootKindSummary>`): Kind, Count, EstimatedRetainedBytes, PctOfManagedHeap ✅
  - ⚠️ `EstimatedRetainedBytes` is an AVERAGE-SIZE estimate (avg size of target type × count from `TypeAggregateIndexEntry`), not per-root BFS retained; may over/under-count if type sizes vary widely
  - ✅ Root kinds enumerated from Phase 1 root index built via `heap.EnumerateRoots()` + `runtime.EnumerateHandles()`

## 5.2 Root Severity Ranking

- Top 20 roots by shallow size of directly reachable objects
  - ✅ `GCRootDomainResult.TopRootsBySeverity` (`IReadOnlyList<RootFinding>`): RootKind, RootAddress, FieldDescription, TargetTypeName, TargetAddress, EstimatedRetainedBytes, SeverityScore
  - ⚠️ Severity bands (Critical >100 MB / Warning 10-100 MB / Info <10 MB) implemented in `GCRootAnalyzer.ComputeSeverity()` — verify thresholds match spec
- Each entry: root kind, declaring type/field name, object type, retained bytes
  - ⚠️ `FieldDescription` is nullable; populated only when field metadata is available from root record
- Severity: Critical > 100 MB / Warning 10-100 MB / Info < 10 MB ✅
- Finalizer roots with large retained sets flagged separately
  - ⚠️ Finalizer roots appear in `ByKind` with kind string; no separate dedicated flag in domain result — section builder must filter by kind

## 5.3 Root Paths

Via `BoundedRootPathFinder` (BFS, depth <= 20, `HashSet<ulong>` visited):
- Root -> object chains for top types from section 6.1
  - ⚠️ `GCRootDomainResult.RootPaths` (`IReadOnlyList<RootPathFinding>`) contains paths for top-severity roots (§5.2), NOT for top leak types from §6.1; section builder must correlate or `GCRootAnalyzer` must be seeded with §6.1 candidates
  - ✅ `PathSearchCapped` and `PathSearchCappedCount` surfaced in domain result
- Format: `[RootKind] RootType.FieldName -> TypeA -> TypeB -> ... -> LeakCandidate`
  - ⚠️ `RootPathFinding` stores `PathTypeNames` (list of type name strings) + `RootKind`; arrow-formatted string must be assembled in section builder
- Max 3 paths per type (shortest first)
  - ❌ No per-type multi-path grouping; `RootPaths` is a flat list per target address; section builder must group
- Paths through `object[]`, `List<T>` annotated as indirect
  - ❌ No indirect-path annotation in `RootPathFinding`
- Depth-limit hits marked [TRUNCATED]
  - ✅ `RootPathFinding.WasCapped` flag available; section builder must append `[TRUNCATED]`

---

# 6. Leak Analysis

## 6.1 Leak Candidates

Suspicion score (0-100):

| Signal | Score | ClrMD Source |
|---|---|---|
| > 80 % instances in Gen2 | +30 | `ClrSegment` generation correlation |
| Type total size > 100 MB | +20 | Heap index aggregate |
| Instance count growing (trend mode) | +15 | `TrendAnalyzer` delta |
| `IsFinalizable` + Gen2 count > 1000 | +15 | `ClrType.IsFinalizable` |
| Reachable from static root | +10 | `ClrStaticField` traversal |
| Reachable from strong/pinned GC handle | +10 | `ClrRuntime.EnumerateHandles()` |
| Known container type | +5 | Type name pattern |
| High reference field density (section 3.3) | +5 | `ClrType.Fields` |

Top 30 by score. Per entry: type name, score, total size, instance count, Gen2 %, root kind.
- ✅ `LeakCandidateAnalyzer` implemented; `LeakCandidateDomainResult` contains `TotalCandidates`, `TopCandidates: IReadOnlyList<LeakCandidateRecord>`, `CandidatesByClass: IReadOnlyDictionary<LeakClass, int>`
- `LeakCandidateRecord` fields: `TypeName`, `TotalSize`, `InstanceCount`, `Gen2Pct`, `SuspicionScore`, `Severity`, `Classification` (`LeakClass`), `RootKind`, `IsFinalizable`, `IsContainer`, `ReferenceFieldRatio`
- Signal assembly (all inputs now joined in a single type-keyed pass — no heap scan):
  - Gen2 pct: from Phase 1 `TypeAggregateIndexEntry.Gen2Count` ✅
  - Type total size >100 MB: `TypeAggregateIndexEntry.TotalSize` ✅
  - Trend delta: `TrendAnalyzer` delta — only in trend/compare mode ⚠️
  - `IsFinalizable` + Gen2 count: `TypeAggregateFlags.IsFinalizableType` + `TypeAggregateIndexEntry.Gen2Count` ✅
  - Static root reachability: `cache.GetStaticRootedAddresses(heap)` hash set ✅
  - Strong/pinned handle: scan of `runtime.EnumerateHandles()` keyed by target type name ✅
  - Dependent handle: same handle scan ✅
  - Container type: type-name pattern match ✅
  - High ref density: `TypeShapeEntry.RefFields / TotalFields` from Phase 1 shape cache ✅

## 6.2 Leak Classification

| Class | Detection | Pattern |
|---|---|---|
| `StaticRetention` | `ClrStaticField` root -> candidate | Static field holding growing container |
| `EventLeak` | `Delegate._invocationList` -> candidate | Long-lived publisher, non-disposable subscriber |
| `CacheLeak` | Known cache type in Gen2, no eviction | Container grows unbounded |
| `ThreadLocalLeak` | `ThreadLocal<T>._linkedSlot` -> candidate | Thread-static holding Gen2 objects |
| `FinalizerRetention` | Candidate in finalizer queue | Object in queue retaining sub-graph |
| `GCHandleRetention` | `ClrHandle` (Strong/Pinned/RefCounted) -> candidate | Explicit handle blocking collection |
| `DependentHandleLeak` | `ClrHandle` (Dependent) source alive, target grown | `ConditionalWeakTable` keeping target alive |
| `Unknown` | Reachable from root, pattern unrecognised | Manual investigation required |

- `EventLeak`: ✅ `EventLeakAnalyzer` → `EventLeakDomainResult` (per-event group, subscriber count, estimated retained bytes, publisher type, field name)
- `StaticRetention`: ✅ cross-referenced in `LeakCandidateAnalyzer` via `cache.GetStaticRootedAddresses(heap)`; `LeakClass.StaticRetention` assigned when sample address is in static root set
- `GCHandleRetention`: ✅ `LeakCandidateAnalyzer` scans `runtime.EnumerateHandles()` for Pinned targets; `LeakClass.GCHandleRetention` assigned per type
- `DependentHandleLeak`: ✅ same handle scan; `LeakClass.DependentHandleLeak` assigned for Dependent handle targets
- `CacheLeak`: ✅ type-name pattern match (`Cache`, `Dictionary`, `ConcurrentDictionary`, `Queue`) + Gen2 > 50 % in `LeakCandidateAnalyzer.Classify()`
- `ThreadLocalLeak`: ✅ type-name pattern match (`ThreadLocal`) in `LeakCandidateAnalyzer.Classify()`
- `FinalizerRetention`: ✅ `IsFinalizable` flag from `TypeAggregateFlags` in `LeakCandidateAnalyzer`
- Unified `LeakClass` enum and classified candidate list: ✅ `LeakClass` enum in `LeakCandidateDomainResult.cs`; `CandidatesByClass` dict and per-record `Classification` field in `LeakCandidateRecord`

## 6.3 Leak Explanation

Per candidate, template-based explanation parameterised with `ClrType.Name`, `ClrField.Name`, `ClrStaticField.Name`:
- Root cause sentence
- Evidence list: root kind, declaring type, field name, path depth, retained bytes
- Corroborating signals: finalizer queue, high Gen2 %, thread-static
- One template per class from section 6.2
- ❌ No template-based explanation infrastructure exists; each section builder generates its own prose; parameterised per-class templates need to be built in section builder or a dedicated `LeakExplainer` helper

## 6.4 Leak Impact

- Memory: shallow + estimated retained, % of total heap
  - ✅ Shallow size from `LeakCandidateRecord.TotalSize`; `% of heap` computed in `LeakAnalysisSectionBuilder` from `memory.TotalBytes`; estimated retained derivable from `DominatorDomainResult.TopDominatorTypes.EstimatedRetainedBytes` (same type key)
- GC: finalizable leaks force two-pass collection
  - ⚠️ `FinalizableObjectAnalyzer.FinalizerQueueDepth` available; two-pass GC impact text is section-builder prose
- Fragmentation: Gen2 blocks compaction; LOH leaks fragment LOH
  - ⚠️ Gen2 bytes from `GCGenerationDomainResult`; LOH bytes from `SegmentAnalysisDomainResult`; no fragmentation score computed
- Thread: finalizer queue backlog starves finalizer thread
  - ⚠️ `FinalizableObjectAnalyzer.FinalizerQueueDepth` + `ThreadDomainResult.FinalizerThreadBlocked` ✅ for signal; section builder assembles text
- Stability risk: Low (< 50 MB) / Medium (50-500 MB) / High (500 MB-2 GB) / Critical (> 2 GB)
  - ✅ `LeakAnalysisSectionBuilder.GetImpactBand(TotalSize)` maps shallow size to Low/Medium/High/Critical bands matching spec thresholds

---

# 7. Thread & Concurrency Analysis

## 7.1 Thread Lifecycle

- Total / alive / inactive / background thread counts
  - ✅ `ThreadDomainResult`: `TotalThreadCount`, `AliveThreadCount`, `InactiveThreadCount`, `BackgroundThreadCount`
- GC thread count; finalizer thread blocked status
  - ✅ `GcThreadCount`, `FinalizerThreadBlocked`, `FinalizerManagedThreadId`, `FinalizerOsThreadId`, `FinalizerLockCount`, `FinalizerFrames`
- Async chain threads (`AsyncChainThreadCount`), max async chain depth
  - ✅ `AsyncChainThreadCount`, `MaxAsyncChainDepth` in `ThreadDomainResult`
- Thread pool via `ClrRuntime.ThreadPool`: `MinThreads`, `MaxThreads`, `ActiveWorkerThreads`, `IdleWorkerThreads`, `RetiredWorkerThreads`, `QueueLength`, `CpuUtilization`
  - ✅ `HangAnalyzer.ReadRuntimeThreadPool()` reads all stable fields directly from `ClrRuntime.ThreadPool` into `HangDomainResult`: `RuntimeMinThreads`, `RuntimeMaxThreads`, `RuntimeActiveWorkerThreads`, `RuntimeIdleWorkerThreads`, `RuntimeRetiredWorkerThreads`, `RuntimeCpuUtilization`, `RuntimeThreadPoolDataAvailable` (= `RuntimeInitialized`)
  - ✅ `QueueLength` is now probed via reflection (`GetIntProperty(tp, "QueueLength")`) and surfaced as nullable `HangDomainResult.RuntimeQueueLength`; when unavailable on a given ClrMD build, reporting falls back to the queued-work-items proxy
  - `ThreadDomainResult.ThreadPoolWorkerCount` is a single int (total count only)
- Starvation flag: `QueueLength > 0` AND `ActiveWorkerThreads == MaxThreads`
  - ✅ `HangDomainResult.IsStarved` now emitted; computed from runtime queue length when present (`RuntimeQueueLength > 0 && Active >= Max`)
- Per-thread stack size: `ClrThread.StackBase - ClrThread.StackLimit`
  - ✅ `ThreadStateSnapshot.StackSizeBytes` (ulong) computed from `StackBase - StackLimit`; rendered in `ThreadConcurrencySectionBuilder` and `ThreadSectionBuilder`

## 7.2 Synchronization Patterns

- Wait categories: `MonitorWait`, `MonitorContention`, `TaskBlocking`, `Sleep`, `Semaphore`, `Mutex`, `WaitHandle`, `ThreadJoin`, `BlockingIO`
  - ✅ `ThreadDomainResult.WaitPatternBreakdown` (`IReadOnlyDictionary<string, int>`); `ThreadAnalyzer.WaitPatterns` array defines all 9 categories with the same names
- Top 10 blocked threads: OS thread ID, wait category, wait reason, lock count, top frame
  - ✅ `ThreadDomainResult.TopBlockedThreads` (`IReadOnlyList<ThreadStateSnapshot>`): `OSThreadId`, `WaitCategory`, `WaitReason`, `LockCount`, `TopFrames`
- Top 10 lock-holding threads: lock count, GC mode, top frames
  - ✅ `ThreadDomainResult.TopLockedThreads` (`IReadOnlyList<ThreadStateSnapshot>`): `LockCount`, `GcMode`, `TopFrames`
- Frame hotspots: top 10 frames across all blocked threads by frequency
  - ✅ `ThreadDomainResult.TopStackHotspots` (`IReadOnlyList<NameCountEntry>`)
- `GcMode` distribution: Cooperative vs Preemptive
  - ✅ `ThreadDomainResult.GcModeDistribution` (`IReadOnlyDictionary<string, int>`)

## 7.3 Deadlock Detection

Via `LockGraphAnalyzer` from `ClrThread.BlockingObjects`:
- Directed wait-for graph: thread -> lock -> owner thread
  - ✅ `LockGraphAnalyzer` builds a graph from `heap.EnumerateSyncBlocks()` correlating lock holders and waiters
- DFS cycle detection
  - ✅ Deadlock candidates detected; `LockGraphDomainResult.DeadlockCandidateCount`
- Per cycle: full thread chain, lock addresses, owning type names
  - ✅ `LockGraphDomainResult.DeadlockCandidates` (`IReadOnlyList<DeadlockCandidateSnapshot>`): `ManagedThreadId`, `OsThreadId`, `LockObjectTypes` (type names), `LockObjectAddresses` (ulong list), `CycleSummary`
  - ✅ Lock addresses now included in `DeadlockCandidateSnapshot.LockObjectAddresses`
- Suspected deadlock (no confirmed cycle): mutual lock holding flagged
  - ⚠️ `LockGraphDomainResult.ContestedLocks` (`IReadOnlyList<ContestedLockSnapshot>`) covers threads waiting on contested monitors; mutual-holding without confirmed cycle not separately flagged

---

# 8. Async & Task Analysis

## 8.1 Task Summary

Via `HangAnalyzer`, `Task`/`Task<T>` heap inspection:
- Total `Task` objects
  - ✅ `AsyncTaskDomainResult.TotalTasks` (dedicated `AsyncTaskAnalyzer` — separate from `HangAnalyzer`)
- Status breakdown: `Pending` / `Running` / `Faulted` / `Canceled` / `RanToCompletion`
  - ✅ `AsyncTaskDomainResult`: `PendingTasks`, `RunningTasks`, `FaultedTasks`, `CanceledTasks`, `CompletedTasks` (CompletedTasks = RanToCompletion)
- `QueuedWorkItems` from `ClrRuntime.ThreadPool.QueueLength`
  - ⚠️ `HangDomainResult.QueuedWorkItems` exists but sourced from heap task scan, not `ClrRuntime.ThreadPool.QueueLength`; actual `QueueLength` not read (see §7.1 gap)
- `TotalTaskContinuations`: sum of non-null `m_continuationObject` fields
  - ✅ `AsyncTaskDomainResult.TotalTaskContinuations` now a field; computed by `AsyncTaskAnalyzer` during task scan
- `RuntimeThreadPoolDataAvailable` flag
  - ⚠️ In `HangDomainResult` as `RuntimeInitialized`; not in `AsyncTaskDomainResult`

## 8.2 Orphaned Tasks

Detection: `m_continuationObject == null`, status = `RanToCompletion` or `Faulted`:
- Faulted + no continuation: unobserved exception
  - ✅ `AsyncTaskDomainResult.OrphanedTasks` (count); `TopOrphanedTasks` (`IReadOnlyList<OrphanedTaskSnapshot>`): Address, TaskType, ResultType, Size
  - `TopFaultedTaskTypes` lists faulted task type names by count ✅
- Completed + no continuation + not in stack roots: fire-and-forget
  - ⚠️ `OrphanedTasks` count captures both faulted and completed orphans; not split by sub-category
- Top 10 orphaned faulted tasks: address, exception type, exception message
  - ✅ `OrphanedTaskSnapshot` now includes `ExceptionType` (nullable string) and `ExceptionMessage` (nullable string); populated for faulted orphaned tasks

## 8.3 Continuation Chains

Via `m_continuationObject` chain traversal:
- `MaxAsyncChainDepth`, `AsyncChainThreadCount`
  - ✅ `AsyncTaskDomainResult.MaxContinuationDepth`; `ThreadDomainResult.AsyncChainThreadCount`
- Top 5 deepest chains: root task type -> continuation type sequence
  - ✅ `AsyncTaskDomainResult.TopDeepestChains` (`IReadOnlyList<ContinuationChainSnapshot>`): `RootAddress`, `RootType`, `Depth`, `ChainTypes` (ordered type-name sequence); sorted by `Depth` descending
- Depth > 50 flags state machine leak or unbounded continuations
  - ✅ `AsyncAnalysisSectionBuilder` checks `MaxContinuationDepth > 50` and emits a warning block
- `TopContinuationTypes` (from `HangDomainResult`)
  - ✅ Also present in `AsyncTaskDomainResult.TopContinuationTypes` (same data, from `AsyncTaskAnalyzer`)

---

# 9. GC & Allocation Pressure

## 9.1 Allocation Patterns

Via `ClrSegment` generation correlation:
- Gen0 (short-lived): count, size, top 10 types
  - ✅ `GCGenerationDomainResult`: `Gen0Objects`, `Gen0Bytes`; per-type gen counts via `PerTypeGenerationProfiles` (`TypeGenerationProfile.Gen0Count`)
  - ⚠️ No dedicated `TopGen0Types` list; section builder must filter `PerTypeGenerationProfiles` by Gen0Count
- Gen1 (medium-lived): top 10 types
  - ⚠️ Same as Gen0 — derivable from `PerTypeGenerationProfiles.Gen1Count`; no pre-built top-N list
- Gen2/LOH (long-lived): top 10 by count and size
  - ✅ `GCGenerationDomainResult.TopLohTypes`; Gen2 top types derivable from `PerTypeGenerationProfiles`
- Survival ratio per type: Gen2 count / total count (near 1.0 = permanent)
  - ⚠️ Derivable from `TypeGenerationProfile` fields but not pre-computed as `SurvivalRatio`
- Allocation pressure: ephemeral segment fill % above 80 % = imminent Gen0 GC
  - ⚠️ `SegmentReservationDomainResult.AvgEphemeralFillPct` available; `HeapSegmentSnapshot` now has `UsedBytes` field; `FillPct` in `SegmentReservationEntry` is computed as `CommittedMemory / Length` (proxy for fill, not `UsedBytes / CommittedBytes`)
- Allocation density: objects per KB of total size
  - ❌ Not computed anywhere
- Size histogram: < 64 B / 64-256 B / 256 B-1 KB / 1-85 KB / > 85 KB
  - ✅ `MemoryDomainResult.SizeBucketHistogram` (`IReadOnlyList<SizeBucketEntry>`: RangeLabel, ObjectCount, TotalBytes); 8 buckets via `SizeBucketHelper`
  - ⚠️ Bucket labels are code-defined; verify they match spec exactly

## 9.2 GC Efficiency

- Promotion rate per type: Gen1 / (Gen0+Gen1+Gen2)
  - ⚠️ Derivable from `TypeGenerationProfile` but not pre-computed; section builder must compute per type
- Gen2 accumulation rate: Gen2 / total count
  - ⚠️ Same — derivable, not pre-computed
- Finalizable Gen2 overhead: count and size of `IsFinalizable` types in Gen2
  - ⚠️ `TypeAggregateFlags.IsFinalizable` flag + `Gen2Count` in `TypeAggregateIndexEntry` exist; not pre-aggregated into a scalar; section builder must filter and sum via `GCGenerationDomainResult.PerTypeGenerationProfiles`
- Segment utilisation: `UsedBytes / CommittedMemory` per segment
  - ✅ `HeapSegmentSnapshot` now has `UsedBytes` and `CommittedBytes` fields; `SegmentAnalyzer` computes `UsedBytes` from `ClrSegment`; `SegmentAnalysisDomainResult.TotalUsedBytes` aggregated
- Committed vs reserved gap (`ClrSegment.CommittedMemory` vs `ClrSegment.ReservedMemory`)
  - ✅ `HeapSegmentSnapshot.ReservedBytes` now stored; `SegmentAnalysisDomainResult.TotalReservedBytes` and `ReservationGapBytes` available; per-segment in `SegmentReservationEntry.ReservedBytes`
- Cross-heap distribution (Server GC): count and bytes per logical heap; skew > 2x = affinity problem
  - ✅ `SegmentAnalysisDomainResult.PerLogicalHeapSummaries` now implemented (see §2.1); rendered in `MemoryTopologySectionBuilder` and `HeapSegmentDiagnosticsSectionBuilder`
- Compaction blockage: pinned handle count (section 9.3) + POH object count (section 10.4)
  - ⚠️ Pinned handle count: `GCHandleDomainResult.PinnedHandleTargets` ✅; POH object count: `SegmentAnalysisDomainResult.KindSummaries` (Pinned kind) ✅; combined score/metric not computed

## 9.3 Pinning Impact

Via `GCHandleAnalyzer` (`ClrHandle.Kind = Pinned`) and `SegmentAnalyzer`:
- Total pinned handle count; top pinned target types
  - ✅ `GCHandleDomainResult.PinnedHandleTargets` (count); `TopPinnedTargetTypes` (`IReadOnlyList<NameCountEntry>`); `TopPinnedObjectsBySize` (`IReadOnlyList<NameBytesEntry>`)
- Gen0/Gen1 pinned objects: most disruptive to compaction (correlate `ClrHandle` address with `ClrSegment` generation)
  - ❌ Not computed; requires correlating pinned handle target addresses with segment generation ranges — no infrastructure for this exists
- Clustering: concentrated vs spread across segments
  - ❌ Not computed
- POH vs GC-handle pinning comparison
  - ⚠️ `SegmentAnalysisDomainResult.PohBytes` and `GCHandleDomainResult.PinnedRetainedBytes` both available; not cross-referenced in any model; section builder must join
- Estimated gap bytes from pinned object compaction blockage
  - ❌ `PinnedRetainedBytes` approximates total pinned size, not compaction gap (which would require segment layout analysis)

---

# 10. LOH / POH / FOH Diagnostics

## 10.1 LOH Summary

- Total LOH size, segment count, object count
  - ✅ `SegmentAnalysisDomainResult.LohBytes`, `LohSegmentCount`; object count via `KindSummaries` (LOH `SegmentKindSummary.ObjectCount`)
- Top LOH types by size and count (`GCGenerationDomainResult.TopLohTypes`)
  - ✅ `GCGenerationDomainResult.TopLohTypes` (`IReadOnlyList<TypeGenerationProfile>`)
- Flag types just over 85 000 B threshold
  - ❌ Not computed; would require checking average object size vs 85,000 B threshold per type in `TypeAggregateIndexEntry`

## 10.2 LOH Fragmentation

From `LohFragmentationDomainResult`:
- Per-segment: `FreeBytes / TotalBytes x 100`
  - ✅ `LohFragmentationDomainResult.FragmentationPercent` (global); `TopFragmentedSegments` (`IReadOnlyList<LohSegmentSnapshot>`) for per-segment details
- Free block count; largest free block size
  - ✅ `FreeBlockCount`, `LargestFreeBlock`
- Top 5 most fragmented segments: address, free bytes, largest contiguous free block
  - ✅ `TopFragmentedSegments`; `FreeGapHistogram` (`IReadOnlyList<FreeGapBucket>`) for gap-size distribution
- Severity: Critical > 60 % / Warning 30-60 % / OK < 30 %
  - ❌ Severity band not pre-computed in `LohFragmentationDomainResult`; section builder must apply thresholds to `FragmentationPercent`

## 10.3 Large Object Lifetimes

- Long-lived LOH objects (Gen2, no finalizer)
  - ❌ No per-object LOH lifetime data; only per-type aggregates from `GCGenerationDomainResult.TopLohTypes`
- Top 10 largest LOH objects: address, type, size
  - ❌ Not captured; would require a heap scan filtered to LOH segments — expensive on large dumps
- Arrays > 1 MB: element type, length, size
  - ❌ No dedicated detection; array element type and length not in any current model

## 10.4 POH Diagnostics

Via `HeapSegmentKind.PinnedObjectHeap`:
- POH segment count, total size, object count
  - ✅ `SegmentAnalysisDomainResult.PohSegmentCount`, `PohBytes`; object count via `KindSummaries` (PinnedObjectHeap)
- Top POH types by size
  - ❌ No type distribution for POH; `SegmentAnalyzer` classifies segments by kind but does not enumerate POH type aggregates
- Flag long-lived POH objects no longer referenced by native code
  - ❌ Native reference tracking not available via ClrMD; would require P/Invoke stub inspection
- POH size vs GC-handle-pinned size comparison (section 9.3)
  - ⚠️ Both values available (`PohBytes` + `GCHandleDomainResult.PinnedRetainedBytes`); comparison must be assembled in section builder

## 10.5 FOH Diagnostics

Via `HeapSegmentKind.Frozen`:
- FOH segment count, total size, object count
  - ✅ `SegmentAnalysisDomainResult.FrozenSegmentCount`, `FrozenBytes`; object count via `KindSummaries` (Frozen)
- Top FOH types by count (typically `System.String`, `System.Byte[]`, `FrozenDictionary` internals)
  - ❌ No FOH type distribution; `SegmentAnalyzer` does not enumerate frozen segment objects by type
- Large FOH signals over-use of `RuntimeHelpers.GetUninitializedObject`, `MemoryMarshal`, or frozen collections
  - ❌ No pattern detection; would require FOH type enumeration (small scan — FOH segments are typically tiny)

---

# 11. String & Data Analysis

## 11.1 Duplicate Strings

Via `ClrType.IsString`; dedup by value hash:
- Total `System.String` count and bytes
  - ✅ `StringDomainResult.TotalStrings`, `TotalStringMemoryBytes`
- Unique string count
  - ✅ `StringDomainResult.UniqueStrings`
- Duplication ratio: `(total - unique) / total` (> 0.5 = heavy redundancy)
  - ✅ `StringDomainResult.DuplicationRatio`
- Top 20 duplicates: preview (first 80 chars), duplicate count, wasted bytes = `(count-1) x ClrObject.Size`
  - ✅ `StringDomainResult.TopDuplicatesByWaste` (`IReadOnlyList<DuplicateStringSnapshot>`): Preview, Count, WastedBytes, TotalSize, SampleAddresses
- Length histogram: < 16 / 16-64 / 64-256 / 256-1 KB / 1-85 KB / > 85 KB
  - ✅ `StringDomainResult.Distribution.LengthBuckets` (from `DistributionSummary`)
  - ⚠️ Bucket labels are code-defined in `StringAnalyzer`; verify they match the spec labels exactly
- Very long strings (> 85 KB): address, length, size
  - ✅ `StringDomainResult.VeryLongStrings` (`IReadOnlyList<LongStringEntry>`): Address, CharLength, SizeBytes
- Interned strings (FOH): count and size
  - ✅ `InternedStringCount`, `InternedStringBytes` (derived from FOH segment scan)
- Strings in Gen2: count and size
  - ✅ `Gen2StringCount`, `Gen2StringBytes`

## 11.2 Memory Waste

- Total duplicate waste bytes
  - ✅ `StringDomainResult.DuplicateWastedBytes`
- LOH string pressure: total size of strings > 85 KB
  - ✅ `StringDomainResult.LohStringBytes`
- ASCII-only strings stored as UTF-16 (encoding waste)
  - ❌ Not detected; would require reading string char content during dedup pass — significant I/O cost on large dumps
- Estimated saving from interning top-20 duplicates (caveat: interned strings never collected)
  - ❌ Not pre-computed; section builder can sum `WastedBytes` from `TopDuplicatesByWaste.Take(20)` as an approximation

Recommendations per finding:
- ⚠️ Recommendation text is prose generated in section builder; no structured `Recommendation` type or template registry exists

---

# 12. Event & Delegate Analysis

## 12.1 Subscription Graph

Via `ClrType.Fields` on `Delegate`/`MulticastDelegate`:
- `_target` (`ClrInstanceField`): subscriber object
- `_invocationList` (`ClrInstanceField`): `object[]` of all subscribers
- `_invocationCount`: subscriber count
  - ✅ `EventLeakAnalyzer` traverses `_invocationList` and counts subscribers per instance
- Per event field on heap: publisher type + address, subscriber count, top subscriber types, shallow size of reachable subscriber objects
  - ✅ `EventLeakGroupSnapshot`: `PublisherType`, `EventFieldName`, `InstanceCount`, `TotalSubscribers`, `TopSubscriberTypes`, `EstimatedSubscriberRetainedBytes`
  - ✅ `EventLeakInstanceSnapshot` has per-instance publisher address (`PublisherAddress`)
- Top 20 publisher types by total subscriber count
  - ✅ `EventLeakDomainResult.TopPublisherEvents` (`IReadOnlyList<PublisherEventSummary>`): PublisherType, EventFieldName, TotalSubscribers, InstanceCount, EstimatedRetainedBytes; capped at top 50

## 12.2 Event Leaks

Leak condition: publisher GC-rooted AND subscriber `_target` in Gen2 with no other strong root:
- Retained subscriber count and bytes
  - ✅ `EventLeakDomainResult.TotalSubscribers`; per-group `EstimatedSubscriberRetainedBytes` in `EventLeakGroupSnapshot`
- Publisher lifetime: Gen0/1 (short-lived) vs Gen2/static (long-lived)
  - ⚠️ `EventLeakInstanceSnapshot.PublisherGeneration` field available at instance level (int, -1 if unknown); not propagated up to `EventLeakGroupSnapshot`; section builder must aggregate from instances
- Static event fields: `ClrStaticField` + type name containing `EventHandler` -> any subscriber retained indefinitely
  - ✅ `EventLeakDomainResult.StaticEventLeakCount`; `EventLeakGroupSnapshot.IsStatic` flag
- Per publisher: event field name, subscriber count, retained bytes, severity
  - ✅ `EventLeakGroupSnapshot`: EventFieldName, TotalSubscribers, EstimatedSubscriberRetainedBytes, SeverityScore; also HasDuplicateSubscriptions, HasLifetimeMismatch, OrphanedSubscriberInstances

---

# 13. Exception Analysis

## 13.1 Exception Frequency

- Most common exception types and counts
  - ✅ `CrashDomainResult.ExceptionTypeCounts` (`IReadOnlyDictionary<string, int>`) — all heap exceptions grouped by type
  - ✅ `CrashDomainResult.ActiveExceptionTypeCounts` — subset currently active on thread call stacks
  - ✅ `TotalExceptions`, `ActiveExceptions` scalar counts

## 13.2 Failure Hotspots

- Top 10 stack frames across threads with active exceptions: frame name, associated exception type, count
  - ⚠️ `CrashDomainResult.TopCrashThreadCandidates` (`IReadOnlyList<CrashThreadCandidateSnapshot>`): ThreadId, OSThreadId, PrimaryExceptionType, TopFrames, OriginalStackTrace
  - `CrashThreadCandidateSnapshot` is per-thread, not per-frame-frequency; no pre-aggregated frame hotspot count across all exception threads
  - `ThreadDomainResult.TopStackHotspots` gives frame frequency across ALL threads — section builder must filter to exception threads only
- Origin: `UserCode` / `FrameworkCode` (`System.*`/`Microsoft.*`) / `ThirdParty` (via `ModuleAnalyzer`)
  - ✅ `ExceptionAnalysisSectionBuilder.ClassifyFrameOrigin(frame, modules)` classifies each frame as `FrameworkCode`, `ThirdParty`, or `UserCode` using module inventory; per-thread origin breakdown table emitted
- InnerException chain depth histogram (depth > 5 flagged)
  - ✅ `ExceptionInstanceSnapshot.ChainDepth` computed by `CrashAnalyzer.ComputeExceptionChainDepth()` (recursive `_innerException` traversal); `ExceptionAnalysisSectionBuilder` renders a per-depth-value histogram from `TopExceptionInstances`

---

# 14. Temporal / Diff Analysis

Trend mode should read as a narrative of change rather than a copy of the single-dump report with extra deltas appended.

## 14.1 Trend Summary

Trend mode opens with:
- Dump count and snapshot order
  - ✅ `TrendReportComposer` populates `TrendDumpCount`, `TrendDumpPaths`, and `IncidentContext.TrendSnapshots`
- Finding lifecycle summary
  - ✅ `FindingLifecycleComparer.Compare()` produces `NewFindings`, `PersistentFindings`, and `ResolvedFindings`
- Executive summary deltas
  - ✅ `TrendReportComposer.ComputeTrendExecutiveSummary()` adds leak, GC pressure, and thread-contention deltas to the summary model
- Top regressions
  - ✅ `TrendReportComposer.BuildTopRegressionFindings()` surfaces the worst metric regressions as findings

## 14.2 Growth Trends

Via `TrendAnalyzer` + `AnalyzerTrendComparers` + `TrendReportComposer`:
- Per-type count delta and byte delta between snapshots
  - ✅ `MemoryAnalyzerTrendComparer.Compare()` emits `MetricDelta` per type: `type.bytes` and `type.count` keyed by type name; delta and delta-percent computed
  - ✅ `TrendReportComposer` assembles `AnalyzerTrendResult` lists into the diff report
- Top 20 by byte delta; top 20 by count delta
  - ⚠️ `MetricDelta` records are flat; top-N sorting is done in `TrendReportComposer`; capped to top types in `TopTypesBySize`/`TopTypesByCount` (default top 10 per snapshot), not full type universe
- New types in snapshot B absent from A
  - ✅ `TrendReportComposer.BuildNewTypes()` compares `MemoryDomainResult.TopTypesBySize` between baseline and latest snapshots and emits a dedicated `NEW TYPES (BASELINE → CURRENT)` block in the trend section
  - ⚠️ The comparison is capped to the top-N memory types retained in each snapshot, not the full type universe
- Classification: Stable (< 5 % delta) / Growing (5-50 %) / Exploding (> 50 %)
  - ✅ `TrendReportComposer` emits classification labels in the trend report text ("— Stable", "Growing", "Exploding")
  - ⚠️ Classification is applied in section builder prose, not as a typed enum field in the domain model

## 14.3 Regression Detection

- Types negligible in A but leak candidates in B (cross-ref section 6.1)
  - ✅ `TrendAnalyzer.ComputeNewLeakSignals()` now compares `LeakCandidateDomainResult.TopCandidates` by type across snapshots and emits `NewLeakSignal` entries under `"Leak Candidate Analysis"`
- `FindingLifecycleComparer`: new regressions (B not A) and resolved issues (A not B)
  - ✅ `FindingLifecycleComparer.Compare()` → `FindingLifecycleResult`: `NewFindings`, `PersistentFindings`, `ResolvedFindings` (`IReadOnlyList<InsightFinding>`) — based on finding fingerprint matching
- Severity escalations: Warning -> Critical between snapshots
  - ✅ `TrendReportComposer.BuildSeverityEscalations()` compares finding fingerprints between baseline and latest snapshots and emits a dedicated `SEVERITY ESCALATIONS` block for Warning -> Critical transitions
  - ⚠️ Current implementation checks baseline-vs-latest only; intermediate snapshot hops are not reported separately
- Requires `--compare` mode; skipped in single-dump runs
  - ✅ `TrendAnalyzer.CompareAll()` is only invoked in diff/compare mode; single-dump path is separate

---

# 15. Visualization

Trend visualization layer:
- Memory pie/bar: SOH/LOH/POH/FOH, Gen0/1/2 (sections 2.1-2.2)
  - ⚠️ Raw data available via `SegmentAnalysisDomainResult` and `GCGenerationDomainResult`; no dedicated visualization artifact produced — section builder would need to emit Chart.js data or SVG
- Type treemap: retained bytes per type (sections 3.1, 4.1)
  - ⚠️ `TypeSnapshot.EstimatedRetainedBytes` now populated for top types; shallow bytes available from `TypeAggregateIndexEntry`; no treemap artifact produced yet
- Retention graph: dominator candidates as Graphviz `.dot` or JSON adjacency list (section 4.2)
  - ⚠️ `DominatorDomainResult.TopDominatorTypes` with retained bytes now available; no `.dot` or adjacency-list artifact produced yet
- Thread timeline: states per thread ID (section 7)
  - ⚠️ Thread state data available in `ThreadDomainResult.ThreadStateDistribution`; no timeline artifact produced; `ThreadStackClusterAnalyzer` emits `thread-clusters.json` (`ReportArtifact`) which can be consumed as a grouping basis
- LOH fragmentation heatmap: segment ranges with free blocks (section 10.2)
  - ⚠️ `LohFragmentationDomainResult.TopFragmentedSegments` and `FreeGapHistogram` available; no heatmap artifact produced
- Leak score bar: suspicion scores per type (section 6.1)
  - ⚠️ `LeakCandidateDomainResult.TopCandidates` with `SuspicionScore` per type now available; no bar-chart artifact produced yet
- Diff waterfall: byte delta per type (section 14.1)
  - ✅ `TrendReportComposer` emits a chart-backed waterfall block for trend deltas
- `ReportArtifact` system exists and is used by `StringAnalyzer`, `WeakReferenceAnalyzer`, `ThreadStackClusterAnalyzer` (JSON/NDJSON.gz exports) — trend visuals should follow the same pattern when a data payload is cheaper than recomputing in the renderer

---

# 16. Insights & Recommendations

## 16.1 Findings

Via `InsightEngine` across all `AnalyzerRunResult[]`:
- Ranked: Critical -> Warning -> Info
  - ✅ `InsightEngine.Rank()` sorts findings by `FindingSeverity` (Critical > Warning > Info)
- Each finding: `Source`, `Title`, `Detail`, `Severity`, `ConfidenceScore` (0.0-1.0), `Caveats[]`
  - ✅ `InsightFinding` record has: `Analyzer` (= Source), `Title`, `Evidence` (= Detail), `Severity`, `Recommendation`, `Tags`, `MetricValue`, `MetricUnit`, `Fingerprint`
  - ✅ `InsightFinding.ConfidenceScore` (nullable double) added; defaults to severity-based value (Critical=0.9, Warning=0.7, Info=0.5) when not explicitly set; `EffectiveConfidenceScore` always non-null
  - ✅ `InsightFinding.Caveats` is now `IReadOnlyList<string>?`; serialized output carries `FindingRecord.CaveatItems`
- Cross-analyzer correlations (e.g., high LOH % + high pinned handle count -> compaction blocked)
  - ✅ `InsightEngine` has cross-correlation methods: `DetectAllocationPressureCrossCorrelation()`, `DetectBoxingGCCorrelation()`, and others that join results from multiple analyzers into single findings
- >= 3 failed analyzers = Warning finding
  - ✅ `InsightEngine.DetectAnalyzerFailures()` emits a Warning finding with `Tags: ["analysis-quality", "failed-analyzer"]` when `failCount >= 3` (threshold = `AnalyzerFailureWarning = 3`)
- Critical/Warning provenance: source analyzer, metric key, address list, artifact path, snapshot index
  - ✅ Serialized findings now populate `FindingRecord.EvidenceRefs` with `Analyzer` plus best-effort `MetricKey`; single-dump serialization also attaches analyzer artifact file paths when available, and trend serialization adds `SnapshotIndex`
  - ⚠️ Address lists are still not carried by `InsightFinding`, so `EvidenceRef.Addresses` remains null in the current pipeline; metric keys are heuristic (derived from tags), not analyzer-authored

## 16.2 Root Cause Narratives

Per Critical/Warning finding:
- Cause: specific pattern detected
- Effect: measured impact (retained bytes, GC impact)
- Evidence chain: contributing findings with section refs
- Confidence: High (>= 0.8) / Medium (0.5-0.8) / Low (< 0.5)
- ❌ No `RootCauseNarrative` type or template infrastructure exists; `InsightFinding.Evidence` is a single freeform string; multi-step evidence chains with section refs not modeled
- ✅ `InsightFinding.ConfidenceScore` (nullable double) now available; `EffectiveConfidenceScore` uses severity-based default when not set
- ⚠️ Individual findings include `Recommendation` text and `Evidence` string — these serve as the narrative substrate; structured narrative assembly requires section-builder work

## 16.3 Suggested Fixes

| Leak Type | Fix | Difficulty |
|---|---|---|
| Cache leak | Add eviction (`MemoryCache` with size limit, `WeakReference` values) | Medium |
| Event leak | Unsubscribe in `Dispose`, use `WeakEventManager` or `IObservable` | Medium |
| Static root | Review static field lifetime; scoped DI registration | Hard |
| LOH pressure | `ArrayPool<T>`, `RecyclableMemoryStream` | Easy |
| Thread pool starvation | Remove `.Result`/`.Wait()`; `async`/`await` throughout | Hard |
| Pinning fragmentation | Migrate to POH or `MemoryPool<T>` | Medium |
| Finalizer backlog | Implement `IDisposable`, `GC.SuppressFinalize` | Easy |

Each fix includes owner/team, effort, validation step, and tracking status.
- ⚠️ Fix text for each leak type is emitted as prose in individual section builders (e.g., `EventLeakSectionBuilder`, `LohFragmentationSectionBuilder`) via `InsightFinding.Recommendation`
- ✅ Serialized findings (`FindingRecord`) now carry `SuggestedOwner`, `Effort`, `ValidationStep`, and `TrackingStatus`; values are populated in `ReportSerializer` from finding category/severity heuristics
- ⚠️ These fields exist in the serialized/report model, not on `InsightFinding` itself; ownership and effort remain heuristic rather than analyzer-authored

---

# 17. Confidence & Limitations

## Confidence Scale

| Score | Meaning |
|---|---|
| 1.0 | Directly measured (e.g., confirmed GC root via `ClrHeap.EnumerateRoots()`) |
| 0.8 | High-confidence heuristic (e.g., static field retaining Gen2 chain) |
| 0.5 | Moderate heuristic (e.g., type name pattern for cache detection) |
| < 0.5 | Speculative (e.g., allocation pattern from Gen0/Gen2 ratio alone) |

- ✅ `InsightFinding.ConfidenceScore` (nullable double) now a first-class field; confidence values persisted in domain model and surfaced in HTML/JSON outputs via `BuildConfidenceScore(finding)` → `finding.EffectiveConfidenceScore`

## Per-Analyzer Status Fields

`AnalyzerRunResult`: `Status`, `ElapsedMs`, `ObjectsScanned`, `SkipReason`/`ErrorMessage`.
Run-status summary (completed/failed/skipped/timed-out counts) emitted in all formats.
- ✅ `AnalyzerRunResult`: `Status` (`AnalyzerExecutionStatus`: `Success` / `Failed` / `SkippedByFilter` / `SkippedByCancellation`), `Duration` (TimeSpan), `ObjectScanCount` (long), `ErrorMessage`, `ErrorType`, `FindingGeneratorError`, `SkipReason`
- ✅ `SkipReason` is populated by pipeline/filter services and rendered in `ConfidenceSectionBuilder`
- ✅ Run-status summary emitted via `ReportSerializer.BuildConfidenceNotes()` and surfaced in HTML/JSON outputs

## Known Heuristic Limitations

| Limitation | Sections |
|---|---|
| Retained size is bounded BFS, not true dominator | 3.1, 4.1, 4.2 |
| Allocation sites unavailable from `.dmp` (require ETW) | 2.3 |
| Task orphan detection relies on CLR field name stability | 8.2 |
| FOH/POH sizes include runtime-internal objects | 10.4, 10.5 |
| `ClrThread.StackBase/StackLimit` may be 0 for GC/finalizer threads | 7.1 |
| Deadlock detection misses cooperative waits without `BlockingObjects` | 7.3 |

- ✅ Limitation table is accurate for the current codebase; all entries verified against analyzer implementations
- ⚠️ "Retained size is bounded BFS, not true dominator" is now accurate (not aspirational): `BoundedRetainedSizeBfs` is implemented and called by `MemoryAnalyzer` and `DominatorAnalyzer`; the BFS is exclusive (claims visited set) so it approximates, but does not guarantee, exclusive retained bytes
- ⚠️ Additional unlisted limitations: GC handle retained bytes are avg-size estimates (§5.1), gen counts for type bytes are approximated (§3.1), string encoding waste not detected (§11.2)

---

# 18. AppDomain & Assembly Analysis

## 18.1 AppDomain Inventory

Via `ClrRuntime.AppDomains`:
- Domain name, address, numeric ID, module count
  - ✅ `AppDomainSnapshot`: Name, Address, DomainId, ModuleCount, EstimatedManagedBytes
- Per domain module list: assembly name, path, size (`ClrModule.Size`), `IsDynamic`, `IsPEFile`
  - ✅ `AppDomainSnapshot.TopModules: IReadOnlyList<string>` populated by `ModuleAnalyzer` (top module names per domain)
  - ✅ `LoadedModuleSnapshot` (in `ModuleDomainResult.TopModulesBySize`): Name, AssemblyName, FullPath, Address, Size, IsDynamic, `IsPEFile` — all fields present
  - ⚠️ `AppDomainSnapshot.TopModules` is a name-only list; full `LoadedModuleSnapshot` detail is global (not per-domain)
- Managed memory attributable per domain (cross-ref heap index by `ClrType.Module`)
  - ⚠️ `AppDomainSnapshot.EstimatedManagedBytes` exists; built by joining heap index type aggregates with module assignment — approximation (avg-size based)
- Types loaded in > 1 domain flagged
  - ❌ Not detected; cross-domain type presence check not implemented

## 18.2 Assembly Version Conflicts

- Multiple `ClrModule` instances with same `AssemblyName` but different `FileName`/`MetadataToken`
  - ✅ `ModuleDomainResult.VersionConflictGroups` (count); `ConflictingAssemblyNames` (list); `ConflictDetails` (`IReadOnlyList<ModuleConflictGroup>`): ModuleName + conflicting `LoadedModuleSnapshot` instances
- Grouped: assembly name -> conflicting instances with paths and addresses
  - ✅ `ModuleConflictGroup.Instances` (`IReadOnlyList<LoadedModuleSnapshot>`) with FullPath and Address
- Dynamic assemblies (`IsDynamic = true`): never unloaded; total count and size
  - ✅ `ModuleDomainResult.DynamicModules` (count); `ModuleDomainResult.DynamicModuleBytes` (ulong) aggregated in `ModuleAnalyzer`
- Anonymous modules (no file path): in-memory code generation
  - ✅ `ModuleDomainResult.AnonymousModuleCount` (int) tracked in `ModuleAnalyzer`; finding emitted by `ModuleFindingGenerator` when count >= threshold

## 18.3 Type Density per Module

Via `ClrModule.EnumerateTypes()`:
- Type count per module (unique `MethodTable` count)
  - ✅ `ModuleDomainResult.TopModulesByTypeCount` (`IReadOnlyList<ModuleTypeCountEntry>`): ModuleName, TypeCount, LiveTypeCount, ObjectCount, TotalBytes
  - ✅ `ModuleDomainResult.HeavyTypeDensityModules` (`IReadOnlyList<ModuleTypeDensity>`): ModuleName, UniqueTypeCount, ObjectCount, TotalBytes, BytesPerType
- Modules with > 5000 types flagged (source generators, AOT, reflection-heavy)
  - ⚠️ Not explicitly flagged; section builder must apply threshold to `TopModulesByTypeCount.TypeCount`
- Heap footprint per module: `ClrObject.Size` sum for instances whose `ClrType.Module` matches
  - ✅ `ModuleHeapStats.TotalBytes` (via `ModuleDomainResult.TopModulesByHeapMemory`); derived from Phase 1 type aggregate index joined with `ModuleId`
- Type-to-object ratio
  - ⚠️ `ModuleTypeDensity.BytesPerType` is bytes-per-type, not objects-per-type; objects-per-type derivable from `ObjectCount / UniqueTypeCount`

---

# 19. JIT & Native Code Footprint

## 19.1 JIT Heap Usage

Via `ClrRuntime.GetJitManagers()`:
- Total JIT code heap size
  - ✅ `JitDomainResult.TotalJitHeapBytes`
- JIT manager count
  - ✅ `JitDomainResult.JitManagerCount`
- JIT heap as % of total process memory
  - ✅ `JitDomainResult.JitHeapPctOfTotalProcess`

## 19.2 Compiled Method Analysis

Via `ClrStackFrame.Method` across all thread stacks:
- Active method hotspot map: methods on most thread stacks simultaneously
  - ✅ `JitDomainResult.TopActiveFrameTypes` (`IReadOnlyList<NameCountEntry>`); `ActiveMethodsOnStacks` count
- Per method: `ClrMethod.Signature`, declaring `ClrType.Name`, `NativeCode` address
  - ✅ `JitMethodSnapshot`: Signature, DeclaringType, NativeCodeAddress, HotSize, ColdSize, IsTiered
- Native code range: `ClrMethod.HotColdInfo` hot + cold region; methods > 64 KB flagged
  - ✅ HotSize and ColdSize stored in `JitMethodSnapshot`; TopLargestMethods (`IReadOnlyList<JitMethodSnapshot>`) sorted by size
  - ⚠️ ">64 KB" flag is not stored as a boolean; section builder must check `HotSize + ColdSize > 65536`
- Unmanaged frame ratio: `ClrStackFrame.Kind` per thread
  - ✅ `JitDomainResult.UnmanagedFrameCount`, `ManagedFrameCount`; ratio derivable

## 19.3 Tiered Compilation & ReadyToRun

- Same `MetadataToken` -> two address ranges = tiered (Tier0 -> Tier1)
  - ✅ `JitAnalyzer` detects tiering: `tokenToNativeCode` dictionary; if same token seen with different NativeCode addresses, increments `tieredMethodCount`
  - ✅ `JitDomainResult.TieredMethodCount`; `JitMethodSnapshot.IsTiered` flag
- ReadyToRun: `ClrModule.IsPEFile` + R2R header
  - ❌ R2R detection not implemented; ClrMD does not expose R2R header presence directly; `IsPEFile` is readable but no R2R check in `JitAnalyzer`
- `NativeCode == 0` on a current stack frame = Tier0 stub not yet JIT-compiled
  - ⚠️ Not explicitly detected or flagged in `JitDomainResult`; `NativeCode == 0` check is possible in section builder from `JitMethodSnapshot.NativeCodeAddress`

---

# 20. Boxing & Value Type Pressure

## 20.1 Boxed Value Type Inventory

Detection: `ClrType.IsValueType = false` + `BaseType` is `System.ValueType`/`System.Enum`, or `ClrObject.AsBoxedValue()`:
- Total boxed object count and size
  - ✅ `BoxingDomainResult.TotalBoxedObjects`, `TotalBoxedBytes`
- Top 20 most-boxed types: name, box count, total size
  - ✅ `BoxingDomainResult.TopBoxedTypes` (`IReadOnlyList<BoxedTypeEntry>`): ValueTypeName, BoxCount, TotalBoxBytes, IsEnum
- Boxed enums (`ClrType.IsEnum`): anti-pattern
  - ✅ `BoxingDomainResult.BoxedEnumCount`, `BoxedEnumBytes`
- Boxed structs in `object[]`/`IEnumerable<object>`: flag `List<object>`, `ArrayList`, `Hashtable`
  - ❌ Not detected at the container level; boxing detection is per-type aggregate from index, not per-object container context
- Structs > 16 bytes flagged as oversized
  - ✅ `BoxingDomainResult.OversizedValueTypeCount`; threshold configurable via `BoxingAnalysisOptions.OversizedThresholdBytes`

## 20.2 Value Type Shape Issues

Via `ClrType.Fields` on value types:
- Mutable ref-containing structs: `IsObjectReference = true` fields -> aliasing + write barrier cost
  - ❌ Not detected in `BoxingAnalyzer`; only struct padding and box counts are computed; ref-field mutable struct check would require per-field `IsObjectReference` scan
- Struct field padding waste: `ClrInstanceField.Offset` gaps vs total field sizes
  - ✅ `BoxingDomainResult.TopPaddingWasteTypes` (`IReadOnlyList<StructPaddingEntry>`): TypeName, TotalFieldBytes, StructSize, WastedPaddingBytes, WasteRatio
  - Implementation: `StaticSize − sum(field.Size)` per value type
- Large structs on stack: > 64 bytes via `ClrThread.EnumerateStackObjects()` -> flag for `ref` or class
  - ❌ Not detected; `EnumerateStackObjects()` not used in `BoxingAnalyzer`
- Top 10 oversized by `ClrType.StaticSize`
  - ✅ `OversizedValueTypeCount` is a scalar; individual oversized type list not stored — only top boxed types list available; section builder must filter `TopBoxedTypes` by `IsEnum = false` + large size

---

# 21. Finalizable Object Lifecycle

## 21.1 Finalizable Object Population

All `ClrType.IsFinalizable = true` objects:
- Total count and size
  - ✅ `FinalizableObjectDomainResult.TotalFinalizableObjects`, `TotalFinalizableBytes`
- By generation: Gen0/1/2/LOH
  - ✅ `Gen0Count`, `Gen1Count`, `Gen2Count`, `LohCount` — separately aggregated for clarity (LOH finalizable objects extend lifetime by 2+ GCs)
- Top 20 finalizable types by Gen2 count and size
  - ✅ `TopFinalizableTypesByGen2Count` (`IReadOnlyList<TypeGenerationProfile>`) sorted by Gen2Count
- `IsFinalizable` + `IDisposable` + `_disposed = false` -> Dispose never called (heuristic)
  - ✅ `FinalizerQueueEntry.IsDisposableType`, `DisposedFieldFound`, `DisposedFieldValue`; checked for queued objects
  - ⚠️ Heuristic only applies to objects IN the finalizer queue (`TopQueueEntriesByRetainedSize`), not all finalizable objects on the heap

## 21.2 Finalizer Queue Analysis

Via `heap.EnumerateFinalizableObjects()`:
- Queue depth (total count)
  - ✅ `FinalizableObjectDomainResult.FinalizerQueueCount`
- Queue type distribution (type count aggregation)
  - ✅ `TopQueueTypesByCount` (`IReadOnlyList<QueueTypeStatistic>`): TypeName, QueueCount — answers "which types dominate the queue"
- Top queue entries by retained size
  - ✅ `TopQueueEntriesByRetainedSize` (`IReadOnlyList<FinalizerQueueEntry>`): Address, TypeName, ShallowSize, EstimatedRetainedBytes, IsDisposableType, DisposedFieldFound, DisposedFieldValue
- Severity: Critical > 10 000 / Warning 1000-10 000 / OK < 1000
  - ❌ Severity band not pre-computed; section builder must apply thresholds to `FinalizerQueueCount`
- Queue objects retaining large sub-graphs (bounded BFS)
  - ✅ `FinalizableObjectDomainResult.FinalizerQueueRetainedBytes` (ulong); BFS-estimated total retention (upper bound: shared sub-graphs may be double-counted across entries)
  - ✅ `FinalizableObjectDomainResult.IsRetainedEstimatePartial` (bool); true if any BFS was capped by node/depth limits—indicates partial graph traversal
- Undisposed IDisposable in queue
  - ✅ `FinalizableObjectDomainResult.HasUndisposedDisposableInQueue` (bool); true if any sampled queue entry is `IDisposable` + disposed field is `false` (NOT resurrection detection; resurrection requires `GC.ReRegisterForFinalize` calls)

## 21.3 Finalizer Thread Health

Via `ClrThread.IsFinalizer = true`:
- Alive status, OS thread ID
  - ✅ `ThreadDomainResult.FinalizerThreadBlocked`, `FinalizerManagedThreadId`, `FinalizerOsThreadId`, `FinalizerLockCount`
- Blocked: `LockCount > 0` or known wait frame
  - ✅ `FinalizerThreadBlocked` flag in `ThreadDomainResult`
- Blocking frame: `ClrStackFrame.FrameName`
  - ✅ `ThreadDomainResult.FinalizerFrames` (`IReadOnlyList<string>`)
- Large queue + blocked thread = confirmed starvation
  - ⚠️ Both signals available (`FinalizerQueueCount` + `FinalizerThreadBlocked`); combined "confirmed starvation" flag not emitted — section builder or `InsightEngine` must correlate
- Full stack trace of finalizer thread
  - ✅ `FinalizerFrames` list

---

# 22. Array Deep Analysis

## 22.1 Array Population Overview

All `ClrType.IsArray = true` objects:
- Total count and combined size
  - ✅ `ArrayDomainResult.TotalArrayObjects`, `TotalArrayBytes`
- By element type and rank: count and size
  - ✅ `ArrayDomainResult.TopArrayTypesBySize` (`IReadOnlyList<ArrayTypeProfile>`): ElementTypeName, Rank, Count, TotalBytes, IsMultiDimensional
- By generation
  - ❌ Generation breakdown for arrays not in `ArrayDomainResult`; LOH subset: `LohArrayCount`, `LohArrayBytes` ✅
- Top 20 by total size
  - ✅ `TopArrayTypesBySize` is sorted by TotalBytes; per-type, not per-instance

## 22.2 Large Array Analysis

Arrays with `ClrObject.Size > 85 000`:
- Individual entries: address, element type, length, size
  - ✅ `ArrayDomainResult.TopLargeArrays` (`IReadOnlyList<LargeArrayEntry>`): Address, ElementTypeName, Length, Rank, Size
- Anti-patterns: `byte[]` > 1 MB, `string[]`/`object[]` > 10 000 elements, multi-dim > 85 KB
  - ⚠️ All data available in `TopLargeArrays`; anti-pattern classification labels not pre-computed; section builder must apply type-name and size thresholds
- Top 10 largest instances
  - ✅ `TopLargeArrays` sorted by Size

## 22.3 Sparse & Wasteful Arrays

Bounded sampling via `ClrObject.AsArray().GetObjectValue(index)`:
- Null density in reference arrays (> 50 % null = over-allocated)
  - ✅ `ArrayDomainResult.TopSparseArrays` (`IReadOnlyList<SparseArrayEntry>`): Address, ElementTypeName, Length, NullOrZeroCount, SparseRatio, WastedBytes
  - `ArrayAnalyzer` uses `GetObjectValue()` sampling; threshold is `SparseRatio >= 0.5`
- Zero density in value arrays
  - ⚠️ `SparseEntry` uses same `NullOrZeroCount` field for both null (ref) and zero (value) — combined in one metric
- `List<T>._items`, `Dictionary<K,V>._entries` fill rate < 25 % flagged
  - ❌ Not detected specifically; `CollectionAnalyzer` covers over-capacity containers; no cross-reference from `ArrayAnalyzer` to specific container backing arrays

## 22.4 Jagged vs Multi-Dimensional

- `T[][]`: many small inner arrays -> consider flat `T[]` with manual indexing
  - ⚠️ `ArrayTypeProfile.IsMultiDimensional = false` + Rank = 1 + ElementTypeName ends with `[]` = jagged; pattern detectable in section builder but not pre-labeled
- `T[,]`/`T[,,]`: contiguous but incompatible with `Span<T>`, `Memory<T>`, `ArrayPool<T>`
  - ✅ `IsMultiDimensional = true` in `ArrayTypeProfile`; incompatibility note is section builder prose

---

# 23. Async State Machine Objects

## 23.1 State Machine Population

Detection: `ClrType.Interfaces` contains `IAsyncStateMachine` OR name matches `<.*>d__\d+`:
- Total count and size
  - ✅ `AsyncStateMachineDomainResult.TotalStateMachines`, `TotalStateMachineBytes`
- Top 20 by count and size
  - ✅ `TopStateMachineTypes` (`IReadOnlyList<StateMachineTypeProfile>`): TypeName, OriginatingMethod, DeclaringType, Count, TotalBytes, AvgStateValue, ReferenceFieldCount
- State field (`<>1__state`): -1 = completed, -2 = not started, >= 0 = suspended at await N
  - ✅ `StateMachineTypeProfile.AvgStateValue` (average of sampled state values for the type)
  - ⚠️ Only AVERAGE state stored, not a distribution; cannot tell how many are at each await point from the domain result alone
- Distribution of state values
  - ❌ No state-value histogram; `AvgStateValue` is a single int per type

## 23.2 Captured Closure Analysis

Via `ClrType.Fields` on state machine types:
- Reference fields = captured objects retained until async method completes
  - ✅ `StateMachineTypeProfile.ReferenceFieldCount`; `HighCaptureStateMachine.TotalCapturedRefBytes`, `LargeCaptures` (list of capture type names)
- Large captures: instances with total ref field shallow size > 1 MB
  - ✅ `AsyncStateMachineDomainResult.TopByCapturedSize` (`IReadOnlyList<HighCaptureStateMachine>`): Address, TypeName, TotalCapturedRefBytes, LargeCaptures
- Nested captures: state machine fields referencing other state machines
  - ❌ Not detected; would require checking if any field type in `LargeCaptures` is itself a state machine type
- Problematic capture types: `HttpClient`, `DbContext`, `Stream`, `ILogger`
  - ⚠️ `LargeCaptures` contains type names; section builder can pattern-match against problematic type list; not pre-flagged
- Top 10 by total captured reference size
  - ✅ `TopByCapturedSize` sorted by `TotalCapturedRefBytes`

## 23.3 Suspended Method Map

- Originating method name decoded from compiler-generated class name
  - ✅ `SuspendedMethodEntry.MethodName`, `DeclaringType` in `AsyncStateMachineDomainResult.SuspendedMethodMap`
- Grouped by declaring type
  - ✅ `SuspendedMethodEntry`: DeclaringType, MethodName, SuspendedCount, TotalBytes
- Cross-ref with section 8.1: state machines whose `Task` is `Faulted` but uncollected
  - ❌ Not cross-referenced; `AsyncTaskDomainResult` and `AsyncStateMachineDomainResult` are independent; joining by type name is possible in section builder but not pre-computed

---

# 24. Weak Reference & ConditionalWeakTable Analysis

## 24.1 Weak GC Handle Population

Via `ClrRuntime.EnumerateHandles()` where `HandleKind` is `Weak`, `WeakLong`, or `SizedRef`:
- Total weak handle count
  - ✅ `WeakReferenceDomainResult.TotalWeakHandles`
- Alive vs collected targets (`ClrHeap.GetObject(address).IsValid`)
  - ✅ `AliveWeakTargets`, `DeadWeakTargets`, `DeadTargetRatio`
- Top 10 target types by weak handle count
  - ✅ `TopWeakTargetTypes` (`IReadOnlyList<NameCountEntry>`)
- `WeakLong` vs `Weak` distinction
  - ⚠️ `TotalWeakHandles` aggregates all weak kinds; per-kind breakdown not stored in `WeakReferenceDomainResult` (handled in `GCHandleDomainResult.HandlesByKind` as strings)

## 24.2 `WeakReference<T>` Object Analysis

- Total count and size
  - ✅ `WeakReferenceDomainResult.WeakReferenceObjectCount`, `WeakReferenceObjectBytes`
- Stale wrappers: target collected but wrapper still alive (read `m_handle` field)
  - ✅ `StaleWrapperCount`; `TopStaleWrapperHolderTypes` (`IReadOnlyList<NameCountEntry>`)
- Top types holding large stale `WeakReference` counts
  - ✅ `TopStaleWrapperHolderTypes`

## 24.3 `ConditionalWeakTable<TKey, TValue>` Analysis

Via `ClrRuntime.EnumerateHandles()` where `HandleKind = Dependent`:
- Total `DependentHandle` count
  - ✅ `WeakReferenceDomainResult.DependentHandleDeadKeyCount` (dead keys count); total dependent handle count available from `DependentHandleAnalyzer.DependentHandleDomainResult`
- Source/target type per pair; top 10 source->target type pairs
  - ✅ `DependentHandleDomainResult.TopSourceTargetPairs` (from `DependentHandleAnalyzer`)
- Live vs dead key analysis
  - ✅ `DependentHandleDeadKeyCount` in `WeakReferenceDomainResult`
- Tables with > 10 000 entries flagged
  - ❌ No per-table entry count; aggregate handle counts only; individual `ConditionalWeakTable` instance enumeration not implemented

---

# 25. Virtual Memory & Segment Reservation

## 25.1 Committed vs Reserved Memory

Via `ClrSegment.CommittedMemory` / `ClrSegment.ReservedMemory`:
- Total committed managed memory
  - ✅ `SegmentReservationDomainResult.TotalCommittedBytes`
- Total reserved managed memory
  - ✅ `TotalReservedBytes`
- Reservation gap: Reserved - Committed
  - ✅ `ReservationGapBytes`, `ReservedToCommittedRatio`
- Per-segment committed vs reserved table
  - ✅ `SegmentTable` (`IReadOnlyList<SegmentReservationEntry>`): Address, Kind, CommittedBytes, ReservedBytes, IsEphemeral, LogicalHeap, FillPct
- Ratio > 4x notable; > 10x = address space exhaustion risk
  - ✅ `AddressSpacePressureRisk` (bool), `PressureRiskReason` (string)

## 25.2 Segment Lifecycle

- Segment count by kind: SOH ephemeral / SOH non-ephemeral / LOH / POH / FOH
  - ✅ `SegmentReservationDomainResult.EphemeralSegmentCount`, `NonEphemeralSohSegmentCount`; LOH/POH/FOH counts from `SegmentAnalysisDomainResult`
- Ephemeral segments: one per logical GC heap; fill % = primary GC trigger
  - ✅ `SegmentReservationEntry.IsEphemeral`, `FillPct`; `AvgEphemeralFillPct` aggregate
- Non-ephemeral SOH: high count = heap never fully compacted
  - ✅ `NonEphemeralSohSegmentCount`
- Address ranges (`Start`-`End`): fragmentation across virtual address space
  - ⚠️ `SegmentReservationEntry` stores `Address` (start) + `ReservedBytes`; End derivable but not stored explicitly
- Logical heap assignment (`ClrSegment.LogicalHeap`)
  - ✅ `SegmentReservationEntry.LogicalHeap`; `ReservedByLogicalHeap` (`IReadOnlyDictionary<int, ulong>`)

## 25.3 Address Space Pressure

- Total VA consumed by managed heap (sum of all segment reserved ranges)
  - ✅ `TotalReservedBytes`
- 32-bit risk: reserved > 1.5 GB -> elevated `OutOfMemoryException` risk
  - ✅ Covered by `AddressSpacePressureRisk` + `PressureRiskReason`
- Fragmented VA: many small non-contiguous segments
  - ⚠️ Segment count and addresses available in `SegmentTable`; contiguity/gap analysis not computed; section builder must sort by address and compute gaps
- JIT heap (section 19.1) + managed reserved + native heap = full VA picture
  - ⚠️ `JitDomainResult.TotalJitHeapBytes` and `SegmentReservationDomainResult.TotalReservedBytes` available; native heap size not available via ClrMD; full VA sum must be assembled in section builder

---

# Analyzer Coverage Map

| Analyzer | Primary Sections | Notes |
|---|---|---|
| `MemoryAnalyzer` | 1, 2.1, 3.1 | ✅ Full; size histogram from Phase 1 index |
| `AllocationPatternAnalyzer` | 2.3, 9.1-9.2 | ⚠️ Profile enum names differ from spec |
| `GCGenerationAnalyzer` | 2.2, 9.1, 9.2, 10.1 | ⚠️ Byte counts approximated (avg × gen count) |
| `ObjectShapeAnalyzer` | 3.3 | ⚠️ Thresholds differ from spec (`Balanced` vs `Mixed`); `IsArray` now surfaced in `TypeShapeProfile` ✅ |
| `SegmentAnalyzer` | 2.1, 9.2, 10.4, 10.5, 25.1, 25.2 | ✅ `FrozenPercent`, `ReservedBytes`, `UsedBytes`, and `PerLogicalHeapSummaries` now implemented |
| `GCRootAnalyzer` | 5.1-5.3 | ⚠️ Retained bytes are avg-size estimates; paths flat (not grouped by type) |
| `RetentionAnalyzer` | 4.1-4.2, 6.1-6.4 | ⚠️ Counts incoming refs only; `EstimatedRetainedBytes` via BFS now done by `MemoryAnalyzer` and `DominatorAnalyzer` instead |
| `StringAnalyzer` | 11.1-11.2 | ✅ Comprehensive; encoding waste detection missing |
| `StaticRootLeakDetector` | 4.3, 6.2 | ⚠️ Chain depth not captured; `LeakCandidateAnalyzer` now cross-references static root reachability |
| `LohFragmentationAnalyzer` | 10.1-10.3 | ✅ Full; severity band not in model; per-object lifetimes ❌ |
| `ThreadAnalyzer` | 7.1-7.2 | ✅ `ThreadStateSnapshot.StackSizeBytes` now computed; thread pool detail in `HangDomainResult` |
| `LockGraphAnalyzer` | 7.3 | ✅ `LockObjectAddresses` now in `DeadlockCandidateSnapshot` |
| `HangAnalyzer` | 7.1, 8.1-8.3 | ✅ All ThreadPool fields now surfaced; `RuntimeQueueLength` is read opportunistically via reflection and reported when available |
| `AsyncTaskAnalyzer` | 8.1-8.3 | ✅ `ExceptionType`/`ExceptionMessage` on orphaned tasks; `TopDeepestChains` with ordered type sequences; `TotalTaskContinuations`; depth > 50 flag in section builder |
| `ThreadStackClusterAnalyzer` | 7.2 | ✅ JSON/NDJSON artifact export |
| `GCHandleAnalyzer` | 5.1, 9.3, 24.1 | ⚠️ Gen0/Gen1 pinning correlation ❌; retained bytes are estimates |
| `DependentHandleAnalyzer` | 5.1, 24.3 | ⚠️ Per-table entry count ❌; type-pair counts ✅ |
| `EventLeakAnalyzer` | 4.3, 12.1-12.2 | ✅ Comprehensive; publisher gen not propagated to group level |
| `CollectionAnalyzer` | 3.3, 4.3, 22.3 | ⚠️ Container fill-rate for section 22.3 not cross-referenced |
| `CrashAnalyzer` | 13.1-13.2 | ✅ `ChainDepth` per exception and depth histogram; frame origin classification in section builder |
| `ModuleAnalyzer` | 13.2, 18.1-18.3 | ✅ `IsPEFile` in `LoadedModuleSnapshot`; `DynamicModuleBytes` aggregated in `ModuleAnalyzer` |
| `ReferenceChainAnalyzer` | 4.1-4.2, 5.3 | ⚠️ Root paths for §5.2 severity roots, not wired to §6.1 candidates |
| `InsightEngine` | 16.1-16.3 | ✅ `ConfidenceScore`/`Caveats[]` on `InsightFinding`; ≥ 3 failed-analyzer Warning emitted |
| `TrendAnalyzer` | 14.1-14.2 | ✅ Leak-candidate new-signal comparison; `TrendReportComposer` now emits explicit new-type and severity-escalation blocks; classification remains prose-only ⚠️ |
| `LeakCandidateAnalyzer` | 6.1-6.4 | ✅ New; suspicion scoring, `LeakClass` classification, `LeakCandidateDomainResult` |
| `DominatorAnalyzer` | 4.1-4.2 | ✅ New; bounded BFS exclusive retained per top-N type via `BoundedRetainedSizeBfs` |
| `JitAnalyzer` | 19.1-19.3 | ⚠️ R2R detection ❌; >64KB flag not in model; Tier0-stub flag not stored |
| `BoxingAnalyzer` | 20.1-20.2 | ⚠️ Container boxing context ❌; mutable ref-struct detection ❌; stack-size ❌ |
| `FinalizableObjectAnalyzer` | 21.1-21.3 | ⚠️ LOH finalizable count not separate; retained bytes avg-estimate only |
| `ArrayAnalyzer` | 22.1-22.4 | ⚠️ Generation breakdown ❌; container fill-rate not cross-referenced |
| `AsyncStateMachineAnalyzer` | 23.1-23.3 | ⚠️ State distribution not stored; nested-capture detection ❌; Task cross-ref ❌ |
| `WeakReferenceAnalyzer` | 24.1-24.3 | ⚠️ WeakLong vs Weak breakdown not in domain result; per-table CWT count ❌ |
| `SegmentReservationAnalyzer` | 25.1-25.3 | ✅ Comprehensive; VA gap analysis not pre-computed; native heap ❌ |
