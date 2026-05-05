# String Analysis — Improvements (referencing StringAnalyzer.cs and StringSectionBuilder.cs)

This document lists prioritized, actionable improvements for string analysis (analysis engine + reporting/UX), referencing the current implementation in `StringAnalyzer.cs` and `StringSectionBuilder.cs`.

## Goals
- Improve accuracy and actionability of duplicate detection.
- Keep analysis memory- and time-bounded for very large dumps.
- Surface artifacts and distribution data clearly in reports and enable downstream automation.

---

## Analysis (engine) improvements
- Sampling & caps
  - Add deterministic hash-based sampling and configurable `SamplingMode` (Full / Aggressive / Moderate / Minimal). Use seed for reproducible samples.
  - Implement `ComputeEffectiveCaps` behaviour tests and expose in index metadata.

- Percentiles & histogram
  - Compute p50/p75/p90/p95 and bucket histograms during analysis and store in `StringDedupDistribution` (index metadata) for reuse.
  - Prefer index-provided distribution when present (already in code); add fallback deterministic estimators when only counts available.

- Memory-efficient streaming
  - Continue using streamed `foreach (heap.EnumerateObjects())`, `ArrayPool` and `Span<T>` for temporary buffers.
  - Avoid LINQ allocations in hot loops; micro-optimize `FingerprintAddress` for minimal allocations.

- Deduplication improvements
  - Use strong, fast hashing (xxHash64/Hardware accelerated) for content fingerprinting already represented by `StringFingerprint.Hash`.
  - Add normalization profiles (case-insensitive, whitespace collapse, JSON normalization) configurable per-run; include normalization mode in artifact metadata.
  - Reservoir sampling for representative snapshots and exact Top-K heaps for waste/count (already used) — ensure deterministic tie-breaking.

- Index integration
  - Expand `HeapIndex` metadata: include `StringDedupDistribution` (sampleCount, percentiles, length/frequency buckets), `StringDedupIndex` provenance (seed, build tool version).
  - When index available, avoid heap I/O for distribution + counts; only read sample snapshots when requested.

- Clustering / near-duplicate detection (future)
  - Add optional n-gram / token signature clustering to group parameterized messages (e.g., "User {0} logged in") and surface parameterized families.

- Metadata & provenance
  - Include `analysis_run_id`, `analyzer_version`, `sampling_seed`, `sampling_mode`, and `dedup_mode` in artifact `index.json` and NDJSON headers.

- Performance & safety
  - Add microbenchmarks for `FingerprintAddress` and sampling paths; enforce a runtime budget and gracefully degrade (report partial analysis state).

## Reporting & artifacts
- NDJSON exports
  - Ensure exported NDJSON contains minimal fields: `address`, `methodTable`, `length`, `hash`, `preview`, `sampleTag`, `samplingSource`.
  - Add gz compress support (already present) and clear `ContentType` + `FileName` in `ReportArtifact`.

- Enriched snapshots
  - Export sampled shallow-object-graphs per duplicate (addresses + immediate refs + type names) so offline root-path analysis can be run against artifact.

- Actionable summaries
  - For each top duplicate, compute quick actionable hints: "Likely static cache", "Event handler retention", or "string.Concat in hot path" (heuristic based on dominant types and stacks).

- Report index
  - `artifacts/index.json` must include per-artifact metadata and a top-level summary that `ReportSerializer` and UI can consume.

## UX / UI improvements
- Artifact visibility
  - Add clear badge in analyzer header: `Artifacts: N files — open/download` (link to `artifacts/<dump-base-name>/index.json`).
  - When artifacts exported, include the short note in analyzer section (already implemented) and a clickable example preview.

- Distribution visualization
  - Interactive histogram with percentile markers; clicking a bucket shows sample records (from NDJSON) and top-K for that bucket.

- Drill-down flow
  - From summary → bucket → top duplicate → snapshot → root-path viewer. Keep previews paginated to avoid loading large files into the browser.

- Filters & search
  - Filters by `length`, `count`, `wasted bytes`, `dominant type`, `generation` and normalization mode. Full-text search across sample previews with highlighting.

- Export & integration
  - Add `Export selected` and `Open artifact` actions. Provide a CLI hint in the UI for downloading and analyzing artifacts offline.

- Explainability & remediation
  - Each finding should include a `Confidence` and a short `Remediation Tip` (one-liner) to surface next steps.

## Testing & QA
- Golden artifacts
  - Add small NDJSON golden files (representative samples) and unit tests that ensure: artifact created, `index.json` contains provenance, and `ReportSerializer` inserts the artifact note.

- Collision & fuzz tests
  - Add randomized strings and assert fingerprint collision rate below threshold; include targeted tests for normalization modes.

- End-to-end regression
  - Test analyzer + WriteOutputStage + ReportSerializer + formatter path using small synthetic dumps to validate artifact wiring and UI preview payloads.

---

## Implementation notes (refs)
- Engine entry: `src/DumpDetective.Analysis/Analyzers/StringAnalyzer.cs` — central places: sampling, dedup branching, `FingerprintAddress`, distribution computation.
- Report builder: `src/DumpDetective.Reporting/SectionBuilders/StringSectionBuilder.cs` — render percentiles, buckets, top-K and examples; update to show artifact links/badges from `AnalyzerDetailSection.Artifacts` (future change).
- Artifact metadata: `src/DumpDetective.Core/Models/ReportArtifact.cs` and `WriteOutputStage` behaviour for `artifacts/<dump-base-name>/index.json`.

---

If you want, I can open a small PR implementing the histogram UI prototype in the canonical HTML formatter and wire artifact-badges into `StringSectionBuilder.cs`.
