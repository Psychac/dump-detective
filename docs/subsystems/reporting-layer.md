# Reporting / Output Layer

**Purpose:**
- Convert analyzer and insight results into user-facing outputs (CLI, JSON, HTML reports).

**Responsibilities:**
- Implement `IFindingGenerator` to convert domain results to `InsightFinding`.
- Compose the final report (sections, summaries, artifacts).
- Handle failures in finding generators gracefully and surface warnings.

**Key types / interfaces:**
- `IFindingGenerator`
- Report composers and renderers (HTML, JSON, CLI formatters)

**Performance / safety constraints:**
- Avoid including raw, unbounded object dumps in reports.
- Stream outputs where possible; paginate large sections.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
- [sample-report.html](sample-report.html)
