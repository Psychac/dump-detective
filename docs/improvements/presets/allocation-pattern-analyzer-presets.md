**Allocation Pattern Analyzer — Presets (current state)**

- **Fast:** `TopTypeLimit = 10`, `ScanMultiplier = 2`
- **Balanced (default):** `TopTypeLimit = 20`, `ScanMultiplier = 2`
- **Full:** `TopTypeLimit = 50`, `ScanMultiplier = 2`

Notes: these mappings reflect the values defined by `AllocationPatternAnalysisOptions.Preset(AnalysisProfile)` in code.

# AllocationPatternAnalyzer — Preset Design & Current Behavior

Purpose: read `HeapIndexBuildResult.TypeAggregates` (Phase‑2 only), compute generation and LOH percentages, derive a GC pressure score, and produce top-type lists for short/transient and long-lived candidates.

Where to look in the repo:
- Analyzer: `src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs`
- Section builder: `src/DumpDetective.Reporting/SectionBuilders/AllocationPatternSectionBuilder.cs`

What the analyzer returns

- Percentages are rounded to two decimals for clarity (rendered as `F2`).
- Domain result contains three per-type lists: `TopTransientTypes`, `TopShortishTypes`, and `TopLongLivedTypes`.

Selection knobs and units

- `TopTypeLimit` (int): how many entries to include per result list.
- `ScanMultiplier` (int): how many top entries to scan. Effective candidate scan limit = `Min(sorted.Count, TopTypeLimit * ScanMultiplier)`.
- `LongLivedSelectionThreshold` (ratio 0.0–1.0): select a type into long-lived results when `(Gen2 + LOH)/Count > LongLivedSelectionThreshold` (default 0.3).
- `LongLivedClassificationThreshold` (ratio 0.0–1.0): used to classify `AllocationProfile.Retained` (default 0.5).
- `TransientClassificationThreshold` (percent 0–100): a type is classified `Transient` when `Gen0% > TransientClassificationThreshold` (default 70.0).
- `ShortLivedSelectionThreshold` (percent 0–100): types with `Gen0% >= ShortLivedSelectionThreshold` (and not long-lived) are eligible for the short-ish table (default 25.0).

Note: long-lived thresholds are ratios (0..1) while transient/short thresholds are percentages (0..100).

Rendering behavior

- The section builder emits separate tables for transient, short-ish, and long-lived lists only when the corresponding lists are non-empty.
- Table truncation: `TopTypeRows = 15` in the section builder (display limit per table).
- Formatting: generation and size percentages use two decimals (`F2`); long-lived ratio is shown as percent (`P2`).

Troubleshooting guidance

- If your Balanced report omits transient/short-ish tables, no types met the configured thresholds. Remedies:
  - Lower `ShortLivedSelectionThreshold` (e.g. from 25.0 → 10.0).
  - Increase `TopTypeLimit` (Balanced → Full) to examine more candidates.
  - Increase `ScanMultiplier` to widen the candidate window scanned.

Code references

- Analyzer selection and thresholds: `AllocationPatternAnalyzer.Analyze`.
- Render and truncation: `AllocationPatternSectionBuilder.Build` (`TopTypeRows = 15`).

Optional suggestion

- Consider unifying threshold units (all ratios or all percentages) to reduce confusion. This is purely a UX/doc improvement but will require small changes and test updates.

---

Which next step do you want me to take? I can implement the options change and update the `Preset` factory first.
