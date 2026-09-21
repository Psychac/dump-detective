# Phase 5 Step 4 — Remaining Comparer Retirement Audit

Part of [../modularity-plan.md](../modularity-plan.md), split out of
[phase-5-observations-synthesis.md](phase-5-observations-synthesis.md) on 2026-09-21, the same way
[phase-5-step2-per-analyzer-findings.md](phase-5-step2-per-analyzer-findings.md) was split out of
that doc a day earlier — once step 4's remaining scope moved from the 11 all-headline comparers
(all retired, see that doc's own step 4 log) to the 24 "mixed" comparers (headline **and** scoped
metrics), a full per-comparer audit didn't fit inline anymore. This is a snapshot audit, not part
of the live plan: it records the headline/scoped split and Observation-registry backing status for
each of the 24, one comparer at a time, so the extend-emission/trim/retire work that follows this
doesn't have to re-derive the analysis. Update it (or note staleness in the affected row) as
comparers actually get worked through it — don't let it silently drift from the code the way the
"Per-analyzer notable findings" doc explicitly avoids for step 2.

`LeakCandidateTrendComparer` is excluded — it's already fully deferred to step 6 (folding
`InsightEngine`, per [phase-5-observations-synthesis.md](phase-5-observations-synthesis.md)'s own
"Carve out `LeakCandidateAnalyzer` explicitly" prerequisite), not part of step 4/5's scope at all.

## Method

For each comparer, every metric its `ExtractMetrics` returns is one of:

- **headline** — `AnalyzerMetric.Scope is null`. A whole-run scalar, the shape
  `ObservationTrendDiffer` operates on.
- **scoped** — `AnalyzerMetric.Scope` set (per-type/per-entity). Explicitly out of the generic
  differ's scope (see the parent doc's step 4 log) — these branches stay in the comparer
  regardless of category below.

Each headline metric is then one of:

- **backed** — a `(ObservationType, measure key)` pair for that exact value already exists in
  `observation-type-registry.json` with a `direction`, and therefore in
  `MeasureDirectionRegistry`. Found by reading the analyzer's own `Analyze`/emission code, not
  just the comparer — the comparer's metric-key string and the Observation's measure-key string
  are unrelated namespaces that happen to describe the same value.
- **gap** — the analyzer's Stage-1 emission has never promoted that value to any Observation at
  all. Closeable the same way the 3 stranded comparers were this phase: add the missing
  measure(s) to an existing whole-run Observation, or a whole new one if the analyzer emits none.
- **composite** — the value comes from a classifier/scorer (`*Classifier.Compute*`), not a raw
  domain-result field. The observation-and-correlation-model's purity rule (§2a) bans composite
  scores from ever being a raw Observation measure — extending emission can't close this one.
  These are real candidates for step 5's "keep the genuine exceptions" bucket, not a step 4
  backlog item.

That produces four buckets, by what work (if any) is needed before a comparer's headline branches
can retire onto the differ:

| Bucket | Meaning | Next action |
|---|---|---|
| **A — fully backed** | Every headline metric is already backed. | Pure mechanical trim: delete the headline `ExtractMetrics`/`Compare` branches, keep the scoped ones, add the analyzer to `ObservationDifferAnalyzerOwnership`, delete now-redundant tests. Zero new emission — exactly the shape used to retire the 3 stranded comparers, just without the emission-extension step first. |
| **B — small gap** | 1–3 headline metrics are gaps. | Small Stage-1 emission extension (same recipe as this phase's `AsyncStateMachineAnalyzer`/`SqlConnectionPoolAnalyzer`/`HeapTopologyAnalyzer` work), then Bucket A's mechanical trim. |
| **C — composite exception** | At least one headline metric is a composite. | No emission work closes this. Write it up as an explicit step 5 "genuine exception" — the metric's `Compare`/`ExtractMetrics` branch for it stays hand-written permanently. Any *other* headline metrics on the same comparer still sort into A/B independently. |
| **D — large gap** | 4+ headline metrics are gaps (often most or all of them). | Needs a real new whole-run Observation type, analyzer by analyzer — same-sized work as this phase's 3 stranded comparers, not a quick follow-on. |

A comparer can appear in more than one bucket (e.g. `HangTrendComparer` is A for 6 of its 7
headline metrics and C for the 7th) — the tables below note this explicitly rather than forcing a
single label per comparer.

## Bucket A — ready for mechanical retirement now

**Done (2026-09-21).** All headline metrics on these 6 comparers were already backed, so no
emission work was needed. Their headline `ExtractMetrics`/`Compare` branches are removed, each
analyzer is registered in `ObservationDifferAnalyzerOwnership`, and the comparer classes stay
registered for their scoped branches:

| Comparer | Headline | Backing Observation | Scoped branches kept |
|---|---|---|---|
| `StaticRootTrendComparer` | 2/2 ✅ | `static-root.population` (`root.count`, `retained.bytes`) | `static.root.byname.bytes` (by root description) |
| `CollectionTrendComparer` | 3/3 ✅ | `collection.waste-summary` (`total-collection.count`, `total-wasted.bytes`, `wasteful-collection.count`) | `collection.waste.kind.count`/`.bytes` (by `CollectionKind`) |
| `CrashTrendComparer` | 2/2 ✅ | `runtime.crash-population` (`total.count`, `active.count`) | `crash.exception.type` (by exception type) |
| `ModuleTrendComparer` | 5/5 ✅ | `runtime.module-population` (`total.count`, `dynamic.count`, `version-conflict.count`, `total-domains.count`, `total-dynamic.count`) | `modules.heap.bytes`/`.objects`, `appdomain.module.type.count`/`.bytes` (by module name) |
| `ThreadTrendComparer` | 4/4 ✅ | `runtime.thread-triage` (`alive-thread.count`, `blocked-thread.count`, `lock-holding-thread.count`, `active-exception-thread.count`) | `thread.wait.category` (by wait category) |
| `MemoryTrendComparer` | 4/4 ✅ | `heap.memory-composition` (`total.bytes`, `loh.bytes`, `loh.percent`, `unique-types.count`) | `type.bytes`/`.count` (by type name, top 10 each), `memory.bucket.N.count` (by size bucket) |

`ModuleTrendComparer` and `StaticRootTrendComparer`'s `Compare` had no pre-existing scoped delta
branches (only `ExtractMetrics` fed their scoped metrics into the timeline), so their `Compare` now
always returns `[]` — expected, not a regression: `TrendAnalyzer.CompareAll` already skips
zero-delta comparer results, and the observation differ now supplies their headline deltas.

`ThreadTrendComparer.alive` is a useful precedent for the differ's Neutral handling: it's extracted
for the timeline but never appears in `Compare`'s regression/improvement list (Neutral direction
correctly excludes it there already) — the same behavior `ObservationTrendDiffer` needs to
reproduce, not a special case to design for.

`HangTrendComparer` belongs here too — **done (2026-09-21)** — for 6 of its 7 headline metrics
(`alive-thread.count`, `waiting-thread.count`, `waiting.percent`, `queued-work-items.count`,
`pending-task.count`, `faulted-task.count`, all backed by `runtime.hang-pressure`, now registered in
`ObservationDifferAnalyzerOwnership`). Its 7th, `hang.health.score`, stays hand-written permanently
as the Bucket C composite exception below — trimming the 6 backed metrics didn't wait on that
write-up landing, since the code-level work is independent of it.

## Bucket B — small emission extension needed

**Done (2026-09-21).** All 8 comparers retired: each gap closed by a small Stage-1 emission
extension (see the schema CHANGELOG entries below the table for the exact registry version each
landed in), then the same mechanical trim as Bucket A.

| Comparer | Backed / Headline | Gap (needs emission) |
|---|---|---|
| `WcfChannelTrendComparer` | 5/5 ✅ **done (2026-09-21)** | `faulted.count` — `infra.wcf-channel-population` gained `faulted.count` (registry 1.70.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `ObjectShapeTrendComparer` | 7/7 ✅ **done (2026-09-21)** | `balanced-type.count` — `type.shape-composition` gained it (registry 1.71.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `GCHandleTrendComparer` | 7/7 ✅ **done (2026-09-21)** | `strong` (`StrongLikeHandles`), `dependent.unresolved.percent` — `handle.population` gained `strong-like.count`/`dependent-unresolved.percent` (registry 1.72.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `AsyncTaskTrendComparer` | 7/7 ✅ **done (2026-09-21)** | `canceled` (`CanceledTasks`), `orphaned` (`OrphanedTasks`) — `async.task-population` gained `canceled.count`/`orphaned.count` (registry 1.73.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `DbConnectionTrendComparer` | 7/7 ✅ **done (2026-09-21)** | `open` (`OpenConnections`), `gen2.open`, `gen0.open` — `infra.db-connection-population` gained `open.count`/`gen2-open.count`/`gen0-open.count` (registry 1.74.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `SqlCommandTrendComparer` | 3/3 ✅ **done (2026-09-21)** | `active` — `infra.sql-command-population` gained `active.count` (registry 1.75.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `SqlTransactionTrendComparer` | 4/4 ✅ **done (2026-09-21)** | `active` — same pattern as `SqlCommand` above; `infra.sql-transaction-population` gained `active.count` (registry 1.76.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |
| `BoxingTrendComparer` | 11/11 ✅ **done (2026-09-21)** | `avg.instance.bytes`, `padding.aggregate`, `gen2.fraction` — `type.boxing-pressure` gained `avg-instance.bytes`/`padding-aggregate.bytes`/`gen2.percent` (registry 1.77.0); comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership` |

`GCRootTrendComparer` and `AllocationPatternTrendComparer` also had a Bucket B component —
**done (2026-09-21)**, see the Bucket C table below for the detail; their composite headline
metrics remain permanent Bucket C exceptions.

## Bucket C — composite/classifier-derived exceptions

**Written up (2026-09-21)** as step 5's explicit "genuine exceptions" list — see
[phase-5-observations-synthesis.md](phase-5-observations-synthesis.md) ## Work step 5. No code
changes of its own: all 3 comparers' non-composite headline metrics were already trimmed as part
of Bucket A/B; this just records the 4 composite metrics below as permanent, not a to-do.

These headline metrics can never be closed by more emission — they're derived scores, banned from
raw Observations by the purity rule. Each belongs to a comparer that is otherwise Bucket A or B for
its remaining headline metrics; only the composite metric itself needs to stay a hand-written
`Compare`/`ExtractMetrics` branch permanently:

| Comparer | Composite metric | Classifier | Comparer's other headline metrics |
|---|---|---|---|
| `HangTrendComparer` | `hang.health.score` | `HangHealthClassifier.ComputeHealthScore` (also `LowerIsWorse`, unlike the rest of the comparer) | 6/6 backed — Bucket A |
| `GCRootTrendComparer` | `gcroot.top.severity.score` | `GCRootSeverityClassifier.ComputeSeverity` | 4/4 non-composite ✅ **done (2026-09-21)** — `root.population` gained `strong-handle.count`/`finalizer-queue.count` (registry 1.78.0); non-composite headline branches trimmed, `gcroot.top.severity.score` stays hand-written, registered in `ObservationDifferAnalyzerOwnership` |
| `AllocationPatternTrendComparer` | `alloc.gc.pressure`, `alloc.promotion.pressure` | `AllocationPatternPressureClassifier.ClassifyPressure`/`ComputePromotionPressureScore` | 10/10 non-composite ✅ **done (2026-09-21)** — `gc.allocation-composition` gained `gen1.size-fraction` (registry 1.78.0); non-composite headline branches trimmed, both composite metrics stay hand-written, registered in `ObservationDifferAnalyzerOwnership` |

## Bucket D — needs a real new whole-run Observation type

Same-sized work as this phase's `AsyncStateMachineAnalyzer`/`HeapTopologyAnalyzer`/
`SqlConnectionPoolAnalyzer` extensions — tackle one analyzer at a time, not as a batch:

| Comparer | Backed / Headline | Notes |
|---|---|---|
| `ArrayTrendComparer` | 5/5 ✅ **done (2026-09-21)** | Turned out smaller than the Bucket D framing implied: `ArrayAnalyzer` already computed all 5 totals as independent locals during the scan that builds the existing, uncapped `type.array-composition` stream — no new heap traversal needed. New whole-run `type.array-population` observation type (registry 1.79.0), same shape as the Bucket B fixes; comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership`. |
| `TimerLeakTrendComparer` | 6/6 ✅ **done (2026-09-21)** | Same shape as `ArrayTrendComparer` — `TimerLeakAnalyzer` already had all 6 category totals plus total count/bytes as locals, no new heap traversal. New whole-run `infra.timer-population` type (registry 1.80.0) deliberately collapses `timer.logical`/`timer.queue` (always-identical fields — `LogicalTimerCount` is a legacy alias for `TimerQueueTimerCount`, per `TimerLeakSynthesisRule`'s own remarks) into one `timer-queue.count` measure instead of two, so 7 headline metrics land as 6 backed. Comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership`. |
| `FinalizableObjectTrendComparer` | 8/8 ✅ **done (2026-09-21)** | `total`/`total.bytes`/`gen2.count` backed by new whole-run `gc.finalizable-composition`; `queue.retained`/`queue.pressure.ratio`/`queue.critical.count`/`queue.critical.bytes` backed by `finalizer.queue-population` extended with 4 new measures (registry 1.81.0) — that emission also moved later in `Analyze` since `retained.bytes` needs the per-entry retained-bytes loop's exact totals. `pressure.ratio` is a plain derived ratio (`QueuePressureRatio`'s own formula, computed inline), not a classifier composite — same category as Boxing's `gen2.percent`, not Bucket C. Comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership`. |
| `DominatorTrendComparer` | 6/6 ✅ **done (2026-09-21)** | Simplest Bucket D item yet — every value was already sitting on the fully-constructed `DominatorDomainResult` by the time `EmitRetentionObservationsAsync` runs; just extended the existing whole-run `type.retention-population` (registry 1.82.0). Deliberately collapses `dominator.analyzed`/`dominator.top.count` (always-identical fields, both from the same `topTypes.Count`) into one `analyzed.count` measure, so 7 headline metrics land as 6 backed. `pressure.ratio` is a plain derived ratio, not a classifier composite. Comparer's headline branches trimmed and registered in `ObservationDifferAnalyzerOwnership`. |
| `HttpObjectTrendComparer` | 2/13 | Largest gap by headline-metric count — already flagged in the parent doc's CHANGELOG entry for the `direction` rollout ("`HttpObjectTrendComparer` has 13 headline metrics total, so 11 of them are unbacked"). |
| `StringTrendComparer` | 6/11 | `total`, `unique`, `gen0.count`, `gen1.count`, `gen2.count` have no backing whole-run Observation — `type.string-composition` backs the other 6, and the separate `1.68.0` string-sampling Observation (`very-long-string.*`, `sampling-coverage.ratio`, `strings-sampled.count`) doesn't cover these either. |
| `GCGenerationTrendComparer` | 9/14 | `gen0.percent`, `gen1.objects`, `gen1.percent`, `loh.objects`, `poh.percent` are gaps; the other 9 already match `gc.generation-composition` measures. |

## Suggested execution order

1. **Bucket A (6 comparers) — done 2026-09-21.** Zero emission risk, re-proved the exact retirement
   mechanism used for the 3 stranded comparers on ordinary mixed comparers (i.e. also verified the
   "comparer stays registered for its scoped branches, differ covers its headline ones" composition
   path in production for the first time — the 3 retired earlier this phase were all fully retired,
   never partially).
2. **Bucket B (8 comparers, some sharing a row with Bucket C) — done 2026-09-21.** Small, contained
   emission extensions, one comparer at a time, same recipe as this phase's 3 stranded comparers.
   `GCRootTrendComparer` and `AllocationPatternTrendComparer`'s Bucket B components are done too
   (2026-09-21) — see the Bucket C table below for detail; only their composite metrics remain as
   genuine Bucket C exceptions.
3. **Bucket C (3 comparers' worth of exceptions) — done 2026-09-21.** No code beyond what Bucket
   A/B already did to those comparers' other metrics; written up in step 5 as the explicit kept
   exceptions.
4. **Bucket D (7 comparers)** — the biggest remaining work, deliberately last: each needs its own
   analyzer-specific emission design decision the way `AsyncStateMachineAnalyzer`/
   `HeapTopologyAnalyzer`/`SqlConnectionPoolAnalyzer` did this phase, not a mechanical pass.
