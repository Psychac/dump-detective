# ThreadStackClusterAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low

## Report Sections Served
- §7.2 Synchronization Patterns (thread cluster / contention hotspot grouping)

---

## Currently Produces
- Groups threads by stack signature hash
- Reports contention hotspot clusters

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Cluster-level memory retained estimate | §7.2 | Low |
| Integration with `LockGraphAnalyzer` (which cluster holds locks?) | §7.2 | Medium |

---

## Required Changes

1. **Add `LockHolderClusterCount`** — number of clusters where at least one thread holds a
   lock. Cross-reference with `ThreadAnalyzer.ThreadsWithLocks` addresses. No extra scan.
2. **Add `DominantWaitCategory`** per cluster — most common `WaitPattern` in the cluster.
   Derived from existing frame data; zero overhead addition.

---

## Phase Assignment

`ThreadStackClusterAnalyzer` is **entirely Phase 2**. Thread stack frames require live
`runtime.Threads` enumeration. Clustering is a pure in-memory operation over already-read
stack data.

Both additions (`LockHolderClusterCount`, `DominantWaitCategory`) are Phase 2 cross-references
with no index reads or heap scans.

---

## Related Analyzers
- **`LockGraphAnalyzer`** — provides `ThreadsWithLocks` address set used for `LockHolderClusterCount`
- **`ThreadAnalyzer`** — `WaitCategoryDistribution` is the source for `DominantWaitCategory` per cluster
