# ⚡ Performance Checklist

This document defines strict performance guidelines for the dump analyzer.

All contributions MUST comply.

---

# 🧠 Core Principles

- Never trade correctness for performance, but always question unnecessary work
- Memory usage must remain bounded regardless of dump size
- Prefer streaming over materialization
- Disk is cheaper than RAM for large datasets

---

# 🚨 Critical Rules (Must Never Be Violated)

## Heap Handling
- [ ] Do NOT call `.ToList()` on `heap.EnumerateObjects()`
- [ ] Do NOT store all `ClrObject` instances in memory
- [ ] Always stream using `yield return`

## Object Graph
- [ ] Do NOT build full object graphs eagerly
- [ ] Do NOT recursively traverse without depth limits
- [ ] Always use bounded traversal (BFS with limits)

## Allocations
- [ ] Avoid per-object allocations in hot paths
- [ ] Avoid boxing/unboxing
- [ ] Avoid unnecessary string allocations

## Data Structures
- [ ] Do NOT use `Dictionary<string, ...>` in hot paths
- [ ] Prefer `ulong`, `int`, or IDs instead of strings
- [ ] Prefer `struct` over `class` where applicable

---

# 📦 Heap Streaming

- [ ] Use:
  foreach (var obj in heap.EnumerateObjects())

- [ ] Skip invalid objects:
  - `obj.IsValid == false`
  - `obj.Type == null`

- [ ] Extract only minimal metadata:
  - Address
  - MethodTable
  - Size

---

# 🧱 Type Indexing

- [ ] Build in a single pass
- [ ] Use MethodTable as key (ulong)
- [ ] Avoid string-based grouping

---

# 💾 Disk Usage

- [ ] Use append-only writes
- [ ] Prefer binary format over JSON
- [ ] Avoid random writes
- [ ] Use memory-mapped files for large reads

---

# 🔗 Graph Traversal

## Forward References
- [ ] Compute lazily
- [ ] Do NOT cache globally

## Reverse References
- [ ] Build only when needed
- [ ] Scope to subset (types or objects)
- [ ] Avoid full reverse index

---

# 🌳 Root Path Finding

- [ ] Use BFS (not DFS)
- [ ] Apply depth limit (default: 20)
- [ ] Maintain visited set (`HashSet<ulong>`)
- [ ] Stop traversal early when possible

---

# 🧪 Leak Detection

- [ ] Analyze only top N types (default: 20–50)
- [ ] Avoid scanning entire heap multiple times
- [ ] Reuse indices from Phase 1

---

# 🧵 Thread Analysis

- [ ] Safe to fully enumerate threads
- [ ] Avoid redundant stack trace parsing
- [ ] Cache method names if reused

---

# ⚙️ ClrMD Usage

- [ ] Always check:
  - `obj.IsValid`
  - `obj.Type != null`

- [ ] Cache:
  - `ClrType` metadata
  - Field layouts

- [ ] Avoid repeated expensive API calls

---

# 🧠 Memory Optimization

## Strings
- [ ] Deduplicate type names
- [ ] Use string interning or mapping to TypeId

## Buffers
- [ ] Use `ArrayPool<T>` for temporary buffers
- [ ] Avoid large temporary allocations

## Collections
- [ ] Pre-size collections when possible
- [ ] Reuse collections where safe

---

# ⚡ CPU Optimization

- [ ] Avoid LINQ in hot paths
- [ ] Prefer `for` loops over `foreach` in tight loops
- [ ] Minimize branching inside loops

---

# 🔄 Concurrency

- [ ] Phase 1 segment scan may parallelize across `ClrHeap.Segments` (tiered degree of
      parallelism by dump size) — this is the one sanctioned exception; see
      `DiskBackedObjectIndexWriter`
- [ ] Do NOT otherwise parallelize heap enumeration outside the segment scan
- [ ] Parallelize only:
  - The Phase 1 segment scan (above)
  - Type analysis
  - Independent queries

- [ ] Avoid lock contention
- [ ] Use thread-safe structures only when required

---

# 📊 Benchmarking

All major features must be validated against:

- [ ] Small dump (~100MB)
- [ ] Medium dump (~1GB)
- [ ] Large dump (10GB+)

Metrics:
- [ ] Peak memory usage
- [ ] Execution time
- [ ] GC pressure

## Perf Test Pattern

When a feature has distinct phases or competing implementations, prefer one small opt-in perf test that:

- Uses a representative real dump, not synthetic data
- Reuses the same prebuilt index/cache across the compared runs
- Prints per-phase timings so you can tell whether the win is in the dispatcher, the scan, or post-scan work
- Can compare sequential vs parallel, or old vs new, under the same harness
- Is narrow enough to run repeatedly during diagnosis without turning into a full benchmark suite

This pattern is especially useful for analyzers and dispatchers, because it lets the same test answer three questions at once: is it faster, is it more memory-efficient, and which phase is responsible.

---

# 🚨 Red Flags (Immediate Review Required)

- [ ] Memory usage grows linearly with dump size
- [ ] Large spikes in allocations
- [ ] Repeated full heap scans
- [ ] Long GC pauses
- [ ] Excessive disk seeks

---

# 🏁 Definition of Performance Done

A feature is considered performant only if:

- [ ] Works on 10GB+ dumps without failure
- [ ] Memory usage remains stable and bounded
- [ ] No unnecessary allocations in hot paths
- [ ] Execution time is within acceptable limits
- [ ] No regression in existing benchmarks