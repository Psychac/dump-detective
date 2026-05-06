**LOH Fragmentation Analyzer — Presets**

- **Fast:** `TopSegments`: 5; `TopLargeObjectsCount`: 10
- **Balanced (default):** `TopSegments`: 10; `TopLargeObjectsCount`: 20
- **Full:** `TopSegments`: 25; `TopLargeObjectsCount`: 60

Notes: values taken directly from `LohFragmentationAnalysisOptions.Preset(AnalysisProfile)`.

# LOH Fragmentation Analyzer — Preset Design

Purpose: assess fragmentation in the Large Object Heap (LOH) and present free-gap histograms, per-segment fragmentation and top large objects.

Where to look in the repo:
- Analyzer: `src/DumpDetective.Analysis/Analyzers/LohFragmentationAnalyzer.cs`
- Section builder: `src/DumpDetective.Reporting/SectionBuilders/LohFragmentationSectionBuilder.cs`

Observed implementation details:
- The analyzer iterates `heap.Segments`, uses reflection to detect LOH segments across ClrMD versions (`IsLargeObjectSegment`, `Kind`, or `IsLarge`).
- For each LOH segment it enumerates objects and tallies `TotalBytes`, `FreeBytes`, `UsedBytes`, `LargestFreeBlock`, `FreeObjectCount` and computes `FragmentationPercent`.
- A `TopSegments` internal constant drives how many per-segment snapshots are returned (currently `TopSegments = 10`). The section builder renders free-gap histograms and top large objects when present.

Preset levers (recommended):
- `TopSegmentsToShow` (promote `TopSegments` to option)
- `IncludeFreeGapHistogram` (bool)
- `TopLargeObjectsToShow` (int)
- `SegmentScanSamplingFraction` (double 0..1) for very large heaps to sample objects inside segments

Concrete preset mappings:
Fast:
- Skip histogram and large-object enumeration: `IncludeFreeGapHistogram = false`, `TopSegmentsToShow = 3`, `TopLargeObjectsToShow = 5`, `SegmentScanSamplingFraction = 0.01`

Balanced (sensible default matching current analyzer behavior):
- `IncludeFreeGapHistogram = true`, `TopSegmentsToShow = 10`, `TopLargeObjectsToShow = 20`, `SegmentScanSamplingFraction = 0.1`

Full:
- Full scan: `IncludeFreeGapHistogram = true`, `TopSegmentsToShow = 50`, `TopLargeObjectsToShow = 200`, `SegmentScanSamplingFraction = 1.0`

Minimal code changes:
- Promote `TopSegments` const into `LohFragmentationAnalysisOptions.TopSegmentsToShow` and add `IncludeFreeGapHistogram`, `SegmentScanSamplingFraction`.
- When `SegmentScanSamplingFraction < 1.0`, sample objects inside a segment deterministically (e.g., stride-based sampling) to produce approximate histograms.
- Annotate the `LohFragmentationSectionBuilder` output with sampling fraction and whether segment counts were approximated.

Tests and validation:
- Unit: synthetic `LohSegmentStats` to validate `CalculateOverallFragmentationPercent`, formatting and top-segment selection.
- Integration: validate histogram shape and overall fragmentation percent on LOH-heavy traces; compare approximated vs full histogram with `SegmentScanSamplingFraction`.

Next steps:
- I can promote `TopSegments` to a configurable option and add sampling; tell me if you want the patch now and I will update `LohFragmentationAnalyzer.cs` and its options/tests.

Built-in presets (from `LohFragmentationAnalysisOptions.Preset`):
- **Fast:** `TopSegments = 5`, `TopLargeObjectsCount = 10`.
- **Balanced:** `TopSegments = 10`, `TopLargeObjectsCount = 20`.
- **Full:** `TopSegments = 25`, `TopLargeObjectsCount = 60`.

Rationale:
- **Fast:** small per-segment list keeps CPU and memory low when LOH is large; good for triage.
- **Balanced:** sensible default for medium dumps; reports enough segments and large objects for investigation.
- **Full:** collect many segments and LOH objects for deep forensic analysis; expect higher CPU and disk IO if histograms are enabled.

