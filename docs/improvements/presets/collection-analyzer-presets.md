# CollectionAnalyzer — Presets

Purpose: detect wasteful collections, large containers and over-allocations.

Options observed in code:
- `WasteThresholdBytes` (ulong) — minimum wasted bytes to consider a collection "wasteful".
- `TopWastefulCollectionsToShow` (int) — how many top wasteful collections to return.
- `MaxDegreeOfParallelism` (int) — parallelism for scanning/index paths.
- `PathAnalysisTopN` (int) — how many top items to run reference-path analysis for.
- `SerializeHeapAccess` (bool) — serialize ClrHeap accesses when scanning in parallel.

Fast:
- `WasteThresholdBytes`: 10 * 1024 (10 KB)
- `TopWastefulCollectionsToShow`: 25
- `MaxDegreeOfParallelism`: Environment.ProcessorCount (default)
- `PathAnalysisTopN`: 0
- `SerializeHeapAccess`: false

Balanced (default):
- `WasteThresholdBytes`: 10 * 1024 (10 KB)
- `TopWastefulCollectionsToShow`: 50
- `MaxDegreeOfParallelism`: Environment.ProcessorCount
- `PathAnalysisTopN`: 5
- `SerializeHeapAccess`: false

Full/Deep:
- `WasteThresholdBytes`: 10 * 1024 (10 KB)
- `TopWastefulCollectionsToShow`: 100
- `MaxDegreeOfParallelism`: Environment.ProcessorCount
- `PathAnalysisTopN`: 15
- `SerializeHeapAccess`: false

Flow notes:
- Prefer index-backed analysis (in-memory or disk-backed HeapIndex) — preserves perf.
- `PathAnalysisTopN` controls targeted deep reference-chain searches; keep 0 for fastest runs.
- `MaxDegreeOfParallelism` can be reduced to 1 on constrained hosts to avoid CPU contention.

Rationale — when to pick each preset:
- **Fast:** set `PathAnalysisTopN=0` to skip reference-path work and limit I/O; use when doing wide triage on large dumps.
- **Balanced:** run `PathAnalysisTopN=5` for targeted path searches on top wasteful collections; good default for most investigations.
- **Full/Deep:** increase `PathAnalysisTopN` and `ReferenceChainOptions` budgets to explore more candidates deeply (costly but thorough).

Next steps:
- Consider documenting a recommended host configuration (CPU/IO) for `Full` runs to help users pick appropriate `MaxDegreeOfParallelism`.
