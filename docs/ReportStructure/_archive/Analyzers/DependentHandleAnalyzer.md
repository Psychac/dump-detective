# DependentHandleAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §5.1 Root Distribution (dependent handles — partial)
- §12 Event & Delegate Analysis (potential event source detection — heuristic)
- §24.3 `ConditionalWeakTable<TKey, TValue>` Analysis (source/target type distribution — partial ✅)

---

## Currently Produces
- `DependentHandleDomainResult`: edge counts, source/target type distributions
- Source-target pair type counts
- ✅ Covers §24.3 source/target type distribution

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| ConditionalWeakTable size contribution | §12, §24.3 | Low |
| Integration with §12.1 subscription graph | §12 | Low |
| Live vs dead key analysis (source no longer strongly reachable) | §24.3 | Medium — deferred to `WeakReferenceAnalyzer` |

---

## Required Changes

1. **Add `EstimatedRetainedBytes`** — sum of target object sizes for all resolved edges.
   The target address is already resolved; adding a size lookup is minimal cost (TypeAggregates
   avg size per target MT).
2. **Add `IsPotentialEventSource`** flag — heuristic: if source type name ends in
   `"EventSource"` / `"Observable"` / `"Subject"` or target type name contains `"Handler"`.
   Links dependent handle analysis to §12.

---

## Phase Assignment

`DependentHandleAnalyzer` is **entirely Phase 2**. Dependent handle enumeration uses
`runtime.EnumerateHandles()` with `HandleKind.Dependent` filter.

After `HandleSnapshot.bin` is added (Phase 1, Item 5 — `GCHandleAnalyzer`), dependent handles
are captured in the snapshot (Kind = `DependentHandle`). `DependentHandleAnalyzer` can then
read from `HandleSnapshot.bin` instead of re-calling `runtime.EnumerateHandles()`.

Both new additions are O(edges) computations over the existing edge set — no extra API calls.

---

## Related Analyzers
- **`WeakReferenceAnalyzer`** (new) — handles live/dead key analysis for §24.3 that `DependentHandleAnalyzer` defers
- **`GCHandleAnalyzer`** — `HandleSnapshot.bin` shared input; eliminates duplicate `EnumerateHandles()` calls
- **`EventLeakAnalyzer`** — `IsPotentialEventSource` flag links dependent handles to event subscription detection
