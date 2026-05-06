# HangAnalyzer — Presets

Purpose: detect hangs, waiting-thread pressure, and thread-pool/task backlog.

Options observed in code (`HangAnalysisOptions`):
- `LongWaitThreshold` (int) — seconds threshold to classify a thread as long-waiting.
- `HighThreadPoolThreshold` (int) — queued work items threshold for thread-pool health scoring.
- `MaxTasksToScan` (int) — cap on task/continuation scanning across the heap/index.
- `TopWaitingThreadsPerGroup` (int) — how many waiting-thread snapshots to store per wait-category.
- `TopContinuationTypesToShow` (int) — how many continuation types to include in the summary.

Fast:
- `LongWaitThreshold`: 8
- `HighThreadPoolThreshold`: 150
- `MaxTasksToScan`: 20_000
- `TopWaitingThreadsPerGroup`: 3
- `TopContinuationTypesToShow`: 3

Balanced (default):
- `LongWaitThreshold`: 5
- `HighThreadPoolThreshold`: 100
- `MaxTasksToScan`: 50_000
- `TopWaitingThreadsPerGroup`: 5
- `TopContinuationTypesToShow`: 5

Full:
- `LongWaitThreshold`: 3
- `HighThreadPoolThreshold`: 60
- `MaxTasksToScan`: 150_000
- `TopWaitingThreadsPerGroup`: 10
- `TopContinuationTypesToShow`: 15

Flow notes:
- `MaxTasksToScan` bounds async work scanning cost; increase only for targeted investigations.
- Health scoring uses these thresholds to weight findings; tune `HighThreadPoolThreshold` for expected load profiles.

Rationale — when to pick each preset:
- **Fast:** higher `LongWaitThreshold` and `HighThreadPoolThreshold` reduce noisy short waits and decrease alarm sensitivity; lower `MaxTasksToScan` to minimize heap reads.
- **Balanced:** (default) tuned to typical throughput and wait profiles for medium-load services.
- **Full:** lower `LongWaitThreshold` and `HighThreadPoolThreshold` to increase sensitivity; raise `MaxTasksToScan` to explore more continuations and thread-pool candidates.

Next steps:
- Recommend documenting expected wall-time cost for `MaxTasksToScan` values on representative hardware to help users pick sensible caps.
