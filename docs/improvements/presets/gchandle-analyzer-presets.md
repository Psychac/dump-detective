# GCHandleAnalyzer — Presets

Purpose: analyze `GCHandle` usage, classify handle kinds, and surface pinned-object retention.

Options observed in code (`GCHandleAnalysisOptions`):
- `TopTypeCount` (int) — number of top target types / handle kinds to include in the report.

Fast:
- `TopTypeCount`: 8

Balanced (default):
- `TopTypeCount`: 15

Full:
- `TopTypeCount`: 40

Flow notes:
- The analyzer is I/O/CPU-light; `TopTypeCount` controls report width rather than scan cost.

Rationale — when to pick each preset:
- **Fast:** `TopTypeCount=8` — succinct summary of handle categories for quick triage.
- **Balanced:** `TopTypeCount=15` — default balance between coverage and output size.
- **Full:** `TopTypeCount=40` — deeper listing useful for audits of pinned-object retention.

Next steps:
- No code changes required; `GCHandleAnalysisOptions` already implements `Preset(AnalysisProfile)`.
