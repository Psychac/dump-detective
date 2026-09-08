 # 🧠 Project: High-Performance .NET Dump Analyzer (ClrMD 3.1.5)

 Production-grade memory dump analyzer using Microsoft.Diagnostics.Runtime (ClrMD 3.1.5).
 Handles very large dumps (1GB–25GB+), huge heaps, and millions of heap objects.

 ## 📎 Reference Documents

 - **Architecture guidelines**: #file:'D:\POC\DumpAnalyzer\DumpDetective\docs\architecture.md'
 - **Disk-based indexing guidelines**: #file:'D:\POC\DumpAnalyzer\DumpDetective\docs\binary-format.md'
 - **Performance checklist**: #file:'D:\POC\DumpAnalyzer\DumpDetective\docs\performance-checklist.md'

 See referenced docs for design, formats, and perf requirements.

 ---

 # 🚨 Core Philosophy (MANDATORY)

 ## ❌ Never
 - Materialize full heap into memory
 - Call `.ToList()` on heap enumeration
 - Build full object graphs eagerly
 - Use LINQ in hot paths
 - Store large strings redundantly
 - Allocate per-object unnecessarily

 ## ✅ Do
 - Stream (`yield return`)
 - Use `ulong` address as identity
 - Prefer `readonly struct` on hot paths
 - Use disk-backed indices
 - Build indices in one pass
 - Run deep analysis only on filtered subsets

 ---

 # 🏗️ Architecture Overview

 Phases:
 - Phase 1: Streaming index build — single-pass heap scan, minimal metadata, append-only disk writes
 - Phase 2: On-demand analysis — lazy graph traversal, root-paths on request, scoped expensive ops

 ---

 # 📦 Core Modules (high level)
 - Dump layer: `DumpLoader`, `RuntimeFacade`
 - Heap layer: `HeapStreamer`, `HeapEntry`, `TypeIndexBuilder`, object index writers
 - Storage: `ObjectIndexReader` (FileStream + ArrayPool), binary format `Address|MethodTable|Size`
 - Cache: `HeapAnalysisCache` (type/meta caches only)
 - Graph: `ReferenceGraph`, optional `ReverseReferenceIndex`, `RootPathFinder` (BFS, depth 20)
 - Analysis: set of analyzers (leaks, threads, GCHandles, modules, LOH, etc.)
 - Query: `QueryEngine` for index-based queries
 - Insight: `InsightEngine` ranks cross-analyzer findings

 ---

 # 🔥 Performance Rules (STRICT)

 ## Heap Traversal
 ALWAYS use:
 ```csharp
 foreach (var obj in heap.EnumerateObjects())
 ```
 NEVER:
 ```csharp
 heap.EnumerateObjects().ToList()
 ```

 ## Object Representation — keep minimal
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

 - Avoid string allocations and per-object heap allocations in hot paths.
 - Maintain `string->int` type id map; intern/dedupe type names.
 - Use `ArrayPool` for buffers; reuse memory; avoid JSON for large datasets.

 ---

 # 🔗 Graph Rules
 - Forward refs: compute lazily from `ClrObject` fields
 - Reverse refs: never build full reverse graph in memory; build partial indexes scoped to types or suspects
 - Root paths: BFS with depth limit (20), visited `HashSet<ulong>`, stop early when found

 ---

 # 🧪 Leak Detection (short)
 1) Candidate selection: top types by size, long-lived (Gen2/LOH)
 2) Selective analysis: build ref paths, check GC roots, detect static/event/thread retention
 3) Score and rank suspects

 ---

 # 🧵 Thread Analysis
 - Enumerate threads; group stacks; detect blocking and deadlocks

 ---

 # ⚙️ ClrMD Usage (must)
 - Check `obj.IsValid` and `obj.Type != null`
 - Read fields carefully: `field.ReadObject(obj.Address)`
 - Cache `ClrType` metadata and field layouts to avoid repeated expensive calls

 ---

 # 🧠 Caching
 - Allowed: type metadata, `MethodTable -> Type` maps
 - Avoid: full object or full-graph caching

 ---

 # 🧩 Extensibility
 - `IAnalyzer` contract must be implemented by analyzers.

 ```csharp
 public interface IAnalyzer
 {
     string Name { get; }
     string Category { get; }
     IReadOnlyCollection<string> Tags { get; }
     int Order { get; }
     bool IsThreadSafe { get; }
     ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);
 }
 ```

 - Analyzer must return domain result and call `result.Stamp(this)`.

 ## Adding a new analyzer (summary)
 1. Add `XxxDomainResult` in models
 2. Implement `XxxAnalyzer.cs` using streaming heap loops
 3. Register in `DefaultAnalyzerFactory`
 4. Add finding generator, trend comparer, section builder, tests, docs

 ---

 # 📊 Output
 - Support CLI, JSON, structured reports
 - Summarize and rank findings; avoid raw dumps

 ---

 # 🧹 Code Style
 - Avoid LINQ in hot paths; prefer explicit loops
 - Use `Span<T>`/`Memory<T>` and `readonly struct` where applicable

 ---

 # 🧪 Testing
 - Support small and very large dumps
 - Include perf benchmarks and memory validation

 ---

 # 🚀 Advanced (optional)
 - Dump diffing, async analysis, event/delegate graphs, LOH fragmentation

 ---

 # ⚠️ Anti-patterns
 - Don't load entire heap; don't build full adjacency lists; avoid heavy reflection in hot paths

 ---

 # 🏁 Definition of Done
 - Works on 10GB+ dumps without crashing
 - Bounded memory usage, reasonable runtime, no unnecessary allocations

 ---

 ## Codebase Search (SocratiCode) — quick rules
 1) Start with `codebase_search` for discovery and symbol lookups
 2) Check index health with `codebase_status`; re-index if stale
 3) Use `codebase_graph_query` / `codebase_impact` before edits to check blast radius
 4) Use grep only when index unavailable or exact string known (include fallback preamble)
 5) Prefer search results to narrow files before opening

 Short policy: always preface search actions with which tool and why; prefer semantic search.

 ---

 Terse style enforced: drop filler, use fragments, keep code and paths unchanged.
