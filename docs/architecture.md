# 🧠 High-Performance .NET Dump Analyzer
## Architecture Document

---

# 1. 📌 Overview

This system is a high-performance memory dump analyzer built using Microsoft.Diagnostics.Runtime (ClrMD 3.1.5).

It is designed to:
- Analyze extremely large memory dumps (1GB–25GB+)
- Operate under constrained memory environments
- Provide actionable diagnostics (leaks, retention, thread issues)

---

## DumpDetective — Architecture (concise)

Purpose
- Provide a concise, operational architecture for a high-performance dump analyzer.

Design goals (summary)
- Scale to very large dumps (1GB–25GB+)
- Keep runtime memory bounded via disk-backed indices and streaming
- Keep analyzer contracts small and stable to minimize ripples across projects
- Make reporting depend on Analysis via stable models (avoid polluting Core)

High-level layers
- CLI: entrypoint, pipeline orchestration, DI registration
- Analysis: dump loading, heap indexing, analyzers, traversal, cache
- Reporting: finding generators, printers, report composition
- Core: small, stable contracts and base domain primitives

Dependency graph
Cli -> Analysis, Reporting, Core
Analysis -> Core
Reporting -> Analysis, Core
Core -> ClrMD (runtime dependency)

Two-phase execution
- Phase 1 — Index build: single-pass heap scan; write compact on-disk index (Address|MethodTable|Size)
- Phase 2 — On-demand analysis: run analyzers against `IHeapAnalysisCache` and `RuntimeFacade`; expensive operations are lazy and bounded

- Key contracts
- `IDumpLoader` — load dump, resolve DAC, produce `DumpLoadContext`
- `RuntimeFacade` — safe, cached ClrMD access with `MethodTable->ClrType` cache
- `IHeapAnalysisCache` (Core) — read-only queries for analyzers (type stats, indexed entries, roots)
- `IHeapIndexBuilder` (Analysis) — build-time contract: `PrebuildHeapIndex`, progress callbacks
- `IAnalyzer` (Core) — `Name`, `Category`, `Order`, `IsThreadSafe`, `AnalyzeAsync(context)`
- `IFindingGenerator` (Reporting) — convert domain results to `InsightFinding`

- Performance and safety rules (enforced)
- Stream enumerations; avoid `.ToList()` on heap
- Avoid LINQ allocations in hot paths; prefer loops and `yield return`
- Use `ArrayPool` and small readonly structs for hot-paths
- Cache `ClrType` metadata; prefer `GetTypeByMethodTable` before `GetObject`

Graph and traversal
- Forward refs computed lazily from object fields
- Reverse indexes built selectively and disk-backed when needed
- Root-path BFS: depth limits, visited set, and time budget
- `RootSetCache` (`DumpDetective.Analysis.Cache`) is the single canonical root-set service: builds
  `RootRecord` (`TargetAddr`, `RootAddr`, `Kind`) once per run from the Phase-1 disk index, falling
  back to a live `heap.EnumerateRoots()` walk when no index is present. `GCRootAnalyzer`,
  `StaticRootLeakDetector`, and `EventLeakAnalyzer` all read roots through it instead of
  independently re-enumerating stack/static/handle roots.
- `BoundedGraphWalk` (`DumpDetective.Analysis.Traversal`) is the single canonical forward-BFS
  primitive, enforcing a hard 20-depth cap inside the walk itself (not left to caller discipline).
  It replaces the formerly-separate `HeapTypePathTraversal`, `BoundedRetainedSizeBfs`, and
  `HeapAnalysisCache.GetRetainedObjects` implementations; `GCRootAnalyzer`, `RetentionAnalyzer`,
  `DominatorAnalyzer`, and `StaticRootLeakDetector` all call into it. Callers still own their
  `visited` set's lifetime — `RetentionAnalyzer` shares one set across a batch (exclusive-retained,
  no double counting), while `DominatorAnalyzer` intentionally allocates a fresh set per candidate
  (unchanged, deferred design decision — see `RootPathFinder`, below, for the unrelated
  bidirectional shortest-path search `ReferenceChainAnalyzer` uses instead).

Reporting and fault handling
- Finding generator failures are captured on `AnalyzerRunResult.FindingGeneratorError` and surfaced as warnings in console and report

Extensibility and testing
- Add analyzers and their models to `DumpDetective.Analysis.Models`; register analyzer, comparer, and generator in DI
- Keep orchestration small and testable (`AnalyzerFilterService`, `SingleDumpOrchestrationService`)

Operational recommendations
- Make resource bounds and index-mode selection configurable via `ResolvedExecutionOptions`
- Prefer `internal` test-only APIs; avoid exposing production surface as public

ExecutionPolicy (example)
The runtime reads an `ExecutionPolicy` / `Indexing` block from config (see `config.sample.json`) to centralize resource bounds and tuning. Current `config.sample.json` exposes keys the CLI reads; example settings:

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

Where to look next
- See `docs/binary-format.md` and `docs/performance-checklist.md` for low-level constraints and tuning guidance.

## 5.1 Heap Layer

### HeapStreamer
- Streams heap objects using `yield return`
- Produces minimal `HeapEntry` structs

Implementation notes:
- Phase 1 scanning is parallelized across heap segments (controlled by dump size tier) with per-segment buffers rented from `ArrayPool<T>` to avoid large allocations.
- Each segment builds a small per-thread `TypeIndexBuilder` which is merged into a global `TypeIndexBuilder` after the parallel loop.
- Serialization to `ObjectIndex.bin` happens in fixed-size chunks; segment threads serialize into pooled byte buffers and flush under a short write lock to the shared stream.
- During the scan the indexer also collects satellite candidates (task addresses, event/delegate candidates, LOH free-block candidates, large objects) and a `StringDedup` table via XxHash64 sampling to avoid additional heap passes.

### HeapEntry
Minimal representation of an object:

```csharp
internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;   // 8 bytes — object size in bytes (uses 64-bit to preserve full size fidelity)

    public HeapEntry(ulong address, ulong methodTable, ulong size) { ... }
}
```

### TypeIndexBuilder
- Aggregates per-type statistics
- Runs during streaming phase
- Stores:
  - Count
  - Total size

### SegmentAnalyzer
- Classifies heap segments:
  - SOH
  - LOH
  - POH
- Tracks distribution and fragmentation indicators

---

## 5.2 Storage Layer

### ObjectIndexWriter
- Writes object metadata to disk
- Uses sequential binary format

### ObjectIndexReader
- Reads object metadata efficiently
- Supports sequential `FileStream + ArrayPool<byte>` reads with bounded batches

### Storage Format

As of format version 2, object records are stored **columnar** (struct-of-arrays), not interleaved: three parallel `ulong[]` sections — `ObjectAddresses`, `ObjectMethodTables`, `ObjectSizes` — one entry per heap object, aligned by index. `DiskBackedObjectIndexWriter` writes per-segment scratch-file columns that are concatenated into the three container sections; `ObjectIndexReader` zips them back into `HeapEntry` records in batches sized to the index's total byte count, using pooled buffers. The container TOC's `RecordCount` field replaces the old per-section header.

Per-column record size: 8 bytes (`ulong`), no padding.

Characteristics:
- Append-only
- Sequential writes
- Cache-friendly

### Phase 1 — Single container format (`cache.bin`)

As of **2026-07-15**, the system writes all index data into a single **`cache.bin`** container file into a per-dump `<dump>.dumpindex/` folder to accelerate Phase 2 analyzers and to avoid re-scanning the heap on subsequent runs. (Prior to this, data was written as nine separate files; the container consolidates them while preserving all binary payload formats unchanged.)

The container holds these sections (see `DumpDetective.Analysis.Indexing.Container.CacheSectionId`):
- **ObjectAddresses / ObjectMethodTables / ObjectSizes** — columnar (struct-of-arrays) object index: three parallel `ulong[]` sections, aligned by index, replacing the legacy interleaved `Objects` section as of format version 2.
- **TypeAggregates** — compact TypeAggregate table, module registry, global size buckets, and type-shape cache; presence enables a fast-path that skips full heap rescan.
- **StringDedup** + **StringDedupMeta** — XxHash64 -> preview/count/total-size table for string deduplication and sampling (meta section holds UTF-8 JSON distribution summary).
- **Handles** — GC handle snapshot (Addr, MethodTable, Kind) consumed by handle/weakref analyzers.
- **Roots** — pre-enumerated GC roots (TargetAddr, RootAddr, Kind) consumed by `GCRootAnalyzer`, `FinalizableObjectAnalyzer`, and `StaticRootLeakDetector`.
- **Tasks** — Task / ValueTask candidate addresses used by `AsyncTaskAnalyzer`.
- **EventCandidates** — delegate/event candidates for `EventLeakAnalyzer`.
- **LohFreeBlocks** — LOH/POH free-block candidates used by `LohFragmentationAnalyzer`.
- **LargeObjects** — top-large-object list (LOH) used by LOH and array analyzers.

Consumers and notes:
- Index data is produced in disk-backed mode and mirrored by in-memory structures when the prebuild mode selects `Memory`.
- Many analyzers prefer satellite sections when present in the container and will fall back to an in-memory scan otherwise (see `HeapIndexBuildResult` fields: `InMemoryEntries`, `InMemoryTaskCandidates`, `InMemoryEventCandidates`, `InMemoryRootCandidates`, etc.).
- Container validity is verified once via `Magic` + `FormatVersion`; subsequent section access is instant. Presence of a valid container indicates a successful completed Phase 1 and is used as a cache hit to skip re-scans.
- Satellite writes are non-fatal: partial section failures are surfaced as `SatelliteWarnings` on the `HeapIndexBuildResult` so downstream stages can either degrade gracefully or warn the user.
- Container writes are atomic: a crash during write leaves no partial `.dumpindex/cache.bin` behind (only the `.tmp` file, which cleanup removes); the next run sees a cache miss and rebuilds cleanly.

For binary format details, see [docs/binary-format.md § Container Format](../binary-format.md#-container-format-cachebin).

### Cache directory resolution (`--cache-dir`)

Before the first index access, `DumpIndexPaths.ResolveCacheDirectory` picks where the `.dumpindex/` folder
above is written, trying each tier in order and stopping at the first writable location:

1. **`--cache-dir <dir>`** (or the `CacheDirectory` config-file setting) — if set, the index is written to
   `<dir>/<dumpFileName>.dumpindex/`. If this location is not writable, resolution fails immediately with an
   error rather than silently falling through, since an explicit user override that can't be honored should
   be surfaced, not masked.
2. **Colocated** — `<dumpPath>.dumpindex/`, next to the dump file. This is the default when no `--cache-dir`
   is given.
3. **Temp folder (best effort)** — `%TEMP%/dumpdetective-cache/<hash>/`, where `<hash>` is a truncated SHA256
   of the dump's full path, used to isolate the cache per-dump when several dumps share the fallback temp
   root. Used only when the dump folder itself is not writable (e.g. read-only or network storage). A
   warning is printed when this tier is used, since the temp folder can be evicted at any time and the
   cached index is not guaranteed to persist across runs.
4. **Failure** — if neither the dump folder nor the temp folder are writable, an error is thrown asking the
   user to specify a writable `--cache-dir`.

The chosen directory is resolved once per dump (keyed by the dump's full path) and reused by all other
`DumpIndexPaths` call sites for that run.

---

## 5.3 Graph Layer

### ReferenceGraph
- Provides lazy forward traversal
- Extracts references via ClrMD field inspection

### ReverseReferenceIndex (Optional)
- Built selectively for subsets
- Disk-backed when large
- Never fully materialized

### RootPathFinder
- Finds paths from GC roots to target objects
- Uses bounded BFS:
  - Depth-limited
  - Early termination
- Distinct from `BoundedGraphWalk`: solves shortest-path-to-any-root via a bidirectional
  candidate-set search (forward-expand from root-frontier and target-frontier, then BFS with
  reverse-index backpointers); used only by `ReferenceChainAnalyzer`. Not migrated onto
  `BoundedGraphWalk` — different problem shape, out of scope for the root/retention graph
  service below.

---

## 5.4 Analysis Layer

### RetentionAnalyzer (+ StaticRootLeakDetector)
Together implement the `LeakDetector` role described in guidelines:
- `RetentionAnalyzer` (in `MemoryLeakAnalyzer.cs`) — retained-reference / highly-referenced object
  analysis; computes exclusive retained bytes via `BoundedGraphWalk.ComputeExclusiveRetained` with
  one `visited` set shared across its batch
- `StaticRootLeakDetector` — identifies large object graphs retained by static roots; reads roots
  from `RootSetCache` (byte-kind filter on `RootRecord.IsStatic`, not string matching) and walks
  retained objects via `BoundedGraphWalk.CollectRetainedObjects` (depth-capped at 20)
- Uses heuristic scoring: retained size, root type, object lifetime

### ThreadAnalyzer
- Enumerates threads
- Groups stack traces, detects blocking/wait patterns
- Reports finalizer thread state

### AllocationPatternAnalyzer
- Classifies allocation pressure and churn using generation mix and size buckets

### ObjectShapeAnalyzer
- Profiles field shapes and reference density from Phase 1 type shape data

### GCRootAnalyzer
- Builds bounded GC root paths and retention summaries from indexed roots
- Reads roots via `RootSetCache.GetOrBuildRoots(heap)` (disk index, falling back to a live
  `heap.EnumerateRoots()` walk when no index is present) and traces top-N root paths via
  `BoundedGraphWalk.CollectForwardTypeNames`

### GCHandleAnalyzer
- Analyzes GC handles: Strong, Weak, Pinned, Dependent

### DependentHandleAnalyzer
- Analyzes ConditionalWeakTable / dependent handle edges

### CollectionAnalyzer
- Detects wasteful List/Dictionary/Queue/Stack allocations
- Computes fill-rate and wasted capacity per collection

### EventLeakAnalyzer
- Detects unsubscribed event handler leaks

### CrashAnalyzer
- Reports active exceptions and crash-thread candidates

### LockGraphAnalyzer
- Identifies potential deadlocks via lock-graph analysis

### HangAnalyzer
- Detects blocked threads, waiting tasks, and async-over-sync patterns

### ModuleAnalyzer
- Reports loaded modules, version conflicts, and per-module heap footprint

### LohFragmentationAnalyzer
- Reports LOH free-block distribution and fragmentation percentage

### GCGenerationAnalyzer
- Reports object distribution across Gen0 / Gen1 / Gen2 / LOH

### ReferenceChainAnalyzer
- Samples reference chains from heap index to GC roots

### SegmentAnalyzer
- Classifies all heap segments (SOH / LOH / POH / Frozen)
- Tracks committed bytes and object count per segment kind

### ThreadStackClusterAnalyzer
- Groups threads by stack signature to identify contention hotspots

### AsyncTaskAnalyzer
- Summarizes task states, orphaned continuations, and async chain depth

### AsyncStateMachineAnalyzer
- Profiles compiler-generated async state machines and captured closures

### BoxingAnalyzer
- Detects boxed value types and oversized value-type pressure

### FinalizableObjectAnalyzer
- Profiles finalizable objects, finalizer queue backlog, and thread health

### ArrayAnalyzer
- Reports array sizes, element distributions, and large-array hotspots

### WeakReferenceAnalyzer
- Analyzes weak-reference population and stale wrappers

### AppDomainAnalyzer
- Summarizes appdomains, module distribution, and cross-domain loading

### JitAnalyzer
- Reports JIT heap usage and active method/native code hotspots

### SegmentReservationAnalyzer
- Tracks committed vs reserved memory and address-space pressure

---

## 5.5 Query Layer

### QueryEngine
- Provides structured querying capabilities
- Operates on indices (not raw heap)

Examples:
- Top types by memory
- Objects of a specific type
- Reference paths

---

## 5.6 Insight Layer

### InsightEngine
Generates high-level diagnostics:
- Leak suspicion
- GC pressure issues
- Thread contention

---

## 5.7 Output Layer

Supports:
- CLI output
- JSON export
- Structured reports

---

# 6. ⚙️ Execution Model

## Phase 1: Index Build
- Single pass over heap
- Streaming processing
- Disk-backed persistence
- No large allocations

Index storage modes:
- `HeapIndexPrebuildMode` can be `Auto`, `Memory`, or `Disk` (config/CLI). In `Auto` mode the `HeapAnalysisCache` selects `Disk` for large dumps and `Memory` for small dumps based on dump size tiers.
- `HeapIndexStorageKind` indicates the actual storage used: `Memory` (in-memory arrays and candidate snapshots) or `Disk` (writes `.dumpindex/` satellite files). Many analyzers switch behavior based on `StorageKind` to prefer fast in-memory access when available.

Sizing notes:
- The in-repo threshold for choosing disk-backed mode is currently tuned around several GB (see `HeapAnalysisCache`), but the exact value is configurable via CLI/config and may be adjusted by profiling.

## Phase 2: Analysis
- Triggered by queries
- Lazy computation
- Scoped to subsets

---

# 7. 🧠 Memory Model

## Key Principles
- Address-based identity (`ulong`)
- No object retention unless required
- Disk as primary storage for large data

## Memory Usage Strategy

| Component        | Memory Usage |
|------------------|--------------|
| Heap Streaming   | Minimal      |
| Type Index       | Small        |
| Object Index     | Disk-backed  |
| Graph Traversal  | On-demand    |

---

# 8. ⚡ Performance Considerations

## Optimizations
- Streaming enumeration
- Struct-based data models
- String deduplication (TypeId mapping)
- Sequential disk IO
- `ArrayPool` usage

## Avoided Patterns
- Full heap materialization
- Recursive graph traversal without bounds
- Large in-memory dictionaries of objects

---

# 9. 🔗 Graph Traversal Strategy

## Forward Traversal
- Computed lazily per object

## Reverse Traversal
- Built selectively
- Scoped to:
  - Specific types
  - Suspicious objects

## Root Path Finding
BFS with:
- Depth limit (default: 20)
- Visited tracking
- Stops on first match

---

# 10. 🧪 Leak Detection Strategy

## Step 1: Candidate Selection
- Top N types by memory usage

## Step 2: Deep Analysis
- Reference paths
- GC roots
- Retention patterns

## Step 3: Scoring

Example factors:
- Object size
- Root type (static > stack)
- Retention depth

---

# 11. 🏗️ Dump Layer

## DumpLoader (`DumpDetective.Analysis.Dump`)
- Loads a memory dump via `DataTarget.LoadDump(path)`
- Validates CLR version presence and heap walkability
- Returns `DumpLoadContext` (owns and disposes `DataTarget` + `ClrRuntime`)
- Registered via `IDumpLoader` interface — decoupled from CLI

## RuntimeFacade (`DumpDetective.Analysis.Dump`)
- Wraps `ClrRuntime` + `ClrHeap` with a `ConcurrentDictionary`-backed
  `MethodTable → ClrType` cache (`IMethodTableCache`)
- Prevents redundant `GetTypeByMethodTable` calls across analyzers
- Exposed as `RuntimeFacade?` on `RuntimeAnalysisContext`

---

# 12. 🗄️ Cache Layer

## IHeapAnalysisCache (`DumpDetective.Core.Abstractions`)
Read-only contract used by all analyzers:
- `GetOrBuildTypeStatistics(heap)` — type name → `CachedTypeStatistics`
- `EnumerateIndexedEntriesAsTuples()` — stream `(Address, MT, Size)` tuples
- `GetStaticRootedAddresses(heap)` — addresses reachable from static roots (derived from `RootSetCache`, byte-kind filtered)
- `GetOrBuildValidRoots(heap)` — named GC roots (string-projected compatibility shape over `RootSetCache`)

`HeapAnalysisCache` (the concrete `IHeapAnalysisCache` implementation, `DumpDetective.Analysis.Cache`)
additionally exposes `GetOrBuildRoots(heap) -> IReadOnlyList<RootRecord>` for same-assembly
consumers that need the richer `(TargetAddr, RootAddr, Kind)` shape (`RootRecord` can't be exposed
on the public interface — it lives in `DumpDetective.Analysis`, a different assembly than
`DumpDetective.Core.Abstractions`). Internally this is backed by `RootSetCache`, memoized per run.

## IHeapIndexBuilder (`DumpDetective.Analysis.Cache`)
Build-time contract (same `HeapAnalysisCache` instance, different interface):
- `PrebuildHeapIndex(heap, dumpPath, ct, progress, mode)` — builds disk or memory index
- `TryGetHeapIndex(out result)` — check if index is ready

## HeapAnalysisCache (`DumpDetective.Analysis.Cache`)
- Implements both `IHeapAnalysisCache` and `IHeapIndexBuilder`
- Selects disk-backed or memory-backed index writer based on dump size tier
- Lazy-initializes all cached data on first access

---

# 13. 📊 Reporting Layer (`DumpDetective.Reporting`)

## FindingGenerator Pattern
One `IFindingGenerator` per analyzer:
- Receives strongly-typed `AnalyzerDomainResult`
- Emits zero or more `InsightFinding` records
- Registered as `IFindingGenerator` in DI

## Printer Pattern
One `IPrinter` per analyzer:
- Formats domain results for console output
- Ordered by `SortOrder` for consistent section layout

## FindingGenerationPipeline
- Iterates all `IFindingGenerator` registrations
- Attaches `InsightFinding` lists to `AnalyzerRunResult` records
- Runs as a dedicated stage after analysis completes

## ReportBuilder / CanonicalReportFormatter
- Assembles per-analyzer sections into a full structured report
- Supports JSON, markdown, and HTML renderers from the same `AnalysisReportDocument` payload

## Trend Report Composer
- Compares snapshot results across multiple dumps
- Produces lifecycle/regression summaries and snapshot-aware deltas

---

# 14. ⚙️ CLI Staged Pipeline (`DumpDetective.Cli.Pipeline`)

The CLI executes analysis as a linear sequence of `IAnalysisStage` steps
sharing a single `SingleDumpPipelineState` instance:

| Stage | Responsibility |
|---|---|
| `LoadDumpStage` | Loads dump via `IDumpLoader`; stores `DumpLoadContext` |
| `BuildHeapIndexStage` | Calls `IHeapIndexBuilder.PrebuildHeapIndex`; stores index |
| `RunAnalyzersPipelineStage` | Runs all active analyzers; stores `AnalyzerRunResult[]` |
| `GenerateFindingsStage` | Runs `FindingGenerationPipeline`; enriches run results |
| `BuildReportStage` | Renders full report via `ReportBuilderFacade` |
| `WriteOutputStage` | Writes report to console and/or file |

After the pipeline, `InsightEngine` runs cross-cutting analysis on the completed runs.

---

# 15. 📈 Trend Analysis

## TrendAnalyzer (`DumpDetective.Analysis.Trend`)
- Compares `AnalyzerRunResult` collections across multiple dump snapshots
- Delegates domain-specific comparison to registered `IAnalyzerTrendComparer` implementations
- One comparer per analyzer (e.g. `MemoryAnalyzerTrendComparer`, `HangTrendComparer`)

## TrendOrchestrationService (`DumpDetective.Cli.Services`)
- Orchestrates multi-dump trend runs: loads each dump sequentially,
  runs analysis, then invokes `TrendAnalyzer`

## TrendReportComposer (`DumpDetective.Reporting.Services`)
- Assembles per-finding lifecycle (new / worsened / stable / resolved)
  across trend snapshots into a ranked trend report

---

# 17. 📋 Analyzer Coverage Snapshot

Current analyzer set in the CLI factory:
- MemoryAnalyzer
- AllocationPatternAnalyzer
- GCGenerationAnalyzer
- ObjectShapeAnalyzer
- SegmentAnalyzer
- GCRootAnalyzer
- RetentionAnalyzer
- StringAnalyzer
- StaticRootLeakDetector
- LohFragmentationAnalyzer
- ThreadAnalyzer
- LockGraphAnalyzer
- HangAnalyzer
- AsyncTaskAnalyzer
- ThreadStackClusterAnalyzer
- GCHandleAnalyzer
- DependentHandleAnalyzer
- EventLeakAnalyzer
- CollectionAnalyzer
- CrashAnalyzer
- ModuleAnalyzer
- ReferenceChainAnalyzer
- InsightEngine
- TrendAnalyzer
- AppDomainAnalyzer
- JitAnalyzer
- BoxingAnalyzer
- FinalizableObjectAnalyzer
- ArrayAnalyzer
- AsyncStateMachineAnalyzer
- WeakReferenceAnalyzer
- SegmentReservationAnalyzer

---

# 16. 🧩 Extensibility Model

All analyzers implement:

```csharp
public interface IAnalyzer
{
    string Name { get; }
    string Category { get; }     // default: inferred from Name
    IReadOnlyCollection<string> Tags { get; }  // default: []
    int Order { get; }           // default: 0 — controls execution order
    bool IsThreadSafe { get; }   // default: false — opt-in for parallel execution
    ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken);
}
```

Return type `AnalyzerDomainResult` is an abstract record. Each analyzer defines a
strongly-typed subtype (e.g. `MemoryDomainResult`, `ThreadDomainResult`) and stamps
`AnalyzerName` and `Category` via `result.Stamp(this)` at the end of `AnalyzeAsync`.

## Plugin Capability
- New analyzers can be added without modifying core engine
- Analyzer execution pipeline is configurable

---

# 12. 🧵 Concurrency Model

## Phase 1
- Single-threaded (sequential IO optimized)

## Phase 2
Parallelizable:
- Type analysis
- Graph traversal (controlled)

---

# 13. 🚨 Failure Handling
- Invalid objects are skipped
- Missing metadata handled gracefully
- Partial results allowed

---

# 14. 📊 Observability
- Execution time tracking
- Memory usage monitoring
- Logging for:
  - Phase transitions
  - Errors
  - Slow operations

---

# 15. 🚀 Future Enhancements
- Dump diffing engine
- Async/Task analysis
- Event/delegate graph analysis
- Web-based visualization UI

---

# 16. 🏁 Summary

This architecture prioritizes:
- Scalability over convenience
- Streaming over materialization
- Insight over raw data

The system is designed to operate efficiently on massive dumps while providing deep, actionable diagnostics.

---