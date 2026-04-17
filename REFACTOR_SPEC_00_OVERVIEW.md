# DumpDetective — Refactor Master Spec (Overview)

> **How to use these specs with an LLM**
> Feed the overview first, then each numbered spec in order. Each spec is self-contained
> but references types defined in earlier specs. Do not skip specs — later ones assume
> the structures from earlier ones exist.

---

## 1. Spec Files Index

| File | Scope |
|---|---|
| `REFACTOR_SPEC_00_OVERVIEW.md` | This file — goals, phases, what to keep vs change |
| `REFACTOR_SPEC_01_SOLUTION_STRUCTURE.md` | Multi-project layout, namespaces, file-to-project mapping |
| `REFACTOR_SPEC_02_CONFIGURATION_AND_CLI.md` | `System.CommandLine` + `IOptions<T>` per-concern options |
| `REFACTOR_SPEC_03_CORE_CONTRACTS.md` | Async `IAnalyzer`, enriched `AnalysisContext`, `IAnalyzerReporter` registry |
| `REFACTOR_SPEC_04_SERVICES_AND_DI.md` | `IHostBuilder`, DI registrations, service refactors |
| `REFACTOR_SPEC_05_REPORTING.md` | `IReportFormatter` hierarchy, trend composer, report pipeline |
| `REFACTOR_SPEC_06_TESTS.md` | xUnit test project — what to test, structure, example skeletons |

---

## 2. Goals

1. **Enforce layering** — no circular dependencies between projects.
2. **Eliminate manual wiring** — DI owns construction; `new` only in test fakes or leaf value objects.
3. **Single source of truth for CLI** — `System.CommandLine` definitions replace both `FromCommandLineArgs` and `PrintUsage`.
4. **Testability** — all pure-logic classes have unit tests with zero ClrMD dependency.
5. **Extensibility** — adding a new analyzer + reporter requires touching zero existing files.
6. **Preserve all existing functionality** exactly — this is a structural refactor, not a feature change.

---

## 3. What to Keep Unchanged (Verbatim Carry-over)

The following are well-designed today and should be copied into the new project structure
**without modification** to their logic:

| Asset | Target Project |
|---|---|
| All `AnalyzerDomainResult` record hierarchy | `DumpDetective.Core` |
| `InsightFinding` record + `FindingSeverity` enum | `DumpDetective.Core` |
| `AnalysisSnapshot` record | `DumpDetective.Core` |
| `FindingFingerprint` | `DumpDetective.Core` |
| `FindingTagger` | `DumpDetective.Core` |
| `TrendAnalyzer` + all `IAnalyzerTrendComparer` implementations | `DumpDetective.Analysis` |
| All `*Analyzer` implementations (body logic only) | `DumpDetective.Analysis` |
| `AnalysisPipeline` (body logic, adapted to async) | `DumpDetective.Analysis` |
| `HeapAnalysisCache` | `DumpDetective.Analysis` |
| All `*Printer` / `IAnalyzerReporter` implementations (body logic) | `DumpDetective.Reporting` |
| `OutputWriter` | `DumpDetective.Reporting` |
| `FormatHelper`, `StringConstants` | `DumpDetective.Core` |
| `ConsoleUx` | `DumpDetective.Cli` |

---

## 4. What Changes and Why

| Today | After Refactor | Why |
|---|---|---|
| `AnalysisConfiguration.FromCommandLineArgs` (~250 lines) | `System.CommandLine` root command + binder | Single source of truth; auto `--help`; typed binding |
| Single 15-property `AnalysisConfiguration` class | 5 strongly-typed options classes + `IOptions<T>` | Per-concern isolation; independently testable |
| `MutableSettings` workaround | Eliminated — `System.CommandLine` binder fills options directly | No longer needed |
| `DumpAnalysisService` news up all dependencies | All dependencies injected via DI | Testable; swappable |
| `TrendAnalyzer` hard-codes comparers in constructor | Comparers registered via DI `IEnumerable<IAnalyzerTrendComparer>` | Open/closed — new comparers auto-discovered |
| `AnalyzerReportRenderer` takes a plain list | Reporters registered via DI `IEnumerable<IAnalyzerReporter>` | Open/closed |
| `ReportFormatter` static partial class | `IReportFormatter` interface + 3 concrete classes | Individually testable; injectable |
| `IAnalyzer.Execute` (sync) | `IAnalyzer.ExecuteAsync(AnalysisContext, CancellationToken)` | Supports timeout/cancel; future parallelism |
| `AnalysisContext` has 3 properties | `AnalysisContext` enriched with options + `IProgress<T>` + `CancellationToken` | Single seam between infra and analysis |
| No test project (only benchmarks) | `DumpDetective.Tests` (xUnit) | Pure logic classes are trivially testable |
| Single `.csproj` | 5-project solution | Enforced dependency boundaries |

---

## 5. Dependency Graph (enforced — no reverse arrows allowed)

```
DumpDetective.Core
       ↑
DumpDetective.Analysis
       ↑
DumpDetective.Reporting
       ↑
DumpDetective.Cli  (entry point — references all)

DumpDetective.Tests → Core, Analysis, Reporting (no Cli)
```

`DumpDetective.Core` has **zero** project references — only BCL and `Microsoft.Diagnostics.Runtime`.

---

## 6. Migration Phases

Execute phases in order. Each phase ends with a green build.

### Phase 0 — Repo Preparation
- Create solution file referencing all 5 projects.
- Move existing code into the correct project (copy first, verify build, then delete from old location).
- Keep `DumpDetective` (original project) as the CLI project renamed to `DumpDetective.Cli`.

### Phase 1 — Core Models (Spec 01 + Spec 03 models section)
- Create `DumpDetective.Core` project.
- Move models, interfaces, `FindingTagger`, `FormatHelper`, `StringConstants`.
- No logic changes in this phase.

### Phase 2 — Analysis Layer (Spec 03 analyzers section)
- Create `DumpDetective.Analysis` project.
- Move all analyzers, pipeline, cache, trend analyzer.
- Change `IAnalyzer.Execute` → `IAnalyzer.ExecuteAsync`.
- Enrich `AnalysisContext` (add options + cancellation).

### Phase 3 — Reporting Layer (Spec 05)
- Create `DumpDetective.Reporting` project.
- Move all printers, `OutputWriter`, `ReportBuilder`, formatters.
- Replace `ReportFormatter` static partials with `IReportFormatter` implementations.

### Phase 4 — Configuration + CLI (Spec 02)
- Replace `AnalysisConfiguration.FromCommandLineArgs` with `System.CommandLine` binder.
- Replace monolithic config with 5 `IOptions<T>` classes.
- Replace `PrintUsage` with auto-generated help.

### Phase 5 — DI + Host (Spec 04)
- Wire `IHostBuilder` + `Microsoft.Extensions.DependencyInjection`.
- Register all analyzers, reporters, comparers, formatters via DI.
- Eliminate all manual `new` in service classes.

### Phase 6 — Tests (Spec 06)
- Create `DumpDetective.Tests` xUnit project.
- Add tests for all pure logic classes.

---

## 7. NuGet Packages to Add

| Package | Version | Project |
|---|---|---|
| `System.CommandLine` | 2.0.0-beta4.* (latest stable beta) | `DumpDetective.Cli` |
| `Microsoft.Extensions.Hosting` | 10.x | `DumpDetective.Cli` |
| `Microsoft.Extensions.DependencyInjection` | 10.x | `DumpDetective.Cli` |
| `Microsoft.Extensions.Options` | 10.x | `DumpDetective.Core` |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 10.x | `DumpDetective.Cli` |
| `xunit` | latest | `DumpDetective.Tests` |
| `xunit.runner.visualstudio` | latest | `DumpDetective.Tests` |
| `Microsoft.NET.Test.Sdk` | latest | `DumpDetective.Tests` |
| `FluentAssertions` | latest | `DumpDetective.Tests` |

All other packages (`Microsoft.Diagnostics.Runtime`, etc.) are existing — just redistribute
to the correct projects.

---

## 8. LLM Prompt Template

Use this template when feeding a spec file to an LLM:

```
Context: I am refactoring a .NET 10 C# 14 CLI tool called DumpDetective.
The tool analyzes Windows memory dump files using ClrMD.

I have already completed phases [X, Y].
The following types exist in project DumpDetective.Core: [list key types].

Now implement: [paste spec section].

Rules:
- Use file-scoped namespaces.
- All new interfaces go in DumpDetective.Core unless the spec says otherwise.
- Do not change existing method signatures unless the spec explicitly lists the new signature.
- Do not add comments unless the surrounding code already has comments.
- Prefer init-only properties and records for data types.
- Target net10.0, LangVersion 14.
```
