# Single-Dump Report Format

## Purpose

Define the composition, section map, and rendering rules for the professional single-dump report.
This document is the authoritative schema spec that all renderers (HTML, JSON, markdown) must satisfy.
It supersedes the section-by-section data audit in `ProfessionalTierReport.md`, which remains the
data-availability reference. Trend mode composition lives in `TrendReportBlueprint.md`.

---

## Design Goals

1. **Answer "what's wrong?" in one screen** — health scorecard + critical findings before any tables.
2. **Problem-domain grouping** — sections follow diagnostic concerns, not analyzer boundaries.
3. **Finding-first within every section** — lead with the finding, follow with evidence, collapse detail.
4. **Confidence inline** — every finding shows its confidence band; heuristics are visually distinct.
5. **Dynamic section ordering** — domains with Critical findings appear before domains with no findings.
6. **Full data coverage** — every field produced by every analyzer has a home in the report.

---

## Document Schema

```
AnalysisReportDocument
├── Header
├── HealthScorecard
├── ExecutiveSummary
├── Domains[]                  ordered by MaxSeverityInDomain descending
│   └── DomainSection
│       ├── DomainHeader
│       ├── Sections[]
│       │   └── ReportSection
│       │       ├── LeadFinding     (nullable — null = informational only)
│       │       ├── KeyMetrics{}    (scalar KPIs for this section)
│       │       ├── Tables[]        (collapsed by default in HTML)
│       │       └── Provenance      (analyzer name, confidence, limits, elapsed)
│       └── DomainInsights[]   (cross-analyzer findings scoped to this domain)
├── CrossDomainInsights[]      (InsightEngine findings that span multiple domains)
└── Appendix
    ├── AnalyzerRunSummary
    ├── MemoryDiagnostics       (present only when --memory-diagnostics enabled)
    └── KnownLimitations[]
```

### Header

| Field | Source |
|---|---|
| Dump file path | `AnalysisIncidentContext.DumpPath` |
| Dump file size | `AnalysisIncidentContext.DumpSizeTier` |
| Analysis timestamp | `AnalysisIncidentContext.AnalysisTimestamp` |
| Analyzer version | assembly version |
| Runtime version | `ClrRuntime.ClrInfo.Version` |
| GC mode | `AnalysisIncidentContext.GcMode` |
| Logical heap count | `AnalysisIncidentContext.HeapCount` |
| Total managed bytes | `MemoryDomainResult.TotalBytes` |
| Total objects | `MemoryDomainResult.TotalObjects` |
| Total unique types | `MemoryDomainResult.UniqueTypes` |

### HealthScorecard

One row per domain. Severity = max of all `InsightFinding.Severity` within that domain.
Renderers must place this above the executive summary.

```
Memory   GC      Leaks   Threads  Async   Exceptions  Runtime
🔴 Crit  🟡 Warn 🔴 Crit 🟢 OK   🟢 OK   🟢 OK       🟡 Warn
```

Domains with no analyzers run or all skipped show `⚪ Unknown`.

### ExecutiveSummary

| Block | Source | Limit |
|---|---|---|
| Critical findings | `InsightFinding` where `Severity = Critical`, severity-sorted | top 5 |
| Warning findings | `InsightFinding` where `Severity = Warning` | top 5 |
| Top recommendations | `InsightFinding.Recommendation` from top Critical+Warning findings | top 3 |
| Key metrics strip | see table below | — |

Key metrics strip (all in one row for HTML, list for markdown):

| Metric | Source |
|---|---|
| Total heap | `MemoryDomainResult.TotalBytes` |
| LOH | `MemoryDomainResult.LohBytes` + `LohPercent` |
| Gen2 % | `GCGenerationDomainResult.Gen2Pct` |
| GC pressure | `AllocationPatternDomainResult.GCPressure` |
| Leak candidates | `LeakCandidateDomainResult.TotalCandidates` |
| Hang score | `HangDomainResult.HealthScore` (0=healthy, lower=worse) |
| Blocked threads | `ThreadDomainResult.BlockedThreadCount` |
| Deadlock cycles | `LockGraphDomainResult.DeadlockCandidateCount` |
| Active exceptions | `CrashDomainResult.ActiveExceptions` |
| Finalizer queue | `FinalizableObjectDomainResult.FinalizerQueueCount` |

### ReportSection (per-section contract)

Every section must provide:

```
LeadFinding:
  severity: Critical | Warning | Info | (absent)
  title: string                  — one sentence, actionable
  evidence: string               — metric-grounded, not vague
  recommendation: string
  confidence: ●●●● | ●●●○ | ●●○○ | ●○○○   — inline band, not footnote
  caveats: string[]              — heuristic disclosures

KeyMetrics:
  map of metric-label → scalar value
  (shown collapsed/prominent regardless of table visibility)

Tables:
  each table: title, columns[], rows[][], rowLimit (default 20)
  HTML: collapsed by default, "Show all N rows" toggle
  JSON: always full
  markdown: top-N only, remainder truncated with note

Provenance:
  analyzer: string
  status: Completed | Failed | Skipped | TimedOut
  durationMs: long
  objectScanCount: long          — from AnalyzerRunResult.Diagnostics
  cacheHits / cacheMisses: long  — from AnalyzerRunResult.Diagnostics
  cappingNotes: string[]         — e.g. "path search capped at 5000 objects"
```

Confidence bands (inline, not a separate section):

| Band | Score | Symbol | Meaning |
|---|---|---|---|
| High | ≥ 0.85 | ●●●● | Directly measured (GC root, exact count) |
| Med-High | 0.65–0.85 | ●●●○ | Heuristic with strong signals |
| Medium | 0.45–0.65 | ●●○○ | Pattern-match or estimate |
| Low | < 0.45 | ●○○○ | Weak signal or highly approximate |

---

## Domain Map

Domains are rendered in this priority order; within each domain sections are similarly sorted.
The renderer reorders entire domains by `MaxSeverityInDomain` at runtime.

### Domain A — Memory & Leaks

**A1. Leak Candidates** ← DOMAIN LEAD  
Source: `LeakCandidateDomainResult`

LeadFinding: most severe `LeakCandidateRecord` by `SuspicionScore`.

KeyMetrics:
- `TotalCandidates`
- Count by `LeakClass` (from `CandidatesByClass`)
- Highest suspicion score

Tables:
- **Top suspects** — `TopCandidates` sorted by `SuspicionScore` desc  
  Columns: TypeName | Score | Severity | Classification | TotalSize | InstanceCount | Gen2% | RootKind | IsFinalizable | IsContainer | RefFieldRatio
- **By leak class** — `CandidatesByClass` summary

Confidence: `HeuristicOnly = true` → always ●●○○ max.

---

**A2. Memory Overview**  
Source: `MemoryDomainResult`

KeyMetrics: TotalBytes, TotalObjects, UniqueTypes, LohBytes, LohPercent.

Tables:
- **Top types by size** — `TopTypesBySize`  
  Columns: TypeName | Count | TotalBytes | LohBytes | AverageSize | EstimatedRetainedBytes | SampleAddress | ModuleName
- **Top types by count** — `TopTypesByCount`
- **Size histogram** — `SizeBucketHistogram`  
  Columns: RangeLabel | ObjectCount | TotalBytes

---

**A3. Dominator Analysis**  
Source: `DominatorDomainResult`

KeyMetrics: CandidateCount, AnalyzedCount, TotalEstimatedRetainedBytes, MaxBreadth, MaxDepth.

Tables:
- **Top dominators** — `TopDominatorTypes`  
  Columns: TypeName | Count | TotalBytes | EstimatedRetainedBytes | RetentionRatio | AverageSize | SampleAddress
  - RetentionRatio = EstimatedRetainedBytes / TotalBytes (computed in renderer)
- **Dominator impact per-mille** — (EstimatedRetainedBytes / heap total) × 1000 per type

Confidence: ●●○○ — bounded BFS, not true Lengauer-Tarjan.  
Caveats: breadth and depth limits; "HeuristicOnly" flag forwarded as caveat text.

---

**A4. Retention Hotspots**  
Source: `RetentionDomainResult`

KeyMetrics: HighlyReferencedObjectCount, TopHighlyReferencedTotalBytes, FinalizerQueueCount, SkippedReferenceAddresses.

Tables:
- **Highly referenced objects** — `TopHighlyReferencedObjects`  
  Columns: Address | TypeName | Size | IncomingReferences | EstimatedRetainedBytes
- **Retention type aggregates** — `TopRetentionTypes`  
  Columns: TypeName | ObjectCount | TotalBytes | TotalIncomingReferences | MaxIncomingReferences | EstimatedRetainedBytes

Flags: `ObjectScanCapped`, `ReferenceCountingSkipped` → surface as caveats.

---

**A5. GC Root Intelligence**  
Source: `GCRootDomainResult`

KeyMetrics: TotalRoots, PathSearchCappedCount.

Tables:
- **Root distribution** — `ByKind`  
  Columns: Kind | Count | EstimatedRetainedBytes | PctOfManagedHeap
- **Top roots by severity** — `TopRootsBySeverity`  
  Columns: RootKind | RootAddress | FieldDescription | TargetTypeName | TargetAddress | EstimatedRetainedBytes | SeverityScore
- **Root paths** — `RootPaths` (grouped by TargetTypeName, max 3 per type, shortest first)  
  Format: `[RootKind] path[0] → path[1] → ... → TargetTypeName`  
  Append `[TRUNCATED]` when `WasCapped = true`.

---

**A6. Static Roots**  
Source: `StaticRootDomainResult`

KeyMetrics: RootCount, TotalRetainedBytes.

Tables:
- **Top static roots** — `TopRootsByRetainedBytes`  
  Columns: Name | Bytes

---

**A7. String Analysis**  
Source: `StringDomainResult`

KeyMetrics: TotalStrings, TotalStringMemoryBytes, UniqueStrings, DuplicationRatio, DuplicateWastedBytes, PctOfManagedHeap, LohStringBytes, InternedStringCount, InternedStringBytes, Gen2StringCount, Gen2StringBytes.

Tables:
- **Top duplicates by waste** — `TopDuplicatesByWaste`  
  Columns: Preview | Count | WastedBytes | TotalSize | AvgSize | SamplingSource
- **Top duplicates by count** — `TopDuplicatesByCount`
- **Very long strings** — `VeryLongStrings`  
  Columns: Address | CharLength | SizeBytes
- **Length distribution** — `Distribution.LengthBuckets`
- **String type distribution** — `TopDuplicateTypes` (NameCountEntry list)
- **Percentile table** — `Distribution.Percentiles` (p50, p90, p99 char lengths)

Metadata shown as prose: SamplingMode, DeduplicationMode, DeduplicationThreshold, DedupSkipReason when DeduplicationSkipped.

---

### Domain B — GC Health

**B1. Generation Pressure**  
Source: `GCGenerationDomainResult`

KeyMetrics: Gen0Bytes/Objects, Gen1Bytes/Objects, Gen2Bytes/Objects, LohBytes/Objects, TotalObjects, Gen2Pct, LohPercent.

Tables:
- **Per-type generation profiles** — `PerTypeGenerationProfiles`  
  Columns: TypeName | Gen0Count | Gen1Count | Gen2Count | LohCount | TotalBytes | IsFinalizable  
  Derived columns: Gen2% = Gen2Count / (Gen0+Gen1+Gen2+Loh), SurvivalRatio = Gen2Count / total
- **Top LOH types** — `TopLohTypes`  
  Columns: TypeName | Gen0Count | Gen1Count | Gen2Count | LohCount | TotalBytes

---

**B2. Allocation Patterns**  
Source: `AllocationPatternDomainResult`

KeyMetrics: Gen0CountPct, Gen1CountPct, Gen2CountPct, LohCountPct (count-based), Gen0SizePct, Gen1SizePct, Gen2SizePct, LohSizePct (size-based), GCPressure, PromotionPressureScore.

Tables:
- **Classification summary** — Profile label + GCPressure label
- **Top transient types** — `TopTransientTypes`  
  Columns: TypeName | Gen0Count | Gen1Count | Gen2Count | LongLivedRatio | Profile
- **Top long-lived types** — `TopLongLivedTypes` (same columns)
- **Top medium-lived types** — `TopShortishTypes`

Note: allocation sites require ETW; these are dump-snapshot heuristics.

---

**B3. Heap Topology**  
Source: `SegmentAnalysisDomainResult`

KeyMetrics: TotalSegments, TotalCommittedBytes, TotalUsedBytes, TotalReservedBytes, ReservationGapBytes, SohBytes, LohBytes, PohBytes, FrozenBytes, FrozenPercent, LohPercent, PohPercent.

Tables:
- **Kind summary** — `KindSummaries`  
  Columns: Kind | SegmentCount | ObjectCount | TotalBytes | ReservedBytes
- **Per logical heap** — `PerLogicalHeapSummaries`  
  Columns: LogicalHeapIndex | Bytes | ObjectCount | SegmentCount  
  Flag: skew > 2× between heaps = thread affinity or allocation hotspot warning
- **Top segments by size** — `TopSegmentsBySize`  
  Columns: Address | Kind | Length | CommittedBytes | UsedBytes | ReservedBytes | Generation | ObjectCount
- **POH types** — `TopPohTypes` (TypeSnapshot list, if populated)
- **Frozen types** — `TopFrozenTypes` (TypeSnapshot list, if populated)

---

**B4. LOH Fragmentation**  
Source: `LohFragmentationDomainResult`

LeadFinding: emit Warning when `FragmentationPercent > 30`, Critical when `> 60`.

KeyMetrics: SegmentCount, TotalBytes, FreeBytes, UsedBytes, FreeBlockCount, FragmentationPercent, LargestFreeBlock.

Tables:
- **Top fragmented segments** — `TopFragmentedSegments`  
  (see LohSegmentSnapshot fields from source)
- **Free gap histogram** — `FreeGapHistogram`  
  (see FreeGapBucket fields from source)
- **Top large LOH objects** — `TopLargeObjects`  
  (LargeObjectSnapshot — from Phase 1 LargeObjectIndex.bin; field list from source)

---

**B5. Segment Reservation & Virtual Memory**  
Source: `SegmentReservationDomainResult`

LeadFinding: emit Warning when `ReservedToCommittedRatio > 4`, Critical when `> 10`.

KeyMetrics: TotalCommittedBytes, TotalReservedBytes, ReservationGapBytes, ReservedToCommittedRatio, EphemeralSegmentCount, AvgEphemeralFillPct, NonEphemeralSohSegmentCount, AddressSpacePressureRisk.

Tables:
- **Segment table** — `SegmentTable`  
  Columns: Address | Kind | CommittedBytes | ReservedBytes | IsEphemeral | LogicalHeap | FillPct
- **Reserved by logical heap** — `ReservedByLogicalHeap` dict

When `AddressSpacePressureRisk = true`: surface `PressureRiskReason` as lead finding.

---

**B6. Finalizable Objects**  
Source: `FinalizableObjectDomainResult`

LeadFinding: emit Warning when `FinalizerQueueCount > 1000`, Critical when `> 10000`.

KeyMetrics: TotalFinalizableObjects, TotalFinalizableBytes, Gen0Count, Gen1Count, Gen2Count, FinalizerQueueCount, FinalizerQueueRetainedBytes, PotentialResurrectionDetected.

Tables:
- **Finalizable types by Gen2 count** — `TopFinalizableTypesByGen2Count`  
  Columns: TypeName | Gen0Count | Gen1Count | Gen2Count | LohCount | TotalBytes | IsFinalizable
- **Finalizer queue** — `TopQueueEntriesByRetainedSize`  
  Columns: Address | TypeName | ShallowSize | EstimatedRetainedBytes | IsDisposableType | DisposedFieldFound | DisposedFieldValue

Cross-ref: `ThreadDomainResult.FinalizerThreadBlocked` — combine with queue depth for "confirmed starvation" lead finding.

---

**B7. GC Handles**  
Source: `GCHandleDomainResult`

KeyMetrics: TotalHandles, StrongLikeHandles, WeakLikeHandles, PinnedHandleTargets, PinnedRetainedBytes.

Tables:
- **Handles by kind** — `HandlesByKind`  
  Columns: Kind | Count | % Total
- **Top handle target types** — `TopTargetTypes`  
  Columns: Type | Count
- **Pinned handle target types** — `TopPinnedTargetTypes`  
  Columns: Type | Count
- **Top pinned by size** — `TopPinnedObjectsBySize`  
  Columns: Type | Bytes | % Pinned

---

**B8. Weak References**  
Source: `WeakReferenceDomainResult`

KeyMetrics: TotalWeakHandles, AliveWeakTargets, DeadWeakTargets, DeadTargetRatio, WeakReferenceObjectCount, WeakReferenceObjectBytes, StaleWrapperCount, DependentHandleDeadKeyCount.

Tables:
- **Weak handle kinds** — `WeakHandleKinds`  
  Columns: Kind | Count
- **Top alive weak target types** — `TopWeakTargetTypes`  
  Columns: Type | Count
- **Stale wrapper holder types** — `TopStaleWrapperHolderTypes`  
  Columns: Type | Count

Caveat: handle scan capped at 50 000 entries when `ScanCapped = true`.

---

**B9. Dependent Handles**  
Source: `DependentHandleDomainResult`

KeyMetrics: DependentHandleCount, ResolvedEdgeCount, UnresolvedTargetCount, UnresolvedPercent.

Tables:
- **Source type distribution** — `TopSourceTypes`  
  Columns: Type | Count
- **Target type distribution** — `TopTargetTypes`  
  Columns: Type | Count
- **Source → target pairs** — `TopSourceTargetEdges`  
  Columns: Pair | Count

---

### Domain C — Type System

**C1. Type Table**  
Source: `MemoryDomainResult.TopTypesBySize` + `GCGenerationDomainResult.PerTypeGenerationProfiles` + `ObjectShapeAnalyzerDomainResult`

Full per-type table joining all available signals:

| Column | Source |
|---|---|
| TypeName | index key |
| Count | `TypeSnapshot.Count` |
| TotalBytes | `TypeSnapshot.TotalBytes` |
| AverageSize | `TypeSnapshot.AverageSize` |
| EstimatedRetainedBytes | `TypeSnapshot.EstimatedRetainedBytes` |
| LohBytes | `TypeSnapshot.LohBytes` |
| ModuleName | `TypeSnapshot.ModuleName` |
| Gen0% / Gen1% / Gen2% | from `TypeGenerationProfile` joined by TypeName |
| IsFinalizable | `TypeGenerationProfile.IsFinalizable` |
| IsValueType | `TypeShapeProfile.IsValueType` |
| IsArray | `TypeShapeProfile.IsArray` |
| ReferenceFields | `TypeShapeProfile.ReferenceFields` |
| RefFieldRatio | `TypeShapeProfile.ReferenceFieldRatio` |
| BaseTypeChainDepth | `TypeShapeProfile.BaseTypeChainDepth` |

Default sort: TotalBytes desc. Secondary sorts available: Count, EstimatedRetainedBytes, Gen2%.

---

**C2. Object Shape Analysis**  
Source: `ObjectShapeAnalyzerDomainResult`

KeyMetrics: TotalTypesAnalyzed, AvgRefFieldsPerType.

Tables:
- **Reference-heavy types** — `TopReferenceHeavyTypes`  
  Columns: TypeName | TotalFields | ReferenceFields | ValueFields | ReferenceFieldRatio | InstanceCount | IsFinalizable | IsValueType | IsArray | BaseTypeChainDepth | InterfaceCount | Category
- **Value-heavy types** — `TopValueHeavyTypes` (same columns)  
  ← *This list was absent from the old 25-section format; added here.*

---

**C3. Collection Health**  
Source: `CollectionDomainResult`

KeyMetrics: TotalCollections, WastefulCollectionCount, TotalWastedMemory, collection type breakdown (Dictionaries, Lists, HashSets, Queues, Stacks, SortedLists, SortedSets, ArrayLists).

Tables:
- **Collection inventory** — count per type as a summary bar  
  ← *The collection-type breakdown was buried or absent in old format.*
- **Wasteful collections** — `TopWastefulCollections`  
  Columns: Type | Kind | Count | Capacity | FillRate | WastedMemory | Address | ElementType | ElementSize | DetectionMethod | RootDescription
- **Waste by kind** — `WasteCountsByKind`

---

**C4. Arrays**  
Source: `ArrayDomainResult`

KeyMetrics: TotalArrayObjects, TotalArrayBytes, MultiDimArrayCount, LohArrayCount, LohArrayBytes.

Tables:
- **Array types by size** — `TopArrayTypesBySize`  
  Columns: ElementTypeName | Rank | Count | TotalBytes | IsMultiDimensional
- **Large arrays (> 85 KB)** — `TopLargeArrays`  
  Columns: Address | ElementTypeName | Length | Rank | Size
- **Sparse arrays** — `TopSparseArrays`  
  Columns: Address | ElementTypeName | Length | NullOrZeroCount | SparseRatio | WastedBytes

Flag `ScanLimited` as caveat when true.

---

**C5. Boxing & Value Type Pressure**  
Source: `BoxingDomainResult`

KeyMetrics: TotalBoxedObjects, TotalBoxedBytes, BoxedEnumCount, BoxedEnumBytes, OversizedValueTypeCount.

Tables:
- **Top boxed types** — `TopBoxedTypes`  
  Columns: ValueTypeName | BoxCount | TotalBoxBytes | IsEnum
- **Struct padding waste** — `TopPaddingWasteTypes`  
  Columns: TypeName | TotalFieldBytes | StructSize | WastedPaddingBytes | WasteRatio

Flag `TypeScanCapped` as caveat.

---

### Domain D — Threads & Concurrency

**D1. Thread Overview**  
Source: `ThreadDomainResult`

KeyMetrics: TotalThreadCount, AliveThreadCount, InactiveThreadCount, BackgroundThreadCount, GcThreadCount, BlockedThreadCount, LockHoldingThreadCount, ThreadsWithActiveExceptionsCount, FinalizerThreadBlocked, AsyncChainThreadCount, MaxAsyncChainDepth.

Tables:
- **Thread state distribution** — `ThreadStateDistribution`
- **GC mode distribution** — `GcModeDistribution`
- **Wait pattern breakdown** — `WaitPatternBreakdown`
- **Top blocked threads** — `TopBlockedThreads`  
  Columns: ThreadId | OSThreadId | LockCount | ThreadState | GcMode | WaitCategory | WaitReason | StackSizeBytes | TopFrames
- **Top lock-holding threads** — `TopLockedThreads` (same columns)
- **Threads with active exceptions** — `ThreadsWithActiveExceptions`  
  ← *Was missing from old format; now a first-class table.*  
  Columns: ThreadId | OSThreadId | ExceptionType | ExceptionMessage | LockCount | GcMode | TopFrames
- **Frame hotspots** — `TopStackHotspots`
- **Sampled thread snapshots** — `SampledThreads` (collapsed, for deep-dive)  
  ← *Raw sample data; absent from old format.*  
  Note: `SampledSnapshotCount`, `CapturedSnapshotCount`, `SamplingCapacity` shown as metadata.
- **AppDomain distribution** — `AppDomainDistribution`  
  ← *Absent from old format.*

Finalizer thread detail: FinalizerManagedThreadId, FinalizerOsThreadId, FinalizerLockCount, FinalizerFrames — shown as a dedicated sub-block.

---

**D2. Hang & Blocking**  
Source: `HangDomainResult`

LeadFinding: emit Warning when `IsStarved = true` or `HealthScore < 50`.  
← *`HealthScore` was never surfaced in the old 25-section format; now a primary KPI.*

KeyMetrics: TotalAliveThreads, WaitingThreadCount, ThreadsHoldingLocks, WaitingPercent, HealthScore, IsStarved, RuntimeMinThreads, RuntimeMaxThreads, RuntimeActiveWorkerThreads, RuntimeIdleWorkerThreads, RuntimeRetiredWorkerThreads, RuntimeQueueLength, RuntimeCpuUtilization, QueuedWorkItems, RuntimeThreadPoolDataAvailable.

Tables:
- **Wait category breakdown** — `WaitCategoryBreakdown`
- **Top waiting threads** — `TopWaitingThreads`  
  Columns: ThreadId | OSThreadId | WaitType | WaitReason | LockCount | TopStackFrame
- **Continuation types** — `TopContinuationTypes`

Note: `RuntimeQueueLength` is nullable (reflection probe); show "unavailable" when null.

---

**D3. Lock Graph & Deadlocks**  
Source: `LockGraphDomainResult`

LeadFinding: emit Critical when `DeadlockCandidateCount > 0`.

KeyMetrics: TotalHeldLocks, ContestedLockCount, MaxWaitersOnSingleLock, DeadlockCandidateCount.

Tables:
- **Deadlock cycles** — `DeadlockCandidateDetails`  
  Columns: ManagedThreadId | OsThreadId | LockObjectTypes | LockObjectAddresses | CycleSummary
- **Contested lock types** — `TopContestedLockTypes`
- **Contested lock details** — `ContestedLockDetails`  
  Columns: ObjectAddress | ObjectTypeName | WaitingThreadCount | OwnerManagedThreadId | RecursionCount

---

**D4. Event & Delegate Leaks**  
Source: `EventLeakDomainResult`

KeyMetrics: TotalEventLeakInstances, TotalSubscribers, StaticEventLeakCount, InstanceEventLeakCount, TotalEventsScanned, TotalPublisherInstances.

Tables:
- **Top publisher events (summary)** — `TopPublisherEvents`  
  Columns: PublisherType | EventFieldName | TotalSubscribers | InstanceCount | EstimatedRetainedBytes
- **Leak groups (detail)** — `TopLeakGroups`  
  Columns: PublisherType | EventFieldName | IsStatic | SeverityScore | InstanceCount | TotalSubscribers | AverageSubscribers | MinSubscribers | MaxSubscribers | EstimatedSubscriberRetainedBytes | HasDuplicateSubscriptions | HasLifetimeMismatch | OrphanedSubscriberInstances
- **Leak instances** — `TopLeakInstances`  
  Columns: PublisherType | EventFieldName | IsStatic | PublisherAddress | SeverityScore | SubscriberCount | RootHint | PublisherGeneration | DuplicateSubscriptionCount  
  Expand: `SubscriberDetails` (Type | MethodName | Size | Count) ← *method-name detail; absent from old format.*

---

### Domain E — Async & Tasks

**E1. Task Overview**  
Source: `AsyncTaskDomainResult`

KeyMetrics: TotalTasks, PendingTasks, RunningTasks, FaultedTasks, CanceledTasks, CompletedTasks, OrphanedTasks, TotalTaskContinuations, MaxContinuationDepth, AvgContinuationDepth.

LeadFinding: emit Warning when `MaxContinuationDepth > 50`.

Tables:
- **Task status summary** — scalar KPI table (Pending/Running/Faulted/Canceled/Completed/Orphaned)
- **Top pending task types** — `TopPendingTaskTypes`
- **Top faulted task types** — `TopFaultedTaskTypes`
- **Orphaned tasks** — `TopOrphanedTasks`  
  Columns: Address | TaskType | ResultType | Size | ExceptionType | ExceptionMessage
- **Deepest continuation chains** — `TopDeepestChains`  
  Columns: RootAddress | RootType | Depth | ChainTypes (sequence)  
  ← *Was ❌ missing in old format; now present.*
- **Top continuation types** — `TopContinuationTypes`

Flag `TaskScanLimited` as caveat.

---

**E2. Async State Machines**  
Source: `AsyncStateMachineDomainResult`

KeyMetrics: TotalStateMachines, TotalStateMachineBytes.

Tables:
- **State machine types** — `TopStateMachineTypes`  
  Columns: TypeName | OriginatingMethod | DeclaringType | Count | TotalBytes | AvgStateValue | ReferenceFieldCount
- **High-capture state machines** — `TopByCapturedSize`  
  Columns: Address | TypeName | TotalCapturedRefBytes | LargeCaptures (list)
- **Suspended method map** — `SuspendedMethodMap`  
  Columns: DeclaringType | MethodName | SuspendedCount | TotalBytes

Flag `ScanLimited` as caveat.

---

### Domain F — Exceptions

**F1. Exception Analysis**  
Source: `CrashDomainResult`

LeadFinding: emit Critical when `ActiveExceptions > 0` on thread stacks.

KeyMetrics: TotalExceptions, ActiveExceptions, InferredTraceCount.

Tables:
- **Exception type counts (all heap)** — `ExceptionTypeCounts`
- **Active exception type counts** — `ActiveExceptionTypeCounts`  
  ← *Split between heap-total and active was not shown in old format.*
- **Crash thread candidates** — `TopCrashThreadCandidates`  
  Columns: ThreadId | OSThreadId | ActiveExceptionCount | PrimaryExceptionType | OriginalStackTraceConfidence | OriginalStackTraceInferredFrom | TopFrames  
  Expand: OriginalStackTrace  
  ← *`InferenceConfidence` and `OriginalStackTraceInferredFrom` were absent from old format.*
- **Exception instances** — `TopExceptionInstances`  
  Columns: Type | Address | Message | HResult | InnerExceptionType | ChainDepth | IsActive | ThreadId | OSThreadId  
  Expand: OriginalStackTrace

---

### Domain G — Runtime Infrastructure

**G1. Modules & Assemblies**  
Source: `ModuleDomainResult`

KeyMetrics: TotalModules, DynamicModules, UniqueModuleNames, VersionConflictGroups.

Tables:
- **Top modules by size** — `TopModulesBySize` (LoadedModuleSnapshot)  
  Columns: Name | AssemblyName | FullPath | Address | Size | IsDynamic | IsPEFile
- **Top modules by heap memory** — `TopModulesByHeapMemory` (ModuleHeapStats)  
  Columns: ModuleName | AssemblyName | UniqueTypeCount | ObjectCount | TotalBytes
- **High type-density modules** — `HeavyTypeDensityModules` (ModuleTypeDensity)  
  Columns: ModuleName | AssemblyName | UniqueTypeCount | ObjectCount | TotalBytes | BytesPerType
- **Version conflict groups** — `ConflictDetails` (ModuleConflictGroup)  
  Columns: ModuleName | conflicting instances (path, address, size)

---

**G2. AppDomains**  
Source: `AppDomainDomainResult`

KeyMetrics: TotalDomains, AnonymousModuleCount, TotalDynamicModules, DynamicModuleBytes, ExcludedModuleCount.

Tables:
- **AppDomain inventory** — `Domains` (AppDomainSnapshot)  
  Columns: Domain Name | ID | Address | Module Count | EstimatedManagedBytes
- **Top modules by type count** — `TopModulesByTypeCount` (ModuleTypeCountEntry)  
  Columns: Module | Assembly | Types | Live Types | Objects | Bytes

---

**G3. JIT & Code Footprint**  
Source: `JitDomainResult`

KeyMetrics: TotalJitHeapBytes, JitManagerCount, JitHeapPctOfTotalProcess, ActiveMethodsOnStacks, TieredMethodCount, UnmanagedFrameCount, ManagedFrameCount.

Tables:
- **Top JIT-compiled methods by size** — `TopLargestMethods`  
  Columns: Signature | DeclaringType | NativeCodeAddress | HotSize | ColdSize | IsTiered  
  Flag: HotSize + ColdSize > 65536 = "oversized" annotation
- **Active frame types** — `TopActiveFrameTypes`

---

### Cross-Domain Insights

All `InsightFinding` records from `InsightEngine` that span more than one domain (cross-correlation findings, failed-analyzer warnings, boxing×GC correlations, etc.) are rendered here in a single ranked table after all domain sections.

Columns: Severity | Analyzer | Category | Title | Evidence | Recommendation | ConfidenceScore | Caveats | Tags

---

### Appendix

**Z1. Analyzer Run Summary**  
Source: `AnalyzerRunResult[]`

Per-analyzer row:

| Column | Source |
|---|---|
| Analyzer | `AnalyzerName` |
| Status | `Status` |
| Duration | `Duration` |
| Objects Scanned | `Diagnostics.ObjectScanCount` |
| Cache Hits | `Diagnostics.CacheHits` |
| Cache Misses | `Diagnostics.CacheMisses` |
| Findings | `FindingCount` |
| Warnings | `WarningCount` |
| Skip Reason | `SkipReason` |
| Error | `ErrorMessage` |
| Finding Gen Error | `Diagnostics.FindingGeneratorError` |

Summary: Completed N / Failed N / Skipped N / TimedOut N.

---

**Z2. Memory Diagnostics** *(present only when `--memory-diagnostics` enabled)*  
Source: `AnalyzerRunResult.Diagnostics.MemoryStats` per analyzer.

| Column | Source |
|---|---|
| Analyzer | `AnalyzerName` |
| WS Before | `MemoryStats.WorkingSetBefore` |
| WS After | `MemoryStats.WorkingSetAfter` |
| WS Delta | `MemoryStats.WorkingSetDelta` |
| MH Before | `MemoryStats.ManagedHeapBefore` |
| MH After | `MemoryStats.ManagedHeapAfter` |
| MH Delta | `MemoryStats.ManagedHeapDelta` |

---

**Z3. Known Limitations**  

| Limitation | Affected Sections |
|---|---|
| Retained size is bounded BFS, not true dominator | A3, A4 |
| GC root retained bytes are avg-size estimates | A5 |
| Allocation sites unavailable from `.dmp` (require ETW) | B2 |
| Gen byte counts approximated (avg × gen count, not per-object) | B1 |
| Task orphan detection relies on CLR private field name stability | E1 |
| FOH/POH sizes include runtime-internal objects | B3 |
| `ClrThread.StackBase/StackLimit` may be 0 for GC/finalizer threads | D1 |
| Deadlock detection misses cooperative waits without `BlockingObjects` | D3 |
| String encoding waste (UTF-16 vs ASCII) not detected | A7 |
| Async state machine state-value distribution not available (avg only) | E2 |
| Collection generation field not yet available | C3 |
| Gen0/Gen1 pinned object generation correlation not computed | B7 |
| Weak reference handle scan capped at 50 000 entries; totals may be underestimated | B8 |
| `RuntimeQueueLength` is a reflection probe; may be null | D2 |

---

## Rendering Rules

### Ordering
1. `HealthScorecard` always renders first.
2. Domains ordered by `max(InsightFinding.Severity)` across all findings in that domain.
3. Within a domain, sections ordered by their lead finding severity.
4. Domains with no findings or all-skipped analyzers go last.

### Collapsing (HTML only)
- Every `Table` block is collapsed behind "Show N rows" toggle.
- `KeyMetrics` strip is always visible (no collapse).
- `LeadFinding` block is always visible (no collapse).
- `Provenance` block is collapsed by default.

### Finding display
```
[🔴 Critical] Title of finding                           ●●●● High
  Evidence: metric-grounded sentence
  Recommendation: actionable sentence
  Caveats: heuristic disclosure if any
```

### Null / missing data
- When an analyzer was skipped: show `⚪ Skipped — {SkipReason}` in the section header, omit all tables.
- When an analyzer failed: show `⚠️ Failed — {ErrorMessage}`, omit tables, preserve any partial findings.
- When a nullable list is empty: omit the table entirely; do not render an empty table.

### Stable section anchors
Every `ReportSection` must emit a stable `id` attribute (HTML) / JSON key:
`{domain-letter}{section-number}` e.g. `A1`, `B4`, `D3`.
Cross-references within finding text use these IDs.

### JSON output
- Section ordering matches the rendered document order (severity-sorted).
- All lists are fully emitted (no top-N truncation in JSON).
- `ReportSection.provenance` block is always present in JSON.
- `HealthScorecard` is the first key in the JSON document.
