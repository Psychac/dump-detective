# ThreadAnalyzer — Preset Reference (implemented)

Purpose: describe the `Fast` / `Balanced` / `Full` presets as implemented for `ThreadAnalyzer` and map them to concrete `ThreadAnalysisOptions` fields and runtime behavior.

## Implementation summary
- New options implemented in `ThreadAnalysisOptions`: `MaxFramesForThreadScan`, `MaxStackRootsToCount`, `MaxThreadsToCaptureSnapshots`, `IncludeStackSamples`, `AsyncChainDetection` (enum), `DetectWaitPatterns`, `MaxTopHotspots`, and `SamplingSeed`.
- Presets drive both numeric caps and analyzer control flow (sampling vs exhaustive, async-chain depth, wait-pattern scanning).
- Deterministic streaming sampling: `ThreadAnalyzer` uses a `ReservoirSampler<T>` (seeded RNG). When `SamplingSeed == 0` the pipeline derives a per-dump seed from the dump path (SHA256 -> 32-bit seed) so samples are reproducible per-dump but differ across dumps.
- Sampled stacks are surfaced in reports under the "SAMPLED THREAD SNAPSHOTS" section, and `MaxSampledStackSnapshots` / `MaxThreadsToCaptureSnapshots` bound how many are retained.

## How presets affect behavior (runtime)
- `IncludeStackSamples == false`: skip sampled-stack collection for non-top threads (saves CPU/memory).
- `MaxThreadsToCaptureSnapshots`: cap how many threads get a full snapshot (top blocked/locked/hot lists).
- `MaxStackRootsToCount` bounds `GetOrCountThreadStackRoots(...)` per-thread; counts are cached lazily in `HeapAnalysisCache`.
 - `AsyncChainDetection` controls async state-machine handling (Disabled, CountOnly, Full).
- `DetectWaitPatterns` toggles token-based wait-pattern detection; disabling it uses cheaper heuristics.

## Concrete preset mappings (current defaults)

- Fast
	- `MaxFramesForThreadScan = 4`
	- `MaxStackRootsToCount = 128`
	- `MaxThreadsToCaptureSnapshots = 10`
	- `IncludeStackSamples = false`
	- `AsyncChainDetection = CountOnly`
	- `DetectWaitPatterns = true`

- Balanced (baseline / existing defaults)
	- `MaxFramesForThreadScan = 8`
	- `MaxStackRootsToCount = 256`
	- `MaxThreadsToCaptureSnapshots = 20`
	- `IncludeStackSamples = true`
	- `AsyncChainDetection = Full`
	- `DetectWaitPatterns = true`

- Full
	- `MaxFramesForThreadScan = 16`
	- `MaxStackRootsToCount = 1024`
	- `MaxThreadsToCaptureSnapshots = 50`
	- `IncludeStackSamples = true`
	- `AsyncChainDetection = Full`
	- `DetectWaitPatterns = true`

Notes: these mappings reflect the current `ThreadAnalysisOptions.Preset(...)` defaults in source and mirror the changes made during the recent sampling/refactor work.

## Testing and validation (what exists)
- Unit tests added for `ReservoirSampler` determinism and for sampling index selection (see `ReservoirSamplerTests` and `ThreadAnalyzerSamplingTests`).
- `InternalsVisibleTo.Tests` was added to allow testing internal helpers used to verify deterministic sampling.
 - Unit tests added for async-chain behavior (`ThreadAsyncChainTests`) covering `CountOnly`, `Full`, and `Disabled` behaviors.
 - Added tests verifying sampler determinism and zero-capacity behavior (`ThreadAnalyzerSamplingTests`).
 - Preset-value and preset-behavior tests added (`ThreadAnalysisOptionsTests`, `PresetBehaviorTests`, `ThreadAnalyzerSamplerCapacityTests`).

## Implementation notes & tuning guidance
- Sampling seed: leave `SamplingSeed = 0` in presets to derive a per-dump deterministic seed (helps reproducibility across runs on the same dump). Set a fixed seed only for repeatable fuzzing tests.
- Cache behavior: `HeapAnalysisCache.GetOrCountThreadStackRoots(...)` lazily initializes its dictionary on first use; counts are stored keyed by `(thread.Address, MaxStackRootsToCount)`.
- Performance: small reservoir capacities increase replacement churn (RNG + copy) which can increase CPU; if `Balanced` appears slower than `Full` in some dumps, instrument per-phase timing (stack enumeration vs sampling vs root counting) before changing presets.

## Next steps (recommended)
- Add unit tests that assert `ThreadAnalysisOptions.Preset(...)` values and that `ThreadAnalyzer` respects `IncludeStackSamples` and `MaxThreadsToCaptureSnapshots` control flow.
- Add optional prewarm for `HeapAnalysisCache` if profiling shows cold-cache misses dominate Balanced runs (prewarm top-N threads only).
 - Remaining test work: add an integration-style unit that exercises `CategorizeThreads` end-to-end using lightweight fakes for `ClrThread` to assert gating behaviour for `IncludeStackSamples` and `MaxThreadsToCaptureSnapshots`.
 - Consider adding end-to-end tests using small synthetic dumps (longer task) to validate runtime performance and full reporting paths.

If you want, I can now implement the unit tests for preset behaviors or add a small instrumentation probe to `ThreadAnalyzer` to collect per-phase timings for Balanced vs Full runs.

## Presets as Behavioral Switches — actionable suggestions

The codebase now exposes numeric knobs; below are concrete suggestions to make presets drive control flow and reporting beyond raw numbers. Each suggestion includes an implementation hint referencing `ThreadAnalyzer.cs` and `ThreadSectionBuilder.cs`.

 - Sampling vs exhaustive capture -> implemented (sampling metadata added; gating remains controlled by `IncludeStackSamples`)
	- Intent: `Fast` and `Balanced` should prefer low-cost sampling; `Full` should favor exhaustive capture where practical.
	- Implementation: use `options.IncludeStackSamples` + `options.MaxSampledStackSnapshots` to gate calls to the reservoir sampler in `ThreadAnalyzer.CategorizeThreads(...)`. If `IncludeStackSamples == false` skip calling `sampler.Add(...)` entirely; if `MaxSampledStackSnapshots == 0` construct a zero-capacity sampler so `sampler.Samples()` is empty.
	- Reporting: surface sample provenance (method, capacity, seed) in `ThreadSectionBuilder` next to the "SAMPLED THREAD SNAPSHOTS" header so users know snapshots are sampled and reproducible.

 - Snapshot selection and determinism -> implemented (seed reported; deterministic sampler used)
	- Intent: deterministic selection across runs on the same dump, but differing seeds across dumps.
	- Implementation: continue deriving seed when `SamplingSeed == 0` in pipeline; when selecting the top-N for full snapshots, break ties deterministically (e.g., sort by `LockCount` then `thread.Address`). Use `SampleCandidateIndices(...)` in tests to validate determinism.
	- Reporting: include the seed hex value and sample count in summary (e.g., "Sampled snapshots: 32 (reservoir seed: 0xDEADBEEF)").


 - Async-chain detection modes (control-flow toggle) -> implemented (counting + `Full` extra path-frame capture)
	 - Intent: let presets turn async analysis into a cheap count-only pass for `Fast`, full depth for `Balanced`, and rich path capture for `Full`.
	 - Implementation: `CountOnly` computes chain depth only. `Full` captures a representative async-path window so reports include representative async frames. This is implemented in `ThreadAnalyzer` and is covered by unit tests.


 - Wait-pattern detection toggle -> implemented
	- Intent: expensive token matching can be skipped for low-cost runs.
	- Implementation: wrap `DetectWaitPattern(...)` invocation with `if (options.DetectWaitPatterns) { ... } else { use cheap heuristics }` in `CategorizeThreads` to avoid iterating token lists in `Fast` mode.


 - Cache prewarm strategies -> implemented (sync prewarm for non-large dumps; background prewarm option available)
	- Intent: reduce first-use latency for stack-root counts on large dumps when Balanced is chosen.
	- Implementation options:
		- Conservative: prewarm `HeapAnalysisCache.GetOrCountThreadStackRoots(thread, options.MaxStackRootsToCount)` for only the top-N threads (N = `MaxThreadsToCaptureSnapshots`) before `CategorizeThreads` materializes snapshots. -> implemented (sync prewarm for non-large dumps)
		- Background: run prewarm in a background task and report progress; only block materialization when the specific thread is required. -> implemented (controlled by `PrewarmCacheInBackground`, enabled by `Full` preset)
	- Tradeoffs: prewarm adds upfront CPU and memory; prefer prewarm for small/medium dumps or only top-N threads for large dumps.


 - Progress and milestone reporting -> implemented (start/complete/prewarm/materializing milestones)
	- Intent: make progress messages reflect phases so users and CI can understand where time is spent.
	- Implementation: in `CategorizeThreads` use `progress?.Report(new(count, "Sampling threads: X of Y"))` at start/finish of sampling and keep `ObjectScanCounter` for steady updates. Add short milestone messages: "Starting thread sampling", "Thread sampling complete", "Materializing snapshots", "Thread analysis complete".
	- Reporting: add counters to `ThreadDomainResult` (e.g., `SampledSnapshotCount`, `CapturedSnapshotCount`) and show these in `ThreadSectionBuilder` summary table.


 - Report UX improvements (what to show) -> implemented
	- Always indicate whether a snapshot was "captured" (top-N) or "sampled" when rendering each thread block in `ThreadSectionBuilder`. -> implemented
	- Add a short `NOTE` above the sampled section describing sampling method and seed: "These are reservoir-sampled snapshots (seed: 0x...) — reproducible per-dump." This clarifies why some threads are present/absent. -> implemented (seed & counts displayed)


 - Adaptive preset tuning -> not implemented

 - Adaptive preset tuning -> implemented (heuristic scale by `HeapAnalysisCache.SizeTier`)
  
 - Runtime thread-count scaling: removed. Presets adapt by `HeapAnalysisCache.SizeTier`.
	- Intent: let presets adapt to dump size and runtime metrics instead of hard-coded constants.
	- Implementation idea: combine `HeapAnalysisCache.SizeTier` (available from the index prebuild) with profile to scale `MaxSampledStackSnapshots` and `MaxThreadsToCaptureSnapshots` up or down. Example: Balanced -> min(20, TotalThreads/10) snapshots.


 - Tests to validate behavioral presets -> not implemented

 - Tests to validate behavioral presets -> implemented (preset flag tests + sampler capacity tests added)
	- Unit tests: assert `ThreadAnalysisOptions.Preset(Profile)` yields expected enum and boolean flags (not just numbers). Add integration-style unit that runs `CategorizeThreads` on a small synthetic `ClrThread` collection (or use the `SampleCandidateIndices` helper) to assert sampling path taken when `IncludeStackSamples` is true vs false.

These suggestions are intentionally actionable and small-scope: they can be implemented incrementally (start with reporting + gating `IncludeStackSamples`, then add async-mode guards, then prewarm). Tell me which ones you want implemented first and I will open a PR with focused commits.


