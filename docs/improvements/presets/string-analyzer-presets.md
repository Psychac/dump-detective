**String Analyzer — Presets**

- **Fast:**
  - `EnableDeduplication`: true
  - `DeduplicationMode`: `PreferPrebuiltOnly`
  - `SamplingMode`: `Aggressive`
  - `MaxUniqueStringTracking`: 50_000
  - `MaxStringsToDedup`: 10_000
  - `TopDuplicatesToShow`: 10
  - `PreviewMaxLength`: 64
  - `DetectInterning`: false
  - `ProduceRawExports`: false
  - `MinDuplicateCharLength`: 8

- **Balanced (default):**
  - `EnableDeduplication`: true
  - `DeduplicationMode`: `FallbackToHeapScan`
  - `SamplingMode`: `Moderate`
  - `MaxUniqueStringTracking`: 200_000
  - `MaxStringsToDedup`: 50_000
  - `TopDuplicatesToShow`: 20
  - `PreviewMaxLength`: 80
  - `DetectInterning`: true
  - `ProduceRawExports`: false
  - `MinDuplicateCharLength`: 4

- **Full:**
  - `EnableDeduplication`: true
  - `DeduplicationMode`: `FallbackToHeapScan`
  - `SamplingMode`: `Full`
  - `MaxUniqueStringTracking`: 500_000
  - `MaxStringsToDedup`: 200_000
  - `TopDuplicatesToShow`: 50
  - `PreviewMaxLength`: 120
  - `DetectInterning`: true
  - `ProduceRawExports`: true
  - `MinDuplicateCharLength`: 1

Other numeric defaults (e.g. `VeryLongStringThresholdBytes`, `LohThresholdBytes`) remain at their class defaults (85_000) unless overridden.

Source: `StringAnalysisOptions.Preset(AnalysisProfile)`.

# StringAnalyzer — Preset Design

Purpose: detect duplicate/large strings, interning and string-related memory pressure while balancing I/O and memory.

Where to look in the repo:
- Analyzer: `src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs`
- Section builder: `src/DumpDetective.Reporting/SectionBuilders/StringSectionBuilder.cs`

Observed implementation details:
- Prefers `heapIndex.StringDedupIndex` (zero additional dump I/O) when `IHeapAnalysisCache.TryGetHeapIndex` supplies a `HeapIndexBuildResult`.
- Falls back to: (a) index-backed enumeration via cache, or (b) bounded heap `EnumerateObjects()` scan limited by `MaxStringsToDedup` and `MaxUniqueStringTracking`.
- Computes scalar stats from `TypeAggregateIndexEntry` when available and always records LOH/very-long counts.
- FOH/interned string detection: `BuildFohSegments()` + segment-scoped object scans when `DetectInterning` is enabled.
- Exports: JSON/CSV + streaming NDJSON gz artifact written to a temp file and attached via `ReportArtifact` with a `FilePath` (moved into report by `WriteOutputStage`).

Key API/implementation notes to preserve in presets:
- `StringAnalyzer` exposes `ComputeEffectiveCaps(...)` that maps `StringSamplingMode` to numeric caps; presets should set both semantic modes and numeric bases.
- `DeduplicationMode` is already respected in the analyzer: `PreferPrebuiltOnly` (prebuilt-only), `FallbackToHeapScan` (prefer prebuilt else index scan else heap scan), `Disabled` (skip fingerprinting).

Suggested options (match code):
- `DeduplicationMode { Disabled, PreferPrebuiltOnly, FallbackToHeapScan }` (used directly in `StringAnalyzer`)
- `StringSamplingMode { Aggressive, Moderate, Full }` (affects `ComputeEffectiveCaps`)
- `DetectInterning` (bool), `ProduceRawExports` (bool), `MinDuplicateCharLength` (int)

Practical preset mappings (align with existing defaults in code):
Fast:
- Prefer prebuilt index only: `DeduplicationMode = PreferPrebuiltOnly`
- Conservative numeric caps: `MaxUniqueStringTracking = 50_000`, `MaxStringsToDedup = 10_000`
- `DetectInterning = false`, `ProduceRawExports = false`, `MinDuplicateCharLength = 8`

Balanced (matches current defaults used by analyzer):
- `DeduplicationMode = FallbackToHeapScan`, `MaxUniqueStringTracking = 200_000`, `MaxStringsToDedup = 50_000`, `DetectInterning = true`

Full:
- `DeduplicationMode = FallbackToHeapScan`, `MaxUniqueStringTracking = 500_000`, `MaxStringsToDedup = 200_000`, `ProduceRawExports = true`

Concrete code changes recommended:
- `StringAnalysisOptions`: add `DeduplicationMode` and `SamplingMode` (if not present) and ensure `Preset(AnalysisProfile)` sets them.
- `StringAnalyzer.Analyze`: already branches on `DeduplicationMode`; ensure tests cover `PreferPrebuiltOnly` vs `FallbackToHeapScan` and missing-index behavior.
- `StringSectionBuilder`: already renders distribution, dedup metadata and artifact links — ensure it displays `SamplingMode` and `DedupSource` fields produced by the analyzer.

Tests and validation:
- Unit: mock `HeapIndexBuildResult` and `IHeapAnalysisCache` to verify index-only, index-scan and heap-scan paths and that `ComputeEffectiveCaps` maps sampling modes correctly.
- Integration: run on a representative dump with `Fast`/`Balanced`/`Full` and compare `StringsSampled`, `DeduplicationSkipped`, runtime and memory.

Next steps I can take:
- Implement `StringAnalysisOptions.Preset(...)` values, add unit tests for `ComputeEffectiveCaps`, and update `StringSectionBuilder` tests to assert sampling metadata is shown.

Balanced:
- `DeduplicationMode`: FallbackToHeapScan
- `EnableDeduplication`: true
- `MaxUniqueStringTracking`: 200_000
- `MaxStringsToDedup`: 50_000
- `TopDuplicatesToShow`: 20
- `StringSamplingMode`: Moderate
- `DetectInterning`: true
- `ProduceRawExports`: false

Full:
- `DeduplicationMode`: FallbackToHeapScan
- `EnableDeduplication`: true
- `MaxUniqueStringTracking`: 500_000
- `MaxStringsToDedup`: 200_000
- `TopDuplicatesToShow`: 50
- `StringSamplingMode`: Full
- `DetectInterning`: true
- `ProduceRawExports`: true

Flow notes:
- Presets influence code-path selection (index-only vs heap scan vs disabled), sampling aggressiveness and whether to emit on-disk artifacts.
