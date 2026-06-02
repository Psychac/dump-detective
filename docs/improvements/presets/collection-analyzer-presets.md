# CollectionAnalyzer — Presets

Purpose: detect wasteful collections, large containers and over-allocations. This document makes preset-driven behavior explicit (numeric caps and flow decisions).

**Where to look**: - [DumpDetective/Analyzers/CollectionAnalyzer.cs](DumpDetective/Analyzers/CollectionAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/CollectionSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/CollectionSectionBuilder.cs)

## Current working (summary)
- Options observed in code: `WasteThresholdBytes` (ulong), `TopWastefulCollectionsToShow` (int), `MaxDegreeOfParallelism` (int), `PathAnalysisTopN` (int), `SerializeHeapAccess` (bool), plus likely `ReferenceChainOptions` budgets.
- `CollectionAnalyzer` prefers index-backed analysis when a `HeapIndex` is available (`HeapAnalysisCache.TryGetHeapIndex`). Index path builds an in-memory `HeapEntry[]` and runs the parallel accumulation logic; fallback enumerates per-segment objects.
- Deep reference-path work is gated by `PathAnalysisTopN` (0 to skip), and `SerializeHeapAccess` exists to safely serialize ClrHeap operations when running aggressive parallelism on constrained hosts.

## Goals for preset-driven flow
- Presets should control both numeric caps (TopN, thresholds, parallelism) and behavioral choices (skip deep path analysis, prefer index-only fast path, serialize heap access).
- Analyzer-level options must remain overrideable by explicit configuration.
- Balanced should match current defaults.

## Suggested new options (small additions)
- `enum PathAnalysisMode { Disabled, TopN, Full }` — semantic control for reference-path analysis. Maps to `PathAnalysisTopN` when `TopN`.
- `bool PreferIndexOnly` — when true, avoid fallbacks that enumerate the heap and rely on index data when present.
- `ReferenceChainOptions` — tune budgets when `PathAnalysisMode != Disabled` (optional grouping; likely exists already in codebase).

These additions make intent explicit and easier to test.

## How analyzer flow should respect presets
- If `PathAnalysisMode == Disabled` => set `PathAnalysisTopN = 0` and skip reference-chain searches entirely (only collect waste metrics and Top lists).
- If `PreferIndexOnly == true` and no index is available => record a diagnostic and skip expensive heap enumeration.
- `MaxDegreeOfParallelism` should be reduced automatically on resource-constrained hosts if the preset is `Fast` or when `SerializeHeapAccess` is true.

## Algorithmic preset policy (logical behaviors)
Presets change observable code paths (not just numbers). Prefer named enums/flags so tests can assert code-path selection (index-only vs fallback vs deep path analysis).

Concrete preset mappings (recommended)

- Fast
	- `PathAnalysisMode = Disabled`
	- `WasteThresholdBytes = 10 * 1024` (10 KB)
	- `TopWastefulCollectionsToShow = 25`
	- `MaxDegreeOfParallelism = Environment.ProcessorCount` (or fewer on low-memory hosts)
	- `PathAnalysisTopN = 0`
	- `SerializeHeapAccess = false`
	- `PreferIndexOnly = true`

- Balanced (baseline / existing defaults)
	- `PathAnalysisMode = TopN`
	- `WasteThresholdBytes = 10 * 1024` (10 KB)
	- `TopWastefulCollectionsToShow = 50`
	- `MaxDegreeOfParallelism = Environment.ProcessorCount`
	- `PathAnalysisTopN = 5`
	- `SerializeHeapAccess = false`
	- `PreferIndexOnly = false`

- Full / Deep
	- `PathAnalysisMode = Full` (or `TopN` with large `PathAnalysisTopN`)
	- `WasteThresholdBytes = 10 * 1024` (10 KB)
	- `TopWastefulCollectionsToShow = 100`
	- `MaxDegreeOfParallelism = Environment.ProcessorCount`
	- `PathAnalysisTopN = 15` (or larger for exhaustive runs)
	- `SerializeHeapAccess = false` (only enable if required for safety)
	- `PreferIndexOnly = false`

These mappings escalate coverage and cost from Fast → Balanced → Full.

## Minimal code changes (implementation plan)
1. Add `PathAnalysisMode` enum and `PreferIndexOnly` to `CollectionAnalysisOptions` and set them in `Preset(...)` for each profile.
2. Respect `PreferIndexOnly` in `AnalyzeCollections(...)` by short-circuiting to return `CollectionStatistics` without running the heap enumeration when index is missing (and optionally emit a diagnostic).
3. Map `PathAnalysisMode` to `PathAnalysisTopN` inside the analyzer's entry flow to simplify downstream checks.
4. Ensure `SerializeHeapAccess` is used where the analyzer locks or serializes access to `ClrHeap` in parallel code paths.
5. Add unit tests covering: index-backed vs fallback path selection, `PathAnalysisMode` effects (Disabled vs TopN), and `TopWastefulCollectionsToShow` truncation.

Implementation notes:
- `AnalyzeCollections(...)` already branches on `heapCache.TryGetHeapIndex(out var heapIdx)` — wire `PreferIndexOnly` checks around this existing logic rather than duplicating checks.
- `RunParallelCollectionAnalysis(...)` contains the heavy parallel work and already reads `_options.WasteThresholdBytes`; mapping `PathAnalysisMode` early avoids scattering checks.

## Tests and validation
- Unit tests: mock `HeapAnalysisCache.TryGetHeapIndex(...)` to assert code-path selection for `PreferIndexOnly` and for `PathAnalysisMode` variants.
- Unit tests: assert top-N truncation behavior in `CollectionSectionBuilder` when `TopWastefulCollectionsToShow` varies.
- Integration: run the analyzer on medium dumps with `Fast`, `Balanced`, and `Full` presets and verify runtime, memory and that `PathAnalysis` work is skipped/present as expected.

## Next steps I can take
- Implement the `CollectionAnalysisOptions` additions and update `Preset(...)` mappings.
- Add unit tests that mock the heap index and verify the three `PathAnalysisMode` behaviors.
- Optionally update `CollectionSectionBuilder` to use `TopWastefulCollectionsToShow` instead of the hardcoded `Top 15` title for the rendered block.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.
