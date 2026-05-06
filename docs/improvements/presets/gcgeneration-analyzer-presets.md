# GCGenerationAnalyzer — Presets

Purpose: report generation splits and LOH/Gen2 hotspots using Phase‑1 indices when available.

Options observed in code (`GCGenerationAnalysisOptions`):
- `LohThresholdBytes` (ulong) — size threshold for LOH classification when tuning sensitivity.
- `TopLohTypeLimit` (int) — number of LOH types to include in the Top LOH list.
- `TopGenProfileLimit` (int) — number of per-type generation profiles to emit.

Fast:
- `LohThresholdBytes`: 85_000
- `TopLohTypeLimit`: 8
- `TopGenProfileLimit`: 10

Balanced (default):
- `LohThresholdBytes`: 85_000
- `TopLohTypeLimit`: 15
- `TopGenProfileLimit`: 20

Full:
- `LohThresholdBytes`: 85_000
- `TopLohTypeLimit`: 30
- `TopGenProfileLimit`: 40

Flow notes:
- Analyzer prefers `TypeAggregates` for accurate per-MT gen counts; presets mainly control reporting breadth and LOH sensitivity.

Rationale — when to pick each preset:
- **Fast:** restrict `TopLohTypeLimit` and `TopGenProfileLimit` to keep output narrow and avoid expensive type-level profile expansions.
- **Balanced:** default caps that produce a reasonably comprehensive view of LOH and generation splits without excessive output.
- **Full:** increase top limits to show more LOH/gen-profile entries for deep investigations where coverage matters.

Next steps:
- Consider documenting expected output sizes (MB) for different `TopLohTypeLimit`/`TopGenProfileLimit` values to guide users on report verbosity.
