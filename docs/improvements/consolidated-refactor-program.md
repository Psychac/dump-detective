# DumpDetective Consolidated Refactor Program

## Status
Proposed consolidated program.

Implementation verification update (2026-05-30):
- Program phases 0-8 are implemented and closed in the active codebase.
- Enforcement gates are in place (`.github/workflows/phase8-fitness.yml`) and validated locally.
- Post-program follow-on cleanup items remain (documented in project critical reviews) but are no longer phase blockers.

Execution status update (2026-05-30):
- Phase 0: Complete (baseline script, snapshots, and smoke/guardrail tests green)
- Phase 1: Complete (legacy top-level project removed, active layout clarified)
- Phase 2: Complete (module-driven registration and completeness validation in place)
- Phase 3: Complete (finding-generator ownership moved to Reporting; Analysis source-link removed)
- Phase 4: Complete (CLI composition ownership reduced; factories/facade/capability catalog now Reporting-owned)
- Phase 5: Complete (serializer/trend decomposition delivered, renderer policy explicit, UI modules split with targeted coverage)
- Phase 6: Complete (pipeline collaborators extracted, insight rules grouped, shared traversal adopted in analyzer path)
- Phase 7: Complete (Core boundary tightened: legacy option bag removed, policy inference moved to Analysis, context surface narrowed)
- Phase 8: Complete (fitness enforcement wired into CI with architecture/no-source-link/hotspot/perf guardrails)

Re-validation notes (2026-05-30):
- Focused architecture/integration guardrail suite: 17/17 passed
- Official baseline harness `tools/Phase0/Invoke-Phase0Baseline.ps1`: passed
- Baseline artifacts under `artifacts/reports/phase0`: all `status = pass`
- Phase 4 follow-up: baseline harness updated for Reporting-owned capability catalog path
- Phase 5 milestone: HTML renderer now uses explicit render settings (no mutable static override flags)
- Phase 6 milestone: pipeline/infrastructure/insight decomposition validated with focused tests and baseline harness
- Phase 7 milestone: Core contracts slimmed; runtime-aware boundary decision documented; focused tests and baseline harness revalidated
- Phase 8 milestone: CI fitness gates added for architecture direction, no-source-link boundaries, hotspot guardrail coverage, benchmark compile, and baseline harness

Validated against:
- `architecture-refactor-roadmap.md`
- `cli-project-critical-review.md`
- `reporting-project-critical-review.md`
- `analysis-project-critical-review.md`
- `core-project-critical-review.md`

Prepared on 2026-05-30.

## Purpose
This document consolidates the architectural roadmap and the four project-specific critical reviews into one program-level refactor plan.

It is intended to answer one question clearly:

How do we move from the current architecture to the intended one without destabilizing the dump-analysis core?

## Program Goal
Achieve a cleaner, more explicit architecture where:
- `Cli` is a thin host shell
- `Reporting` owns finding generation, canonical report composition, and rendering
- `Analysis` owns runtime/indexing/query/traversal/domain-result production
- `Core` stays small and stable, with tighter contract boundaries

## Non-Negotiable Invariants
The following must remain true throughout the program:
- no eager full-heap materialization
- no accidental extra heap passes in hot flows
- disk-backed indexing remains available for large dumps
- cached `MethodTable -> ClrType` access remains in place
- analyzer outputs remain stable enough for report and trend generation
- report outputs remain functionally equivalent during ownership migration

## Consolidated Diagnosis

### What is actually wrong today
The codebase is not primarily suffering from low-level performance design problems.

It is suffering from ownership drift.

That drift shows up in four ways:

### 1. `Cli` owns too much topology
- feature registration lives there
- analyzer catalog lives there
- section-builder catalog lives there
- orchestration logic lives there
- report-building facade lives there

### 2. `Reporting` contains two systems without a sharp internal split
- canonical backend composition
- browser-side interactive report app

### 3. `Analysis` contains both strong infrastructure and broad policy-heavy classes
- infrastructure is mostly sound
- some analyzers and pipeline types are too broad
- a Reporting concern still leaks into Analysis via linked finding-generator compilation

### 4. `Core` is mostly healthy but needs boundary tightening
- direct ClrMD dependency may be too low-level for a true contract layer
- contracts include some inferred policy
- ambient context and options are growing broad

## Program Strategy

### Strategy summary
Refactor in this order:
1. remove ambiguity
2. stabilize registration and structural guardrails
3. fix ownership boundaries
4. reduce project-local complexity
5. clean deep implementation surfaces only after topology is stable

### Why this sequence
If you start with deep analyzer extraction or report UI cleanup before ownership and registration are fixed, you will spend effort inside the wrong structural frame.

The program should first make the architecture legible, then make it smaller.

## Target End State

## Desired responsibility map

### `DumpDetective.Cli`
Owns only:
- command parsing
- config resolution/validation
- terminal UX
- exit code mapping
- top-level application invocation

Does not own:
- analyzer catalogs
- section-builder catalogs
- report-building facades
- analysis runtime context assembly
- feature registration sprawl

### `DumpDetective.Reporting`
Owns:
- finding generation
- analyzer/detail section building
- report-section composition
- canonical report document projection
- trend augmentation
- output format renderers
- browser-side HTML report behavior

Internally split into:
- projection/composition
- renderers
- web app / template behavior

### `DumpDetective.Analysis`
Owns:
- dump/runtime access
- heap indexing and cache
- query and traversal services
- analyzer execution
- analyzer domain results
- cross-analyzer insight rule execution

Does not own:
- reporting finding-generator implementations
- presentation-oriented result interpretation

### `DumpDetective.Core`
Owns:
- stable interfaces
- shared models and records
- stable options/configuration primitives

Should remain:
- small
- low-policy
- explicit in boundary intent

## Program Workstreams

## Workstream A: Structural Clarity and Guardrails
Purpose:
- make the active architecture obvious
- prevent further drift during the refactor

Includes:
- repository cleanup
- dependency-direction checks
- architecture fitness tests
- registration completeness tests

## Workstream B: Feature Registration and Composition Root Cleanup
Purpose:
- eliminate parallel, hand-maintained registration lists

Includes:
- feature module / capability descriptor model
- analyzer registration cleanup
- section-builder registration cleanup
- composition root simplification

## Workstream C: Boundary Ownership Realignment
Purpose:
- move behavior to the project that should own it

Includes:
- finding generation moved to Reporting
- CLI orchestration thinned
- report-building ownership moved out of CLI
- Core boundary clarification

## Workstream D: Reporting Internal Decomposition
Purpose:
- split backend report composition from frontend report-app behavior

Includes:
- serializer decomposition
- trend composition cleanup
- renderer policy cleanup
- template UI module cleanup

## Workstream E: Analysis Internal Decomposition
Purpose:
- keep the infrastructure spine, shrink broad analyzers and broad policy hosts

Includes:
- pipeline collaborator extraction
- analyzer algorithm service extraction
- insight-rule modularization
- shared traversal/query strengthening

## Program Phases

## Phase 0: Baseline and Safety Net

### Goal
Create the minimum safety harness before structural edits.

### Why first
The upcoming refactors change ownership, registration, and execution paths.
Without baselines, equivalence checking will be too weak.

### Deliverables
- golden dump set defined for single-dump and trend runs
- baseline snapshots for:
  - resolved analyzer/capability graph
  - single-dump findings and section presence
  - trend findings and key deltas
  - HTML report smoke output
- focused tests added around:
  - CLI orchestration entry points
  - reporting serializer/composer hotspots
  - heavyweight analysis heuristics

### Primary source documents driving this phase
- `architecture-refactor-roadmap.md`
- `cli-project-critical-review.md`
- `reporting-project-critical-review.md`
- `analysis-project-critical-review.md`

### Exit criteria
- structural changes can be validated against stable baselines

Current status:
- Complete

## Phase 1: Remove Ambiguity and Freeze the Active Architecture

### Goal
Make the active production architecture obvious.

### Deliverables
- move legacy top-level `DumpDetective/` under explicit archive location
- add README/note clarifying `DumpDetective.slnx` and `src/` as authoritative
- update docs to point contributors at the active architecture only

### Primary affected area
- repository layout

### Payoff
High

### Risk
Low

### Exit criteria
- repo navigation and code search no longer blur active and legacy generations

Current status:
- Complete

## Phase 2: Introduce Capability Modules and Architecture Guardrails

### Goal
Replace parallel registrations before moving ownership.

### Why here
This is the enabling step for most later cleanup.

### Deliverables
- define `AnalyzerFeatureModule` or equivalent explicit capability descriptor
- each module can declare:
  - analyzer
  - finding generator
  - trend comparer
  - analyzer section builder
  - report section contributions
  - order/metadata
- replace manual analyzer list in CLI
- replace manual section-builder list in CLI
- replace large DI registration blocks with module registration
- add completeness tests for modules
- add architecture fitness tests for dependency rules

### Primary source findings being addressed
- CLI composition root overload
- factory ownership drift
- registration sprawl

### Primary affected projects
- `Cli`
- `Reporting`
- `Analysis`

### Payoff
Very High

### Risk
Medium

### Exit criteria
- a new feature can be registered through one cohesive module path
- no more manual parallel lists that must stay in sync

Current status:
- Complete

## Phase 3: Realign the Reporting/Analysis Boundary

### Goal
Move finding generation to its proper owner.

### Deliverables
- remove linked compilation of Reporting finding generators from `DumpDetective.Analysis.csproj`
- make Reporting own finding-generator implementations and registration
- choose and implement the migration model:
  - preferred end state: Reporting generates findings from domain results
- preserve `AnalyzerRunResult` compatibility during migration
- add adapter if needed so console/reporting paths remain stable during the transition

### Primary source findings being addressed
- strongest boundary violation in `Analysis`
- unclear ownership between analysis result production and report interpretation

### Primary affected projects
- `Analysis`
- `Reporting`
- `Cli` as transitional consumer/adaptor layer

### Payoff
Very High

### Risk
Medium

### Exit criteria
- `Analysis` no longer compiles Reporting-owned source files
- report outputs remain functionally equivalent on the golden set

Current status:
- Complete

## Phase 4: Thin the CLI into a Host Shell

### Goal
Remove non-host ownership from `Cli`.

### Deliverables
- move analyzer-factory ownership out of `Cli`
- move section-builder ownership out of `Cli`
- move report-building facade ownership out of `Cli`
- move stage-construction/orchestration policy behind application-facing services
- keep `Cli` responsible only for:
  - command mapping
  - config/validation
  - UX/progress
  - exit codes

### Optional implementation note
No new `Application` project is required immediately.

The application layer can exist conceptually first and physically later if needed.

### Primary source findings being addressed
- CLI acting as second application layer
- service bucket ambiguity
- over-concrete orchestration services

### Primary affected projects
- `Cli`
- `Analysis`
- `Reporting`

### Payoff
High

### Risk
Medium

### Exit criteria
- `Cli` reads like a shell over application services rather than a system owner

Current status:
- Complete

## Phase 5: Decompose Reporting Internals

### Goal
Separate report projection, renderer transport, and browser app behavior.

### Deliverables

#### Backend composition cleanup
- decompose `ReportSerializer` into smaller builders/services
- clarify canonical document assembly vs domain grouping vs executive summary vs correlation building
- simplify or repurpose `CanonicalReportDocumentFactory` based on the new composition seams

#### Trend composition cleanup
- separate base document projection from trend augmentation
- reduce repeated full-document rebuilding where possible

#### Renderer cleanup
- replace static mutable renderer overrides with explicit render settings
- split payload shaping, asset bundling, and render policy

#### Browser-side cleanup
- split `report.ui.js` into focused modules
- define a narrower browser-facing view contract
- add targeted tests for reading mode, anchor integrity, and dynamic section behavior

#### Internal organization cleanup
- group section builders and finding generators by domain/capability rather than leaving them flat

### Primary source findings being addressed
- Reporting contains both backend composition and frontend app behavior
- serializer is too broad
- trend composition duplicates work
- UI hotspot is behavior-dense and weakly covered

### Primary affected project
- `Reporting`

### Payoff
High

### Risk
Medium

### Exit criteria
- report composition and report UI behavior are independently understandable and testable

Current status:
- Complete
- Completed in this iteration:
  - renderer policy cleanup with explicit `HtmlRenderSettings`
  - executive-summary projection extracted into `ExecutiveSummaryProjector`
  - trend summary deltas composed via shared executive-summary projection
  - browser UI split into focused modules (`report.ui.toc.js`, `report.ui.integrity.js`)
  - targeted visuals/renderer tests and baseline harness revalidated

## Phase 6: Decompose Analysis Internals Without Touching the Performance Spine

### Goal
Keep the indexing/cache/runtime design intact while shrinking broad policy hosts and analyzers.

### Deliverables

#### Pipeline cleanup
- split `AnalysisPipeline` into collaborators such as:
  - analyzer runner
  - diagnostics publisher
  - cleanup/GC policy
  - progress adapter
  - result enrichment/post-processing

#### Analyzer cleanup
- extract reusable services from heavyweight analyzers:
  - root index reader
  - bounded graph/path traversal
  - retained-size estimator
  - ranking/scoring helpers

#### Insight cleanup
- turn `InsightEngine` into a grouped rule pipeline or rule set orchestrator

#### Infrastructure cleanup
- strengthen shared `Traversal` and `Query` so analyzers stop duplicating local search logic
- decompose `HeapAnalysisCache` internally only where it clarifies responsibilities without harming the unified access model

### Explicit constraint
Do not rewrite the indexing layer as part of this phase.

Preserve:
- `RuntimeFacade`
- index-first query model
- memory/disk index strategy
- performance-sensitive writer implementations

### Primary source findings being addressed
- pipeline operational policy overload
- large analyzers doing too much inline
- monolithic insight rules
- traversal logic duplicated locally

### Primary affected project
- `Analysis`

### Payoff
High

### Risk
Medium

### Exit criteria
- heavyweight analyzers become thinner coordinators over shared services
- pipeline mechanics are cleaner without performance regression

Current status:
- Complete
- Completed in this iteration:
  - extracted pipeline collaborators: `AnalyzerExecutionRunner`, `AnalysisDiagnosticsPublisher`, `AnalyzerResultPostProcessor`, `AnalyzerCleanupPolicy`
  - reduced `AnalysisPipeline` to orchestration over explicit collaborators
  - reorganized `InsightEngine` into grouped rule-set orchestration while preserving rule behavior
  - introduced shared traversal helper `ObjectGraphTraversal` and adopted it in `AsyncTaskAnalyzer` exception graph search path
  - validated with focused suites (`AnalysisPipelineTests`, `AsyncTaskFindingGeneratorTests`, `ReportingCompositionTests`) and baseline harness pass

## Phase 7: Tighten Core Boundaries

### Goal
Keep Core small, stable, and explicit about what it is.

### Deliverables
- decide and document whether Core is:
  - runtime-neutral, or
  - intentionally dump-runtime-aware
- reduce legacy option plumbing in `AnalysisContext`
- narrow ambient context where practical
- revisit location/role of `IFindingGenerator` after boundary realignment
- consider removing policy inference from core contracts where practical
- audit and reduce `InternalsVisibleTo` where possible

### Primary source findings being addressed
- Core/ClrMD boundary ambiguity
- ambient context growth
- policy helper creep inside abstractions

### Primary affected project
- `Core`

### Payoff
Medium

### Risk
Low to Medium

### Exit criteria
- Core remains small and no longer looks like a stealth policy layer

Current status:
- Complete
- Completed in this iteration:
  - runtime-boundary decision documented: Core is intentionally dump-runtime-aware at `AnalysisContext`
  - removed legacy `AnalysisContext.Options` type-keyed map in favor of typed `AnalysisOptions`
  - removed policy inference helpers from `DiagnosticsOptions` and moved evaluation to Analysis (`AnalyzerCollectionPolicyEvaluator`)
  - narrowed ambient runtime context by removing duplicate `RuntimeAnalysisContext.ExecutionPolicy`
  - audited `InternalsVisibleTo`: retained single Analysis-to-tests entry only (no Core internals exposure)
  - updated affected benchmarks/tests to typed options path and validated behavior

## Phase 8: Hardening and Fitness Enforcement

### Goal
Lock in the gains.

### Deliverables
- CI architecture tests for dependency direction
- no-source-linking rule across layer boundaries
- capability-module completeness tests
- hotspot coverage checks for key orchestration and UI modules
- performance guardrails for representative index-build and analyzer runs

### Note
Some of this begins in Phase 2 and Phase 0, but this phase is where enforcement becomes mandatory and complete.

### Exit criteria
- structural regressions fail fast in CI

Current status:
- Complete
- Completed in this iteration:
  - added architecture fitness tests enforcing no-source-link namespace boundaries for Analysis/Core
  - added hotspot guardrail presence checks for orchestration and report UI test surfaces
  - added CI workflow `.github/workflows/phase8-fitness.yml` to run architecture/hotspot tests, benchmark compile guardrail, and `tools/Phase0/Invoke-Phase0Baseline.ps1`
  - revalidated focused tests, benchmark project build, and Phase0 baseline harness locally

## Program Ordering Summary

## Recommended delivery waves

### Wave 1: Stabilize the frame
- Phase 0: Baseline and safety net
- Phase 1: Remove ambiguity
- Phase 2: Capability modules and guardrails

### Wave 2: Fix the most important ownership errors
- Phase 3: Reporting/Analysis boundary realignment
- Phase 4: Thin CLI into a host shell

### Wave 3: Clean project-local hotspots
- Phase 5: Reporting internal decomposition
- Phase 6: Analysis internal decomposition

### Wave 4: Tighten the boundary layer
- Phase 7: Core tightening
- Phase 8: hardening and enforcement finalization

## Cross-Project Mapping

| Project | Main problem | Main program phases |
|---|---|---|
| `Cli` | Owns too much topology and orchestration | 2, 4, 8 |
| `Reporting` | Backend composition and frontend app behavior are collapsed together | 3, 5, 8 |
| `Analysis` | Strong spine, but broad analyzers/pipeline and a Reporting ownership leak | 3, 6, 8 |
| `Core` | Mostly healthy, but needs boundary tightening | 7, 8 |

## Recommended First 90% Path
If you want the minimum path that yields most of the architectural benefit, do these in order:
1. Baselines and tests for hot seams
2. Capability module design and rollout
3. Move finding generation out of Analysis
4. Remove analyzer/section/report ownership from CLI
5. Split Reporting backend composition from browser app behavior

That sequence addresses most of the structural pain before touching deeper algorithm extraction.

## Recommended First Concrete Deliverables

### Deliverable 1: Capability module spike
Produce:
- explicit module shape
- sample implementation for 2 to 3 analyzer families
- registration completeness test design

### Deliverable 2: Finding-generation migration sketch
Produce:
- chosen migration model
- transition adapters needed
- expected changes to `AnalyzerRunResult` handling

### Deliverable 3: CLI thinning map
Produce:
- list of classes leaving `Cli`
- list of classes staying in `Cli`
- proposed folder structure after cleanup

### Deliverable 4: Reporting decomposition plan
Produce:
- proposed split of `ReportSerializer`
- proposed split of `report.ui.js`
- trend composition cleanup targets

### Deliverable 5: Analysis extraction shortlist
Produce:
- first 3 shared services to extract from analyzers
- first 2 pipeline collaborators to split from `AnalysisPipeline`

## Program Risks and Mitigations

### Risk: registration cleanup causes feature drift
Mitigation:
- snapshot resolved capability graph before refactor
- add module completeness tests early

### Risk: finding ownership migration changes report semantics
Mitigation:
- use golden dump equivalence checks for finding counts, severity, fingerprints, and section presence

### Risk: CLI thinning causes runtime behavior regressions
Mitigation:
- add orchestration tests before moving responsibilities
- keep console adapters near the CLI boundary while moving orchestration outward

### Risk: Reporting cleanup becomes a frontend rewrite
Mitigation:
- keep the embedded JS model
- refactor by module boundary, not by stack replacement

### Risk: Analysis cleanup regresses performance
Mitigation:
- do not touch indexing architecture early
- benchmark extracted services where they affect hot or near-hot paths

### Risk: Core cleanup becomes abstraction churn
Mitigation:
- make only boundary-tightening changes that improve clarity
- avoid purity-only abstractions

## Definition of Done
The program is complete when:
- the active architecture is obvious from the repository and docs
- feature registration is cohesive and module-driven
- `Analysis` no longer owns Reporting finding-generator implementations
- `Cli` is a thin shell over application-facing services
- `Reporting` has a clear split between canonical composition and browser-side behavior
- `Analysis` preserves its performance spine while shrinking broad policy hosts
- `Core` remains small and explicit about its boundary role
- CI enforces the architecture and guards against drift

## Recommended Immediate Next Step
Begin with one short design iteration that produces all of the following:
- the capability module design
- the finding-generation migration decision
- the CLI ownership exit map

That trio unlocks most of the rest of the program with the best payoff-to-risk ratio.