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

References:
- Analyzer: `src/DumpDetective.Analysis/Analyzers/AllocationPatternAnalyzer.cs` — implement explicit option knobs and use them in the selection loop.
- Renderer: `src/DumpDetective.Reporting/SectionBuilders/AllocationPatternSectionBuilder.cs` — rendering should remain data-driven (emit tables only when lists non-empty).


### Algorithmic preset policy (reminder)

- Presets MAY change analyzer control flow only when the change is explicit in code (feature flag / named enum), documented here, and reversible via configuration. Prefer named enums/flags over implicit branching so behavior is observable and testable.

---

Which of the quick recommendations above should I add as a sample config file in the repo (lower threshold, increase scan multiplier, or increase TopTypeLimit)?

## Proposed algorithmic preset options and mappings

Below are concrete option additions to consider adding to `AllocationPatternAnalysisOptions` and how they would be used in `AllocationPatternAnalyzer.cs`. These let presets control algorithmic behavior (sorting, scan strategy, selection flow, emission) in an explicit, testable way while keeping numeric knobs available.

- **`SelectionMode` (enum)** — `TopByCount | TopByGen0Pct | TopBySize | CompositeScore`
  - Purpose: let presets prefer Gen0‑biased or size‑heavy types instead of raw count.
  - Mapping: compute per-entry `Gen0Pct`, `Gen2Ratio`, `LohSizePct`, `CompositeScore` and select comparator by `SelectionMode` before sorting.

- **`ScanStrategy` (enum)** — `TopN | TopNByComparator | FullScan`
  - Purpose: control how far and by what order the analyzer scans candidate entries.
  - Mapping: `TopN` uses current `TopTypeLimit * ScanMultiplier`; `TopNByComparator` sorts by the `SelectionMode` comparator before taking the window; `FullScan` scans all entries but respects `MaxScanItemsAbsolute`.

- **`SelectionPriority` (enum)** — `LongLivedFirst | ClassificationFirst | Mixed`
  - Purpose: make classification vs selection ordering explicit and testable.
  - Mapping: `ClassificationFirst` collects buckets for the whole scan window then trims each bucket to `TopTypeLimit`; `LongLivedFirst` preserves current sequential short/transient/long logic.

- **`EmitFlags` (booleans/flags)** — `EmitTransient`, `EmitShortish`, `EmitLongLived`
  - Purpose: allow presets to disable certain tables (useful for `Fast` preset to reduce work/output).
  - Mapping: analyzer returns empty lists for disabled emissions; section builder already hides empty lists.

- **`CompositeWeights` (struct/doubles)** — `Gen0Weight`, `Gen2Weight`, `LohSizeWeight`
  - Purpose: tune `CompositeScore` used by `SelectionMode=CompositeScore` so presets express different priorities.
  - Mapping: `CompositeScore = Gen0Pct*Gen0Weight + Gen2Ratio*Gen2Weight + LohSizePct*LohSizeWeight`.

- **`MaxScanItemsAbsolute` (int)**
  - Purpose: safety cap to avoid pathological CPU/memory when `FullScan` is enabled on huge type sets.
  - Mapping: apply as upper bound to computed `scanLimit`.

- **Unify threshold units (optional)**
  - Purpose: convert transient/short thresholds to 0..1 ratios or clearly document units to avoid confusion when presets change both percent/ratio knobs.

### Concrete changes in `AllocationPatternAnalyzer.cs`

- Replace the current single sort-by-count with a precomputed list of tuples `(mt, entry, gen0Pct, gen2Ratio, lohSizePct, compositeScore)` and a comparator selected by `SelectionMode`.
- Compute `scanLimit` based on `ScanStrategy` and `MaxScanItemsAbsolute`.
- Implement `SelectionPriority.ClassificationFirst` by classifying all scanned items into buckets, then trimming each bucket to `TopTypeLimit` using the selected comparator.
- Honor `EmitFlags` by returning empty lists for disabled emissions; the section builder will omit empty tables.

### Suggested preset mappings (keep defaults backward-compatible)

- `Fast`:
  - `SelectionMode = TopByCount`, `ScanStrategy = TopN`, `EmitShortish = false`, `TopTypeLimit = 10`, `ScanMultiplier = 1`
- `Balanced` (default):
  - `SelectionMode = CompositeScore` (balanced weights), `ScanStrategy = TopNByComparator`, `SelectionPriority = ClassificationFirst`, `TopTypeLimit = 20`, `ScanMultiplier = 2`
- `Full`:
  - `SelectionMode = CompositeScore`, `ScanStrategy = FullScan` (with `MaxScanItemsAbsolute` guard), `TopTypeLimit = 50`, `ScanMultiplier = 2`

### Tests to add

- Comparator tests: verify ordering for `TopByGen0Pct`, `TopBySize`, and `CompositeScore` on synthetic inputs.
- Classification-first test: feed synthetic `TypeAggregateIndexEntry` inputs and assert correct bucket membership and trimming behaviour.
- Preset mapping test: assert `AllocationPatternAnalysisOptions.Preset(AnalysisProfile)` sets enums/flags and numeric knobs as documented.

### Costs & trade-offs

- Complexity: moderate — adds options and branching; mitigate by preserving current defaults.
- Performance: `FullScan` and composite scoring increase CPU; mitigate with `MaxScanItemsAbsolute` and lazy metric computation.

If you want, I can implement `SelectionMode`, `ScanStrategy`, and `MaxScanItemsAbsolute` first and wire them into `AllocationPatternAnalysisOptions` and `AllocationPatternAnalyzer.cs`. Which set should I implement next?
