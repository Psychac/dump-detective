# DumpDetective — Refactor Execution Checklist (Specs 01–07)

> **Runtime Target:** `.NET 10`
> **Execution Style:** Iterative, green build at every step
> **Branch:** `rearch`

---

## Global Quality Gates (apply to every iteration)

- [x] Build passes.
- [ ] Relevant tests pass.
- [x] File config precedence preserved (`config` first, CLI fallback only if config missing).
- [ ] Report detail preserved (no evidence/remediation loss).
- [ ] Duplicate report sections removed at source.
- [ ] Long table values wrapped (no truncation).
- [ ] Deterministic behavior preserved.

---

## Iteration 0 — Baseline and Guardrails

### Tasks
- [ ] Capture baseline build/test status.
- [ ] Document current behavior snapshots for pipeline + reporting outputs.
- [ ] Add ADRs for dependency direction and reporting boundary.

### Exit Criteria
- [ ] Baseline documented.
- [ ] Green build.

---

## Iteration 1 — Solution Structure (Spec 01)

### Tasks
- [ ] Ensure project layout matches `src/` + `tests/` structure.
- [x] Ensure all projects target `.NET 10`.
- [x] Move `Core` models/interfaces/utilities into `DumpDetective.Core`.
- [x] Normalize namespaces and project references.

### Validation
- [x] Build passes after each move batch.
- [x] No forbidden dependency direction introduced.

### Exit Criteria
- [ ] Structure aligned with Spec 01.
- [ ] No behavior regression.

### Current Status Notes (Spec 01)
- Multi-project structure (`Core`, `Analysis`, `Reporting`, `Cli`) is in place and compiles.
- `DumpDetective.Tests` project exists, but test restore/execution is currently blocked by feed auth in this environment.
- Temporary migration bridges were explicitly marked with `TEMP-REFRACTOR-BRIDGE` and tracked in `REFACTOR_TEMP_BRIDGES.md`.
- Full `src/` + `tests/` physical directory alignment is still pending.

---

## Iteration 2 — Options + Config Precedence (Spec 02)

### Tasks
- [x] Finalize strongly typed option classes.
- [x] Implement config-first resolution flow.
- [x] Implement CLI fallback only when config is not found.
- [x] Add startup validation with actionable field-level errors.

### Validation
- [ ] Tests for precedence matrix pass.
- [ ] Invalid config path/shape fails clearly.

### Exit Criteria
- [ ] Precedence rule enforced and tested.

### Current Status Notes (Spec 02)
- `RootCommandBuilder` now maps typed CLI arguments via `System.CommandLine`.
- `ConfigurationResolver` enforces config-first behavior and uses CLI only when config is not found.
- `StartupValidator` performs field-level path/range checks and returns actionable validation errors.
- Full end-to-end execution is still under temporary bridge flow (`TEMP-REFRACTOR-BRIDGE`) until Spec 03/04 orchestration is completed.

---

## Iteration 3 — Analyzer Contracts + Async Pipeline (Spec 03)

### Tasks
- [ ] Finalize `IAnalyzer` contract in `Core`.
- [ ] Finalize `AnalyzerDomainResult`, `AnalyzerRunResult`, `AnalyzerExecutionStatus`.
- [ ] Extract/finalize `AnalysisContext`.
- [ ] Implement deterministic async `AnalysisPipeline`.
- [ ] Add failure policy + cancellation behavior.
- [ ] Migrate analyzers to async contract.

### Validation
- [ ] Order is deterministic (`Order`, then `Name`).
- [ ] `ContinueOnAnalyzerFailure` behavior tested.
- [ ] Cancellation status handling tested.

### Exit Criteria
- [ ] Pipeline deterministic and policy-compliant.

---

## Iteration 4 — CLI Hosting + Command Model (Spec 04)

### Tasks
- [ ] Stabilize `Program.cs` bootstrapping.
- [ ] Implement `RootCommandBuilder` request mapping.
- [ ] Centralize DI in `ServiceRegistration`.
- [ ] Wire config merge + validation before execution.
- [ ] Standardize exit codes.

### Validation
- [ ] End-to-end CLI flow works with and without config.
- [ ] Exit codes match failure categories.

### Exit Criteria
- [ ] CLI architecture stable and predictable.

---

## Iteration 5 — Reporting Boundary + Formatters (Spec 05)

### Tasks
- [ ] Refactor `ReportBuilder` to composition-only.
- [ ] Define canonical section model + stable `SectionKey`.
- [ ] Implement source-level dedup merge logic.
- [ ] Align `IReportFormatter` implementations (`md/txt/html`) to render-only behavior.
- [ ] Centralize wrapping/table helpers.

### Validation
- [ ] Same composed sections rendered across all formats.
- [ ] No formatter-side dedup behavior remains.
- [ ] No truncation of long values.

### Exit Criteria
- [ ] Reporting boundary enforced.

---

## Iteration 6 — Tests + Golden Files (Spec 06)

### Tasks
- [ ] Expand unit test coverage (pipeline, precedence, dedup, wrapping).
- [ ] Add integration scenarios for composed reporting flow.
- [ ] Add golden fixtures and baselines (`md/txt/html`).
- [ ] Add deterministic normalization in tests (time, paths, line endings).

### Validation
- [ ] Golden tests fail with readable diffs on drift.
- [ ] CI requires all test categories.

### Exit Criteria
- [ ] Regression guardrails active for behavior + output contracts.

---

## Iteration 7 — Observability + Performance Guardrails (Spec 07)

### Tasks
- [ ] Implement normalized diagnostics event model.
- [ ] Add analyzer/run-level metrics (timing, scans, cache hit/miss).
- [ ] Add diagnostics sinks and summary output.
- [ ] Expand benchmark coverage for hotspots.
- [ ] Add CI baseline comparison thresholds.

### Validation
- [ ] Diagnostics mode provides actionable timing and cache/scan summaries.
- [ ] Performance regressions are detectable in CI.

### Exit Criteria
- [ ] Observability and perf guardrails operational.

---

## Final Definition of Done

- [ ] Specs 01–07 completed.
- [ ] All projects build on `.NET 10`.
- [ ] Dependency direction clean.
- [ ] Config precedence rule verified.
- [ ] Report detail preserved with source dedup and wrapped long values.
- [ ] Unit + integration + golden tests green.
- [ ] Diagnostics and performance checks active.

---

## Suggested Working Cadence

- [ ] Use one PR per iteration.
- [ ] Keep each PR scoped to one spec.
- [ ] Include explicit checklist references in PR description.
- [ ] Require test evidence and output examples for reporting changes.
