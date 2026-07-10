
# Implementation Specification – Performance & Validation

## Goals

Verify that cache modernization improves performance without changing correctness.

## Benchmark Dumps

Use representative dumps:

- Small (<1 GB)
- Medium (1–5 GB)
- Large (5–25 GB)
- Very Large (25 GB+)

## Metrics

Index Build Time

Graph Build Time

Peak Managed Memory

Peak Private Memory

ClrMD Reference Enumerations

Analyzer Runtime

Disk I/O (when applicable)

## Correctness Checks

- Analyzer output unchanged.
- Root counts unchanged.
- Object counts unchanged.
- Type statistics unchanged.
- Reference chain results equivalent.

## Regression Targets

The modernization should never:

- Increase peak memory significantly without measurable benefit.
- Introduce duplicate graph construction.
- Increase ClrMD calls for migrated analyzers.

## Future Opportunities

Once complete, the architecture should support:

- Dominator tree analysis
- Retained size
- SCC detection
- Cycle detection
- Root path caching
- Graph-based leak diagnostics

without major architectural changes.
