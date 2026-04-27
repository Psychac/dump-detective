# DumpDetective — Architecture Review & Improvement Guide

> **Branch:** `optimize`  
> **Reviewed:** All four projects — `Core`, `Analysis`, `Reporting`, `Cli`  
> **Principles applied:** KISS (Keep It Simple, Stupid), SOLID, project guidelines

---

## Changelog

| Date | Issue | Status | Notes |
|------|-------|--------|-------|
| 2025 | CRITICAL-01 | ✅ **Done** | `Reporting → Analysis` reference added; 30 domain types moved to `Analysis/Models/AnalyzerDomainModels.cs`; `Core/Models/AnalyzerDomainResult.cs` reduced from 285 → 23 lines; zero individual file changes via `GlobalUsings.cs` in both projects |
| 2025 | CRITICAL-02 | ✅ **Done** | `TrendAnalyzer` refactored to primary DI constructor; 16 comparers registered in `ServiceRegistration.cs`; `TrendAnalyzer` injected into `DumpAnalysisService`; dead duplicate `Core/Abstractions/IAnalyzerTrendComparer.cs` removed |
| 2025 | CRITICAL-03 | ✅ **Done** | `FindingGeneratorError` added to `AnalyzerRunResult`; `FindingGenerationPipeline` catch now populates it; `ReportBuilder` emits a `Warning` section; `GenerateFindingsStage` warns per-generator failure to console immediately |
| 2025 | MINOR-14 | ✅ **Done** | Stage 4 comment block added to `SingleDumpPipelineState.cs` between Stage 3 and Stage 5 |
| 2025 | MAJOR-07 | ✅ **Done** | `AnalysisContext` → `RuntimeAnalysisContext` in `Analysis/Pipeline/`; all 6 alias usages removed across Cli, BenchmarkSuite1, and test files; architecture test updated to reflect correct `Reporting → Analysis` dependency |
| 2025 | MAJOR-05 | ✅ **Done** | `AnalysisContext.Options` key changed `string` → `Type`; `GetOption<T>()` extension added; `RuntimeAnalysisContext` redundant properties removed; 3 magic-key analyzers fixed; `CollectionAnalyzer` wired to context; `CollectionAnalyzerOptions` added to `ResolvedExecutionOptions` and config |
| 2025 | MAJOR-06 | ✅ **Done** | `IHeapIndexBuilder` interface added to `Analysis/Cache/`; `HeapAnalysisCache` implements it; state bag split into `IHeapIndexBuilder HeapIndexBuilder` + `IHeapAnalysisCache HeapCache`; `AnalysisPipeline` casts replaced with interface checks |
| 2025 | MINOR-08 | ✅ **Done** | Full-clear eviction rationale expanded in `LazyReferenceGraph`; OPT-#7 comment now documents the thrash scenario, the deliberate choice, and the two upgrade paths |
| 2025 | MINOR-09 | ✅ **Done** | `GenerateAsync` renamed to `Generate`; return type changed from `Task<IReadOnlyList<...>>` to `IReadOnlyList<...>`; `Task.FromResult` wrapper removed; both call sites updated (`GenerateFindingsStage` and `DumpAnalysisService` trend path) |
| 2025 | MINOR-11 | ✅ **Done** | Explicit `Category` property override added to all 16 analyzers; `Infer()` retained as fallback for unknown/third-party analyzers only |
| 2025 | MINOR-10 | ✅ **Done** | `EnumerateIndexedEntriesAsTuples` now delegates to `EnumerateIndexedEntries().Select(...)`; duplicate iteration body removed |
| 2025 | MINOR-12 | ✅ **Done** | `Resolve<T>()` helper added to `ConfigurationResolver`; all 7 option ternaries collapsed to one-line calls |
| 2025 | MINOR-15 | ✅ **Done** | `LazyReferenceGraph` now implements `IReferenceProvider` via explicit interface; `ReferenceChainAnalyzer` replaced `ClrReferenceProvider` with `LazyReferenceGraph`, gaining edge caching across the 3 BFS phases |

---

## Table of Contents

1. [Layer Architecture Overview](#1-layer-architecture-overview)
2. [Issue Register](#2-issue-register)
   - [CRITICAL-01 — `Core` is polluted with all analyzer-specific domain types](#critical-01--core-is-polluted-with-all-analyzer-specific-domain-types)
   - [CRITICAL-02 — `TrendAnalyzer` constructor hardcodes all comparers (3-point sync trap)](#critical-02--trendanalyzer-constructor-hardcodes-all-comparers-3-point-sync-trap)
   - [CRITICAL-03 — `FindingGenerationPipeline` silently swallows exceptions](#critical-03--findingGenerationpipeline-silently-swallows-exceptions)
   - [MAJOR-04 — `DumpAnalysisService` is a God Class](#major-04--dumpanalysisservice-is-a-god-class)
   - [MAJOR-05 — Options resolution uses fragile magic-key dictionary pattern](#major-05--options-resolution-uses-fragile-magic-key-dictionary-pattern)
   - [MAJOR-06 — `HeapAnalysisCache` bypasses `IHeapAnalysisCache` in the CLI pipeline](#major-06--heapanalysiscache-bypasses-iheapanalysiscache-in-the-cli-pipeline)
   - [MAJOR-07 — Two `Pipeline` namespaces with `AnalysisContext` name collision](#major-07--two-pipeline-namespaces-with-analysiscontext-name-collision)
   - [MINOR-08 — `LazyReferenceGraph` full-cache eviction on limit hit](#minor-08--lazygraph-full-cache-eviction-on-limit-hit)
   - [MINOR-09 — `FindingGenerationPipeline.GenerateAsync` is sync wrapped in `Task.FromResult`](#minor-09--findinggenerationpipelinegenerateasync-is-sync-wrapped-in-taskfromresult)
   - [MINOR-10 — Duplicate iteration code in `HeapAnalysisCache`](#minor-10--duplicate-iteration-code-in-heapanalysiscache)
   - [MINOR-11 — `AnalyzerCategory.Infer()` relies on fragile name matching](#minor-11--analyzercategoryinfer-relies-on-fragile-name-matching)
   - [MINOR-12 — `ConfigurationResolver` has mechanical duplication](#minor-12--configurationresolver-has-mechanical-duplication)
   - [MINOR-13 — `MemoryLeakAnalyzer` and `ReferenceChainAnalyzer` are `public`](#minor-13--memoryleakanalyzer-and-referencechainanalyzer-are-public)
   - [MINOR-14 — `SingleDumpPipelineState` stage comment gap](#minor-14--singledumppipelinestate-stage-comment-gap)
   - [MINOR-15 — `ClrReferenceProvider` and `LazyReferenceGraph` are redundant](#minor-15--clrreferenceprovider-and-lazygraph-are-redundant)
3. [What's Working Well (Strengths)](#3-whats-working-well-strengths)
4. [Prioritized Action Plan](#4-prioritized-action-plan)

---

## 1. Layer Architecture Overview

```
┌────────────────────────────────────────────────────────────────┐
│  DumpDetective.Cli                                             │
│  Entry point · StagedPipelineRunner · DumpAnalysisService      │
│  Commands · ConsoleUx · ServiceRegistration (DI root)          │
├───────────────────────────┬────────────────────────────────────┤
│  DumpDetective.Analysis   │  DumpDetective.Reporting           │
│  Analyzers (16)           │  FindingGenerators (16)            │
│  HeapAnalysisCache        │  Printers (16)                     │
│  Indexing / Traversal     │  ReportBuilder                     │
│  TrendAnalyzer            │  TrendReportComposer               │
├───────────────────────────┴────────────────────────────────────┤
│  DumpDetective.Core                                            │
│  IAnalyzer · IFindingGenerator · IAnalyzerTrendComparer        │
│  AnalyzerDomainResult (abstract base + shared primitives only) │  ✅ cleaned
│  InsightFinding · Options · IHeapAnalysisCache                 │
└────────────────────────────────────────────────────────────────┘
```

### Dependency rules (updated — after CRITICAL-01)
```
Cli        → Analysis, Reporting, Core
Analysis   → Core
Reporting  → Analysis, Core   ✅ reference added
Core       → ClrMD (Microsoft.Diagnostics.Runtime)
```

> ~~The rule `Reporting ✗ Analysis` forces **all concrete domain result types into `Core`**, which is the root of Issue CRITICAL-01.~~  
> **RESOLVED:** `Reporting` now references `Analysis`. Domain result types live in `DumpDetective.Analysis.Models`.

---

## 2. Issue Register

---

### ✅ CRITICAL-01 — `Core` is polluted with all analyzer-specific domain types — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Core/Models/AnalyzerDomainResult.cs`

#### What *(was)*
`Core` defined the abstract base `AnalyzerDomainResult` **and** every single concrete subtype for all 16 analyzers: `MemoryDomainResult`, `GCGenerationDomainResult`, `MemoryLeakDomainResult`, `ThreadDomainResult`, `CrashDomainResult`, etc. This was a >300-line file that grew with every new analyzer.

#### Why it was a problem *(archived)*
- **SRP violation:** `Core` is supposed to be the contract/abstraction layer. It should not know about `FinalizerQueueResult`, `ReferenceTypeSampleSnapshot`, or `LohSegmentSnapshot` — those are `Analysis` concerns.
- **Coupling:** Any change to an analyzer's output shape required modifying `Core`, which then recompiled `Analysis`, `Reporting`, and `Cli`. A low-level change propagated to every layer.
- **Root cause:** `Reporting`'s `FindingGenerators` needed to read `MemoryLeakDomainResult` to produce `InsightFinding` objects. Since `Reporting` did not reference `Analysis`, the types had to be moved to the shared `Core` layer.

#### What was done

| File | Change |
|------|---------|
| `Reporting/DumpDetective.Reporting.csproj` | Added `<ProjectReference>` to `Analysis` |
| `Analysis/Models/AnalyzerDomainModels.cs` | **New file** — all 30 analyzer-specific domain result types, namespace `DumpDetective.Analysis.Models` |
| `Analysis/GlobalUsings.cs` | **New file** — `global using DumpDetective.Analysis.Models;` covering all 16 analyzer files transparently |
| `Reporting/GlobalUsings.cs` | **New file** — `global using DumpDetective.Analysis.Models;` covering all 32 printer/generator files transparently |
| `Core/Models/AnalyzerDomainResult.cs` | Reduced from **285 → 23 lines**; retains only `AnalyzerDomainResult` (abstract), `GenericAnalyzerDomainResult`, `TypeSnapshot`, `NameCountEntry`, `NameBytesEntry` |

**Zero individual `.cs` files** in `Analysis` or `Reporting` were touched — the two `GlobalUsings.cs` files resolved the namespace transition automatically across all 48 consuming files.

**Visibility note:** `Analysis.csproj` already declared `<InternalsVisibleTo Include="DumpDetective.Reporting" />`, so all `internal` domain types (`MemoryLeakDomainResult`, `ModuleDomainResult`, etc.) are visible to `Reporting` with no additional changes.

#### Dependency graph after fix
```
Cli        → Analysis, Reporting, Core
Analysis   → Core
Reporting  → Analysis, Core   ✅ reference added
Core       → ClrMD
```
No circular dependency: `Analysis` → `Core` ← `Reporting` → `Analysis` is acyclic because `Analysis` does not reference `Reporting`.

---

### ✅ CRITICAL-02 — `TrendAnalyzer` constructor hardcodes all comparers (3-point sync trap) — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Analysis/Trend/TrendAnalyzer.cs`

#### What *(was)*
```csharp
public TrendAnalyzer()
{
    var list = new List<IAnalyzerTrendComparer>
    {
        new MemoryAnalyzerTrendComparer(),
        // ... 15 more manually listed
    };
    _comparers = list.ToDictionary(c => c.AnalyzerName, StringComparer.Ordinal);
}
```
The `TrendAnalyzer` was constructed without DI and manually instantiated every comparer.

#### Why it was a problem *(archived)*
Adding a new analyzer required touching **three separate registration points** with no compile-time enforcement, creating a silent regression risk.

#### What was done

| File | Change |
|------|--------|
| `Analysis/Trend/TrendAnalyzer.cs` | Replaced parameterless constructor with primary DI constructor `(IEnumerable<IAnalyzerTrendComparer> comparers)`; removed `using DumpDetective.Analysis.Trend.Comparers;` (no longer needed) |
| `Cli/Services/DumpAnalysisService.cs` | Added `TrendAnalyzer trendAnalyzer` constructor parameter + `_trendAnalyzer` field; removed `TrendAnalyzer trendAnalyzer = new()` local instantiation |
| `Cli/Hosting/ServiceRegistration.cs` | Registered all 16 `IAnalyzerTrendComparer` singletons + `TrendAnalyzer` singleton alongside the existing `IFindingGenerator` registrations |
| `Core/Abstractions/IAnalyzerTrendComparer.cs` | **Deleted** — was a dead duplicate of `Core/Models/AnalyzerTrendContracts.cs`; caused CS0104 ambiguity and was never implemented by any comparer |

#### Adding a new analyzer checklist (current state)
| Step | What to add | File |
|------|-------------|------|
| 1 | New `IAnalyzer` implementation | `Analysis/Analyzers/` |
| 2 | New `IAnalyzerTrendComparer` | `Analysis/Trend/Comparers/` |
| 3 | New `IFindingGenerator` | `Reporting/FindingGenerators/` |
| 4 | Register all three in `ServiceRegistration.cs` | `Cli/Hosting/` |

One file, three lines — no hidden runtime surprises.

---

### ✅ CRITICAL-03 — `FindingGenerationPipeline` silently swallows exceptions — **RESOLVED**

> **Implemented (Option B).** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Reporting/Pipeline/FindingGenerationPipeline.cs`

#### What *(was)*
```csharp
catch
{
    // swallows errors from finding generation to avoid failing reporting
    updated.Add(run);
}
```
A bare `catch` with no error capture — generator failures were invisible to both the user and the report.

#### Why it was a problem *(archived)*
Violated the project guideline *"more actionable diagnostic data is better"*. Zero findings from an analyzer could mean clean results **or** a crashed generator — indistinguishable.

#### What was done (Option B)

| File | Change |
|------|--------|
| `Core/Models/AnalyzerRunResult.cs` | Added `string? FindingGeneratorError = null` as the last optional parameter with an XML doc comment explaining its semantics |
| `Reporting/Pipeline/FindingGenerationPipeline.cs` | `catch` → `catch (Exception ex)`; populates `run with { FindingGeneratorError = "{ex.GetType().Name}: {ex.Message}" }` instead of the original unmodified `run` |
| `Reporting/Services/ReportBuilder.cs` | Added a `FindingSeverity.Warning` report section (`finding-generator-error:{analyzerName}`) rendered alongside the existing `analyzer-failure` section pattern |
| `Cli/Pipeline/Stages/GenerateFindingsStage.cs` | After the pipeline completes, iterates `state.Runs` and calls `ConsoleUx.Warning(...)` for every run with a non-null `FindingGeneratorError` — visible in all modes, not just diagnostic |

#### Visibility chain
A generator crash now surfaces at three levels:
1. **Console** — `[WARN] Finding generator failed for 'X': ExceptionType: message` printed by `GenerateFindingsStage` immediately after the stage
2. **Report** — `Finding generator failed: X` Warning section in the canonical report (same section pattern as analyzer execution failures)
3. **Model** — `run.FindingGeneratorError` on `AnalyzerRunResult` for programmatic inspection by any future consumer

---

### MAJOR-04 — `DumpAnalysisService` is a God Class

**File:** `src/DumpDetective.Cli/Services/DumpAnalysisService.cs`

#### What
`DumpAnalysisService.ExecuteAsync` currently handles:
1. Config resolution + startup validation
2. Analyzer factory call + filter/order logic
3. Trend vs. single-dump routing decision
4. Building the CLI stage list
5. Running the `StagedPipelineRunner`
6. Rendering diagnostic summary to console
7. Trend orchestration (`ExecuteTrendAsync` is a large private method)

#### Why it's a problem
- **SRP violation:** 5–7 distinct responsibilities in a single class.
- Every new feature (new routing mode, new output format, new validation rule) touches this file.
- `ExecuteTrendAsync` duplicates pipeline logic that partially overlaps with `BuildSingleDumpStages`.

#### How to fix
Extract into focused services. Suggested decomposition:

```
DumpAnalysisService (coordinator — thin, orchestrates only)
├── AnalyzerFilterService          (filter + order analyzers from IReadOnlyList)
├── TrendOrchestrationService      (owns ExecuteTrendAsync logic)
└── SingleDumpOrchestrationService (owns BuildSingleDumpStages + RunAsync)
```

**`AnalyzerFilterService`** — pure static logic, easily unit-testable without any DI:
```csharp
internal static class AnalyzerFilterService
{
    public static IReadOnlyList<IAnalyzer> Apply(
        IReadOnlyList<IAnalyzer> all,
        IReadOnlyCollection<string> include,
        IReadOnlyCollection<string> exclude) { ... }

    public static IReadOnlyList<IAnalyzer> Order(IReadOnlyList<IAnalyzer> filtered) { ... }
}
```

**`DumpAnalysisService` after refactor** becomes:
```csharp
internal sealed class DumpAnalysisService(
    ConfigurationResolver configurationResolver,
    StartupValidator startupValidator,
    IAnalyzerFactory analyzerFactory,
    SingleDumpOrchestrationService singleDumpOrchestration,
    TrendOrchestrationService trendOrchestration)
{
    public async Task<int> ExecuteAsync(AnalysisCommandRequest request, CancellationToken cancellationToken)
    {
        ResolvedExecutionOptions resolved = Resolve(request);
        IReadOnlyList<IAnalyzer> active = AnalyzerFilterService.Order(
            AnalyzerFilterService.Apply(analyzerFactory.CreateAnalyzers(), resolved.IncludeAnalyzers, resolved.ExcludeAnalyzers));

        return TryResolveTrendSequence(resolved, out var trendPaths)
            ? await trendOrchestration.ExecuteAsync(resolved, active, trendPaths!, cancellationToken)
            : await singleDumpOrchestration.ExecuteAsync(resolved, active, cancellationToken);
    }
}
```

---

### ✅ MAJOR-05 — Options resolution uses fragile magic-key dictionary pattern — **RESOLVED**

> **Implemented (Option A, key changed to `Type`).** See [Changelog](#changelog) for details.

**Files:** `Analysis/Analyzers/MemoryLeakAnalyzer.cs`, `ReferenceChainAnalyzer.cs`, `EventLeakAnalyzer.cs`, `CollectionAnalyzer.cs`, `RunAnalyzersPipelineStage.cs`

#### What *(was)*
Three analyzers used `context.Options.TryGetValue(nameof(XxxOptions), ...)` with a string key. Renaming the options class silently fell through to the default. `CollectionAnalyzer` was separately wired via constructor, bypassing the context entirely.

#### What was done

| File | Change |
|------|--------|
| `Core/Abstractions/IAnalyzer.cs` | `Options` dict key changed from `string` to `Type`; doc comment added explaining `GetOption<T>()` |
| `Analysis/Pipeline/AnalysisContextExtensions.cs` | **New file** — `GetOption<T>()` extension; returns `new T()` when not registered |
| `Analysis/Pipeline/RuntimeAnalysisContext.cs` | Removed `MemoryLeakOptions`, `ReferenceChainOptions`, `EventLeakOptions`, `DiagnosticsOptions` redundant properties; stripped `Core.Options` using |
| `Analysis/GlobalUsings.cs` | Added `global using DumpDetective.Analysis.Pipeline;` so `GetOption<T>()` is visible to all 16 analyzer files |
| `Analysis/Analyzers/MemoryLeakAnalyzer.cs` | `context.GetOption<MemoryLeakOptions>()` |
| `Analysis/Analyzers/ReferenceChainAnalyzer.cs` | `context.GetOption<ReferenceChainOptions>()` |
| `Analysis/Analyzers/EventLeakAnalyzer.cs` | `context.GetOption<EventLeakOptions>()` |
| `Analysis/Analyzers/CollectionAnalyzer.cs` | `_options` made non-readonly; `AnalyzeAsync` sets `_options = context.GetOption<CollectionAnalyzerOptions>()`; logger-only constructor added |
| `Analysis/Pipeline/AnalysisPipeline.cs` | `context.DiagnosticsOptions` → `context.Diagnostics` (used the base property, not the now-removed derived one) |
| `Cli/Pipeline/Stages/RunAnalyzersPipelineStage.cs` | `typeof(T)` keys; `CollectionAnalyzerOptions` added; redundant named properties removed |
| `Cli/Services/DumpAnalysisService.cs` | Same in trend-path context |
| `Cli/Services/DefaultAnalyzerFactory.cs` | `CollectionAnalyzer(logger)` — logger-only ctor; options come from context |
| `Cli/Services/ResolvedExecutionOptions.cs` | `CollectionAnalyzerOptions Collection` parameter added |
| `Cli/Services/ConfigurationResolver.cs` | `BuildCollectionFromConfig` / `BuildCollectionFromCli` added; `config.Collection` on file model; `CliConfigurationJsonSerializerContext` updated |
| `BenchmarkSuite1/AnalyzerBenchmarkBase.cs` | `typeof(T)` keys |
| `BenchmarkSuite1/PipelineHotspotBenchmark.cs` | Dropped removed `RuntimeAnalysisContext` properties |
| `tests/.../AnalysisDiagnosticsTests.cs` | Dropped removed `RuntimeAnalysisContext` properties |
| `tests/.../AnalysisPipelineTests.cs` | Dropped removed `RuntimeAnalysisContext` properties |
| `tests/.../StartupValidatorTests.cs` | Added `Collection` to `ResolvedExecutionOptions` construction |

#### Adding a new analyzer with options (current state)
1. Create `Analysis/Options/XxxAnalyzerOptions.cs` (or `Analysis/Analyzers/` until a future move)
2. Call `context.GetOption<XxxAnalyzerOptions>()` in the analyzer
3. Add `[typeof(XxxAnalyzerOptions)] = resolved.Xxx` in `RunAnalyzersPipelineStage`
4. Add `XxxAnalyzerOptions Xxx` to `ResolvedExecutionOptions` + `ConfigurationResolver`

One entry in each of two files — no magic strings, no silent fallback risk.

---

### ✅ MAJOR-06 — `HeapAnalysisCache` bypasses `IHeapAnalysisCache` in the CLI pipeline — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**Files:** `Cli/Pipeline/SingleDumpPipelineState.cs`, `Cli/Pipeline/Stages/BuildHeapIndexStage.cs`

#### What *(was)*
`SingleDumpPipelineState.HeapCache` was typed as the concrete `HeapAnalysisCache` class. `BuildHeapIndexStage` called `heapCache.PrebuildHeapIndex()` directly on the concrete type. `AnalysisPipeline` cast `context.Cache is HeapAnalysisCache` to call `SetProgress`. No interface stood between the build-time API and its consumers.

#### What was done

| File | Change |
|------|--------|
| `Analysis/Cache/IHeapIndexBuilder.cs` | **New file** — `internal interface IHeapIndexBuilder` with `PrebuildHeapIndex()` and `SetProgress()`. Lives in `Analysis` (not `Core`) because `HeapIndexBuildResult` and `HeapIndexPrebuildMode` are `internal` to `Analysis.Indexing` |
| `Analysis/Cache/HeapAnalysisCache.cs` | Added `IHeapIndexBuilder` to the implements list: `HeapAnalysisCache : IHeapAnalysisCache, IHeapIndexBuilder` |
| `Cli/Pipeline/SingleDumpPipelineState.cs` | `HeapAnalysisCache? HeapCache` split into: `IHeapIndexBuilder? HeapIndexBuilder` (Stage 2 build contract) + `IHeapAnalysisCache? HeapCache` (Stage 3 read-only cache contract). Both point to the same `HeapAnalysisCache` instance |
| `Cli/Pipeline/Stages/BuildHeapIndexStage.cs` | `HeapAnalysisCache heapCache = new()` kept for construction; `IHeapIndexBuilder heapBuilder = heapCache` used for `PrebuildHeapIndex()`. Both `HeapIndexBuilder` and `HeapCache` are set on state |
| `Cli/Pipeline/Stages/RunAnalyzersPipelineStage.cs` | Removed `using DumpDetective.Analysis.Cache;` — `state.HeapCache` is now `IHeapAnalysisCache`, no concrete type imported |
| `Analysis/Pipeline/AnalysisPipeline.cs` | `context.Cache is HeapAnalysisCache cacheWithProgress` → `context.Cache is IHeapIndexBuilder cacheWithProgress` (both `SetProgress` call-sites) |
| `Analysis/Pipeline/RuntimeAnalysisContext.cs` | Removed down-cast `HeapCache` property; replaced with `IHeapIndexBuilder? HeapIndexBuilder => Cache as IHeapIndexBuilder` (null-safe) |
| `Cli/Services/DumpAnalysisService.cs` | Trend path: `heapCache.PrebuildHeapIndex()` → `IHeapIndexBuilder heapBuilder = heapCache; heapBuilder.PrebuildHeapIndex()` |

#### Contracts after fix
```
BuildHeapIndexStage  → IHeapIndexBuilder  (build-time: PrebuildHeapIndex, SetProgress)
RunAnalyzers stage   → IHeapAnalysisCache (read-only: GetStaticRoots, EnumerateEntries, ...)
AnalysisPipeline     → IHeapIndexBuilder  (SetProgress per analyzer)
RuntimeAnalysisContext.HeapIndexBuilder → IHeapIndexBuilder? (null-safe cast from Cache)
```
Pipeline stage tests can now substitute either interface independently without touching `HeapAnalysisCache`.

---

### ✅ MAJOR-07 — Two `Pipeline` namespaces with `AnalysisContext` name collision — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**Files:** `Analysis/Pipeline/AnalysisContext.cs`, `Cli/Pipeline/Stages/RunAnalyzersPipelineStage.cs`, `Cli/Services/DumpAnalysisService.cs`

#### What *(was)*
`DumpDetective.Analysis.Pipeline.AnalysisContext` collided with `DumpDetective.Core.Abstractions.AnalysisContext`, forcing five files to declare a `PipelineAnalysisContext` alias:
```csharp
using PipelineAnalysisContext = DumpDetective.Analysis.Pipeline.AnalysisContext;
```

#### What was done

| File | Change |
|------|--------|
| `Analysis/Pipeline/AnalysisContext.cs` | **Deleted** |
| `Analysis/Pipeline/RuntimeAnalysisContext.cs` | **New file** — class renamed from `AnalysisContext` to `RuntimeAnalysisContext` |
| `Analysis/Pipeline/AnalysisPipeline.cs` | Both `AnalysisContext context` parameters updated to `RuntimeAnalysisContext context` |
| `Cli/Pipeline/Stages/RunAnalyzersPipelineStage.cs` | Alias removed; `PipelineAnalysisContext` → `RuntimeAnalysisContext` throughout |
| `Cli/Pipeline/Stages/BuildHeapIndexStage.cs` | Unused alias removed |
| `Cli/Services/DumpAnalysisService.cs` | Alias removed; `PipelineAnalysisContext` → `RuntimeAnalysisContext` |
| `BenchmarkSuite1/PipelineHotspotBenchmark.cs` | Alias removed; `PipelineAnalysisContext` → `RuntimeAnalysisContext` |
| `tests/.../AnalysisPipelineTests.cs` | Alias removed; `PipelineAnalysisContext` → `RuntimeAnalysisContext` |
| `tests/.../AnalysisDiagnosticsTests.cs` | Alias removed; `PipelineAnalysisContext` → `RuntimeAnalysisContext` |
| `tests/.../DependencyDirectionTests.cs` | Expected `Reporting` deps updated to `{"DumpDetective.Analysis", "DumpDetective.Core"}` (reflects CRITICAL-01) |

---

### ✅ MINOR-08 — `LazyReferenceGraph` full-cache eviction on limit hit — **RESOLVED**

> **Implemented (Option A — document and accept).** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Analysis/Traversal/LazyReferenceGraph.cs`

The existing OPT-#7 comment was expanded to document the full-clear eviction strategy, the worst-case thrash scenario (dense-graph BFS on large dumps), the deliberate choice to keep it aggressive, and the two concrete upgrade paths (halve the limit; or partial eviction evicting oldest 50% by insertion order). Revisit during profiling if thrash is observed on dumps >500 MB managed heap.

---

### ✅ MINOR-09 — `FindingGenerationPipeline.GenerateAsync` is sync wrapped in `Task.FromResult` — **RESOLVED**

> **Implemented (Option A — truly sync).** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Reporting/Pipeline/FindingGenerationPipeline.cs`

| File | Change |
|------|--------|
| `Reporting/Pipeline/FindingGenerationPipeline.cs` | `GenerateAsync` renamed to `Generate`; return type `Task<IReadOnlyList<...>>` → `IReadOnlyList<...>`; `Task.FromResult` wrapper removed |
| `Cli/Pipeline/Stages/GenerateFindingsStage.cs` | `async Task ExecuteAsync` → `Task ExecuteAsync`; `await findingGenerationPipeline.GenerateAsync(...)` → `findingGenerationPipeline.Generate(...)`; early-return `return;` → `return Task.CompletedTask;` |
| `Cli/Services/DumpAnalysisService.cs` | Trend path: `await _findingGenerationPipeline.GenerateAsync(...)` → `_findingGenerationPipeline.Generate(...)` |

---

### ✅ MINOR-10 — Duplicate iteration code in `HeapAnalysisCache` — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

`EnumerateIndexedEntriesAsTuples()` now delegates to `EnumerateIndexedEntries()` via a single LINQ `Select` projection. The duplicated in-memory/disk iteration block was removed. `HeapEntry` is a small struct, so the projection is zero-overhead.

```csharp
public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
    => EnumerateIndexedEntries().Select(e => (e.Address, e.MethodTable, e.Size));
```

---

### ✅ MINOR-11 — `AnalyzerCategory.Infer()` relies on fragile name matching — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Core/Abstractions/IAnalyzer.cs`

All 16 built-in analyzers now carry an explicit `public string Category => "...";` override. `AnalyzerCategory.Infer()` is retained as the default interface implementation for unknown/third-party analyzers.

| Analyzer | Category |
|---|---|
| Collection Analysis | Memory |
| Crash Analysis | Crash |
| Dependent Handle Analysis | Handles |
| Event Leak Analysis | Events |
| GC Generation Analysis | GC |
| GC Handle Analysis | Handles |
| Hang Analysis | Hang |
| Lock Graph Analysis | Locks |
| LOH Fragmentation Analysis | Memory |
| Memory Analysis | Memory |
| Memory Leak Analysis | Memory |
| Module Analysis | Modules |
| Reference Chain Analysis | Memory |
| Static Root Leak Detection | Memory |
| Thread Analysis | Threads |
| Thread Stack Signature Clustering | Threads |

---

### ✅ MINOR-12 — `ConfigurationResolver` has mechanical duplication — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Cli/Services/ConfigurationResolver.cs`

A private static `Resolve<T>()` helper was added. All 7 ternary option-building blocks in `Resolve()` are now single-line calls:

```csharp
MemoryLeakOptions memoryLeak   = Resolve(usedConfigFile, BuildMemoryLeakFromConfig,   BuildMemoryLeakFromCli,   fileModel, request);
ReferenceChainOptions refChain = Resolve(usedConfigFile, BuildReferenceChainFromConfig, BuildReferenceChainFromCli, fileModel, request);
// ... all 7 options follow the same pattern
```

Behavior is unchanged; the pattern is now expressed once.

---

### MINOR-13 — `MemoryLeakAnalyzer` and `ReferenceChainAnalyzer` are `public`

**Files:** `Analysis/Analyzers/MemoryLeakAnalyzer.cs`, `Analysis/Analyzers/ReferenceChainAnalyzer.cs`

#### What
Both classes are declared `public class` and expose secondary `Analyze()` overloads without the `cache` and `progress` parameters — intended as test-friendly entry points:

```csharp
// Intended for test use — bypasses cache and progress
public AnalyzerDomainResult Analyze(ClrHeap heap, ClrRuntime runtime, MemoryLeakOptions options)
{
    return Analyze(heap, runtime, cache: null, options, progress: null);
}
```

#### Why it's a problem
- `public` leaks the implementation as an API surface of the `Analysis` assembly.
- The test-only overload is indistinguishable from production API without reading the comment.

#### How to fix
```csharp
// Mark both classes internal
internal class MemoryLeakAnalyzer : IAnalyzer { ... }
internal class ReferenceChainAnalyzer : IAnalyzer { ... }
```

The `Analysis.csproj` already has `InternalsVisibleTo` for `DumpDetective.Tests`, so test projects can still call `internal` members. The secondary `Analyze()` overloads remain accessible to tests but are no longer part of the public API.

---

### ✅ MINOR-14 — `SingleDumpPipelineState` stage comment gap — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Cli/Pipeline/SingleDumpPipelineState.cs`

#### What was done
Added a Stage 4 comment block between the Stage 3 and Stage 5 comments, documenting that `GenerateFindingsStage` enriches `Runs` in-place and requires no new state properties:

```csharp
// ── Stage 3: RunAnalyzersPipelineStage ──────────────────────────────────────
public IReadOnlyList<AnalyzerRunResult> Runs { get; set; } = [];
public TimeSpan AnalysisElapsed { get; set; }

// ── Stage 4: GenerateFindingsStage ───────────────────────────────────────────
// Enriches Runs in-place with InsightFinding lists; no new properties required.

// ── Stage 5: BuildReportStage ────────────────────────────────────────────────
public string RenderedReport { get; set; } = string.Empty;
```

---

### ✅ MINOR-15 — `ClrReferenceProvider` and `LazyReferenceGraph` are redundant — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**Files:** `Core/Abstractions/ClrReferenceProvider.cs`, `Analysis/Traversal/LazyReferenceGraph.cs`

| File | Change |
|------|--------|
| `Analysis/Traversal/LazyReferenceGraph.cs` | Added `IReferenceProvider` to the class declaration; explicit interface implementation `IEnumerable<ulong> IReferenceProvider.GetReferences(ulong address) => GetReferences(address)` adapts the `IReadOnlyList<ulong>` public API to the interface |
| `Analysis/Analyzers/ReferenceChainAnalyzer.cs` | `new ClrReferenceProvider(heap)` → `new LazyReferenceGraph(heap)` in `TryFindAnyRootPath_Bidirectional`; comment updated to explain the caching benefit across all 3 BFS phases |
| `Core/Abstractions/ClrReferenceProvider.cs` | Retained as a lightweight non-caching fallback (no deletion — KISS) |

`ReferenceChainAnalyzer` now uses `LazyReferenceGraph` as the `IReferenceProvider` for bidirectional path-finding. The same cache is reused across the candidate-set build, reverse-index build, and constrained BFS phases — reducing redundant `ClrObject.EnumerateReferences` calls on graphs with repeated edges.

---

## 3. What's Working Well (Strengths)

These are design decisions worth preserving as the codebase grows.

| Area | Detail |
|---|---|
| **Stage-based CLI pipeline** | `IAnalysisStage` + `StagedPipelineRunner` is clean, extensible, and auto-tracking. Adding a stage requires one class. |
| **`IAnalyzer` default interface members** | `Category`, `Tags`, `Order` with defaults avoids boilerplate in all 16 analyzers. Correct use of C# interface defaults. |
| **`AnalyzerDomainResult` as `abstract record`** | Immutable, `with`-expression friendly. Correct value-semantic model for results that should never be mutated after production. |
| **`AnalyzerDomainResultExtensions.Stamp()`** | Ensures `AnalyzerName` and `Category` are always stamped without copy-paste. Enforces the contract at the call site, not the definition. |
| **Adaptive indexing (disk vs. memory)** | Automatic selection of `MemoryBackedObjectIndexWriter` vs `DiskBackedObjectIndexWriter` based on dump size is production-grade. The `DumpSizeTier` enum is a clean abstraction for this. |
| **`FindingGenerationPipeline` pattern** | Clean separation between data extraction (`IAnalyzer`) and threshold interpretation (`IFindingGenerator`). The 1:1 analyzer-to-generator mapping is transparent. |
| **`IAnalysisDiagnosticsSink` + null object** | `NullAnalysisDiagnosticsSink.Instance` means analyzers can always call `DiagnosticsSink.Publish()` without null-checks. Correct null-object pattern. |
| **`ObjectScanCounter`** | Encapsulates the progress-reporting logic for heap enumeration. Prevents each analyzer from reinventing scan-count reporting. |
| **`TrendAnalyzer.CompareSeries()`** | N-dump series comparison is correct. `CompareSeries` produces a step-by-step list, while `ExtractTimeline` produces a per-metric time series. Both are useful and distinct. |
| **`BoundedRootPathFinder` smart pruning** | Skipping `System.String`, `System.Object`, and large fan-out nodes before BFS is the right heuristic for production dumps. Prevents analysis from spending time on noise paths. |
| **`AnalysisPipeline` skip-on-cancel** | Emitting a `Skipped` result (with reason) rather than silently dropping the analyzer maintains result set integrity for downstream reporting. |
| **`ConfigurationResolver` file-first priority** | Correct implementation of the project guideline: config file takes full precedence over CLI args. Well-isolated in a single resolver class. |

---

## 4. Prioritized Action Plan

Issues are ordered by impact. Within each tier, order by effort (low effort first).

### 🔴 Tier 1 — High Impact, Address Next Sprint

| ID | Action | Files to Touch | Effort |
|---|---|---|---|
| ~~CRITICAL-03~~ | ~~Add diagnostics to `FindingGenerationPipeline` catch block~~ | ~~`Reporting/Pipeline/FindingGenerationPipeline.cs`, `Core/Models/AnalyzerRunResult.cs`~~ | ✅ **Done** |
| ~~MINOR-14~~ | ~~Fix `SingleDumpPipelineState` stage 4 comment gap~~ | ~~`Cli/Pipeline/SingleDumpPipelineState.cs`~~ | ✅ **Done** |
| ~~MAJOR-07~~ | ~~Rename `Analysis.Pipeline.AnalysisContext` → `RuntimeAnalysisContext`~~ | ~~1 rename + 4 usages~~ | ✅ **Done** |
| ~~CRITICAL-02~~ | ~~Inject `IEnumerable<IAnalyzerTrendComparer>` into `TrendAnalyzer`~~ | ~~`Analysis/Trend/TrendAnalyzer.cs`, `Cli/Hosting/ServiceRegistration.cs`~~ | ✅ **Done** |
| ~~MAJOR-05~~ | ~~Add `GetOption<T>()` extension method to eliminate magic-key pattern~~ | ~~`Analysis/` (new extensions file), 3 analyzer files~~ | ✅ **Done** |

### 🟡 Tier 2 — High Impact, Plan for Next Cycle

| ID | Action | Files to Touch | Effort |
|---|---|---|---|
| ~~CRITICAL-01~~ | ~~Add `Reporting → Analysis` project reference; move domain types out of `Core`~~ | ~~`Reporting.csproj`, `Core/Models/AnalyzerDomainResult.cs`, all `Reporting/FindingGenerators/`~~ | ✅ **Done** |
| ~~MAJOR-06~~ | ~~Extract `IHeapIndexBuilder` interface; split `HeapCache` state bag property~~ | ~~`Core/Abstractions/` (new file), `Cli/Pipeline/SingleDumpPipelineState.cs`, `BuildHeapIndexStage.cs`~~ | ✅ **Done** |
| MINOR-13 | Mark `MemoryLeakAnalyzer`, `ReferenceChainAnalyzer` as `internal` | 2 files | XS |
| ~~MINOR-11~~ | ~~Add explicit `Category` override to each analyzer; keep `Infer()` as fallback only~~ | ~~16 analyzer files (2-line change each)~~ | ✅ **Done** |

### 🟢 Tier 3 — Quality / Maintainability

| ID | Action | Files to Touch | Effort |
|---|---|---|---|
| ~~MINOR-09~~ | ~~Change `GenerateAsync` to sync `Generate` or `ValueTask`~~ | ~~`Reporting/Pipeline/FindingGenerationPipeline.cs`, `Cli/Pipeline/Stages/GenerateFindingsStage.cs`~~ | ✅ **Done** |
| ~~MINOR-10~~ | ~~Delegate `EnumerateIndexedEntriesAsTuples` to `EnumerateIndexedEntries`~~ | ~~`Analysis/Cache/HeapAnalysisCache.cs`~~ | ✅ **Done** |
| ~~MINOR-12~~ | ~~Extract `Resolve<T>()` helper in `ConfigurationResolver`~~ | ~~`Cli/Services/ConfigurationResolver.cs`~~ | ✅ **Done** |
| ~~MINOR-15~~ | ~~Have `LazyReferenceGraph` implement `IReferenceProvider`; retire `ClrReferenceProvider`~~ | ~~`Analysis/Traversal/LazyReferenceGraph.cs`, `Core/Abstractions/ClrReferenceProvider.cs`~~ | ✅ **Done** |
| ~~MINOR-08~~ | ~~Document cache eviction strategy in `LazyReferenceGraph` (or implement partial eviction)~~ | ~~`Analysis/Traversal/LazyReferenceGraph.cs`~~ | ✅ **Done** |
| MAJOR-04 | Decompose `DumpAnalysisService` into focused service classes | `Cli/Services/` (new files) | L |

---

**Effort key:** XS = <30 min · S = 30–90 min · M = half-day · L = 1–2 days

> **Note on MAJOR-04:** This is the remaining high-risk refactor. Decompose `DumpAnalysisService` with the test suite green before and after each extraction step.

> ~~**Note on CRITICAL-01:** This was a project reference change — run a full build and all tests immediately after moving each domain result type.~~ **CRITICAL-01 is complete. Build passed clean.**
