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

# 2. 🎯 Design Goals

## Primary Goals
- Low memory footprint (sub-linear to dump size)
- High throughput heap scanning
- On-demand deep analysis
- Extensible analyzer framework

## Non-Goals
- Full in-memory object graph representation
- Real-time dump analysis
- GUI-first design (CLI-first)

---

# 3. 🧱 System Architecture

The system follows a **two-phase architecture**:


Phase 1: Streaming Index Build
↓
Phase 2: On-Demand Analysis


---

# 4. 🔄 Data Flow


[Dump File]
↓
[DumpLoader]
↓
[RuntimeFacade]
↓
[HeapStreamer] ───────────────┐
↓ │
[TypeIndexBuilder] │
[ObjectIndexWriter] │
[SegmentAnalyzer] │
↓
[Disk-backed Storage]

[Query / Analysis Request]
↓
[QueryEngine]
↓
[GraphEngine / RootAnalyzer / LeakDetector]
↓
[InsightEngine]
↓
[Output Layer]


---

# 5. 📦 Core Components

## 5.1 Dump Layer

### DumpLoader
Responsible for:
- Loading dump files
- Resolving DAC
- Initializing ClrMD runtime

### RuntimeFacade
Wrapper over ClrMD APIs:
- Abstracts ClrHeap, ClrRuntime, ClrType
- Provides safe, cached access

---

## 5.2 Heap Layer

### HeapStreamer
- Streams heap objects using `yield return`
- Produces minimal `HeapEntry` structs

### HeapEntry
Minimal representation of an object:

```csharp
internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;   // 8 bytes — object size can exceed int.MaxValue on large heaps

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

## 5.3 Storage Layer

### ObjectIndexWriter
- Writes object metadata to disk
- Uses sequential binary format

### ObjectIndexReader
- Reads object metadata efficiently
- Supports memory-mapped access

### Storage Format

Binary layout per object:

| Address (8 bytes) | MethodTable (8 bytes) | Size (8 bytes) |

Characteristics:
- Append-only
- Sequential writes
- Cache-friendly

---

## 5.4 Graph Layer

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

---

## 5.5 Analysis Layer

### MemoryLeakAnalyzer (+ StaticRootLeakDetector)
Together implement the `LeakDetector` role described in guidelines:
- `MemoryLeakAnalyzer` — finalizer queue, duplicate strings, highly-referenced objects
- `StaticRootLeakDetector` — identifies large object graphs retained by static roots
- Uses heuristic scoring: retained size, root type, object lifetime

### ThreadAnalyzer
- Enumerates threads
- Groups stack traces, detects blocking/wait patterns
- Reports finalizer thread state

### GCHandleAnalyzer
- Analyzes GC handles: Strong, Weak, Pinned, Dependent

### CollectionAnalyzer
- Detects wasteful List/Dictionary/Queue/Stack allocations
- Computes fill-rate and wasted capacity per collection

### EventLeakAnalyzer
- Detects unsubscribed event handler leaks

### LockGraphAnalyzer
- Identifies potential deadlocks via lock-graph analysis

### CrashAnalyzer
- Reports active exceptions and crash-thread candidates

### HangAnalyzer
- Detects blocked threads, waiting tasks, and async-over-sync patterns

### ThreadStackClusterAnalyzer
- Groups threads by stack signature to identify contention hotspots

### DependentHandleAnalyzer
- Analyzes ConditionalWeakTable / dependent handle edges

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

---

## 5.6 Query Layer

### QueryEngine
- Provides structured querying capabilities
- Operates on indices (not raw heap)

Examples:
- Top types by memory
- Objects of a specific type
- Reference paths

---

## 5.7 Insight Layer

### InsightEngine
Generates high-level diagnostics:
- Leak suspicion
- GC pressure issues
- Thread contention

---

## 5.8 Output Layer

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
- `GetStaticRootedAddresses(heap)` — addresses reachable from static roots
- `GetOrBuildValidRoots(heap)` — named GC roots

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
- Supports both console (rich) and JSON export modes

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