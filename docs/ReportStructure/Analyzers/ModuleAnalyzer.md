# ModuleAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Effort: Low

## Report Sections Served
- §13.2 Failure Hotspots (exception origin classification — assembly source)
- §18.2 Assembly Version Conflicts ✅ (fully covered)
- §18.3 Type Density per Module (heap footprint — partially covered)

---

## Currently Produces
- `ModuleDomainResult`: module counts, dynamic module count, version conflict groups
- `TopModulesBySize`, `ModuleHeapStats`, `HeavyTypeDensityModules`
- ✅ Covers §18.2 (version conflicts) and §18.3 (heap footprint per module) fully

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Module → retained memory (not just type count) | §3.1 utility | Low |
| AOT / R2R detection flag | §19.3 | Low — `ClrModule.IsPEFile` check |
| Per-`AppDomain` module list | §18.1 | Medium — deferred to `AppDomainAnalyzer` |
| Type count per module via `ClrModule.EnumerateTypes()` | §18.3 | Medium — deferred to `AppDomainAnalyzer` |

---

## Required Changes

1. **Add `TotalRetainedEstimateBytes`** to `ModuleHeapStats` — set to `0` initially; populated
   by `DominatorAnalyzer` in a post-pass (same pattern as `TypeSnapshot.EstimatedRetainedBytes`).
2. This analyzer is otherwise well-scoped; no major structural changes needed.

---

## Phase Assignment

`ModuleAnalyzer` is **entirely Phase 2**. Module enumeration uses `ClrRuntime.AppDomains` and
`ClrModule` APIs which require a live runtime.

The `TotalRetainedEstimateBytes` post-pass from `DominatorAnalyzer` is a Phase 2 cross-analyzer
write; `ModuleAnalyzer` only needs to expose the field as settable.

---

## Related Analyzers
- **`AppDomainAnalyzer`** (new) — handles per-domain module list and `EnumerateTypes()` type counts (§18.1, §18.3)
- **`DominatorAnalyzer`** (new) — writes `TotalRetainedEstimateBytes` into `ModuleHeapStats` in post-pass
- **`CrashAnalyzer`** — consumes module list for exception origin classification (§13.2)
- **`JitAnalyzer`** (new) — `ClrModule.IsPEFile` R2R detection complements module analysis for §19.3
