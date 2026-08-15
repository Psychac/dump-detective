# DumpDetective — Architecture

Ground truth verified directly against source on `upgrade/clrmd-4`.

---

## 1. Overview

DumpDetective is a high-performance .NET memory dump analyzer built on
Microsoft.Diagnostics.Runtime (ClrMD) **4.0.732401**. It is designed to:

- Analyze extremely large memory dumps (1GB–25GB+)
- Operate under bounded memory regardless of dump size
- Provide actionable diagnostics (leaks, retention, thread issues, async/task health,
  infra resource leaks) via a fixed pipeline of independent analyzers

Design goals:
- Scale to very large dumps via disk-backed indices and streaming, not full materialization
- Keep analyzer contracts small and stable to minimize ripple across projects
- Make reporting depend on Analysis via stable models (Core stays minimal and stable)

---

## 2. Project layout and dependency graph

- **Core** — small, stable contracts and domain primitives (`IAnalyzer`, `IFindingGenerator`,
  `AnalyzerDomainResult`, `IHeapAnalysisCache`)
- **Analysis** — dump loading, heap indexing/cache, analyzers, graph traversal
- **Reporting** — finding generators, section builders, report composition/formatters, trend analysis
- **Cli** — entrypoint, staged pipeline orchestration, DI registration, hosting

```
Cli -> Analysis, Reporting, Core
Reporting -> Analysis, Core
Analysis -> Core
Core -> ClrMD (runtime dependency)
```

Key contracts:
- `IDumpLoader` (Analysis.Dump) — load dump, resolve DAC, produce `DumpLoadContext`
- `RuntimeFacade` (Analysis.Dump) — cached ClrMD access with `MethodTable → ClrType` cache
- `IHeapAnalysisCache` (Core.Abstractions) — read-only queries for analyzers (type stats,
  indexed entries, roots, point lookup)
- `IHeapIndexBuilder` (Analysis.Cache) — build-time contract: `PrebuildHeapIndex`, `TryGetHeapIndex`
- `IAnalyzer` (Core.Abstractions) — `Name`, `Category`, `Tags`, `Order`, `IsThreadSafe`,
  `AnalyzeAsync(context, ct)`, and `IDisposable` (default no-op; analyzers holding buffers/streams
  override it for deterministic cleanup)
- `IFindingGenerator` (Core.Abstractions) — convert an `AnalyzerDomainResult` to `InsightFinding`s
- `IAnalyzerSectionBuilder` / `IReportSectionBuilder` (Reporting.Abstractions) — convert domain
  results into structured `AnalyzerDetailSection` report data (per-analyzer and cross-cutting,
  respectively); replaced the older printer-based text formatting entirely — there is no `IPrinter`
  in the codebase

---

## 3. Two-phase execution

- **Phase 1 — Index build**: single-pass heap scan (parallelized across segments); writes a
  compact on-disk container (`cache.bin`) or, for small dumps, mirrors the same data in memory
- **Phase 2 — On-demand analysis**: each `IAnalyzer` runs against `IHeapAnalysisCache` and
  `RuntimeFacade`; expensive graph operations (root paths, retained-size walks) are lazy and
  bounded

Index storage modes:
- `HeapIndexPrebuildMode` — `Auto`, `Memory`, or `Disk` (config/CLI `--index-mode`). In `Auto`,
  `HeapAnalysisCache` selects `Disk` for large dumps and `Memory` for small dumps based on a
  size-tier threshold (currently tuned around several GB; configurable).
- `HeapIndexStorageKind` — the actual storage used at runtime (`Memory` or `Disk`); many analyzers
  branch on this to prefer fast in-memory candidate lists when available.

For the on-disk container's byte-level layout, see [docs/binary-format.md](binary-format.md).
For the cache subsystem's internal architecture (facade, sub-caches, writer/reader orchestration,
governing design constraints), see [docs/cache/cache-architecture.md](cache/cache-architecture.md)
— not duplicated here.

---

## 4. Performance and safety rules (enforced)

- Stream heap enumeration; never `.ToList()` on `heap.EnumerateObjects()`
- Avoid LINQ allocations in hot paths; prefer explicit loops and `yield return`
- Use `ArrayPool<T>` and small `readonly struct`s on hot paths
- Cache `ClrType` metadata; never cache `ClrObject`/`ClrType` instances themselves — extract
  immutable data (addresses, `MethodTable`s, `TypeMetadata` records) instead
- Prefer the disk/memory index over a live ClrMD call whenever the index already has the answer

---

## 5. Heap layer

### HeapEntry
Minimal per-object representation:

```csharp
internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;       // 64-bit — object size can exceed int.MaxValue on large heaps
    public readonly sbyte Generation; // GC generation (0/1/2, higher for LOH/POH/Frozen), or -1 if unresolved
}
```

### DiskBackedObjectIndexWriter (Phase 1 scan)
`src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs` is the single-pass heap
scanner:

- **Segment scan**: `Parallel.For` over `ClrHeap.Segments`, degree of parallelism tiered by dump
  size (`Min(ProcessorCount, 8)` Large / `4` Medium / `2` otherwise). Each segment writes its own
  columnar scratch file; scratch files are concatenated into the container after the full scan
  completes.
- **Per-object work**: per-type shape/flags (`ComputeTypeFlags`, `IsDelegateType`,
  `IsAsyncStateMachineType`, field-shape/string-field detection) computed once per unique
  `MethodTable`, not per object.
- **String dedup**: `masterStringDedup` capped at 500k unique entries (XxHash64-keyed).
- **Satellite candidates** collected in the same pass: task/event/LOH-free-block/large-object
  candidates, avoiding extra heap passes.
- A `MemoryBackedObjectIndexWriter` mirrors the same output in memory for small dumps
  (`HeapIndexPrebuildMode.Memory`).

For the memory-backed vs disk-backed dispatch and sub-cache design, see
[docs/cache/cache-architecture.md](cache/cache-architecture.md).

---

## 6. Storage layer

### cache.bin container
As of 2026-07-15, all Phase 1 disk-backed index data is written into a single `cache.bin`
container per dump (`<dump>.dumpindex/cache.bin`), replacing a prior nine-file layout. See
[docs/binary-format.md](binary-format.md) for the full byte-level format.

### ObjectIndexReader
Reads the columnar `ObjectAddresses`/`ObjectMethodTables`/`ObjectSizes`/`ObjectGenerations`
sections back into `HeapEntry` batches using pooled buffers, sized to the index's total record
count.

### Cache directory resolution (`--cache-dir`)
`DumpIndexPaths.ResolveCacheDirectory` picks where `.dumpindex/` is written, trying tiers in
order and stopping at the first writable location:

1. **`--cache-dir <dir>`** (or `CacheDirectory` config) — explicit override; fails immediately if
   not writable rather than silently falling through.
2. **Colocated** — `<dumpPath>.dumpindex/`, the default.
3. **Temp folder (best effort)** — `%TEMP%/dumpdetective-cache/<hash>/`, used only when the dump
   folder isn't writable; a warning is printed since this location can be evicted at any time.
4. **Failure** — if neither tier is writable, an error asks the user to specify `--cache-dir`.

Resolved once per dump (keyed by full path) and reused by all `DumpIndexPaths` call sites for
that run.

---

## 7. Graph layer

### BoundedGraphWalk
`src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs` is the single canonical forward-BFS
primitive, enforcing a hard 20-depth cap (`AbsoluteMaxDepth`) internally, not left to caller
discipline. It replaced three previously-separate implementations (`HeapTypePathTraversal`,
`BoundedRetainedSizeBfs`, `HeapAnalysisCache.GetRetainedObjects`). `GCRootAnalyzer`,
`RetentionAnalyzer` (in `MemoryAnalyzer.cs`), `DominatorAnalyzer`, and `StaticRootLeakDetector`
all call into it. Callers own their `visited` set's lifetime — e.g. retention analysis shares one
set across a batch (exclusive-retained, no double counting) while `DominatorAnalyzer` allocates a
fresh set per candidate.

### Reverse (parent-lookup) index
`src/DumpDetective.Analysis/Indexing/ReverseIndex/` — a real, shipped, disk-backed index of
incoming references, hash-partitioned and sorted per-bucket. Scoped narrowly to parent lookup, not
a general forward+reverse object graph; never fully materialized in memory. Optional/skippable via
`DD_SKIP_REVERSE_INDEX_BUILD=1`. See
[docs/cache/cache-architecture.md § 5](cache/cache-architecture.md) for the write/read path.

### RootSetCache
`src/DumpDetective.Analysis/Cache/` — the single canonical root-set service: builds `RootRecord`
(`TargetAddr`, `RootAddr`, `Kind`) once per run from the Phase 1 disk index, falling back to a live
`heap.EnumerateRoots()` walk when no index is present. `GCRootAnalyzer`, `StaticRootLeakDetector`,
and `EventLeakAnalyzer` all read roots through it instead of independently re-enumerating
stack/static/handle roots.

### RootPathFinder / ReferenceChainAnalyzer
Distinct from `BoundedGraphWalk`: solves shortest-path-to-any-root via a bidirectional
candidate-set search (forward-expand from root-frontier and target-frontier, then BFS with
reverse-index backpointers); used only by `ReferenceChainAnalyzer`. Different problem shape from
the root/retention graph service above — not migrated onto `BoundedGraphWalk`.

---

## 8. Cache layer

### IHeapAnalysisCache (Core.Abstractions)
Read-only contract used by all analyzers: `GetOrBuildTypeStatistics`,
`EnumerateIndexedEntriesAsTuples`, `GetStaticRootedAddresses`, `GetOrBuildValidRoots`,
`TryGetObjectMetadata` (address → `(MethodTable, Size)` point lookup).

### HeapAnalysisCache (Analysis.Cache)
Implements both `IHeapAnalysisCache` and `IHeapIndexBuilder`. A thin facade delegating to seven
single-responsibility sub-caches (`HeapIndexCache`, `StatisticsCache`, `RootSetCache`,
`ReverseIndexCache`, `ThreadCache`, `MethodTableCache`, `TypeMetadataCache`), each closed over the
shared built index. Full sub-cache responsibilities and governing constraints are documented in
[docs/cache/cache-architecture.md](cache/cache-architecture.md) — not duplicated here.

---

## 9. Analysis layer — analyzer catalog

Analyzers are registered as **feature modules** in `DefaultAnalyzerFeatureModuleCatalog`
(`DumpDetective.Reporting.Capabilities`), one entry per analyzer bundling its `IAnalyzer`,
`IFindingGenerator`, `IAnalyzerTrendComparer`, and `IAnalyzerSectionBuilder`, plus an execution
`Order` and a set of `Tags` used for CLI filtering (`--tags`, `--only`, `--skip`). Current catalog,
in execution order:

| Order | Key | Analyzer | Tags |
|---|---|---|---|
| 100 | `memory` | `MemoryAnalyzer` (+ retention logic) | memory |
| 110 | `gc-generation` | `GCGenerationAnalyzer` | gc |
| 120 | `allocation-pattern` | `AllocationPatternAnalyzer` | gc, allocation |
| 130 | `object-shape` | `ObjectShapeAnalyzer` | types |
| 140 | `gc-root` | `GCRootAnalyzer` | roots |
| 150 | `heap-topology` | `HeapTopologyAnalyzer` | heap |
| 160 | `module` | `ModuleAnalyzer` | runtime |
| 170 | `crash` | `CrashAnalyzer` | exceptions |
| 180 | `hang` | `HangAnalyzer` | threads |
| 190 | `async-task` | `AsyncTaskAnalyzer` | async |
| 210 | `leak-candidate` | `LeakCandidateAnalyzer` | leaks |
| 220 | `dominator` | `DominatorAnalyzer` | retention, dominator |
| 230 | `string` | `StringAnalyzer` | memory, string |
| 240 | `collection` | `CollectionAnalyzer` | collections |
| 250 | `static-root` | `StaticRootLeakDetector` | roots, leaks |
| 260 | `reference-chain` | `ReferenceChainAnalyzer` | roots |
| 270 | `gc-handle` | `GCHandleAnalyzer` | handles |
| 290 | `loh-fragmentation` | `LohFragmentationAnalyzer` | gc, loh |
| 300 | `thread-stack-cluster` | `ThreadStackClusterAnalyzer` | threads |
| 310 | `thread` | `ThreadAnalyzer` | threads |
| 320 | `lock-graph` | `LockGraphAnalyzer` | threads, locks |
| 330 | `event-leak` | `EventLeakAnalyzer` | events, leaks |
| 340 | `finalizable-object` | `FinalizableObjectAnalyzer` | gc |
| 350 | `async-state-machine` | `AsyncStateMachineAnalyzer` | async |
| 360 | `array` | `ArrayAnalyzer` | types |
| 380 | `segment-reservation` | `SegmentReservationAnalyzer` | gc, segments |
| 390 | `weak-reference` | `WeakReferenceAnalyzer` | gc |
| 400 | `boxing` | `BoxingAnalyzer` | types, perf |
| 410 | `jit` | `JitAnalyzer` | runtime, perf |
| 420 | `db-connection` | `DbConnectionAnalyzer` | infra, network |
| 430 | `wcf-channel` | `WcfChannelAnalyzer` | infra, network |
| 440 | `http-object` | `HttpObjectAnalyzer` | infra, network |
| 450 | `timer-leak` | `TimerLeakAnalyzer` | infra, timers |

Plus cross-cutting, non-catalog stages: `InsightEngine` (cross-analyzer synthesis) and
`TrendAnalyzer` (multi-snapshot comparison, § 12).

Global (non-analyzer-scoped) report sections, also registered in the catalog:
`ExecutiveSummarySectionBuilder`, `TypeSystemSectionBuilder`, `InsightsSectionBuilder`,
`ConfidenceSectionBuilder`.

Brief responsibility notes for less-obvious analyzers:
- **DominatorAnalyzer** — dominator-tree-style exclusive retained-size analysis over
  `BoundedGraphWalk`
- **LeakCandidateAnalyzer** — cross-type leak candidate scoring (distinct from
  `StaticRootLeakDetector`'s static-root-specific view)
- **HeapTopologyAnalyzer** — heap-wide shape/segment topology summary
- **DbConnectionAnalyzer / WcfChannelAnalyzer / HttpObjectAnalyzer / TimerLeakAnalyzer** —
  infra-resource-leak analyzers (connections, WCF channels, `HttpClient`/handler objects, timers)
  targeting typed-resource retention patterns, sharing `TypedResourceSampler` /
  `TypedResourceScanDriver` / `ITypedResourceCandidateSource` scan infrastructure
- **EventLeakAnalyzer** — has a dedicated fast-scan path, `EventLeakFastScanner`, alongside the
  full analyzer

---

## 10. Query layer

### QueryEngine
Structured querying over indices (not raw heap): top types by memory, objects of a specific type,
reference paths.

---

## 11. Reporting layer (`DumpDetective.Reporting`)

- **`IFindingGenerator`** (Core.Abstractions) — one per analyzer; converts a strongly-typed
  `AnalyzerDomainResult` into zero or more `InsightFinding` records
- **`IAnalyzerSectionBuilder`** — one per analyzer; converts a domain result into a structured
  `AnalyzerDetailSection` (pure data, no text formatting)
- **`IReportSectionBuilder`** — cross-cutting sections that pull from multiple analyzer results
  (`AnalyzerResultSet`) — e.g. executive summary, type system, insights, confidence
- **`FindingGenerationPipeline`** — runs all registered finding generators after analysis
  completes, attaching `InsightFinding` lists to each `AnalyzerRunResult`
- **`ReportBuilderFacade`** — assembles per-analyzer and global sections into a full
  `AnalysisReportDocument`
- **Canonical formatters** (`Formatters/`) — `TextCanonicalReportFormatter`,
  `MarkdownCanonicalReportFormatter`, `JsonCanonicalReportFormatter` render the same
  `AnalysisReportDocument` to each output format
- **Trend report composer** — compares snapshot results across multiple dumps, producing
  lifecycle/regression summaries (§ 12)

Finding generator failures are captured on `AnalyzerRunResult.FindingGeneratorError` and surfaced
as warnings rather than failing the run.

---

## 12. Trend analysis

- **`TrendAnalyzer`** (`DumpDetective.Analysis.Trend`) — compares `AnalyzerRunResult` collections
  across multiple dump snapshots, delegating domain-specific comparison to one
  `IAnalyzerTrendComparer` per analyzer (registered in the same feature-module catalog as its
  analyzer)
- **`TrendOrchestrationService`** (`DumpDetective.Cli.Services`) — loads each dump sequentially,
  runs analysis, then invokes `TrendAnalyzer`
- **`TrendReportComposer`** (`DumpDetective.Reporting.Services`) — assembles per-finding lifecycle
  (new / worsened / stable / resolved) across snapshots into a ranked trend report

---

## 13. CLI staged pipeline (`DumpDetective.Cli.Pipeline`)

The CLI executes analysis as a linear sequence of `IAnalysisStage` steps sharing a single
`SingleDumpPipelineState`, run by `StagedPipelineRunner`:

| Stage | Responsibility |
|---|---|
| `LoadDumpStage` | Loads dump via `IDumpLoader`; stores `DumpLoadContext` |
| `BuildHeapIndexStage` | Calls `IHeapIndexBuilder.PrebuildHeapIndex`; stores index |
| `RunAnalyzersPipelineStage` | Runs all active analyzers (per `AnalyzerFilterService` selection); stores `AnalyzerRunResult[]` |
| `BuildReportStage` | Runs `FindingGenerationPipeline` and renders the full report via `ReportBuilderFacade` |
| `WriteOutputStage` | Writes report to console and/or file |

`ExecutePerDumpPipelineStage` composes the above for each dump in a multi-dump/trend run.
`InsightEngine` runs cross-cutting analysis after the per-analyzer pipeline completes.

---

## 14. Concurrency model

- **Phase 1**: heap scan is parallelized across segments (see § 5); index write-out is
  single-threaded/sequential
- **Phase 2**: analyzers may run in parallel when `IsThreadSafe` is opted in; graph traversal
  parallelism is bounded per-call, not heap-wide

---

## 15. Failure handling

- Invalid objects (`obj.IsValid == false`, `obj.Type == null`) are skipped, not thrown
- Missing/optional satellite sections degrade gracefully — analyzers fall back to a live ClrMD
  scan (see [docs/cache/cache-architecture.md § 7](cache/cache-architecture.md))
- Partial results are allowed; finding-generator failures are captured per-analyzer, not fatal

---

## 16. Observability

### Execution & performance tracking
`PhaseTimeline` tracks per-stage timing through the staged pipeline.

### Analyzer logging
Analyzers may take an optional `ILogger<T>? logger = null` constructor parameter for per-object
error/debug diagnostics, resolved automatically via `ActivatorUtilities` in
`DefaultAnalyzerFactory`. The CLI host wires up generic-host logging; the Reporting layer provides
a `NullLoggerFactory` fallback for direct construction (tests, benchmarks).

Use case: analyzers that scan large object populations and expect malformed/unexpected heap data —
e.g. `CollectionAnalyzer` logging per-Dictionary/Queue/List/HashSet errors and generation-lookup
failures while walking millions of collection objects. Not intended for routine control-flow
logging.

Diagnostics levels:
- **Errors** (`LogError`): genuine per-object scan failures (exceptions during field read,
  invalid object state)
- **Debug** (`LogDebug`): expected/ignorable per-object issues (missing optional fields, invalid
  backing arrays, resolution fallbacks)
- **Information** (`LogInformation`): user-initiated cancellation, analysis milestones

---

## 17. Extensibility

```csharp
public interface IAnalyzer : IDisposable
{
    string Name { get; }
    string Category => AnalyzerCategory.Infer(Name);   // default: inferred from Name
    IReadOnlyCollection<string> Tags => [];            // default: []
    int Order => 0;                                    // default: 0 — controls execution order
    bool IsThreadSafe => false;                         // default: false — opt-in for parallel execution
    ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);
    void IDisposable.Dispose() { }                      // default: no-op
}
```

Each analyzer returns a strongly-typed `AnalyzerDomainResult` subtype (e.g. `MemoryDomainResult`,
`ThreadDomainResult`) and stamps `AnalyzerName`/`Category` via `result.Stamp(this)` at the end of
`AnalyzeAsync`.

Adding an analyzer:
1. Add a `XxxDomainResult` in `DumpDetective.Analysis.Models`
2. Implement `XxxAnalyzer.cs` using streaming heap loops
3. Implement its `IFindingGenerator`, `IAnalyzerTrendComparer`, `IAnalyzerSectionBuilder`
4. Register one `Module(...)` entry in `DefaultAnalyzerFeatureModuleCatalog` (key, display name,
   the four types above, execution order, tags)
5. Add tests and docs

No core engine changes are needed to add an analyzer — the staged pipeline, report assembly, and
trend analysis all iterate the catalog generically.

---

## 18. ExecutionPolicy (example)

The runtime reads an `ExecutionPolicy` / `Indexing` block from config (see `config.sample.json`)
to centralize resource bounds and tuning:

```json
"ExecutionPolicy": {
  "MaxLeakScanObjects": 2000000,
  "MaxReferenceAddresses": 1000000,
  "ReferenceChainMaxPathDepth": 25,
  "ReferenceChainFastModeMaxDepth": 25,
  "ReferenceChainMaxPathSearchObjects": 5000,
  "IndexPrebuildMode": "auto"
}
```

Place defaults in `config.sample.json` and document any CLI overrides in the CLI help text.

---

## 19. Where to look next

- [docs/binary-format.md](binary-format.md) — disk-based indexing / binary container format
- [docs/cache/cache-architecture.md](cache/cache-architecture.md) — cache subsystem internals
  (facade, sub-caches, writer/reader orchestration, governing constraints)
- [docs/cache/backlog.md](cache/backlog.md) — known gaps and unbuilt perf wins in the cache
  subsystem
- [docs/performance-checklist.md](performance-checklist.md) — perf requirements checklist
- [docs/schema-versioning.md](schema-versioning.md) — report/snapshot schema versioning policy

---

## 20. Summary

This architecture prioritizes scalability over convenience, streaming over materialization, and
insight over raw data — designed to operate efficiently on massive dumps while providing deep,
actionable diagnostics across memory, threading, async, GC, and infra-resource domains.
