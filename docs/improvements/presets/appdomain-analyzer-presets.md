# AppDomainAnalyzer — Preset Design

Purpose: enumerate AppDomains, list modules per domain and compute per-module type counts (bounded enumeration of module type maps).

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/AppDomainAnalyzer.cs


Observed implementation details:
- Reads `ClrRuntime.AppDomains` and joins with `TypeAggregates` when available; no full heap scan is required.
- Module type counting uses `ClrModule.EnumerateTypeDefToMethodTableMap()` and is bounded by `ModuleEnumerationLimit` (top N modules by size).
- Output caps are controlled by `TopModuleTypeCountLimit`.

Built-in presets (`AppDomainAnalysisOptions.Preset`):
- Fast: `ModuleEnumerationLimit=25`, `TopModuleTypeCountLimit=10`
- Balanced (default): `ModuleEnumerationLimit=50`, `TopModuleTypeCountLimit=20`
- Full: `ModuleEnumerationLimit=100`, `TopModuleTypeCountLimit=40`


Minimal code changes recommended:
- No-op: `AppDomainAnalysisOptions` already implements `Preset(AnalysisProfile)` and `Default`.
- Consider emitting a non-fatal progress/log message when `ModuleEnumerationLimit` truncates enumeration to make trade-offs visible.

Tests and validation:
- Unit: fake `ClrAppDomain`/`ClrModule` inputs and validate module-type aggregation and truncation.
- Integration: run against multi-AppDomain examples to check per-domain module counts and top-module ordering.

Rationale — when to pick each preset:
- **Fast:** small `ModuleEnumerationLimit` (25) and `TopModuleTypeCountLimit` (10) reduce the number of module-type enumerations; pick this for very large processes or when you only need top offenders per domain.
- **Balanced:** (default) these values (50 / 20) return useful per-domain module summaries while keeping enumeration bounded.
- **Full:** larger caps (100 / 40) are appropriate for deep debugging in small-to-medium processes where exploring more modules is affordable.

Next steps:
- I can implement the `Preset(...)` factory and add a tiny integration check that toggles `ModuleEnumerationLimit`.
