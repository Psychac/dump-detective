# FinalizableObjectAnalyzer — Presets

Purpose: inventory finalizable types and analyse the finalizer queue and retained graphs.

Options observed in code:
- `TopTypeLimit` (int) — how many finalizable types to include in the Top list.
- `QueueScanLimit` (int) — how many finalizer-queue instances to capture for analysis.
- `TopQueueEntries` (int) — how many top queue entries to run BFS/retainer estimate for.
- `MaxBfsNodes` / `MaxBfsDepth` (int) — budgets for bounded BFS used for retained-size estimates.

Fast:
- `TopTypeLimit`: 10
- `QueueScanLimit`: 200
- `TopQueueEntries`: 5
- `MaxBfsNodes`: 100
- `MaxBfsDepth`: 8

Balanced (default):
- `TopTypeLimit`: 20
- `QueueScanLimit`: 500
- `TopQueueEntries`: 10
- `MaxBfsNodes`: 200
- `MaxBfsDepth`: 10

Full:
- `TopTypeLimit`: 50
- `QueueScanLimit`: 2_000
- `TopQueueEntries`: 25
- `MaxBfsNodes`: 1_000
- `MaxBfsDepth`: 20

Flow notes:
- The analyzer prefers Phase‑1 `TypeAggregates` for the population pass; presets only affect budgets for queue probing and BFS depth.
- Keep `QueueScanLimit` modest for large heaps; increase in Full profile when detailed queue analysis is required.

Rationale — when to pick each preset:
- **Fast:** reduce `QueueScanLimit` and `MaxBfsNodes`/`MaxBfsDepth` to avoid expensive BFS traversals while still surfacing top finalizable types.
- **Balanced:** default trade-off capturing representative finalizer queue entries and running bounded BFS for the top queue entries.
- **Full:** raise `QueueScanLimit` and BFS budgets to exhaustively probe retainer graphs for forensic finalizer backlog analysis (higher I/O and CPU).

Next steps:
- Document that `MaxBfsNodes` and `MaxBfsDepth` directly affect `PathSearchCapped` behavior in reports and advise incrementing them only when necessary.
