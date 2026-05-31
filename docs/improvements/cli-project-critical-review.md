# DumpDetective.Cli Critical Review

## Status
Architectural/code-structure review.

Validated against active source on 2026-05-30.

## Implementation Status Update (2026-05-30)
Overall status: Substantially remediated (phase objectives complete; structural polish opportunities remain, but the broad `Services/` catch-all has now been split down to support helpers).

Addressed in implementation:
- capability/module-driven registration adopted and validated via architecture guardrails
- analyzer/section/report factory ownership moved out of CLI to appropriate owning projects
- CLI reduced as host shell relative to prior state (composition ownership significantly reduced)
- execution/path guardrails and baseline harness validation are in place
- execution coordinators moved into `Execution/`:
  - `AnalyzerExecutionService`
  - `PerDumpExecutionService`
  - `DumpAnalysisService`
  - `SingleDumpOrchestrationService`
  - `TrendOrchestrationService`
- output writing moved into `Output/`:
  - `ReportOutputWriter`
- host wiring now covers the relocated execution/orchestration services, the pipeline factory, the trend report assembly service, `StagedPipelineRunner`, and `InsightEngine`

Remaining follow-on cleanup:
- `Services/` is now mostly support code (`ConfigurationResolver`, `StartupValidator`, `ExitCodes`, `CliExceptions`, filters, diagnostics sinks, and resolution helpers)
- the remaining support helpers can be split into narrower folders such as `Configuration`, `Diagnostics`, `Support`, or `Policy`
- additional direct tests for specific support classes can still improve refactor safety

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
- analyzer feature registry
- report builder registry
- application orchestration layer
- part of the analysis execution adapter layer

That makes the project heavier than a CLI project should be and is the main reason the project feels structurally crowded.

The project is not messy because the code is random. It is messy because too much architectural ownership has accumulated here.

## Primary Findings

### 1. `Cli` is acting as a second application layer
Severity: High

Evidence:
- `Execution/DumpAnalysisService.cs`
- `Execution/SingleDumpOrchestrationService.cs`
- `Execution/TrendOrchestrationService.cs`
- `Execution/AnalyzerExecutionService.cs`

Why this is a problem:
- A CLI project should primarily translate command intent into application-service calls.
- Here, the CLI is coordinating execution mode selection, analyzer set resolution, runtime context construction, stage orchestration, insight execution, report construction, and output handling.
- That makes the project hard to reason about because host concerns and domain/application concerns are blended.

Refactor opportunity:
- Move run orchestration into an application-facing service owned outside the CLI shell.
- Keep `Cli` focused on:
  - command parsing
  - config binding/validation
  - user-facing diagnostics
  - exit code mapping

### 2. The composition root is overloaded and fragile
Severity: High

Evidence:
- `Hosting/ServiceRegistration.cs`

Why this is a problem:
- `ServiceRegistration` manually wires a very large number of analyzers, finding generators, trend comparers, formatters, and factories.
- The file contains an explicit comment warning that multiple lists must remain in sync.
- This is a structural drift indicator, not just a style issue.

Refactor opportunity:
- Replace manual parallel registration with a feature-module or capability-descriptor model.
- The CLI should register modules, not enumerate every analyzer-adjacent component itself.

### 3. Factory ownership is in the wrong project
Severity: High

Evidence:
- `Services/DefaultAnalyzerFactory.cs`
- `Services/DefaultSectionBuilderFactory.cs`
- `Services/ReportBuilderFacade.cs`

Why this is a problem:
- The analyzer catalog belongs conceptually to the analysis side.
- The section-builder catalog belongs conceptually to the reporting side.
- The report builder facade lives in `Cli`, but it is a reporting/application service, not a CLI-specific abstraction.

This means the CLI project knows too much about both analysis features and report composition details.

Refactor opportunity:
- Move analyzer-factory ownership out of `Cli`.
- Move section-builder resolution out of `Cli`.
- Move report-building facade ownership to Reporting or an application layer.

### 4. `AnalyzerExecutionService` mixes adaptation, policy shaping, and execution
Severity: Medium

Evidence:
- `Execution/AnalyzerExecutionService.cs`

Why this is a problem:
- This class derives thread sampling policy, adapts options to dump size, constructs `RuntimeAnalysisContext`, creates `RuntimeFacade`, builds diagnostics plumbing, and then executes the analysis pipeline.
- Those are several different responsibilities:
  - option adaptation
  - runtime context assembly
  - execution dispatch

Refactor opportunity:
- Split into:
  - context builder
  - execution dispatcher
  - option adaptation helper

### 5. The orchestration services are too concrete and stage-aware
Severity: Medium

Evidence:
- `Execution/SingleDumpOrchestrationService.cs`
- `Execution/TrendOrchestrationService.cs`
- `Pipeline/Stages/*`

Why this is a problem:
- `SingleDumpOrchestrationService` directly constructs the stage list.
- It directly instantiates `StagedPipelineRunner` and `InsightEngine`.
- Trend orchestration contains detailed lifecycle and reporting assembly logic while also handling CLI-visible progress behavior.

This makes orchestration hard to reuse and hard to test in isolation.

Refactor opportunity:
- Build the pipeline externally and inject it.
- Treat orchestration as application policy, not as a CLI helper.
- Keep progress/reporting callbacks as adapters near the CLI surface.

### 6. The project is service-heavy, but many services are really procedural coordinators
Severity: Medium

Evidence:
- `Services/ConfigurationResolver.cs`
- `Services/AnalyzerFilterService.cs`
- `Services/ConsoleDiagnosticsSink.cs`
- `Services/FileDiagnosticsSink.cs`
- `Services/IncidentContextFactory.cs`

Why this is a problem:
- The `Services` folder has become a catch-all for all non-command code.
- Some classes are true services.
- Some are factories.
- Some are orchestration coordinators.
- Some are adapters.

That weakens folder semantics and makes the project feel flatter than it really is.

Refactor opportunity:
- Replace the generic `Services` bucket with narrower subdomains such as:
  - `Hosting`
  - `Composition`
  - `Execution`
  - `Output`
  - `Configuration`

### 7. Critical orchestration surfaces appear to have weak direct test coverage
Severity: Medium

Evidence:
- Graph query found no direct tests for `Hosting/ServiceRegistration.cs`
- Graph query found no direct tests for `Execution/SingleDumpOrchestrationService.cs`

Why this is a problem:
- These files control wiring and execution behavior that will likely change during refactoring.
- Without focused tests, cleanup work will be slower and riskier.

Refactor opportunity:
- Add wiring tests and orchestration tests before deeper structural changes.

## Structure Review

## Project layout assessment

### What is good
- Top-level folders are readable.
- `Commands`, `Console`, `Hosting`, `Pipeline`, and `Services` are at least recognizable responsibilities.
- `Program.cs` is thin, which is good.

### What is not good enough
- `Services` has become the de facto application layer.
- `Pipeline` is CLI-local even though much of the behavior is not CLI-specific.
- Factories with non-CLI ownership are parked in the CLI project.

### Cleanup opportunity
Aim for this mental model:
- `Program` and `Commands` define the entry surface.
- `Hosting` defines dependency setup only.
- `Execution` contains CLI adapters over application services.
- No analysis/reporting registries or domain factories live here.

## Class Structure Review

### `Program`
Assessment:
- Good.
- Small and readable.

Keep:
- host creation
- command parsing
- exception-to-exit-code mapping

Do not grow:
- no feature-specific decisions
- no runtime orchestration logic

### `DumpAnalysisService`
Assessment:
- Reasonable as a front-door application coordinator.
- But it currently depends on too many downstream concerns.

Problem:
- It knows about analyzers, finding generators, trend comparers, section builders, and trend-vs-single routing.

Recommendation:
- Keep it as a front-door if desired, but make it depend on a single orchestration abstraction rather than multiple registry/factory surfaces.

### `SingleDumpOrchestrationService`
Assessment:
- Functionally clear, structurally overburdened.

Problem areas:
- builds the stage pipeline itself
- instantiates `InsightEngine` directly
- owns console summary behavior
- owns success/failure exit decision indirectly through run inspection

Recommendation:
- split application orchestration from CLI output summary generation
- inject insight generation instead of newing it up
- inject a pipeline definition or runner abstraction

### `TrendOrchestrationService`
Assessment:
- Useful but too wide.

Problem areas:
- per-dump pipeline execution
- heartbeat/progress handling
- snapshot assembly
- trend comparison
- report document generation
- output writing
- diagnostic summary printing

Recommendation:
- split into:
  - trend execution coordinator
  - progress adapter
  - trend report assembly adapter

### `ReportBuilderFacade`
Assessment:
- Not a CLI class in any meaningful sense.

Recommendation:
- move out of CLI ownership.

### `DefaultAnalyzerFactory`
Assessment:
- straightforward but architecturally misplaced.

Recommendation:
- replace with descriptor/module-driven analyzer registration owned by the analysis/application layer.

### `DefaultSectionBuilderFactory`
Assessment:
- same structural issue as analyzer factory, but on the reporting side.

Recommendation:
- move to reporting-owned registration.

## Concrete Refactor Opportunities

## Opportunity 1: Introduce a proper CLI shell boundary
Why:
- This gives the biggest clarity gain inside this project.

What to do:
- Make `Cli` depend on one or two top-level orchestration interfaces.
- Remove analysis/reporting feature registration ownership from this project.

Expected outcome:
- `Cli` becomes clearly host-shaped rather than feature-shaped.

## Opportunity 2: Replace factory/list sprawl with capability modules
Why:
- The current design makes feature addition expensive and fragile.

What to do:
- Introduce `FeatureModule` descriptors that register analyzer + finding + trend + reporting capabilities together.

Expected outcome:
- smaller service registration file
- fewer sync bugs
- easier review of feature completeness

## Opportunity 3: Move application orchestration out of `Services`
Why:
- The current `Services` folder obscures which classes are host services and which are orchestration engines.

What to do:
- Introduce folders such as:
  - `Execution`
  - `Output`
  - `Configuration`
  - `Composition`

Expected outcome:
- the project reads closer to its real architecture

## Opportunity 4: Extract CLI-only adapters from mixed coordinators
Why:
- Progress rendering, console summaries, and exit-code mapping are CLI concerns.
- Stage planning, trend snapshot construction, and report assembly are not.

What to do:
- split mixed classes so only UI/terminal concerns stay in CLI.

Expected outcome:
- smaller orchestration classes
- easier testability

## Opportunity 5: Add tests before moving structure
Why:
- This project controls a lot of topology.
- Refactoring without harnesses will be slower and less confident.

What to test first:
- host wiring for key command paths
- single-dump orchestration happy path with stubs
- trend orchestration happy path with stubs
- analyzer/filter/registration completeness expectations

## Recommended Cleanup Order

### Step 1
Add focused tests around:
- service registration
- single-dump orchestration
- trend orchestration

### Step 2
Introduce capability/feature module descriptors.

### Step 3
Move analyzer and section-builder ownership out of CLI.

### Step 4
Move report-building facade out of CLI.

### Step 5
Split orchestration from console/output adapters.

### Step 6
Rename or reorganize folders so `Services` is no longer the architectural junk drawer.

## Suggested Target Shape

### Desired responsibility map
- `Program.cs`: host bootstrap and exit policy
- `Commands/*`: command model and argument mapping
- `Hosting/*`: container and configuration bootstrap only
- `Console/*`: terminal UX only
- `Execution/*`: CLI adapters that call application orchestration interfaces

### Responsibilities that should leave this project
- analyzer catalog ownership
- section-builder catalog ownership
- report-building facade ownership
- analysis runtime context assembly
- direct insight-engine ownership

## What to preserve
- thin `Program`
- clear command entry surface
- explicit console UX behavior
- cancellation and exception mapping behavior

## What not to do
- Do not rewrite command parsing first.
- Do not create many tiny abstractions unless they clarify ownership.
- Do not move hot-path analysis internals into CLI-facing layers.

## Bottom Line
`DumpDetective.Cli` should become smaller, more declarative, and more boring.

Right now it is carrying feature topology and orchestration responsibilities that belong elsewhere.

The cleanup goal is not fewer classes. The goal is sharper ownership so the project reads like a CLI shell instead of a second application core.