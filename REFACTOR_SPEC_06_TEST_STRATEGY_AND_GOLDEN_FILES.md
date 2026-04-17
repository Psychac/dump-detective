# DumpDetective — Spec 06: Test Strategy and Golden Files

> **Phase:** Iteration 6
> **Prerequisite:** `REFACTOR_SPEC_01_SOLUTION_STRUCTURE.md`, `REFACTOR_SPEC_02_FULL_REWRITE_EXECUTION_PLAN.md`, `REFACTOR_SPEC_03_ANALYZER_CONTRACTS_AND_PIPELINE.md`, `REFACTOR_SPEC_04_CLI_HOSTING_AND_COMMAND_MODEL.md`, `REFACTOR_SPEC_05_REPORTING_BOUNDARY_AND_FORMATTERS.md`
> **Target Runtime:** `.NET 10`

---

## 1. Goal

Define a complete, repeatable testing strategy that protects:
- analyzer correctness,
- configuration precedence behavior,
- reporting detail preservation,
- source-level deduplication,
- formatter parity and no-truncation guarantees.

This spec establishes golden-file testing as a primary regression guardrail.

---

## 2. Testing Principles

1. **Behavior over implementation detail**: tests validate observable outcomes, not private internals.
2. **Determinism first**: all tests must be stable across repeated runs and environments.
3. **No data-loss regressions**: tests explicitly guard against dropped findings/evidence.
4. **Config precedence is mandatory**: config file wins over CLI if config exists.
5. **Dedup at source**: tests assert canonical section count before formatter output.
6. **No truncation**: long names/values must remain fully representable in output.

---

## 3. Test Pyramid for `DumpDetective.Tests`

## 3.1 Unit Tests (fast, isolated)

Focus:
- analyzer logic edge cases,
- pipeline ordering/failure/cancellation,
- options validation and merge precedence,
- section key generation and dedup merge policies,
- wrapping helper behavior.

Expected characteristics:
- no real dump dependency where avoidable,
- no file I/O unless directly testing serialization/rendering helper behavior.

## 3.2 Integration Tests (service composition)

Focus:
- pipeline + report composition + formatter path,
- CLI request/config merge behavior,
- end-to-end report generation for controlled synthetic inputs.

Expected characteristics:
- controlled fixtures,
- deterministic outputs and section ordering.

## 3.3 Golden/Snapshot Tests (output contract)

Focus:
- `markdown`, `text`, `html` output stability,
- high-value report details retained,
- long-value wrapping behavior,
- duplicate-section elimination reflected in final output.

Expected characteristics:
- explicit baseline files,
- reviewable diffs for intentional changes.

---

## 4. Folder and File Organization

Recommended structure under `tests/DumpDetective.Tests/`:

- `Unit/Analysis/` — analyzer and pipeline unit tests
- `Unit/Reporting/` — section model, dedup, wrapping helper tests
- `Unit/Configuration/` — precedence and options validation tests
- `Integration/` — end-to-end service composition tests
- `Golden/Fixtures/` — normalized input fixtures
- `Golden/Baselines/Markdown/`
- `Golden/Baselines/Text/`
- `Golden/Baselines/Html/`
- `Golden/GoldenFileTests.cs`

---

## 5. Core Test Specifications

## 5.1 Analyzer/Pipeline Unit Coverage

Must cover:
- analyzer execution order (`Order` then `Name`),
- `ContinueOnAnalyzerFailure=true|false` behavior,
- cancellation propagation and terminal status,
- exception-to-status mapping (`Failed` with error details),
- duration/metrics capture is populated.

## 5.2 Configuration Precedence Coverage

Must cover:
- config present + CLI present => config value used,
- config present + CLI value absent in config => CLI fills missing field only,
- config missing => CLI used,
- invalid config => validation failure with actionable message.

## 5.3 Reporting Composition Coverage

Must cover:
- canonical section key determinism,
- source-level duplicate merge rules,
- no evidence/remediation loss during merge,
- deterministic section ordering.

## 5.4 Formatting/Wrapping Coverage

Must cover:
- identical section counts/keys across `md/txt/html`,
- long type/member names are wrapped, not truncated,
- no hidden formatter-level dedup/filtering,
- remediation and diagnostics details remain visible.

---

## 6. Golden-File Test Contract

## 6.1 Golden Input Sets

Create representative fixture sets:
1. **BaselineSmall** — simple, low-noise finding set.
2. **DuplicateHeavy** — intentional duplicate-like findings to verify source dedup.
3. **LongNames** — extremely long type/member/assembly names.
4. **RichEvidence** — multiple evidence rows + remediation actions.
5. **MixedSeverity** — deterministic ordering checks.

## 6.2 Baseline Naming

Recommended pattern:
- `<FixtureName>.<Format>.golden`  
Examples:
- `BaselineSmall.markdown.golden`
- `DuplicateHeavy.text.golden`
- `LongNames.html.golden`

## 6.3 Golden Assertion Rules

For each fixture + format:
- actual output must match baseline text exactly,
- if mismatch occurs, fail with readable diff output,
- no automatic overwrite in CI,
- local baseline updates require intentional approval flow.

---

## 7. Determinism Controls

To keep snapshots stable:
- freeze time/provider for `GeneratedAt` fields,
- normalize path separators and machine-specific absolute paths,
- avoid unordered dictionary iteration in rendering,
- sort analyzers/sections/findings with stable tie-breakers,
- normalize line endings before compare.

---

## 8. CI Strategy

## 8.1 Required Checks

CI must run:
1. unit tests,
2. integration tests,
3. golden-file tests,
4. build validation.

All are required for merge.

## 8.2 Failure Triage Guidance

When golden tests fail:
- verify if change is intentional behavior change,
- if intentional: review and update baselines in same PR with rationale,
- if unintentional: fix regression and keep baselines unchanged.

---

## 9. Performance and Stability Guardrails in Tests

- avoid large real dump files in regular CI path,
- keep integration fixtures lightweight but semantically rich,
- mark heavy/extended tests separately if needed,
- use deterministic synthetic datasets for core contract tests.

---

## 10. Implementation Plan

## Step 1 — Test harness alignment
- establish folder structure and base helpers,
- add stable test data builders for findings/sections.

## Step 2 — Unit coverage expansion
- implement pipeline/config/reporting helper unit tests.

## Step 3 — Integration scenarios
- add end-to-end report generation tests for critical scenarios.

## Step 4 — Golden framework
- add golden compare utility with readable diffs,
- generate and commit initial baselines for all three formats.

## Step 5 — CI enforcement
- wire all test categories into CI,
- block merge on failing golden tests.

---

## 11. Acceptance Criteria

1. Unit tests cover pipeline, precedence, dedup, and wrapping rules.
2. Integration tests validate composed report flow and key invariants.
3. Golden tests exist for `markdown`, `text`, and `html` across required fixtures.
4. Tests assert no truncation of long values and no formatter-level dedup behavior.
5. CI runs all test categories and enforces pass status for merge.

---

## 12. Deliverables

- structured test suite under `DumpDetective.Tests`.
- golden fixture set and baseline files.
- helper utilities for deterministic output compare.
- CI pipeline updates for mandatory test execution.
- contributor guidance for updating golden baselines.

---

## 13. Exit Criteria

- Build is green.
- Unit, integration, and golden tests pass in CI.
- Regressions in report detail, dedup, truncation, or precedence are reliably detected.
- Ready to start `REFACTOR_SPEC_07_OBSERVABILITY_AND_PERF_GUARDRAILS.md`.
