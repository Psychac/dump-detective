# DumpDetective.Core Critical Review

## Status
Architectural/code-structure review. Re-validated against active source on 2026-07-18.

## Executive Summary
`DumpDetective.Core` remains the cleanest project overall — small, and mostly a contract/model layer. Since the last review, the two most ambient-state-related findings have been substantially resolved (context narrowing, intentional-seam confirmation for `IFindingGenerator`), while the two surface-growth findings have gotten worse, not better (`AnalysisOptions` aggregate, `InternalsVisibleTo` breadth). The remaining problems are still boundary-discipline problems, not scale problems:
- Core still depends on ClrMD directly — now explicitly documented as an intentional decision
- `AnalysisOptions` has grown into a very wide options aggregate (29 nested option types)
- internal visibility is still broad (5 friend assemblies, unchanged)
- some abstractions still carry policy/inference behavior (`AnalyzerCategory.Infer`)

This project does not need major redesign. It needs continued tightening on options surface and internal visibility.

## Primary Findings

### 1. Core depends directly on ClrMD — now an explicit, documented decision
Severity: Low (downgraded from High)
Status: Resolved as a conscious architectural choice

Evidence:
- `DumpDetective.Core.csproj` still references `Microsoft.Diagnostics.Runtime`
- `Models/AnalysisContext.cs` exposes `ClrRuntime`/`ClrHeap`, now with an explicit comment: *"Intentional boundary decision (Phase 7): Core remains dump-runtime-aware. AnalysisContext carries ClrRuntime/ClrHeap as shared execution substrate."*

Assessment:
- Opportunity 1 from the prior review (decide and document the ClrMD dependency) is done. The coupling itself is unchanged, but it is no longer an open architectural question — it's a recorded decision. No further action needed unless the decision is revisited.

### 2. `AnalysisContext` — no longer a broad ambient bag
Severity: Low (downgraded from Medium-High)
Status: Largely resolved

Evidence:
- `Models/AnalysisContext.cs` now exposes exactly 7 members: `Runtime`, `Heap` (derived), `Cache`, `AnalysisOptions`, `Diagnostics`, `DiagnosticsSink`, `Progress`.
- The legacy type-keyed `Options` dictionary called out in the prior review has been fully retired.

Assessment:
- Opportunity 2 is effectively complete. The type is now a small, explicit collaborator surface rather than an ambient environment. No further narrowing needed.

### 3. `AnalyzerCategory.Infer` — policy inference still lives in the contract layer
Severity: Medium
Status: Unchanged, still open

Evidence:
- `Abstractions/IAnalyzer.cs`: `string Category => AnalyzerCategory.Infer(Name);`
- `AnalyzerCategory` is an `internal static class` performing name-based category inference.

Why this is a problem:
- Contracts are cleanest when declarative. Deriving category from analyzer name string-matching is policy, and can silently drift from actual categorization used by Reporting.

Refactor opportunity (unchanged):
- Consider making category an explicit property set by each analyzer rather than inferred.

### 4. `IFindingGenerator` — confirmed as an intentional, now-tightened seam
Severity: Low (downgraded from Medium)
Status: Resolved — kept by design, access tightened

Evidence:
- `Abstractions/IFindingGenerator.cs` is now `internal interface IFindingGenerator` (not public).
- Every implementer lives in `DumpDetective.Reporting/FindingGenerators/*` — confirmed as the sole consumer, validating that this is genuinely the Analysis→Reporting seam rather than an unused or misplaced contract.

Assessment:
- The prior review's open question ("does this belong in Core?") is answered: yes, it's a real cross-project seam, and making it `internal` (reachable only via `InternalsVisibleTo`) is a reasonable tightening. No further action needed unless Reporting ownership changes.

### 5. `AnalysisOptions` — options aggregate has grown significantly wider
Severity: Medium-High (raised from Medium)
Status: Worse since last review

Evidence:
- `Options/AnalysisOptions.cs` now aggregates **29** nested option properties (one per analyzer, e.g. `AllocationPatternAnalysis`, `BoxingAnalysis`, `GCRootAnalysis`, `StaticRootLeakAnalysis`, `MemoryAnalysis`, etc.), up from a narrower set in the prior review.
- `Options/` folder now holds 35 files.
- The reflection-based `TryGet<T>` lookup (walks `GetType().GetProperties()`, matches by assignability) is still present and still the only generic access path.

Why this is a problem:
- Every new analyzer option type adds a required property to this one record, making it a growth magnet with no natural ceiling.
- `TryGet<T>` is O(n) reflection per call and silently returns the first assignable match — fragile if two option types ever share a compatible shape.

Refactor opportunity:
- Prefer explicit property access in call sites (already largely true); treat `TryGet<T>` as compatibility glue only.
- Consider whether analyzer-specific options should be resolved via a keyed/typed registry instead of one flat record, if growth continues.

### 6. Internal visibility remains broad
Severity: Medium-Low
Status: Unchanged, still open

Evidence:
- `DumpDetective.Core.csproj` still declares 5 `InternalsVisibleTo` entries: `BenchmarkSuite1`, `DumpDetective.Analysis`, `DumpDetective.Reporting`, `DumpDetective.Cli`, `DumpDetective.Tests`.
- By contrast, `DumpDetective.Analysis` itself only grants `InternalsVisibleTo` to `DumpDetective.Tests` — showing a narrower pattern is already used elsewhere in the solution and could be a model for Core.

Refactor opportunity (unchanged):
- Audit which of the 5 friend assemblies actually need internal access vs. what could be exposed as public contract. `IFindingGenerator` (Finding #4) is a case where `internal` + friend access is justified since Reporting is the sole, intentional consumer — the same reasoning should be applied per-assembly rather than granting blanket access to all 5.

### 7. Configuration primitives — reorganized, one type appears retired
Severity: Low
Status: Structural change, worth noting only

Evidence:
- The `Configuration/` folder from the prior review no longer exists. `ReportFormat` and `ReportStyleVersion` now live under `Enums/`.
- `ReportAudience`, previously listed as a Core configuration primitive, no longer exists anywhere in the codebase (confirmed via full-project search).

Assessment:
- No action needed — this is a folder rename/consolidation (`Configuration/` → `Enums/`) plus removal of an apparently-unused concept. Flagged only so the doc's file references stay accurate.

## Structure Review

### What is good
- Small project; understandable top-level folders: `Abstractions`, `Models`, `Options`, `Enums`, `Utilities`.
- `AnalysisContext` is now a tight, explicit collaborator surface (Finding #2).
- `IFindingGenerator` is confirmed as a real, intentionally-scoped seam (Finding #4).

### What is not good enough
- `Options/` is the largest folder (35 files) and still growing linearly with analyzer count.
- `Abstractions/` still mixes pure interfaces with policy helpers (`AnalyzerCategory.Infer`).
- `InternalsVisibleTo` breadth is unchanged from the original review.

## Concrete Refactor Opportunities (remaining)

### Opportunity A: Remove policy inference from `IAnalyzer`/`AnalyzerCategory`
Make `Category` an explicit per-analyzer value instead of inferred from `Name`.

### Opportunity B: Contain `AnalysisOptions` growth
Keep the aggregate for now, but resist further ad-hoc growth; treat `TryGet<T>` as compatibility glue, not the primary access model. Revisit a keyed/typed registry if growth continues past current scale.

### Opportunity C: Narrow `InternalsVisibleTo` per-assembly
Apply the same reasoning used for `IFindingGenerator` (grant only to the actual sole/primary consumer) across the other 4 friend-assembly entries; use `DumpDetective.Analysis`'s narrower pattern (friend access to Tests only) as the model.

## What to preserve
- Small project size, clear folder structure, explicit option records.
- The now-narrow `AnalysisContext` shape — do not let ambient members creep back in.
- The documented, intentional ClrMD-aware boundary decision.

## What not to do
- Do not over-engineer Core into a framework.
- Do not create abstraction layers that exist only for purity.
- Do not split the project further unless dependency direction really requires it.

## Bottom Line
`DumpDetective.Core` has improved since the last review on the two ambient-coupling findings (context shape, `IFindingGenerator` seam) and regressed on the two surface-growth findings (`AnalysisOptions` width, unchanged `InternalsVisibleTo` breadth). Remaining work is narrow and mechanical:
- contain `AnalysisOptions` growth
- narrow `InternalsVisibleTo` per-assembly
- make analyzer category explicit rather than inferred

No dramatic refactor program needed — Core can stay small and stable if these three items are addressed opportunistically.
