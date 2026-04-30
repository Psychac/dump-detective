# CollectionAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §4.3 Retention Patterns (cache chain detection — partial)
- §6.2 Leak Classification (cache retention — partially labeled)
- §22.3 Sparse & Wasteful Arrays (backing array fill rate — partial)

---

## Currently Produces
- `CollectionDomainResult`: counts by collection type, total wasted memory
- `WastefulCollectionSnapshot`: type, capacity, fill rate, wasted memory, element info

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Cache-pattern classification (Dictionary used as unbounded cache) | §4.3, §6.2 | Medium |
| GC generation of wasteful collections (Gen2 oversized = more concerning) | §6.2 | Medium |

---

## Required Changes

1. **Add `CachePatternScore`** to `WastefulCollectionSnapshot` — heuristic 0–10 scoring whether
   a collection looks like an unbounded cache: high capacity, high count, in Gen2, type name
   contains "Cache/Store/Registry/Pool". Pure field additions; no new scan.
2. **Add `Generation`** field to `WastefulCollectionSnapshot` — capture the generation of the
   collection object during the existing scan. Requires resolving `ClrHeap.GetGeneration(address)`
   for each wasteful collection found (small set, bounded cost).

---

## Phase Assignment

`CollectionAnalyzer` is **entirely Phase 2**. Collection capacity analysis requires reading
internal fields (`_count`, `_size`, `_entries.Length`, etc.) via `ClrType.Fields` —
live heap access only.

The `Generation` field addition is a single `heap.GetGeneration(address)` call per wasteful
collection object. The wasteful collection set is small (bounded by the existing cap), so
this is O(small_N) — negligible overhead.

---

## Related Analyzers
- **`DominatorAnalyzer`** (new) — `StaticCache` retention pattern classification uses collection type names
- **`EventLeakAnalyzer`** — event chain retention complementary to collection cache retention in §4.3
- **`ArrayAnalyzer`** (new) — §22.3 backing array sparse analysis extends what `CollectionAnalyzer` produces for fill rate
