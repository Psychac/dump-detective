# DumpDetective.Cli Critical Review

## Status
Architectural/code-structure review.

Validated against active source on 2026-05-30. Re-validated against active source on 2026-07-17. Re-validated a second time on 2026-07-17 with deeper cross-file verification (call-graph checks on dead-code claims, current file paths for every finding's evidence list).

## Implementation Status Update (2026-07-17, second pass)
Overall status unchanged: substantially remediated (phase objectives complete; structural polish opportunities remain). This pass corrects stale evidence paths left over from the first 2026-07-17 pass and adds one finding that call-graph verification surfaced.

Addressed in implementation (confirmed again this pass):
- capability/module-driven registration is real: `DumpDetective.Reporting.Capabilities.DefaultAnalyzerFeatureModuleCatalog` is instantiated directly in `Hosting/ServiceRegistration.cs` and registered as `IAnalyzerFeatureModuleCatalog`; the module list drives finding-generator and trend-comparer registration via `ActivatorUtilities.CreateInstance`, not hand-listed types.
- CLI reduced as a host shell relative to prior state — current folders: `Commands/`, `Configuration/`, `Console/`, `Diagnostics/`, `Execution/`, `Hosting/`, `Models/`, `Output/`, `Pipeline/`, `Services/`.
- `CliExceptions`, `ConsoleDiagnosticsSink`, `FileDiagnosticsSink` live in `Diagnostics/`; `ConfigurationResolver` lives in top-level `Configuration/`; `AnalyzerFilterService` and `AnalyzerExecutionService` live in `Execution/`; `IncidentContextFactory` lives in `Models/`.
- output writing lives in `Output/ReportOutputWriter.cs`.

Correction carried over from the first 2026-07-17 pass (still accurate):
- `DumpAnalysisService`, `SingleDumpOrchestrationService`, and `TrendOrchestrationService` still live in `Services/` (`Services/DumpAnalysisService.cs`, `Services/SingleDumpOrchestrationService.cs`, `Services/TrendOrchestrationService.cs`). Only `AnalyzerExecutionService`, `AnalyzerFilterService`, and `PerDumpExecutionService` are under `Execution/`. The CLI's top-level run-orchestration surface (Finding #1) remains physically co-located with `Services/` support code.

Correction to this document itself (found during this pass):
- The evidence lists under the original Finding #6 (`Services/ConfigurationResolver.cs`, `Services/AnalyzerFilterService.cs`, `Services/ConsoleDiagnosticsSink.cs`, `Services/FileDiagnosticsSink.cs`, `Services/IncidentContextFactory.cs`) describe a pre-move state. All five of those files have already relocated to `Configuration/`, `Execution/`, `Diagnostics/` (x2), and `Models/` respectively — the same moves already credited in "Addressed in implementation" above. Finding #6 is restated below with the actual current contents of `Services/`.

New finding since the last pass — dead capability-coverage validation path:
- `Services/Capabilities/AnalyzerFeatureModuleAdapter.cs` (`CreateResolvedModules`, `ComputeCoverage`) and `Services/Capabilities/AnalyzerFeatureModuleSpikeCatalog.cs` are, as previously noted, unreferenced by `Hosting/ServiceRegistration.cs`. Call-graph verification this pass additionally shows that `StartupValidator.ValidateFeatureModuleCoverage(...)` — the method that would consume an `AnalyzerFeatureModuleCoverage` produced by `AnalyzerFeatureModuleAdapter.ComputeCoverage` — has zero callers anywhere in `src/`. So this isn't just an orphaned adapter backstopping a test; it's a full three-hop capability-coverage-check feature (`ComputeCoverage` → `AnalyzerFeatureModuleCoverage` → `ValidateFeatureModuleCoverage`) that is wired together and exercised only by `AnalyzerFeatureModuleSpikeTests`, never invoked from `Program`/`ServiceRegistration`/`StartupValidator.Validate`. Either wire `ValidateFeatureModuleCoverage` into real startup validation against the Reporting-owned catalog, or delete the adapter, the spike catalog, and the unused validator method together.
- Confirmed still true: `Output/AnalysisSummaryFormatter.cs` and `Services/AnalysisSummaryFormatter.cs` are distinct types with the same name. This pass traced call sites directly: `SingleDumpOrchestrationService.cs` and `TrendOrchestrationService.cs` both `use DumpDetective.Cli.Output` and call the `Output` type's `FormatConfigSummary` directly. A call-graph query for callers of `Services.AnalysisSummaryFormatter` returns none — the `Services/` copy is confirmed dead, not just redundant. Safe to delete outright.

Remaining follow-on cleanup (unchanged from prior pass):
- `Services/` still mixes orchestration (`DumpAnalysisService`, `SingleDumpOrchestrationService`, `TrendOrchestrationService`, `StartupValidator`) with pure support code (`ExitCodes`), dead duplication (`AnalysisSummaryFormatter`), dead capability-coverage code (`Capabilities/*`), and config parsing helpers (`Configuration/*` — a second, differently-scoped `Configuration` namespace nested inside `Services`, distinct from the top-level `Configuration/ConfigurationResolver.cs`).
- moving the orchestration classes into `Execution/` would resolve the correction noted above and match the originally intended structure.
- the naming overlap between top-level `Configuration/ConfigurationResolver.cs` and `Services/Configuration/*` (`AnalyzerOptionsBuilder`, `CliConfigurationModels`, `ConfigurationParseHelpers`) still invites confusion about which folder owns configuration concerns.
- no direct unit tests were found for `DumpAnalysisService`, `SingleDumpOrchestrationService`, `TrendOrchestrationService`, or `Hosting/ServiceRegistration.cs` (confirmed again this pass via targeted search — zero hits under `tests/`); the highest-risk orchestration and wiring classes still lack focused unit tests.

## Scope
Project reviewed: `src/DumpDetective.Cli`

Focus areas:
- code structure
- class/service structure
- composition root health
- orchestration boundaries
- cleanup and refactor opportunities for a cleaner project

## Executive Summary
`DumpDetective.Cli` is carrying more than host responsibilities.

It currently acts as:
- CLI host
- dependency composition root
- application orchestration layer
- part of the analysis execution adapter layer
- home to a small amount of dead/orphaned capability-validation code left over from the module-catalog migration

That makes the project heavier than a CLI project should be. Note the shape of the problem has shifted since the original review: analyzer/report factory ownership and the parallel-list registration risk (originally Findings #2 and #3) are now genuinely resolved — the catalog lives in Reporting and is real. What's left is (a) orchestration classes that still sit in `Services/` instead of `Execution/`, and (b) dead code (a duplicate formatter, an unused capability-coverage adapter) that should be deleted rather than migrated.

## Primary Findings

### 1. `Cli` is acting as a second application layer
Severity: High

Evidence:
- `Services/DumpAnalysisService.cs`
- `Services/SingleDumpOrchestrationService.cs`
- `Services/TrendOrchestrationService.cs`
- `Execution/AnalyzerExecutionService.cs`

Why this is a problem:
- A CLI project should primarily translate command intent into application-service calls.
- Here, the CLI is coordinating execution mode selection, analyzer set resolution, runtime context construction, stage orchestration, insight execution, report construction, and output handling.
- That makes the project hard to reason about because host concerns and domain/application concerns are blended.

Refactor opportunity:
- Move run orchestration into an application-facing service owned outside the CLI shell, or at minimum relocate the three `Services/*OrchestrationService` classes into `Execution/` to match the CLI-adapter role the rest of `Execution/` already plays.
- Keep `Cli` focused on:
  - command parsing
  - config binding/validation
  - user-facing diagnostics
  - exit code mapping

### 2. `Services/` is a mixed-responsibility bucket, not a coherent layer
Severity: Medium (downgraded from High in the original review — see note)

Evidence, current contents of `src/DumpDetective.Cli/Services/`:
- `DumpAnalysisService.cs`, `SingleDumpOrchestrationService.cs`, `TrendOrchestrationService.cs` — orchestration
- `StartupValidator.cs` — startup validation
- `ExitCodes.cs` — pure constants
- `AnalysisSummaryFormatter.cs` — a dead forwarding shim (see Finding #4)
- `Capabilities/AnalyzerFeatureModuleAdapter.cs`, `Capabilities/AnalyzerFeatureModuleSpikeCatalog.cs` — unused capability-coverage code (see Finding #5)
- `Configuration/AnalyzerOptionsBuilder.cs`, `Configuration/CliConfigurationModels.cs`, `Configuration/ConfigurationParseHelpers.cs` — config parsing helpers, distinct from top-level `Configuration/ConfigurationResolver.cs`

Note on severity: the original review's Finding #2 was about `ServiceRegistration.cs` manually wiring dozens of analyzers/generators/comparers with a comment warning that parallel lists must stay in sync. That specific problem is resolved — registration now iterates `DefaultAnalyzerFeatureModuleCatalog`. What remains is a naming/organization problem: `Services/` no longer signals what it contains.

Why this is a problem:
- The folder mixes orchestration, a validator, constants, dead code, and two different flavors of "configuration" work under one name.
- A reader can't infer from the folder name which classes are safe to delete, which are load-bearing orchestration, and which duplicate something elsewhere.

Refactor opportunity:
- Move orchestration classes to `Execution/`.
- Delete the dead `AnalysisSummaryFormatter` shim and the unused `Capabilities/*` adapter (see Findings #4 and #5).
- Either fold `Services/Configuration/*` into the top-level `Configuration/` folder or rename one of the two to remove the ambiguity.

### 3. `AnalyzerExecutionService` mixes adaptation, policy shaping, and execution
Severity: Medium

Evidence:
- `Execution/AnalyzerExecutionService.cs`

Why this is a problem:
- This class derives thread sampling policy, adapts options to dump size, constructs `RuntimeAnalysisContext`, creates `RuntimeFacade`, builds diagnostics plumbing, and then executes the analysis pipeline.
- Those are several different responsibilities: option adaptation, runtime context assembly, execution dispatch.

Refactor opportunity:
- Split into: context builder, execution dispatcher, option adaptation helper.

### 4. Duplicate `AnalysisSummaryFormatter` — confirmed dead, not just redundant
Severity: Low, but concrete and zero-risk to fix

Evidence:
- `Output/AnalysisSummaryFormatter.cs` (real implementation, `internal static class`)
- `Services/AnalysisSummaryFormatter.cs` (thin one-method forwarding shim to the `Output` type)
- `SingleDumpOrchestrationService.cs` and `TrendOrchestrationService.cs` both `use DumpDetective.Cli.Output` and call `AnalysisSummaryFormatter.FormatConfigSummary` from that namespace directly — a call-graph query for callers of the `Services/` copy returns zero results.

Why this is a problem:
- It's dead duplication with an identical name in a different namespace, which is exactly the kind of thing that causes someone to edit the wrong copy later.

Refactor opportunity:
- Delete `Services/AnalysisSummaryFormatter.cs` outright. No caller migration needed — it already has none.

### 5. Orphaned capability-coverage validation path (adapter, spike catalog, and the validator method that would consume them)
Severity: Low/Medium — dead code, but non-trivial in size and shape

Evidence:
- `Services/Capabilities/AnalyzerFeatureModuleAdapter.cs` — `CreateResolvedModules`, `ComputeCoverage` (produces `AnalyzerFeatureModuleCoverage`)
- `Services/Capabilities/AnalyzerFeatureModuleSpikeCatalog.cs` — a hardcoded 3-module catalog tagged `phase2-spike` in comments
- `StartupValidator.ValidateFeatureModuleCoverage(AnalyzerFeatureModuleCoverage, bool, string)` — the only consumer shape for `ComputeCoverage`'s output
- `Hosting/ServiceRegistration.cs` wires `DefaultAnalyzerFeatureModuleCatalog` from Reporting directly and never touches any of the above
- `tests/DumpDetective.Tests/Unit/Architecture/AnalyzerFeatureModuleSpikeTests.cs` is the only caller of any of it

Why this is a problem:
- This isn't just an orphaned adapter — it's a fully-formed, three-piece feature (adapter → coverage record → validator method) that never actually runs in the product. `StartupValidator.ValidateFeatureModuleCoverage` has no callers anywhere in `src/`, so even if something did call `ComputeCoverage`, there's no wired path that would act on the result.
- It reads as production-shaped code (proper types, proper validator method signature) which makes it easy to mistake for an active guardrail when it is not.

Refactor opportunity:
- Decide one of two things and act on it: either wire `StartupValidator.ValidateFeatureModuleCoverage` into real startup validation against the real Reporting-owned catalog (computing coverage from `DefaultAnalyzerFeatureModuleCatalog` instead of the spike catalog), or delete `AnalyzerFeatureModuleAdapter.cs`, `AnalyzerFeatureModuleSpikeCatalog.cs`, `ValidateFeatureModuleCoverage`, and retarget/remove `AnalyzerFeatureModuleSpikeTests` accordingly.

### 6. The orchestration services are too concrete and stage-aware
Severity: Medium

Evidence:
- `Services/SingleDumpOrchestrationService.cs`
- `Services/TrendOrchestrationService.cs`
- `Pipeline/Stages/*`

Why this is a problem:
- `SingleDumpOrchestrationService` directly builds the stage list via `BuildStages` and instantiates `StagedPipelineRunner`/`InsightEngine` inline.
- `TrendOrchestrationService` (538 lines) contains detailed lifecycle and reporting assembly logic (`ExecutePipelineForDumpAsync`, `BuildSnapshot`, `PrintTrendDumpSummary`, `PrintTrendOverallSummary`, `PrintMemorySummary`) while also handling CLI-visible progress behavior in the same class.

This makes orchestration hard to reuse and hard to test in isolation.

Refactor opportunity:
- Build the pipeline externally and inject it.
- Treat orchestration as application policy, not as a CLI helper.
- Keep progress/reporting callbacks as adapters near the CLI surface; `TrendOrchestrationService` in particular should have its `Print*` methods split out into a dedicated presentation/summary type.

### 7. Critical orchestration and wiring surfaces still have no direct test coverage
Severity: Medium

Evidence:
- A search across `tests/` for `SingleDumpOrchestrationService`, `TrendOrchestrationService`, and `ServiceRegistration` returns zero results.

Why this is a problem:
- These files control wiring and execution behavior that will likely change during refactoring.
- Without focused tests, cleanup work (including the moves recommended in Findings #1, #2, and #6) will be slower and riskier to verify.

Refactor opportunity:
- Add wiring tests for `ServiceRegistration.BuildHost` and orchestration tests (with stubbed dependencies) for `SingleDumpOrchestrationService` and `TrendOrchestrationService` before doing the structural moves above.

## Structure Review

### What is good
- Top-level folders are readable: `Commands`, `Console`, `Hosting`, `Configuration`, `Diagnostics`, `Output`, `Execution` all map to a recognizable single responsibility.
- `Program.cs` is thin, which is good.
- The capability-module catalog genuinely lives in Reporting now; `ServiceRegistration.cs` is a small, iteration-driven file rather than a hand-maintained parallel-list file.

### What is not good enough
- `Services` is the one folder left that doesn't map to a single responsibility — it holds orchestration, a validator, constants, dead code, and a second `Configuration` sub-namespace.
- `Pipeline` is CLI-local even though much of the behavior (stage definitions, `StagedPipelineRunner`) is not CLI-specific.

### Cleanup opportunity
Aim for this mental model:
- `Program` and `Commands` define the entry surface.
- `Hosting` defines dependency setup only.
- `Execution` contains CLI adapters over application services, including the three orchestration classes currently in `Services/`.
- `Services/` either disappears entirely (contents distributed to `Execution/`, `Configuration/`, and deletions) or is narrowed to genuinely miscellaneous support code with nothing dead left in it.

## Class Structure Review

### `Program`
Assessment: Good — small and readable.

Keep: host creation, command parsing, exception-to-exit-code mapping.
Do not grow: no feature-specific decisions, no runtime orchestration logic.

### `DumpAnalysisService`
Assessment: Reasonable as a front-door application coordinator, but depends on too many downstream concerns (analyzer factory, finding generators, trend comparers, section builder factory, both orchestration services).

Recommendation: keep it as a front-door if desired, but make it depend on a single orchestration abstraction rather than multiple registry/factory surfaces.

### `SingleDumpOrchestrationService`
Assessment: Functionally clear, structurally overburdened.

Problem areas: builds the stage pipeline itself, instantiates `StagedPipelineRunner`/`InsightEngine` directly, owns console summary behavior (`PrintInsights`, `PrintMemorySummary`, `PrintDiagnosticsSummary`).

Recommendation: split application orchestration from CLI output summary generation; inject the pipeline/insight-engine instead of newing them up.

### `TrendOrchestrationService`
Assessment: Useful but too wide — 538 lines covering per-dump pipeline execution, heartbeat/progress handling, snapshot assembly, trend comparison, report document generation, output writing, and diagnostic summary printing all in one class.

Recommendation: split into a trend execution coordinator, a progress adapter, and a trend report/summary presentation adapter (`PrintTrendDumpSummary`, `PrintTrendOverallSummary`, `PrintMemorySummary` are strong candidates to extract as-is).

### `StartupValidator`
Assessment: Contains one confirmed-live path (`Validate`, `ValidateRegistrations`, `ValidateNameCoverage`, and the option-specific validators) and one confirmed-dead path (`ValidateFeatureModuleCoverage`, called by nothing in `src/`).

Recommendation: resolve per Finding #5 — either wire the dead path into `Validate` against the real Reporting catalog, or delete it.

### `AnalysisSummaryFormatter` (Services copy)
Assessment: dead forwarding shim, confirmed zero callers.

Recommendation: delete.

### `AnalyzerFeatureModuleAdapter` / `AnalyzerFeatureModuleSpikeCatalog`
Assessment: dead outside of one architecture test.

Recommendation: resolve per Finding #5.

## Concrete Refactor Opportunities

### Opportunity 1: Move orchestration out of `Services/` into `Execution/`
Why: this is the last structural piece of the original "second application layer" finding that hasn't been done — the catalog/factory ownership pieces are already resolved.

What to do: move `DumpAnalysisService`, `SingleDumpOrchestrationService`, `TrendOrchestrationService` into `Execution/`.

Expected outcome: `Services/` shrinks to `StartupValidator`, `ExitCodes`, and the `Configuration/*` helpers — a much more defensible bucket.

### Opportunity 2: Delete confirmed-dead code
Why: two independent dead-code findings (Finding #4, Finding #5) are now backed by call-graph verification, not just suspicion.

What to do: delete `Services/AnalysisSummaryFormatter.cs`; delete or wire up `Services/Capabilities/AnalyzerFeatureModuleAdapter.cs`, `AnalyzerFeatureModuleSpikeCatalog.cs`, and `StartupValidator.ValidateFeatureModuleCoverage`; retarget or remove `AnalyzerFeatureModuleSpikeTests` accordingly.

Expected outcome: no more same-named types in different namespaces; no more code that looks production-shaped but never runs.

### Opportunity 3: Split mixed orchestration coordinators from CLI-only adapters
Why: progress rendering, console summaries, and exit-code mapping are CLI concerns; stage planning, trend snapshot construction, and report assembly are not.

What to do: split `SingleDumpOrchestrationService` and `TrendOrchestrationService` so only UI/terminal concerns stay CLI-side.

Expected outcome: smaller orchestration classes, easier testability.

### Opportunity 4: Add tests before moving structure
Why: `ServiceRegistration`, `SingleDumpOrchestrationService`, and `TrendOrchestrationService` still have zero direct tests, and Opportunity 1 will touch all three.

What to test first: host wiring for key command paths; single-dump orchestration happy path with stubs; trend orchestration happy path with stubs.

## Recommended Cleanup Order

### Step 1 (done)
Added `ServiceRegistrationTests` (host resolves every registered service, singleton lifetimes) and `SingleDumpOrchestrationServiceTests` / `TrendOrchestrationServiceTests` (dump-load failure propagation). Remaining gap: no happy-path test for either orchestration service — both current tests only cover the `IDumpLoader` failure branch. Add happy-path coverage with stubbed analyzers/report writers before attempting Opportunity 1's move into `Execution/`.

### Step 2 (done)
Deleted `Services/AnalysisSummaryFormatter.cs` (a redundant forwarding shim over `Output/AnalysisSummaryFormatter.cs`; both callers already imported the `Output` namespace, so removal was a no-op behavior-wise) and moved its test to `tests/Unit/Output/`. Removed the `AnalyzerFeatureModuleAdapter`/`AnalyzerFeatureModuleSpikeCatalog`/`ValidateFeatureModuleCoverage` path entirely: it duplicated the coverage checks `StartupValidator.ValidateRegistrations` already performs (by analyzer name) with a heavier type-based mechanism whose computed results (`resolvedCoverage`, `resolvedCoverageValidated`, `spikeCoverageValidated`) were never consumed beyond the validation side effect. Deleted `AnalyzerFeatureModuleSpikeTests.cs` accordingly. Note: `DefaultAnalyzerFeatureModuleCatalog`/`IAnalyzerFeatureModuleCatalog` in `DumpDetective.Reporting/Capabilities` is a separate, still-unreferenced catalog (zero callers) left out of scope for this step.

### Step 3 (done)
Moved `DumpAnalysisService`, `SingleDumpOrchestrationService`, `TrendOrchestrationService` into `Execution/`.

### Step 4 (done)
Split orchestration from console/output adapters: extracted `SingleDumpConsolePresenter` / `TrendConsolePresenter` for `Print*` methods and `SingleDumpStageFactory` for stage-building logic, registered the new factory in `ServiceRegistration`, and fixed a latent `TrendReportData` construction bug (missing `Timeline`, misassigned `Snapshots`) surfaced while wiring the split through.

### Step 5
Resolve the `Configuration/` vs `Services/Configuration/` naming overlap.

## Suggested Target Shape

### Desired responsibility map
- `Program.cs`: host bootstrap and exit policy
- `Commands/*`: command model and argument mapping
- `Hosting/*`: container and configuration bootstrap only
- `Console/*`: terminal UX only
- `Execution/*`: CLI adapters that call application orchestration interfaces, including today's `Services/*OrchestrationService` classes
- `Services/*`: narrowed to `StartupValidator`, `ExitCodes`, and config-parsing helpers, with no dead code

### Responsibilities that should leave this project
- direct insight-engine ownership by orchestration classes
- the dead capability-coverage validation path (either promote it to real or remove it — it should not remain in limbo)

## What to preserve
- thin `Program`
- clear command entry surface
- explicit console UX behavior
- cancellation and exception mapping behavior
- the real, working capability-module catalog wiring in `ServiceRegistration.cs` (this is a genuine improvement from the original review and should not be disturbed by the cleanup above)

## What not to do
- Do not rewrite command parsing first.
- Do not create many tiny abstractions unless they clarify ownership.
- Do not move hot-path analysis internals into CLI-facing layers.
- Do not "fix" the dead capability-coverage code by quietly wiring it in without deciding whether it should validate against the real Reporting catalog — wiring dead code straight in without adapting it to the current catalog would just move the staleness risk rather than remove it.

## Bottom Line
The biggest structural risks from the original review — the analyzer/report factory sprawl and the manually-synchronized parallel registration lists — are genuinely fixed. What's left is smaller and more mechanical: three orchestration classes that haven't yet moved from `Services/` to `Execution/`, a confirmed-dead duplicate formatter, and a confirmed-dead capability-coverage validation path that looks production-ready but is invoked by nothing outside one test file. None of these require redesign — they require a move, two deletions (or one deletion plus one deliberate wiring decision), and a handful of tests around the orchestration classes before they're relocated.
