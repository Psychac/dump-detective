# DumpDetective.Core Critical Review

## Status
Architectural/code-structure review.

Validated against active source on 2026-05-30. Re-validated against active source on 2026-07-17.

## Implementation Status Update (2026-07-17)
Overall status: Partially remediated — narrower than the 2026-05-30 assessment claimed. Directly inspecting `DumpDetective.Core.csproj` shows Finding #6 (`InternalsVisibleTo`) is **not** resolved; the previous "reduced/audited" claim does not match current source and is corrected below.

Addressed in implementation:
- boundary intent documented: Core is intentionally dump-runtime-aware for current architecture
- legacy ambient option bag removed from `AnalysisContext` in favor of typed `AnalysisOptions` — confirmed: `Models/AnalysisContext.cs` is now a small type (~1.3KB / 13 symbols), consistent with the legacy `Options` dictionary having been retired
- diagnostics collection policy inference moved out of Core into Analysis (`AnalyzerCollectionPolicyEvaluator`)

Correction to prior status:
- The claim that "`InternalsVisibleTo` surface reduced/audited to avoid broad Core internals exposure" is **inaccurate**. `DumpDetective.Core.csproj` still declares five `InternalsVisibleTo` entries: `BenchmarkSuite1`, `DumpDetective.Analysis`, `DumpDetective.Reporting`, `DumpDetective.Cli`, `DumpDetective.Tests` — the same breadth as the original review. This also contradicts `consolidated-refactor-program.md`'s Phase 7 note claiming retention of "a single Analysis-to-tests entry only." Finding #6 below should be treated as still fully open, not resolved.

Remaining follow-on cleanup:
- Core remains directly ClrMD-aware by design decision (not migrated to runtime-neutral contracts)
- some contract-level policy helpers (e.g., category inference patterns) remain candidates for future simplification
- `IFindingGenerator` placement remains a seam decision to revisit only if boundary strategy changes — unchanged, still in `Abstractions/IFindingGenerator.cs`
- `InternalsVisibleTo` reduction (Opportunity 5 / Finding #6) has not been started; still five broad friend-assembly entries

## Scope
Project reviewed: `src/DumpDetective.Core`

Focus areas:
- code structure
- contract and model structure
- option/configuration shape
- dependency-boundary health
- cleanup and refactor opportunities for a cleaner project

## Executive Summary
`DumpDetective.Core` is the cleanest project overall.

It is already relatively small and mostly behaves like a contract/model layer.

Its problems are not scale problems. They are boundary-discipline problems:
- Core still depends on ClrMD directly
- some abstractions contain policy/inference behavior rather than staying purely contractual
- options are accumulating into a wide, centralized shape
- internal visibility is broad, which reduces the strength of the boundary

This project does not need major redesign. It needs tightening.

## Primary Findings

### 1. `Core` depends directly on ClrMD, which weakens its role as a stable boundary layer
Severity: High

Evidence:
- `DumpDetective.Core.csproj` references `Microsoft.Diagnostics.Runtime`
- `Abstractions/AnalysisContext.cs` exposes `ClrRuntime` and `ClrHeap`
- `Abstractions/IAnalyzer.cs` imports `Microsoft.Diagnostics.Runtime`

Why this is a problem:
- A small core contract layer is usually strongest when it depends on domain-neutral abstractions, not runtime implementation libraries.
- By exposing ClrMD types directly, Core becomes tied to the dump-runtime implementation surface.
- That makes it harder to keep Core stable if runtime access patterns change or if you ever want a stricter boundary between contracts and runtime infrastructure.

Refactor opportunity:
- decide whether Core is truly meant to be a platform-neutral contract layer.
- if yes, move direct ClrMD exposure out of Core over time.
- if no, document that Core is a dump-analysis contract layer rather than a general domain-core layer.

### 2. `AnalysisContext` is acting as a broad ambient state bag
Severity: Medium-High

Evidence:
- `Models/AnalysisContext.cs`

Why this is a problem:
- It exposes runtime, heap, cache, typed options, legacy type-keyed options, diagnostics sink, and progress.
- This is convenient, but it encourages analyzers to depend on a large implicit environment instead of explicit collaborators.

Refactor opportunity:
- narrow the context over time
- retire the legacy `Options` dictionary once the typed option model is complete
- prefer strongly typed access over ambient bags where feasible

### 3. Core abstractions include policy/inference behavior that may not belong in the contract layer
Severity: Medium

Evidence:
- `Abstractions/IAnalyzer.cs`
- `AnalyzerCategory.Infer(...)`
- `AnalyzerDomainResultExtensions.Stamp(...)`

Why this is a problem:
- Contracts are usually cleaner when they define the shape but not product heuristics.
- Category inference based on analyzer name is convenient, but it is also policy.
- That policy may drift or become mismatched with reporting/domain categorization later.

Refactor opportunity:
- consider making category explicit rather than inferred
- keep extension helpers if they reduce noise, but separate policy helpers from pure contracts where possible

### 4. `IFindingGenerator` living in Core makes sense only if it is a stable cross-project contract
Severity: Medium

Evidence:
- `Abstractions/IFindingGenerator.cs`

Why this is a problem:
- If finding generation is fully owned by Reporting, this interface may not belong in Core long-term.
- If it stays in Core, that should be because it is intentionally the stable seam between Analysis and Reporting.

Refactor opportunity:
- revisit the location of `IFindingGenerator` when the finding-generation ownership cleanup happens
- keep it in Core only if that seam remains a consciously shared contract

### 5. `AnalysisOptions` is becoming a wide central options aggregate
Severity: Medium

Evidence:
- `Options/AnalysisOptions.cs`

Why this is a problem:
- The type now aggregates many analyzer-specific options.
- It is convenient for transport, but it can turn into a very wide “everything bag” over time.
- `TryGet<T>` uses reflection to locate options by type, which is flexible but also loose and slightly opaque.

Refactor opportunity:
- keep the aggregate if it is operationally useful
- but prefer explicit property access in most code paths
- treat reflection-based `TryGet<T>` as compatibility glue, not the primary access model

### 6. Internal visibility is broad, which weakens boundary enforcement
Severity: Medium-Low
Status (2026-07-17): Still open — unchanged since original review.

Evidence:
- `InternalsVisibleTo` includes Analysis, Reporting, Cli, Tests, BenchmarkSuite1 (verified directly against `DumpDetective.Core.csproj`, still five entries)

Why this is a problem:
- Broad friend access can be pragmatic.
- But it also reduces the pressure to maintain tight public contracts.
- Over time, this can let internal implementation details become quasi-shared API.

Refactor opportunity:
- reduce `InternalsVisibleTo` usage where possible
- prefer explicit public contracts for genuinely shared behavior

### 7. Core configuration types are small and useful, but they should stay low-policy
Severity: Low

Evidence:
- `Configuration/ReportAudience.cs`
- `Configuration/ReportFormat.cs`
- `Configuration/ReportStyleVersion.cs`
- `Options/ExecutionPolicy.cs`

Why this is a problem:
- No acute issue today.
- The risk is that Core slowly absorbs more operational policy and report-specific concerns.

Refactor opportunity:
- keep Core configuration primitive and stable
- do not let it become a product-policy dumping ground

## Structure Review

## Project layout assessment

### What is good
- The project is small.
- Top-level folders are understandable: `Abstractions`, `Models`, `Options`, `Configuration`, `Utilities`.
- The project does not appear structurally noisy.

### What is not good enough
- The `Abstractions` folder contains both pure interfaces and helper/policy logic.
- `Options` is trending toward a broad central aggregate surface.
- The Core/runtime boundary is blurrier than the project name implies because of direct ClrMD dependency.

### Cleanup opportunity
Keep Core intentionally narrow:
- contracts
- small shared models
- stable option/configuration primitives

Avoid letting it absorb:
- runtime-library coupling unless explicitly intended
- reporting policy
- orchestration logic
- feature heuristics

## Class and Contract Review

### `IAnalyzer`
Assessment:
- small and useful
- slightly too opinionated for a pure contract

Concern:
- default category inference in Core is convenient but policy-laden
- default `IDisposable` implementation is pragmatic, but makes the contract carry a convenience behavior as well

Recommendation:
- keep the interface small
- consider making category explicit over time

### `AnalysisContext`
Assessment:
- practical
- too ambient

Recommendation:
- gradually narrow its role or split off optional capability access patterns
- remove legacy option mechanisms after migration

### `AnalysisOptions`
Assessment:
- useful transport object
- at risk of becoming over-centralized

Recommendation:
- preserve as a stable aggregate for now
- avoid adding reflection-heavy or generic lookup patterns as the main access strategy

### `ExecutionPolicy`
Assessment:
- good conceptually
- should remain minimal and cross-cutting only

Recommendation:
- keep it focused on genuine execution bounds and not analyzer-specific heuristics

### `IFindingGenerator`
Assessment:
- depends on the future architecture decision

Recommendation:
- revisit after finding-generation ownership is finalized

## Concrete Refactor Opportunities

## Opportunity 1: Clarify whether Core is runtime-neutral or dump-runtime-aware
Why:
- This is the main architectural question for the project.

What to do:
- make an explicit decision:
  - either Core may depend on ClrMD and act as dump-analysis contracts
  - or Core should be decoupled from ClrMD over time

Expected outcome:
- clearer dependency intent across the solution

## Opportunity 2: Narrow `AnalysisContext`
Why:
- Ambient bags grow easily and are hard to constrain.

What to do:
- reduce legacy option paths
- keep only the capabilities analyzers actually need as stable context members

Expected outcome:
- less hidden coupling
- clearer analyzer dependencies

## Opportunity 3: Remove policy inference from core contracts where possible
Why:
- Core is strongest when it is declarative and boring.

What to do:
- reconsider category inference-by-name
- keep policy helpers outside the minimum contract layer where practical

Expected outcome:
- sharper contract layer

## Opportunity 4: Keep options explicit and resist generic option plumbing
Why:
- Wide option surfaces are manageable if they stay explicit.

What to do:
- prefer direct properties over reflective lookup in active code
- retire compatibility paths when migration completes

Expected outcome:
- easier reasoning
- less hidden runtime behavior

## Opportunity 5: Reduce unnecessary `InternalsVisibleTo`
Why:
- Stronger boundaries force cleaner contracts.

What to do:
- audit internal sharing and narrow it where possible.

Expected outcome:
- better separation discipline across projects

## Recommended Cleanup Order

### Step 1
Decide and document whether Core is allowed to remain ClrMD-aware.

### Step 2
Retire or reduce legacy option plumbing in `AnalysisContext` and `AnalysisOptions`.

### Step 3
Revisit placement of `IFindingGenerator` after the Reporting/Analysis boundary cleanup.

### Step 4
Reduce policy helpers in `Abstractions` where that improves contract clarity.

### Step 5
Audit `InternalsVisibleTo` and narrow sharing where practical.

## Suggested Target Shape

### Desired responsibility map
- `Abstractions/*`: minimal stable interfaces only
- `Models/*`: stable cross-project records and result models
- `Options/*`: explicit configuration records
- `Configuration/*`: enum-like product configuration primitives
- `Utilities/*`: only generic shared helpers with very low policy content

### Things this project should own clearly
- shared result and finding models
- small interface contracts
- stable options and execution-bound records

### Things this project should avoid
- runtime-specific implementation assumptions unless explicitly accepted
- inferred product policy in contracts
- orchestration helpers
- presentation logic

## What to preserve
- small project size
- clear folder structure
- stable shared record types
- explicit option records

## What not to do
- Do not over-engineer Core into a framework.
- Do not create abstraction layers that exist only for purity.
- Do not split the project further unless dependency direction really requires it.

## Bottom Line
`DumpDetective.Core` is in decent shape.

It does not need a dramatic refactor program. It needs a boundary cleanup program:
- clarify the ClrMD dependency decision
- narrow ambient context shape
- keep contracts low-policy
- resist gradual expansion into a generic dumping ground

If that discipline holds, Core can remain small and stable while the heavier projects evolve around it.