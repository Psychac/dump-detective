# CrashAnalyzer — Preset Design

Purpose: surface exceptions, stack traces and instance context for root-cause analysis while bounding noise and cost. This document follows the `String Analyzer — Preset Design` style, making preset-driven behavior explicit (caps + flow choices).

**Where to look**: - [DumpDetective/Analyzers/CrashAnalyzer.cs](DumpDetective/Analyzers/CrashAnalyzer.cs) - [src/DumpDetective.Reporting/SectionBuilders/CrashSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/CrashSectionBuilder.cs)

## Current working (summary)
- `CrashAnalyzer` enumerates exception objects using a unified scan (index-backed when a `HeapIndex` exists, else per-segment `ClrHeap` walks) and builds an `ExceptionAnalysis` plus candidate crash-thread snapshots.
- The analyzer currently uses several internal caps (e.g., per-type caps, per-instance caps, frame-depth constants) and aggregates similar exception types to avoid explosion of payload size.
- `CrashSectionBuilder` renders top exception types, likely crash threads, and detailed exception instances (capped). It currently uses a few local constants for table widths and Top-N.

## Summary of recent implementations
- The analyzer already supports payload truncation via `_options.IncludeAllTypesInPayload` and `_options.TopExceptionTypesToInclude`.
- Building crash-thread candidates performs inference (original stack trace inference) and can link detailed exception instances into the report.
- Exception stack extraction and normalization logic exists to trim and simplify frames (`NormalizeFrame`, `TakeNormalized`).

## Goals for preset-driven flow
- Make presets control both numeric caps (TopN, per-type caps, frame depth) and behavior (produce raw exports, aggregate vs sample, include inferred traces).
- Preserve ability for explicit config to override presets.
- Keep `Balanced` matching current defaults.

## Suggested new options (add to `CrashAnalysisOptions`)
- `int TopDetailedExceptionInstances` — how many detailed exception entries to include.
- `int MaxExceptionsPerType` — cap stored per-type instances.
- `int MaxOriginalStackFramesToPrint` — original-stack depth per instance.
- `int MaxCurrentThreadFramesToPrint` — current-thread stack frames to show.
- `enum InnerExceptionDepth { None, Shallow, Deep }` — control inner-exception chain capture.
- `bool ProduceRawExports` — emit gzipped NDJSON/JSON artifacts for offline forensic export.
- `bool AggregateSimilarExceptions` — whether to aggressively deduplicate similar instances.
- `bool PreferIndexOnly` — when true, avoid per-segment heap enumeration if index not present and emit diagnostic.

These identifiers make preset intent explicit and testable.

## How analyzer flow should respect presets
- `AggregateSimilarExceptions = true` => sample and aggregate instances by message/type/stack signature rather than storing every instance.
- `PreferIndexOnly = true` => use index path only; if index missing emit a short diagnostic and skip expensive heap enumeration.
- `InnerExceptionDepth` maps to how many inner-exception frames are extracted and included in instance snapshots.
- `ProduceRawExports = true` => write detailed per-instance artifacts to disk and emit `ReportArtifact(FilePath)` for `WriteOutputStage` to move into `artifacts/`.

## Algorithmic preset policy (logical behaviors)
Presets change observable code paths (not just numbers). Prefer enums/flags so tests can assert code-path selection (index-only vs fallback vs full extraction).

Concrete preset mappings (recommended)

- Fast
	- `TopDetailedExceptionInstances = 5`
	- `MaxExceptionsPerType = 5`
	- `MaxOriginalStackFramesToPrint = 8`
	- `MaxCurrentThreadFramesToPrint = 8`
	- `InnerExceptionDepth = Shallow`
	- `AggregateSimilarExceptions = true`
	- `ProduceRawExports = false`
	- `PreferIndexOnly = true`

- Balanced (baseline / existing defaults)
	- `TopDetailedExceptionInstances = 20`
	- `MaxExceptionsPerType = 20`
	- `MaxOriginalStackFramesToPrint = 32`
	- `MaxCurrentThreadFramesToPrint = 16`
	- `InnerExceptionDepth = Shallow`
	- `AggregateSimilarExceptions = false`
	- `ProduceRawExports = false`
	- `PreferIndexOnly = false`

- Full
	- `TopDetailedExceptionInstances = 100`
	- `MaxExceptionsPerType = 200`
	- `MaxOriginalStackFramesToPrint = 256`
	- `MaxCurrentThreadFramesToPrint = 128`
	- `InnerExceptionDepth = Deep`
	- `AggregateSimilarExceptions = false`
	- `ProduceRawExports = true`
	- `PreferIndexOnly = false`

Values escalate coverage/cost from Fast → Balanced → Full.

## Minimal code changes (implementation plan)
1. Add new fields and enums to `CrashAnalysisOptions` and implement `Preset(AnalysisProfile)` mapping the above values.
2. Replace internal constants in `CrashAnalyzer` with option usage (respect `_options` populated from `AnalysisContext`).
3. Wire `PreferIndexOnly` into `AnalyzeExceptions(...)` to short-circuit and produce a diagnostic when index missing and index-only requested.
4. When `ProduceRawExports` is enabled, write per-instance NDJSON gz artifacts and emit `ReportArtifact(FilePath)` so `WriteOutputStage` can move them into the report artifacts folder.
5. Update `CrashSectionBuilder` to render sample/sampling metadata, note when results were truncated, and include artifact links.
6. Add unit tests asserting code-path selection, capping semantics, and artifact emission.

Implementation notes:
- `CrashAnalyzer` already supports payload truncation via `_options.IncludeAllTypesInPayload` and `_options.TopExceptionTypesToInclude` — follow this pattern for new fields.
- Exception stack extraction helpers (`NormalizeFrame`, `TakeNormalized`) should be reused; only the max-frame budgets should come from options.

## Tests and validation
- Unit tests: synthetic `ExceptionInstance` collections to assert capping/aggregation and `InnerExceptionDepth` behavior.
- Unit tests: mock `HeapAnalysisCache.TryGetHeapIndex(...)` to assert `PreferIndexOnly` short-circuits or falls back correctly.
- Integration: run `CrashAnalyzer` on a dump with many exception objects and compare `Fast`/`Balanced`/`Full` outputs and runtime.

## Next steps I can take
- Implement `CrashAnalysisOptions` additions and update `CrashAnalysisOptions.Preset(...)`.
- Replace constants in `CrashAnalyzer.cs` with option lookups and add unit tests.
- Add a small integration acceptance test that validates `ProduceRawExports` artifacts are moved into `artifacts/`.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.

