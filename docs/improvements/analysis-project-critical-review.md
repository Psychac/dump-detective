# DumpDetective.Analysis Critical Review

## Status
Architectural/code-structure review.

Validated against active source on 2026-05-30. Re-validated against active source on 2026-07-17.

## Implementation Status Update (2026-07-17)
Overall status: Partially remediated (major phase goals delivered; remaining cleanup is now narrower and more localized). Consistent with the 2026-05-30 assessment; no regression found, but two nuances below were not previously called out.

Addressed in implementation:
- Reporting ownership leak removed: `DumpDetective.Analysis.csproj` no longer links/compiles Reporting finding-generator source (verified directly against the current csproj — no `Compile Include` targeting `..\DumpDetective.Reporting\FindingGenerators`).
- `AnalysisPipeline` decomposed via explicit collaborators (`AnalyzerExecutionRunner`, `AnalysisDiagnosticsPublisher`, `AnalyzerCleanupPolicy`, `AnalyzerResultPostProcessor`), plus a newer `AnalyzerCollectionPolicyEvaluator` collaborator (added as part of the Core boundary-tightening phase).
- `InsightEngine` reorganized internally into grouped rule objects (`Apply(...)` per rule), which is a real structural improvement over one flat procedure — but it still lives as a single ~80KB / ~70-symbol file (`Insight/InsightEngine.cs`). The "grouped rule-set" claim is accurate at the code-organization level, not at the file-splitting level; a reader still opens one large file to see all rules.
- Shared traversal extraction adopted in analyzer flow (`ObjectGraphTraversal` used by `AsyncTaskAnalyzer`, `HeapTypePathTraversal` used by `GCRootAnalyzer`).
- Shared root-index reading extracted into `RootIndexReader` and reused by `GCRootAnalyzer` and `HeapAnalysisCache`.
- Memory projection/ranking logic extracted out of `MemoryAnalyzer` into `MemoryAnalysisProjection`; `MemoryAnalyzer.cs` itself is now small (~5.2KB, 18 symbols), confirming the extraction actually shrank the analyzer rather than just adding a helper alongside it.
- Focused direct tests confirmed present: `InsightEngineTests`, `RootIndexReaderTests`, `MemoryAnalysisProjectionTests`, and `AnalysisPipelineTests` all exist under `tests/DumpDetective.Tests/Unit/Analysis`.

New/refined finding since last review:
- The Reporting `FindingGenerators/*.cs` files (now physically owned by and compiled in `DumpDetective.Reporting`) still declare `namespace DumpDetective.Analysis.FindingGenerators`. The project-ownership boundary is genuinely fixed (Analysis no longer compiles them), but the namespace is a leftover from the pre-move state and misleads readers about which project owns the type. Low severity, cheap to fix (rename namespace to `DumpDetective.Reporting.FindingGenerators`), but worth doing before it calcifies further.

Remaining follow-on cleanup:
- heavyweight analyzer decomposition is still incomplete, but the broadest local algorithm blocks have now been peeled off (`MemoryAnalyzer` is now thin; `GCRootAnalyzer` is thinner but still ~10KB/22 symbols and still hosts local BFS/grouping logic beyond the extracted `RootIndexReader`)
- `HeapAnalysisCache` internal split is still pending beyond the root-index reader extraction (still ~11.8KB / 58 symbols)
- shared traversal/query expansion is partial rather than comprehensive
- `MemoryAnalyzer` and `GCRootAnalyzer` still have no direct unit-test file of their own (the existing `*AnalyzerDiscrepancyTests` cover cache-vs-live discrepancy behavior, not internal heuristic logic); the extracted `MemoryAnalysisProjection`/`RootIndexReader` helpers are directly tested, which mitigates but does not eliminate this gap

## Scope
Project reviewed: `src/DumpDetective.Analysis`

Focus areas:
- code structure
- class/service structure
- performance-spine architecture
- analyzer organization
- cross-cutting policy placement
- cleanup and refactor opportunities for a cleaner project

## Executive Summary
`DumpDetective.Analysis` is the strongest project in the current architecture.

It contains the real technical core of the product:
- dump/runtime access
- heap indexing
- cache management
- query/traversal surfaces
- analyzer execution

The important distinction is this:
- much of the complexity here is justified by the problem
- some of the complexity is still accidental and should be cleaned up

The project should be preserved as the performance-critical spine.

The refactor goal is not simplification by reduction. It is simplification by sharper separation between:
- infrastructure
- reusable analysis algorithms
- per-analyzer coordination
- cross-cutting projection/policy

## Primary Findings

### 1. `Analysis` still owns a Reporting concern through linked finding-generator compilation
Severity: High

Evidence:
- `DumpDetective.Analysis.csproj` compiles `..\DumpDetective.Reporting\FindingGenerators\*.cs`

Why this is a problem:
- This is the clearest structural boundary violation in the active solution.
- Finding generation is presentation/report interpretation work, not core analysis execution.
- It makes project boundaries look cleaner than they really are.

Refactor opportunity:
- move finding-generator ownership fully into Reporting
- keep Analysis focused on domain result production and execution infrastructure

### 2. `AnalysisPipeline` is carrying too much cross-cutting operational policy
Severity: High

Evidence:
- `Pipeline/AnalysisPipeline.cs`

Why this is a problem:
- The pipeline does execution flow, diagnostics publication, cancellation handling, exception capture, memory tracking, progress throttling, cleanup/disposal, optional GC triggering, and finding-generation post-processing.
- Those are all related to execution, but not all belong in one pipeline class.

This makes the pipeline hard to reason about and hard to evolve without incidental coupling.

Refactor opportunity:
- split execution concerns into collaborators such as:
  - analyzer runner
  - diagnostics publisher
  - analyzer cleanup policy
  - progress heartbeat adapter
  - post-processing/enrichment step

### 3. Some analyzers are too large and are implementing reusable algorithms inline
Severity: High

Evidence:
- `Analyzers/MemoryAnalyzer.cs`
- `Analyzers/GCRootAnalyzer.cs`

Why this is a problem:
- `MemoryAnalyzer` does ranking, normalization, histogram derivation, scoring, retained-estimation coordination, and result shaping.
- `GCRootAnalyzer` does root file reading, grouping, severity scoring, and local BFS traversal.

These analyzers are not only coordinating domain logic. They are also hosting reusable algorithmic machinery.

Refactor opportunity:
- extract reusable services for:
  - root index reading
  - bounded graph traversal
  - retained-size estimation
  - ranking/scoring helpers
  - common heuristic calculations

### 4. `InsightEngine` has become a large rule-bank without explicit rule boundaries
Severity: Medium-High

Evidence:
- `Insight/InsightEngine.cs`

Why this is a problem:
- The engine contains a large number of thresholds, rule methods, and cross-domain correlations in a single class.
- It is stateless and clean in one sense, but structurally it is becoming a monolithic rule host.

Refactor opportunity:
- split into rule objects or grouped rule sets by domain/correlation family
- keep a small orchestrator that executes registered insight rules over the run set

### 5. `HeapAnalysisCache` is doing both caching and index-build orchestration
Severity: Medium

Evidence:
- `Cache/HeapAnalysisCache.cs`
- `Cache/IHeapIndexBuilder.cs`

Why this is a problem:
- The dual-interface split is good.
- But the implementation still mixes cache responsibilities with index-build mode selection and build lifecycle control.
- It is a pragmatic design, but the class is broad.

Refactor opportunity:
- keep the dual-interface concept
- consider separating:
  - cache/query state
  - index build coordination/policy

### 6. Query/traversal infrastructure exists, but analyzers still duplicate traversal logic locally
Severity: Medium

Evidence:
- `Traversal/ReferenceGraph.cs`
- `Query/QueryEngine.cs`
- local BFS implementation inside `GCRootAnalyzer`

Why this is a problem:
- The project already has a `Traversal` area, but it is thin.
- At least some analyzers still implement traversal/search logic themselves.
- That weakens the benefit of having shared traversal infrastructure.

Refactor opportunity:
- expand shared traversal services intentionally
- move analyzer-local BFS or graph search code into reusable traversal components where appropriate

### 7. Test visibility appears weak around heavyweight analysis logic
Severity: Medium

Evidence:
- graph query found no direct tests for `Analyzers/MemoryAnalyzer.cs`
- graph query found no direct tests for `Insight/InsightEngine.cs`

Why this is a problem:
- These are high-value, heuristic-heavy units.
- Cleanup will be riskier if behavior is only indirectly covered.

Refactor opportunity:
- add focused tests around heavy analyzers and insight-rule behavior before extracting internals

## Structure Review

## Project layout assessment

### What is good
- The top-level areas are strong and mostly map to real responsibilities:
  - `Dump`
  - `Cache`
  - `Indexing`
  - `Pipeline`
  - `Query`
  - `Traversal`
  - `Insight`
  - `Trend`
  - `Analyzers`

### What is especially good
- The indexing layer is clearly separated and feels performance-driven rather than ad hoc.
- `RuntimeFacade` is a good example of a focused analysis infrastructure abstraction.
- `QueryEngine` correctly operates on the prebuilt index instead of re-enumerating the raw heap.

### What is not good enough
- `Analyzers` contains both lightweight coordinator analyzers and heavyweight algorithm hosts.
- `Pipeline` contains more execution policy than a minimal pipeline should.
- `Insight` is conceptually cross-cutting, but structurally monolithic.
- The linked finding-generator compilation distorts project ownership.

## Class Structure Review

### `RuntimeFacade`
Assessment:
- strong
- focused
- worth preserving as-is conceptually

Why:
- clear responsibility
- obvious performance purpose
- small public surface

### `HeapAnalysisCache`
Assessment:
- useful and central
- too broad in implementation scope

Why:
- it combines cache state, adaptive build selection, root caches, type statistics, and several targeted memoization behaviors.

Recommendation:
- preserve as a central access point if desired, but decompose internally.

### `AnalysisPipeline`
Assessment:
- important but overloaded

Why:
- it is both pipeline engine and operational policy host.

Recommendation:
- separate pipeline mechanics from execution policy helpers.

### `FindingGenerationPipeline`
Assessment:
- small and clean locally
- architecturally misplaced in this project

Recommendation:
- migrate ownership out of Analysis.

### `QueryEngine`
Assessment:
- good directionally
- small and disciplined

Opportunity:
- grow this concept instead of duplicating ad hoc index-query logic across analyzers.

### `ReferenceGraph`
Assessment:
- useful start
- currently too limited to anchor all shared traversal needs

Recommendation:
- expand shared traversal deliberately if multiple analyzers need bounded graph/search behaviors.

### `MemoryAnalyzer`
Assessment:
- useful analyzer
- structurally too broad

Recommendation:
- extract ranking/scoring and retained-estimation helpers.

### `GCRootAnalyzer`
Assessment:
- good example of index-first design
- structurally too broad

Recommendation:
- extract root reader and traversal/search helpers.

### `InsightEngine`
Assessment:
- valuable
- should evolve into a rule set, not a monolith

Recommendation:
- treat it as an insight-rule host with grouped rule modules.

## Concrete Refactor Opportunities

## Opportunity 1: Remove the Reporting ownership leak first
Why:
- This is the cleanest architectural correction in the project.

What to do:
- stop compiling Reporting finding-generator files into Analysis.

Expected outcome:
- real project boundaries match the intended architecture.

## Opportunity 2: Split the execution pipeline into smaller collaborators
Why:
- This improves comprehension without touching hot indexing code first.

What to do:
- separate:
  - analyzer invocation
  - diagnostics emission
  - memory tracking
  - cleanup/GC policy
  - post-analysis enrichment

Expected outcome:
- cleaner pipeline code
- better targeted tests

## Opportunity 3: Extract reusable analyzer algorithms into shared services
Why:
- This is the best way to shrink heavy analyzers without reducing capability.

What to do:
- introduce services in areas such as:
  - `Traversal/`
  - `Heuristics/`
  - `Readers/`
  - `Scoring/`

Expected outcome:
- analyzers become coordinators over reusable analysis primitives

## Opportunity 4: Turn `InsightEngine` into a rule pipeline
Why:
- It is already a rule bank; make that explicit.

What to do:
- group rules by domain or concern
- create an orchestrator that runs them in sequence over the run set

Expected outcome:
- easier extension
- smaller rule files
- more focused tests

## Opportunity 5: Decompose `HeapAnalysisCache` internally, not conceptually
Why:
- The concept is good, but the implementation breadth is growing.

What to do:
- keep the cache as the main entry point if useful
- extract helper components for:
  - type statistics hydration/building
  - root cache management
  - per-feature memoization helpers
  - adaptive index selection policy

Expected outcome:
- lower cognitive load without losing the unified cache abstraction

## Opportunity 6: Expand shared traversal/query instead of duplicating local search logic
Why:
- The project already has the right conceptual seams.

What to do:
- strengthen `Traversal` and `Query` as first-class reusable layers.

Expected outcome:
- less analyzer-local graph logic
- more consistent bounded traversal policies

## Opportunity 7: Add focused tests around heuristic-heavy units
Why:
- Refactoring heuristic logic safely requires direct harnesses.

What to test first:
- `MemoryAnalyzer`
- `GCRootAnalyzer`
- `InsightEngine`
- pipeline failure/cancellation/continue-on-failure behavior

## Recommended Cleanup Order

### Step 1
Add focused tests around:
- `AnalysisPipeline`
- `MemoryAnalyzer`
- `GCRootAnalyzer`
- `InsightEngine`

### Step 2
Remove finding-generator ownership from Analysis.

### Step 3
Split `AnalysisPipeline` into smaller collaborators.

### Step 4
Extract reusable traversal/reader/scoring services from heavy analyzers.

### Step 5
Refactor `InsightEngine` into grouped rules.

### Step 6
Decompose `HeapAnalysisCache` internally only where it improves clarity.

## Suggested Target Shape

### Desired responsibility map
- `Dump/*`: runtime and dump loading primitives
- `Indexing/*`: performance-critical index build/storage code
- `Cache/*`: read-mostly cache/query state
- `Pipeline/*`: execution mechanics only
- `Traversal/*`: bounded graph/reference services
- `Query/*`: structured index-based query services
- `Analyzers/*`: thin coordinators over infrastructure and heuristics
- `Insight/*`: modular cross-analyzer rule engine

### Things this project should own clearly
- runtime access
- heap indexing and cache behavior
- bounded query/traversal
- analyzer execution
- domain result production

### Things this project should not own
- reporting-owned finding generation implementations
- presentation-specific projection logic
- host-layer orchestration concerns

## What to preserve
- index-first architecture
- adaptive memory/disk index approach
- `RuntimeFacade`
- `QueryEngine` direction
- performance-aware comments and constraints in indexing code

## What not to do
- Do not rewrite the indexing layer first.
- Do not flatten analyzers into one generic framework.
- Do not remove performance-oriented specialization in the name of cleanliness.

## Bottom Line
`DumpDetective.Analysis` is where the architecture is strongest.

Its cleanup target is not the performance spine. It is the control-plane complexity around that spine:
- the Reporting ownership leak
- large analyzers doing too much themselves
- pipeline classes carrying broad operational policy
- insight rules accumulating in one monolithic engine

If those are cleaned up while preserving the indexing and cache model, this project can stay powerful without feeling overgrown.