# DumpDetective.Reporting Critical Review

## Status
Architectural/code-structure review.

Validated against active source on 2026-05-30. Re-validated against active source on 2026-07-17.

## Implementation Status Update (2026-07-17)
Overall status: Partially remediated (major decomposition delivered; some internal cleanup remains). Consistent with the 2026-05-30 assessment on the core claims; one new ownership finding and one clarification below.

Addressed in implementation:
- mutable static renderer overrides removed; explicit `HtmlRenderSettings` path adopted — confirmed via source search: no `ForcePreRender`-style static override remains anywhere in the codebase, and `HtmlReportRenderer.cs` is now small (~9.9KB / 24 symbols).
- executive summary projection extracted (`ExecutiveSummaryProjector`) and reused by trend composition.
- browser UI decomposition started with a focused module split (`report.ui.toc.js` ~739 bytes, `report.ui.integrity.js` ~3.6KB) — genuinely extracted, but `report.ui.js` itself is still ~47.9KB, so this is an early step rather than a completed decomposition; the "started" wording in the original doc remains the accurate characterization.
- targeted renderer/visuals and baseline guardrail tests added and validated.
- Reporting now owns the production capability/module catalog: `Capabilities/DefaultAnalyzerFeatureModuleCatalog.cs` declares ~34 `AnalyzerFeatureModule` entries (analyzer, finding generator, trend comparer, section builder per module) and is consumed directly by `DumpDetective.Cli/Hosting/ServiceRegistration.cs`. This is new scope for Reporting relative to the original review (which predates the capability-module system) and is architecturally consistent with Reporting owning finding-generation/registration composition, though it does widen what "Reporting" is responsible for beyond report projection/rendering.

New/clarified finding since last review:
- Finding generators physically live under `Reporting/FindingGenerators/*.cs` but still declare `namespace DumpDetective.Analysis.FindingGenerators` (verified in `MemoryFindingGenerator.cs` and referenced this way from `DefaultAnalyzerFeatureModuleCatalog.cs`'s `using DumpDetective.Analysis.FindingGenerators;`). Reporting fully owns and compiles these files, but the namespace still advertises Analysis ownership — a cosmetic leftover from the migration that should be renamed to `DumpDetective.Reporting.FindingGenerators` to match physical/project reality. (Same issue flagged in the Analysis project review.)

Remaining follow-on cleanup:
- `ReportSerializer` remains broad despite extraction improvements (confirmed: still ~60.9KB / 60 symbols)
- trend composition still has residual complexity and can be further reduced around shared base projection (confirmed: `TrendReportComposer.cs` still ~51.2KB / 51 symbols)
- builder/generator internal organization is still relatively flat and can be grouped by domain/capability (confirmed: `SectionBuilders/` and `FindingGenerators/` each contain dozens of files with no subfolder grouping)
- Analysis-to-Reporting contract surface can still be narrowed further over time

## Scope
Project reviewed: `src/DumpDetective.Reporting`

Focus areas:
- code structure
- class/service structure
- document composition
- renderer structure
- embedded UI/app complexity
- cleanup and refactor opportunities for a cleaner project

## Executive Summary
`DumpDetective.Reporting` contains two different systems inside one project:
- canonical report projection/composition
- interactive HTML report application

Those systems are related, but they are not the same kind of problem.

The project currently works, but it is accumulating complexity because:
- serializer and composer logic are deep and broad
- trend reporting layers on additional composition paths
- the HTML renderer bundles and controls client behavior through static switches and resource flattening
- the template JS has become a meaningful browser-side application without obvious project-level separation or strong visible test coverage

The result is a project that is powerful, but not cleanly legible.

## Primary Findings

### 1. Reporting contains both backend document composition and frontend report-app behavior
Severity: High

Evidence:
- `Services/ReportSerializer.cs`
- `Services/TrendReportComposer.cs`
- `Formatters/HtmlReportRenderer.cs`
- `Templates/report.ui.js`
- `Templates/report.renderers.*.js`

Why this is a problem:
- The serializer/composer path is a backend projection concern.
- The template JS path is a client-side application concern.
- Housing both in the same project is reasonable, but only if their internal boundaries are obvious.
- Right now they feel adjacent rather than deliberately separated.

Refactor opportunity:
- Establish an explicit internal split between:
  - canonical document projection
  - HTML transport/rendering
  - browser interaction/view behavior

### 2. `ReportSerializer` is doing too much orchestration and policy in one class
Severity: High

Evidence:
- `Services/ReportSerializer.cs`

Why this is a problem:
- It builds analyzer sections.
- It builds cross-cutting sections.
- It merges and orders sections.
- It annotates metadata.
- It normalizes contract slots.
- It maps findings and pipeline errors.
- It derives summary values such as total managed bytes.
- It builds domains, correlations, appendix, and executive summary.

This is more than serialization. It is a report assembly engine.

Refactor opportunity:
- Split into narrower collaborators such as:
  - section assembly service
  - finding projection service
  - domain grouping service
  - executive summary builder
  - correlation builder

### 3. Trend composition is layered on top of the single-dump path in a way that duplicates composition work
Severity: High

Evidence:
- `Services/TrendReportComposer.cs`
- `Services/CanonicalReportDocumentFactory.cs`

Why this is a problem:
- Trend composition builds a base document.
- It computes trend findings separately.
- It builds per-dump sections.
- It builds per-dump full documents again.
- It applies additional HTML-specific shaping such as `TrendAnalyzerSections` vs `PerDumpDocuments`.

This is functional, but it suggests the single-dump and trend document paths are not factoring through the cleanest shared model.

Refactor opportunity:
- define a smaller canonical base projection layer
- treat trend as an additive augmentation pipeline
- avoid rebuilding full document structures where a smaller reusable representation would suffice

### 4. `HtmlReportRenderer` is coupling runtime rendering policy, static global overrides, and asset bundling
Severity: Medium-High

Evidence:
- `Formatters/HtmlReportRenderer.cs`
- static `ForcePreRender`
- static `ForceReportStyleVersion`

Why this is a problem:
- Static mutable renderer flags are hard to reason about and easy to misuse in concurrent or multi-report scenarios.
- The same class decides render mode, compacts JSON, bundles assets, and applies global rendering overrides.
- That is too much state and too much responsibility in one formatter.

Refactor opportunity:
- move rendering options into an explicit immutable render-options object
- separate:
  - HTML payload shaping
  - asset bundling
  - render-mode policy

### 5. The template UI has grown into a behavior-dense application surface
Severity: Medium-High

Evidence:
- `Templates/report.ui.js`
- multiple renderer files under `Templates/`
- graph query found no direct tests for `report.ui.js`

Why this is a problem:
- `report.ui.js` manages reading mode, dynamic content sync, collapsible state, accessibility sync, anchor recovery, and interaction policy.
- This is no longer just “small report glue code”.
- The function density and branching increase the maintenance burden.

Refactor opportunity:
- split UI concerns into smaller modules:
  - reading mode controller
  - anchor/navigation integrity
  - dynamic section lifecycle
  - accessibility sync helpers

### 6. Builder sprawl is real, and the project needs better internal grouping
Severity: Medium

Evidence:
- many classes in `SectionBuilders/`
- many classes in `FindingGenerators/`

Why this is a problem:
- The project has many builder/generator classes, which is acceptable.
- The problem is that the internal organizational strategy is still fairly flat.
- As the number grows, discoverability and cohesion get worse unless grouped by domain or capability.

Refactor opportunity:
- group builders and finding generators by domain or feature family
- keep cross-cutting builders in a separate clearly named area

### 7. The project references `Analysis` directly and therefore straddles domain and presentation layers
Severity: Medium

Evidence:
- `DumpDetective.Reporting.csproj`

Why this is a problem:
- Reporting depends on analysis result models and trend types, which is understandable.
- But once Reporting also owns finding generation, section routing, trend augmentation, renderers, and browser app behavior, direct dependence on analysis internals can grow into tight coupling.

Refactor opportunity:
- tighten the contract surface between Analysis and Reporting
- prefer stable result/document contracts over broad reach into analysis-side details

## Structure Review

## Project layout assessment

### What is good
- The project already has recognizable areas: `Abstractions`, `Formatters`, `Models`, `SectionBuilders`, `Services`, `Templates`, `Trend`.
- Embedded resource usage is explicit.
- Runtime template assets are clearly under `Templates/`.

### What is not good enough
- `Services` still carries too much concentrated composition logic.
- `Templates` is effectively a frontend application, but without a similarly explicit internal app structure.
- `FindingGenerators` and `SectionBuilders` are numerous and fairly flat.
- `Serialization` is very small, while `Services` absorbs most of the heavy transformation work.

### Cleanup opportunity
Aim for a more intentional layout, for example:
- `Projection/` for document assembly and mapping
- `Composition/` for section/domain/trend composition
- `Renderers/` for formatter implementations
- `WebApp/` or `Templates/App/` for browser-side modules
- `Features/<Domain>/` for builders and finding generators grouped by capability

## Class Structure Review

### `ReportSerializer`
Assessment:
- central and important
- too broad

Problem:
- It is named like a serializer but behaves like a composition engine.

Recommendation:
- rename or split so naming matches actual responsibility.

### `CanonicalReportDocumentFactory`
Assessment:
- thin wrapper
- acceptable as a seam

Concern:
- it mainly delegates into `ReportSerializer`, so it is only useful if the underlying projection/composition pieces are cleanly separated.

Recommendation:
- keep only if it remains a true composition seam.
- otherwise collapse or repurpose after serializer decomposition.

### `TrendReportComposer`
Assessment:
- useful domain-specific behavior
- structurally too wide

Problem areas:
- trend insight generation
- summary calculation
- per-dump section materialization
- per-dump full document generation
- trend HTML-specific shaping

Recommendation:
- split into trend document planner + specialized builders/adapters.

### `HtmlReportRenderer`
Assessment:
- practical but overloaded

Problem areas:
- mutable static global switches
- payload shaping
- template population
- asset bundling
- fallback behavior

Recommendation:
- move static control into explicit render settings
- keep the formatter instance stateless

### Section builder interfaces
Assessment:
- conceptually sound

Concern:
- the system has many builders but no visible higher-order grouping strategy.

Recommendation:
- preserve interfaces, improve registration and grouping.

## Concrete Refactor Opportunities

## Opportunity 1: Separate projection from presentation runtime
Why:
- This is the clearest conceptual cleanup in the project.

What to do:
- define one internal layer for canonical document/data composition
- define one internal layer for HTML transport/rendering
- define one internal layer for browser behavior

Expected outcome:
- easier navigation
- fewer mixed-responsibility classes

## Opportunity 2: Break `ReportSerializer` into focused builders
Why:
- It is the most concentrated backend complexity hotspot.

What to do:
- extract:
  - section assembler
  - finding mapper
  - domain projector
  - appendix builder
  - executive summary builder
  - correlation builder

Expected outcome:
- smaller units
- better testability
- easier reuse across single-dump and trend modes

## Opportunity 3: Remove static mutable renderer overrides
Why:
- Static state is the wrong shape for renderer policy.

What to do:
- replace `ForcePreRender` and `ForceReportStyleVersion` with explicit render settings passed through the render call or formatter construction.

Expected outcome:
- deterministic rendering behavior
- less hidden cross-call coupling

## Opportunity 4: Give the browser-side report app a real module boundary
Why:
- It already behaves like an app.

What to do:
- split `report.ui.js` into focused modules.
- keep a thin bootstrap file only.

Expected outcome:
- easier targeted testing
- smaller functions
- clearer responsibility boundaries in the client code

## Opportunity 5: Group builders/generators by domain
Why:
- The project is feature-rich, and flat folders do not scale well.

What to do:
- group `FindingGenerators` and `SectionBuilders` by domain families such as:
  - Memory
  - GC
  - Threads
  - Async
  - Runtime
  - Infrastructure
  - CrossCutting

Expected outcome:
- faster discovery
- easier feature ownership

## Opportunity 6: Simplify trend composition after the base projection split
Why:
- Trend logic is important, but easier to clean after the single-dump pipeline is decomposed.

What to do:
- create a smaller base projection that both single and trend flows consume.
- reduce redundant per-dump full-document builds if possible.

Expected outcome:
- less duplication
- clearer distinction between canonical data and trend-specific augmentation

## Opportunity 7: Add direct tests for UI hotspots and composition seams
Why:
- This project will be hard to refactor safely without a harness.

What to test first:
- serializer output shape for representative runs
- trend document composition snapshots
- HTML renderer payload shape
- UI reading mode behavior
- anchor/navigation integrity behavior

## Recommended Cleanup Order

### Step 1
Add focused tests around:
- `ReportSerializer`
- `TrendReportComposer`
- `HtmlReportRenderer`
- `report.ui.js` behavior hotspots

### Step 2
Replace static renderer overrides with explicit render settings.

### Step 3
Decompose `ReportSerializer` into smaller builders.

### Step 4
Split `report.ui.js` into concern-based modules.

### Step 5
Reorganize builders/generators by domain.

### Step 6
Rationalize trend composition around the cleaner base projection model.

## Suggested Target Shape

### Desired responsibility map
- `Projection/*`: mapping analyzer runs to canonical report data
- `Composition/*`: section/domain/executive/trend builders
- `Formatters/*`: output-format implementations only
- `Templates/App/*`: browser-side report behavior modules
- `Features/<Domain>/*`: finding generators and section builders by domain

### Things this project should own clearly
- finding generation
- report document composition
- section building
- format-specific rendering
- browser-side report UX

### Things this project should avoid
- hidden global renderer switches
- serializer classes that are really orchestration engines
- UI modules that are too behavior-dense to test easily

## What to preserve
- strong canonical-document idea
- embedded-resource delivery model for offline reports
- separation between analyzer-section and report-section concepts
- the ability to render multiple output formats from one document model

## What not to do
- Do not replace the embedded-template architecture just to create boundaries.
- Do not introduce a heavy web framework unless there is a clear need.
- Do not redesign all report models before first decomposing the composition logic.

## Bottom Line
`DumpDetective.Reporting` is valuable but currently over-compressed.

Its biggest cleanup need is not fewer capabilities. It is sharper internal separation between:
- canonical report projection
- trend augmentation
- HTML transport/rendering
- browser-side report application behavior

Once those boundaries are explicit, the project can stay feature-rich without feeling structurally overloaded.