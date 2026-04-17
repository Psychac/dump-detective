# DumpDetective — Spec 07: Observability and Performance Guardrails

> **Phase:** Iteration 7
> **Prerequisite:** `REFACTOR_SPEC_01_SOLUTION_STRUCTURE.md`, `REFACTOR_SPEC_02_FULL_REWRITE_EXECUTION_PLAN.md`, `REFACTOR_SPEC_03_ANALYZER_CONTRACTS_AND_PIPELINE.md`, `REFACTOR_SPEC_04_CLI_HOSTING_AND_COMMAND_MODEL.md`, `REFACTOR_SPEC_05_REPORTING_BOUNDARY_AND_FORMATTERS.md`, `REFACTOR_SPEC_06_TEST_STRATEGY_AND_GOLDEN_FILES.md`
> **Target Runtime:** `.NET 10`

---

## 1. Goal

Define measurable observability and performance standards so analysis remains:
- diagnosable,
- deterministic,
- scalable on large dumps,
- safe from regressions as analyzers grow.

---

## 2. Scope

### In Scope
- structured diagnostics events and run summaries,
- per-analyzer timing and throughput metrics,
- cache/memory/scan instrumentation,
- benchmark and budget guardrails,
- performance-focused CI checks and triage policy.

### Out of Scope
- business logic changes to analyzer findings,
- CLI command design changes (already covered in Spec 04),
- report composition/formatter boundary rules (covered in Spec 05).

---

## 3. Observability Contract

## 3.1 Diagnostics Event Model

Define a common diagnostics event payload with at least:
- `RunId`
- `EventType` (`RunStarted`, `AnalyzerStarted`, `AnalyzerCompleted`, `AnalyzerFailed`, `AnalyzerCanceled`, `RunCompleted`)
- `TimestampUtc`
- `AnalyzerName` (nullable for run-level events)
- `Category`
- `DurationMs` (when applicable)
- `ObjectScanCount`
- `CacheHits`
- `CacheMisses`
- `Message`
- `ExceptionType`/`ExceptionMessage` (when failed)

Rule: all analyzer pipeline events use this normalized shape.

## 3.2 Diagnostics Sinks

Support at least:
- in-memory sink for tests,
- console sink for interactive mode,
- optional file sink for diagnostics mode.

Rules:
- sinks must be non-blocking in default mode,
- sink failures must not crash analysis run.

---

## 4. Metrics Specification

## 4.1 Required Per-Analyzer Metrics

For each analyzer execution capture:
- elapsed time,
- finding count,
- warning count,
- object scans,
- cache hits/misses,
- status (`Success`, `Failed`, `Skipped`, `Canceled`).

## 4.2 Required Run-Level Metrics

Per run capture:
- total elapsed time,
- analyzers executed/failed/skipped/canceled,
- total findings,
- total object scans,
- aggregate cache hit ratio,
- peak working set estimate (if available).

## 4.3 Metric Quality Rules

- metric values must be deterministic for deterministic inputs,
- counters should be monotonic per run,
- use explicit units (`ms`, `count`, `bytes`, ratios).

---

## 5. Performance Guardrails

## 5.1 Baseline and Budget Model

Define baseline budgets per representative fixture:
- max total pipeline duration,
- max critical analyzer duration,
- max memory growth threshold,
- minimum cache hit ratio for cache-enabled analyzers.

Budgets are versioned and reviewed when analyzer behavior intentionally changes.

## 5.2 Regression Policy

A regression is flagged when:
- runtime exceeds budget threshold,
- memory growth exceeds threshold,
- scan count unexpectedly spikes,
- cache effectiveness drops materially.

Suggested default threshold: >10% deviation from approved baseline unless explained.

---

## 6. Benchmark Strategy

## 6.1 Benchmark Scope

Use existing benchmark project (`benchmarks/BenchmarkSuite1`) to cover:
- full pipeline run,
- high-cost analyzers individually,
- reference-chain traversal hotspot,
- section composition on large finding sets,
- formatter render on large tables with long names.

## 6.2 Benchmark Inputs

Use deterministic synthetic fixtures and sanitized representative dump data.

## 6.3 Benchmark Output Requirements

Capture and persist:
- mean/median/p95 duration,
- allocation volume,
- GC collections,
- throughput indicators where applicable.

---

## 7. Hotspot Instrumentation Requirements

Instrument these components first:
- `AnalysisPipeline` execution loop,
- heap traversal and object scanning primitives,
- reference chain/path discovery,
- dedup merge engine in report composition,
- formatter table wrapping for long values.

Rule: instrumentation must not truncate or suppress actionable detail in diagnostics output.

---

## 8. CLI and Diagnostics Mode Behavior

When diagnostics mode is enabled:
- print run summary with analyzer timings sorted by descending duration,
- print top N slow analyzers,
- print scan and cache summaries,
- include config source used (config-first precedence trace),
- include warning/failure summary with analyzer names.

Default mode should remain concise but still preserve full report detail.

---

## 9. CI/CD Guardrails

## 9.1 Required Performance Checks

CI should include:
1. unit/integration/golden tests (Spec 06),
2. selected benchmark smoke suite,
3. regression comparison against approved baseline.

## 9.2 Failure Handling

If performance guardrail fails:
- mark build failed (or warning-only for pre-agreed non-blocking stage),
- publish comparison artifact,
- require explicit waiver/rationale for temporary threshold exceptions.

---

## 10. Implementation Plan

## Step 1 — Diagnostics contracts
- define normalized diagnostics event model,
- add sink abstractions and default implementations.

## Step 2 — Pipeline instrumentation
- add per-analyzer and run-level timing/counter collection,
- surface metrics in run result model.

## Step 3 — Cache/scan metrics
- expose cache hit/miss counters and scan totals from shared services,
- integrate into diagnostics and summaries.

## Step 4 — Benchmark expansion
- add/align benchmarks for pipeline/analyzer/render hotspots,
- store baseline metrics.

## Step 5 — CI integration
- run benchmark smoke set in CI,
- compare against baseline thresholds,
- publish regression reports.

---

## 11. Acceptance Criteria

1. Analyzer and run-level metrics are emitted consistently.
2. Diagnostics mode provides actionable performance summaries.
3. Benchmark suite covers pipeline, analyzer hotspots, and reporting hotspots.
4. Baselines and thresholds are defined and enforced in CI.
5. Regressions are detectable with clear failure diagnostics.

---

## 12. Test Plan

## 12.1 Unit Tests
- diagnostics event payload completeness,
- metric aggregation correctness,
- sink failure isolation behavior,
- deterministic ordering of timing summaries.

## 12.2 Integration Tests
- full run emits expected analyzer lifecycle events,
- diagnostics mode output includes key summaries,
- cancellation/failure runs still produce valid metrics.

## 12.3 Benchmark Validation
- verify benchmarks execute in controlled environment,
- validate comparison tooling flags threshold breaches.

---

## 13. Risks and Mitigations

1. **Risk:** instrumentation overhead distorts runtime  
   **Mitigation:** keep default instrumentation lightweight; add verbose detail only in diagnostics mode.

2. **Risk:** noisy benchmark data from environment variance  
   **Mitigation:** use stable fixtures, isolated runners, and tolerance bands.

3. **Risk:** metric schema drift across components  
   **Mitigation:** centralized diagnostics contract and shared event factory.

---

## 14. Deliverables

- diagnostics event contract and sinks,
- per-analyzer and run-level metric collection,
- benchmark coverage for critical hotspots,
- CI regression guardrails and reporting artifacts,
- contributor guidance for baseline updates and waivers.

---

## 15. Exit Criteria

- Build and test pipeline are green.
- Diagnostics mode shows actionable timing and cache/scan summaries.
- Performance regression checks are operational in CI.
- Refactor spec pack complete through Spec 07.
