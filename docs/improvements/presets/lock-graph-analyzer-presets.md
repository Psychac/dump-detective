# LockGraphAnalyzer — Presets

Purpose: build lock acquisition/holding graph and detect deadlocks.

Options observed in code (`LockGraphAnalysisOptions`):
- `MaxContestedLocksToShow` (int) — number of top contested locks/details to include in the report.

Fast:
- `MaxContestedLocksToShow`: 8

Balanced (default):
- `MaxContestedLocksToShow`: 15

Full:
- `MaxContestedLocksToShow`: 40

Flow notes:
- The analyzer enumerates sync blocks; cost is proportional to number of sync blocks and threads. `MaxContestedLocksToShow` controls report size only.

Rationale — when to pick each preset:
- **Fast:** `MaxContestedLocksToShow=8` — small report focused on the loudest contested locks.
- **Balanced:** default gives a slightly larger view suitable for typical investigations (`MaxContestedLocksToShow=15`).
- **Full:** increase to `MaxContestedLocksToShow=40` to reveal a more comprehensive contention graph for small-to-medium services; be mindful of memory while building the graph.

Next steps:
- Consider exposing a `MaxGraphExpansionNodes` cap if graph-building memory usage becomes a concern.
