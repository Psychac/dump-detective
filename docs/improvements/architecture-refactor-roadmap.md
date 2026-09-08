# DumpDetective Architecture Refactor Roadmap

## Status
Proposed roadmap — largely executed. This document is the original planning artifact; for current execution status of each phase, see `consolidated-refactor-program.md`, which tracks Phases 0-8 (mapped onto this roadmap's Phases 1-8) and has been re-validated most recently.

Validated against active source layout on 2026-05-30. Re-validated for stale evidence/file references on 2026-07-17 (see note below).

Re-validation note (2026-07-17): most of the concrete file references in the "Evidence" and "Recommended First 5 Refactor Targets" sections below describe the *pre-refactor* state and no longer exist in current source — this is expected, since those files were the intended refactor targets and have since been replaced by the capability-module system (Phase 2/4 of the consolidated program). They are left in place below as historical evidence for why the roadmap was written, not as a description of current state. One exception was found and is called out inline: the `InternalsVisibleTo` reduction goal referenced under Phase 8/Core tightening in the consolidated program was not actually completed despite being marked done there — see `core-project-critical-review.md`.

## Purpose
This document turns the architectural critique into an execution roadmap ordered by payoff and implementation risk.

It is grounded in the current active solution under `src/`, not the legacy top-level `DumpDetective/` project.

## Executive Summary

### What is working and should be preserved
- The performance-oriented analysis spine is sound.
- The heap index and cache strategy are aligned with the tool's large-dump requirements.
- The active `src/` solution already has a usable project split: `Core`, `Analysis`, `Reporting`, `Cli`.

### What is creating most of the accidental complexity
- Layer ownership is blurred, especially between `Analysis`, `Reporting`, and `Cli`.
- The system is assembled through several parallel, hand-maintained registries.
- Report generation has grown into a second application with its own orchestration and UI complexity.
- Some analyzers contain reusable algorithms that should live in shared services instead of large per-analyzer classes.
- A legacy top-level code generation remains in the repository and raises cognitive overhead even though it is not part of the active solution.

### Guiding principle for the refactor
Do not simplify by flattening the architecture.

Simplify by making ownership explicit:
- `Analysis` owns dump/runtime/indexing/query/traversal/domain-result production.
- `Reporting` owns finding generation, section building, canonical documents, and rendering.
- `Cli` owns input parsing, execution configuration, command UX, and top-level application flow.
- `Core` owns small stable contracts and options, not feature behavior.

## Evidence Behind This Roadmap

### Active complexity seams observed in code (pre-refactor state; see resolution notes)
- `src/DumpDetective.Analysis/DumpDetective.Analysis.csproj` compiles source files from `..\DumpDetective.Reporting\FindingGenerators\*.cs`. **Resolved**: confirmed no such linked compilation remains in the current csproj.
- `src/DumpDetective.Cli/Services/DefaultAnalyzerFactory.cs` owns the analyzer catalog. **Resolved**: this file no longer exists; the catalog now lives in Reporting's `Capabilities/DefaultAnalyzerFeatureModuleCatalog.cs`.
- `src/DumpDetective.Cli/Hosting/ServiceRegistration.cs` manually registers analyzers, finding generators, trend comparers, formatters, and contains a comment warning that the lists must stay in sync. **Resolved**: `ServiceRegistration.cs` is now a small (~23-symbol) file that iterates the capability-module catalog instead of hand-listing components.
- `src/DumpDetective.Cli/Services/SingleDumpOrchestrationService.cs` constructs stages, invokes `InsightEngine`, and combines analysis policy with host orchestration. **Partially resolved**: the file still exists in `Cli/Services/` (not moved to a separate `Execution/` folder as some later notes implied), but per `cli-project-critical-review.md` the composition-root/catalog ownership issue this evidence was illustrating is fixed; remaining concern is folder placement only.
- `src/DumpDetective.Cli/Services/ReportBuilderFacade.cs` caches builder lists from a CLI-owned section-builder factory. **Resolved**: this file no longer exists in current source.
- `src/DumpDetective.Reporting/Services/ReportSerializer.cs` and `src/DumpDetective.Reporting/Services/TrendReportComposer.cs` form a deep document-composition stack. **Still open**: both files still exist and remain broad (`ReportSerializer.cs` ~60.9KB/60 symbols, `TrendReportComposer.cs` ~51.2KB/51 symbols) — see `reporting-project-critical-review.md`.
- `src/DumpDetective.Reporting/Templates/report.ui.js` contains a substantial client-side interaction layer and showed up as a graph hotspot without direct test coverage. **Partially resolved**: `report.ui.toc.js` and `report.ui.integrity.js` have been extracted, but `report.ui.js` itself is still ~47.9KB — decomposition has started, not completed.
- `src/DumpDetective.Analysis/Analyzers/MemoryAnalyzer.cs` and `src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs` each contain both domain logic and reusable algorithmic machinery. **Partially resolved**: `MemoryAnalyzer.cs` is now thin (~5.2KB/18 symbols) after the `MemoryAnalysisProjection` extraction; `GCRootAnalyzer.cs` is thinner but still ~10KB/22 symbols and retains local logic beyond the extracted `RootIndexReader`.

### Important constraint
The refactor must not regress the bounded-memory, index-first analysis model.

That means the following stay as architectural invariants:
- single-pass or bounded-pass heap work
- no eager full-heap materialization
- disk-backed index path for large dumps
- cached `MethodTable -> ClrType` lookup path
- analyzer outputs expressed as stable domain results

## Prioritization Model

### Ordering logic
The roadmap is ordered by this heuristic:
- highest payoff with low-to-medium delivery risk first
- structural preconditions before broad mechanical rewiring
- defer high-risk algorithm or schema changes until ownership is cleaner

### Risk/payoff scale
- Payoff: Low, Medium, High, Very High
- Risk: Low, Medium, High

## Roadmap Overview

| Order | Initiative | Payoff | Risk | Why it comes here |
|---|---|---|---|---|
| 1 | Remove repository-level ambiguity | High | Low | Reduces confusion immediately with minimal runtime impact |
| 2 | Consolidate feature registration | Very High | Medium | Eliminates the current sync problem across analyzer-related lists |
| 3 | Fix layer ownership for finding generation | Very High | Medium | Removes the most concrete architectural boundary violation |
| 4 | Shrink CLI to application-host responsibilities | High | Medium | Clarifies orchestration and makes the system easier to evolve |
| 5 | Split reporting composition from report UI app concerns | High | Medium | Contains the largest non-analysis hotspot |
| 6 | Extract reusable algorithm services from heavy analyzers | High | Medium | Simplifies analyzers without changing the domain surface |
| 7 | Rationalize trend composition | Medium | Medium | Valuable, but easier after ownership is fixed |
| 8 | Introduce architecture fitness tests | High | Low | Locks in gains and prevents drift |

## Phase 1: Remove Repository-Level Ambiguity

### Priority
1

### Payoff
High

### Risk
Low

### Why
The active solution is the `src/` tree, but the repository still contains an older top-level `DumpDetective/` implementation that appears in graph results and code searches.

That increases onboarding cost, review ambiguity, and the chance of editing the wrong generation of a component.

### What to do
- Move the legacy top-level `DumpDetective/` tree under an explicit archive location such as `archive/legacy-monolith/`.
- Add a short readme in that archive stating it is not part of the active solution.
- Add a short note in root documentation explaining that `DumpDetective.slnx` and `src/` are authoritative.

### Scope
- Repository organization only.
- No active runtime behavior change.

### Validation
- Solution still loads and builds from `DumpDetective.slnx`.
- Code search and graph results stop mixing legacy and active surfaces.

### Success criteria
- A new contributor can identify the production architecture by looking at the repo root once.

## Phase 2: Consolidate Feature Registration into One Capability Model

### Priority
2

### Payoff
Very High

### Risk
Medium

### Why
Right now each analyzer-related feature is registered in multiple places:
- analyzer catalog
- finding generator DI registration
- trend comparer DI registration
- section builder lists

This is the clearest sign that the architecture has become expensive to extend.

Every new feature increases the chance of partial registration, drift, or a missing capability in one output path.

### What to do
Replace the current parallel registration model with a single feature manifest per analyzer family.

Recommended shape:
- `AnalyzerFeatureModule` or `AnalysisFeatureDescriptor`
- one module declares:
  - analyzer type
  - optional finding generator type
  - optional trend comparer type
  - optional analyzer section builder type
  - optional report section contributions
  - ordering metadata

### Example end-state
Instead of editing several files, adding a new analyzer family should require one module definition and possibly one DI scan hook.

### Implementation options

#### Option A: Explicit module classes
Best balance for this repo.

Pros:
- predictable startup behavior
- explicit ownership
- easy to test

Cons:
- still some boilerplate

#### Option B: Attribute scanning
Useful only if startup reflection cost and discovery complexity are acceptable.

Pros:
- less wiring code

Cons:
- harder to debug
- weaker compile-time visibility

Recommendation: use explicit module classes.

### First concrete targets
- Replace `DefaultAnalyzerFactory`
- Replace large manual registration blocks in `ServiceRegistration`
- Replace `DefaultSectionBuilderFactory`

### Scope boundary
Do not change analyzer algorithms in this phase.

This phase is about assembly and ownership only.

### Validation
- Build and run with all current analyzers present.
- Snapshot test the resolved analyzer list and associated capabilities.
- Add a registration completeness test asserting that each analyzer family has the expected ancillary components.

### Success criteria
- A feature can be added by updating one module registration path rather than four or five lists.

## Phase 3: Move Finding Generation Fully into Reporting

### Priority
3

### Payoff
Very High

### Risk
Medium

### Why
The current linked compile from Reporting into Analysis is the strongest objective sign that the layer boundary is wrong.

If finding generation is considered presentation-oriented interpretation of domain results, it belongs entirely in `Reporting`.

That aligns with your architecture intent:
- `Analysis` emits domain facts
- `Reporting` converts those facts into findings, sections, documents, and renderers

### What to do
- Remove linked compilation of reporting finding generators from `DumpDetective.Analysis.csproj`.
- Keep `IFindingGenerator` contract in a stable shared location only if truly needed across project boundaries.
- Register finding generators from the Reporting project as Reporting-owned feature components.
- Make the analysis pipeline consume finding-generation services through an abstraction, not through cross-owned source files.

### Architectural decision to make
Choose one of these models:

#### Model A: Findings are part of Reporting only
Analysis pipeline returns domain results only.

Pros:
- cleanest separation
- reporting can evolve its heuristics independently

Cons:
- console summary paths may need a small reporting-facing adapter

#### Model B: Analysis invokes a projection interface implemented by Reporting
Analysis still asks for findings, but does not own implementations.

Pros:
- smaller execution-path change

Cons:
- some boundary tension remains

Recommendation: move toward Model A, with Model B as an intermediate step if you want lower migration risk.

### Migration notes
- Keep current `AnalyzerRunResult` shape stable during transition.
- If needed, add a temporary adapter in CLI or Reporting so existing report and console paths continue to work.

### Validation
- Single-dump and trend report outputs remain functionally equivalent.
- Finding counts and major fingerprints remain stable for a golden dump set.

### Success criteria
- `Analysis` no longer compiles or owns Reporting source files.

## Phase 4: Shrink CLI to Host/Application Responsibilities Only

### Priority
4

### Payoff
High

### Risk
Medium

### Why
The CLI project currently mixes:
- command parsing
- execution configuration
- pipeline composition
- insight execution
- report builder ownership
- output policy

That makes the host layer too knowledgeable about internal analysis behavior.

### What to do
- Move stage construction out of `SingleDumpOrchestrationService` into an application-layer pipeline service closer to `Analysis`.
- Stop direct construction of `InsightEngine` inside the CLI orchestrator.
- Move feature-specific orchestration knowledge behind services injected from the owning layer.
- Keep the CLI responsible for:
  - reading command-line intent
  - resolving config/options
  - invoking application services
  - printing UX and exit codes

### Recommended target shape
- `DumpDetective.Application` is not required as a new project yet.
- But conceptually, you want one application-service layer where run orchestration lives.

If adding a new project feels excessive, place this transitional application layer in `Analysis/Pipeline` first and keep `Cli` thin.

### Concrete extraction targets
- Stage list construction
- Insight execution trigger
- Analyzer context assembly where it is really analysis-specific rather than CLI-specific

### Validation
- Existing CLI commands continue to behave the same.
- Unit tests can exercise orchestration without going through command parsing.

### Success criteria
- `Cli` reads like a host shell, not like a second business-logic layer.

## Phase 5: Split Reporting Document Composition from Interactive Report UI

### Priority
5

### Payoff
High

### Risk
Medium

### Why
Reporting is currently carrying two different kinds of complexity:
- server-side document composition and serialization
- browser-side report application behavior

Those are related, but they should not evolve as one indistinct subsystem.

The graph also showed the UI renderer/interactivity path as one of the highest criticality areas, with no direct tests identified for the hotspot functions.

### What to do

#### Server-side reporting split
Make the following responsibilities explicit:
- canonical document assembly
- domain/findings projection
- section composition
- renderer formatting

#### Client-side report app split
Within `Templates/`, separate:
- view-model shaping
- navigation/interactivity
- rendering primitives
- mode/state policy

### Immediate restructuring targets
- Break `report.ui.js` into smaller modules around reading mode, anchor integrity, and dynamic section behavior.
- Define a narrow report-view contract that the browser code consumes.
- Keep `ReportSerializer` focused on pure document projection.
- Keep trend-specific augmentation out of generic document assembly where possible.

### What not to do yet
Do not rewrite the report frontend framework or introduce a SPA stack just to create boundaries.

The current embedded JS model is acceptable if the module responsibilities are cleaner.

### Validation
- Golden HTML output diff on a fixed document set.
- Browser smoke tests for navigation, reading mode, and incremental section rendering.
- Focused tests for the current hotspot functions in the UI modules.

### Success criteria
- Report UI behavior can evolve without touching core report serialization logic.

## Phase 6: Extract Reusable Algorithm Services from Heavy Analyzers

### Priority
6

### Payoff
High

### Risk
Medium

### Why
Some analyzers are becoming mini-frameworks.

That makes them harder to test, harder to compare, and harder to reuse when another analyzer needs the same machinery.

### What to do
Create shared analysis services for reusable algorithmic concerns.

Recommended first extractions:
- root index reader service
- bounded graph/path traversal service
- retained-size estimator service
- type ranking and scoring helper services
- analyzer severity/scoring helpers where the heuristics are generic

### Candidate files to target first
- `src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs`
- `src/DumpDetective.Analysis/Analyzers/MemoryAnalyzer.cs`
- `src/DumpDetective.Analysis/Insight/InsightEngine.cs`

### Design rule
An analyzer should mostly do three things:
- acquire the already-indexed data it needs
- call a small set of focused services
- assemble a domain result

If the analyzer is also implementing traversal, scoring, serialization-ish shaping, and ranking infrastructure, it is too broad.

### Validation
- Existing analyzer outputs stay within agreed tolerance for the golden dump set.
- Microbenchmarks cover extracted services where they touch hot or near-hot paths.

### Success criteria
- At least the largest analyzers become thin coordinators over shared services.

## Phase 7: Rationalize Trend Composition After Ownership Cleanup

### Priority
7

### Payoff
Medium

### Risk
Medium

### Why
Trend reporting is useful, but the current composition path appears layered on top of the single-dump document path in a way that duplicates some work and mixes specialized behavior into the general reporting flow.

This is worth fixing, but only after the feature-registration and ownership issues are cleaner.

### What to do
- Separate base document composition from trend augmentation more explicitly.
- Reduce repeated document-building where only subsets or projections are needed.
- Define what is canonical trend-only structure vs reused single-dump structure.

### Likely target outcome
- One stable canonical base projection model
- One trend augmentation model layered on top without rebuilding more than necessary

### Validation
- Trend reports remain semantically identical for the existing sample set.
- Compare runtime and allocation cost before and after any composition changes.

### Success criteria
- Trend composition becomes an additive specialization, not a parallel reporting pipeline.

## Phase 8: Add Architecture Fitness Tests and Guardrails

### Priority
8

### Payoff
High

### Risk
Low

### Why
Without guardrails, the architecture will drift back toward the same problems.

This repo already has enough moving parts that structural tests will pay for themselves.

### What to do
- Add project-level dependency tests that enforce allowed directions.
- Add tests ensuring `Analysis` does not compile or depend on Reporting implementation assets.
- Add registration completeness tests for analyzer feature modules.
- Add hotspot coverage checks for key report UI modules and orchestration services.
- Add performance guardrails for index build and representative analyzer runs.

### Recommended rules to enforce
- `Cli -> Analysis, Reporting, Core`
- `Reporting -> Analysis, Core` only where intentionally accepted and documented
- `Analysis -> Core`
- no source-file linking across layer boundaries
- no new manual parallel registration lists without an approved exception

### Validation
- Tests run in CI.
- Architectural violations fail fast before feature work accumulates on top.

### Success criteria
- Structural regressions are caught automatically.

## Suggested Delivery Waves

### Wave 1: Highest payoff, lowest destabilization
- Phase 1: Remove repository-level ambiguity
- Phase 2: Consolidate feature registration
- Phase 8: Add architecture guardrails for the new registration model

### Wave 2: Fix the most important layer boundary
- Phase 3: Move finding generation fully into Reporting
- Phase 4: Shrink CLI responsibilities

### Wave 3: Tame reporting complexity
- Phase 5: Split report composition from report UI app concerns
- Phase 7: Rationalize trend composition

### Wave 4: Simplify deep implementation surfaces
- Phase 6: Extract reusable algorithm services from heavy analyzers

## Recommended First 5 Refactor Targets
Status (2026-07-17): targets 1-4 below are done — the referenced files no longer exist and have been replaced by the capability-module system. Target 5 (`report.ui.js` split) is in progress, not complete.

### 1. `src/DumpDetective.Analysis/DumpDetective.Analysis.csproj`
Why:
- Contains the clearest boundary violation.

What to change:
- Remove linked Reporting source compilation as part of the finding-generation ownership move.

### 2. `src/DumpDetective.Cli/Hosting/ServiceRegistration.cs`
Why:
- Central symptom of registry sprawl.

What to change:
- Replace list-by-list registration with a feature-module registration pattern.

### 3. `src/DumpDetective.Cli/Services/DefaultAnalyzerFactory.cs`
Why:
- Hard-coded analyzer catalog makes extension costly.

What to change:
- Make analyzer resolution descriptor-driven.

### 4. `src/DumpDetective.Cli/Services/DefaultSectionBuilderFactory.cs`
Why:
- Same problem as analyzer registration, but in the reporting path.

What to change:
- Fold into the same capability/feature model.

### 5. `src/DumpDetective.Reporting/Templates/report.ui.js`
Why:
- Hotspot with high behavioral density and weak apparent test coverage.

What to change:
- Split by concern and cover critical behaviors with targeted tests.

## Migration Risks and Mitigations

### Risk: feature loss during registration consolidation
Mitigation:
- Add a snapshot test of the resolved capability graph before the refactor.

### Risk: finding semantics shift during ownership cleanup
Mitigation:
- Use golden dumps and compare findings, severities, fingerprints, and section presence.

### Risk: trend report regressions
Mitigation:
- Freeze representative trend outputs and diff at the document-model level before diffing HTML.

### Risk: performance regressions from service extraction
Mitigation:
- Keep hot-path infrastructure untouched until benchmark coverage is in place.
- Prefer extracting orchestration and near-hot logic before touching index build internals.

### Risk: over-correcting with too many new projects
Mitigation:
- Introduce conceptual boundaries first.
- Add physical projects only where they materially improve isolation.

## What Not to Change Early
- Do not rewrite the heap indexing architecture first.
- Do not rewrite the report frontend stack first.
- Do not redesign all analyzer output models first.
- Do not attempt a big-bang package/module split across the whole repo.

These would add risk before the main ownership problems are fixed.

## Definition of Done for the Refactor Program
- Active architecture is obvious from repository layout and docs.
- New analyzer families are added through one cohesive registration model.
- `Analysis` no longer compiles Reporting source files.
- `Cli` acts as a host shell, not as a second domain/orchestration layer.
- Reporting composition and report UI behavior are independently understandable.
- Heavy analyzers delegate reusable algorithms to shared services.
- Architectural boundaries are enforced by tests.

## Recommended Next Step
Start with a small design spike that produces two outputs:
- a concrete `AnalyzerFeatureModule` design
- a migration sketch for moving finding generators out of `Analysis`

That combination gives the best payoff-to-risk ratio and sets up the rest of the roadmap cleanly.