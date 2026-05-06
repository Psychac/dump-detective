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
- `AsyncChainDetection` controls async state-machine handling (Disabled, CountOnly, Full, FullWithPaths).
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
	- `AsyncChainDetection = FullWithPaths`
	- `DetectWaitPatterns = true`

Notes: these mappings reflect the current `ThreadAnalysisOptions.Preset(...)` defaults in source and mirror the changes made during the recent sampling/refactor work.

## Testing and validation (what exists)
- Unit tests added for `ReservoirSampler` determinism and for sampling index selection (see `ReservoirSamplerTests` and `ThreadAnalyzerSamplingTests`).
- `InternalsVisibleTo.Tests` was added to allow testing internal helpers used to verify deterministic sampling.

## Implementation notes & tuning guidance
- Sampling seed: leave `SamplingSeed = 0` in presets to derive a per-dump deterministic seed (helps reproducibility across runs on the same dump). Set a fixed seed only for repeatable fuzzing tests.
- Cache behavior: `HeapAnalysisCache.GetOrCountThreadStackRoots(...)` lazily initializes its dictionary on first use; counts are stored keyed by `(thread.Address, MaxStackRootsToCount)`.
- Performance: small reservoir capacities increase replacement churn (RNG + copy) which can increase CPU; if `Balanced` appears slower than `Full` in some dumps, instrument per-phase timing (stack enumeration vs sampling vs root counting) before changing presets.

## Next steps (recommended)
- Add unit tests that assert `ThreadAnalysisOptions.Preset(...)` values and that `ThreadAnalyzer` respects `IncludeStackSamples` and `MaxThreadsToCaptureSnapshots` control flow.
- Add optional prewarm for `HeapAnalysisCache` if profiling shows cold-cache misses dominate Balanced runs (prewarm top-N threads only).

If you want, I can now implement the unit tests for preset behaviors or add a small instrumentation probe to `ThreadAnalyzer` to collect per-phase timings for Balanced vs Full runs.


