# DumpDetective — Full Detailed Refactor Spec (Iterative)

> **Status:** Draft v1
> **Target Runtime:** `.NET 10`
> **Execution Model:** Multi-iteration (small, verifiable slices)
> **Primary Goal:** Rebuild architecture for long-term maintainability while preserving analyzer depth and report usefulness.

---

## 1. Purpose

This spec defines a complete, implementation-ready refactor roadmap from current state to a stable multi-project architecture:

- `DumpDetective.Core`
- `DumpDetective.Analysis`
- `DumpDetective.Reporting`
- `DumpDetective.Cli`
- `DumpDetective.Tests`

It is designed to be executed incrementally with green builds and test validation at each iteration.

---

## 2. Non-Negotiable Requirements

1. **Configuration precedence**: if JSON config exists, it takes precedence; CLI args are fallback only.
2. **Actionability over brevity**: report output must retain detailed analyzer evidence and diagnostics.
3. **No duplicate sections at output source**: deduplicate in composition/generation layer, not formatter aliases.
4. **No truncation of long names in tables**: wrap long values instead of trimming.
5. **One-way dependency flow**: `Cli -> Reporting/Analysis -> Core`.
6. **Async-first pipeline** with cancellation support.

---

## 3. Architecture Contract

## 3.1 Project Responsibilities

### `DumpDetective.Core`
- Domain contracts, immutable models, shared abstractions, options, shared formatting helpers.
- No orchestration logic.
- No CLI-specific behavior.

### `DumpDetective.Analysis`
- Analyzer implementations.
- Pipeline orchestration and execution context.
- Heap/cache/diagnostic support services.

### `DumpDetective.Reporting`
- Report composition service(s).
- Analyzer rendering adapters/printers.
- Output formatters (`Text`, `Markdown`, `Html`) with rendering-only behavior.

### `DumpDetective.Cli`
- `System.CommandLine` wiring.
- Host/DI bootstrapping.
- Input resolution, config loading, user interaction.

### `DumpDetective.Tests`
- Unit, integration, and golden-file tests.

## 3.2 Dependency Rules

Allowed references:
- `Analysis -> Core`
- `Reporting -> Core`, `Reporting -> Analysis`
- `Cli -> Core`, `Cli -> Analysis`, `Cli -> Reporting`
- `Tests -> all runtime projects`

Disallowed examples:
- `Core -> Analysis/Reporting/Cli`
- `Analysis -> Cli`
- `Reporting -> Cli`

---

## 4. Domain and Contract Spec

## 4.1 Core Models

Must exist under `Core/Models`:
- `InsightFinding`
- `FindingSeverity`
- `FindingFingerprint`
- `AnalysisSnapshot`
- `AnalyzerDomainResult`
- `AnalyzerRunResult`
- trend-related models and contracts

### Rules
- Use file-scoped namespaces.
- Enable nullable reference types.
- Prefer immutable records for result models where practical.
- Separate “raw evidence” from “formatted text” to avoid data loss.

## 4.2 Analyzer Abstractions

`IAnalyzer` contract should include:
- Identity (`Name`, `Category`, optional tags)
- `AnalyzeAsync(AnalysisContext, CancellationToken)`
- Deterministic output contract (`AnalyzerDomainResult`)

`IAnalyzerReporter` should convert analyzer result data to report sections without analyzer side effects.

---

## 5. Analysis Pipeline Spec

## 5.1 Pipeline Responsibilities

`AnalysisPipeline` must:
1. Resolve analyzer list from DI/registry.
2. Execute analyzers in configured order.
3. Capture timing and status per analyzer.
4. Aggregate deterministic `AnalyzerRunResult` collection.
5. Honor cancellation and fail-fast settings.

## 5.2 Failure Policy

For each analyzer:
- Capture exception details into diagnostics channel.
- Continue or stop based on policy option (`ContinueOnAnalyzerFailure`).
- Mark analyzer result status explicitly (`Success`, `Failed`, `Skipped`).

## 5.3 Performance Support

- Centralized heap cache and scan counter.
- Optional memoization for expensive traversal primitives.
- Emit object-scan and cache metrics.

---

## 6. Reporting Spec (Critical)

## 6.1 Composition vs Rendering Boundary

`ReportBuilder` composes report sections from domain data only.

- It must not contain formatter-specific text shaping.
- It must not truncate data.
- It must enforce canonical section keys and source-level deduplication.

`IReportFormatter` implementations only render provided section models.

## 6.2 Canonical Section Strategy

Every section must include:
- stable `SectionKey`
- display title
- severity/priority metadata
- structured payload and narrative summary

Duplicate candidates (e.g., same fingerprint/category/title) are merged in composition layer with full evidence preserved.

## 6.3 Formatter Rules

- Markdown/Text/HTML all render from same section model.
- Table renderer wraps long strings; never drops characters.
- All formatters must preserve complete findings and remediation hints.

---

## 7. Configuration and Options Spec

## 7.1 Sources

1. JSON file (`appsettings` / explicit config path)
2. CLI arguments (fallback when config is missing)

## 7.2 Validation

Validate options at startup:
- numeric ranges
- required paths
- mutually exclusive options

Invalid config should produce actionable error output with exact field names.

## 7.3 Option Groups

Expected strongly-typed option classes:
- `MemoryLeakOptions`
- `ReferenceChainOptions`
- `EventLeakOptions`
- `DiagnosticsOptions`
- `ReportOptions`

---

## 8. CLI Spec

## 8.1 Command Design

`RootCommandBuilder` should provide:
- input dump path
- output format and destination
- analyzer toggles/filtering
- diagnostics verbosity
- config path

## 8.2 Host Bootstrapping

`ServiceRegistration` must:
- register all analyzers
- register report composition and formatters
- register options binding and validation

---

## 9. Testing Spec

## 9.1 Unit Tests

- Analyzer-specific logic and edge cases.
- Section composition dedup behavior.
- Option precedence and validation.

## 9.2 Integration Tests

- End-to-end pipeline from dump input to report output.
- Verify no duplicate sections at source.
- Verify detailed evidence is retained.

## 9.3 Golden-File Tests

Per formatter (`md`, `txt`, `html`):
- Expected output snapshots.
- Checks for wrapped long names and full data retention.

---

## 10. Iterative Delivery Plan

## Iteration 0 — Baseline and Guardrails

### Deliverables
- Build and test baseline captured.
- Create architecture decision records (ADRs) for dependency rules and reporting boundary.

### Exit Criteria
- Green build.
- Existing behavior documented.

---

## Iteration 1 — Project Structure + Core Move

### Deliverables
- Create and wire project structure per solution spec.
- Move core models/interfaces/utilities.
- Namespace normalization.

### Exit Criteria
- Green build.
- No behavior change.

---

## Iteration 2 — Options + Config Precedence

### Deliverables
- Strongly typed options classes.
- JSON-first, CLI-fallback precedence implemented.
- Startup validation and diagnostics.

### Exit Criteria
- Tests prove precedence behavior.
- Misconfiguration errors are actionable.

---

## Iteration 3 — Analysis Pipeline Extraction

### Deliverables
- Async `AnalysisPipeline` + `AnalysisContext`.
- Analyzer execution policy and status capture.
- Cancellation propagation.

### Exit Criteria
- Analyzer orchestration moved out of CLI/service glue.
- Pipeline tests pass.

---

## Iteration 4 — Reporting Boundary Hardening

### Deliverables
- `ReportBuilder` strictly composes from domain models.
- `IReportFormatter` and formatter implementations aligned.
- Source-level section dedup implemented.

### Exit Criteria
- No formatter-level dedup hacks.
- Same canonical sections rendered in all formats.

---

## Iteration 5 — Printers/Renderers Split

### Deliverables
- Analyzer-specific printers under `Reporting/Printers`.
- `AnalyzerReportRenderer` and `TrendReportComposer` separation.

### Exit Criteria
- Adding new analyzer requires no edits to existing formatter internals.

---

## Iteration 6 — Observability + Diagnostics

### Deliverables
- Analyzer timing, scan metrics, cache metrics.
- Diagnostic mode output improvements.

### Exit Criteria
- Performance hotspots traceable per analyzer.

---

## Iteration 7 — Test Expansion + Hardening

### Deliverables
- Golden-file coverage for all output formats.
- Integration fixtures for common dump scenarios.

### Exit Criteria
- Stable outputs with snapshot approval.
- Regression confidence for future analyzer additions.

---

## 11. Definition of Done (Final)

- All projects target `.NET 10` and build cleanly.
- Dependency direction violations eliminated.
- `ReportBuilder` composition-only rule enforced.
- Source-level dedup and long-name wrapping validated.
- Config precedence and option validation fully tested.
- End-to-end tests and golden files passing.

---

## 12. Risks and Mitigations

1. **Risk:** behavior drift during moves  
   **Mitigation:** incremental moves + baseline snapshots per iteration.

2. **Risk:** formatter regressions  
   **Mitigation:** golden-file tests per output format.

3. **Risk:** performance regressions from richer output  
   **Mitigation:** metric instrumentation and benchmark gate for hotspots.

4. **Risk:** analyzer contract churn  
   **Mitigation:** stable `IAnalyzer` + adapter layer for legacy analyzers.

---

## 13. Next Spec Pack (Proposed)

- `REFACTOR_SPEC_03_ANALYZER_CONTRACTS_AND_PIPELINE.md`
- `REFACTOR_SPEC_04_CLI_HOSTING_AND_COMMAND_MODEL.md`
- `REFACTOR_SPEC_05_REPORTING_BOUNDARY_AND_FORMATTERS.md`
- `REFACTOR_SPEC_06_TEST_STRATEGY_AND_GOLDEN_FILES.md`
- `REFACTOR_SPEC_07_OBSERVABILITY_AND_PERF_GUARDRAILS.md`

These can be authored and executed one by one in subsequent iterations.
