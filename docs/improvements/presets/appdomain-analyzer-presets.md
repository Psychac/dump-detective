# AppDomain Analyzer — Preset Design

Purpose: enumerate AppDomains, show per-domain module counts and compute per-module type statistics using a bounded enumeration of module type→method-table maps.

Where to look in the repo:
- Analyzer implementation: [src/DumpDetective.Analysis/Analyzers/AppDomainAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/AppDomainAnalyzer.cs)
- Report rendering: [src/DumpDetective.Reporting/SectionBuilders/AppDomainSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/AppDomainSectionBuilder.cs)

## Current working (summary)
- Reads `ClrRuntime.AppDomains` and builds per-domain `AppDomainSnapshot` objects; no full heap enumeration is performed.
- When available, the analyzer joins with the `TypeAggregates` index (MT → aggregate) from the `HeapIndexBuildResult` to estimate managed bytes per domain.
- Module-level type discovery uses `ClrModule.EnumerateTypeDefToMethodTableMap()`; this is bounded by `ModuleEnumerationLimit` (modules ranked by `ClrModule.Size`) to keep work bounded on processes with many modules.
- The final top-module list is capped by `TopModuleTypeCountLimit` and sorted by defined `TypeCount`.

## Observed implementation details
- Per-domain processing collects: domain name, address, id, module count, and an estimated managed-bytes total derived from `TypeAggregates` when present.
- A shared dictionary keyed by module address (`ulong`) deduplicates module entries that appear in multiple domains.
- For each enumerated module the analyzer accumulates: `TypeCount`, `LiveTypeCount`, `ObjectCount`, and `TotalBytes` (via the index).
- The analyzer increments counters for `IsDynamic` modules and anonymous modules (empty name).

## Goals for preset-driven flow
- Make presets actionable: each preset should declaratively set both numeric budgets (caps) and safe, documented behavioral flags that control analyzer flow.
- Preserve backward compatibility where possible: avoid changing core algorithms unless an explicit enum/flag is provided and documented.
- Ensure presets produce predictable cost and observable signals (diagnostics or report sections) when they reduce coverage.

## Suggested options (minimal)
- `int ModuleEnumerationLimit` — number of top modules (by module size) to enumerate types for per domain. Controls CPU cost.
- `int TopModuleTypeCountLimit` — how many modules to include in the final Top Modules table.
- `bool EmitTruncationNotice` — optional; emit a non-fatal log/analysis note when `ModuleEnumerationLimit` causes truncation.

These are already present in `AppDomainAnalysisOptions` (see analyzer). The suggestions are primarily about observability rather than new algorithms.

## Concrete preset mappings (numeric + behavioral)
Each preset below lists the numeric caps and the recommended logical flags/enums to drive analyzer flow. These values are actionable: they can be set in `AppDomainAnalysisOptions.Preset(...)` and are intended to be testable and observable.

- Fast
	- Numeric:
		- `ModuleEnumerationLimit = 25`
		- `TopModuleTypeCountLimit = 10`
	- Behavioral:
		- `ModuleSelectionMode = TopBySize`
		- `TypeEnumerationMode = Sampled`
		- `PreferIndexOnly = true`
		- `IncludeExcludedModuleSummary = false`
	- When to use: quick triage on large processes; low CPU/memory budget.

- Balanced (baseline / existing defaults)
	- Numeric:
		- `ModuleEnumerationLimit = 50`
		- `TopModuleTypeCountLimit = 20`
	- Behavioral:
		- `ModuleSelectionMode = TopBySize`
		- `TypeEnumerationMode = Full` if `TypeAggregates` present else `Sampled`
		- `PreferIndexOnly = false`
		- `IncludeExcludedModuleSummary = true`
	- When to use: default; balances coverage and cost.

- Full
	- Numeric:
		- `ModuleEnumerationLimit = 100`
		- `TopModuleTypeCountLimit = 40`
	- Behavioral:
		- `ModuleSelectionMode = TopByTypeCount` (or hybrid)
		- `TypeEnumerationMode = Full`
		- `PreferIndexOnly = false`
		- `IncludeExcludedModuleSummary = true`
	- When to use: deep debugging on small-to-medium processes; prefers thorough enumeration.

These mappings mirror current code defaults and extend them with explicit, named behaviors so presets are both powerful and auditable.

## Minimal code changes (implementation plan)
1. Confirm `AppDomainAnalysisOptions.Preset(AnalysisProfile)` sets the above values (no-op if already implemented).
2. Add a small, non-fatal diagnostic when enumeration was truncated (controlled by `EmitTruncationNotice`).
3. Update `AppDomainSectionBuilder` captions/text if needed to mention the enumeration cap when `EmitTruncationNotice` is true.
4. Add unit tests that assert enumeration truncation behavior and that `TopModulesByTypeCount` respects `TopModuleTypeCountLimit`.

## Algorithmic preset policy (logical behaviors)
Presets may and should be allowed to change analyzer control flow when the change is explicit, documented, and safe. Achieve this via well-named flags/enums in `AppDomainAnalysisOptions` so behavior is observable and testable.

Suggested enum/flags (example additions to `AppDomainAnalysisOptions`):
- `enum ModuleSelectionMode { TopBySize, TopByTypeCount, StratifiedSample }`
	- Controls which modules are selected for type enumeration when the domain has more modules than `ModuleEnumerationLimit`.
- `enum TypeEnumerationMode { Full, Sampled, Skip }`
	- `Full`: enumerate all method-table mappings for selected modules.
	- `Sampled`: sample type-def→mt mappings (bounded budget); use when index missing or to reduce cost.
	- `Skip`: do not enumerate types; only report module counts and index-derived estimates.
- `bool IncludeExcludedModuleSummary`
	- When true, include a brief summary of modules that were excluded by selection/truncation (counts, size buckets).
- `bool PreferIndexOnly`
	- When true, rely only on `TypeAggregates` (if present) and skip type enumeration when index is missing.

Example preset logical mappings
- Fast
	- `ModuleSelectionMode = TopBySize`
	- `TypeEnumerationMode = Sampled`
	- `PreferIndexOnly = true`
	- `IncludeExcludedModuleSummary = false`
- Balanced
	- `ModuleSelectionMode = TopBySize`
	- `TypeEnumerationMode = Full` if `TypeAggregates` present else `Sampled`
	- `PreferIndexOnly = false`
	- `IncludeExcludedModuleSummary = true`
- Full
	- `ModuleSelectionMode = TopByTypeCount` (or a hybrid)
	- `TypeEnumerationMode = Full`
	- `PreferIndexOnly = false`
	- `IncludeExcludedModuleSummary = true`

How the analyzer should respect flags (implementation sketch)
- Compute `selectedModules` using `ModuleSelectionMode` + `ModuleEnumerationLimit`.
- If `TypeEnumerationMode == Skip` -> do not call `EnumerateTypeDefToMethodTableMap()`; rely only on index-derived aggregates and module metadata.
- If `TypeEnumerationMode == Sampled` -> enumerate but stop after N method-tables per module (budgeted sampling), record sampling metadata in accumulator.
- If `PreferIndexOnly && typeAggregates == null` -> skip expensive enumeration and mark result with a diagnostic suggesting re-run with index available or a higher preset.
- If `IncludeExcludedModuleSummary` -> add a small block in `AppDomainSectionBuilder` showing counts/sizes of excluded modules.

Testing and observability
- Add unit tests for each `ModuleSelectionMode` and `TypeEnumerationMode` using small fakes of `ClrModule` that expose predictable `EnumerateTypeDefToMethodTableMap()` sequences.
- Verify that `PreferIndexOnly` causes early-skip behavior and that the report contains a diagnostic when index is missing.
- Ensure `AppDomainSectionBuilder` renders the excluded-module summary when enabled.

Rationale
- Using explicit enums keeps presets powerful but transparent: a preset changes behavior only via named options that are easy to document, test and override in configuration. This avoids hidden internal branching while enabling richer preset semantics.

## Tests and validation
- Unit tests: mock `ClrAppDomain`/`ClrModule` to validate per-module accumulation, deduplication by module address, and truncation when `ModuleEnumerationLimit` is smaller than the domain module count.
- Integration: run analyzer on multi-AppDomain test dumps and verify that the `APPDOMAIN INVENTORY` and `TYPE DENSITY PER MODULE` tables match expected sizes and that estimated managed bytes come from `TypeAggregates` when present.

## Next steps
- I can implement or verify the `Preset(...)` values in `AppDomainAnalysisOptions` and add the optional truncation diagnostic.
- Would you like me to update the options factory, add the truncation notice, or write the tests next?

## Implementation status
- `AppDomainAnalysisOptions`: Added `ModuleSelectionMode`, `TypeEnumerationMode`, `PreferIndexOnly`, `IncludeExcludedModuleSummary`, and `EmitTruncationNotice` with `Preset(AnalysisProfile)` mappings.
- `AppDomainAnalyzer`: Respects the new flags; implements sampled enumeration, `PreferIndexOnly` early-skip, and emits truncation warnings and `ExcludedModuleCount` metric.
- `AppDomainSectionBuilder`: Renders `Warnings` and an `EXCLUDED MODULES` summary/metric when present.
- Tests: Added unit test `AppDomainSectionBuilderTests.Build_IncludesWarningsAndExcludedModuleSummary` (passes in CI-local run).

If you want, I can now add more unit tests for `TypeEnumerationMode` behavior and `PreferIndexOnly` skipping.
