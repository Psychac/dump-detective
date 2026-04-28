# 🧠 Project: High-Performance .NET Dump Analyzer (ClrMD 3.1.5)

This project is a production-grade memory dump analyzer built on Microsoft.Diagnostics.Runtime (ClrMD 3.1.5).

The system MUST handle extremely large dumps (1GB–25GB+) with even (1M-80M+) objects on heap and large heaps(1-21GB+) efficiently.

## 📎 Reference Documents

- **Architecture guidelines**: #file:'D:\POC\DumpAnalyzer\DumpDetective\docs\architecture.md'
- **Disk-based indexing guidelines**: #file:'D:\POC\DumpAnalyzer\DumpDetective\docs\binary-format.md'
- **Performance checklist**: #file:'D:\POC\DumpAnalyzer\DumpDetective\docs\performance-checklist.md'

> All design decisions, storage formats, and performance requirements described in this file are elaborated in the reference documents above. When in doubt, consult them.

---

# 🚨 Core Philosophy (MANDATORY)

## ❌ Never Do This
- Do NOT materialize full heap into memory
- Do NOT use `.ToList()` on heap enumeration
- Do NOT build full object graphs eagerly
- Do NOT use LINQ in hot paths
- Do NOT store large strings redundantly
- Do NOT allocate per-object unnecessarily

## ✅ Always Do This
- Use streaming (`yield return`) wherever possible
- Use `ulong address` as primary identity
- Prefer structs over classes for hot data paths
- Use disk-backed storage for large datasets
- Build indices in a single pass
- Perform deep analysis ONLY on filtered subsets

---

# 🏗️ Architecture Overview

The system is divided into phases:

## Phase 1: Streaming Index Build
- Heap is scanned exactly once
- Minimal metadata is extracted
- Data is written to disk-backed storage

## Phase 2: On-Demand Analysis
- Graph traversal is lazy
- Root paths are computed only when requested
- Expensive operations are scoped to small subsets

---

# 📦 Core Modules

## Dump Layer
- `DumpLoader` (in `Analysis.Dump`, registered as `IDumpLoader`)
- `RuntimeFacade` (wraps ClrMD APIs; per-session MethodTable cache)

## Heap Layer
- `HeapStreamer` (stream objects)
- `HeapEntry` (plain `readonly struct`)
- `TypeIndexBuilder` (aggregation)
- `IObjectIndexWriter` / `DiskBackedObjectIndexWriter` / `MemoryBackedObjectIndexWriter`

## Storage Layer
- `ObjectIndexReader` (`IObjectIndexReader`) — sequential `FileStream + ArrayPool` reads
- Binary format: `Address (8) | MethodTable (8) | Size (8)` per record
- Append-only, sequential writes, no random access

## Cache Layer
- `IHeapAnalysisCache` — read-only contract for analyzers
- `IHeapIndexBuilder` — build-time contract (same instance)
- `HeapAnalysisCache` — implements both interfaces

## Graph Layer
- `ReferenceGraph` (lazy forward traversal)
- `ReverseReferenceIndex` (optional, partial, disk-backed)
- `RootPathFinder` (bounded BFS, depth limit 20)

## Analysis Layer (16 analyzers)
- `MemoryLeakAnalyzer` + `StaticRootLeakDetector` (implement `LeakDetector` role)
- `ThreadAnalyzer`
- `GCHandleAnalyzer`
- `SegmentAnalyzer`
- `CollectionAnalyzer`
- `EventLeakAnalyzer`
- `LockGraphAnalyzer`
- `CrashAnalyzer`
- `HangAnalyzer`
- `ThreadStackClusterAnalyzer`
- `DependentHandleAnalyzer`
- `ModuleAnalyzer`
- `LohFragmentationAnalyzer`
- `GCGenerationAnalyzer`
- `ReferenceChainAnalyzer`

## Query Layer
- `QueryEngine` (`IQueryEngine`) — operates on indices, not raw heap
- `TopTypesBySize(int n)`, `ObjectsOfType(string typeName)`
- Exposed as `RuntimeAnalysisContext.Query`

## Insight Layer
- `InsightEngine` — cross-cutting pattern detection across `AnalyzerRunResult[]`
- Emits ranked `InsightFinding` records (Critical → Warning → Info)
- Runs after all analyzers and finding generators complete

---

# 🔥 Performance Rules (STRICT)

## Heap Traversal

✅ ALWAYS use:
```csharp
foreach (var obj in heap.EnumerateObjects())
```

❌ NEVER:
```csharp
heap.EnumerateObjects().ToList()
```

---

## Object Representation

Use minimal struct:

```csharp
internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;   // 8 bytes — object size can exceed int.MaxValue on large heaps

    public HeapEntry(ulong address, ulong methodTable, ulong size)
    {
        Address = address;
        MethodTable = methodTable;
        Size = size;
    }
}
```

Avoid:
- Strings in hot paths
- Object allocations per entry
- `record struct` — synthesized equality/ToString machinery is never needed on hot-path structs

## Type Handling
- Maintain mapping: `string → int` (TypeId)
- Intern or deduplicate all type names

## Memory Management
- Use `ArrayPool` for temporary buffers
- Avoid large allocations
- Reuse buffers wherever possible

## Disk Usage
- Prefer append-only binary format
- Use memory-mapped files for reads
- Avoid JSON for large datasets

---

# 🔗 Graph Traversal Rules

## Forward References
- Compute lazily via `ClrObject` field inspection

## Reverse References
- NEVER build full reverse graph in memory
- Build partial index ONLY when needed
- Scope to:
  - Specific types
  - Suspicious objects

## Root Path Finding
Use BFS with:
- Depth limit (default: 20)
- Visited set (`HashSet<ulong>`)
- Stop traversal early when target found

---

# 🧪 Leak Detection Strategy

## Step 1: Identify Candidates
- Top N types by total size
- Long-lived objects (Gen2, LOH)

## Step 2: Analyze Selectively
- Build reference paths
- Check GC roots
- Detect patterns:
  - Static retention
  - Event handler leaks
  - Thread retention

## Step 3: Score
- Assign suspicion score per type

---

# 🧵 Thread Analysis
- Threads are safe to fully enumerate
- Group stack traces by similarity
- Detect:
  - Blocking patterns
  - Deadlocks (optional advanced)

---

# ⚙️ ClrMD Usage Rules (IMPORTANT)

Always check:
- `obj.IsValid`
- `obj.Type != null`

Access fields carefully:
```csharp
field.ReadObject(obj.Address)
```

Avoid repeated expensive calls. Cache:
- `ClrType` metadata
- Field layouts

---

# 🧠 Caching Strategy

## Allowed
- Type metadata cache
- `MethodTable` → Type info

## Avoid
- Full object caching
- Large graph caching

---

# 🧩 Extensibility

All analyzers must implement `IAnalyzer`:

```csharp
public interface IAnalyzer
{
    string Name { get; }
    string Category { get; }     // default: inferred from Name
    IReadOnlyCollection<string> Tags { get; }  // default: []
    int Order { get; }           // default: 0
    bool IsThreadSafe { get; }   // default: false
    ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken);
}
```

Return type `AnalyzerDomainResult` is an abstract record.
Each analyzer defines a strongly-typed subtype (e.g. `MemoryDomainResult`) and
calls `result.Stamp(this)` at the end of `AnalyzeAsync` to attach name/category.

---

# 📊 Output Guidelines

Support:
- Console output (CLI)
- JSON export
- Structured reports

- Avoid dumping raw data
- Always summarize + rank results

---

# 🧹 Code Style Guidelines
- Avoid LINQ in performance-critical paths
- Prefer explicit loops
- Use `Span<T>`/`Memory<T>` where applicable
- Use `readonly struct` when possible
- Minimize allocations

---

# 🧪 Testing Strategy

Must support:
- Small dumps (fast iteration)
- Large dumps (stress testing)

Include:
- Performance benchmarks
- Memory usage validation

---

# 🚀 Advanced Features (Optional but Encouraged)
- Dump diffing (compare two dumps)
- Async/Task analysis
- Event/delegate graph
- LOH fragmentation analysis

---

# ⚠️ Anti-Patterns (Copilot MUST Avoid)
- Loading entire heap into memory
- Creating `List<ClrObject>`
- Recursive graph traversal without limits
- Using reflection-heavy logic repeatedly
- Using string keys in hot paths
- Building full adjacency lists for all objects

---

# 🏁 Definition of Done

A feature is complete only if:

- It works on 10GB+ dumps without crashing
- Memory usage stays bounded
- Execution time is reasonable
- No unnecessary allocations are introduced

---

# 💡 Guiding Principle

This is **NOT** a demo tool.

This is a **high-performance diagnostic system**.

Every line of code must justify its memory and CPU cost.

---