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
| 2025 | MAJOR-04 | ✅ **Done** | `DumpAnalysisService` decomposed into `AnalyzerFilterService` (static), `SingleDumpOrchestrationService`, and `TrendOrchestrationService`; dead `IFindingGenerator` injection removed; coordinator reduced to ~60 LOC |
| 2025 | PERF-CRIT-04 | ✅ **Done** | `++_objectScanCount` in `GetRetainedObjects` replaced with `Interlocked.Increment`; consistent with all other increment sites in the file |
| 2025 | PERF-HIGH-05 | ✅ **Done** | `ThrowIfCancellationRequested()` in `DiskBackedObjectIndexWriter` write loop throttled to `ProgressReportEveryObjects` (100 K) cadence; eliminates 80 M volatile reads on a large dump |
| 2025 | PERF-CRIT-01 | ✅ **Done** | `binary-format.md` updated to reflect true on-disk layout: `Size` is `ulong` / 8 bytes, not `int` / 4 bytes; phantom padding row and 20-byte total removed; note added explaining `RecordSize = sizeof(ulong) * 3 = 24` |
| 2025 | PERF-MED-04 | ✅ **Done** | `BoundedPathSearchResult` changed from `sealed record` to `sealed class` with explicit constructor; eliminates synthesized `Equals`/`GetHashCode`/`==`/`Clone` machinery that was never used |
| 2025 | PERF-LOW-02 | ✅ **Done** | `BoundedPathSearchBudget` changed from `readonly record struct` to `readonly struct` with explicit properties and constructor; removes synthesized equality overhead on a config-only struct |
| 2025 | PERF-LOW-03 | ✅ **Done** | `bool IsThreadSafe { get; }` added to `IAnalyzer` with default interface implementation returning `false`; gives `AnalysisPipeline` a concurrency contract to query before scheduling parallel Phase 2 |

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

### ✅ MAJOR-04 — `DumpAnalysisService` is a God Class — **RESOLVED**

> **Implemented.** See [Changelog](#changelog) for details.

**File:** `src/DumpDetective.Cli/Services/DumpAnalysisService.cs`

#### What was done

| File | Change |
|------|--------|
| `Cli/Services/AnalyzerFilterService.cs` | **New file** — static class; `Validate()`, `Apply()`, `Order()`, `GetStageRank()` extracted from `DumpAnalysisService`; pure logic, no DI, unit-testable without infrastructure |
| `Cli/Services/SingleDumpOrchestrationService.cs` | **New file** — DI service; owns the single-dump pipeline: header output, `StagedPipelineRunner`, diagnostic summary, exit code. Deps: `DumpLoader`, `FindingGenerationPipeline`, `ReportBuilderFacade` |
| `Cli/Services/TrendOrchestrationService.cs` | **New file** — DI service; owns the full trend pipeline: per-dump `ExecutePipelineForDumpAsync`, snapshot building, `TrendReportData` construction, staged output (3 stages). Deps: `DumpLoader`, `FindingGenerationPipeline`, `ReportBuilderFacade`, `TrendAnalyzer` |
| `Cli/Services/DumpAnalysisService.cs` | Reduced from ~560 → ~65 LOC; now a thin coordinator: resolve config, validate, filter/order analyzers, route to the appropriate orchestrator via `TryResolveTrendSequence` |
| `Cli/Hosting/ServiceRegistration.cs` | Registered `SingleDumpOrchestrationService` and `TrendOrchestrationService` as singletons; dead `IEnumerable<IFindingGenerator>` parameter removed from `DumpAnalysisService` |

#### Responsibility map after refactor
```
DumpAnalysisService            ← config resolution + routing only (~65 LOC)
  AnalyzerFilterService        ← validate + filter + order (static, ~95 LOC)
  SingleDumpOrchestrationService ← single-dump stages + console output
  TrendOrchestrationService    ← per-dump pipeline + trend report + staged output
```

Every new routing mode, output format, or validation rule now touches exactly one class.

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
| ~~MAJOR-04~~ | ~~Decompose `DumpAnalysisService` into focused service classes~~ | ~~`Cli/Services/` (new files)~~ | ✅ **Done** |

---

**Effort key:** XS = <30 min · S = 30–90 min · M = half-day · L = 1–2 days

> **Note on MAJOR-04:** This is the remaining high-risk refactor. Decompose `DumpAnalysisService` with the test suite green before and after each extraction step.

> ~~**Note on CRITICAL-01:** This was a project reference change — run a full build and all tests immediately after moving each domain result type.~~ **CRITICAL-01 is complete. Build passed clean.**

---

---

# Performance & Memory Deep-Dive Review (Round 2)

**Date:** 2025-07-16  
**Branch:** `optimize`  
**Scope:** `HeapStreamer`, `HeapEntry`, `HeapAnalysisCache`, `DiskBackedObjectIndexWriter`, `HeapIndexEntryReader`, `BoundedRootPathFinder`, `LazyReferenceGraph`, `TypeAggregateIndexBuilder`, `AnalysisPipeline`  
**Standard:** Project performance checklist + architecture guidelines

---

## Legend

| Symbol | Meaning |
|--------|---------|
| 🔴 | Critical — correctness risk, charter violation, or data corruption |
| 🟠 | High — measurable performance or memory regression at scale |
| 🟡 | Medium — meaningful overhead or design gap; address before large-dump testing |
| 🟢 | Low — proactive hardening, defensive coding |
| ✅ | Confirmed correct; preserve as-is |

---

## Confirmed Strengths

| # | Location | What Is Good |
|---|----------|-------------|
| ✅1 | `HeapStreamer.cs` | `yield return` with `IsValid` + `Type != null` guard before every `MethodTable` access. Zero heap materialization. |
| ✅2 | `TypeAggregateIndexBuilder.Add()` | `CollectionsMarshal.GetValueRefOrAddDefault` — zero struct copy in the innermost hot loop. Correct `Merge()` for parallel segment results. |
| ✅3 | `DiskBackedObjectIndexWriter` | Per-segment list capacity pre-sized from `segment.Length / 128`; `ArrayPool<byte>` write buffer; `MaxSegmentParallelism = 4` caps concurrent page pressure. |
| ✅4 | `HeapIndexEntryReader` | `ArrayPool<byte>` read buffer; adaptive batch size by index file size; `SequentialScan` hint; partial-record carry-forward path. |
| ✅5 | `BoundedRootPathFinder` | BFS (not DFS); pre-allocated `Queue`/`HashSet`/`Dictionary` cleared between roots; hard-caps on `maxRoots`, `maxNodes`, `maxEdges`, `maxDepth`. |
| ✅6 | `LazyReferenceGraph` | `MaxCachedNodes = 500_000` bound on cache size. `carefully: true` prevents invalid pointer dereference in BFS. |
| ✅7 | `HeapAnalysisCache.MethodTableHasOutgoingRefs()` | `ulong MethodTable` key; `ContainsPointers` fast path before field-by-field inspection; conservative `true` fallback. |
| ✅8 | `HeapAnalysisCache.PrebuildHeapIndex()` | Auto-selects memory vs. disk backend by dump size; idempotent on repeat calls. |
| ✅9 | `AnalysisPipeline` | Per-analyzer `Stopwatch`; structured `AnalysisDiagnosticsEvent` at every lifecycle point; graceful cancellation with `Skipped` result. |
| ✅10 | `TypeAggregateIndexBuilder` | Keyed by `ulong MethodTable` — no type-name string allocation in the index hot path. |

---

## Issues by Priority

---

### 🔴 P1 — Critical

---

#### PERF-CRIT-01 · `HeapEntry.Size` is `ulong`; `binary-format.md` documents it as `int` (4 bytes) — spec/code divergence

**File:** `src/DumpDetective.Analysis/Indexing/HeapEntry.cs`, `docs/binary-format.md`

```csharp
// Actual struct:
internal readonly record struct HeapEntry(ulong Address, ulong MethodTable, ulong Size);
// Size = ulong = 8 bytes on disk

// binary-format.md claims:
// | Size    | 4 | int    | Object size in bytes |
// | Padding | 4 | unused | Reserved             |
```

The writer encodes `Size` with `BinaryPrimitives.WriteUInt64LittleEndian(span[16..], entry.Size)` (8 bytes). The reader also reads 8 bytes at offset 16. Both sides are internally consistent at 24 bytes with layout `[Address:8][MethodTable:8][Size:8]`. However:

- `binary-format.md` describes a **different** on-disk layout: `[Address:8][MethodTable:8][Size:4][Padding:4]`.
- The architecture doc shows `HeapEntry.Size` as `int`.
- Any external reader, migration script, or future developer following the spec will produce or consume a **corrupt index**.
- `RecordSize = sizeof(ulong) * 3 = 24` is only coincidentally correct because `Size` happens to be `ulong` in practice.

**Action:** Update `binary-format.md` to reflect the true layout (`Size: 8 bytes, ulong`). Update the architecture doc `HeapEntry` code snippet. If `int` sizing is preferred (saves 4 bytes/record = ~320 MB on 80M objects), change the struct, writer, and reader atomically.

---

#### PERF-CRIT-02 · `GetOrBuildTypeStatistics` executes a full second heap scan when index hydration returns zero entries

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
if (TryHydrateTypeStatisticsFromIndex(...))
{
    _typeStats = hydratedStats;
    return _typeStats;
}
// Falls through to full Parallel.ForEach heap scan even after Phase 1 ran
```

`TryHydrateTypeStatisticsFromIndex` returns `false` only when `hydratedStats.Count == 0`. That triggers a **complete second parallel heap scan** — identical to Phase 1, without the disk write. On a 10 GB dump this adds 10–30 seconds and significant GC pressure. If `typeAggregates` is empty, the fallback scan will also produce nothing. The fallback gives no benefit in any real scenario.

**Action:** If `_heapIndex` exists and `typeAggregates.Count > 0`, trust the index unconditionally. Reserve the fallback scan exclusively for the case where no index was built at all.

---

#### PERF-CRIT-03 · `_retainedObjectsCache` grows without bound

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
private Dictionary<ulong, HashSet<ulong>>? _retainedObjectsCache;
// Each entry capped at maxObjects (default 10,000) ulongs — never evicted
_retainedObjectsCache[rootAddress] = retained;
```

`GetRetainedObjects` is called per root address — once per top-N suspect type sample. For top-50 types with 1 sample each, this accumulates 50 × 10,000 = 500,000 `ulong` entries plus per-`HashSet` metadata (~40 bytes each = ~2 MB overhead on top of data). With no eviction, this memory persists for the entire analysis run.

**Action:** Cap the cache at a fixed entry count (e.g. 32). On insertion when full, either evict the oldest key or skip caching the new result. Document the cap with a comment explaining the trade-off.

---

#### PERF-CRIT-04 · `++_objectScanCount` in `GetRetainedObjects` is non-atomic — data race under parallel Phase 2

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
++_objectScanCount; // plain increment — not atomic
```

Every other increment site in this class uses `Interlocked.Increment(ref _objectScanCount)` or `Interlocked.Add`. This single non-atomic write is a torn-write race if `GetRetainedObjects` is ever called from multiple threads. The architecture document states Phase 2 is planned to be parallelized.

**Action:** Replace `++_objectScanCount` with `Interlocked.Increment(ref _objectScanCount)`.

---

### 🟠 P2 — High

---

#### PERF-HIGH-01 · `DateTime.UtcNow` in BFS inner loop — ~100–250 ms overhead per `TryFindAnyRootPath` call

**File:** `src/DumpDetective.Analysis/Traversal/BoundedRootPathFinder.cs`

```csharp
// Called in the outer root loop AND inside the BFS dequeue loop:
if (DateTime.UtcNow - start > maxDuration) { ... }

// Also called in Complete() for the Elapsed result:
DateTime.UtcNow - started
```

`DateTime.UtcNow` on Windows calls `GetSystemTimeAsFileTime` — a kernel mode transition costing ~200–500 ns. With `maxNodes = 10,000` and `maxRoots = 50`, this executes up to 500,000 times per call. At 300 ns average, that is ~150 ms of pure time-check overhead per invocation, independent of actual BFS work.

`Stopwatch.GetTimestamp()` issues a single `RDTSC` instruction (~5 ns, 40–100× faster) and does not require a kernel transition.

**Action:**

```csharp
// Replace:
DateTime start = DateTime.UtcNow;
if (DateTime.UtcNow - start > maxDuration) { ... }

// With:
long startTicks = Stopwatch.GetTimestamp();
long maxTicks   = (long)(maxDuration.TotalSeconds * Stopwatch.Frequency);
if (Stopwatch.GetTimestamp() - startTicks > maxTicks) { ... }

// For Elapsed in Complete():
TimeSpan elapsed = Stopwatch.GetElapsedTime(startTicks);
```

`Stopwatch.GetElapsedTime(long)` is a .NET 7+ API and available on .NET 10.

---

#### PERF-HIGH-02 · `Dictionary<string, CachedTypeStatistics>` — string keys in analysis-hot cache

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
private Dictionary<string, CachedTypeStatistics>? _typeStats;
private Dictionary<string, ulong>? _sampleInstances;
```

Every lookup in `GetOrBuildTypeStatistics`, `GetSampleInstanceAddress`, and `TryHydrateTypeStatisticsFromIndex` hashes and compares type name strings. On a dump with 5,000 unique types, this means 5,000 string hash computations and comparisons per merge. Per checklist: **"Do NOT use `Dictionary<string, ...>` in hot paths."**

Phase 1 already produces `TypeAggregateIndexEntry` keyed by `ulong MethodTable`. The string name is only needed at output time.

**Action:**
1. Change `_typeStats` to `Dictionary<ulong, CachedTypeStatistics>` (keyed by `MethodTable`).
2. Change `_sampleInstances` to `Dictionary<ulong, ulong>` (MethodTable → sample address).
3. Keep a separate `Dictionary<ulong, string>` for display-name resolution, populated lazily at report time.
4. Update callers to use `MethodTable` keys; resolve type name strings in `FindingGenerator` / `Printer` layers only.

---

#### PERF-HIGH-03 · `LazyReferenceGraph` full-cache eviction causes thrash cliff on dense graphs

**File:** `src/DumpDetective.Analysis/Traversal/LazyReferenceGraph.cs`

```csharp
if (_cache.Count >= MaxCachedNodes)
    _cache.Clear(); // evicts all 500,000 entries at once
```

The file's own comment acknowledges the risk. When the cache hits 500,000 entries on a dense graph (large collection types, event subscriber chains), it clears entirely. The very next BFS immediately re-fetches the same hot nodes — triggering a second wave of `ClrObject.EnumerateReferences` calls. On a graph where the top-N types share many common nodes, this cycle repeats, making cache hit rate effectively zero in the worst case.

**Action:** Replace full-clear with ordered partial eviction. .NET 10 `Dictionary<TKey, TValue>` preserves insertion order for enumeration. Evict the oldest 50% on threshold using `ArrayPool<ulong>` to collect the keys, avoiding LINQ:

```csharp
if (_cache.Count >= MaxCachedNodes)
{
    int toRemove = MaxCachedNodes / 2;
    ulong[] keys = ArrayPool<ulong>.Shared.Rent(toRemove);
    int i = 0;
    foreach (ulong key in _cache.Keys)
    {
        if (i >= toRemove) break;
        keys[i++] = key;
    }
    for (int j = 0; j < i; j++)
        _cache.Remove(keys[j]);
    ArrayPool<ulong>.Shared.Return(keys);
}
```

---

#### PERF-HIGH-04 · `DiskBackedObjectIndexWriter` materialises all `HeapEntry` records into a `HeapEntry[]` before any disk write — peak RAM doubles

**File:** `src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs`

```csharp
// All per-segment lists remain alive while the flat array is allocated:
HeapEntry[] entries = new HeapEntry[Math.Max(totalCount, 1)];
// ... CopyTo from all segment lists ...
foreach (HeapEntry entry in entries) { /* write to disk */ }
```

After parallel segment scanning, per-segment `List<HeapEntry>` instances are alive **simultaneously** with the flat `entries` array. On 80M objects × 24 bytes = ~1.9 GB in lists + ~1.9 GB in the array = **~3.8 GB peak RAM** before a single byte reaches disk. This directly violates the bounded-memory core charter.

**Action:** Write each segment's entries to disk as it completes inside the `Parallel.ForEach` body, protected by a `SemaphoreSlim(1,1)` for sequential disk access. This eliminates `entries`, `allSegmentEntries`, and the flatten step entirely. Each segment's `List<HeapEntry>` is freed as soon as its write completes.

---

#### PERF-HIGH-05 · `CancellationToken.ThrowIfCancellationRequested()` called once per record in the write loop

**File:** `src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs`

```csharp
foreach (HeapEntry entry in entries)
{
    cancellationToken.ThrowIfCancellationRequested(); // 80M volatile reads
    ...
}
```

On 80M objects this executes 80M volatile reads of the cancellation token's state, even when cancellation is never requested. `ThrowIfCancellationRequested` internally reads a volatile `bool` and a linked-list of registered callbacks on every call.

**Action:** Throttle to the existing `ProgressReportEveryObjects` cadence (100,000 records):

```csharp
if (writtenCount % ProgressReportEveryObjects == 0)
{
    cancellationToken.ThrowIfCancellationRequested();
    progress?.Report(...);
}
```

---

#### PERF-HIGH-06 · `ResolveTypeNameFromSample` calls `heap.GetObject()` for every unique type during index hydration

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
foreach ((ulong methodTable, TypeAggregateIndexEntry aggregate) in typeAggregates)
{
    string typeName = ResolveTypeNameFromSample(heap, aggregate.SampleAddress, methodTable);
    // ResolveTypeNameFromSample → heap.GetObject(sampleAddress) — one ClrMD object read per type
}
```

On a dump with 5,000 unique types, this is 5,000 `heap.GetObject()` calls in a tight loop, each potentially causing a page fault into the dump file. `heap.GetTypeByMethodTable(methodTable)` achieves the same result using already-loaded type metadata without an object read.

**Action:**

```csharp
private static string ResolveTypeNameFromSample(ClrHeap heap, ulong sampleAddress, ulong methodTable)
{
    ClrType? type = heap.GetTypeByMethodTable(methodTable);
    if (type?.Name is string name)
        return name;

    if (sampleAddress != 0)
    {
        ClrObject sample = heap.GetObject(sampleAddress);
        if (sample.IsValid && sample.Type?.Name is string sampleName)
            return sampleName;
    }

    return $"MethodTable@0x{methodTable:X}";
}
```

---

### 🟡 P3 — Medium

---

#### PERF-MED-01 · `EnumerateIndexedEntriesAsTuples()` wraps a streaming enumerator in LINQ `.Select()`

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
    => EnumerateIndexedEntries().Select(e => (e.Address, e.MethodTable, e.Size));
```

This allocates a `SelectEnumerableIterator<HeapEntry, (ulong,ulong,ulong)>` wrapper and a delegate closure on every call. Callers iterate millions of entries through this wrapper. Per checklist: **"Avoid LINQ in hot paths."**

**Action:**

```csharp
public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
{
    foreach (HeapEntry entry in EnumerateIndexedEntries())
        yield return (entry.Address, entry.MethodTable, entry.Size);
}
```

---

#### PERF-MED-02 · `RootCandidate` struct carries a `string RootKind` field — managed reference in a traversal-hot struct

**File:** `src/DumpDetective.Analysis/Traversal/BoundedRootPathFinder.cs`

```csharp
internal readonly record struct RootCandidate(string RootKind, ulong Address);
```

`RootCandidate` is iterated up to `maxRoots` times per call. The `RootKind` string is only consumed when a path is **found** (one success out of potentially thousands of roots checked). Carrying a managed reference through the entire BFS root loop inflates struct size and increases GC scan surface for no benefit in the common (not-found) case.

**Action:** Replace `string RootKind` with a `byte`-sized enum:

```csharp
internal enum RootKind : byte { Static, Stack, Finalizer, Handle, AsyncStateMachine, Other }
internal readonly record struct RootCandidate(RootKind Kind, ulong Address);
```

Resolve the display string only in `Complete()` when a path is actually returned.

---

#### PERF-MED-03 · `ReconstructPath` allocates a `List<ulong>` then calls `.Reverse()` — two O(N) passes

**File:** `src/DumpDetective.Analysis/Traversal/BoundedRootPathFinder.cs`

```csharp
List<ulong> reversed = new(capacity: 16) { targetAddress, targetParent };
// ... traverse `previous` backwards ...
reversed.Reverse(); // second pass
```

Using a `Stack<ulong>` naturally produces the path in the correct (root → target) order without a reverse pass, saving one O(N) traversal:

```csharp
private static IReadOnlyList<ulong> ReconstructPath(
    Dictionary<ulong, ulong> previous, ulong startAddress, ulong targetAddress, ulong targetParent)
{
    Stack<ulong> stack = new(capacity: 16);
    stack.Push(targetAddress);
    ulong cursor = targetParent;
    while (cursor != startAddress && previous.TryGetValue(cursor, out ulong parent))
    {
        stack.Push(cursor);
        cursor = parent;
    }
    stack.Push(startAddress);
    return stack.ToArray();
}
```

---

#### PERF-MED-04 · `BoundedPathSearchResult` is a `sealed record` — synthesized equality machinery never used

**File:** `src/DumpDetective.Analysis/Traversal/BoundedRootPathFinder.cs`

```csharp
internal sealed record BoundedPathSearchResult(bool Found, string? RootKind, IReadOnlyList<ulong>? Path, ...);
```

`record` synthesizes `Equals`, `GetHashCode`, `==`, `!=`, `<Clone>$`, and `PrintMembers`. None are used on a result container. `IReadOnlyList<ulong>?` in the record means `GetHashCode` falls back to reference equality on the list — potentially misleading. `sealed class` with init-only properties is semantically cleaner and avoids the generated overhead.

**Action:** Change to `internal sealed class BoundedPathSearchResult` with a constructor or `init` properties.

---

#### PERF-MED-05 · `_methodTableHasRefs` is not thread-safe — will race when Phase 2 is parallelized

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
private Dictionary<ulong, bool>? _methodTableHasRefs;
// ...
_methodTableHasRefs ??= new Dictionary<ulong, bool>(capacity: 512);
_methodTableHasRefs[methodTable] = has; // non-thread-safe write
```

The architecture document explicitly plans parallel Phase 2. `MethodTableHasOutgoingRefs` is called from graph traversal during BFS. Concurrent writes to a `Dictionary<TKey,TValue>` produce undefined behaviour.

**Action:** Change to `ConcurrentDictionary<ulong, bool>` or protect all reads/writes with a dedicated `lock` object.

---

#### PERF-MED-06 · No centralized `RuntimeFacade` — `ClrHeap` used directly across all analyzers

**Architecture doc:** Section 5.1 specifies a `RuntimeFacade` that "wraps and caches ClrMD APIs."  
**Reality:** `ClrHeap` and `ClrRuntime` are passed raw through `RuntimeAnalysisContext` and consumed ad-hoc.

Consequences:
- `ClrType` metadata is fetched redundantly per-analyzer (`heap.GetTypeByMethodTable` called independently in multiple places).
- No centralized field-layout cache — violates the checklist: *"Cache: ClrType metadata, Field layouts."*
- Hard to unit-test analyzers without a real or mock `ClrHeap`.

**Action:** Introduce `IRuntimeFacade` with `GetCachedType(ulong methodTable)` and `GetCachedFields(ClrType type)`. Back it with a `Dictionary<ulong, ClrType>` populated during Phase 1 from `TypeAggregates`. Inject it into `RuntimeAnalysisContext`.

---

#### PERF-MED-07 · Phase 2 analyzers run sequentially despite architecture intent to parallelize

**File:** `src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs`

```csharp
foreach (IAnalyzer analyzer in _analyzers) { ... await ... }
```

Architecture doc Section 12: *"Phase 2: Parallelizable — Type analysis, Graph traversal (controlled)."*  
`ThreadAnalyzer`, `GCHandleAnalyzer`, `LohFragmentationAnalyzer`, and `ModuleAnalyzer` have no data dependencies on each other and all operate on already-built indices. Running them sequentially wastes wall-clock time proportional to the number of independent analyzers.

**Note:** Requires PERF-MED-05 and PERF-MED-06 (thread-safety) to be resolved first.

**Action:** Add `bool IsThreadSafe { get; }` to `IAnalyzer` (default `false` via default interface implementation). Group thread-safe analyzers and run them via `Task.WhenAll`; run the rest sequentially.

---

#### PERF-MED-08 · Progress reporting called from parallel threads inside `GetOrBuildTypeStatistics` fallback scan

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

```csharp
// Inside Parallel.ForEach over heap.Segments:
long s = Interlocked.Increment(ref totalScanned); // correct
// ...
_progress?.Report(...) // called from multiple parallel threads concurrently
```

`Progress<T>.Report()` on a console app without a `SynchronizationContext` dispatches directly on the calling thread — meaning concurrent calls from `Parallel.ForEach` workers race on any shared state the progress handler touches.

**Action:** Report progress only inside the sequential merge phase (after `Parallel.ForEach` completes) using the final `totalScanned` value.

---

### 🟢 P4 — Low / Proactive

---

#### PERF-LOW-01 · `HeapIndexEntryReader` partial-record remainder path silently discards data on premature stream end

**File:** `src/DumpDetective.Analysis/Indexing/HeapIndexEntryReader.cs`

```csharp
int nextRead = stream.Read(readBuffer, remaining, batchSize - remaining);
if (nextRead <= 0)
    break; // `remaining` bytes of partial record silently dropped
```

If the file ends mid-record (crash during index write, disk full), the partial record is silently discarded. The caller receives fewer entries than the header's `recordCount` field promises with no diagnostic.

**Action:** Add a `Debug.Assert(remaining % RecordSize == 0)` before the `break`, and log a warning in production when `remaining > 0 && remaining < RecordSize`.

---

#### PERF-LOW-02 · `BoundedPathSearchBudget` is a `readonly record struct` — synthesized equality unused

**File:** `src/DumpDetective.Analysis/Traversal/BoundedRootPathFinder.cs`

```csharp
internal readonly record struct BoundedPathSearchBudget(int MaxRoots, int MaxNodes, ...);
```

A configuration struct passed as a parameter does not need record equality semantics. `readonly struct` is sufficient and avoids synthesized `Equals`/`GetHashCode`/`PrintMembers`.

**Action:** Change to `internal readonly struct BoundedPathSearchBudget` with explicit property declarations.

---

#### PERF-LOW-03 · `IAnalyzer` missing a concurrency contract — future parallel Phase 2 has no safety signal

**File:** `src/DumpDetective.Core/Abstractions/IAnalyzer.cs`

Without a declared concurrency contract, third-party analyzer implementers have no guidance on whether their analyzer will be called from multiple threads simultaneously. A silent concurrent call to a single-threaded analyzer will produce non-deterministic results once PERF-MED-07 is implemented.

**Action:** Add `bool IsThreadSafe { get; }` with a default interface implementation returning `false`, so `AnalysisPipeline` can query it before scheduling parallel execution.

---

#### PERF-LOW-04 · `using System.Linq` in `HeapAnalysisCache.cs` — LINQ available in hot-path context

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

Having `System.Linq` imported in a file containing hot-path methods creates low friction for accidental LINQ introduction in future edits. After PERF-MED-01 is resolved (removing `.Select()`), remove this import entirely to enforce the no-LINQ rule at the compiler level.

---

#### PERF-LOW-05 · `AnalysisPipeline` constructor calls `.ToList()` on `IEnumerable<IAnalyzer>`

**File:** `src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs`

```csharp
private readonly IReadOnlyList<IAnalyzer> _analyzers = analyzers.ToList();
```

Analyzer count is always small (< 20) so this is not a performance issue. However, `.ToList()` on `IEnumerable<T>` is the exact pattern banned for heap-scale enumerables. It sets a misleading precedent in a file that new contributors will read as a pattern reference.

**Action:** Accept `IReadOnlyList<IAnalyzer>` directly in the constructor signature.

---

## Architecture Gaps vs. Design Documents

| Gap | Architecture Doc Reference | Reality |
|-----|---------------------------|---------|
| No `QueryEngine` layer | Section 5.6: *"Operates on indices, not raw heap"* | Analyzers access `HeapAnalysisCache` directly; no boundary prevents raw heap access |
| No `RuntimeFacade` | Section 5.1 | `ClrHeap`/`ClrRuntime` passed raw; no centralized type metadata cache |
| `InsightEngine` not implemented | Section 5.7 | Findings generated directly in `FindingGenerator` classes |
| `ReverseReferenceIndex` not implemented | Section 5.4 | `GetRetainedObjects` traverses forward from root, which is not equivalent to a reverse index |
| `HeapEntry.Size` type mismatch | Architecture doc shows `int Size` | Actual field is `ulong` — 8 bytes, not 4 |
| `binary-format.md` layout incorrect | Section 3 | Doc: `[Address:8][MT:8][Size:4][Pad:4]`; actual: `[Address:8][MT:8][Size:8]` |

---

## Fix Priority Backlog

| ID | Severity | File | Description | Effort |
|----|----------|------|-------------|--------|
| ~~PERF-CRIT-01~~ | 🔴 | ~~`HeapEntry.cs`, `binary-format.md`~~ | ~~Spec/code divergence on `Size` field layout~~ | ✅ **Done** |
| PERF-CRIT-02 | 🔴 | `HeapAnalysisCache.cs` | Second full heap scan when index hydration returns empty | S |
| PERF-CRIT-03 | 🔴 | `HeapAnalysisCache.cs` | `_retainedObjectsCache` unbounded memory growth | S |
| ~~PERF-CRIT-04~~ | 🔴 | ~~`HeapAnalysisCache.cs`~~ | ~~`++_objectScanCount` non-atomic — data race~~ | ✅ **Done** |
| PERF-HIGH-01 | 🟠 | `BoundedRootPathFinder.cs` | `DateTime.UtcNow` in BFS inner loop; replace with `Stopwatch` | XS |
| PERF-HIGH-02 | 🟠 | `HeapAnalysisCache.cs` | `Dictionary<string, ...>` hot-path cache; change to `ulong` keys | M |
| PERF-HIGH-03 | 🟠 | `LazyReferenceGraph.cs` | Full-clear eviction causes thrash cliff on dense graphs | S |
| PERF-HIGH-04 | 🟠 | `DiskBackedObjectIndexWriter.cs` | All entries materialized into `HeapEntry[]` before disk write — ~2× peak RAM | L |
| ~~PERF-HIGH-05~~ | 🟠 | ~~`DiskBackedObjectIndexWriter.cs`~~ | ~~`ThrowIfCancellationRequested()` per-record; throttle to 100K cadence~~ | ✅ **Done** |
| PERF-HIGH-06 | 🟠 | `HeapAnalysisCache.cs` | `heap.GetObject()` per type in hydration; use `GetTypeByMethodTable` instead | XS |
| PERF-MED-01 | 🟡 | `HeapAnalysisCache.cs` | LINQ `.Select()` on streaming enumerator; replace with `yield return` | XS |
| PERF-MED-02 | 🟡 | `BoundedRootPathFinder.cs` | `string RootKind` in traversal-hot struct; intern to `enum RootKind : byte` | S |
| PERF-MED-03 | 🟡 | `BoundedRootPathFinder.cs` | `ReconstructPath` list + `.Reverse()`; use `Stack<ulong>` instead | XS |
| ~~PERF-MED-04~~ | 🟡 | ~~`BoundedRootPathFinder.cs`~~ | ~~`BoundedPathSearchResult` as `sealed record`; change to `sealed class`~~ | ✅ **Done** |
| PERF-MED-05 | 🟡 | `HeapAnalysisCache.cs` | `_methodTableHasRefs` not thread-safe; change to `ConcurrentDictionary` | XS |
| PERF-MED-06 | 🟡 | Architecture | No `RuntimeFacade`; `ClrHeap` used raw in all analyzers | L |
| PERF-MED-07 | 🟡 | `AnalysisPipeline.cs` | Phase 2 runs sequentially; parallelize independent analyzers | M |
| PERF-MED-08 | 🟡 | `HeapAnalysisCache.cs` | Progress reporting called from parallel threads | XS |
| PERF-LOW-01 | 🟢 | `HeapIndexEntryReader.cs` | Partial-record remainder silently dropped; add diagnostic | XS |
| ~~PERF-LOW-02~~ | 🟢 | ~~`BoundedRootPathFinder.cs`~~ | ~~`BoundedPathSearchBudget` as `record struct`; simplify to plain `struct`~~ | ✅ **Done** |
| ~~PERF-LOW-03~~ | 🟢 | ~~`IAnalyzer.cs`~~ | ~~No concurrency contract; add `IsThreadSafe` default interface property~~ | ✅ **Done** |
| PERF-LOW-04 | 🟢 | `HeapAnalysisCache.cs` | Remove `using System.Linq` after PERF-MED-01 | XS |
| PERF-LOW-05 | 🟢 | `AnalysisPipeline.cs` | `.ToList()` on analyzer collection; accept `IReadOnlyList` directly | XS |

**Effort key:** XS = < 30 min · S = 1–2 h · M = half-day · L = 1–2 days

---

## Recommended Execution Order

```
Phase A — Correctness & Safety  (zero behaviour change — do first, batch in one PR)
  ✅ PERF-CRIT-04  ++_objectScanCount  →  Interlocked.Increment
  ✅ PERF-HIGH-05  ThrowIfCancellationRequested throttle to 100K cadence
  ✅ PERF-CRIT-01  Update binary-format.md and architecture.md to match HeapEntry code
  ✅ PERF-MED-04   BoundedPathSearchResult  →  sealed class
  ✅ PERF-LOW-02   BoundedPathSearchBudget  →  plain readonly struct
  ✅ PERF-LOW-03   IAnalyzer.IsThreadSafe default interface property

Phase B — Performance hot-path fixes  (high ROI, low risk)
  PERF-HIGH-01  DateTime.UtcNow  →  Stopwatch in BoundedRootPathFinder
  PERF-HIGH-06  heap.GetObject()  →  GetTypeByMethodTable in hydration
  PERF-MED-01   EnumerateIndexedEntriesAsTuples  →  yield return
  PERF-MED-03   ReconstructPath  →  Stack<ulong>
  PERF-MED-08   Throttle parallel progress report to sequential merge phase
  PERF-LOW-04   Remove using System.Linq from HeapAnalysisCache

Phase C — Memory bounds  (validate with large-dump run after each)
  PERF-CRIT-02  Guard fallback heap scan in GetOrBuildTypeStatistics
  PERF-CRIT-03  Cap _retainedObjectsCache at 32 entries
  PERF-HIGH-03  LazyReferenceGraph partial eviction (ArrayPool-based)
  PERF-HIGH-04  DiskBackedObjectIndexWriter write-as-you-go (eliminates HeapEntry[])

Phase D — Architecture alignment  (larger scope, plan separately)
  PERF-HIGH-02  Dictionary<string,>  →  Dictionary<ulong,> in HeapAnalysisCache
  PERF-MED-02   RootCandidate.RootKind  →  enum RootKind : byte
  PERF-MED-05   _methodTableHasRefs  →  ConcurrentDictionary
  PERF-MED-06   Introduce IRuntimeFacade / RuntimeFacade
  PERF-MED-07   Parallel Phase 2 in AnalysisPipeline  (requires MED-05 + MED-06 first)
  PERF-LOW-01   HeapIndexEntryReader partial-record diagnostic
  PERF-LOW-05   AnalysisPipeline constructor  →  accept IReadOnlyList<IAnalyzer>
```
