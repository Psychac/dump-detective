# BoxingAnalyzer — Preset Design

Purpose: find boxed value-type instances and surface struct-padding / oversized value types. This document follows the `String Analyzer — Preset Design` style and clarifies how presets should drive numeric caps and analyzer flow.

**Where to look**: - [src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/BoxingAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/BoxingSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/BoxingSectionBuilder.cs)

## Current working (summary)
- `BoxingAnalyzer` primarily operates over Phase‑1 `HeapIndex` `TypeAggregates` and optionally `TypeShapeCache`. It resolves MethodTable -> `ClrType` for each aggregate up to a configured `TypeScanCap` to bound metadata lookups.
- Observed options in code (`BoxingAnalysisOptions`): `TypeScanCap`, `TopBoxedTypeLimit`, `TopPaddingLimit`, `OversizedThresholdBytes`.
- Report rendering uses `TopTypesToShow` / `TopPaddingToShow` in `BoxingSectionBuilder` (currently hard-coded constants) to limit presentation width.

## Goals for preset-driven flow
- Presets should control both numeric caps (type-scan cap, top-N limits, oversized threshold) and behavioral choices (prefer index-only vs allow extra resolution, emit raw exports).
- Analyzer-level explicit options remain able to override preset values.
- `Balanced` should match current defaults.

## Suggested new options (small additions)
- `bool PreferIndexOnly` — when true, avoid additional expensive `ClrType` probing beyond index-derived aggregates and emit a diagnostic if deep resolution is required but unavailable.
- `bool ProduceRawExports` — when true, emit gzipped NDJSON/CSV artifacts containing top-boxed rows and padding candidates and attach `ReportArtifact(FilePath)`.

These flags make preset intent observable and testable.

## How analyzer flow should respect presets
- If `PreferIndexOnly == true` and `TypeAggregates` exist but `TypeShapeCache` or required metadata is missing, skip expensive resolution and mark that detailed analysis was omitted (emit diagnostic).
- `TypeScanCap` limits the number of MethodTable -> `ClrType` resolutions performed; when the scan is capped, `BoxingAnalyzer` should set `TypeScanCapped = true` in the domain result (already implemented).
- `OversizedThresholdBytes` controls which value types count toward the oversized tally and affects the `OversizedValueTypeCount` metric.

## Algorithmic preset policy (logical behaviors)
Presets should change observable code paths when behaviorally meaningful (e.g., require index vs allow full resolution). Prefer named flags over implicit numeric-only changes so unit tests can assert control-flow selection.

Concrete preset mappings (recommended)

- Fast
	- `PreferIndexOnly = true`
	- `TypeScanCap = 5_000`
	- `TopBoxedTypeLimit = 10`
	- `TopPaddingLimit = 10`
	- `OversizedThresholdBytes = 96`
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `PreferIndexOnly = false`
	- `TypeScanCap = 10_000`
	- `TopBoxedTypeLimit = 20`
	- `TopPaddingLimit = 20`
	- `OversizedThresholdBytes = 64`
	- `ProduceRawExports = false`

- Full
	- `PreferIndexOnly = false`
	- `TypeScanCap = 50_000`
	- `TopBoxedTypeLimit = 50`
	- `TopPaddingLimit = 50`
	- `OversizedThresholdBytes = 48`
	- `ProduceRawExports = true`

These mappings trade off metadata-resolution cost for coverage from Fast → Balanced → Full.

## Minimal code changes (implementation plan)
1. Add `PreferIndexOnly` and `ProduceRawExports` to `BoxingAnalysisOptions` and implement `Preset(AnalysisProfile)` mapping the above values.
2. Ensure `BoxingAnalyzer` reads `options.PreferIndexOnly` and short-circuits expensive resolution when requested (emit diagnostic). The analyzer already sets `TypeScanCapped` when `options.TypeScanCap` is reached.
3. Optionally update `BoxingSectionBuilder` to read presentation caps (`TopTypesToShow`, `TopPaddingToShow`) from options instead of hard-coded constants so report width mirrors preset values.
4. Add unit tests for capped scan behavior, oversized-threshold calculation, and optional artifact emission.

Implementation notes:
- `BoxingAnalyzer` already uses `TypeScanCap`, `TopBoxedTypeLimit`, `TopPaddingLimit`, and `OversizedThresholdBytes` — wiring `PreferIndexOnly` and `ProduceRawExports` will be lightweight.
- `ComputeTotalFieldBytes(...)` is defensive — preserve that behavior when raising `TypeScanCap` to avoid noisy failures on corrupt dumps.

## Tests and validation
- Unit tests: mock `HeapIndexBuildResult.TypeAggregates` and `TypeShapeCache` to assert `TopBoxedTypeLimit`/`TopPaddingLimit` truncation and that `TypeScanCapped` is set when `TypeScanCap` exceeded.
- Integration: run `BoxingAnalyzer` with Fast vs Full presets to compare runtime, type-resolution counts, and produced artifacts when `ProduceRawExports=true`.

## Next steps I can take
- Implement `BoxingAnalysisOptions` additions and update `BoxingAnalysisOptions.Preset(...)`.
- Add unit tests that toggle `TypeScanCap` and `PreferIndexOnly` and verify behavior.
- Optionally update `BoxingSectionBuilder` to read presentation caps from options.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.
