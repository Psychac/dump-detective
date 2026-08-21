# ⚡ Performance Checklist

This document defines strict performance guidelines for the dump analyzer.

All contributions MUST comply.

---

# 🧠 Core Principles

- Never trade correctness for performance, but always question unnecessary work
- Memory usage must remain bounded regardless of dump size — **non-negotiable, unconditional**
- Work (traversal depth, item counts, scan passes) should be bounded too, but only where the bound
  doesn't change *what number gets reported* — see the split below
- Prefer streaming over materialization
- Disk is cheaper than RAM for large datasets

## Bounded memory vs. bounded work — read this before adding or removing a cap

These are two different constraints and this document used to conflate them. Getting the difference
wrong in either direction is a real bug, not a style nit:

- **Bounded memory** (§ "Never Negotiable" below): resident memory must not scale with dump size.
  This is architectural and unconditional — it holds regardless of what an analyzer is trying to
  compute, and dropping it is not on the table.
- **Bounded work** (§ "Case-by-Case" below): limiting *how much* gets processed (a depth limit, a
  top-N cap, a sample stride) is a legitimate scoping choice **only when it doesn't silently
  redefine a total, count, or exactness flag the analyzer reports**. A cap that truncates a
  **render-layer list** (the top 20 rows shown in a table) is fine. A cap that truncates an
  **accumulator, a total, or a boolean like "is this object retained"** while the analyzer still
  reports that value as authoritative is a correctness bug wearing a performance-rule costume —
  streaming and disk-backed indexing exist precisely so the work doesn't need to be bounded to keep
  memory bounded. See
  [analysis-profile-removal-plan.md](refactor/analysis-profile-removal-plan.md) (§3 categories,
  §6.1-6.3, §9 per-analyzer audit) for the worked examples that motivated this split, and
  [dominator-tree-phase1-integration.md](analysis/phase1-redesigns/dominator-tree-phase1-integration.md)
  for the concrete architecture (uncapped reachability walk + disk-backed exact dominator tree) that
  makes "bounded work" unnecessary for reachability/retained-bytes correctness.
- When in doubt, ask: *if I raised this cap to infinity, would memory usage grow with dump size?* If
  no — it was a bounded-work cap masquerading as a bounded-memory one; prefer removing it (uncapped +
  streaming/disk-backed) over keeping it. If yes — it's genuinely bounded-memory; keep it and make
  sure it's actually enforced via streaming/disk, not just via a `Top(N)`/`Take(N)` that still
  materializes the full set first.

---

# 🔒 Bounded Memory (Never Negotiable)

These hold unconditionally, for every analyzer, regardless of exactness goals. None of these are
affected by the "remove the caps for exactness" work — CLAUDE.md's bounded-memory rule stands,
unmodified.

## Heap Handling
- [ ] Do NOT call `.ToList()` on `heap.EnumerateObjects()`
- [ ] Do NOT store all `ClrObject` instances in memory
- [ ] Always stream using `yield return`

## Object Graph
- [ ] Do NOT build the full forward or reverse object graph **resident in memory** — build it
      disk-backed (see [Graph Traversal](#-graph-traversal) below) or compute it lazily per query
- [ ] Traversal must terminate via a `HashSet<ulong>`/dense-id visited set, not by relying on a depth
      or item cap to bound memory

## Allocations
- [ ] Avoid per-object allocations in hot paths
- [ ] Avoid boxing/unboxing
- [ ] Avoid unnecessary string allocations

## Data Structures
- [ ] Do NOT use `Dictionary<string, ...>` in hot paths
- [ ] Prefer `ulong`, `int`, or IDs instead of strings
- [ ] Prefer `struct` over `class` where applicable

## Memory Optimization

### Strings
- [ ] Deduplicate type names
- [ ] Use string interning or mapping to TypeId

### Buffers
- [ ] Use `ArrayPool<T>` for temporary buffers
- [ ] Avoid large temporary allocations

### Collections
- [ ] Pre-size collections when possible
- [ ] Reuse collections where safe

---

# ⏱️ Bounded Work (Case-by-Case — Not a Blanket Rule)

A work bound is legitimate when it scopes *effort* (which items get deep analysis, which rows get
displayed, which path gets shown as a representative example) — and illegitimate when it silently
truncates a value the analyzer still reports as exact or authoritative. Each item below states which
side of that line it's on.

## Traversal depth / path selection
- [ ] Root-path-finding BFS depth limit (default: 20) is a **display-path selection** default — it
      picks *which* representative path to show for a root-cause finding, not whether an object is
      reachable or retained. Reachability and retained-bytes exactness come from the disk-backed
      dominator-tree / reachability index (uncapped — see
      [dominator-tree-phase1-integration.md](analysis/phase1-redesigns/dominator-tree-phase1-integration.md)),
      not from this BFS. Keep the depth limit for path display; do not use it as a proxy for "is
      retained."
- [ ] Use BFS (not DFS) for path-finding, with a visited set — same rule whether or not a depth limit
      is applied
- [ ] Stop traversal early once the query is answered (e.g. target found) — this is a work
      optimization, not a correctness bound

## Reverse references
- [ ] The reverse-edge index is a **full, disk-backed, uncapped** index fed by the Stage A
      reachability walk (`MaxParentsPerChild` was deleted outright, not raised — see
      [dominator-tree-phase1-integration.md §3](analysis/phase1-redesigns/dominator-tree-phase1-integration.md#3-stage-a--reachability-walk-shipped)).
      "Avoid a full reverse index" is no longer the rule; the old working-set concern is resolved by
      disk-backed storage plus bounded per-query read memory, not by refusing to build the index
- [ ] Forward references: still fine to compute lazily and not cache globally — this remains a
      genuine memory-bounding choice, not a work cap on a reported value

## Leak detection
- [ ] Top-N **candidate selection** (default 20-50 types) for deep, expensive per-object analysis is
      a legitimate scoping heuristic — it decides *which* types get the expensive treatment
- [ ] That selection must not be the mechanism that caps a **reported total** (e.g. "total leaked
      bytes across the whole population"). Compute totals over the full population via streaming
      aggregation; apply the top-N cap only to which items get the expensive follow-up or which rows
      render
- [ ] Avoid scanning the entire heap multiple times; reuse indices from Phase 1

## Rendering / report display
- [ ] Domain results should carry the complete, exact ranked collection; apply row limits
      (`Top(N)`/`Take(N)`) only in the section-builder / render layer, after the exact aggregate is
      computed — never inside the analyzer's accumulation loop

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
- [ ] Full, disk-backed, uncapped index — see [Bounded Work § Reverse references](#-bounded-work-case-by-case--not-a-blanket-rule)
      for why "avoid a full reverse index" no longer applies
- [ ] Scope in-memory working set per query (segment/bucket reads), not by refusing to build the
      index at all

---

# 🌳 Root Path Finding

- [ ] Use BFS (not DFS)
- [ ] Depth limit (default: 20) selects the representative **display path** only — see
      [Bounded Work § Traversal depth](#-bounded-work-case-by-case--not-a-blanket-rule)
- [ ] Maintain visited set (`HashSet<ulong>`)
- [ ] Stop traversal early when possible

---

# 🧪 Leak Detection

- [ ] Scope expensive per-object analysis to top N candidate types (default 20-50); see
      [Bounded Work § Leak detection](#-bounded-work-case-by-case--not-a-blanket-rule) for why this
      must not cap reported totals
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
- [ ] A cap on a work bound (depth/top-N/sample) is the only thing standing between a reported
      "total"/"count"/"is retained" value and correctness — see
      [Bounded Work](#-bounded-work-case-by-case--not-a-blanket-rule)

---

# 🏁 Definition of Performance Done

A feature is considered performant only if:

- [ ] Works on 10GB+ dumps without failure
- [ ] Memory usage remains stable and bounded
- [ ] No unnecessary allocations in hot paths
- [ ] Execution time is within acceptable limits
- [ ] No regression in existing benchmarks
- [ ] Any remaining work bound (depth/top-N/sample) is justified per
      [Bounded Work](#-bounded-work-case-by-case--not-a-blanket-rule) — it scopes effort or display,
      not a reported total
