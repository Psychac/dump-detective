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

No emission work needed. All headline metrics on these 6 comparers are already backed:

| Comparer | Headline | Backing Observation | Scoped branches kept |
|---|---|---|---|
| `StaticRootTrendComparer` | 2/2 | `static-root.population` (`root.count`, `retained.bytes`) | `static.root.byname.bytes` (by root description) |
| `CollectionTrendComparer` | 3/3 | `collection.waste-summary` (`total-collection.count`, `total-wasted.bytes`, `wasteful-collection.count`) | `collection.waste.kind.count`/`.bytes` (by `CollectionKind`) |
| `CrashTrendComparer` | 2/2 | `runtime.crash-population` (`total.count`, `active.count`) | `crash.exception.type` (by exception type) |
| `ModuleTrendComparer` | 5/5 | `runtime.module-population` (`total.count`, `dynamic.count`, `version-conflict.count`, `total-domains.count`, `total-dynamic.count`) | `modules.heap.bytes`/`.objects`, `appdomain.module.type.count`/`.bytes` (by module name) |
| `ThreadTrendComparer` | 4/4 | `runtime.thread-triage` (`alive-thread.count`, `blocked-thread.count`, `lock-holding-thread.count`, `active-exception-thread.count`) | `thread.wait.category` (by wait category) |
| `MemoryTrendComparer` | 4/4 | `heap.memory-composition` (`total.bytes`, `loh.bytes`, `loh.percent`, `unique-types.count`) | `type.bytes`/`.count` (by type name, top 10 each), `memory.bucket.N.count` (by size bucket) |

`ThreadTrendComparer.alive` is a useful precedent for the differ's Neutral handling: it's extracted
for the timeline but never appears in `Compare`'s regression/improvement list (Neutral direction
correctly excludes it there already) — the same behavior `ObservationTrendDiffer` needs to
reproduce, not a special case to design for.

`HangTrendComparer` belongs here for 6 of its 7 headline metrics too (`alive-thread.count`,
`waiting-thread.count`, `waiting.percent`, `queued-work-items.count`, `pending-task.count`,
`faulted-task.count`, all backed by `runtime.hang-pressure`) — see Bucket C below for its 7th.

## Bucket B — small emission extension needed

| Comparer | Backed / Headline | Gap (needs emission) |
|---|---|---|
| `WcfChannelTrendComparer` | 4/5 | `faulted.count` |
| `ObjectShapeTrendComparer` | 6/7 | `balanced-type.count` (no measure exists for `TopBalancedTypes.Count`) |
| `GCHandleTrendComparer` | 5/7 | `strong` (`StrongLikeHandles`), `dependent.unresolved.percent` |
| `AsyncTaskTrendComparer` | 5/7 | `canceled` (`CanceledTasks`), `orphaned` (`OrphanedTasks`) |
| `DbConnectionTrendComparer` | 4/7 | `open` (`OpenConnections`), `gen2.open`, `gen0.open` |
| `SqlCommandTrendComparer` | 2/3 | `active` — `ActiveCount` is currently deliberately *not* raw-emitted (registry note: reconstructable by counting `infra.sql-command` per-command observations); the differ can't do that grouping, so the pragmatic fix is to just emit it directly, same call already made for `infra.sql-connection-pool-population.near-capacity.count` this session |
| `SqlTransactionTrendComparer` | 3/4 | `active` — same reconstructable-but-not-emitted pattern as `SqlCommand` above |
| `BoxingTrendComparer` | 8/11 | `avg.instance.bytes`, `padding.aggregate` (both plain raw sums/averages already computed), `gen2.fraction` (a simple derived ratio — same shape as `AsyncStateMachineAnalyzer`'s `gen2.percent` added this session, not a classifier composite) |

`GCRootTrendComparer` and `AllocationPatternTrendComparer` also have a Bucket B component — see
Bucket C, which covers both buckets for each of those two comparers together.

## Bucket C — composite/classifier-derived exceptions

These headline metrics can never be closed by more emission — they're derived scores, banned from
raw Observations by the purity rule. Each belongs to a comparer that is otherwise Bucket A or B for
its remaining headline metrics; only the composite metric itself needs to stay a hand-written
`Compare`/`ExtractMetrics` branch permanently, is a real step 5 "genuine exception," and should be
written up there rather than chased as an emission gap:

| Comparer | Composite metric | Classifier | Comparer's other headline metrics |
|---|---|---|---|
| `HangTrendComparer` | `hang.health.score` | `HangHealthClassifier.ComputeHealthScore` (also `LowerIsWorse`, unlike the rest of the comparer) | 6/6 backed — Bucket A |
| `GCRootTrendComparer` | `gcroot.top.severity.score` | `GCRootSeverityClassifier.ComputeSeverity` | 2/4 backed (`total.roots`, `path.capped.count`); `strong.handle.count`/`finalizer.count` are Bucket B gaps |
| `AllocationPatternTrendComparer` | `alloc.gc.pressure`, `alloc.promotion.pressure` | `AllocationPatternPressureClassifier.ClassifyPressure`/`ComputePromotionPressureScore` | 9/10 backed; `alloc.gen1.size.pct` is a Bucket B gap (the registry has `gen1.count-fraction` but not `gen1.size-fraction` — a genuine one-key hole next to nine matches) |

## Bucket D — needs a real new whole-run Observation type

Same-sized work as this phase's `AsyncStateMachineAnalyzer`/`HeapTopologyAnalyzer`/
`SqlConnectionPoolAnalyzer` extensions — tackle one analyzer at a time, not as a batch:

| Comparer | Backed / Headline | Notes |
|---|---|---|
| `ArrayTrendComparer` | 0/5 | Already named in the parent doc's step 4 log as one of the "100% headline by Scope convention, 0% backed" cases found during the `direction` rollout — this was already known, not a new finding here. |
| `TimerLeakTrendComparer` | 0/7 | `TimerLeakAnalyzer` currently emits only the per-timer-type `infra.timer` — zero whole-run aggregate exists at all, same starting point `SqlConnectionPoolAnalyzer` was at before this phase's work. |
| `FinalizableObjectTrendComparer` | 1/8 | Only `queue.count` (`finalizer.queue-population`) is backed; `total`, `total.bytes`, `gen2.count`, `queue.retained`, `queue.pressure.ratio`, `queue.critical.count`, `queue.critical.bytes` are all gaps. |
| `DominatorTrendComparer` | 1/7 | Only `leak.highly.referenced` (`type.retention-population.highly-referenced.count`) is backed; no `dominator.*` whole-run Observation type exists at all for the other 6. |
| `HttpObjectTrendComparer` | 2/13 | Largest gap by headline-metric count — already flagged in the parent doc's CHANGELOG entry for the `direction` rollout ("`HttpObjectTrendComparer` has 13 headline metrics total, so 11 of them are unbacked"). |
| `StringTrendComparer` | 6/11 | `total`, `unique`, `gen0.count`, `gen1.count`, `gen2.count` have no backing whole-run Observation — `type.string-composition` backs the other 6, and the separate `1.68.0` string-sampling Observation (`very-long-string.*`, `sampling-coverage.ratio`, `strings-sampled.count`) doesn't cover these either. |
| `GCGenerationTrendComparer` | 9/14 | `gen0.percent`, `gen1.objects`, `gen1.percent`, `loh.objects`, `poh.percent` are gaps; the other 9 already match `gc.generation-composition` measures. |

## Suggested execution order

1. **Bucket A (6 comparers)** — zero emission risk, re-proves the exact retirement mechanism used
   for the 3 stranded comparers on ordinary mixed comparers (i.e. also verifies the "comparer stays
   registered for its scoped branches, differ covers its headline ones" composition path actually
   used in production for the first time — the 3 retired earlier this phase were all fully
   retired, never partially).
2. **Bucket B (8 comparers, some sharing a row with Bucket C)** — small, contained emission
   extensions, one comparer at a time, same recipe as this phase's 3 stranded comparers.
3. **Bucket C (3 comparers' worth of exceptions)** — no code beyond what Bucket A/B already did to
   those comparers' other metrics; write these up in step 5 as the explicit kept exceptions once
   A/B land for them.
4. **Bucket D (7 comparers)** — the biggest remaining work, deliberately last: each needs its own
   analyzer-specific emission design decision the way `AsyncStateMachineAnalyzer`/
   `HeapTopologyAnalyzer`/`SqlConnectionPoolAnalyzer` did this phase, not a mechanical pass.
