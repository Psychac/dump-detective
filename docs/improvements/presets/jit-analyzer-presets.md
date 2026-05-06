# JitAnalyzer — Presets

Purpose: surface JIT/native code heap sizes, hot methods, and tiered compilation signals.

Options observed in code (`JitAnalysisOptions`):
- `MaxFramesPerThread` (int) — max stack frames to sample per thread.
- `TopMethodsLimit` (int) — number of largest compiled methods to report.
- `TopFrameTypesLimit` (int) — number of active frame types to include.
- `LargeMethodThresholdBytes` (uint) — threshold (bytes) to consider a method "large".

Fast:
- `MaxFramesPerThread`: 100
- `TopMethodsLimit`: 10
- `TopFrameTypesLimit`: 10
- `LargeMethodThresholdBytes`: 96 * 1024

Balanced (default):
- `MaxFramesPerThread`: 200
- `TopMethodsLimit`: 20
- `TopFrameTypesLimit`: 20
- `LargeMethodThresholdBytes`: 64 * 1024

Full:
- `MaxFramesPerThread`: 400
- `TopMethodsLimit`: 50
- `TopFrameTypesLimit`: 50
- `LargeMethodThresholdBytes`: 32 * 1024

Flow notes:
- `MaxFramesPerThread` trades analysis completeness for scan time; increase when stack-sampling accuracy matters.
- `LargeMethodThresholdBytes` lowers the bar in Full profile to surface more potentially problematic compilations.

Rationale — when to pick each preset:
- **Fast:** smaller `MaxFramesPerThread` and `TopMethodsLimit` reduce stack/frame sampling I/O; use for wide triage.
- **Balanced:** default values provide a balanced view of hot methods and compiled-code footprint without excessive sampling.
- **Full:** increase frame sampling and report width to capture more hot-methods and lower `LargeMethodThresholdBytes` to catch more large methods.

Next steps:
- Consider adding a `SampleThreadGroups` toggle to avoid sampling low-value system threads during Full runs.
