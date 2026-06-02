# GCHandleAnalyzer — Preset Design

Purpose: define how `GCHandleAnalyzer` presets (Fast / Balanced / Full)
should control reporting breadth and optional artifact emission, following the
`string-preset-design.md` template.

## Current working (summary)
- `GCHandleAnalyzer` enumerates runtime handles (lightweight) and aggregates
	counts by handle kind, top target types and pinned-object summaries. It is
	low CPU/I/O compared to other analyzers.
- Primary option observed: `TopTypeCount` controls the number of rows shown
	for top kinds / target types / pinned types.

## Goals for preset-driven flow
- Presets should determine report width (`TopTypeCount`) and optionally
	whether the analyzer emits raw exports for offline inspection.
- Keep behaviors explicit and overridable via section configuration.

## Suggested new options (extend `GCHandleAnalysisOptions` if desired)
- `int TopTypeCount` — how many top kinds/target types to include (existing).
- `bool ProduceRawExports` — when true, stream an on-disk NDJSON/GZ artifact
	containing handle snapshots for offline analysis.
- `bool PreferIndexOnly` — optional: when true, prefer using a shared handle
	snapshot provider (heap index or cached snapshot) and avoid live
	enumeration if the snapshot provider is unavailable.

## How analyzer flow should respect presets
- Use `TopTypeCount` directly for the counts and table truncation in the
	`GCHandleSectionBuilder`.
- When `ProduceRawExports` is true, stream handle entries to a gzipped NDJSON
	file and attach `ReportArtifact(FilePath)` to the domain result; keep
	the memory usage streaming and bounded.
- `PreferIndexOnly` (if implemented) should cause the analyzer to emit a
	diagnostic and skip live enumeration when no snapshot provider is present.

## Concrete preset mappings (recommended)

- Fast
	- `TopTypeCount = 8`
	- `ProduceRawExports = false`
	- `PreferIndexOnly = true`

- Balanced (baseline / existing defaults)
	- `TopTypeCount = 15`
	- `ProduceRawExports = false`
	- `PreferIndexOnly = false`

- Full
	- `TopTypeCount = 40`
	- `ProduceRawExports = true`
	- `PreferIndexOnly = false`

## Minimal code changes (implementation notes)
- No immediate code changes required — `GCHandleAnalysisOptions` already
	implements `Preset(AnalysisProfile)` and the analyzer honors
	`TopTypeCount`.
- Optional small changes:
	1. Add `ProduceRawExports` and `PreferIndexOnly` to
		 `GCHandleAnalysisOptions` and set sensible preset defaults.
	2. If `ProduceRawExports` enabled, stream handle snapshots to an on-disk
		 NDJSON/GZ file and emit `ReportArtifact(FilePath)`.

## Tests and validation
- Unit tests: assert `TopTypeCount` truncation in the `GCHandleDomainResult`
	and that `GCHandleSectionBuilder` renders the expected number of rows.
- Integration: when `ProduceRawExports` is true, verify the artifact file is
	produced and moved into the report `artifacts/` directory by
	`WriteOutputStage`.

## Rationale
- Keep the analyzer fast by default. Small named flags (like
	`ProduceRawExports`) make it easy to opt into larger, disk-backed exports
	for forensic workflows.

## Next steps I can take
- Implement `ProduceRawExports` and `PreferIndexOnly` in
	`GCHandleAnalysisOptions` and update the `Preset` factory.
- Add a small integration test that validates artifact generation and
	movement into `artifacts/` when `ProduceRawExports` is enabled.

Which next step do you want me to take? I can implement the options change and
update the `Preset` factory first.
