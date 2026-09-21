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

## `observation-type-registry.json` 1.71.0 — 2026-09-21

Phase 5 step 4, Bucket B: `type.shape-composition` gains `balanced-type.count` (`neutral`) —
`ObjectShapeAnalyzer` already computed `TopBalancedTypes.Count` for the domain result but never
promoted it, unlike its `reference-heavy-type.count`/`value-heavy-type.count` siblings; balanced
types have no per-type observation to reconstruct the count from either. Closes
`ObjectShapeTrendComparer`'s last headline gap (6/7 → 7/7), unblocking its retirement onto the
differ.

## `observation-type-registry.json` 1.70.0 — 2026-09-21

Phase 5 step 4, Bucket B (docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md):
`infra.wcf-channel-population` gains `faulted.count` (`higherIsWorse`) — `WcfChannelAnalyzer`
already computed `totalFaulted` for the domain result but never promoted it to the whole-run
observation, since it's reconstructable by counting `infra.wcf-channel`'s faulted-only per-instance
stream. `ObservationTrendDiffer` can't do that grouping, so it's emitted directly instead — the
same pragmatic call already made this phase for `infra.sql-connection-pool-population.near-capacity.count`.
Closes `WcfChannelTrendComparer`'s last headline gap (4/5 → 5/5), unblocking its retirement onto
the differ.

## `observation-type-registry.json` 1.69.0 — 2026-09-21

Phase 5 step 4 (docs/refactor/modularity/phase-5-observations-synthesis.md): extends emission for
the 3 comparers left stranded by the `direction` rollout — `AsyncStateMachineTrendComparer`,
`HeapTopologyTrendComparer`, `SqlConnectionPoolTrendComparer` — each 100% headline by `Scope`
convention but backed by zero registry measures, because their analyzer never promoted a whole-run
aggregate to an `Observation`. New whole-run, no-entity-subject types (`gc.generation-composition`
pattern): `type.async-state-machine-population` (4 measures, `AsyncStateMachineAnalyzer` summed
once over its existing `IsAsyncStateMachineType` totals loop) and
`infra.sql-connection-pool-population` (3 measures, `SqlConnectionPoolAnalyzer` computed from the
pool list it already builds — first whole-run aggregate this analyzer has ever emitted).
`heap.topology-population` (existing, 1.58.0) gains 9 measures instead of a new type, since
`HeapTopologyDomainResult` already carries the exact totals as sums over the 5
`heap.segment-kind-composition` rows — no additional heap traversal needed. All 9 new measures'
`direction`s added inline, mined from each comparer's existing `MetricTrendDirection` the same way
the rollout itself was mined. Unblocks all 3 comparers to retire onto `ObservationTrendDiffer`
next, closing the "3 comparers explicitly NOT retired" gap step 4 left open on 2026-09-20.

## `observation-type-registry.json` 1.62.0 — 2026-09-20

Eighth and last package in the `direction` rollout — `Plugins.Infra`: 15 headline measures across
`infra.db-connection-population` (4), `infra.http-object-population` (2 of only 2 keys that type
declares — full coverage, though `HttpObjectTrendComparer` has 13 headline metrics total, so 11 of
them are unbacked), `infra.sql-command-population` (2), `infra.sql-transaction-population` (3, full
coverage), and `infra.wcf-channel-population` (4). `SqlConnectionPoolTrendComparer` and
`TimerLeakTrendComparer` both back zero registry measures — `SqlConnectionPoolAnalyzer` never
emits a whole-run aggregate at all (`infra.sql-connection-pool`'s `current.size`/`max.size`/
`min.size` are per-pool), and `TimerLeakAnalyzer`'s only observation type (`infra.timer`) is
emitted per timer type inside a loop, same as `TimerLeakTrendComparer`'s own scoped metrics.
Fifth and sixth cases of "100% headline by Scope convention, 0% backed", after `AsyncStateMachine`,
`Array`, and `HeapTopology`.

This completes the headline `direction` rollout across all 8 `Plugins.*` packages started in
1.55.0. Running total: 149 measures annotated (40 + 20 + 14 + 5 + 23 + 8 + 16 + 15) across the 25
observation types that turned out to carry a genuine whole-run headline aggregate with at least one
comparer opinion to mine. 6 comparers across the whole codebase
(`AsyncStateMachine`/`Array`/`HeapTopology`/`SqlConnectionPool`/`TimerLeak`, all "headline by Scope
convention, 0% backed") back nothing at all, and every package left a residue of registry measures
with no comparer ground truth to mine, or comparer metrics with no registry measure to attach to
(derived/composite scores, percent-of-total figures, values only available via scoped aggregation).
Both are left as-is rather than guessed - see docs/refactor/modularity/phase-5-observations-synthesis.md
## Work step 4 for what happens next (the differ itself, then per-package comparer retirement).

## `observation-type-registry.json` 1.61.0 — 2026-09-20

Seventh package — `Plugins.Runtime`: 16 headline measures across `runtime.crash-population` (2),
`runtime.jit-activity` (8 — every one of `JitTrendComparer`'s headline metrics maps cleanly, the
cleanest single comparer covered so far), and `runtime.module-population` (6), mined from
`Crash`/`Jit`/`Module`'s trend comparers. `HeapTopologyTrendComparer` backs zero registry measures
- its only whole-run observation type, `heap.topology-population`, carries `logical-heap`/
`is-server-gc` measures unrelated to the comparer's `segment.*` metrics, which are all sums over
the *scoped*, per-kind `heap.segment-kind-composition` type instead. Third case of "100% headline
by Scope convention, 0% backed", after `AsyncStateMachine` (1.58.0) and `Array` (1.59.0).

## `observation-type-registry.json` 1.60.0 — 2026-09-20

Sixth package — `Plugins.Leaks`: 8 headline measures across `type.retention-population` (1),
`event.publisher-leak-population` (3, full coverage), `reference-chain.population` (2), and
`static-root.population` (2, full coverage), mined from
`Dominator`/`EventLeak`/`ReferenceChain`/`StaticRoot`'s trend comparers.
`DominatorTrendComparer` is the sparsest single comparer covered so far — 6 of its 7 headline
metrics (candidates, analyzed count, retained bytes, retention-pressure ratio, top-type count,
highly-referenced bytes) have no whole-run registry measure; only `leak.highly.referenced` matches.
`LeakCandidateAnalyzer` contributes nothing at all - it emits no observations yet, still deferred to
step 6 per the per-analyzer playbook.

## `observation-type-registry.json` 1.59.0 — 2026-09-20

Fifth package — `Plugins.Types`: 23 headline measures across `type.boxing-pressure` (8),
`collection.waste-summary` (3, full coverage — every measure that type declares now has a
direction), `type.shape-composition` (6), and `type.string-composition` (6), mined from
`Boxing`/`Collection`/`ObjectShape`/`String`'s trend comparers. `ArrayTrendComparer` is the second
case (after `AsyncStateMachine` in 1.58.0) of a comparer that's 100% headline by `Scope` convention
but backs zero registry measures — verified against `ArrayAnalyzer.cs`: its sole composition
observation type (`type.array-composition`) is emitted per array element type
(`foreach (... in result.TopArrayTypesBySize)`), never as a whole-run aggregate.

## `observation-type-registry.json` 1.58.0 — 2026-09-20

Fourth package — `Plugins.Async`: only 5 headline measures, all under `async.task-population`,
mined from `AsyncTaskTrendComparer`. `AsyncStateMachineTrendComparer` is 100% headline by the
comparer's own `AnalyzerMetric.Scope` convention (it trends run-wide totals: `statemachine.total`,
`.gen2.count`, `.gen2.fraction`, `.total.bytes`) but contributes zero registry measures — verified
against `AsyncStateMachineAnalyzer.cs` directly: its only two observation types
(`type.async-state-machine`, `type.async-captured-closure`) are both emitted per state-machine
type/method (`MethodRef`-subject, inside a loop), never as a whole-run aggregate. "Headline by the
comparer's Scope convention" and "backed by a headline Observation" are different claims — this is
the sharpest case of that gap found so far.

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
