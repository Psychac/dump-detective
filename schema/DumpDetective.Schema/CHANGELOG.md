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

## `observation-type-registry.json` 1.57.0 — 2026-09-20

Third package in the `direction` rollout — `Plugins.Threads`'s headline measures: 14 across
`runtime.hang-pressure`, `runtime.lock-pressure`, `runtime.thread-cluster-diversity`, and
`runtime.thread-triage`, mined from `Hang`/`LockGraph`/`ThreadStackCluster`/`Thread`'s trend
comparers. `LockGraphTrendComparer` is the sparsest yet — 3 of its 4 headline metrics
(`lock.contested`, `lock.max.waiters`, `lock.deadlock.candidates`) have no raw whole-run measure at
all; the registry's own `lock.contested` observation type is per-lock (scoped), a naming
coincidence with the comparer metric of the same name, not the same data. Only `lock.held` (→
`held-lock.count`) is backed. `ThreadStackClusterTrendComparer`'s `cluster.top5.stability.percent`
is explicitly a cross-snapshot-only derived value per its own doc comment — never a single-snapshot
fact, so it was never a direction candidate at all, not merely unclassified.

## `observation-type-registry.json` 1.56.0 — 2026-09-20

Second package in the `direction` rollout started in 1.55.0 — `Plugins.Gc`'s headline measures: 20
across `finalizer.queue-population`, `handle.population`, `root.population`, and
`weakreference.population`, mined from `FinalizableObject`/`GCHandle`/`GCRoot`/`WeakReference`'s
trend comparers. Same rule as before: only measures with a real comparer opinion got a value; a
substantial fraction of these 4 analyzers' comparer metrics (finalizer queue retained bytes,
GC-handle "strong" count, GC-root severity score, weak-reference dead ratio) had no exact raw
measure to attach to — either derived/composite at comparison time or only available as scoped
per-entity data — and are left unclassified, same as Memory's gaps in 1.55.0.

## `observation-type-registry.json` 1.55.0 — 2026-09-20

Adds an optional `direction` field (`higherIsWorse` / `lowerIsWorse` / `neutral` /
`contextDependent`, backing the new public `DumpDetective.Sdk.Observations.MeasureDirection` enum)
alongside each measure's existing `unit`/`semantics`, for Phase 5 step 4's generic trend differ
(docs/refactor/modularity/phase-5-observations-synthesis.md ## Work step 4) — the differ needs to
know whether a growing value is bad without asking 36 hand-written comparers. Mined from those same
36 comparers' existing `MetricTrendDirection` per metric key, not guessed: a measure with no
matching comparer metric today is left without a `direction` key rather than defaulted, since an
absent key means "not yet classified" and `neutral` is itself an assertion. Rolling out headline
(unscoped) measures only, package by package, alongside the differ's own migration — scoped
(per-type) measures are explicitly out of this step's scope per the same doc entry. First package:
`Plugins.Memory`'s 5 owned observation types (`gc.allocation-composition`,
`gc.generation-composition`, `heap.loh-fragmentation`, `heap.memory-composition`,
`heap.segment-reservation`) — 40 of their measures annotated, the rest left unclassified pending
either a real comparer opinion to mine or a deliberate call once the differ exists to make one.

## `observation.schema.json` 2.0.0 — 2026-09-10

Scoped to `observation.schema.json` only — `capability-registry.json`/`observation-type-registry.json`
stay at 1.2.0 below; each file in this directory versions independently via its own
`schemaVersion`/`registryVersion` field, even where changelog entries share a version number by
coincidence.

Breaking change to the wire shape: `ArtifactId`, `Capability`, and `ObservationId` now serialize as
bare JSON strings instead of one-key wrapper objects (`{"value":"..."}"`/`{"key":"..."}"`), and
`ObservationId`'s string form now matches its `ToString()` exactly (both `"N"` format, no hyphens —
previously the wire form silently used the Guid default `"D"` format instead). Found by the
[Phase 1 SDK review](../../docs/refactor/modularity/phase-1-sdk-review-findings.md) (items 8 and 9);
fixed by giving each type its own `JsonConverter`. No real consumer existed for the 1.0.0 shape yet
(no UI, no persisted `report.json` corpus depending on it), so this was the correct time to fix it
rather than a migration anyone needs to plan around — verified by re-running the same live-build
probe-and-validate process the 1.0.0 schema was originally derived from.

## 1.3.0 — 2026-09-10

Removes `temporal.series`, added in 1.0.0 but never consumed by any real code. Same reasoning as
`TemporalKind.Series`'s removal
([Phase 1 SDK review](../../docs/refactor/modularity/phase-1-sdk-review-findings.md), follow-on to
item 11): a `Capability` is declared per-*artifact* (`ArtifactDescriptor.Provides`), and no single
artifact can provide "a series" any more than a single `Observation` can hold one. If a
session-level capability concept (`AnalysisSession.AvailableCapabilities` computing something no
individual artifact provides) turns out to be real once Phase 4 exists, reintroduce it there with
actual grounding — not kept here on a guess. `temporal.point`/`temporal.interval` are also
currently unconsumed by any real code but were left alone; removing them wasn't part of this pass.

## 1.2.0 — 2026-09-10

Splits `heap.dominators` into `heap.dominators` (narrowed to retained size/immediate
dominator/thread retention) and new `heap.reachability` — a
[Phase 1 SDK review](../../docs/refactor/modularity/phase-1-sdk-review-findings.md) finding (item 4)
that 1.1.0's single capability bundled two independently-gated build stages: reachability is a
Stage A product, the rest of `heap.dominators` is Stage B (gated on an analyzer implementing
`IRequiresDominatorTreeIndex`, optional even when Stage A succeeds). A session where Stage A
succeeded but Stage B didn't had no way to expose reachability alone under the old shape. Backs the
new `IHeapReachabilityQuery` interface alongside a narrowed `IHeapDominatorQuery`
(`src/DumpDetective.Sdk/Analysis/`). Additive, zero behavior change, no existing analyzer touched.

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
