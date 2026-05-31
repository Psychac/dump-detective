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
- Make execution policy (timeouts, depth limits, index mode) configurable.
- Surface progress and allow resume/caching where appropriate.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [config.sample.json](config.sample.json)
