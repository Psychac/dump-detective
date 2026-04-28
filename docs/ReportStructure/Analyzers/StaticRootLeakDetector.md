# StaticRootLeakDetector — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low

## Report Sections Served
- §4.3 Retention Patterns (static chain detection — partial)
- §5.1 Root Distribution (static root kind — partial)
- §5.2 Root Severity Ranking (top roots by retained bytes — static only)
- §5.3 Root Paths (BFS from static roots)
- §6.2 Leak Classification (StaticRetention class)

> Long-term, this analyzer may become an internal component of `GCRootAnalyzer` (new).

---

## Currently Produces
- `StaticRootDomainResult`: root count, total retained bytes, top roots by retained bytes
- Walks static roots via `cache.GetOrBuildValidRoots(heap)` and BFS-limits each root

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Root type breakdown (field type that holds the root) | §5.1 | Medium |
| Leak classification label ("static retention") | §6.2 | Low — implied but not explicit |
| Confidence/cap signal (when BFS was cut short) | §17 | Medium |

---

## Required Changes

1. **Add `BfsCappedCount`** — number of roots where BFS hit `MaxRetainedObjectsToScan` budget.
   Already detectable; just not surfaced. Add to `StaticRootDomainResult`.
2. **Add `RetentionPatternHints`** — `IReadOnlyList<string>` — heuristic labels per significant
   root (e.g., `"Dictionary<K,V> cache"`, `"EventHandler chain"`) based on type name pattern
   matching. Pure string analysis — no extra heap scan.
3. **Consolidate** with `GCRootAnalyzer` long-term: static root detection should be one input
   stream to a unified root intelligence layer. `StaticRootLeakDetector` may eventually
   become an internal component of `GCRootAnalyzer`.

---

## Phase Assignment

`StaticRootLeakDetector` is **entirely Phase 2**. It uses `cache.GetOrBuildValidRoots(heap)`
which performs a live heap root enumeration.

**After `RootIndex.bin` is added** (Phase 1, Item 10 — `GCRootAnalyzer`):
- `cache.GetOrBuildValidRoots()` can be backed by `RootIndex.bin` `Kind == 0 (Static)`
- Root enumeration becomes an index read instead of a live heap walk
- `StaticRootLeakDetector` remains Phase 2 but its root source becomes pre-indexed

> `BfsCappedCount` and `RetentionPatternHints` are pure Phase 2 additions — no index changes needed.

---

## Related Analyzers
- **`GCRootAnalyzer`** (new) — unified cross-kind root analysis; `StaticRootLeakDetector` is the static-only precursor
- **`ReferenceChainAnalyzer`** — uses same `BoundedRootPathFinder` utility; root path output is complementary
- **`DominatorAnalyzer`** (new) — uses retained size estimates that `StaticRootLeakDetector` approximates per-root
