# GCGenerationAnalyzer — Preset Design

Purpose: define how `GCGenerationAnalyzer` presets (Fast / Balanced / Full)
should control both numeric reporting budgets and optional behavioral choices
using the `string-preset-design.md` template as canonical guidance.

## Current working (summary)
- `GCGenerationAnalyzer` prefers Phase‑1 `TypeAggregates` when available and
	computes LOH and per-generation profiles from that index.
- When the heap index is missing the analyzer falls back to cached type
	statistics delivered by `GetOrBuildTypeStatistics` (less precise for per-MT
	bytes but still useful).
- Config knobs in `GCGenerationAnalysisOptions`: `LohThresholdBytes`,
	`TopLohTypeLimit`, `TopGenProfileLimit`.

## Goals for preset-driven flow
- Presets should set both reporting breadth (Top limits) and any behavior that
	affects cost (whether to attempt expensive per-type profile expansion when
	index is missing).
- Preset-driven behavior should be explicit and overridable by section
	configuration.

## Suggested new options (add to `GCGenerationAnalysisOptions`)
- `bool PreferIndexOnly` — when true, avoid fallback code-paths that attempt
	expensive per-type expansions or heap rescans when a Phase‑1 index is
	missing; emit a diagnostic instead.
- `ulong LohThresholdBytes` — LOH sensitivity threshold (existing).
- `int TopLohTypeLimit` — how many LOH types to report (existing).
- `int TopGenProfileLimit` — how many per-type generation profiles to emit
	(existing).
- `bool ProduceRawExports` — optional: emit CSV/NDJSON artifacts for top LOH
	types for offline analysis.

## How analyzer flow should respect presets
- If `PreferIndexOnly` is true and `TypeAggregates` are not present, skip the
	expansion/fallback that attempts to build per-type gen-profiles and record
	a diagnostic explaining the skipped work.
- Use `TopLohTypeLimit` and `TopGenProfileLimit` directly when selecting the
	number of rows emitted by the `GCGenerationSectionBuilder`.
- When `ProduceRawExports` is true, stream an on-disk artifact for the top LOH
	types and attach `ReportArtifact(FilePath)` to the domain result.

## Concrete preset mappings (recommended)

- Fast
	- `PreferIndexOnly = true`
	- `LohThresholdBytes = 85_000`
	- `TopLohTypeLimit = 8`
	- `TopGenProfileLimit = 10`
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `PreferIndexOnly = false`
	- `LohThresholdBytes = 85_000`
	- `TopLohTypeLimit = 15`
	- `TopGenProfileLimit = 20`
	- `ProduceRawExports = false`

- Full
	- `PreferIndexOnly = false`
	- `LohThresholdBytes = 85_000`
	- `TopLohTypeLimit = 30`
	- `TopGenProfileLimit = 40`
	- `ProduceRawExports = true`

## Minimal code changes (implementation plan)
1. Add `PreferIndexOnly` and optional `ProduceRawExports` to
	 `GCGenerationAnalysisOptions` and set preset defaults in
	 `Preset(AnalysisProfile)`.
2. In `GCGenerationAnalyzer`:
	 - Check for `TypeAggregates` as the primary path. If missing and
		 `PreferIndexOnly` is true, emit a diagnostic and return a lighter-weight
		 domain result (avoid per-type expansions).
	 - Otherwise continue with the existing fallback `BuildFromTypeStatistics`.
	 - When `ProduceRawExports` is true, stream top LOH types to an on-disk
		 artifact and attach `ReportArtifact(FilePath)`.
3. Update `GCGenerationSectionBuilder` to honor `TopLohTypeLimit` and
	 `TopGenProfileLimit` (it already uses a local `TopLohTypes` constant —
	 switch to reading from options when rendering if desired).

## Tests and validation
- Unit tests: mock `HeapIndexBuildResult` present/absent and verify behavior
	when `PreferIndexOnly` toggles; assert `TopLohTypeLimit` and
	`TopGenProfileLimit` are honored when building the domain result.
- Integration: run representative dumps under Fast/Balanced/Full and compare
	output size and number of rows for LOH and per-type generation profiles.

## Rationale
- Small named flags (`PreferIndexOnly`, `ProduceRawExports`) make preset
	intent explicit and allow predictable behavior for low-cost Fast runs while
	enabling deeper investigation under Full.

## Next steps I can take
- Implement the `GCGenerationAnalysisOptions` additions and update the
	`Preset` factory.
- Modify `GCGenerationAnalyzer` and `GCGenerationSectionBuilder` to read the
	options and add unit/integration tests.

Which next step should I take? I can implement the options and update the
`Preset` factory now.
