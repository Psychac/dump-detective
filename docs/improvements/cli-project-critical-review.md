# DumpDetective.Cli Critical Review

## Current State
All major structural issues resolved. `src/DumpDetective.Cli` is clean: orchestration moved to `Execution/`, dead code deleted, `Configuration/` unambiguous and single, `Services/` narrowed to `ExitCodes` + `StartupValidator`. No known dead code or parallel-list registration risk remains.

## Findings

### 1. `TrendOrchestrationService` has large, concrete execution methods
`ExecuteAsync` (185 lines) and `ExecutePipelineForDumpAsync` (100 lines) inline pipeline construction and per-dump coordination; hard to test in isolation. `SingleDumpOrchestrationService` already got the presenter/factory split (83 lines now); trend path needs the same.

### 2. `AnalyzerExecutionService` mixes multiple responsibilities  
Option adaptation, runtime context assembly, diagnostics plumbing, and execution dispatch all in one class. Split into: context builder, execution dispatcher, option adapter.

### 3. Orchestration services lack happy-path tests
Both `SingleDumpOrchestrationServiceTests` and `TrendOrchestrationServiceTests` test only dump-load failures. No tests cover the success path (stage execution, report assembly, output writing). Blocker for refactoring Findings 1–2 safely.

## Opportunities

### Extract trend per-dump execution (blocks on #3)
Pull `ExecutePipelineForDumpAsync`'s logic into a dedicated coordinator matching the `SingleDumpStageFactory` pattern. Leaves `ExecuteAsync` as a thin iteration loop.

### Add happy-path orchestration tests (do first)
Stub `IDumpLoader`, analyzer execution, report writer. Assert full success path completes and produces expected report/output calls.

### Split `AnalyzerExecutionService` (lower priority)
Extract context builder, execution dispatcher, option adapter into separate types.

## Structure

| Folder | Purpose | Status |
|--------|---------|--------|
| `Program`, `Commands/` | Entry surface | ✓ Clean |
| `Hosting/` | DI bootstrap only | ✓ Clean |
| `Configuration/` | Config parsing/binding | ✓ Single, unambiguous |
| `Execution/` | Orchestration, adapters, presenters | ⚠ Trend presenter split done; execution logic remains |
| `Services/` | `StartupValidator`, `ExitCodes` | ✓ Minimal |
| `Console/`, `Output/`, `Diagnostics/` | UX/output | ✓ Clean |

## Do Not
- Refactor Findings 1–2 without adding happy-path tests first (Finding 3).
- Reintroduce a second `Configuration/` namespace.
- Move hot-path analysis internals into CLI layers.
