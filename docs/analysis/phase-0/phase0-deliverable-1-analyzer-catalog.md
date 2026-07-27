# Phase 0 — Deliverable 1: Analyzer Catalog

> Scope: this document covers **Deliverable 1 only** (Analyzer Catalog) from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Other deliverables (capability matrix, dependency graph, roadmap, etc.) are out of scope here.

Reviewed as a static architecture pass over `src/DumpDetective.Analysis/Analyzers/`
(33 `IAnalyzer` implementations, down from the original 36 — `RetentionAnalyzer` was merged into
`DominatorAnalyzer`, `DependentHandleAnalyzer` was merged into `GCHandleAnalyzer`, and
`AppDomainAnalyzer` was merged into `ModuleAnalyzer`, per
[Deliverable 6](phase0-deliverable-6-analyzer-boundary-review.md) and
[Deliverable 10 P0 item 3](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0))
and their registration in `DefaultAnalyzerFeatureModuleCatalog`. Implementations were not
deep-reviewed line by line — per Phase 0 instructions, this is an architectural pass, not an
implementation review.

This catalog's `Heap Scan Mode` column has not been individually re-audited row-by-row against
source — treat any given row's `Index`/`Index+Container` label as indicative, not verified, until
cross-checked against code. Direct grep verification against
`HeapAnalysisCache.EnumerateIndexedEntries()` / `EnumerateIndexedEntriesAsTuples()` call sites
(see [Deliverable 10, Current State](phase0-deliverable-10-platform-roadmap.md#current-state))
found that only **9** analyzers actually stream the on-disk object index this way; a further 5
perform a full `ClrHeap.EnumerateObjects()` sweep that this catalog's Index/Index+Container labels
do not distinguish from index streaming.

## Legend — Heap Scan Mode

| Mode | Meaning |
|---|---|
| **Index** | Reads the Phase-1 disk-backed object index (`DumpDetective.Analysis.Indexing`) — no independent full heap re-scan |
| **Index+Container** | Reads a specialized on-disk container/satellite index (arrays, tasks, LOH, weak refs) in addition to/instead of the main object index |
| **Cache-only** | Uses `HeapAnalysisCache` (type metadata) but walks a ClrMD-native source directly (handles, threads, segments) rather than the object index |
| **Direct ClrMD** | No shared index or cache — talks straight to `Microsoft.Diagnostics.Runtime` APIs (segments, JIT heap, etc.) |

## Analyzer Catalog

| Order | Analyzer (class) | Tags | Primary Responsibility | Evidence / Outputs | Dependencies | Heap Scan Mode | Status |
|---|---|---|---|---|---|---|---|
| 100 | `MemoryAnalyzer` | memory | Top-level heap summary: total bytes, object count, top types | Executive summary stats | Cache, Indexing | Index | Clear — runs first by design |
| 110 | `GCGenerationAnalyzer` | gc | Per-generation (Gen0/1/2/LOH) object/byte distribution | Generation breakdown | Cache, Indexing | Index | Clear |
| 120 | `AllocationPatternAnalyzer` | gc, allocation | Classifies allocation profile/pressure (Gen0 churn vs LOH-heavy etc.) | Profile + pressure classification | Cache, Indexing | Index | Clear |
| 130 | `ObjectShapeAnalyzer` | types | Type hierarchy shape stats (base-type depth, field layout) | Shape/depth stats | Cache, Indexing | Index | Clear |
| 140 | `GCRootAnalyzer` | roots | Root enumeration & categorization (stack/static/handle roots) | Root categories, counts | Cache, Indexing, Traversal | Index | Clear |
| 150 | `HeapTopologyAnalyzer` | heap | GC segment layout: committed/reserved bytes, per-segment top types | Segment topology | Core.Abstractions, `Pipeline` (⚠ see flags) | Direct ClrMD | ⚠ Unusual dependency on `Analysis.Pipeline` from an analyzer |
| 160 | `ModuleAnalyzer` | runtime | Loaded module/assembly inventory + version-conflict detection, plus per-AppDomain module/type/object stats (absorbed from the merged `AppDomainAnalyzer`) | Module list, version conflicts, `AppDomainSnapshot` domains | Cache, Indexing, Utilities | Index | Clear — sole module/AppDomain provider after the merge |
| 170 | `CrashAnalyzer` | exceptions | Active/historical exception analysis, crash-thread snapshots, stack traces | Exception chains, crash threads | Indexing, Cache, Utilities | Index | Clear, but large (898 lines) |
| 180 | `HangAnalyzer` | threads | Threadpool health/hang detection: wait-pattern, lock-holding, continuation backlog | Health score, wait threads, threadpool stats | Indexing, Cache, Utilities | Index | ⚠ Duplicates wait-pattern logic with `ThreadAnalyzer` |
| 190 | `AsyncTaskAnalyzer` | async | `Task`/`Task<T>` status, continuation chains, faulted-task exception extraction | Task status breakdown, top faulted tasks | Indexing, **Indexing.Satellite**, Traversal, Utilities | Index+Container | ✅ Task-index format (`TaskIndexWriter`) moved under `Indexing/Satellite`, alongside the weak-ref/handle-snapshot satellite formats (P1 item 10) — no longer a standalone private format; still overlaps `HangAnalyzer`/`CrashAnalyzer` exception extraction |
| 210 | `LeakCandidateAnalyzer` | leaks | Scores/ranks leak candidates from heap+handle+retention signals | Ranked leak candidates w/ severity | Cache, Indexing, Utilities, Enums | Index | ⚠ No single owner of "leak" scoring — see cross-analyzer flag below |
| 220 | `DominatorAnalyzer` | retention, dominator | Dominator/retained-size estimation, plus highly-referenced-object detection via incoming-ref counting (absorbed from the merged `RetentionAnalyzer`) | Dominator stats, top retention types, high-fan-in objects | Cache, Indexing, Utilities | Index | Clear — sole retained-size/high-fan-in provider after the merge |
| 230 | `StringAnalyzer` | memory, string | String duplication/waste analysis (fingerprinting, FOH detection, top duplicates) | Duplicate groups, wasted bytes | Cache, Indexing, Utilities | Index | Clear — large (991 lines) but single-purpose, not scope creep |
| 240 | `CollectionAnalyzer` | collections | BCL collection introspection (Dictionary/List/Queue/HashSet…), fill-rate/waste analysis | Wasteful collections, waste-by-kind | Cache, Indexing, Utilities, **Logging** | Index | 🚩 Largest analyzer (1702 lines/107 symbols); optional `ILogger<T>` dependency reclassified (P1 item 10) as a documented analyzer pattern — see [architecture.md §14 Observability](../../architecture.md#14--observability) — not an infra-boundary outlier; owns its own reflection field-layout cache |
| 250 | `StaticRootLeakDetector` | roots, leaks | Static-field root scan for large retained subgraphs | Retained subgraph size, contains-collections/events flags | Cache, Utilities, Enums | Cache-only | ⚠ Near-duplicate static-field sweep vs `EventLeakAnalyzer.SweepModuleStaticFields` |
| 260 | `ReferenceChainAnalyzer` | roots | On-demand root-path finding for a given object/type (bidirectional BFS) | Root path(s), telemetry counters | Cache, Traversal, Utilities | Cache-only (BFS over index-backed cache) | ⚠ Should arguably be the sole root-path evidence provider — see flag below |
| 270 | `GCHandleAnalyzer` | handles | GC handle table enumeration by kind, target resolution, plus `DependentHandle` (conditional weak table) target resolution (absorbed from the merged `DependentHandleAnalyzer`) | Handle counts by kind, targets, dependent-handle pairs | Cache, Utilities, Enums | Cache-only | ⚠ Still overlaps `WeakReferenceAnalyzer` |
| 290 | `LohFragmentationAnalyzer` | gc, loh | LOH segment fragmentation: free-block histogram, largest objects | Fragmentation %, free-gap histogram | Cache, Indexing, **Container** | Index+Container | Clear |
| 300 | `ThreadStackClusterAnalyzer` | threads | Clusters threads by stack signature (dedupe similar stacks) | Stack clusters, sample addresses | Cache, Utilities | Cache-only | ⚠ Duplicate stack-walk work vs `ThreadAnalyzer`/`HangAnalyzer` |
| 310 | `ThreadAnalyzer` | threads | Full thread inventory: state, wait reason, exceptions, stack-root counts, hotspots | Thread categorization, distributions | Cache, Utilities, Enums | Cache-only | ⚠ Duplicates `DetectWaitPattern` from `HangAnalyzer` |
| 320 | `LockGraphAnalyzer` | threads, locks | Monitor/lock ownership graph + deadlock-candidate detection | Contested locks, deadlock candidates | Cache, Utilities, Enums | Cache-only | ⚠ Overlaps thread-domain analyzers above |
| 330 | `EventLeakAnalyzer` | events, leaks | Event-handler subscriber leak detection: delegate scanning, static publisher sweep, lifetime mismatch | Leak groups, subscriber counts, severity | Indexing, Cache, Utilities, Enums | Index | 🚩 2nd largest analyzer (1415 lines/67 symbols); duplicates static-field sweep with `StaticRootLeakDetector` |
| 340 | `FinalizableObjectAnalyzer` | gc | Finalizable/undisposed object detection + BFS retained-size estimate | Undisposed instances, retained bytes | Cache, Indexing | Index | Clear |
| 350 | `AsyncStateMachineAnalyzer` | async | Detects async state machine instances (`IAsyncStateMachine`) | State machine counts by method | Cache, Indexing | Index | Clear |
| 360 | `ArrayAnalyzer` | types | Array size/shape statistics, large-array detection | Array size distribution | Cache, Indexing, **Container** | Index+Container | Clear |
| 380 | `SegmentReservationAnalyzer` | gc, segments | GC segment reserved-vs-committed memory (VM reservation waste) | Reserved/committed bytes | Models only | Direct ClrMD | Clear, correctly isolated (no index needed) |
| 390 | `WeakReferenceAnalyzer` | gc | `WeakReference`/`WeakReference<T>` inventory via satellite index | Weak-ref counts by kind | Cache, Indexing, **Indexing.Satellite** | Index+Container | ⚠ 3rd analyzer in the handle/weak-ref space |
| 400 | `BoxingAnalyzer` | types, perf | Boxed value-type detection & wasted-byte estimate | Boxing waste stats | Cache, Indexing | Index | Clear |
| 410 | `JitAnalyzer` | runtime, perf | JIT-compiled method inventory (hot/cold code size) | Top JIT methods by size | Models only | Direct ClrMD | Clear, correctly isolated (JIT heap ≠ object heap) |
| 420 | `DbConnectionAnalyzer` | infra, network | `DbConnection`-derived object state sampling (Open/Closed) | Connection state histogram | Cache, Indexing | Index | ✅ Now implements shared `ITypedResourceCandidateSource`/`ITypedResourceInstanceSampler<T>` via `TypedResourceScanDriver` (see below) |
| 430 | `WcfChannelAnalyzer` | infra, network | WCF channel/proxy `CommunicationState` sampling | Channel state histogram | Cache, Indexing | Index | ✅ Now implements shared `ITypedResourceCandidateSource`/`ITypedResourceInstanceSampler<T>` via `TypedResourceScanDriver` |
| 440 | `HttpObjectAnalyzer` | infra, network | HttpClient/ServicePoint/HttpMessageHandler categorization | Category counts | Cache, Indexing | Index | ✅ Now implements shared `ITypedResourceCandidateSource` (no per-instance state sampling needed) via `TypedResourceScanDriver` |
| 450 | `TimerLeakAnalyzer` | infra, timers | `System.Threading.Timer`/`System.Timers.Timer` categorization & leak signal | Timer category counts | Cache, Indexing | Index | ✅ Now implements shared `ITypedResourceCandidateSource` via `TypedResourceScanDriver` |

## Findings

### 1. Scope creep

- **`CollectionAnalyzer`** (1702 lines, 107 symbols) is the largest analyzer by a wide margin. It
  owns a private reflection-based `FieldLayout` cache and dual parallel/sequential-disk code
  paths. It remains the only analyzer with a `Microsoft.Extensions.Logging` dependency, but as of
  P1 item 10 that's now a documented, intentional pattern (`CLAUDE.md` / architecture.md §14) for
  analyzers scanning large object populations, resolved via `ActivatorUtilities` in
  `DefaultAnalyzerFactory` — no longer flagged as an inconsistent infra boundary, just the first
  (so far only) analyzer to use the pattern.
- **`EventLeakAnalyzer`** (1415 lines, 67 symbols) is close behind, and duplicates static-field
  sweep logic that also lives in `StaticRootLeakDetector` (**still unresolved** — see §2 below).
  It now also has a companion `EventLeakFastScanner.cs` (45KB) extracted alongside it, which grows
  the event-leak surface area further rather than shrinking it.

### 2. Duplicate / near-duplicate work

- **✅ RESOLVED — "Resource state sampler" quartet.** `DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
  `HttpObjectAnalyzer`, `TimerLeakAnalyzer` previously each independently implemented
  classify-by-type-name → sample state field → bucket by state. They now share
  `ITypedResourceCandidateSource` / `ITypedResourceInstanceSampler<T>` (
  `Analyzers/ITypedResourceCandidateSource.cs`), driven through the shared
  `TypedResourceScanDriver` and `TypedResourceSampler`/`InstanceStateSampler<T>` helper
  (`Analyzers/TypedResourceScanDriver.cs`, `Analyzers/TypedResourceSampler.cs`). This closes the
  duplication flagged in the original pass.
- **Thread-domain cluster** — ⚠ Partially resolved. `ThreadAnalyzer`, `HangAnalyzer`,
  `ThreadStackClusterAnalyzer`, and `LockGraphAnalyzer` no longer walk thread stacks
  independently: all four now implement `IThreadStackScanParticipant`
  (`Pipeline/IThreadStackScanParticipant.cs`) and receive frames via a single shared
  `OnThreadStack` scan pass. However, wait-pattern *detection* is still duplicated: `ThreadAnalyzer`
  (`ProcessThread`) delegates to the new shared `ThreadWaitClassifier.Classify`
  (`Analyzers/ThreadWaitClassifier.cs`), but `HangAnalyzer` retains its own separate
  `DetectWaitPattern(ClrThread, ClrStackFrame)` (line 391) with independent logic — the two
  analyzers do not share wait-classification.
- **Static-field sweep** — `StaticRootLeakDetector.AnalyzeStaticRoots` and
  `EventLeakAnalyzer.SweepModuleStaticFields` cover overlapping ground (static fields retaining
  large subgraphs / delegates) with separate implementations.
- **Handle/weak-reference space** — `GCHandleAnalyzer` (now including the former
  `DependentHandleAnalyzer`'s dependent-handle resolution) and `WeakReferenceAnalyzer` still
  enumerate overlapping parts of the GC handle table with no documented boundary between them.
- **Retention/leak scoring** — `DominatorAnalyzer` (now including the former `RetentionAnalyzer`'s
  high-fan-in signal), `LeakCandidateAnalyzer`, and `ReferenceChainAnalyzer` each compute their own
  notion of "how much does this object retain" / "is this a leak" rather than sharing one
  retained-size or confidence-scoring service.

### 3. Unclear ownership / naming

- **Resolved**: the former `MemoryLeakAnalyzer.cs` file/`RetentionAnalyzer` class/`"retention"`
  catalog-key mismatch no longer exists — that file was deleted and its logic merged into
  `DominatorAnalyzer` (module key `"dominator"`).
- Given the module keys `leak-candidate`, `static-root`, and `dominator` all still exist
  as separate registrations, a newcomer has no way to tell from names alone which one to consult
  for "why is this object still alive" — that job is architecturally closest to
  `ReferenceChainAnalyzer`, but it isn't positioned as the canonical entry point.
- **Resolved**: `ModuleAnalyzer` vs `AppDomainAnalyzer` — there is no longer an `AppDomainAnalyzer`
  class; its per-module/assembly-by-AppDomain stats were merged into `ModuleAnalyzer`
  (`AnalyzeAppDomains`/`AppDomainAnalysisResult`, folded into `ModuleDomainResult.Domains`). Same
  merge pattern as `RetentionAnalyzer`→`DominatorAnalyzer`, and already tracked in
  [Deliverable 6](phase0-deliverable-6-analyzer-boundary-review.md) — this catalog just hadn't
  been updated to reflect it.

### 4. Dependency outliers worth a second look

- **✅ RESOLVED** — `HeapTopologyAnalyzer` no longer imports `DumpDetective.Analysis.Pipeline`.
  Its current usings are `Microsoft.Diagnostics.Runtime`, `DumpDetective.Core.Abstractions`,
  `DumpDetective.Core.Models`, `DumpDetective.Core.Options`, and `DumpDetective.Analysis.Models` —
  no orchestration-layer dependency remains.
- `SegmentReservationAnalyzer` and `JitAnalyzer` have no `Cache`/`Indexing` dependency at all —
  this is correct (JIT code heap and segment reservation data aren't part of the object index),
  not a flag, but it's worth documenting explicitly so future contributors don't "fix" it by
  wiring them into the index unnecessarily.
- **✅ RESOLVED** — `AsyncTaskAnalyzer`'s binary "task index" (`TaskIndexWriter`,
  `TaskIndexMagic`/`TaskIndexVersion`/`RecordSize`) was moved into
  `Indexing/Satellite/TaskIndexWriter.cs` (P1 item 10), placing it alongside the other
  satellite-index formats (weak-ref, handle-snapshot) rather than as a standalone private format.
  It's still a second on-disk format distinct from the main object index in
  [binary-format.md](../binary-format.md), but is now consistently homed with its peers.

### 5. Analyzers with obvious, well-scoped purpose (no flags)

`MemoryAnalyzer`, `GCGenerationAnalyzer`, `AllocationPatternAnalyzer`, `ObjectShapeAnalyzer`,
`GCRootAnalyzer`, `LohFragmentationAnalyzer`, `FinalizableObjectAnalyzer`,
`AsyncStateMachineAnalyzer`, `ArrayAnalyzer`, `BoxingAnalyzer`, `StringAnalyzer`,
`SegmentReservationAnalyzer`, and `JitAnalyzer` each have a single, clearly named
responsibility with outputs that map cleanly to their name and no overlapping logic found
elsewhere in the catalog.
