# WeakReferenceAnalyzer — Preset Design
 
**Status:** ✅ COMPLETED — Preset mappings, analyzer wiring, NDJSON/gzip exports, and report wiring implemented and tested.

Purpose: analyse weak GC handles, `WeakReference<T>` object health, and ConditionalWeakTable dead-key patterns.

This document specifies how presets (`Fast` / `Balanced` / `Full`) should drive both numeric limits and analyzer flow (not only TopN numbers), aligned with the implementation in `WeakReferenceAnalyzer.cs` and the reporting blocks in `WeakReferenceSectionBuilder.cs`.

## Current working (summary)
- `WeakReferenceAnalyzer` performs three phases:
	1. Weak handle liveness (prefers a shared in-memory snapshot when present, otherwise reads `HandleSnapshot.bin` from the heap index, and finally falls back to live `runtime.EnumerateHandles()`);
	2. `WeakReference<T>` object analysis (counts objects, bytes, and detects stale wrappers via `m_handle` sample probes);
	3. Dependent-handle dead-key counting (same reader precedence as liveness: in-memory snapshot → disk snapshot file → live enumeration).
- Key runtime knobs used in the analyzer: `HandleScanCap`, `TopTypeLimit`, and an option to produce raw exports.

## Summary of implemented behaviour

- Shared-memory + disk-first: the analyzer first consumes a shared, pre-enumerated in-memory snapshot when available (exposed via `HeapIndexBuildResult.InMemoryHandleSnapshot` and consumed through the `IHandleSnapshotReader` abstraction). If no in-memory snapshot is attached, it next prefers `HandleSnapshot.bin` (disk-backed reader) for fast, stream-friendly processing, and finally falls back to live `runtime.EnumerateHandles()` when snapshots are absent.
- Bounded scanning: `HandleScanCap` prevents unbounded work and sets the `ScanCapped` flag exposed in the domain result and report. When building a memory-backed index the index writer also caps the pre-enumeration to a safe upper limit (the current implementation uses a large default cap so the in-memory snapshot remains bounded).
- Bounded scanning: `HandleScanCap` prevents unbounded work and sets the `ScanCapped` flag exposed in the domain result and report. When building a memory-backed index the index writer also caps the pre-enumeration to a safe upper limit (`MaxHandleSnapshot = 500_000` in `MemoryBackedObjectIndexWriter`) so the in-memory snapshot remains bounded.
- Sampling: `WeakReference<T>` stale-wrapper detection is done by probing `SampleAddress` from the type aggregate rather than enumerating every instance (bounded, conservative estimate).
- Exports: analyzers may emit raw/export artifacts (NDJSON/Gzip or pretty JSON) when `ProduceRawExports` is enabled; the reporting stage and `WriteOutputStage` will move on-disk artifacts into `artifacts/<dumpBase>/` and expose viewing tips.

## Goals for preset-driven flow

- Presets control both numeric caps and behavioural choices: whether to run heavier probes, how large the sample budgets are, and whether raw exports are produced for offline analysis.
- Analyzer-level overrides (explicit config in `config.json`) must take precedence over presets.
- Preserve backward compatibility: `Balanced` should match current defaults and produce the same report shape unless explicitly overridden.

## Suggested new/used options

- `int HandleScanCap` — maximum handle records to process (disk snapshot or live enumeration).
- `int TopTypeLimit` — Top-N limit for reported alive-target types and stale-wrapper holder types.
- `bool ProduceRawExports` — when true, write NDJSON/Gzip and/or pretty JSON artifacts with full per-handle/Per-object samples for offline tooling.
- `int WeakRefProbeSampleLimit` — (optional) caps number of `WeakReference<T>` sample probes used to approximate stale-wrapper counts.

Note: the analyzer already uses `HandleSnapshot.bin` (when present) and exposes `ScanCapped` to the report; these options map directly to the values used in `WeakReferenceAnalyzer.cs`.

## Concrete preset mappings (recommended)

- Fast
	- `HandleScanCap = 20_000`
	- `TopTypeLimit = 8`
	- `WeakRefProbeSampleLimit = 4` (small sample)
	- `ProduceRawExports = false`

- Balanced (baseline / existing defaults)
	- `HandleScanCap = 50_000`
	- `TopTypeLimit = 15`
	- `WeakRefProbeSampleLimit = 8`
	- `ProduceRawExports = false`

- Full
	- `HandleScanCap = 200_000`
	- `TopTypeLimit = 40`
	- `WeakRefProbeSampleLimit = 32` (larger sampling budget)
	- `ProduceRawExports = true` (enable NDJSON/Gzip exports for tooling)

These values are tuned for ascending detail/cost: Fast → Balanced → Full. `ProduceRawExports` is useful for post-processing or feeding other analysis tools.

## How analyzer flow should respect presets

- Reader precedence: prefer a shared in-memory snapshot when present (populated by `MemoryBackedObjectIndexWriter` and surfaced via `HeapIndexBuildResult.InMemoryHandleSnapshot`), then fall back to `HandleSnapshot.bin` (disk-backed reader), and finally fall back to live `runtime.EnumerateHandles()` only when no snapshot file or in-memory snapshot exists.
- When processing records (in-memory, disk, or live), stop iterating once `HandleScanCap` is reached and set `ScanCapped = true` in the domain result.
- Use `WeakRefProbeSampleLimit` to bound the number of `m_handle` probes used to approximate stale-wrapper counts rather than attempting a full object-by-object probe.
- When `ProduceRawExports = true`, emit structured artifacts containing per-handle or per-sample records (NDJSON+gz for streaming toolchains) and a human-friendly `weakrefs.json` for quick inspection; the pipeline `WriteOutputStage` will place these under the artifacts directory.

Note: at present the in-memory snapshot consumer has been wired into `WeakReferenceAnalyzer` only; other analyzers that enumerate handles have TODOs to adopt the shared provider in future changes.

## Minimal code changes (implementation plan)
1. Add/confirm fields in `WeakReferenceAnalysisOptions` and implement `Preset(AnalysisProfile)` to set values above.
2. Ensure `WeakReferenceAnalyzer` reads `HandleSnapshot.bin` first (already implemented) and respects `HandleScanCap` and `WeakRefProbeSampleLimit` during processing.
3. If `ProduceRawExports` is enabled, stream NDJSON records to a gzipped temp file and add a `ReportArtifact` pointing to the file (the pipeline will move it into `artifacts/`).
4. Update `WeakReferenceSectionBuilder` to present an `EXPORTS` block listing artifacts and viewing tips (see `ThreadStackClusterSectionBuilder` for example UX).

## Tests and validation

- Unit tests: assert `Preset(Fast|Balanced|Full)` yields expected numeric settings; assert `WeakReferenceDomainResult.ScanCapped` toggles when cap is hit; assert `DomainResult` can carry `ReportArtifact` references when `ProduceRawExports` is true.
- Integration test: run the pipeline with a small synthetic or recorded dump that contains weak handles, set `Preset(Full)` or explicitly enable `ProduceRawExports`, and assert `weakrefs.ndjson.gz` appears in the final report `artifacts/<dumpBase>/` (follow the `WriteOutputStageTests` pattern already present).

## Next steps
- If you want, I can:
	- Implement the `WeakReferenceAnalysisOptions.Preset(...)` mappings and add unit tests, or
	- Add the NDJSON/Gzip export wiring in `WeakReferenceAnalyzer` and update `WeakReferenceSectionBuilder` to surface exports.

References: `WeakReferenceAnalyzer.cs` (analyzer flow and disk-backed reader) and `WeakReferenceSectionBuilder.cs` (report rendering and UX).
