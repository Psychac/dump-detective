# SegmentAnalyzer — Preset Design

Purpose: classify heap segments (SOH/LOH/POH/Frozen), report per-kind totals and optionally count objects inside segments.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/SegmentAnalyzer.cs
- Section builder: src/DumpDetective.Reporting/SectionBuilders/SegmentSectionBuilder.cs

Observed implementation details:
- Iterates `ClrHeap.Segments` and computes `CommittedBytes` per segment; object counting is gated by `SegmentAnalysisOptions.CountSohObjects`.
- `TopSegments` selection is driven by `SegmentAnalyzerOptions.TopSegmentsCount` and the section builder references `LohCriticalPercentThreshold` and `SpikeDensityMultiplier`.
- SOH object counting is skipped by default (returns sentinel `-1`) to avoid O(10s of millions) object enumerations.

Preset knobs to expose:
- `CountSohObjects` (bool) — whether to enumerate objects in SOH.
- `TopSegmentsCount` (int) — how many segments to include in the Top table.
- `ReportObjectScanInterval` (int) — inner progress/report stride for LOH/POH scans (low priority for presets).

Built-in preset (from `SegmentAnalysisOptions.Preset`):
- **Fast:** `CountSohObjects = false` (avoid expensive SOH object enumerations).
- **Balanced:** `CountSohObjects = false` (default; preserves fast path for large heaps).
- **Full:** `CountSohObjects = true` (count SOH objects exactly; higher CPU/time).

Rationale:
- **Fast/Balanced:** skip SOH enumeration to avoid scanning tens of millions of objects on large processes; still report segment-level committed/reserved totals.
- **Full:** enable exact SOH object counts when precise per-segment instance counts are required and resources permit.

Minimal code changes:
- Ensure `SegmentAnalysisOptions.Preset(...)` sets `CountSohObjects` and `TopSegmentsCount` per profile.
- Add a note in `SegmentSectionBuilder` to surface when SOH counts were skipped (already present) and annotate `TopSegmentsCount` when truncated.

Tests and validation:
- Unit: small synthetic `ClrSegment` arrays to validate `GetCommittedBytes`, `CountObjects` sentinel behavior and top-segment selection.
- Integration: run on a large dump (SOH-heavy) to confirm `CountSohObjects=false` avoids long enumerations.

Next steps:
- I can add `Preset(AnalysisProfile)` wiring for `SegmentAnalysisOptions` and a basic integration check if you want the patch applied now.
