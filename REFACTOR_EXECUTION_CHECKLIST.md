# DumpDetective — Refactor Execution Checklist (Specs 01–07)

> **Runtime Target:** `.NET 10`
> **Execution Style:** Iterative, green build at every step
> **Branch:** `rearch`

---

## Global Quality Gates (apply to every iteration)

- [x] Build passes.
- [x] Relevant tests pass.
- [x] File config precedence preserved (`config` first, CLI fallback only if config missing).
- [x] Report detail preserved (no evidence/remediation loss).
- [x] Duplicate report sections removed at source.
- [x] Long table values wrapped (no truncation).
- [x] Deterministic behavior preserved.

---

## Iteration 0 — Baseline and Guardrails

### Tasks
- [x] Capture baseline build/test status.
- [x] Document current behavior snapshots for pipeline + reporting outputs.
- [x] Add ADRs for dependency direction and reporting boundary.

### Exit Criteria
- [x] Baseline documented.
- [x] Green build.

### Current Status Notes (Iteration 0)
- Baseline rebuild captured in Visual Studio: `6 succeeded, 0 failed`.
- Baseline test status was initially environment-constrained, then unblocked; current automated suite now executes successfully (`32` passing tests at latest validation).
- Baseline behavior snapshot is documented in `docs/refactor/BASELINE_BEHAVIOR_SNAPSHOTS.md`.
- ADRs are documented in `docs/adr/0001-dependency-direction.md` and `docs/adr/0002-reporting-boundary.md`.

---

## Iteration 1 — Solution Structure (Spec 01)

### Tasks
- [x] Ensure project layout matches `src/` + `tests/` structure.
- [x] Ensure all projects target `.NET 10`.
- [x] Move `Core` models/interfaces/utilities into `DumpDetective.Core`.
- [x] Normalize namespaces and project references.

### Validation
- [x] Build passes after each move batch.
- [x] No forbidden dependency direction introduced.

### Exit Criteria
- [x] Structure aligned with Spec 01.
- [x] No behavior regression.

### Current Status Notes (Spec 01)
- Multi-project structure (`Core`, `Analysis`, `Reporting`, `Cli`) is in place and compiles.
- `DumpDetective.Tests` project is active and currently validates successfully in local/CI flow.
- Temporary migration bridge backlog has been fully retired (see `REFACTOR_TEMP_BRIDGES.md`).
- Refactored architecture projects are now under `src/` and `tests/` in the solution graph.
- Legacy `DumpDetective` project remains at repository root intentionally for side-by-side compatibility.

---

## Iteration 2 — Options + Config Precedence (Spec 02)

### Tasks
- [x] Finalize strongly typed option classes.
- [x] Implement config-first resolution flow.
- [x] Implement CLI fallback only when config is not found.
- [x] Add startup validation with actionable field-level errors.

### Validation
- [x] Tests for precedence matrix pass.
- [x] Invalid config path/shape fails clearly.

### Exit Criteria
- [x] Precedence rule enforced and tested.

### Current Status Notes (Spec 02)
- `RootCommandBuilder` now maps typed CLI arguments via `System.CommandLine`.
- `ConfigurationResolver` enforces config-first behavior and uses CLI only when config is not found.
- `StartupValidator` performs field-level path/range checks and returns actionable validation errors.
- End-to-end execution path is active through Spec 03/04 orchestration with bridge-free options flow.

---

## Iteration 3 — Analyzer Contracts + Async Pipeline (Spec 03)

### Tasks
- [x] Finalize `IAnalyzer` contract in `Core`.
- [x] Finalize `AnalyzerDomainResult`, `AnalyzerRunResult`, `AnalyzerExecutionStatus`.
- [x] Extract/finalize `AnalysisContext`.
- [x] Implement deterministic async `AnalysisPipeline`.
- [x] Add failure policy + cancellation behavior.
- [x] Migrate analyzers to async contract.

### Validation
- [x] Order is deterministic (`Order`, then `Name`).
- [x] `ContinueOnAnalyzerFailure` behavior tested.
- [x] Cancellation status handling tested.

### Exit Criteria
- [x] Pipeline deterministic and policy-compliant.

### Current Status Notes (Spec 03)
- `IAnalyzer` now exposes async `AnalyzeAsync(...)` with metadata (`Category`, `Order`, `Tags`).
- `AnalyzerDomainResult` now includes analyzer identity/findings/metrics/warnings contract fields.
- `AnalyzerRunResult` now uses explicit `AnalyzerExecutionStatus` (`Success`, `Failed`, `Skipped`, `Canceled`).
- `AnalysisPipeline` now executes asynchronously with deterministic ordering and failure/cancellation mapping.
- Analyzer native async migration is complete and no temporary bridge items remain active.

---

## Iteration 4 — CLI Hosting + Command Model (Spec 04)

### Tasks
- [x] Stabilize `Program.cs` bootstrapping.
- [x] Implement `RootCommandBuilder` request mapping.
- [x] Centralize DI in `ServiceRegistration`.
- [x] Wire config merge + validation before execution.
- [x] Standardize exit codes.

### Validation
- [x] End-to-end CLI flow works with and without config.
- [x] Exit codes match failure categories.

### Exit Criteria
- [x] CLI architecture stable and predictable.

### Current Status Notes (Spec 04)
- Host-driven bootstrapping is in place (`Program` + `ServiceRegistration.BuildHost`).
- Root command maps parser output into immutable `AnalysisCommandRequest`.
- Config merge/validation executes before dump loading and analysis.
- Exit-code mapping is standardized (`0,1,2,3,4,130`) with typed CLI exceptions.
- End-to-end CLI execution was revalidated with and without config path usage, including explicit missing-config handling.

---

## Iteration 5 — Reporting Boundary + Formatters (Spec 05)

### Tasks
- [x] Refactor `ReportBuilder` to composition-only.
- [x] Define canonical section model + stable `SectionKey`.
- [x] Implement source-level dedup merge logic.
- [x] Align `IReportFormatter` implementations (`md/txt/html`) to render-only behavior.
- [x] Centralize wrapping/table helpers.

### Validation
- [x] Same composed sections rendered across all formats.
- [x] No formatter-side dedup behavior remains.
- [x] No truncation of long values.

### Exit Criteria
- [x] Reporting boundary enforced.

### Current Status Notes (Spec 05)
- Canonical report model (`ComposedReport`/`ReportSection`) is implemented and used for rendering.
- Source-level deduplication now runs inside `ReportBuilder` with deterministic merge behavior.
- Canonical `IReportFormatter` implementations (`Text`, `Markdown`, `Html`) render from composed model only.
- Shared wrapping helper (`TableWrapHelper`) is used to wrap long values without truncation.
- Reporting-focused tests added in `DumpDetective.Tests/ReportingCompositionTests.cs` for dedup, wrapping, and formatter parity.
- Legacy static formatter stack has been removed; canonical formatter pipeline is the sole active reporting formatter path.

---

## Iteration 6 — Tests + Golden Files (Spec 06)

### Tasks
- [x] Expand unit test coverage (pipeline, precedence, dedup, wrapping).
- [x] Add integration scenarios for composed reporting flow.
- [x] Add golden fixtures and baselines (`md/txt/html`).
- [x] Add deterministic normalization in tests (time, paths, line endings).

### Validation
- [x] Golden tests fail with readable diffs on drift.
- [x] CI requires all test categories.

### Exit Criteria
- [x] Regression guardrails active for behavior + output contracts.

### Current Status Notes (Spec 06)
- Added unit coverage for async pipeline ordering/failure/cancellation (`Unit/Analysis/AnalysisPipelineTests.cs`).
- Added config precedence coverage including config-first, CLI gap-fill, CLI fallback, and explicit missing-config failure (`Unit/Configuration/ConfigurationResolverTests.cs`).
- Added reporting golden infrastructure (`Golden/GoldenFileAssert.cs`, `Golden/GoldenFileTests.cs`) and committed formatter baselines for `BaselineSmall` across `text/markdown/html`.
- Expanded golden fixture matrix to `BaselineSmall`, `DuplicateHeavy`, `LongNames`, `RichEvidence`, and `MixedSeverity` across `text/markdown/html`.
- Added integration coverage for composed report flow and dedup through facade rendering (`Integration/ReportFlowIntegrationTests.cs`).
- Added deterministic normalization in golden assert helper (line-ending normalization) and fixed fixture timestamp/path determinism.
- Added baseline copy-to-output wiring in `DumpDetective.Tests.csproj` and validated via `dotnet test` (32 passed).
- Added CI workflow (`.github/workflows/ci.yml`) running restore/build/test and benchmark smoke.

---

## Iteration 7 — Observability + Performance Guardrails (Spec 07)

### Tasks
- [x] Implement normalized diagnostics event model.
- [x] Add analyzer/run-level metrics (timing, scans, cache hit/miss).
- [x] Add diagnostics sinks and summary output.
- [x] Expand benchmark coverage for hotspots.
- [x] Add CI baseline comparison thresholds.

### Validation
- [x] Diagnostics mode provides actionable timing and cache/scan summaries.
- [x] Performance regressions are detectable in CI.

### Exit Criteria
- [x] Observability and perf guardrails operational.

### Current Status Notes (Spec 07)
- Added normalized diagnostics event contract (`AnalysisDiagnosticsEvent`) and event type taxonomy in `Core`.
- `AnalysisPipeline` now emits run-level and analyzer-level lifecycle diagnostics (`RunStarted`, `AnalyzerStarted`, `AnalyzerCompleted`, `AnalyzerFailed`, `AnalyzerCanceled`, `RunCompleted`).
- Added per-analyzer metric capture in `AnalyzerRunResult` (finding/warning counts, object scans, cache hits/misses).
- Added cache/scan counters in `IHeapAnalysisCache` + `HeapAnalysisCache` implementation.
- Added sink support for null, in-memory (tests), console, and optional file diagnostics sinks.
- Added diagnostics unit coverage (`Unit/Analysis/AnalysisDiagnosticsTests.cs`) and validated full test run (`32 passed`).
- Expanded `BenchmarkSuite1` with pipeline and reporting hotspot benchmarks (`PipelineHotspotBenchmark`, `ReportingHotspotBenchmark`).
- Added benchmark baseline threshold scaffolding (`BenchmarkSuite1/perf-baselines.json`, `BenchmarkSuite1/compare-benchmarks.ps1`).
- CI workflow now executes benchmark smoke and baseline comparison to detect threshold regressions.

---

## Final Definition of Done

- [x] Specs 01–07 completed.
- [x] All projects build on `.NET 10`.
- [x] Dependency direction clean.
- [x] Config precedence rule verified.
- [x] Report detail preserved with source dedup and wrapped long values.
- [x] Unit + integration + golden tests green.
- [x] Diagnostics and performance checks active.

### Remaining Closure Notes
- No open technical closure items remain in Specs 01–07.

---

## Suggested Working Cadence

- [ ] Use one PR per iteration.
- [ ] Keep each PR scoped to one spec.
- [ ] Include explicit checklist references in PR description.
- [ ] Require test evidence and output examples for reporting changes.
