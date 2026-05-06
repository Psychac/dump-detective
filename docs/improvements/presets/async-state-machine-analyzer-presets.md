# AsyncStateMachineAnalyzer — Preset Design

Purpose: identify compiler-generated async state-machine types, estimate captured closure sizes, and produce a suspended-method map.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/AsyncStateMachineAnalyzer.cs


Observed implementation details:
- Detects types via regex on type names (`<MethodName>d__N`) and confirms `IAsyncStateMachine` implementation using ClrMD.
- Operates mostly on `TypeAggregates` (O(#types)) and reads a single sample instance per type (if available) for field-level captured-ref size estimates.
- Options in code (`AsyncStateMachineAnalysisOptions`): `TypeCandidateLimit`, `TopTypeLimit`, `LargeCaptureThresholdBytes`, `TopCapturedSizeEntries`, `SuspendedMethodMapLimit`.

Built-in presets (`AsyncStateMachineAnalysisOptions.Preset`):
- Fast: `TopTypeLimit=10`, `TypeCandidateLimit=100`, `SuspendedMethodMapLimit=10`, `LargeCaptureThresholdBytes=2*1024*1024`, `TopCapturedSizeEntries=5`
- Balanced (default): `TopTypeLimit=20`, `TypeCandidateLimit=200`, `SuspendedMethodMapLimit=20`, `LargeCaptureThresholdBytes=1_024*1_024`, `TopCapturedSizeEntries=10`
- Full: `TopTypeLimit=40`, `TypeCandidateLimit=500`, `SuspendedMethodMapLimit=40`, `LargeCaptureThresholdBytes=512*1024`, `TopCapturedSizeEntries=20`


Minimal code changes recommended:
- No-op: `AsyncStateMachineAnalysisOptions` already provides `Preset(AnalysisProfile)` and `Default`.
- Document that this analyzer is index-first and performs one-sample reads per type (bounded by `TypeCandidateLimit`).

Tests and validation:
- Unit: craft `TypeAggregateIndexEntry` collections and verify regex filtering, candidate truncation and top-limit behaviors.
- Integration: run on sample dumps and confirm `SuspendedMethodMap` size responds to preset caps.

Rationale — when to pick each preset:
- **Fast:** smaller `TypeCandidateLimit` and `TopTypeLimit` reduce the number of sampled types and example reads; good for quick triage on large codebases where you only need the most obvious state-machine types.
- **Balanced:** (default) reasonable candidate and sample caps for normal investigation; still bounded to avoid scanning many rare compiler-generated types.
- **Full:** increases candidate scanning and lowers the `LargeCaptureThresholdBytes` to highlight smaller captured closures and return more captured-size entries — useful when you need exhaustive closure-size analysis.

Next steps:
- I can wire up the `Preset(...)` factory and add a unit test for regex-based detection if you'd like.
