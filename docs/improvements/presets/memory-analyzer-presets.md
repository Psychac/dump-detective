**Memory Analyzer — Presets**

- **Fast:** `LohThresholdBytes`: 85_000; `TopBySizeCount`: 10; `TopByCountCount`: 10
- **Balanced (default):** `LohThresholdBytes`: 85_000; `TopBySizeCount`: 20; `TopByCountCount`: 20
- **Full:** `LohThresholdBytes`: 85_000; `TopBySizeCount`: 50; `TopByCountCount`: 50

Notes: values taken from `MemoryAnalysisOptions.Preset(AnalysisProfile)`.
# MemoryAnalyzer — Preset Design

Purpose: synthesize per-type memory statistics and an object-size histogram using Phase‑1 type statistics when available.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/MemoryAnalyzer.cs
- Section builder: src/DumpDetective.Reporting/SectionBuilders/MemorySectionBuilder.cs

Observed implementation details:
- `MemoryAnalyzer` uses `IHeapAnalysisCache.GetOrBuildTypeStatistics(heap)` to obtain `CachedTypeStatistics` — zero extra heap scans when Phase‑1 index provides `GlobalSizeBuckets`.
- `MemoryAnalysisOptions` contains `TopBySizeCount`, `TopByCountCount` and `LohThresholdBytes` which appear in the domain result and reporting.
- Size-bucket histogram is derived from `HeapIndexBuildResult.GlobalSizeBuckets` if present; otherwise histogram is omitted.

Preset knobs to expose:
- `TopBySizeCount` (int) — top N types by total bytes.
- `TopByCountCount` (int) — top N types by instance count.
- `LohThresholdBytes` (ulong) — the threshold above which an object is considered LOH for summary reporting.

Recommended presets (code-backed):
- **Fast:** `TopBySizeCount=10`, `TopByCountCount=10` — compact lists for quick triage.
- **Balanced:** `TopBySizeCount=20`, `TopByCountCount=20` — default, balanced coverage.
- **Full:** `TopBySizeCount=50`, `TopByCountCount=50` — broader lists for deep analysis (more memory/IO).

Minimal code changes:
- Add `MemoryAnalysisOptions.Preset(AnalysisProfile)` mapping the three knobs above.
- Encourage analyzer to prefer `GlobalSizeBuckets` when available to avoid extra heap passes (already implemented). Document this in the preset file so users know enabling Phase‑1 index speeds Full runs.

Tests and validation:
- Unit: mock `CachedTypeStatistics` map to validate top lists and LOH percentage calculations.
- Integration: confirm histogram appears when heap index present and disappears otherwise.

Next steps:
- I can implement the `Preset(...)` factory and a unit test that toggles presence/absence of `GlobalSizeBuckets` if you want me to apply code changes.
