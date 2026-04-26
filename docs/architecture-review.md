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

### CRITICAL-03 — `FindingGenerationPipeline` silently swallows exceptions

**File:** `src/DumpDetective.Reporting/Pipeline/FindingGenerationPipeline.cs`

#### What
```csharp
catch
{
    // swallows errors from finding generation to avoid failing reporting; diagnostics can be emitted from caller
    updated.Add(run);
}
```
The catch block does nothing — no logging, no sink event, no error message on the run result. A generator can throw `NullReferenceException`, `InvalidCastException`, or any other exception and the user sees zero indication that findings are missing.

#### Why it's a problem
Directly violates the project guideline: *"more actionable diagnostic data is better than condensed summaries"*. Silent failure in finding generation means the report may show zero findings for an analyzer not because there are none, but because the generator crashed. The user has no way to know.

#### How to fix
Inject `IAnalysisDiagnosticsSink` (already exists in `Core`) and publish a `FindingGeneratorFailed` event. Also surface the error in the `AnalyzerRunResult` itself by setting an error field.

The cleanest approach requires `AnalyzerRunResult` to carry a nullable `FindingGeneratorError` string (add it to the record) so the report printer can optionally render it as a warning row.

**Option A — Minimal (no model change): inject and publish to sink**

```csharp
// FindingGenerationPipeline.cs
internal sealed class FindingGenerationPipeline(
    IEnumerable<IFindingGenerator> generators,
    IAnalysisDiagnosticsSink diagnosticsSink)   // ← inject sink
{
    ...
    catch (Exception ex)
    {
        diagnosticsSink.Publish(new AnalysisDiagnosticsEvent(
            RunId: Guid.Empty,
            EventType: AnalysisDiagnosticsEventType.AnalyzerFailed,   // reuse or add FindingGeneratorFailed
            TimestampUtc: DateTime.UtcNow,
            AnalyzerName: run.AnalyzerName,
            Category: "FindingGeneration",
            DurationMs: null,
            ObjectScanCount: 0,
            CacheHits: 0,
            CacheMisses: 0,
            Message: $"Finding generator for '{run.AnalyzerName}' threw: {ex.Message}",
            ExceptionType: ex.GetType().Name,
            ExceptionMessage: ex.Message));
        updated.Add(run);
    }
}
```

**Option B — Full (model change): add `FindingGeneratorError` to `AnalyzerRunResult`**

```csharp
// AnalyzerRunResult.cs — add optional field
internal sealed record AnalyzerRunResult(
    ...
    string? FindingGeneratorError = null)    // ← add
```

Then in the catch:
```csharp
updated.Add(run with { FindingGeneratorError = $"{ex.GetType().Name}: {ex.Message}" });
```

Report printers can then emit this as a `[WARN] Finding generator error: ...` row in the relevant section.

**Recommendation:** Option B gives users the most visibility and is consistent with the project's detail-preservation principle.

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

### MAJOR-05 — Options resolution uses fragile magic-key dictionary pattern

**Files:** `Analysis/Analyzers/MemoryLeakAnalyzer.cs`, `ReferenceChainAnalyzer.cs`, `EventLeakAnalyzer.cs`, `RunAnalyzersPipelineStage.cs`

#### What
Every analyzer that needs options does this:
```csharp
MemoryLeakOptions options = context.Options.TryGetValue(nameof(MemoryLeakOptions), out object? configured)
    && configured is MemoryLeakOptions typed
    ? typed
    : new MemoryLeakOptions();
```
And `RunAnalyzersPipelineStage` populates the same dictionary with keys that must match these strings:
```csharp
Options = new Dictionary<string, object?>
{
    [nameof(MemoryLeakOptions)]     = resolved.MemoryLeak,
    [nameof(ReferenceChainOptions)] = resolved.ReferenceChain,
    [nameof(EventLeakOptions)]      = resolved.EventLeak,
    [nameof(DiagnosticsOptions)]    = resolved.Diagnostics
}
```

#### Why it's a problem
- The dictionary key is a compile-time string (`nameof`) but the coupling between producer and consumer is entirely by convention. Rename the options class and the analyzer silently falls back to defaults — **no compiler error, no test failure**.
- The pattern is copy-pasted across at least 3 analyzers.
- The strongly-typed properties (`MemoryLeakOptions`, `ReferenceChainOptions`, etc.) already exist on `DumpDetective.Analysis.Pipeline.AnalysisContext` but analyzers can't access them because `IAnalyzer.AnalyzeAsync` accepts the base `Core.Abstractions.AnalysisContext`.

#### How to fix
**Option A — Extension method (KISS, no interface change):**
```csharp
// In Analysis project
internal static class AnalysisContextExtensions
{
    public static T GetOption<T>(this AnalysisContext context, T defaultValue = default!)
        where T : class, new()
    {
        string key = typeof(T).Name;
        return context.Options.TryGetValue(key, out object? value) && value is T typed
            ? typed
            : defaultValue ?? new T();
    }
}
```

Usage in each analyzer:
```csharp
// BEFORE
MemoryLeakOptions options = context.Options.TryGetValue(nameof(MemoryLeakOptions), out object? configured)
    && configured is MemoryLeakOptions typed ? typed : new MemoryLeakOptions();

// AFTER
MemoryLeakOptions options = context.GetOption<MemoryLeakOptions>();
```

**Option B — Cast to derived context (since all analyzers live in `Analysis`):**
Since `IAnalyzer` implementations are all in the `Analysis` assembly, they can safely cast:
```csharp
// In AnalyzeAsync, before accessing options:
if (context is DumpDetective.Analysis.Pipeline.AnalysisContext richContext)
    return ValueTask.FromResult(Analyze(richContext.MemoryLeakOptions, ...).Stamp(this));
```

**Recommendation:** Option A. It's less invasive, adds no cast, and centralises the fallback logic in one place.

---

### MAJOR-06 — `HeapAnalysisCache` bypasses `IHeapAnalysisCache` in the CLI pipeline

**Files:** `Cli/Pipeline/SingleDumpPipelineState.cs`, `Cli/Pipeline/Stages/BuildHeapIndexStage.cs`

#### What
```csharp
// SingleDumpPipelineState.cs
public HeapAnalysisCache? HeapCache { get; set; }   // concrete type, not IHeapAnalysisCache

// BuildHeapIndexStage.cs
HeapAnalysisCache heapCache = new();
HeapIndexBuildResult heapIndex = heapCache.PrebuildHeapIndex(...); // not on the interface
```

`IHeapAnalysisCache` is the declared contract but `PrebuildHeapIndex()` and `SetProgress()` are only on the concrete class.

#### Why it's a problem
- The interface provides no actual abstraction for the CLI layer. It's impossible to substitute a test double for `HeapAnalysisCache` in pipeline stage tests.
- `BuildHeapIndexStage` and `RunAnalyzersPipelineStage` are implicitly coupled to the concrete class through the state bag.

#### How to fix
Introduce a second interface covering the build-time API, keeping `IHeapAnalysisCache` as the analyzer read-only contract:

```csharp
// In Core.Abstractions
public interface IHeapIndexBuilder
{
    HeapIndexBuildResult PrebuildHeapIndex(
        ClrHeap heap,
        string dumpPath,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        HeapIndexPrebuildMode mode = HeapIndexPrebuildMode.Auto);

    void SetProgress(IProgress<AnalyzerProgressReport>? progress);
}
```

`HeapAnalysisCache` implements both `IHeapAnalysisCache` and `IHeapIndexBuilder`. The state bag becomes:

```csharp
// SingleDumpPipelineState.cs
public IHeapIndexBuilder? HeapIndexBuilder { get; set; }   // for BuildHeapIndexStage
public IHeapAnalysisCache? HeapCache { get; set; }         // for RunAnalyzersPipelineStage

// Both point to the same HeapAnalysisCache instance, typed through their respective interfaces
```

`BuildHeapIndexStage` uses `IHeapIndexBuilder`, `RunAnalyzersPipelineStage` uses `IHeapAnalysisCache` via `context.Cache`. Pipeline stage tests can now substitute either independently.

---

### MAJOR-07 — Two `Pipeline` namespaces with `AnalysisContext` name collision

**Files:** `Analysis/Pipeline/AnalysisContext.cs`, `Cli/Pipeline/Stages/RunAnalyzersPipelineStage.cs`, `Cli/Services/DumpAnalysisService.cs`

#### What
There is `DumpDetective.Core.Abstractions.AnalysisContext` (base), `DumpDetective.Analysis.Pipeline.AnalysisContext` (derived), and both are in a namespace called `Pipeline`. Any file that needs both must alias one:

```csharp
// Required in RunAnalyzersPipelineStage.cs and DumpAnalysisService.cs
using PipelineAnalysisContext = DumpDetective.Analysis.Pipeline.AnalysisContext;
```

#### Why it's a problem
- Naming friction. The alias `PipelineAnalysisContext` is used in multiple files, adding ceremony.
- `AnalysisContext` in `Core` is already not in a `Pipeline` namespace (it's in `Abstractions`) — the name collision is only in the derived type.

#### How to fix
Rename `DumpDetective.Analysis.Pipeline.AnalysisContext` to `RuntimeAnalysisContext`. This is a single file rename with find-and-replace on usages:

```csharp
// BEFORE: Analysis/Pipeline/AnalysisContext.cs
namespace DumpDetective.Analysis.Pipeline;
internal sealed class AnalysisContext : DumpDetective.Core.Abstractions.AnalysisContext { ... }

// AFTER: Analysis/Pipeline/RuntimeAnalysisContext.cs
namespace DumpDetective.Analysis.Pipeline;
internal sealed class RuntimeAnalysisContext : DumpDetective.Core.Abstractions.AnalysisContext { ... }
```

Affected files to update:
- `Analysis/Pipeline/AnalysisPipeline.cs`
- `Cli/Pipeline/Stages/RunAnalyzersPipelineStage.cs` — remove the `using` alias
- `Cli/Services/DumpAnalysisService.cs` — remove the `using` alias
- `Cli/Pipeline/SingleDumpPipelineState.cs` (if it references the type directly)

---

### MINOR-08 — `LazyReferenceGraph` full-cache eviction on limit hit

**File:** `src/DumpDetective.Analysis/Traversal/LazyReferenceGraph.cs`

#### What
```csharp
private const int MaxCachedNodes = 500_000;

public IReadOnlyList<ulong> GetReferences(ulong address)
{
    ...
    if (_cache.Count >= MaxCachedNodes)
        _cache.Clear();   // ← blows entire cache
    ...
}
```

#### Why it's a problem
In worst-case dense graph traversal (e.g., a large collection type appearing as the top-N sample), the same root nodes are visited repeatedly across multiple BFS runs. When the limit is hit mid-BFS, `_cache.Clear()` discards nodes that will be immediately re-requested in the same run. This creates a thrash cycle: fill → clear → fill → clear.

#### How to fix
**Option A — Accept and document (KISS, current behavior is memory-safe):**
Add a comment documenting the worst-case thrash scenario and the deliberate choice:
```csharp
// Full-clear eviction: when the 500 000-node limit is hit, the entire cache is discarded
// and rebuilt from the next BFS traversal. This is intentionally aggressive — it bounds
// peak memory at the cost of potential re-fetching in dense-graph scenarios. The 500 000
// limit is sized to avoid this in practice for typical production dumps (<500 MB managed heap).
// If thrash is observed on very large dumps, consider halving the limit or switching to a
// generation-based strategy (evict the oldest 50% by insertion order).
if (_cache.Count >= MaxCachedNodes)
    _cache.Clear();
```

**Option B — Partial eviction (evict oldest 50%):**
```csharp
if (_cache.Count >= MaxCachedNodes)
{
    // Evict the oldest half of entries by insertion order.
    // Dictionary<K,V> in .NET preserves insertion order for enumeration.
    int toRemove = _cache.Count / 2;
    foreach (ulong key in _cache.Keys.Take(toRemove).ToList())
        _cache.Remove(key);
}
```

**Recommendation:** Option A for now (KISS). Document it and revisit during profiling on large dumps.

---

### MINOR-09 — `FindingGenerationPipeline.GenerateAsync` is sync wrapped in `Task.FromResult`

**File:** `src/DumpDetective.Reporting/Pipeline/FindingGenerationPipeline.cs`

#### What
```csharp
public Task<IReadOnlyList<AnalyzerRunResult>> GenerateAsync(...)
{
    List<AnalyzerRunResult> updated = new(runs.Count);
    foreach (AnalyzerRunResult run in runs)
    {
        // entirely synchronous work
    }
    return Task.FromResult((IReadOnlyList<AnalyzerRunResult>)updated);
}
```

#### Why it's a problem
The `Async` suffix implies I/O or concurrency but there is none. Callers may `await` this expecting actual async behavior. It allocates a `Task` wrapper unnecessarily.

#### How to fix
Either make it truly sync or use `ValueTask`:

```csharp
// Option A — remove async fiction, make it sync
public IReadOnlyList<AnalyzerRunResult> Generate(IReadOnlyList<AnalyzerRunResult> runs, CancellationToken cancellationToken)
{ ... }

// Option B — use ValueTask for zero-allocation on the hot path
public ValueTask<IReadOnlyList<AnalyzerRunResult>> GenerateAsync(...)
{
    ...
    return ValueTask.FromResult((IReadOnlyList<AnalyzerRunResult>)updated);
}
```

Update the call site in `GenerateFindingsStage.cs` accordingly.

---

### MINOR-10 — Duplicate iteration code in `HeapAnalysisCache`

**File:** `src/DumpDetective.Analysis/Cache/HeapAnalysisCache.cs`

#### What
`EnumerateIndexedEntries()` and `EnumerateIndexedEntriesAsTuples()` are structurally identical — the only difference is the projection:

```csharp
// Method 1: yields HeapEntry
foreach (HeapEntry entry in HeapIndexEntryReader.ReadDiskEntries(_heapIndex.IndexPath))
    yield return entry;

// Method 2: yields (Address, MethodTable, Size)
foreach (HeapEntry entry in HeapIndexEntryReader.ReadDiskEntries(_heapIndex.IndexPath))
    yield return (entry.Address, entry.MethodTable, entry.Size);
```

#### How to fix
```csharp
// IHeapAnalysisCache already exposes the tuple variant; internally delegate:
public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
    => EnumerateIndexedEntries().Select(e => (e.Address, e.MethodTable, e.Size));
```

If `HeapEntry` is a small struct, this is zero-overhead. If callers need the tuple form to avoid the `HeapEntry` type dependency, keep both but delegate the implementation.

---

### MINOR-11 — `AnalyzerCategory.Infer()` relies on fragile name matching

**File:** `src/DumpDetective.Core/Abstractions/IAnalyzer.cs`

#### What
```csharp
internal static string Infer(string analyzerName)
{
    string name = analyzerName.ToLowerInvariant();
    if (name.Contains("memory")) return "Memory";
    if (name.Contains("thread")) return "Threads";
    // ...
    return "General";   // silent fallback
}
```

#### Why it's a problem
If an analyzer is renamed (e.g., `"Heap Pressure Analysis"` instead of `"Memory Analysis"`), its category silently drops to `"General"`. This affects report grouping and trend analysis bucketing. The `Contains("memory")` check is a ticking maintenance debt.

#### How to fix
Prefer explicit override in each analyzer. `IAnalyzer.Category` already has a default implementation that calls `Infer()` — analyzers simply need to override it:

```csharp
// IAnalyzer.cs — keep Infer() as the default fallback, not the primary mechanism
string Category => "General";  // each analyzer overrides this

// MemoryLeakAnalyzer.cs
public string Category => "Memory";

// ThreadAnalyzer.cs
public string Category => "Threads";
```

This is a two-line change per analyzer but gives compile-time guarantees that category is always correct. Keep `Infer()` as a fallback for third-party or dynamically-loaded analyzers only, and log a warning when the fallback fires.

---

### MINOR-12 — `ConfigurationResolver` has mechanical duplication

**File:** `src/DumpDetective.Cli/Services/ConfigurationResolver.cs`

#### What
```csharp
MemoryLeakOptions memoryLeak = usedConfigFile
    ? BuildMemoryLeakFromConfig(fileModel!, request)
    : BuildMemoryLeakFromCli(request);

ReferenceChainOptions referenceChain = usedConfigFile
    ? BuildReferenceChainFromConfig(fileModel!, request)
    : BuildReferenceChainFromCli(request);

EventLeakOptions eventLeak = usedConfigFile
    ? BuildEventLeakFromConfig(fileModel!, request)
    : BuildEventLeakFromCli(request);
// ... repeated 6 times
```

#### How to fix
A single helper reduces the pattern to one expression per option:
```csharp
private T Resolve<T>(
    bool fromFile,
    Func<CliConfigurationFileModel, AnalysisCommandRequest, T> fromConfig,
    Func<AnalysisCommandRequest, T> fromCli,
    CliConfigurationFileModel? fileModel,
    AnalysisCommandRequest request)
    => fromFile ? fromConfig(fileModel!, request) : fromCli(request);

// Usage:
MemoryLeakOptions memoryLeak     = Resolve(usedConfigFile, BuildMemoryLeakFromConfig,     BuildMemoryLeakFromCli,     fileModel, request);
ReferenceChainOptions refChain   = Resolve(usedConfigFile, BuildReferenceChainFromConfig, BuildReferenceChainFromCli, fileModel, request);
EventLeakOptions eventLeak       = Resolve(usedConfigFile, BuildEventLeakFromConfig,      BuildEventLeakFromCli,      fileModel, request);
```

This is a readability improvement rather than a behavior change. The logic is unchanged; the pattern is just expressed once.

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

### MINOR-14 — `SingleDumpPipelineState` stage comment gap

**File:** `src/DumpDetective.Cli/Pipeline/SingleDumpPipelineState.cs`

#### What
The state bag uses region-style comments to document which stage owns each property block. Stage 4 (`GenerateFindingsStage`) has no comment, creating a gap:

```csharp
// ── Stage 3: RunAnalyzersPipelineStage ──────────────────────────────────────
public IReadOnlyList<AnalyzerRunResult> Runs { get; set; } = [];

// ── Stage 5: BuildReportStage ────────────────────────────────────────────    ← gap: Stage 4 missing
public string RenderedReport { get; set; } = string.Empty;
```

#### How to fix
`GenerateFindingsStage` does not add new properties to the state — it transforms `Runs` in place. Add a comment acknowledging this:

```csharp
// ── Stage 3: RunAnalyzersPipelineStage ──────────────────────────────────────
public IReadOnlyList<AnalyzerRunResult> Runs { get; set; } = [];
public TimeSpan AnalysisElapsed { get; set; }

// ── Stage 4: GenerateFindingsStage ──────────────────────────────────────────
// Enriches Runs in-place with InsightFinding lists; no new properties required.

// ── Stage 5: BuildReportStage ────────────────────────────────────────────────
public string RenderedReport { get; set; } = string.Empty;
```

---

### MINOR-15 — `ClrReferenceProvider` and `LazyReferenceGraph` are redundant

**Files:** `Core/Abstractions/ClrReferenceProvider.cs`, `Analysis/Traversal/LazyReferenceGraph.cs`

#### What
Both classes do the same thing — enumerate `ClrObject.EnumerateReferences()` for a given address. `ClrReferenceProvider` is the non-caching version in `Core`; `LazyReferenceGraph` is the caching version in `Analysis`.

```csharp
// ClrReferenceProvider.cs (Core)
public IEnumerable<ulong> GetReferences(ulong obj)
{
    var clrObj = _heap.GetObject(obj);
    foreach (var child in clrObj.EnumerateReferences(carefully: true))
        yield return child.Address;
}

// LazyReferenceGraph.cs (Analysis)
public IReadOnlyList<ulong> GetReferences(ulong address)
{
    // same logic + Dictionary<ulong, ulong[]> cache
}
```

#### Why it's a problem
The `IReferenceProvider` interface exists in `Core` but `LazyReferenceGraph` does not implement it — it implements its own ad-hoc API. So the interface and the actual production implementation are disconnected.

#### How to fix
Have `LazyReferenceGraph` implement `IReferenceProvider`:
```csharp
internal sealed class LazyReferenceGraph(ClrHeap heap) : IReferenceProvider
{
    // IReadOnlyList<ulong> → IEnumerable<ulong> (interface-compatible)
    IEnumerable<ulong> IReferenceProvider.GetReferences(ulong address) => GetReferences(address);

    public IReadOnlyList<ulong> GetReferences(ulong address) { ... }
}
```

`ClrReferenceProvider` can then be removed or kept only as a lightweight non-caching fallback for test scenarios.

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
| CRITICAL-03 | Add diagnostics to `FindingGenerationPipeline` catch block | `Reporting/Pipeline/FindingGenerationPipeline.cs`, `Core/Models/AnalyzerRunResult.cs` | XS |
| MINOR-14 | Fix `SingleDumpPipelineState` stage 4 comment gap | `Cli/Pipeline/SingleDumpPipelineState.cs` | XS |
| MAJOR-07 | Rename `Analysis.Pipeline.AnalysisContext` → `RuntimeAnalysisContext` | 1 rename + 4 usages | S |
| ~~CRITICAL-02~~ | ~~Inject `IEnumerable<IAnalyzerTrendComparer>` into `TrendAnalyzer`~~ | ~~`Analysis/Trend/TrendAnalyzer.cs`, `Cli/Hosting/ServiceRegistration.cs`~~ | ✅ **Done** |
| MAJOR-05 | Add `GetOption<T>()` extension method to eliminate magic-key pattern | `Analysis/` (new extensions file), 3 analyzer files | S |

### 🟡 Tier 2 — High Impact, Plan for Next Cycle

| ID | Action | Files to Touch | Effort |
|---|---|---|---|
| ~~CRITICAL-01~~ | ~~Add `Reporting → Analysis` project reference; move domain types out of `Core`~~ | ~~`Reporting.csproj`, `Core/Models/AnalyzerDomainResult.cs`, all `Reporting/FindingGenerators/`~~ | ✅ **Done** |
| MAJOR-06 | Extract `IHeapIndexBuilder` interface; split `HeapCache` state bag property | `Core/Abstractions/` (new file), `Cli/Pipeline/SingleDumpPipelineState.cs`, `BuildHeapIndexStage.cs` | M |
| MINOR-13 | Mark `MemoryLeakAnalyzer`, `ReferenceChainAnalyzer` as `internal` | 2 files | XS |
| MINOR-11 | Add explicit `Category` override to each analyzer; keep `Infer()` as fallback only | 16 analyzer files (2-line change each) | S |

### 🟢 Tier 3 — Quality / Maintainability

| ID | Action | Files to Touch | Effort |
|---|---|---|---|
| MINOR-09 | Change `GenerateAsync` to sync `Generate` or `ValueTask` | `Reporting/Pipeline/FindingGenerationPipeline.cs`, `Cli/Pipeline/Stages/GenerateFindingsStage.cs` | XS |
| MINOR-10 | Delegate `EnumerateIndexedEntriesAsTuples` to `EnumerateIndexedEntries` | `Analysis/Cache/HeapAnalysisCache.cs` | XS |
| MINOR-12 | Extract `Resolve<T>()` helper in `ConfigurationResolver` | `Cli/Services/ConfigurationResolver.cs` | XS |
| MINOR-15 | Have `LazyReferenceGraph` implement `IReferenceProvider`; retire `ClrReferenceProvider` | `Analysis/Traversal/LazyReferenceGraph.cs`, `Core/Abstractions/ClrReferenceProvider.cs` | S |
| MINOR-08 | Document cache eviction strategy in `LazyReferenceGraph` (or implement partial eviction) | `Analysis/Traversal/LazyReferenceGraph.cs` | XS |
| MAJOR-04 | Decompose `DumpAnalysisService` into focused service classes | `Cli/Services/` (new files) | L |

---

**Effort key:** XS = <30 min · S = 30–90 min · M = half-day · L = 1–2 days

> **Note on MAJOR-04:** This is the remaining high-risk refactor. Decompose `DumpAnalysisService` with the test suite green before and after each extraction step.

> ~~**Note on CRITICAL-01:** This was a project reference change — run a full build and all tests immediately after moving each domain result type.~~ **CRITICAL-01 is complete. Build passed clean.**
