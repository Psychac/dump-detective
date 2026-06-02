# DependentHandleAnalyzer — Preset Design

Purpose: surface dependent-handle source→target pairs and summarize unresolved-target retention risk. This doc follows the `String Analyzer — Preset Design` style and makes preset-driven behavior explicit.

**Where to look**: - [src/DumpDetective.Analysis/Analyzers/DependentHandleAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/DependentHandleAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/DependentHandleSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/DependentHandleSectionBuilder.cs)

## Current working (summary)
- `DependentHandleAnalyzer` enumerates runtime handles (`ClrRuntime.EnumerateHandles()`) and filters dependent handle kinds to build counts of source types, target types, and source→target pair frequencies.
- Observed option in `DependentHandleAnalysisOptions`: `TopCount` (int) — controls how many top entries to include in each Top list.
- The analyzer is lightweight; runtime cost is proportional to `EnumerateHandles()` and the number of handles present in the process.

## Goals for preset-driven flow
- Presets should drive presentation width only (Top-N) for this analyzer, since the analysis is already inexpensive.
- Keep analyzer-level explicit options able to override preset values.
- `Balanced` should match current defaults.

## Suggested new options (optional)
- `bool PreferIndexOnly` — optional: when true, prefer consuming a prebuilt handle snapshot from Phase‑1 index (if available) to avoid multiple handle enumeration passes in large processes.
- `bool ProduceRawExports` — optional: when true, write top pairs to a gzipped NDJSON artifact and emit `ReportArtifact(FilePath)` for offline analysis.

These are optional because the analyzer is already low-cost; they enable consistent behavior across analyzers and offline inspection when needed.

## How analyzer flow should respect presets
- `TopCount` directly controls the size of `TopSourceTypes`, `TopTargetTypes`, and `TopSource->Target` lists returned in the domain result.
- If `PreferIndexOnly == true` and no index snapshot exists, emit a diagnostic and fall back to `EnumerateHandles()` only if explicitly allowed by options.

## Concrete preset mappings (recommended)

- Fast
	- `TopCount = 8`
	- `PreferIndexOnly = true`
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `TopCount = 15`
	- `PreferIndexOnly = false`
	- `ProduceRawExports = false`

- Full
	- `TopCount = 40`
	- `PreferIndexOnly = false`
	- `ProduceRawExports = true`

These mappings increase the output width for deeper investigations while keeping analysis cost low.

## Minimal code changes (implementation plan)
1. (Optional) Add `PreferIndexOnly` and `ProduceRawExports` to `DependentHandleAnalysisOptions` and update `Preset(AnalysisProfile)` if desired.
2. If `ProduceRawExports` is enabled, stream top-pair rows to a gzipped NDJSON artifact and emit `ReportArtifact(FilePath)` for `WriteOutputStage` to place into `artifacts/`.
3. Add a unit test asserting `TopCount` truncation in the domain result.

## Tests and validation
- Unit: synthetic handle collections to assert Top-N truncation and unresolved-target percentage calculations.
- Integration: run the analyzer on a representative dump and verify `TopCount` affects list sizes and that artifact is produced when `ProduceRawExports=true`.

## Next steps I can take
- If you want, I can add the optional `ProduceRawExports` support and a small unit test verifying `TopCount` behavior.
