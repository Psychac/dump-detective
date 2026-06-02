# EventLeakAnalyzer — Presets

Purpose: identify event/delegate retention and listener leaks. This document makes preset-driven behavior explicit (numeric caps + flow choices).

**Where to look**: - `DumpDetective/Analyzers/EventLeakAnalyzer.cs` - `src/DumpDetective.Reporting/SectionBuilders/EventLeakSectionBuilder.cs`

## Current working (summary)
- `EventLeakAnalyzer` scans delegate-backed fields (static and instance) and accumulates `EventLeakInfo` entries. It prefers index-backed enumeration when a `HeapIndex` exists and falls back to per-segment `ClrHeap` walks otherwise.
- Observed options in `EventLeakOptions`: `MinSubscribers`, `IncludeNonLeakingEvents`, `TopSubscriberTypesToShow`, `TopDetailedInstancesPerGroup`, `EnableLowIncomingRefsCheck`, `EnableDiagnostics`, plus internal severity thresholds and presentation caps.
- `EventLeakSectionBuilder` currently renders top publisher events, grouped leak details, and top leak instances. Presentation currently uses `MaxGroupsToShow` / `MaxInstancesToShow` constants.

## Goals for preset-driven flow
- Presets should govern both numeric caps (Top-N, min-subscriber thresholds) and behavioral choices (scan static fields sweep, low-incoming-ref checks, index-only vs fallback).
- Analyzer-level explicit config fields must still be able to override preset defaults.
- `Balanced` should match current default behavior.

## Suggested new options (small additions)
- `bool PreferIndexOnly` — prefer index-backed enumeration and skip fallback heap walks when true (emit diagnostic if index not present).
- `bool ProduceRawExports` — write detailed per-instance NDJSON/GZ exports when enabled and emit `ReportArtifact(FilePath)`.
- `int TopPublisherEventsToShow` — report width for top publisher events (map to renderer instead of hard-coded constant).
- `int SeveritySubscriberThreshold` — used to tune severity scoring and when to escalate findings.

These additions make intent explicit and allow presets to change analyzer control flow in a testable way.

## How analyzer flow should respect presets
- If `PreferIndexOnly == true` and no index available => skip expensive heap enumeration and emit a diagnostic stating heap index required.
- `IncludeNonLeakingEvents == true` => sweep static fields and include non-leaking events in the graph (expensive on large heaps).
- `EnableLowIncomingRefsCheck == true` => enable expensive per-subscriber incoming-ref verification (only for targeted investigations).
- `ProduceRawExports == true` => stream per-instance NDJSON exports to a gz file and attach as `ReportArtifact(FilePath)` for `WriteOutputStage` to move into `artifacts/`.

## Algorithmic preset policy (logical behaviors)
Presets should toggle observable code paths (index-only vs fallback scan, static-sweep on/off, low-incoming-refs check) rather than only changing numbers. Use enums/flags to make behavior explicit and unit-testable.

Concrete preset mappings (recommended)

- Fast
	- `MinSubscribers = 3`
	- `IncludeNonLeakingEvents = false`
	- `TopSubscriberTypesToShow = 3`
	- `TopDetailedInstancesPerGroup = 3`
	- `EnableLowIncomingRefsCheck = false`
	- `EnableDiagnostics = false`
	- `PreferIndexOnly = true`
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `MinSubscribers = 0`
	- `IncludeNonLeakingEvents = false`
	- `TopSubscriberTypesToShow = 5`
	- `TopDetailedInstancesPerGroup = 5`
	- `EnableLowIncomingRefsCheck = false`
	- `EnableDiagnostics = true`
	- `PreferIndexOnly = false`
	- `ProduceRawExports = false`

- Full
	- `MinSubscribers = 0`
	- `IncludeNonLeakingEvents = true`
	- `TopSubscriberTypesToShow = 20`
	- `TopDetailedInstancesPerGroup = 20`
	- `EnableLowIncomingRefsCheck = true` (use with caution)
	- `EnableDiagnostics = true`
	- `PreferIndexOnly = false`
	- `ProduceRawExports = true`

These mappings escalate cost and coverage from Fast → Balanced → Full.

## Minimal code changes (implementation plan)
1. Add `PreferIndexOnly`, `ProduceRawExports`, and `TopPublisherEventsToShow` to `EventLeakOptions` and update `Preset(AnalysisProfile)` to populate them.
2. Respect `PreferIndexOnly` in `FindEventLeaks(...)` / `EnumerateEventEntries(...)` by short-circuiting fallback heap scans when the option is true, and emit a diagnostic.
3. When `ProduceRawExports` is enabled, stream detailed event/subscriber snapshots into a gzipped NDJSON artifact and emit `ReportArtifact(FilePath)`.
4. Update `EventLeakSectionBuilder` to use `TopPublisherEventsToShow` / `TopDetailedInstancesPerGroup` when rendering instead of hard-coded `MaxGroupsToShow` / `MaxInstancesToShow` (optional but recommended).
5. Add unit tests for index-only vs fallback behavior, `IncludeNonLeakingEvents` static-sweep toggle, and artifact emission.

Implementation notes:
- `EventLeakAnalyzer` already uses `heapCache.TryGetHeapIndex(out var heapIdx)` to select the fast path; wire `PreferIndexOnly` around that check rather than duplicating logic.
- `SweepModuleStaticFields(...)` provides the static field sweep path — guard this call with `IncludeNonLeakingEvents`.

## Tests and validation
- Unit tests: mock `HeapAnalysisCache.TryGetHeapIndex(...)` to assert behavior when `PreferIndexOnly` is toggled.
- Unit tests: verify `CreateLeakInfo(...)` and `AddToAccumulator(...)` behaviors under different `MinSubscribers`/severity thresholds.
- Integration: run `EventLeakAnalyzer` on representative dumps with `Fast`/`Balanced`/`Full` presets and measure runtime and produced artifact presence.

## Next steps I can take
- Implement the `EventLeakOptions` additions and update `EventLeakOptions.Preset(...)` to set the above mappings.
- Replace hard-coded presentation constants in `EventLeakSectionBuilder` with options-driven values.
- Add unit tests that mock heap index presence and verify `PreferIndexOnly` short-circuits correctly.

Which next step do you want me to take? I can implement the options change and update the presets mapping first.
