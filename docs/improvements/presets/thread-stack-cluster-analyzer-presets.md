# ThreadStackClusterAnalyzer — Presets

Status: ✅ COMPLETED — presets implemented, exports added, unit tests added.

Purpose: cluster similar managed stack traces (thread stack signatures) to identify hotspots and coordinated blocking patterns.

Files:
- [src/DumpDetective.Analysis/Analyzers/ThreadStackClusterAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/ThreadStackClusterAnalyzer.cs)
- [src/DumpDetective.Reporting/SectionBuilders/ThreadStackClusterSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/ThreadStackClusterSectionBuilder.cs)
- Preset design reference: [docs/improvements/string-preset-design.md](docs/improvements/string-preset-design.md)

## Current working (summary)
- Analyzer: [src/DumpDetective.Analysis/Analyzers/ThreadStackClusterAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/ThreadStackClusterAnalyzer.cs)
	- Scans `ClrRuntime.Threads`, builds a signature per alive `ClrThread` using `BuildSignature(...)`, and aggregates counts into in-memory `StackCluster` instances.
	- Maintains a bounded sample of thread addresses per cluster (`SampleThreadAddresses`) limited by `MaxThreadIdsPerCluster` from options.
	- Projects sample thread addresses to OS thread ids using an upfront `osThreadIdByAddress` map and `ProjectSampleOsThreadIds(...)` when creating `ThreadClusterSnapshot`s.

- Reporting: [src/DumpDetective.Reporting/SectionBuilders/ThreadStackClusterSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/ThreadStackClusterSectionBuilder.cs)
	- Renders summary metrics (alive threads, unique signatures, singleton count, diversity) and shows top signatures and clusters.
	- Truncates long signature text for display and formats sample OS thread ids in hex.


Options exposed by the analyzer (used by presets):
- `MaxFramesPerSignature` — frames captured per thread signature (affects signature granularity).
- `MaxThreadIdsPerCluster` — how many sample thread addresses/IDs to keep per cluster.
- `TopSignaturesToShow` — how many signature samples to render in the report UI.
- `TopClustersToShow` — how many clusters to snapshot and expose as artifacts.

Notes from implementation:
- The analyzer builds signatures from `ClrThread` stack frames; see `BuildSignature(...)` in the analyzer for exact trimming and ordering.
- The reporting layer reads `TopSignaturesToShow`/`TopClusters` and truncates long signatures when rendering; see the section builder for limits and formatting.

Built-in presets (mapped to analyzer options):

- **Fast** (low cost, quick triage)
	- `MaxFramesPerSignature = 4`
	- `MaxThreadIdsPerCluster = 5`
	- `TopSignaturesToShow = 3`
	- `TopClustersToShow = 8`
	- Rationale: prioritize speed on dumps with many threads; signatures are coarser and sampling is small.

- **Balanced** (default)
	- `MaxFramesPerSignature = 6`
	- `MaxThreadIdsPerCluster = 8`
	- `TopSignaturesToShow = 5`
	- `TopClustersToShow = 12`
	- Rationale: good trade-off between signal quality and analyzer cost; matches current defaults used in code.

- **Full** (deeper analysis)
	- `MaxFramesPerSignature = 10`
	- `MaxThreadIdsPerCluster = 20`
	- `TopSignaturesToShow = 10`
	- `TopClustersToShow = 20`
	- Rationale: capture more frames and larger samples to reveal subtle shared call-stacks and less-common hotspots; higher CPU/memory.

Rationale and guidance
- Use **Fast** for large dumps or quick CI checks where runtime matters.
- Use **Balanced** as the everyday default for interactive triage.
- Use **Full** when you need exhaustive clustering for post-mortem investigations and have time to run a heavier analysis.

Reporting & UX
- The section builder truncates signature text for display; large signatures are truncated in the report to keep readability. To inspect full signatures or larger samples, increase `TopSignaturesToShow`/`TopClustersToShow` or export cluster snapshots.

Minimal implementation notes
- Presets should map directly to `ThreadStackClusterAnalysisOptions` and be set via the existing configuration/preset factory.
- Keep preset boundaries behavioral (sampling sizes) — not only presentation numbers. For example, `MaxThreadIdsPerCluster` controls how many thread addresses the analyzer retains (affects downstream OS thread id projection in the analyzer's `ProjectSampleOsThreadIds(...)`).

Suggested tests
- Unit: assert preset values flow into `ThreadStackClusterAnalyzer.Analyze(...)` and that `ProjectSampleOsThreadIds(...)` returns expected counts when sample addresses exist.
- Integration: run analyzer on representative dump with each preset and capture runtime, memory, and top-cluster stability.

Implementation status
- Preset mappings: implemented in `ThreadStackClusterAnalysisOptions.Preset(AnalysisProfile)`.
- Analyzer wiring: `ProduceClusterExports`, `MinClusterSize`, `MaxClusters`, and sampling modes implemented; exports produce `thread-clusters.json` and `thread-clusters.ndjson.gz` when enabled.
- Reporting: `ThreadStackClusterSectionBuilder` surfaces export guidance and artifact links.
- Tests: unit tests added to validate preset mapping and artifact carriage.

Remaining work
- Integration test: verify artifact movement into the report `artifacts/` folder (can follow `WriteOutputStageTests` pattern).
- Open PR and request review.

If you'd like, I can add the integration test and open a PR branch now.
