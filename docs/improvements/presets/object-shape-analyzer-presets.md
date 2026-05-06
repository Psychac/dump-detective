# ObjectShapeAnalyzer — Preset Design

Purpose: build type-shape profiles (field-layout/slot-count signatures) and report common shapes per type.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs
- Section builder: src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs

Observed implementation details:
- Relies on Phase-1 heap index data: `HeapIndexBuildResult.TypeShapeCache` (index-first design).
- `ObjectShapeAnalysisOptions` exposes `InstanceCountCap` and `TopListLimit` which bound memory and output size.
- Analyzer avoids materializing per-object allocations by aggregating into a compact `TypeShapeProfile`.

Preset knobs to expose:
- `InstanceCountCap` (int) — maximum object instances the analyzer will consider per type before sampling.
- `TopListLimit` (int) — how many shapes per type to include in the report.

Built-in presets (from `ObjectShapeAnalysisOptions.Preset`):
- **Fast:** `InstanceCountCap=100`, `TopListLimit=10`
- **Balanced (default):** `InstanceCountCap=200`, `TopListLimit=20`
- **Full:** `InstanceCountCap=1000`, `TopListLimit=50`

Rationale — when to pick each preset:
- **Fast:** low `InstanceCountCap` and `TopListLimit` to keep memory and CPU bounded on large dumps while surfacing representative shapes.
- **Balanced:** default values provide reasonable shape coverage and sampling for medium-sized heaps.
- **Full:** raise caps to explore many shapes per type and produce richer shape histograms for detailed audits (higher memory/cpu).

Minimal code changes:
- No-op: `ObjectShapeAnalysisOptions` already provides `Preset(AnalysisProfile)` and `Default`.

Tests and validation:
- Unit: feed a synthetic `TypeShapeCache` and assert top-shape ordering and `InstanceCountCap` truncation.

Next steps:
- I can add a short note in the README advising caution about `Full` runs memory usage for very large heaps.
