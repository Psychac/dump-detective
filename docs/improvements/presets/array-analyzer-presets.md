# ArrayAnalyzer — Preset Design

Purpose: analyze array population (by element type/rank), identify large arrays (LOH), and sample arrays to detect sparse/wasteful patterns.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/ArrayAnalyzer.cs


Observed implementation details:
- Uses `TypeAggregates` to compute totals and to build a set of array MTs — zero heap scan for basic totals.
- Large-array analysis reads `LargeObjectIndex.bin` when available; falls back to sample addresses from `TypeAggregates` otherwise.
- Sparse sampling is bounded by `SparseSampleLimit`, `SparseSampleMinLength`, `SampleStride`, and `TopSparseLimit`.
- Options in code (`ArrayAnalysisOptions`): `TopTypeLimit`, `TopLargeLimit`, `TopSparseLimit`, `SparseSampleLimit`, `SparseSampleMinLength`, `SampleStride`.

Built-in presets (`ArrayAnalysisOptions.Preset`):
- Fast: `TopTypeLimit=10`, `TopLargeLimit=10`, `TopSparseLimit=5`, `SparseSampleLimit=200`, `SparseSampleMinLength=20_000`, `SampleStride=200`
- Balanced (default): `TopTypeLimit=20`, `TopLargeLimit=20`, `TopSparseLimit=10`, `SparseSampleLimit=500`, `SparseSampleMinLength=10_000`, `SampleStride=100`
- Full: `TopTypeLimit=50`, `TopLargeLimit=50`, `TopSparseLimit=20`, `SparseSampleLimit=1000`, `SparseSampleMinLength=5_000`, `SampleStride=50`


Minimal code changes recommended:
- No-op: `ArrayAnalysisOptions` already has `Preset(AnalysisProfile)`.
- Document the strong perf win when `LargeObjectIndex.bin` is present and prefer index-mode for Full profiles.

Tests and validation:
- Unit: synthesize `TypeAggregateIndexEntry` sets and assert top-type selection and sparse-candidate assembly.
- Integration: run with and without `LargeObjectIndex.bin` to verify fallback behavior and results parity for top entries.

Rationale — when to pick each preset:
- **Fast:** tighten `TopTypeLimit`/`TopLargeLimit`/`TopSparseLimit` and increase `SampleStride` to reduce I/O and sampling work; use when dump is huge or index files are missing.
- **Balanced:** (default) balanced sampling (`SparseSampleLimit=500`, `SampleStride=100`) provides reasonable coverage without excessive I/O on medium dumps.
- **Full:** aggressive sampling and smaller stride (`SparseSampleLimit=1000`, `SampleStride=50`) prioritizes detection of rare/wide sparse arrays and LOH arrays at higher I/O cost.

Next steps:
- I can add a short note recommending `LargeObjectIndex.bin` be present for Full runs to avoid heavy heap scanning.
