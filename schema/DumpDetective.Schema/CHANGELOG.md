# Schema Changelog

Covers `observation.schema.json`, `capability-registry.json`, and `observation-type-registry.json`
in this directory — the Phase 1 SDK wire-format schemas
([../../docs/refactor/modularity/phase-1-contracts-sdk.md](../../docs/refactor/modularity/phase-1-contracts-sdk.md)).
This is a separate version track from `docs/schema-versioning.md`'s "Report schema: 2.1" (that
tracks `AnalysisReportDocument`, the dump-side `report.json`/trend-snapshot shape, which these
files don't touch), but follows the same policy: semver, additive-by-default, major bump only on a
backward-incompatible change to a persisted/wire shape.

`session-report.schema.json` (v3, `sources[]`/`timeline`/per-finding source attribution) is
deliberately not part of this directory yet — it needs Phase 4's session model, which doesn't
exist. See `phase-1-contracts-sdk.md`'s Status section for the standing reason.

`index-container-format.md` is documentation, not a versioned wire contract, so it isn't tracked
here — see its own "Known gap" section for its relationship to `docs/binary-format.md`.

## 1.1.0 — 2026-09-09

Adds `heap.dominators` (new — retained size/immediate dominator/reachability/thread retention as
one capability) and gives `runtime.locks` (declared since 1.0.0, unconsumed) its first real
designed consumer. Both back the Tier-1 capability-scoped SDK surfaces
(`src/DumpDetective.Sdk/Analysis/IHeapDominatorQuery.cs`, `IHeapSyncBlockQuery.cs`) added in
[phase-1-full-extraction-retyping-plan.md](../../docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md)'s
step 1 — additive, zero behavior change, no existing analyzer touched yet. Deliberately did *not*
add a new `heap.sync-blocks` capability for `LockGraphAnalyzer`'s `EnumerateSyncBlocks` usage after
finding `runtime.locks` already reserved for exactly this.

## 1.0.0 — 2026-09-09

Initial cut. All three files describe what's actually shipped and running today, not a forward
design:

- `capability-registry.json`: the 29 capabilities in
  `src/DumpDetective.Sdk/Artifacts/CapabilityVocabulary.cs`, mirrored verbatim. Nine are
  trace-provided; three of those (`trace.cpu-samples`, `trace.gc-events`,
  `trace.contention-events`) are in real production use by Phase 6b's `CpuHotspotAnalyzer`,
  `GcPauseAnalyzer`, and `ContentionAnalyzer`. The rest are declared but not yet produced by any
  shipped indexer or analyzer.
- `observation-type-registry.json`: the three `ObservationType` strings those same three analyzers
  emit today (`gc.pause`, `contention.episode`, `cpu.sample-attribution`), with their real measure
  keys/units/semantics.
- `observation.schema.json`: the wire shape of `DumpDetective.Sdk.Observations.Observation` as
  `TraceReportWriter` actually serializes it into trace-session `report.json` today. Verified
  against a live build of `DumpDetective.Sdk`, not hand-derived — see the schema's own `notes`
  array for five non-obvious serialization behaviors this surfaced (mixed enum/property casing,
  `$kind` only appearing on polymorphically-typed fields, wrapper-object encoding for
  `ArtifactId`/`Capability`/`ObservationId`, `ObservationId.ToString()` disagreeing with its own
  JSON form, and 64-bit handle fields exceeding IEEE-754-safe integer precision).

Conformance enforced by
`tests/DumpDetective.Tests/Unit/Architecture/SdkRegistryConformanceTests.cs`.
