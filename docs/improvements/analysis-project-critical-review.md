# DumpDetective.Analysis Critical Review

## Status
Architectural/code-structure review. Last validated against active source: 2026-07-17.

Overall: mostly remediated. The Reporting ownership leak (including the namespace leftover), the pipeline decomposition, and both heavyweight analyzers (`MemoryAnalyzer`, `GCRootAnalyzer`) hosting inline algorithm logic are fully resolved. Remaining work is narrower and localized: one broad cache class (`HeapAnalysisCache`) still mixes cache state with index-build coordination policy, `InsightEngine` is internally organized into rule groups but still lives as one large file, and `MemoryAnalyzer`/`GCRootAnalyzer` still lack direct unit tests of their own (though their extracted projection/reader helpers are directly tested).

## Scope
Project reviewed: `src/DumpDetective.Analysis`

Focus areas: code structure, class/service structure, performance-spine architecture, analyzer organization, cross-cutting policy placement, cleanup/refactor opportunities.

## Executive Summary
`DumpDetective.Analysis` is the strongest project in the current architecture — it holds the real technical core (dump/runtime access, heap indexing, cache management, query/traversal, analyzer execution) and should be preserved as the performance-critical spine.

Much of its complexity is justified by the problem domain; some is still accidental. The refactor goal is not simplification by reduction, but sharper separation between infrastructure, reusable analysis algorithms, per-analyzer coordination, and cross-cutting projection/policy.

### What is good
- Top-level areas (`Dump`, `Cache`, `Indexing`, `Pipeline`, `Query`, `Traversal`, `Insight`, `Trend`, `Analyzers`) map to real responsibilities.
- Indexing layer is clearly separated and performance-driven rather than ad hoc.
- `RuntimeFacade` is a focused, small-surface infrastructure abstraction — worth preserving as-is.
- `QueryEngine` correctly operates on the prebuilt index instead of re-enumerating the raw heap.

## Findings

### 1. Reporting ownership leak — resolved
`DumpDetective.Analysis.csproj` no longer compiles Reporting finding-generator source; ownership boundary is fixed. The namespace leftover is also fixed: all 36 `FindingGenerators/*.cs` files now declare `namespace DumpDetective.Reporting.FindingGenerators`, matching their physical owning project.

### 2. `AnalysisPipeline` cross-cutting policy — resolved
Was: pipeline mixed execution flow, diagnostics publication, cancellation, memory tracking, progress throttling, cleanup/disposal, GC triggering, and post-processing in one class.

Now: decomposed via explicit collaborators — `AnalyzerExecutionRunner`, `AnalysisDiagnosticsPublisher`, `AnalyzerCleanupPolicy`, `AnalyzerResultPostProcessor`, `AnalyzerCollectionPolicyEvaluator`. `AnalysisPipelineTests` exists directly.

### 3. Heavyweight analyzers hosting reusable algorithms inline — resolved
Severity: was Medium-High

- `MemoryAnalyzer.cs` — resolved. Ranking/normalization/scoring logic extracted into `MemoryAnalysisProjection`; the analyzer itself is now thin (107 lines, 18 symbols) and covered by `MemoryAnalysisProjectionTests`.
- `GCRootAnalyzer.cs` — resolved. Kind-grouping, retained-byte estimation, type-name resolution, and severity scoring extracted into `Utilities/GCRootAnalysisProjection`; the analyzer now only reads roots, calls the projection, selects the top-N by severity, and runs BFS path tracing via `HeapTypePathTraversal`.

### 4. `InsightEngine` rule organization — partially resolved
Severity: Medium (down from Medium-High)

Now: reorganized into an explicit `IInsightRuleGroup` interface with grouped rule objects (`BaselineRuleGroup`, `MemoryAndRuntimeRuleGroup`, `CorrelationRuleGroup`), each with its own `Apply(...)`. This is a real structural improvement over one flat procedure, and `InsightEngineTests` exists directly.

Open: it still lives as a single ~1,600-line / 70-symbol file (`Insight/InsightEngine.cs`). The grouping is accurate at the code-organization level, not at the file-splitting level — a reader still opens one large file to see all rules.

Refactor opportunity (optional, low urgency): split rule groups into separate files under `Insight/Rules/`, keeping the small orchestrator in `InsightEngine`.

### 5. `HeapAnalysisCache` mixes caching and index-build orchestration — open
Severity: Medium

Evidence: `Cache/HeapAnalysisCache.cs` (297 lines, 58 symbols), `Cache/IHeapIndexBuilder.cs`.

Root-index reading was extracted into `RootIndexReader`, but the class still mixes cache/query state with adaptive index-build mode selection and build lifecycle control.

Refactor opportunity: keep the dual-interface concept and the class as the unified entry point if useful, but separate cache/query state from index-build coordination/policy internally.

### 6. Analyzer-local traversal duplication — resolved
Severity: was Medium

Shared traversal extracted and adopted — `ObjectGraphTraversal` (used by `AsyncTaskAnalyzer`) and `HeapTypePathTraversal` (used by `GCRootAnalyzer`). `GCRootAnalyzer`'s remaining local grouping/severity logic is now extracted too (see Finding 3), leaving only BFS path tracing (via `HeapTypePathTraversal`) and thin coordination in the analyzer itself.

### 7. Test coverage of heavyweight analysis logic — partially resolved
Severity: Medium

Resolved: `InsightEngineTests`, `RootIndexReaderTests`, `MemoryAnalysisProjectionTests`, and `AnalysisPipelineTests` all exist under `tests/DumpDetective.Tests/Unit/Analysis`.

Open: `MemoryAnalyzer` and `GCRootAnalyzer` still have no direct unit-test file of their own — existing `*AnalyzerDiscrepancyTests` cover cache-vs-live discrepancy behavior, not internal heuristic logic. The extracted `MemoryAnalysisProjection`/`RootIndexReader` helpers being directly tested mitigates but doesn't eliminate this gap for `GCRootAnalyzer`'s remaining un-extracted logic.

## Recommended Cleanup Order (remaining work)

1. Decompose `HeapAnalysisCache` internally: separate cache/query state from index-build coordination policy (Finding 5).
2. Add direct unit tests for `MemoryAnalyzer` and `GCRootAnalyzer` heuristic logic (Finding 7).
3. Optional/low urgency: split `InsightEngine` rule groups into separate files (Finding 4).

## Suggested Target Shape

Desired responsibility map:
- `Dump/*`: runtime and dump loading primitives
- `Indexing/*`: performance-critical index build/storage code
- `Cache/*`: read-mostly cache/query state
- `Pipeline/*`: execution mechanics only
- `Traversal/*`: bounded graph/reference services
- `Query/*`: structured index-based query services
- `Analyzers/*`: thin coordinators over infrastructure and heuristics
- `Insight/*`: modular cross-analyzer rule engine

This project should own: runtime access, heap indexing and cache behavior, bounded query/traversal, analyzer execution, domain result production.

This project should not own: reporting-owned finding-generation implementations, presentation-specific projection logic, host-layer orchestration concerns.

## What to preserve
- Index-first architecture and adaptive memory/disk index approach.
- `RuntimeFacade`, `QueryEngine` direction.
- Performance-aware comments and constraints in indexing code.

## What not to do
- Do not rewrite the indexing layer first.
- Do not flatten analyzers into one generic framework.
- Do not remove performance-oriented specialization in the name of cleanliness.

## Bottom Line
`DumpDetective.Analysis` remains where the architecture is strongest. Its cleanup target is not the performance spine — it's the remaining control-plane complexity around that spine: `HeapAnalysisCache` still doing more than coordination, `InsightEngine`'s single-file size, and a direct-test gap on the two heaviest analyzers. If those are cleaned up while preserving the indexing and cache model, this project stays powerful without feeling overgrown.
