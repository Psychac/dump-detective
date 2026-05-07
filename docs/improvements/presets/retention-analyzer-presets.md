**Retention Analyzer — Presets**

Purpose: define presets and preset-driven behavior for the Memory Leak Analyzer, and describe how presets should influence both numeric knobs and analyzer control flow (not just top-N reporting).

## Current working (summary)

-- `RetentionAnalyzer` performs a single-pass heap scan that simultaneously:
  - scans the finalizer queue via `heap.EnumerateFinalizableObjects()` (see `AnalyzeFinalizerQueue`),
  - enumerates heap entries and counts incoming references (`AnalyzeObjectsPass`).
- Reporting is produced by `RetentionSectionBuilder` and emits blocks for the finalizer queue and highly referenced objects (top types and top instances).
- Resource controls already present in analyzer options: `HighReferenceThreshold`, `MaxReferenceAddressesToTrack`, `MaxLeakScanObjects`, `Top*` caps for lists (currently internal constants).

See implementation references: `DumpDetective.Analysis.Analyzers.RetentionAnalyzer` and `DumpDetective.Reporting.SectionBuilders.RetentionSectionBuilder`.

## Summary of recent implementations

- Single-pass heap scanning: `AnalyzeObjectsPass` performs one traversal that both counts incoming references and collects string/retention signals when enabled.
- Index-backed fast path: when a `HeapAnalysisCache` with a heap index is available, the analyzer iterates disk-backed tuples instead of calling `heap.GetObject()` for every address.
- Caps & signals: analyzer already reports `SkippedReferenceAddresses` when reference-address tracking is capped, and `ObjectScanCapped` when the object-trace cap (`MaxLeakScanObjects`) is hit.
- Reporting: `RetentionSectionBuilder` renders finalizer queue counts and the top highly-referenced objects list, and shows a message when reference tracking was capped.

## Goals for preset-driven flow

- Presets should influence both numeric budgets (counts/thresholds) and algorithmic choices (index-only vs heap scan, scan budget enforcement, whether to produce on-disk artifacts).
- Preserve streaming-first, bounded-memory philosophy: prefer sampling, index-backed paths and early capping rather than materializing large structures.
- Make preset behavior explicit and reversible (named enums/flags and documented overrides), enabling tests to assert code-path selection.

## Options referenced by the analyzer (existing)

- `TopFinalizerTypesToShow` — controls top-N shown in the finalizer queue table (already present in `RetentionOptions`).
- `TopHighlyReferencedObjectsToShow` — controls the top-k highly referenced objects returned.
- `HighReferenceThreshold` — incoming-reference threshold used to classify "highly referenced" objects.
- `MaxReferenceAddresses` — cap on distinct addresses tracked for incoming-reference counts (reports `SkippedReferenceAddresses` when hit).
- `MaxLeakScanObjects` — cap on number of objects subjected to field-walk (`heap.GetObject()` calls) during the scan.
- `MaxDuplicateStringLength`, `MinDuplicateStringCount` — duplicate-string configuration values present in `RetentionOptions` for coordination with `StringAnalyzer` and reporting; the leak analyzer itself does not fingerprint strings.

These are the concrete knobs already consumed by the analyzer and its options type (`DumpDetective.Core.Options.RetentionOptions`).

## How analyzer flow should respect presets (principles)

- Enforce existing numeric caps exactly as implemented: `MaxReferenceAddresses` drives `AccumulateReference`'s skipping behavior and `MaxLeakScanObjects` sets `ObjectScanCapped` when hit.
- Preserve the index-backed fast path: prefer the heap index when available (current code checks `HeapAnalysisCache.TryGetHeapIndex`).
- Avoid implementing string fingerprinting in this analyzer; presets may alter the `RetentionOptions` duplicate-string knobs to influence `StringAnalyzer` behaviour or reporting, but cross-analyzer coordination should be explicit (see next section).
- Prefer adding explicit boolean flags for enabling/disabling expensive sub-analyses or algorithmic variants instead of overloading numeric knobs (e.g., `EnableDeepRetainerPaths`, `PreferIndexOnly`). These are proposals — not currently implemented.

## Concrete preset mappings (existing)

The `RetentionOptions.Preset(AnalysisProfile)` method already implements `Fast`, `Balanced` (default) and `Full` mappings. See `DumpDetective.Core.Options.RetentionOptions` for the authoritative values. The implemented presets set the numeric knobs used by the analyzer (top-N caps, thresholds and scan budgets).

Notes: choose numeric caps conservatively for large dumps; `MaxLeakScanObjects = 0` disables the object-scan cap and may be expensive.

## Observed implementation details (guidance for changes)

- `AnalyzeFinalizerQueue` iterates `heap.EnumerateFinalizableObjects()` and tallies per-type counts (promote top-N to options).
- `AnalyzeObjectsPass` is a single-pass routine that:
  - optionally uses a disk-backed heap index when available (`HeapAnalysisCache.EnumerateIndexedEntries...`),
  - falls back to `EnumerateLeakEntries` which enumerates `heap.EnumerateObjects()` and builds `HeapEntry` records,
  - respects `MaxLeakScanObjects` and increments `objectsTraced` for each `GetObject` + field walk.
- Incoming-reference counting is bounded by `MaxReferenceAddressesToTrack` and reports `SkippedReferenceAddresses` when the cap is reached.
- `CountIncomingReferencesByAddress`, `AccumulateReference` and `MethodTableHasOutgoingRefs` are the hot paths — keep them unchanged except to consult promoted options.

- `CountIncomingReferencesByAddress`, `AccumulateReference` and `MethodTableHasOutgoingRefs` are the hot paths — keep them unchanged except to consult promoted options.

See source: `DumpDetective.Analysis.Analyzers.RetentionAnalyzer` and `DumpDetective.Reporting.SectionBuilders.RetentionSectionBuilder`.

### Section-builder considerations

- `RetentionSectionBuilder` currently renders `SkippedReferenceAddresses` messaging; extend it to annotate table truncation when `Top*` caps applied and to include a short line showing which preset and which budget knobs influenced truncation.

### Diagnostics and telemetry

- Emit a lightweight diagnostic when a preset causes a major code-path change (e.g., `PreferIndexOnly` short-circuits heap scan) so users/operators can see analysis fidelity tradeoffs in run logs and CI traces.

## Minimal code changes (implementation plan)

1. Add new fields/enums to `RetentionOptions` (or `RetentionAnalysisOptions`) and update `Preset(Profile)` to set them.
2. Update configuration resolver to let analyzer-specific overrides replace preset values.
3. In `RetentionAnalyzer`:
   - Replace internal `Top*` constants with options fields.
   - Respect `LeakScanMode` early and short-circuit heap scan when `PreferIndexOnly`.
   - Respect `ProduceRawExports` and emit `ReportArtifact(FilePath)` when writing on-disk exports.
4. Update `RetentionSectionBuilder` to render `SkippedReferenceAddresses` and annotate truncated lists when `Top*` caps applied.

Optional follow-ups:
- Add a `PresetAudit` record to the domain result summarizing which code-path switches were taken (index used, scan capped, duplicates skipped) to make report UI show fidelity metadata.
- Add unit tests that mock both indexed and non-indexed paths to assert `LeakScanMode` behavior.

## Tests and validation

- Unit tests: mock a small `ClrHeap` and/or `HeapAnalysisCache` to assert code-path selection for `LeakScanMode`, that `SkippedReferenceAddresses` is emitted when `MaxReferenceAddressesToTrack` is exceeded, and that promoted `Top*` values control table rows.
- Integration/perf: run analyzer on a medium dump under each preset and verify `SkippedReferenceAddresses`, `FinalizerQueueCount`, and highly-referenced object counts differ predictably with budgets.

## Next steps I can take

- Implement `RetentionOptions.Preset(...)` and promote the internal `Top*` constants to configurable fields.
- Add a small unit test that asserts the `LeakScanMode` selection and a reporting test that shows `SkippedReferenceAddresses` in the section output.

## Files to review

- Analyzer: [DumpDetective.Analysis.Analyzers.RetentionAnalyzer](DumpDetective.Analysis/Analyzers/RetentionAnalyzer.cs)
- Section builder: [DumpDetective.Reporting.SectionBuilders.RetentionSectionBuilder](DumpDetective.Reporting/SectionBuilders/RetentionSectionBuilder.cs)

Rationale:
- **Fast:** tight budgets and higher `HighReferenceThreshold` reduce I/O and memory on very large dumps; good for initial triage.
- **Balanced:** default trade-off between coverage and resource use; sufficient for most medium-sized investigations.
- **Full:** expanded budgets lower the chance of capping and increase fidelity of duplicate-string and high-reference findings; use only when host has RAM/time to spare.

