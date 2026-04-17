# DumpDetective — Spec 03: Analyzer Contracts and Async Pipeline

> **Phase:** Iteration 3
> **Prerequisite:** `REFACTOR_SPEC_01_SOLUTION_STRUCTURE.md`, `REFACTOR_SPEC_02_FULL_REWRITE_EXECUTION_PLAN.md`
> **Target Runtime:** `.NET 10`

---

## 1. Goal

Define stable analyzer contracts and implement an async, cancellation-aware analysis pipeline that is deterministic, observable, and safe to extend.

---

## 2. Scope

### In Scope
- Analyzer contract standardization.
- `AnalysisContext` extraction and enrichment.
- Async `AnalysisPipeline` orchestration.
- Analyzer execution result model (`AnalyzerRunResult`) and status policy.
- Cancellation, failure policy, timing, and diagnostics hooks.

### Out of Scope
- CLI command wiring details (Spec 04).
- Reporting composition/formatter boundary work (Spec 05).
- Snapshot/golden report tests (Spec 06).

---

## 3. Contract Design

## 3.1 `IAnalyzer` Contract

Location: `src/DumpDetective.Core/Abstractions/IAnalyzer.cs`

Required members:
- `string Name { get; }`
- `string Category { get; }`
- `ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)`

Optional (recommended):
- `IReadOnlyCollection<string> Tags { get; }`
- `int Order { get; }` (default 0)

### Rules
- `Name` must be stable and unique across analyzers.
- `Category` must be canonical (e.g., `Memory`, `Threads`, `GC`, `Handles`, `Events`, `Locks`, `Modules`, `Crash`, `Hang`).
- Analyzer must not write directly to output streams.
- Analyzer must be deterministic for the same input dump and options.

## 3.2 `AnalyzerDomainResult`

Location: `src/DumpDetective.Core/Models/AnalyzerDomainResult.cs`

Required fields:
- `string AnalyzerName`
- `string Category`
- `IReadOnlyCollection<InsightFinding> Findings`
- `IReadOnlyDictionary<string, object?> Metrics`
- `IReadOnlyCollection<string> Warnings`

### Rules
- Keep raw evidence data in structured form (no formatter-oriented text shaping).
- `Findings` should include fingerprint-ready identity attributes.

## 3.3 `AnalyzerRunResult`

Location: `src/DumpDetective.Core/Models/AnalyzerRunResult.cs`

Required fields:
- `string AnalyzerName`
- `AnalyzerExecutionStatus Status` (`Success`, `Failed`, `Skipped`, `Canceled`)
- `TimeSpan Duration`
- `AnalyzerDomainResult? Result`
- `string? ErrorMessage`
- `string? ErrorType`

---

## 4. `AnalysisContext` Specification

Location: `src/DumpDetective.Analysis/Pipeline/AnalysisContext.cs`

Required members:
- Dump/runtime handles needed by analyzers.
- Typed options required by analyzers (read-only).
- Shared cache handles (`HeapAnalysisCache`, counters, helper services).
- Diagnostics sink abstraction.

### Rules
- Immutable context object after creation.
- No analyzer-specific mutable state in context.
- `CancellationToken` must not be stored; it is passed per-call.

---

## 5. `AnalysisPipeline` Specification

Location: `src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs`

## 5.1 Responsibilities
1. Resolve analyzers from DI registry.
2. Sort analyzers by `Order`, then by `Name` for deterministic fallback.
3. Execute analyzers sequentially (default) with cancellation checks before each execution.
4. Capture per-analyzer timing and status.
5. Apply failure policy.
6. Return immutable aggregate collection of `AnalyzerRunResult`.

## 5.2 Failure Policy

Config source: `DiagnosticsOptions.ContinueOnAnalyzerFailure`.

Behavior:
- If `true`: capture failure result, continue next analyzer.
- If `false`: capture failure result, stop pipeline and return partial completed results.

## 5.3 Cancellation Policy

- If cancellation requested before analyzer starts: mark as `Skipped` or stop immediately (implementation decision must be consistent).
- If analyzer throws `OperationCanceledException`: mark current analyzer `Canceled`, stop pipeline.

## 5.4 Diagnostics and Telemetry

Pipeline emits diagnostics events:
- `AnalyzerStarted`
- `AnalyzerCompleted`
- `AnalyzerFailed`
- `AnalyzerCanceled`

Each event includes:
- analyzer name/category
- elapsed duration (if available)
- key metrics (`object scans`, `cache hits/misses` when available)

---

## 6. Dependency and Namespace Requirements

- `IAnalyzer` and shared contracts remain in `Core`.
- `AnalysisPipeline` and `AnalysisContext` remain in `Analysis`.
- File-scoped namespaces only.
- Nullable enabled.

---

## 7. Implementation Plan (Step-by-Step)

## Step 1 — Core contracts
- Finalize `IAnalyzer` in `Core/Abstractions`.
- Finalize `AnalyzerDomainResult` and `AnalyzerRunResult` models in `Core/Models`.
- Add/align `AnalyzerExecutionStatus` enum.

## Step 2 — Analysis context
- Extract and normalize `AnalysisContext` into `Analysis/Pipeline`.
- Inject caches, options, and diagnostics sink.

## Step 3 — Pipeline
- Implement async `AnalysisPipeline` execution loop.
- Add timing capture via `Stopwatch`.
- Add failure/cancellation handling.

## Step 4 — Analyzer migration
- Update each analyzer to implement contract and async signature.
- Ensure each analyzer returns structured findings + metrics.

## Step 5 — Validation
- Build and run analyzer-focused tests.
- Verify deterministic ordering and policy behavior.

---

## 8. Acceptance Criteria

1. All analyzers implement the standardized async contract.
2. Pipeline returns `AnalyzerRunResult` with accurate status and timing per analyzer.
3. `ContinueOnAnalyzerFailure` behavior is fully respected.
4. Cancellation is propagated and reflected as `Canceled` status.
5. Build is green with no dependency rule violations.

---

## 9. Test Plan

## 9.1 Unit Tests
- Pipeline orders analyzers by `Order` then `Name`.
- Failure policy (`continue/stop`) produces expected run result sequence.
- Cancellation token triggers proper terminal behavior.
- Exception mapping sets `Status=Failed` and error fields.

## 9.2 Integration Tests
- Minimal end-to-end run with 2–3 analyzers and synthetic data.
- Verify metrics and diagnostics events emitted.

## 9.3 Non-Regression Tests
- Existing analyzer outputs remain semantically equivalent (same findings/evidence, no data loss).

---

## 10. Risks and Mitigations

1. **Risk:** Contract migration breaks analyzer behavior  
   **Mitigation:** adapter shims for transitional analyzers + incremental migration.

2. **Risk:** Inconsistent cancellation handling across analyzers  
   **Mitigation:** enforce token checks in analyzer templates and code review checklist.

3. **Risk:** Hidden ordering dependencies  
   **Mitigation:** explicit `Order` values and deterministic fallback sort.

---

## 11. Deliverables

- `IAnalyzer` finalized in `Core`.
- `AnalysisContext` extracted/finalized in `Analysis/Pipeline`.
- `AnalysisPipeline` async orchestration complete.
- Analyzer migration to new contract completed.
- Unit/integration tests for pipeline behavior added and passing.

---

## 12. Exit Criteria

- Green build.
- Relevant tests passing.
- Pipeline behavior deterministic and policy-compliant.
- Ready to start `REFACTOR_SPEC_04_CLI_HOSTING_AND_COMMAND_MODEL.md`.
