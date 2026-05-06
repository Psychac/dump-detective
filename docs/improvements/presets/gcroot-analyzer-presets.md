

# GCRootAnalyzer — Preset Design

Purpose: unified GC-root intelligence and bounded BFS path tracing for top suspects.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs
- Options: src/DumpDetective.Core/Options/GCRootAnalysisOptions.cs

Observed implementation knobs (from `GCRootAnalysisOptions`):
- `TopSeverityLimit` (int) — number of top root findings to consider.
- `PathSearchTopN` (int) — how many top findings to run BFS path tracing for.
- `MaxBfsNodes` (int) — node-visit cap for BFS path tracing.
- `MaxBfsDepth` (int) — depth cap for BFS path tracing.

Built-in presets (`GCRootAnalysisOptions.Preset`):
- Fast: `TopSeverityLimit=10`, `PathSearchTopN=10`, `MaxBfsNodes=250`, `MaxBfsDepth=10`
- Balanced (default): `TopSeverityLimit=20`, `PathSearchTopN=25`, `MaxBfsNodes=500`, `MaxBfsDepth=20`
- Full: `TopSeverityLimit=40`, `PathSearchTopN=60`, `MaxBfsNodes=2000`, `MaxBfsDepth=30`


Notes and guidance:
- Analyzer is index-first: it reads `RootIndex.bin` (or `InMemoryRootCandidates`) produced during Phase 1 and does not enumerate all heap roots in Phase 2.
- Keep `Balanced` as the default for medium dumps; use `Fast` for quick triage and `Full` only when deep path tracing is acceptable.
- The analyzer sets `PathSearchCapped` when BFS caps are hit — increase `MaxBfsNodes`/`MaxBfsDepth` only when required and when index/heap size permits.

Rationale — when to pick each preset:
- **Fast:** favor smaller `PathSearchTopN` and BFS budgets to avoid expensive graph traversal on large heaps; useful when you need initial signals quickly.
- **Balanced:** default values provide a mix of depth and speed suitable for most investigative workflows.
- **Full:** increase `PathSearchTopN`, `MaxBfsNodes`, and `MaxBfsDepth` to get more exhaustive root-path traces for top severity findings.

Tests and validation:
- Unit: supply a `HeapIndexBuildResult` with synthetic roots and verify `TopSeverityLimit` and `PathSearchTopN` are enforced.
- Integration: run with and without `RootIndex.bin` to validate fallback behavior and `PathSearchCapped` semantics.

