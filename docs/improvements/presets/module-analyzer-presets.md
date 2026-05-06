# ModuleAnalyzer — Preset Design

Purpose: list loaded modules, detect version conflicts, and optionally attribute live heap memory per module (when heap index present).

Where to look in the repo:
- Analyzer: DumpDetective/Analyzers/ModuleAnalyzer.cs
- Section builder: src/DumpDetective.Reporting/SectionBuilders/ModuleSectionBuilder.cs

Observed implementation details:
- `ModuleAnalyzer` already exposes `TopLoadedAssembliesCount` (used to cap the Top table) and produces `TopModulesByHeapMemory` only when a heap index is available.
- Conflict detection groups multiple copies of the same module path/name into `ConflictDetails`.

Preset knobs to expose:
- `TopLoadedAssembliesCount` (int) — how many modules to list by binary size.
- `TopModulesByHeapCount` (int) — number of modules by heap memory to return (requires Phase‑1 heap index).
- `HeavyModuleWarningThresholdBytes` / `DensityAnomalyMinBytes` / `DensityAnomalyMaxTypes` — severity/density tuning knobs.

Built-in presets (from `ModuleAnalysisOptions.Preset`):
- **Fast:** `TopLoadedAssembliesCount=15`, `TopModulesByHeapCount=10`, `HeavyModuleWarningThresholdBytes=300 MB`, `DensityAnomalyMinBytes=100 MB`, `DensityAnomalyMaxTypes=3`.
- **Balanced (default):** `TopLoadedAssembliesCount=30`, `TopModulesByHeapCount=20`, `HeavyModuleWarningThresholdBytes=200 MB`, `DensityAnomalyMinBytes=50 MB`, `DensityAnomalyMaxTypes=5`.
- **Full:** `TopLoadedAssembliesCount=80`, `TopModulesByHeapCount=50`, `HeavyModuleWarningThresholdBytes=100 MB`, `DensityAnomalyMinBytes=20 MB`, `DensityAnomalyMaxTypes=10`.

Rationale — when to pick each preset:
- **Fast:** reduce listing width and raise `HeavyModuleWarningThresholdBytes` to avoid surfacing many moderately large modules on huge processes.
- **Balanced:** default for most investigations, lists common modules and performs density checks with moderate thresholds.
- **Full:** broaden listings and lower density thresholds to surface smaller but dense modules in detailed audits.

Minimal code changes:
- No-op: `ModuleAnalysisOptions` already implements `Preset(AnalysisProfile)`; remove mentions of `EnableHeapAttribution` since heap attribution is conditional on index presence and not a separate option.

Tests and validation:
- Unit: construct a `ModuleAnalysis` object and assert TopModules truncation and conflict grouping.
- Integration: run ModuleAnalyzer with and without the heap index to verify `TopModulesByHeapMemory` generation toggles correctly.

Next steps:
- I can add a README note explaining that heap attribution requires Phase‑1 indices and that `TopModulesByHeapCount` is ignored when the index is missing.
