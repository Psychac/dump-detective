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

---

## Presets — beyond numeric knobs

Presets are more powerful when they control analyzer *behavior* in addition to numeric limits. The analyzer already exposes sampling/output knobs (e.g. `MaxFramesPerSignature`, `MaxThreadIdsPerCluster`, `TopSignaturesToShow`, `TopClustersToShow`) and export toggles (`ProduceClusterExports`, `MinClusterSize`, `MaxClusters`). Consider mapping presets to the following behavioral switches to make profiles meaningful across dump sizes and use-cases:

- Export policy: enable/disable pretty JSON vs NDJSON+gzip exports and limit export size (Fast: disabled or minimal; Balanced: enabled but trimmed; Full: enabled with full detail).
- Sampling strategy: deterministic vs random sampling, and adaptive sampling based on `aliveThreads` (e.g. sample ratio instead of fixed cap). Deterministic sampling (fixed seed) improves reproducible results for Full runs.
- Signature granularity: coarse vs fine fingerprinting (coarse = fewer frames, normalized method names; fine = more frames, include generic/type args). Fast uses coarse; Full uses fine.
- OS-id projection: project sample thread addresses to OS thread ids only when needed (costly map build). Fast: skip projection; Balanced: project for top clusters; Full: always project.
- Cluster merging threshold: tune when similar signatures are merged (higher threshold for Fast to reduce clusters; lower for Full to expose subtle differences).
- Artifact retention: control whether temporary files stay attached (Full) or are emitted only as inline pretty JSON (Balanced) or not produced (Fast).
- Profiling signals: enable additional diversity metrics or stack-frame histograms only for Balanced/Full.

### Concrete example mappings

- Fast
	- `MaxFramesPerSignature = 4`
	- `MaxThreadIdsPerCluster = 5`
	- `TopSignaturesToShow = 3`, `TopClustersToShow = 8`
	- `ProduceClusterExports = false` (no NDJSON/gz exports)
	- `OsIdProjection = false` (skip building `osThreadIdByAddress` when large)
	- `SamplingMode = "ratio"` with `SamplingRatio = 0.01` (sample 1% when thread count is huge)

- Balanced
	- `MaxFramesPerSignature = 6`
	- `MaxThreadIdsPerCluster = 8`
	- `TopSignaturesToShow = 5`, `TopClustersToShow = 12`
	- `ProduceClusterExports = true` (trim exports to top `MaxClusters`)
	- `OsIdProjection = true` for filtered clusters only (build once, project for exported clusters)
	- `SamplingMode = "cap"` with `MaxSamplePerCluster = 8`

- Full
	- `MaxFramesPerSignature = 10`
	- `MaxThreadIdsPerCluster = 20`
	- `TopSignaturesToShow = 10`, `TopClustersToShow = 20`
	- `ProduceClusterExports = true` (full NDJSON+gzip and pretty JSON)
	- `OsIdProjection = true` (always project)
	- `SamplingMode = "deterministic"` with a `SampleSeed` (reproducible snapshots)
	- `ClusterMergeThreshold = low` (preserve subtle differences)

### Suggested small API additions (optional)

To enable the behavior mappings above without large refactors, consider adding the following options to `ThreadStackClusterAnalysisOptions`:
- `bool OsIdProjection` — whether to build the address→OS-id map and project samples.
- `enum SamplingMode { Cap, Ratio, Deterministic }` plus `double SamplingRatio` and `int SampleSeed`.
- `bool ProduceNdjsonGzip` — separate toggle if users want only pretty JSON in reports.
- `int ClusterMergeThreshold` (or a descriptive enum) — control merging sensitivity.

These are lightweight and can be honored around existing hotspots in `ThreadStackClusterAnalyzer.Analyze(...)` (where `osThreadIdByAddress` is built and where `SampleThreadAddresses` are chosen).

### Implementation notes (where to change)
- See `ThreadStackClusterAnalyzer.Analyze(...)` for where to:
  - avoid building `osThreadIdByAddress` when `OsIdProjection` is false;
  - choose sampling mode when adding to `cluster.SampleThreadAddresses` (apply ratio/cap/deterministic logic);
  - include `SampleSeed` when choosing deterministic sample indices.
- See `ThreadStackClusterSectionBuilder.cs` for export and UX wording; the builder can also show which sampling mode and whether OS-id projection was used.

### Tests and validation
- Unit tests: assert option mappings flow into `Analyze(...)` code paths (e.g., `OsIdProjection=false` should not build the map; `SamplingMode=Deterministic` should produce repeatable snapshots given the same seed).
- Integration: run analyzer on representative traces with each preset and verify:
  - runtime and memory differ as expected;
  - exported artifact sizes reflect preset policies;
  - deterministic sampling yields identical exports across runs with same seed.

### UX guidance
- Document preset differences in the README index and surface key behavioral differences in the report (e.g., "Export: NDJSON+gzip — Full only").
- Recommend `Balanced` for interactive triage; `Full` for forensic audits when reproducibility and exports are desired.

---

Implementation status
- Preset mappings: implemented in `ThreadStackClusterAnalysisOptions.Preset(AnalysisProfile)`.
- Analyzer wiring: `ProduceClusterExports`, `MinClusterSize`, `MaxClusters`, and sampling modes implemented; exports produce `thread-clusters.json` and `thread-clusters.ndjson.gz` when enabled.
- Reporting: `ThreadStackClusterSectionBuilder` surfaces export guidance and artifact links.
- Tests: unit tests added to validate preset mapping and artifact carriage.

Remaining work
- Integration test: verify artifact movement into the report `artifacts/` folder (can follow `WriteOutputStageTests` pattern).
- Open PR and request review.

If you'd like, I can add the integration test and open a PR branch now.
