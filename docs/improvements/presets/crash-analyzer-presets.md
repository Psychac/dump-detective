

# CrashAnalyzer — Preset Design

Purpose: surface exceptions, stack traces and instance context for root-cause analysis while bounding noise and cost.

Where to look in the repo:
- Analyzer: `DumpDetective/Analyzers/CrashAnalyzer.cs`
- Reporting: `docs/ReportStructure/ReportingRefactorPlan.md` references `CrashSectionBuilder` and `CrashPrinter`.

Observed implementation details:
- `CrashAnalyzer` currently uses hard-coded constants for caps and sampling (see `MaxExceptionsPerType`, `TopDetailedExceptionInstances`, `MaxOriginalStackFramesToPrint` in `CrashAnalyzer.cs`).
- It builds an active-exception lookup from `ClrRuntime.Threads` (`thread.CurrentException`) and then scans `ClrHeap.EnumerateObjects()` for exception objects.
- The analyzer prefers aggregation and caps per-type stored instances (`MaxExceptionsPerType`) and selects `TopDetailedExceptionInstances` globally.

Preset levers to introduce (recommended):
- `TopDetailedExceptionInstances`, `MaxOriginalStackFramesToPrint`, `MaxCurrentThreadFramesToPrint`, `IncludeInnerExceptionChains` (enum shallow/moderate/deep), `AggregateSimilarExceptions`, `ProduceRawExports`.

How analyzer flow should respect presets:
- Fast: keep aggregation, shallow stack capture, small per-type instance caps.
- Balanced: larger caps and moderate stack depth for representative instances.
- Full: increase per-type caps, capture deeper original stacks (`MaxOriginalStackFramesToPrint`), and enable raw exports of detailed exception instances.

Minimal code changes (concrete):
- Replace internal constants in `DumpDetective/Analyzers/CrashAnalyzer.cs` with `CrashAnalysisOptions` fields and implement `Preset(AnalysisProfile)`.
- Update `CrashSectionBuilder` to render whether results were sampled and include artifact links when `ProduceRawExports` is enabled.

Tests and validation:
- Unit: synthetic `ExceptionInstance` collections to assert capping/aggregation.
- Integration: a dump with many exception objects to validate runtime and output delta between `Fast`/`Balanced`/`Full`.

Rationale — when to pick each preset:
- **Fast:** small per-type caps and shallow frame capture keep output small and avoid expensive stack-resolve I/O.
- **Balanced:** default caps and moderate stack depths provide representative exception contexts with reasonable I/O.
- **Full:** increase per-type instance caps and deeper original stack capture to preserve more detail for forensic analysis.

Minimal code changes (concrete):
- Replace internal constants in `DumpDetective/Analyzers/CrashAnalyzer.cs` with `CrashAnalysisOptions` fields and use `CrashAnalysisOptions.Preset(AnalysisProfile)` to drive Fast/Balanced/Full.

Next steps:
- I can convert the current constants in `CrashAnalyzer.cs` to options and add a `CrashAnalysisOptions.Preset(...)` implementation; confirm and I'll proceed.

