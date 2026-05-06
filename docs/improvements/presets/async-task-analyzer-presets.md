# AsyncTaskAnalyzer — Preset Design

Purpose: classify Task objects (Pending/Running/Faulted/Completed), detect orphaned tasks, and surface continuation-chain depth.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/AsyncTaskAnalyzer.cs


Observed implementation details:
- Prefers `TaskIndex.bin` / `HeapIndex.InMemoryTaskCandidates` for a zero/low-cost load path; falls back to TypeAggregates or full heap scan.
- Key runtime knobs in code (see `AsyncTaskAnalysisOptions`): `MaxTasksToScan`, `MaxContinuationDepth`, `TopTypesToShow`, `TopOrphanedToShow`.
- StateFlags may be zeroed in Phase 1 and are resolved by reading `m_stateFlags` from a task instance in Phase 2.

Preset knobs to expose (names match `AsyncTaskAnalysisOptions`):
- `MaxTasksToScan` (int)
- `MaxContinuationDepth` (int)
- `TopTypesToShow` (int)
- `TopOrphanedToShow` (int)

Built-in presets (`AsyncTaskAnalysisOptions.Preset`):
- Fast: `MaxTasksToScan=20_000`, `MaxContinuationDepth=10`, `TopTypesToShow=8`, `TopOrphanedToShow=10`
- Balanced (default): `MaxTasksToScan=50_000`, `MaxContinuationDepth=20`, `TopTypesToShow=10`, `TopOrphanedToShow=20`
- Full: `MaxTasksToScan=100_000`, `MaxContinuationDepth=40`, `TopTypesToShow=20`, `TopOrphanedToShow=40`


Minimal code changes recommended:
- No-op: `AsyncTaskAnalysisOptions` already implements `Preset(AnalysisProfile)` and `Default` — ensure docs show these values and the CLI wiring uses `AnalyzerOptionsBuilder.BuildBalancedPresetFromCli` (already present).
- Ensure the analyzer logs (progress) when falling back from `TaskIndex.bin` to a heap scan so users understand runtime cost.

Tests and validation:
- Unit: simulate `TaskIndex.bin` and `InMemoryTaskCandidates` paths to verify caps are respected.
- Integration: run on a medium dump with many tasks to validate `TaskScanLimited` flag and performance implications.

Rationale — when to pick each preset:
- **Fast:** reduce `MaxTasksToScan` and `MaxContinuationDepth` to limit the number of `heap.GetObject()` calls and deep continuation traversal; use for large deployments with many tasks where you only need a quick signal.
- **Balanced:** (default) balanced caps (50k tasks, depth 20) that usually capture most meaningful task activity without long-running deep traversals.
- **Full:** raise `MaxTasksToScan` and `MaxContinuationDepth` to explore larger task graphs and deeper continuation chains when investigating complex async retention or starvation issues.

Next steps:
- I can implement the `Preset(...)` factory for `AsyncTaskAnalysisOptions` and add a small unit test harness — tell me if you'd like that change applied now.
