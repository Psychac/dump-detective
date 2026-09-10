# CLI / Orchestration

**Purpose:**
- Provide a user-facing entrypoint, pipeline orchestration, and configuration handling.

**Responsibilities:**
- Parse CLI flags and map to `ResolvedExecutionOptions`.
- Orchestrate Phase 1 (index build) and Phase 2 (analysis & reporting).
- Register DI and assemble analyzers and generators.

**Key types / interfaces:**
- `SingleDumpOrchestrationService`
- `AnalyzerFilterService`
- `ResolvedExecutionOptions`

**Performance / safety constraints:**
- Surface progress and allow resume/caching where appropriate.
- Analyzer behavior (thresholds, caps, exports) is fixed, not user-configurable — see
  [docs/refactor/analysis-options-removal-plan.md](../refactor/analysis-options-removal-plan.md).
  Only pipeline-level settings (dump/output paths, cache directory, index mode, report format,
  which analyzers run) are configurable via CLI flags or `config.json`.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [config.sample.json](config.sample.json)
- [docs/refactor/analysis-options-removal-plan.md](../refactor/analysis-options-removal-plan.md)
