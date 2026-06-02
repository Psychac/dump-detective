# ObjectShapeAnalyzer — Preset Design

Status: ✅ **COMPLETED**

Purpose: build type-shape profiles (field-layout/slot-count signatures) and report common shapes per type. This document follows the same structure and level of detail as `string-preset-design.md` and maps presets to concrete `ObjectShapeAnalysisOptions.Preset(AnalysisProfile)` values.

Where to look in the repo
- Analyzer: [src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs)
- Section builder: [src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs)

## Current working (summary)
- `ObjectShapeAnalyzer` is a Phase‑2, index-driven analyzer that reads `HeapIndexBuildResult.TypeShapeCache` and `HeapIndexBuildResult.TypeAggregates` to build `TypeShapeProfile` entries without enumerating all heap objects at analysis time.
- The analyzer ranks types by a GC-scan-cost heuristic (roughly `referenceFieldRatio × instanceCount`) and reports top reference-heavy and value-heavy types.
- Key runtime knobs: `InstanceCountCap` (how many top types / instances to consider) and `TopListLimit` (how many types to present in each list).

## Observed implementation details
- Index-first: the analyzer requires a built heap index with `TypeShapeCache`; if the cache is not present it returns an empty result.
- Options exposed by `ObjectShapeAnalysisOptions`: `InstanceCountCap` and `TopListLimit` (used to bound metadata access and output size).
- The analyzer resolves `ClrType` via `heap.GetTypeByMethodTable(mt)` for candidate method tables and computes sample metadata such as `IsFinalizable`, `IsValueType`, base type depth and interface count.
- The section builder formats the results into table blocks for reference‑heavy and value‑heavy types and truncates long type names for readability.

## Goals for preset-driven flow
- Presets should map to conservative, balanced and exhaustive configurations of `InstanceCountCap` and `TopListLimit` so users can pick a profile by expected dump size and required detail.
- Analyzer-level overrides (explicit config in `config.json`) must take precedence over presets.

## Suggested new/used options (already present)
- `int InstanceCountCap` — maximum number of candidate types (by instance count) to consider; bounds ClrType metadata lookups.
- `int TopListLimit` — how many shape entries to include per reported list (reference-heavy / value-heavy).

## Concrete preset mappings (recommended)

- Fast
	- `InstanceCountCap = 100`
	- `TopListLimit = 10`

- Balanced (baseline / existing defaults)
	- `InstanceCountCap = 200`
	- `TopListLimit = 20`

- Full
	- `InstanceCountCap = 1000`
	- `TopListLimit = 50`

Rationale: Fast keeps metadata work small for very large dumps; Balanced is the default tradeoff; Full raises caps to produce richer coverage for audits.

## How analyzer flow respects presets
- The analyzer already uses `options.InstanceCountCap` to limit the number of candidate MTs considered and `options.TopListLimit` to bound the size of `refHeavy` and `valHeavy` result lists.
- No heap object materialization is performed — all work is limited to type-level metadata and index data.

## Minimal code changes (implementation plan)
1. None required — `ObjectShapeAnalysisOptions` already exposes `InstanceCountCap` and `TopListLimit` and implements `Preset(AnalysisProfile)`.
2. If desired, add a README caution about `Full` memory/cpu cost.

## Tests and validation
- Unit tests: construct a synthetic `HeapIndexBuildResult` with `TypeShapeCache` and `TypeAggregates`, run `ObjectShapeAnalyzer.Analyze(...)`, and assert:
  - the number of types analyzed is bounded by `InstanceCountCap`;
  - `TopReferenceHeavyTypes` and `TopValueHeavyTypes` sizes respect `TopListLimit`;
  - type metadata fields (e.g., `IsFinalizable`, `IsValueType`) propagate correctly.
- Integration: exercise the analyzer on a recorded or synthetic dump and compare output counts between `Fast` / `Balanced` / `Full` presets.

## Next steps
- I can add a short note to the `docs/improvements/presets/README.md` advising caution about `Full` runs (higher memory/CPU) and link to `ObjectShapeAnalysisOptions` if you want.

References: see the analyzer implementation at [src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs) and the report view at [src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs).
