# FinalizableObjectAnalyzer — Preset Design

Purpose: describe how `FinalizableObjectAnalyzer` presets (Fast / Balanced / Full)
should drive numeric budgets and behavior (not just TopN values), using the
`string-preset-design.md` template as canonical guidance.

## Current working (summary)
- The analyzer uses Phase‑1 `TypeAggregates` (when present) to build the
	population of finalizable types. When Phase‑1 index is missing it falls back
	to a heap scan to discover finalizable objects and to enumerate the
	finalizer queue via `heap.EnumerateFinalizableObjects()` (costly).
- Config knobs currently observed: `TopTypeLimit`, `QueueScanLimit`,
	`TopQueueEntries`, `MaxBfsNodes`, `MaxBfsDepth`.

## Goals for preset-driven flow
- Presets must control both numeric budgets (Top/queue/BFS limits) and
	behavioral choices (whether to refuse expensive fallbacks, whether to
	emit raw artifacts for offline analysis).
- Analyzer-specific flags should make preset behavior explicit and testable.
- Preserve backward compatibility: `Balanced` should match existing defaults.

## Suggested new options (add to `FinalizableObjectAnalysisOptions`)
- `bool PreferPhase1Only` — if true, refuse fallback heap-scans when Phase‑1
	`TypeAggregates` are not available; record a diagnostic instead.
- `bool ProduceRawExports` — when true, write on-disk artifacts (NDJSON/GZ)
	for top queue entries and retainer estimates; `WriteOutputStage` moves
	artifacts into the report `artifacts/` folder.
- `int TopTypeLimit` — how many finalizable types to include in Top list.
- `int QueueScanLimit` — cap for enumerating finalizer-queue instances.
- `int TopQueueEntries` — how many top queue entries to run BFS on.
- `int MaxBfsNodes`, `int MaxBfsDepth` — bounded BFS budgets for retained
	size estimates.

## How analyzer flow should respect presets
- Resolve `PreferPhase1Only` early. If true and Phase‑1 index is missing,
	emit a diagnostic and skip the expensive fallback heap-scan / queue analysis.
- When `ProduceRawExports` is true, stream NDJSON gz artifacts for the
	`TopQueueEntries` processed and attach `ReportArtifact(FilePath)` to the
	domain result.
- Use numeric caps from options for queue sampling and bounded BFS. Keep the
	scanning loop streaming and memory-bounded (ArrayPool / small buffers).

## Concrete preset mappings (recommended)

- Fast
	- `PreferPhase1Only = true`
	- `ProduceRawExports = false`
	- `TopTypeLimit = 10`
	- `QueueScanLimit = 200`
	- `TopQueueEntries = 5`
	- `MaxBfsNodes = 100`
	- `MaxBfsDepth = 8`

- Balanced (baseline / existing defaults)
	- `PreferPhase1Only = false`
	- `ProduceRawExports = false`
	- `TopTypeLimit = 20`
	- `QueueScanLimit = 500`
	- `TopQueueEntries = 10`
	- `MaxBfsNodes = 200`
	- `MaxBfsDepth = 10`

- Full
	- `PreferPhase1Only = false`
	- `ProduceRawExports = true`
	- `TopTypeLimit = 50`
	- `QueueScanLimit = 2000`
	- `TopQueueEntries = 25`
	- `MaxBfsNodes = 1000`
	- `MaxBfsDepth = 20`

## Minimal code changes (implementation plan)
1. Add `PreferPhase1Only` and `ProduceRawExports` to
	 `FinalizableObjectAnalysisOptions` and update `Preset(AnalysisProfile)` to
	 set Fast/Balanced/Full defaults.
2. In `FinalizableObjectAnalyzer.Analyze`:
	 - Resolve Phase‑1 `TypeAggregates` as currently done. If none and
		 `PreferPhase1Only` is true, record a diagnostic and skip the expensive
		 fallback heap-scan / queue analysis.
	 - Respect `QueueScanLimit` when collecting queue samples and only run
		 `BfsEstimateRetained` for `TopQueueEntries`.
	 - When `ProduceRawExports` is true, stream a gzipped NDJSON file for the
		 `topEntries` and attach `ReportArtifact(FilePath)` to the result.
3. Update `WriteOutputStage` behavior already added for other analyzers to
	 prefer moving analyzer-produced on-disk artifacts into report `artifacts/`.

## Tests and validation
- Unit tests: mock `HeapIndexBuildResult` (Phase‑1 index present/absent) and
	verify code-paths for `PreferPhase1Only` true/false; assert `QueueScanLimit`
	and `TopQueueEntries` are honored and that BFS budgets cap traversal.
- Integration tests: run a medium-sized dump with `Fast`/`Balanced`/`Full`
	profiles and assert runtime/memory differences and that `ProduceRawExports`
	creates artifacts moved into `artifacts/`.

## Rationale
- Making the fallback decision explicit (`PreferPhase1Only`) prevents hidden
	expensive work under `Fast` profiles and makes preset behavior observable
	and testable. `ProduceRawExports` enables offline forensic workflows.

## Next steps I can take
- Implement the `FinalizableObjectAnalysisOptions` additions and update the
	`Preset` factory.
- Wire `FinalizableObjectAnalyzer` to respect the new options and add unit
	and integration tests.

Which next step would you like me to take? I can implement the options and
update the `Preset` factory first.
