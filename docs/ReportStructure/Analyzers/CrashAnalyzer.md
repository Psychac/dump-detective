# CrashAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low · ⏳ **Pending**

> Consider renaming to `ExceptionAnalyzer` in a future pass — the analyzer covers all
> exceptions, not just crash-state ones.

## Report Sections Served
- §13.1 Exception Frequency (type counts, active vs total)
- §13.2 Failure Hotspots (thread-to-exception mapping, stack frame hotspots)

---

## Currently Produces
- `CrashDomainResult`: total/active exception counts, exception type distribution
- `TopExceptionInstances` with thread + stack context
- `TopCrashThreadCandidates`

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Exception frequency over time (needs multi-snapshot) | §13.1 | N/A — single snapshot |
| Exception-to-memory correlation (leaks caused by exceptions) | §13 | Medium |
| `InnerException` chain depth | §13.1 | Low |
| Exception-specific frame hotspot aggregation (exception threads only) | §13.2 | Medium |
| Exception origin classification (UserCode / Framework / ThirdParty) | §13.2 | Medium |

---

## Required Changes

1. **Add `InnerExceptionChainDepth`** to `ExceptionInstanceSnapshot` — already follows
   `InnerExceptionType`; extend to chain depth (how deep the inner chain goes). Pure
   metadata read from `ClrException` — no heap enumeration.
2. **Add `ExceptionMemoryFootprint`** — total bytes held by all exception objects of each type.
   Derive from `ExceptionTypeCounts` keys → look up in heap index for size. Adds
   §13 correlation with §3 type table.
3. **Add `ExceptionFrameHotspots`** — top N stack frames aggregated exclusively across threads
   with active exceptions (same pattern as `TopFrameHotspots` in `ThreadAnalyzer`, but scoped
   to exception threads only).
4. **Add `ExceptionOriginClassification`** per `ExceptionInstanceSnapshot` —
   - `UserCode` — frame in a non-system assembly
   - `FrameworkCode` — frame in `System.*` / `Microsoft.*`
   - `ThirdParty` — frame in other assemblies
   Classification uses `ClrModule` name prefix; module list from `ModuleAnalyzer` provides
   the assembly-to-origin mapping.

---

## Phase Assignment

`CrashAnalyzer` is **entirely Phase 2**. Exception analysis uses `ClrThread.CurrentException`,
`ClrException` chain traversal, and `ClrStackFrame` access — all require live runtime state.

Additions:
- `InnerExceptionChainDepth`: pure `ClrException.Inner` traversal — O(depth), negligible
- `ExceptionMemoryFootprint`: O(1) TypeAggregates dict lookup per exception type
- `ExceptionFrameHotspots`: reuses existing frame iteration, filtered to exception threads
- `ExceptionOriginClassification`: string prefix match per frame module name

---

## Related Analyzers
- **`ModuleAnalyzer`** — provides assembly-to-origin mapping for `ExceptionOriginClassification`
- **`ThreadAnalyzer`** — `TopFrameHotspots` pattern reused for `ExceptionFrameHotspots`
- **`TrendAnalyzer`** — exception type count trends across snapshots for §13.1 frequency
