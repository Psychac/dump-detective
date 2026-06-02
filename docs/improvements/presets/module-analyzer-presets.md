# ModuleAnalyzer — Preset Design

Purpose: examine loaded modules, detect version conflicts, and optionally attribute live heap memory per module (when Phase‑1 heap indices are present). This doc makes preset behavior explicit and testable.

**Where to look**: - [DumpDetective/Analyzers/ModuleAnalyzer.cs](DumpDetective/Analyzers/ModuleAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/ModuleSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/ModuleSectionBuilder.cs)

## Current behavior (summary)
- `ModuleAnalyzer` enumerates `ClrRuntime.EnumerateModules()`, pools repeated strings locally, and builds a `ModuleAnalysis` with groups by file name.
- It exposes numeric knobs through `ModuleAnalysisOptions` such as `TopLoadedAssembliesCount` and `TopModulesByHeapCount` (used by `ModuleSectionBuilder`).
- Heap attribution (`TopModulesByHeapMemory`, `DensityAnomalies`) is produced only when a prebuilt heap index is available; otherwise those sections are omitted.
- Version conflict detection groups modules by file-name then probes per-instance assembly identity via `ModuleProbe.ProbeAssemblyIdentity(...)`, reporting real conflicts when multiple distinct known identities exist.

## Goals for preset-driven flow
- Make presets responsible for both numeric limits and the analyzer's conditional behaviors (e.g., whether to attempt heap attribution, how aggressively to compute density checks).
- Keep analyzer-level explicit config fields able to override preset values.
- Preserve backward compatibility: `Balanced` maps to current defaults.

## Suggested new options (optional, minimal additions)
- `enum HeapAttributionMode { Disabled, WhenIndexAvailable, ForceIfPossible }` — controls whether the analyzer attempts heap attribution.
- `bool PreferIndexOnly` — when true, avoid additional expensive probes (e.g., manifest probing) and rely on index data where available.
- `int TopLoadedAssembliesCount` — existing knob, clarifies listing width.
- `int TopModulesByHeapCount` — existing knob, applies only when attribution is performed.

The additional enums are optional; the analyzer already behaves sensibly by relying on `IHeapIndexBuilder.TryGetHeapIndex`. Named flags make intent explicit and testable.

## How analyzer flow should respect presets
- If `HeapAttributionMode == Disabled` => skip `BuildModuleHeapStats(...)` and do not attempt heap aggregation.
- If `HeapAttributionMode == WhenIndexAvailable` => current behavior: attribute only when index.Modules exists and has data.
- If `HeapAttributionMode == ForceIfPossible` => attempt attribution and surface a diagnostic if the index is missing or incomplete.
- `PreferIndexOnly` toggles whether the analyzer performs additional on-process probes versus relying solely on index-derived aggregates.

## Algorithmic preset policy (logical behaviors)
Presets should set both numeric caps and the attribution policy so that switching profiles changes observable code paths. Prefer explicit enum flags rather than implicit branching.

Concrete preset mappings (recommended)

- Fast
	- `HeapAttributionMode = WhenIndexAvailable`
	- `TopLoadedAssembliesCount = 15`
	- `TopModulesByHeapCount = 10`
	- `HeavyModuleWarningThresholdBytes = 300 * 1024 * 1024` (300 MB)
	- `DensityAnomalyMinBytes = 100 * 1024 * 1024` (100 MB)
	- `DensityAnomalyMaxTypes = 3`
	- `PreferIndexOnly = true`

- Balanced (baseline / existing defaults)
	- `HeapAttributionMode = WhenIndexAvailable`
	- `TopLoadedAssembliesCount = 30`
	- `TopModulesByHeapCount = 20`
	- `HeavyModuleWarningThresholdBytes = 200 * 1024 * 1024` (200 MB)
	- `DensityAnomalyMinBytes = 50 * 1024 * 1024` (50 MB)
	- `DensityAnomalyMaxTypes = 5`
	- `PreferIndexOnly = false`

- Full
	- `HeapAttributionMode = ForceIfPossible` (or `WhenIndexAvailable` with diagnostics)
	- `TopLoadedAssembliesCount = 80`
	- `TopModulesByHeapCount = 50`
	- `HeavyModuleWarningThresholdBytes = 100 * 1024 * 1024` (100 MB)
	- `DensityAnomalyMinBytes = 20 * 1024 * 1024` (20 MB)
	- `DensityAnomalyMaxTypes = 10`
	- `PreferIndexOnly = false`

These values are tuned to escalate coverage and cost from Fast → Balanced → Full.

## Minimal code changes (implementation plan)
1. Add `HeapAttributionMode` enum (optional) and extend `ModuleAnalysisOptions.Preset(...)` to populate it per profile.
2. Update `BuildModuleHeapStats(IHeapAnalysisCache cache, ModuleAnalysisOptions options)` to short-circuit when `HeapAttributionMode == Disabled` and to emit a diagnostic when `ForceIfPossible` cannot find an index.
3. Ensure `ModuleSectionBuilder` continues to tolerate `TopModulesByHeapMemory == null` (current code already handles this).
4. Add unit tests that mock `IHeapIndexBuilder.TryGetHeapIndex(...)` to cover the three attribution modes.

Implementation notes:
- `BuildModuleHeapStats` already checks `cache is not IHeapIndexBuilder builder || !builder.TryGetHeapIndex(out var index)` — wire the attribution-mode checks around this existing logic rather than reworking it.
- `ModuleSectionBuilder` uses a constant `TopModulesToShow = 30`; consider reading from `ModuleAnalysisOptions` when rendering if you want the report width to mirror `TopLoadedAssembliesCount`.

## Tests and validation
- Unit tests: assert that `BuildDomainResult(...)` limits `topModules` according to `TopLoadedAssembliesCount` and that conflict grouping remains unchanged.
- Unit tests: verify `BuildModuleHeapStats(...)` returns `null` when index missing and `HeapAttributionMode` disables attribution; verify diagnostics when `ForceIfPossible` and index missing.
- Integration: run a medium-sized dump with `Fast`, `Balanced`, and `Full` presets; compare report sections and ensure `TopModulesByHeapMemory` appears only when expected.

## Next steps I can take
- Implement the small `ModuleAnalysisOptions` additions and update `Preset(...)` mapping.
- Add unit tests that mock `IHeapIndexBuilder` to validate the three attribution modes.
- Optionally update `ModuleSectionBuilder` to use `TopLoadedAssembliesCount` instead of the hardcoded `TopModulesToShow` constant for report width.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.
