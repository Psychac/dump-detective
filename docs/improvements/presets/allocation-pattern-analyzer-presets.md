**Allocation Pattern Analyzer — Presets**

- **Fast:** `TopTypeLimit`: 10
- **Balanced (default):** `TopTypeLimit`: 20
- **Full:** `TopTypeLimit`: 50

Notes: values derived from `AllocationPatternAnalysisOptions.Preset(AnalysisProfile)` and the analyzer's `TopTypeLimit` usage.

# AllocationPatternAnalyzer — Preset Design

Purpose: analyze `TypeAggregates` to classify allocation profiles (Transient/Steady/Retained), compute a GC pressure score, and list top short- and long-lived types.

Where to look in the repo:
- Analyzer: [src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs](src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs#L1)
- Section builder: [src/DumpDetective.Reporting/SectionBuilders/AllocationPatternSectionBuilder.cs](src/DumpDetective.Reporting/SectionBuilders/AllocationPatternSectionBuilder.cs#L1)

Current working (summary)

- `AllocationPatternAnalyzer` is a pure Phase‑2 post-processor: it reads `HeapIndexBuildResult.TypeAggregates` (no heap enumeration) and computes generation/LOH percentages, a heuristic GC pressure score, and top type lists based on `TopTypeLimit`.
- Key heuristics in the analyzer:
	- `Transient` if type Gen0% > 70%
	- `Retained` if type long‑lived ratio (Gen2+LOH)/Count > 0.5
	- Analyzer-level profile classification: `Transient`/`Retained`/`Steady`/`Mixed` (see `ClassifyProfile`).
	- Pressure score = `(gen0CountPct * 0.3) + (gen2CountPct * 0.5) + (lohSizePct * 0.2)` and mapped to `GCPressureLevel` by cutoffs.

Summary of recent implementations

- Analyzer computes both count% and estimated size% (calls `AnalyzerHelpers.ComputeApproxGenBytes`) and returns `AllocationPatternDomainResult` including `TopShortLivedTypes` and `TopLongLivedTypes`.
- `AllocationPatternSectionBuilder` renders summary blocks, generation distribution table, pressure signal text and top-type tables using `TopTypeRows = 15` for table truncation and `FormatHelper.TruncateString` for display.

Goals for preset-driven flow

- Keep presets responsible for numeric knobs that affect analysis cost and output size (notably `TopTypeLimit`).
- Preserve analyzer's streaming/phase‑2 behavior — presets should not trigger additional heap scans.
- Allow teams to tune how many candidate types are returned for quick triage (`Fast`) vs deep postmortem (`Full`).

Suggested new options

- `int TopTypeLimit` — how many types to return for each short/long-lived list (existing knob; primary preset target).
- `int ScanMultiplier` (optional) — how many top entries to scan (currently: `TopTypeLimit * 2`), expose to tune scanning breadth.
- `double LongLivedThreshold` (optional) — threshold used to consider a type as long‑lived when `(Gen2 + LOH)/Count` > threshold (default 0.3 used in selection, 0.5 for classification); exposing it allows sensitivity tuning.

How analyzer flow should respect presets

- `TopTypeLimit` controls both the capacity of result lists and the `scanLimit = Min(sorted.Count, TopTypeLimit * 2)` used for candidate selection.
- `LongLivedThreshold` (if added) should govern both selection for `longLived` vs `shortLived` buckets and the `typeProfile` classification branch to keep behavior consistent.

Concrete preset mappings (recommended)

- Fast
	- `TopTypeLimit = 10`
	- `ScanMultiplier = 2` (implicit; `scanLimit = TopTypeLimit * 2`)
	- Rationale: minimal candidate set for quick triage on very large TypeAggregates.

- Balanced (baseline / existing defaults)
	- `TopTypeLimit = 20`
	- `ScanMultiplier = 2`
	- Rationale: default behavior in repo; balances coverage vs output size.

- Full
	- `TopTypeLimit = 50`
	- `ScanMultiplier = 2`
	- Rationale: broad candidate set for deep investigations.

Minimal code changes (implementation plan)

1. Verify `AllocationPatternAnalysisOptions` exposes `TopTypeLimit` and `Preset(AnalysisProfile)` sets it appropriately (already present per docs).
2. (Optional) Add `ScanMultiplier` and `LongLivedThreshold` to `AllocationPatternAnalysisOptions` and update `Preset(...)` mappings.
3. Update `AllocationPatternAnalyzer` to read optional `ScanMultiplier`/`LongLivedThreshold` and use them in candidate selection and classification: replace hardcoded `options.TopTypeLimit * 2` and `longLivedRatio > 0.3` with configured values.
4. Update unit tests to cover preset-driven behavior and edge cases.

Tests and validation

- Unit tests: synthesize `TypeAggregateIndexEntry` maps with varied gen counts and LOH to validate:
	- global count%/size% calculations;
	- `ClassifyProfile` behaviour for boundary gen0/gen2 ratios;
	- top-N selection respects `TopTypeLimit` and `ScanMultiplier`.
- Integration: run the analyzer in the pipeline after `GCGenerationAnalyzer` on a representative heap index to confirm stable outputs across presets.

Next steps I can take

- Implement the small optional options (`ScanMultiplier`, `LongLivedThreshold`) and wire them into `AllocationPatternAnalyzer` and `Preset(...)`.
- Add unit tests for preset behavior and update docs.

Which next step do you want me to take? I can implement the options change and update the `Preset` factory first.
