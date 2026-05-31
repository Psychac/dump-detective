# Insight Layer

**Purpose:**
- Synthesize high-level diagnostics (insights) from analyzer results.

**Responsibilities:**
- Score and rank findings (leak suspicion, GC pressure, thread contention).
- Produce `InsightFinding` records consumed by reporting.

**Key types / interfaces:**
- `InsightEngine`
- `InsightFinding`

**Performance / safety constraints:**
- Work on aggregated analyzer results rather than per-object data where possible.
- Keep heuristics explainable and reproducible.

**Related docs:**
- [docs/architecture.md](docs/architecture.md)
