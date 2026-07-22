# Phase 0 — Deliverable 1: Analyzer Catalog

> Scope: this document covers **Deliverable 1 only** (Analyzer Catalog) from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Other deliverables (capability matrix, dependency graph, roadmap, etc.) are out of scope here.

Reviewed as a static architecture pass over `src/DumpDetective.Analysis/Analyzers/`
(36 `IAnalyzer` implementations) and their registration in
`DefaultAnalyzerFeatureModuleCatalog`. Implementations were not deep-reviewed line by line —
per Phase 0 instructions, this is an architectural pass, not an implementation review.

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
| 160 | `ModuleAnalyzer` | runtime | Loaded module/assembly inventory + version-conflict detection | Module list, version conflicts | Cache, Indexing, Utilities | Index | ⚠ Overlaps `AppDomainAnalyzer` |
| 170 | `CrashAnalyzer` | exceptions | Active/historical exception analysis, crash-thread snapshots, stack traces | Exception chains, crash threads | Indexing, Cache, Utilities | Index | Clear, but large (898 lines) |
| 180 | `HangAnalyzer` | threads | Threadpool health/hang detection: wait-pattern, lock-holding, continuation backlog | Health score, wait threads, threadpool stats | Indexing, Cache, Utilities | Index | ⚠ Duplicates wait-pattern logic with `ThreadAnalyzer` |
| 190 | `AsyncTaskAnalyzer` | async | `Task`/`Task<T>` status, continuation chains, faulted-task exception extraction | Task status breakdown, top faulted tasks | Indexing, **Container**, Traversal, Utilities | Index+Container | ⚠ Owns a private on-disk "task index" format; overlaps `HangAnalyzer`/`CrashAnalyzer` exception extraction |
| 200 | `RetentionAnalyzer` (file `MemoryLeakAnalyzer.cs`) | memory, retention | Highly-referenced-object detection via incoming-ref counting as a leak signal | Top retention types, high-fan-in objects | Indexing, Cache, Utilities | Index | ⚠ Class/file name mismatch; overlaps `LeakCandidateAnalyzer`/`DominatorAnalyzer` |
| 210 | `LeakCandidateAnalyzer` | leaks | Scores/ranks leak candidates from heap+handle+retention signals | Ranked leak candidates w/ severity | Cache, Indexing, Utilities, Enums | Index | ⚠ No single owner of "leak" scoring — see cross-analyzer flag below |
| 220 | `DominatorAnalyzer` | retention, dominator | Dominator/retained-size estimation | Dominator stats | Cache, Indexing, Utilities | Index | ⚠ Overlaps retention estimation done elsewhere |
| 230 | `StringAnalyzer` | memory, string | String duplication/waste analysis (fingerprinting, FOH detection, top duplicates) | Duplicate groups, wasted bytes | Cache, Indexing, Utilities | Index | Clear — large (991 lines) but single-purpose, not scope creep |
| 240 | `CollectionAnalyzer` | collections | BCL collection introspection (Dictionary/List/Queue/HashSet…), fill-rate/waste analysis | Wasteful collections, waste-by-kind | Cache, Indexing, Utilities, **Logging** | Index | 🚩 Largest analyzer (1702 lines/107 symbols); only analyzer taking a logging dependency; owns its own reflection field-layout cache |
| 250 | `StaticRootLeakDetector` | roots, leaks | Static-field root scan for large retained subgraphs | Retained subgraph size, contains-collections/events flags | Cache, Utilities, Enums | Cache-only | ⚠ Near-duplicate static-field sweep vs `EventLeakAnalyzer.SweepModuleStaticFields` |
| 260 | `ReferenceChainAnalyzer` | roots | On-demand root-path finding for a given object/type (bidirectional BFS) | Root path(s), telemetry counters | Cache, Traversal, Utilities | Cache-only (BFS over index-backed cache) | ⚠ Should arguably be the sole root-path evidence provider — see flag below |
| 270 | `GCHandleAnalyzer` | handles | GC handle table enumeration by kind, target resolution | Handle counts by kind, targets | Cache, Utilities | Cache-only | ⚠ Overlaps `DependentHandleAnalyzer`/`WeakReferenceAnalyzer` |
| 280 | `DependentHandleAnalyzer` | handles | `DependentHandle` (conditional weak table) enumeration | Dependent-handle pairs | Cache, Utilities, Enums | Cache-only | ⚠ Overlaps `GCHandleAnalyzer`/`WeakReferenceAnalyzer` — unclear boundary |
| 290 | `LohFragmentationAnalyzer` | gc, loh | LOH segment fragmentation: free-block histogram, largest objects | Fragmentation %, free-gap histogram | Cache, Indexing, **Container** | Index+Container | Clear |
| 300 | `ThreadStackClusterAnalyzer` | threads | Clusters threads by stack signature (dedupe similar stacks) | Stack clusters, sample addresses | Cache, Utilities | Cache-only | ⚠ Duplicate stack-walk work vs `ThreadAnalyzer`/`HangAnalyzer` |
| 310 | `ThreadAnalyzer` | threads | Full thread inventory: state, wait reason, exceptions, stack-root counts, hotspots | Thread categorization, distributions | Cache, Utilities, Enums | Cache-only | ⚠ Duplicates `DetectWaitPattern` from `HangAnalyzer` |
| 320 | `LockGraphAnalyzer` | threads, locks | Monitor/lock ownership graph + deadlock-candidate detection | Contested locks, deadlock candidates | Cache, Utilities, Enums | Cache-only | ⚠ Overlaps thread-domain analyzers above |
| 330 | `EventLeakAnalyzer` | events, leaks | Event-handler subscriber leak detection: delegate scanning, static publisher sweep, lifetime mismatch | Leak groups, subscriber counts, severity | Indexing, Cache, Utilities, Enums | Index | 🚩 2nd largest analyzer (1415 lines/67 symbols); duplicates static-field sweep with `StaticRootLeakDetector` |
| 340 | `FinalizableObjectAnalyzer` | gc | Finalizable/undisposed object detection + BFS retained-size estimate | Undisposed instances, retained bytes | Cache, Indexing | Index | Clear |
| 350 | `AsyncStateMachineAnalyzer` | async | Detects async state machine instances (`IAsyncStateMachine`) | State machine counts by method | Cache, Indexing | Index | Clear |
| 360 | `ArrayAnalyzer` | types | Array size/shape statistics, large-array detection | Array size distribution | Cache, Indexing, **Container** | Index+Container | Clear |
| 370 | `AppDomainAnalyzer` | runtime | Per-module/assembly type & object stats grouped by AppDomain | Module/type/object counts | Cache, Indexing | Index | ⚠ Overlaps `ModuleAnalyzer` |
| 380 | `SegmentReservationAnalyzer` | gc, segments | GC segment reserved-vs-committed memory (VM reservation waste) | Reserved/committed bytes | Models only | Direct ClrMD | Clear, correctly isolated (no index needed) |
| 390 | `WeakReferenceAnalyzer` | gc | `WeakReference`/`WeakReference<T>` inventory via satellite index | Weak-ref counts by kind | Cache, Indexing, **Indexing.Satellite** | Index+Container | ⚠ 3rd analyzer in the handle/weak-ref space |
| 400 | `BoxingAnalyzer` | types, perf | Boxed value-type detection & wasted-byte estimate | Boxing waste stats | Cache, Indexing | Index | Clear |
| 410 | `JitAnalyzer` | runtime, perf | JIT-compiled method inventory (hot/cold code size) | Top JIT methods by size | Models only | Direct ClrMD | Clear, correctly isolated (JIT heap ≠ object heap) |
| 420 | `DbConnectionAnalyzer` | infra, network | `DbConnection`-derived object state sampling (Open/Closed) | Connection state histogram | Cache, Indexing | Index | 🔁 Duplicate "resource state sampler" shape (see below) |
| 430 | `WcfChannelAnalyzer` | infra, network | WCF channel/proxy `CommunicationState` sampling | Channel state histogram | Cache, Indexing | Index | 🔁 Duplicate "resource state sampler" shape |
| 440 | `HttpObjectAnalyzer` | infra, network | HttpClient/ServicePoint/HttpMessageHandler categorization | Category counts | Cache, Indexing | Index | 🔁 Duplicate "resource state sampler" shape |
| 450 | `TimerLeakAnalyzer` | infra, timers | `System.Threading.Timer`/`System.Timers.Timer` categorization & leak signal | Timer category counts | Cache, Indexing | Index | 🔁 Duplicate "resource state sampler" shape |

## Findings

### 1. Scope creep

- **`CollectionAnalyzer`** (1702 lines, 107 symbols) is the largest analyzer by a wide margin. It
  owns a private reflection-based `FieldLayout` cache, dual parallel/sequential-disk code paths,
  and is the **only** analyzer with a `Microsoft.Extensions.Logging` dependency — every other
  analyzer is logging-free. That's an inconsistent infrastructure boundary, not just a big file.
- **`EventLeakAnalyzer`** (1415 lines, 67 symbols) is close behind, and duplicates static-field
  sweep logic that also lives in `StaticRootLeakDetector`.

### 2. Duplicate / near-duplicate work

- **"Resource state sampler" quartet** — `DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
  `HttpObjectAnalyzer`, `TimerLeakAnalyzer` all independently implement the same shape:
  classify-by-type-name → sample state field → bucket by state, each with its own
  `MaxStateSamples`/`StateFieldNames`-style constants. This is a strong candidate for a shared
  "typed resource sampler" helper (Deliverable 5 territory, flagged here because the duplication
  is visible at the catalog level).
- **Thread-domain cluster** — `ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`,
  `LockGraphAnalyzer` all walk thread stacks independently. `ThreadAnalyzer` and `HangAnalyzer`
  both implement their own `DetectWaitPattern`.
- **Static-field sweep** — `StaticRootLeakDetector.AnalyzeStaticRoots` and
  `EventLeakAnalyzer.SweepModuleStaticFields` cover overlapping ground (static fields retaining
  large subgraphs / delegates) with separate implementations.
- **Handle/weak-reference space** — `GCHandleAnalyzer`, `DependentHandleAnalyzer`,
  `WeakReferenceAnalyzer` all enumerate overlapping parts of the GC handle table with no
  documented boundary between them.
- **Retention/leak scoring** — `RetentionAnalyzer`, `LeakCandidateAnalyzer`, `DominatorAnalyzer`,
  and `ReferenceChainAnalyzer` each compute their own notion of "how much does this object
  retain" / "is this a leak" rather than sharing one retained-size or confidence-scoring service.

### 3. Unclear ownership / naming

- `MemoryLeakAnalyzer.cs` defines a class called **`RetentionAnalyzer`**, registered in the
  catalog as `"retention"`. The file name, class name, and catalog key all disagree — this makes
  the analyzer hard to find and easy to confuse with `LeakCandidateAnalyzer` or
  `StaticRootLeakDetector`, which sound like they'd own "retention"/"leak" but don't.
- Given the module keys `retention`, `leak-candidate`, `static-root`, and `dominator` all exist
  as separate registrations, a newcomer has no way to tell from names alone which one to consult
  for "why is this object still alive" — that job is architecturally closest to
  `ReferenceChainAnalyzer`, but it isn't positioned as the canonical entry point.
- `ModuleAnalyzer` vs `AppDomainAnalyzer`: both build per-module/assembly type and object
  statistics. Nothing in the naming or catalog tags (`runtime` for both) distinguishes their
  responsibilities.

### 4. Dependency outliers worth a second look

- `HeapTopologyAnalyzer` imports `DumpDetective.Analysis.Pipeline` — the only analyzer with a
  dependency that looks like it belongs to the orchestration layer rather than analysis logic.
  Worth confirming this isn't a layering violation in Deliverable 7.
- `SegmentReservationAnalyzer` and `JitAnalyzer` have no `Cache`/`Indexing` dependency at all —
  this is correct (JIT code heap and segment reservation data aren't part of the object index),
  not a flag, but it's worth documenting explicitly so future contributors don't "fix" it by
  wiring them into the index unnecessarily.
- `AsyncTaskAnalyzer` maintains its own binary "task index" file format
  (`TaskIndexMagic`/`TaskIndexVersion`/`RecordSize`) independent of the main object index format
  described in [binary-format.md](../binary-format.md) — a second on-disk format to maintain.

### 5. Analyzers with obvious, well-scoped purpose (no flags)

`MemoryAnalyzer`, `GCGenerationAnalyzer`, `AllocationPatternAnalyzer`, `ObjectShapeAnalyzer`,
`GCRootAnalyzer`, `LohFragmentationAnalyzer`, `FinalizableObjectAnalyzer`,
`AsyncStateMachineAnalyzer`, `ArrayAnalyzer`, `BoxingAnalyzer`, `StringAnalyzer`,
`SegmentReservationAnalyzer`, and `JitAnalyzer` each have a single, clearly named
responsibility with outputs that map cleanly to their name and no overlapping logic found
elsewhere in the catalog.
