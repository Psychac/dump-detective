# SegmentReservationAnalyzer — Preset Design

Purpose: assess committed vs reserved bytes and address-space pressure (§25.1–25.3) without scanning heap objects.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/SegmentReservationAnalyzer.cs

Observed implementation details:
- Operates on `ClrHeap.Segments` only — reads `ClrSegment.CommittedMemory` and `ClrSegment.ReservedMemory`.
- Classifies ephemeral segments and computes `ReservedByLogicalHeap` for Server GC via `ClrSubHeap.Index`.
- Address-space pressure heuristics use `SegmentReservationAnalysisOptions.ThirtyTwoBitPressureThresholdBytes` and `RatioHighPressureThreshold`.

Preset knobs to expose:
- `IncludeSegmentTable` (bool) — whether to include per-segment rows in the domain result.
- `ReportReservedByHeap` (bool) — include `ReservedByLogicalHeap` breakdown.
- `ThirtyTwoBitPressureThresholdBytes` and `RatioHighPressureThreshold` (tunable thresholds).


Built-in presets (from `SegmentReservationAnalysisOptions.Preset`):
- **Fast:** `ThirtyTwoBitPressureThresholdBytes = 2_000_000_000` (2 GB), `RatioHighPressureThreshold = 12.0`.
- **Balanced:** defaults (`ThirtyTwoBitPressureThresholdBytes = 1_500_000_000` ≈1.5 GB, `RatioHighPressureThreshold = 10.0`).
- **Full:** `ThirtyTwoBitPressureThresholdBytes = 1_000_000_000` (1 GB), `RatioHighPressureThreshold = 8.0`.

Rationale:
- **Fast:** raise thresholds to avoid noisy pressure alerts on large-address-space processes; use for quick triage.
- **Balanced:** moderate thresholds suitable for most analyses and aligned to the codebase defaults.
- **Full:** lower thresholds to surface more address-space pressure signals for thorough forensic checks.

Minimal code changes:
- Add `Preset(AnalysisProfile)` for `SegmentReservationAnalysisOptions` and expose `IncludeSegmentTable`/`ReportReservedByHeap` fields.
- Ensure `SegmentReservationAnalyzer` respects `IncludeSegmentTable` to avoid building large `SegmentTable` lists on fast runs.

Tests and validation:
- Unit: build `ClrSegment` fakes to assert `ReservationGapBytes`, ratio detection and ephemeral fill calculations.
- Integration: run analyzer on medium/large dumps and assert pressure signal flips as thresholds change.

Next steps:
- I can implement `SegmentReservationAnalysisOptions.Preset(...)` and a small unit test harness — say if you want that patched now.
