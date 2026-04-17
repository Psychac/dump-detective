# DumpDetective — Spec 04: CLI Hosting and Command Model

> **Phase:** Iteration 4
> **Prerequisite:** `REFACTOR_SPEC_01_SOLUTION_STRUCTURE.md`, `REFACTOR_SPEC_02_FULL_REWRITE_EXECUTION_PLAN.md`, `REFACTOR_SPEC_03_ANALYZER_CONTRACTS_AND_PIPELINE.md`
> **Target Runtime:** `.NET 10`

---

## 1. Goal

Define a stable CLI architecture for command parsing, configuration precedence, host bootstrapping, and execution orchestration.

Primary outcomes:
- consistent `System.CommandLine` experience,
- strict JSON-first configuration precedence,
- predictable DI composition,
- actionable and detailed execution diagnostics.

---

## 2. Scope

### In Scope
- `Program.cs` entry flow.
- `Commands/RootCommandBuilder.cs` command + option model.
- `Hosting/ServiceRegistration.cs` DI registration.
- CLI-to-options binding and validation behavior.
- Execution service boundaries (`DumpAnalysisService`, `DumpLoader`).

### Out of Scope
- analyzer implementation internals (Spec 03).
- report composition and formatter architecture details (Spec 05).
- full golden output testing (Spec 06).

---

## 3. CLI Architecture Contract

## 3.1 Component Responsibilities

### `Program.cs`
- bootstrap host,
- resolve command pipeline,
- execute root command handler,
- map unhandled exceptions to exit codes.

### `Commands/RootCommandBuilder.cs`
- declare command options/arguments,
- bind invocation inputs into a command request model,
- delegate execution to application service.

### `Hosting/ServiceRegistration.cs`
- register analyzers, pipeline, reporting services, formatters, options, diagnostics,
- centralize dependency lifetime configuration.

### `Services/DumpAnalysisService.cs`
- coordinate dump loading + analysis + reporting,
- enforce precedence and runtime guardrails,
- return execution result model.

### `Services/DumpLoader.cs`
- load dump/runtime resources,
- provide clear diagnostics on invalid or unsupported dump inputs.

---

## 4. Command Model Specification

## 4.1 Root Command

Command shape must support:
- dump input path,
- optional output file path,
- output format (`text|markdown|html`),
- analyzer include/exclude filters,
- diagnostics verbosity,
- config file path,
- cancellation-friendly execution.

## 4.2 Request DTO

Create a single immutable request model (e.g., `AnalysisCommandRequest`) that carries parsed CLI intent.

Minimum fields:
- `string DumpPath`
- `string? OutputPath`
- `ReportFormat? OutputFormat`
- `string? ConfigPath`
- `IReadOnlyCollection<string> IncludeAnalyzers`
- `IReadOnlyCollection<string> ExcludeAnalyzers`
- `bool DiagnosticMode`

---

## 5. Configuration Precedence (Non-Negotiable)

## 5.1 Resolution Order

1. Resolve config file path:
   - explicit `--config` if provided,
   - fallback default path(s) if present.
2. If config exists and loads successfully:
   - use config values as primary source.
   - use CLI only for values not provided in config.
3. If config missing:
   - use CLI values.

## 5.2 Validation

- validate merged runtime options before analysis starts,
- fail fast with actionable field-level messages,
- do not proceed with partial/invalid option state.

## 5.3 Error Handling

- invalid config format: clear parse error with file path + offending section.
- unknown analyzer names in filters: explicit warning or validation failure based on strictness setting.

---

## 6. Hosting and DI Specification

## 6.1 Required Registrations

`ServiceRegistration` must register:
- all analyzers (`IAnalyzer` implementations),
- `AnalysisPipeline`, `AnalysisContext` dependencies,
- report composition and formatters,
- output writer services,
- options binding + validation services,
- diagnostics and timing sinks,
- command handler services.

## 6.2 Lifetime Guidance

- singleton: stateless services, formatters, renderer helpers.
- scoped/transient: services holding run-specific execution state.
- avoid singletons for mutable run data.

## 6.3 Startup Validation

At app startup (or command invocation start):
- validate options classes,
- verify required service graph can resolve,
- emit startup diagnostics when `DiagnosticMode=true`.

---

## 7. Execution Flow Specification

1. Parse CLI input.
2. Build `AnalysisCommandRequest`.
3. Resolve config and merge using precedence rules.
4. Validate runtime options.
5. Load dump via `DumpLoader`.
6. Execute `AnalysisPipeline`.
7. Compose report model.
8. Render selected format.
9. Write output (console and/or file).
10. Return deterministic exit code.

---

## 8. Exit Codes

Standardize exit codes:
- `0`: success.
- `1`: validation/configuration failure.
- `2`: dump loading failure.
- `3`: analysis pipeline failure.
- `4`: output/write failure.
- `130`: canceled (CTRL+C or token cancellation).

---

## 9. UX and Diagnostics Requirements

- CLI errors must be concise but actionable.
- Diagnostic mode must include:
  - resolved config source,
  - active analyzer list,
  - pipeline duration summary,
  - warning/error diagnostics.
- default mode should still preserve detailed analyzer findings in output.

---

## 10. Implementation Plan

## Step 1 — Command surface
- create/refactor `RootCommandBuilder` with complete option set.
- map parser results to `AnalysisCommandRequest`.

## Step 2 — Program bootstrapping
- simplify `Program.cs` to host + invoke command pipeline.
- centralize exception-to-exit-code mapping.

## Step 3 — Service registration
- define analyzer and formatter registration conventions.
- bind and validate options.

## Step 4 — Precedence and merge
- implement config-first merge strategy in CLI execution path.
- add diagnostic output for source resolution.

## Step 5 — Validation and cancellation
- enforce pre-run validation.
- wire cancellation through all execution services.

---

## 11. Acceptance Criteria

1. JSON config takes priority over CLI; CLI fills gaps only when config exists.
2. Missing config falls back to CLI-only behavior.
3. Root command can execute full analysis flow end-to-end.
4. DI graph resolves without runtime registration gaps.
5. Exit codes are consistent with failure category.
6. Diagnostic mode provides actionable execution details.

---

## 12. Test Plan

## 12.1 Unit Tests
- command parsing maps to request model correctly.
- precedence merge logic (config-first, CLI-fallback) verified.
- validation failure returns expected exit code.
- exception mapping to exit codes verified.

## 12.2 Integration Tests
- run with config file + CLI overrides (gap-fill only).
- run without config (CLI only).
- run with invalid dump path.
- run canceled flow.

## 12.3 Non-Regression Tests
- report still contains detailed findings and no data truncation introduced by CLI layer.

---

## 13. Risks and Mitigations

1. **Risk:** Hidden precedence regressions  
   **Mitigation:** dedicated precedence tests and explicit source-tracing diagnostics.

2. **Risk:** DI drift as analyzers grow  
   **Mitigation:** registration conventions + startup service validation.

3. **Risk:** CLI complexity growth  
   **Mitigation:** keep command-to-request mapping thin and move logic to services.

---

## 14. Deliverables

- `Program.cs` bootstrapping aligned to host-driven flow.
- `Commands/RootCommandBuilder.cs` command model stabilized.
- `Hosting/ServiceRegistration.cs` complete registration policy.
- Config precedence merge and validation behavior implemented.
- CLI-focused unit/integration tests added and passing.

---

## 15. Exit Criteria

- Build is green.
- CLI tests pass.
- Config precedence behavior proven.
- Ready to start `REFACTOR_SPEC_05_REPORTING_BOUNDARY_AND_FORMATTERS.md`.
