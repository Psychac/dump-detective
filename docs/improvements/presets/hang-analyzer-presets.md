# HangAnalyzer — Preset Design

Purpose: detect hangs, waiting-thread pressure, and thread-pool/task backlog. This doc follows the `String Analyzer — Preset Design` style and makes preset-driven behavior explicit (numeric caps + analyzer flow).

**Where to look**: - [DumpDetective/Analyzers/HangAnalyzer.cs](DumpDetective/Analyzers/HangAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/HangSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/HangSectionBuilder.cs)

## Current working (summary)
- Options observed in code (`HangAnalysisOptions`): `LongWaitThreshold`, `HighThreadPoolThreshold`, `MaxTasksToScan`, `TopWaitingThreadsPerGroup`, `TopContinuationTypesToShow`.
- `HangAnalyzer` computes a composite health score and produces waiting-thread snapshots and continuation-type counts. It supports an index-backed fast path when a `HeapIndex` exists and falls back to per-segment `ClrHeap` scans.
- The analyzer caps async/task scanning via `MaxTasksToScan` and limits reporting width by `TopContinuationTypesToShow` and the waiting-thread selection logic used when building `HangDomainResult`.

## Goals for preset-driven flow
- Presets should control numeric caps (scan budgets, thresholds, Top-N) and behavioral choices (prefer index-only scan, aggressive task scanning, produce raw exports of continuations).
- Analyzer-level explicit options must remain overrideable by configuration.
- `Balanced` should match current defaults.

## Suggested new options (small additions)
- `bool PreferIndexOnly` — when true, prefer heap-index path and skip full heap enumeration if index missing (emit diagnostic).
- `bool ProduceRawExports` — when true, stream continuation/task snapshots to a gzipped NDJSON artifact and emit `ReportArtifact(FilePath)` for the output stage to include in `artifacts/`.
- `enum TaskScanMode { Conservative, Balanced, Aggressive }` — semantic alias mapping to `MaxTasksToScan` and sampling behavior.

These additions let presets affect flow (not just numbers) and are easy to test.

## How analyzer flow should respect presets
- If `PreferIndexOnly == true` and no heap index available => record a diagnostic and skip expensive heap enumeration.
- `TaskScanMode` maps to `MaxTasksToScan` and determines whether the analyzer samples continuations or attempts more exhaustive scanning.
- `ProduceRawExports == true` enables writing continuation/task artifacts and attaching them to the report for offline analysis.

## Algorithmic preset policy (logical behaviors)
Presets must change observable control flow (index-only vs fallback, conservative vs aggressive scanning). Prefer named enums/flags rather than implicit numeric-only changes so test suites can assert code-path selection.

Concrete preset mappings (recommended)

- Fast
	- `LongWaitThreshold = 8` (seconds)
	- `HighThreadPoolThreshold = 150`
	- `TaskScanMode = Conservative`
	- `MaxTasksToScan = 20_000`
	- `TopWaitingThreadsPerGroup = 3`
	- `TopContinuationTypesToShow = 3`
	- `PreferIndexOnly = true`
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `LongWaitThreshold = 5`
	- `HighThreadPoolThreshold = 100`
	- `TaskScanMode = Balanced`
	- `MaxTasksToScan = 50_000`
	- `TopWaitingThreadsPerGroup = 5`
	- `TopContinuationTypesToShow = 5`
	- `PreferIndexOnly = false`
	- `ProduceRawExports = false`

- Full
	- `LongWaitThreshold = 3`
	- `HighThreadPoolThreshold = 60`
	- `TaskScanMode = Aggressive`
	- `MaxTasksToScan = 150_000`
	- `TopWaitingThreadsPerGroup = 10`
	- `TopContinuationTypesToShow = 15`
	- `PreferIndexOnly = false`
	- `ProduceRawExports = true`

These mappings increase sensitivity and coverage from Fast → Balanced → Full.

## Minimal code changes (implementation plan)
1. Add `PreferIndexOnly`, `ProduceRawExports`, and `TaskScanMode` to `HangAnalysisOptions` and populate them in `Preset(AnalysisProfile)`.
2. Map `TaskScanMode` to `MaxTasksToScan` early in `AnalyzeForHang(...)` to keep downstream code simple.
3. Respect `PreferIndexOnly` when choosing the index-backed path in `AnalyzeAsyncWork(...)` / `RunParallelAsyncScan(...)` and emit a diagnostic when index is required but absent.
4. When `ProduceRawExports` is enabled, stream continuation/task snapshots to NDJSON gz artifacts and emit `ReportArtifact(FilePath)`.
5. Add unit tests asserting task-scan budget enforcement, health-score thresholds behavior, and `PreferIndexOnly` short-circuit behavior.

Implementation notes:
- `AnalyzeAsyncWork(...)` already checks `heapCache.TryGetHeapIndex(out var heapIdx)` and honors `MaxTasksToScan` — wire options around these checks rather than duplicating scanning logic.
- `HangSectionBuilder` currently uses `options.TopContinuationTypesToShow` when rendering; ensure the reporting uses the same option values rather than hard-coded constants.

## Tests and validation
- Unit tests: vary `HangAnalysisOptions.MaxTasksToScan`/`TaskScanMode` and assert `RunParallelAsyncScan` respects limits and flags `TaskScanLimited` when appropriate.
- Unit tests: assert `CreateFinding(...)` severity mapping for waiting-percent and `HighThreadPoolThreshold` boundaries.
- Integration: run `HangAnalyzer` on representative dumps using Fast/Balanced/Full presets and measure runtime and produced artifacts.

## Next steps I can take
- Implement the `HangAnalysisOptions` additions and update `HangAnalysisOptions.Preset(...)`.
- Replace numeric-only branching with `TaskScanMode` mapping and add unit tests.
- Add optional integration test verifying `ProduceRawExports` writes artifacts into the report `artifacts/` directory via `WriteOutputStage`.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.
