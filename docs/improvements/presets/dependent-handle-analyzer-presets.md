# DependentHandleAnalyzer — Presets

Purpose: surface dependent-handle pairs and retention.

Options observed in code (`DependentHandleAnalysisOptions`):
- `TopCount` (int) — number of top source/target pairs to return.

Fast:
- `TopCount`: 8

Balanced (default):
- `TopCount`: 15

Full:
- `TopCount`: 40

Flow notes:
- The analyzer is lightweight; `TopCount` controls how many top pairs are included in the report.

Rationale — when to pick each preset:
- **Fast:** `TopCount=8` — quick summary for large heaps.
- **Balanced:** `TopCount=15` — default balance between coverage and output size.
- **Full:** `TopCount=40` — return more pairs for detailed retention analysis.

Next steps:
- No code changes needed; `DependentHandleAnalysisOptions` already implements `Preset(AnalysisProfile)`.
