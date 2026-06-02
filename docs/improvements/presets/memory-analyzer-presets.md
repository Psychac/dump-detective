# Memory Analyzer — Preset Design

Purpose: synthesize per-type memory statistics and an object-size histogram using Phase‑1 type statistics when available. This document follows the `String Analyzer — Preset Design` style and clarifies how presets should drive both numeric caps and lightweight flow decisions.

**Where to look**: - [src/DumpDetective.Analysis/Analyzers/MemoryAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/MemoryAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/MemorySectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/MemorySectionBuilder.cs)

## Current working (summary)
- `MemoryAnalyzer` obtains per-type `CachedTypeStatistics` via `IHeapAnalysisCache.GetOrBuildTypeStatistics(heap)` and prefers Phase‑1 `HeapIndex` data when present (no extra heap scan).
- `MemoryAnalysisOptions` exposes `TopBySizeCount`, `TopByCountCount`, and `LohThresholdBytes` which are used to size report tables and LOH calculations.
- When `HeapIndexBuildResult.GlobalSizeBuckets` is present, the analyzer derives an object-size histogram without performing another heap traversal; otherwise histogram is omitted.

## Goals for preset-driven flow
- Presets should control both numeric presentation caps (`TopBySizeCount`, `TopByCountCount`) and sampling/scan behavior via a small semantic flag (prefer index-only where possible).
- Analyzer-level explicit config fields must remain able to override preset values.
- `Balanced` should match existing defaults and behavior.

## Suggested new options (small additions)
- `bool PreferIndexOnly` — when true, require a Phase‑1 index for histogram/extended scans and skip additional heap passes if index missing (emit diagnostic).
- `bool ProduceRawExports` — optional: when true, write the size-bucket histogram and top-type tables to gzipped NDJSON/CSV artifacts for offline analysis.

These additions are optional but make preset intent explicit.

## How analyzer flow should respect presets
- If `PreferIndexOnly == true` and `HeapIndex` is missing => skip histogram derivation and, if requested, record a diagnostic saying Phase‑1 index required for Full-level histogram coverage.
- Numeric caps (`TopBySizeCount`, `TopByCountCount`) drive the report width and the size of domain-result lists returned by the analyzer.
- `ProduceRawExports=true` should cause the analyzer to emit artifacts and attach `ReportArtifact(FilePath)` for `WriteOutputStage` to move into the report `artifacts/` folder.

## Algorithmic preset policy (logical behaviors)
Presets should change observable code paths when behaviorally meaningful (e.g., require index vs allow fallback). Prefer named flags rather than implicit numeric-only changes so tests can assert behavior.

Concrete preset mappings (recommended)

- Fast
	- `PreferIndexOnly = true` (avoid extra heap scans)
	- `TopBySizeCount = 10`
	- `TopByCountCount = 10`
	- `LohThresholdBytes = 85_000`
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `PreferIndexOnly = false`
	- `TopBySizeCount = 20`
	- `TopByCountCount = 20`
	- `LohThresholdBytes = 85_000`
	- `ProduceRawExports = false`

- Full
	- `PreferIndexOnly = false` (allow full derivation, prefer index when present)
	- `TopBySizeCount = 50`
	- `TopByCountCount = 50`
	- `LohThresholdBytes = 85_000`
	- `ProduceRawExports = true` (emit artifacts for offline analysis)

These mappings prioritize compact outputs for Fast, balanced coverage for Balanced, and broader outputs plus exports for Full.

## Minimal code changes (implementation plan)
1. Add `PreferIndexOnly` and `ProduceRawExports` to `MemoryAnalysisOptions` and implement `Preset(AnalysisProfile)` mapping to the above values.
2. Keep `MemoryAnalyzer` logic unchanged other than reading `options.PreferIndexOnly` and `options.ProduceRawExports` where applicable. Prefer wiring the checks around the existing `TryGetHeapIndex` usage.
3. Optionally emit a `ReportArtifact(FilePath)` when `ProduceRawExports` is enabled (NDJSON/CSV gz file containing top-type rows and histogram buckets).
4. Add unit tests that assert top-N truncation and that histogram appears only when `HeapIndex.GlobalSizeBuckets` is present or when `PreferIndexOnly` forces a diagnostic.

Implementation notes:
- `MemoryAnalyzer` already respects `HeapIndex.GlobalSizeBuckets` (zero extra heap scan). Use that existing check to honor `PreferIndexOnly` without additional refactor.
- `MemorySectionBuilder` uses a hard-coded `TopItems = 20`; consider exposing that via options or reading `options.TopBySizeCount` when rendering if you want report layout to strictly reflect presets.

## Tests and validation
- Unit tests: mock `CachedTypeStatistics` and `HeapIndexBuildResult` to validate top lists and histogram presence.
- Integration: validate that enabling `ProduceRawExports` creates on-disk artifacts that `WriteOutputStage` moves into the report `artifacts/` folder.

## Next steps I can take
- Implement the `MemoryAnalysisOptions` additions and update `MemoryAnalysisOptions.Preset(...)`.
- Add a unit test asserting histogram derivation behaves correctly with and without `GlobalSizeBuckets`.
- Optionally update `MemorySectionBuilder` to use options-driven `TopItems` instead of the hard-coded constant.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.
