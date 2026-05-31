# Analysis Layer

**Purpose:**
- Implement analyzers that operate on indices and runtime facades to produce domain results.

**Responsibilities:**
- Provide streaming, bounded analyzers (e.g., `RetentionAnalyzer`, `ThreadAnalyzer`, `LohFragmentationAnalyzer`).
- Use `IHeapAnalysisCache` and `RuntimeFacade` rather than direct ClrMD in hot paths.
- Return domain results consumable by finding generators.

**Key types / interfaces:**
- `IAnalyzer` (Core contract)
- `AnalyzerRunResult`
- Domain analyzers (Retention, Thread, Module, GCHandle, etc.)

**Performance / safety constraints:**
- Analyze only filtered subsets when doing deep work.
- Reuse indices from Phase 1; avoid multiple full heap scans.
- Mark analyzers `IsThreadSafe` where safe for parallel type-level tasks.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [docs/performance-checklist.md](docs/performance-checklist.md)
