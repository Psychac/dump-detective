# Phase 5 — Observation & Synthesis Layer

Part of [../modularity-plan.md](../modularity-plan.md). Implements the model in
[observation-and-correlation-model.md](observation-and-correlation-model.md), proven against
dump-only sessions. Depends on [phase-4-session-orchestration.md](phase-4-session-orchestration.md).

**Also owns, carried over from Phase 2's deferred scope**: `ObservationStore`
(see [phase-2-artifact-platform.md](phase-2-artifact-platform.md#migration-steps)'s deferred-items
list) — the "handles a full large-dump run within bounded memory" exit criterion below is exactly
the assumption Phase 2 flagged as unvalidated and deferred to here.

## Goal

Migrate analyzers to emit observations, move finding generation from hand-written per-analyzer
generators to synthesis rules, and **collapse the ~30 bespoke trend comparers into one generic
differ**. All of this happens while dumps are still the only source — deliberately, so the model is
validated under known conditions before trace depends on it.

## Why this phase is before trace

The observation model is the fusion substrate. If it lands *with* trace, two unproven things ship
at once and failures are hard to attribute (is the correlation wrong because the model is wrong, or
because trace ingest is wrong?). Landing it first, where existing dump findings provide an exact
expected output, makes it verifiable: **every finding the tool produces today must still be
produced, unchanged, through the new path.**

## Prerequisite — the analyzer-pipeline audit's cleanup

[../analyzer-pipeline-stages-and-leadfinding-dedup.md](../analyzer-pipeline-stages-and-leadfinding-dedup.md)
reaches this phase's conclusion by a different route, and parts of it **gate this phase rather than
follow from it**:

- **Its P0/P1 fixes should already be done** (Hang, Lock Graph, Finalizable Object, Segment
  Reservation, then Crash/Exception, Async Task). They're live correctness bugs and shouldn't wait
  for this migration. Every builder that stops constructing `SectionLeadFinding` inline is one
  fewer judgment site to migrate here. **Done, shipped 2026-09-08.**
- **Stage-1 purity (its Smell A) must land before or with step 1 below.** If domain results still
  carry `MemoryPressureScore`, `GCPressureLevel`, `HealthScore`, `SeverityScore`, `SuspicionScore`,
  or `LeakCandidateRecord.Severity` when analyzers begin emitting observations, judgment exists in
  two places and this phase bakes in the drift it exists to remove. **5 of 6 done, shipped
  2026-09-17** (Hang, GC Root, Memory, Allocation Pattern, Event Leak — each now has a sole-owner
  classifier; see the Smell A table in the audit doc for what stayed vs. what moved). **Leak
  Candidate's `SuspicionScore`/`Severity`/`Classification` deliberately deferred to this phase**,
  not fixed as a same-shaped extraction: it's cross-analyzer synthesis (needs `GCHandleDomainResult`
  and `TimerLeakDomainResult`, not just its own raw facts) baked into stage 1 ahead of this phase's
  machinery. Plan it as part of step 6 below (folding cross-analyzer synthesis into observation
  rules) rather than bolting a classifier onto `LeakCandidateDomainResult` — see "Leak Candidate —
  deferred to Phase 5" in the audit doc for the full reasoning.
- **`ExplainableScoringEngine`** (Reporting layer) is a fourth independent scoring site and must be
  reconciled here, not left standing. **Resolved, not a gate**: confirmed 2026-09-17 that it only
  reads already-generated `FindingRecord.Severity`/`Category`/`Confidence` — a stage-3 executive
  rollup over stage-2 output, not an independent copy of any stage-1 judgment. Already satisfies
  this phase's exit criterion ("reuses synthesis outputs or is gone").

### Before step 2 — synthesis-rule authoring

Not yet gated above; surfaced 2026-09-17 while scoping step 2. Ordered by need — each depends on
the one before it:

1. **`ISynthesisRule` / `ObservationQuery` must actually be designed.** **Done, shipped
   2026-09-17.** Resolved narrower than the model doc's original hybrid lean — see
   [observation-and-correlation-model.md § 7](observation-and-correlation-model.md) for the
   reasoning against a comparator/predicate DSL. Shipped:
   `src/DumpDetective.Reporting/Synthesis/SynthesisEngine.cs` (per-`ObservationType` index built
   once per run, sequential rule execution, per-rule catch-and-log isolation, `Fingerprint`-sorted
   output — no zero-match skip, so a future negative-result rule can still fire), plus
   `DefaultSynthesisRuleFactory`/`ISynthesisRuleCatalog` in
   `src/DumpDetective.Reporting/Services/` mirroring `DefaultAnalyzerFactory`'s existing
   `ActivatorUtilities` pattern exactly. `DefaultSynthesisRuleCatalog` is deliberately empty — no
   rule exists yet — and is *not* wired into `ServiceRegistration.cs`/the CLI pipeline, since there
   is no consumer for its output until a rule and its pipeline node exist; wiring it in now would be
   speculative. A `SynthesisRuleRegistryConformanceTests.SynthesisRuleCatalog_ShouldHaveUniqueRuleIds`
   architecture test guards `RuleId` collisions across the catalog going forward (trivially green
   today, real protection once rules land — see reasoning in the test itself for why this is a
   uniqueness check, not a full versioned JSON registry like the observation-type one).
   `ConfidenceBreakdown.MeasurementCertain` (all caps/factors at 1.0) ships alongside it, so step
   2/6 rules in a dump-only session don't each hand-roll the same boilerplate object ahead of step
   7's real cap wiring. Covered by `tests/DumpDetective.Tests/Unit/Synthesis/SynthesisEngineTests.cs`
   and the architecture test above. Still open, unchanged by this: items 2-3 below.
2. **Freeze the golden-file corpus, and audit registered `ObservationType`s against what
   generators actually read** — originally two separate items, merged and refined 2026-09-18 after
   checking what already exists, which turned out to be more than either assumed: every one of the
   ~30 `IFindingGenerator`s already has
   a `*FindingGeneratorTests.cs` with hand-picked boundary fixtures per threshold constant, and a
   working golden/snapshot mechanism (`Golden/GoldenTestBase.cs`, documented in `Golden/README.md`
   — auto-creates a `.golden` baseline, forces a review-and-commit, diffs on mismatch) already backs
   `ReportSerializer`/`TrendReportComposer`/`HtmlReportRenderer`. Neither #2 nor #3 needs new
   infrastructure — both are cheaper, per-analyzer procedures reusing what's there. Replaced by the
   per-analyzer playbook below, run at the moment that analyzer migrates, never in bulk upfront:

   Lettered, not numbered — this is a sub-procedure run per analyzer, not one of the numbered
   steps in `## Work` below or the numbered prerequisites in this list; reusing digits for both
   made "step 6" ambiguous (item 3 below and step f here used to both claim it).

   a. Open that analyzer's existing `*FindingGeneratorTests.cs`. Its `BuildResult(...)` calls are
      the fixture set — reuse them; only add a new fixture if a threshold constant genuinely has no
      boundary case there yet.
   b. For each fixture, snapshot the **full** `Finding` list (`Title`, `Evidence`, `Recommendation`,
      `Tags`, `MetricValue`, `Severity` — not just the subset today's loose
      `Contains`/`Tags`/`Severity` assertions check) through `GoldenTestBase.ApproveGoldenOutput`,
      under `Golden/Baselines/Synthesis/<AnalyzerName>/`. Commit the generated baselines — this is
      the frozen corpus for that analyzer, and only that analyzer, captured from the pre-migration
      generator.
   c. Cross-check every field the generator reads — branch conditions *and* narrative-interpolated
      values, both required, not just thresholds — against that analyzer's entry in
      `SdkRegistryConformanceTests.KnownEmittedObservationTypes`.
   d. Gap found → extend that analyzer's `Observation` emission (and the registry-conformance
      table) to carry the missing value, before writing the rule. Never have the new rule reach
      back into the old domain result or hardcode the value — either defeats the purity rules step
      1 exists to enforce.
   e. Write the `ISynthesisRule`, run it over the same fixtures, diff against this item's baselines.
   f. Only then retire that analyzer's `IFindingGenerator`.

   **Step f read "Partial" below, for every analyzer, until 2026-09-19, when the blocking gap
   closed:** `AnalyzerFeatureModule.FindingGeneratorType` was a required field with no
   per-analyzer opt-out, so literally deleting an `IFindingGenerator` needed a bigger, separate
   change — `FindingGeneratorType` is now nullable, `PluginCatalogBuilder` resolves it tolerantly
   instead of throwing when the companion class is gone, and a new `SynthesisRuleType` (also
   convention-resolved) lets `StartupValidator`'s "every analyzer needs a finding path" check
   accept a retired generator as long as a rule covers it. Until that gap closed,
   `SynthesisFindingEnricher` ran after `FindingGenerationPipeline` and unconditionally overrode
   that analyzer's findings with the rule's output — proven to reach the real report without
   touching any shared report-pipeline code (Option 1 from the `Finding`-vs-`InsightFinding`
   design discussion) — while the old generator stayed registered, doing now-discarded work. With
   the gap closed, step f is real retirement, done package-wise (one commit per `Plugins.*`
   package, same order as the earlier observation-emission commits): the old generator class, its
   own unit tests, and its `SynthesisRuleParityTests` are deleted together — the parity test's job
   (proving the rule reproduces the old generator exactly) is done and recorded in git history, so
   it doesn't need to keep exercising the live class. `LeakCandidateFindingGenerator` is the one
   exception still standing: `LeakCandidateAnalyzer` has no rule yet (deferred to step 6 below),
   so it has nothing to retire to.

   ### Per-analyzer migration status

   | Analyzer | Steps a-e | Step f | Observation gap | Rule |
   |---|---|---|---|---|
   | `ArrayFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — 2 of 4 branches unobserved, fixed | `ArraySynthesisRule` |
   | `LohFragmentationFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | No — clean | `LohFragmentationSynthesisRule` |
   | `AsyncStateMachineFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — `type.async-state-machine` had no method identity, fixed | `AsyncStateMachineSynthesisRule` |
   | `AsyncTaskFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — 7 of 8 signals unobserved, fixed | `AsyncTaskSynthesisRule` |
   | `FinalizableObjectFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — 2 of 9 signals unobserved, fixed | `FinalizableObjectSynthesisRule` |
   | `GCHandleFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — 5 of 6 signals unobserved, fixed | `GCHandleSynthesisRule` |
   | `GCRootFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — 3 of 4 signals unobserved, fixed | `GCRootSynthesisRule` |
   | `WeakReferenceFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — most of overview + 2 of 4 signals unobserved, fixed | `WeakReferenceSynthesisRule` |
   | `SqlConnectionPoolFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — connection-string label had no home, fixed | `SqlConnectionPoolSynthesisRule` |
   | `SqlCommandFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — TotalCommands/DisposedCount unobserved, fixed | `SqlCommandSynthesisRule` |
   | `SqlTransactionFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — TotalTransactions/DisposedCount/OtherCount unobserved, fixed | `SqlTransactionSynthesisRule` |
   | `DbConnectionFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — type breakdown + closed/broken/other/unknown counts + root-path label unobserved, fixed | `DbConnectionSynthesisRule` |
   | `HttpObjectFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — 2 whole-run counts + generation split + connection-limit + module breakdown unobserved, fixed | `HttpObjectSynthesisRule` |
   | `TimerLeakFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | No — no observation gap at all | `TimerLeakSynthesisRule` |
   | `WcfChannelFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — per-type totals + binding hint + endpoints + whole-run counts unobserved, fixed | `WcfChannelSynthesisRule` |
   | `DominatorFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — always-heuristic retained bytes + highly-referenced/capped whole-run counts + type-hotspot aggregate unobserved, fixed | `DominatorSynthesisRule` |
   | `EventLeakFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — per-group aggregates + whole-run zero/fallback counts unobserved, fixed | `EventLeakSynthesisRule` |
   | `ReferenceChainFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — multi-sample-enriched per-type fields + whole-run scalars + shared-root groups unobserved, fixed | `ReferenceChainSynthesisRule` |
   | `StaticRootFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — significant-subset whole-run scalars unobserved, fixed | `StaticRootSynthesisRule` |
   | `AllocationPatternFindingGenerator` | Done, 2026-09-18 | Done, 2026-09-19 | Yes — size-fraction measures + classification threshold + top-long-lived-type identity unobserved, fixed | `AllocationPatternSynthesisRule` |
   | `GCGenerationFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — count-based whole-run scalars + thresholds + bounded top-allocator/top-accumulator types unobserved, fixed | `GCGenerationSynthesisRule` |
   | `MemoryFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run pressure inputs + single top-1-type identity unobserved, fixed | `MemorySynthesisRule` |
   | `SegmentReservationFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — re-derived pressure-risk inputs + thresholds unobserved, fixed | `SegmentReservationSynthesisRule` |
   | `CrashFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run scalars + per-thread candidate identity unobserved, fixed | `CrashSynthesisRule` |
   | `HeapTopologyFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — per-kind fragmentation + whole-run server-GC/logical-heap-skew scalars unobserved, fixed | `HeapTopologySynthesisRule` |
   | `JitFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run scalars + single top-method identity unobserved, fixed | `JitSynthesisRule` |
   | `ModuleFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run population + 5 new bounded per-module observation types unobserved, fixed | `ModuleSynthesisRule` |
   | `HangFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run thread-pool/task-backpressure scalars + a per-thread deadlock-candidate flag unobserved, fixed | `HangSynthesisRule` |
   | `LockGraphFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run total-held-locks scalar unobserved, fixed | `LockGraphSynthesisRule` |
   | `ThreadFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run triage scalars + finalizer-thread detail unobserved, fixed | `ThreadSynthesisRule` |
   | `ThreadStackClusterFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run diversity scalars + per-cluster signature/framework-pattern text unobserved, fixed | `ThreadStackClusterSynthesisRule` |
   | `BoxingFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run pressure scalars + oversized-value/struct-padding-waste per-type aggregations unobserved, fixed | `BoxingSynthesisRule` |
   | `CollectionFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run waste scalars + exact per-kind/per-element-type waste breakdowns unobserved, fixed | `CollectionSynthesisRule` |
   | `ObjectShapeFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — three already-selected per-type entries + two exact classification-population totals unobserved, fixed | `ObjectShapeSynthesisRule` |
   | `StringFindingGenerator` | Done, 2026-09-19 | Done, 2026-09-19 | Yes — whole-run duplication/LOH/very-long-string/sampling scalars unobserved, fixed | `StringSynthesisRule` |

   ### Per-analyzer notable findings

   Moved to [phase-5-step2-per-analyzer-findings.md](phase-5-step2-per-analyzer-findings.md) on
   2026-09-21, grouped by package with a per-analyzer heading — what step c's audit found and
   step d's fix was, for all 35 migrated analyzers (everything except `LeakCandidateAnalyzer`, carved
   out explicitly below). The table above is the scannable summary; that file is the full record.
3. **Carve out `LeakCandidateAnalyzer` explicitly.** It doesn't emit observations until `## Work`
   step 6 below (see the Stage-1 purity bullet above — distinct from playbook step f just above),
   so its `IFindingGenerator` cannot be retired alongside the rest in this playbook — state this
   so "retire `IFindingGenerator`" doesn't implicitly assume 36/36. **Resolved 2026-09-22**: step
   6.1 shipped — `LeakCandidateAnalyzer` now emits observations, `LeakCandidateSynthesisRule`
   replaced `LeakCandidateFindingGenerator` (deleted), and it was the *last* surviving
   `IFindingGenerator`, so `services.GetServices<IFindingGenerator>()` is correctly empty now.
   "Retire `IFindingGenerator`" is 36/36 as of this change. Details:
   [phase-5-step6-insight-engine-plan.md § 6.1](phase-5-step6-insight-engine-plan.md#61--leakcandidateanalyzer-first-observation-emission--its-synthesis-rule).

Judgment call, not a gate — decide before designing `Finding`/`ConfidenceBreakdown` in step 2/7,
since redesigning after the fact is the expensive path:
[../../ReportStructure/ReportSystemVision.md § 6.2](../../ReportStructure/ReportSystemVision.md)
reframes findings as a `Claim` (structured `assertion`, `support`/`counter`, `falsifiedBy`, split
confidence) well beyond today's `Finding`. Phase 5 doesn't have to build that now — the report is
a later phase — but since `ISynthesisRule` and `ConfidenceBreakdown` are being designed from
scratch in this same step, it's worth deciding whether `Finding` carries a few forward-compatible
stub fields (empty `counter`/`derivedFrom`) now rather than a second rewrite later.

**Decided 2026-09-17: no.** `Finding` ships as already designed in
[observation-and-correlation-model.md § 2](observation-and-correlation-model.md) —
`DerivedFrom` already covers lineage; no `counter`/`falsifiedBy`/`support` stubs added. Reasoning:
there is still zero real `ISynthesisRule` in the codebase to shape those fields against, so adding
them now would be the same speculative-design risk `ObservationQuery.AdditionalPredicate`'s own
remarks already warn against (see phase-1-sdk-review-findings.md item 16) — guessing a `Claim`
grammar ahead of the report phase that actually needs it, rather than letting that phase's real
rule-authoring surface what it needs. Revisit when the report phase (`ReportSystemVision.md`)
actually starts, not before.

## Work

1. **Emit observations alongside domain results**, obeying the purity rules in
   [observation-and-correlation-model.md § 2a](observation-and-correlation-model.md) — raw measures,
   factual observation types, measurement-only confidence. Composite scores that previously lived in
   domain results are *not* ported into observations; they become synthesis-rule outputs.
   **Done, shipped 2026-09-17 across 8 commits** (one per plugin package: Gc, Memory, Threads,
   Infra, Runtime, Types, Async, Leaks) — 35 of 36 analyzers emit observations (`LeakCandidateAnalyzer`
   deliberately deferred to step 6, same reasoning as its Stage-1-purity deferral above), ~50
   distinct `ObservationType`s registered in `observation-type-registry.json`. Infrastructure: a real
   `CollectingObservationSink`/`ArtifactId` now flow through `RuntimeAnalysisContext` →
   `AnalysisContextBuilder` (was `NoOpObservationSink.Instance`/null); the sink was relocated from
   the trace-only project into the SDK so both sides share it. Domain results are untouched (no
   golden-file risk introduced); every emission is scoped to an already-bounded population
   (typed-resource candidate streams, top-N selections, or a single whole-heap aggregate) — never a
   second, unbounded per-object pass. Along the way, found and fixed a real pre-existing gap: several
   analyzer unit tests construct `AnalysisContext` directly without `ArtifactId`, which only surfaced
   once those analyzers started emitting real observations (16 test files fixed across the 8 batches).
2. **Author synthesis rules** replacing each `IFindingGenerator`. Verified by golden-file equality
   against today's findings for the full test corpus — a finding that changes text or severity is a
   bug in this phase, not an improvement.
3. **Retire `IFindingGenerator`** once every generator has an equivalent rule. **Done, 2026-09-19,
   package-wise (8 commits, one per `Plugins.*` package) — 35/36 analyzers.** Mechanism gap closed
   first (`FindingGeneratorType` made nullable, `SynthesisRuleType` added, `PluginCatalogBuilder`/
   `ServiceRegistration`/`StartupValidator` updated to allow and validate the null case — see the
   per-analyzer playbook's step f note above), then the old generator class + its own unit tests +
   its `SynthesisRuleParityTests` deleted together per package. `LeakCandidateFindingGenerator`
   stays registered — no rule exists yet, deferred to step 6.
4. **Generic trend differ.** Implement diffing over `(ObservationType, Subjects)` keyed by
   `TemporalExtent`, dispatching on `MeasureSemantics` (Absolute → delta/%; Rate → acceleration;
   Ratio → point difference; Count → growth-curve fit; Duration → distribution shift). Migrate
   trend comparers one domain at a time, verifying trend-report equality per domain.

   **Done, 2026-09-22.** Scoping work done before writing the differ itself surfaced real findings
   that narrowed this step's target — recorded here so the eventual differ isn't judged against the
   original one-line description above:

   - **Judgment-trend prep shipped first, ahead of the differ**: `FindingLifecycleComparer` (used
     for the New/Persistent/Resolved lifecycle buckets) gained `SeverityChanges` — every persistent
     finding whose severity moved between snapshots, either direction, not just Warning→Critical.
     This retired three independent hand-rolled mechanisms that were each computing some version of
     "did this get worse": `TrendAnalyzer.ComputeNewLeakSignals`/`NewLeakSignal` (hardcoded to 3
     analyzers via now-deleted marker interfaces `IRetainedObjectsByTypeSignal`/
     `IStaticRootsByDescriptionSignal`/`ILeakCandidatesByTypeSignal`), `TrendRegressionDashboardBuilder`'s
     private `BuildSeverityEscalations` re-join, and `TrendReportComposer`'s inline `hasEscalations`
     LINQ. `TrendRegressionDashboardBuilder`'s T3c "New Leak Signals" table is gone outright — it
     was a strict subset of what the now-generic T3a/T3b already show for every analyzer.
     `FindingLifecycleComparer` had zero test coverage before this; it has real coverage now
     (`tests/DumpDetective.Tests/Unit/Reporting/FindingLifecycleComparerTests.cs`).
   - **Every one of the 36 comparers' metrics was mechanically classified headline (unscoped,
     `AnalyzerMetric.Scope is null`) vs. scoped (per-type/per-entity)**: 11 comparers
     (`AsyncStateMachine`, `WeakReference`, `SqlConnectionPool`, `EventLeak`, `ReferenceChain`,
     `LohFragmentation`, `SegmentReservation`, `HeapTopology`, `Jit`, `LockGraph`,
     `ThreadStackCluster`) are 100% headline. The other 25 mix both; none are pure-scoped.
   - **Scoped metrics are not one problem.** Spot-checking what backs them today split into three
     structurally different cases: (a) a per-type `Observation` already exists (e.g.
     `GCGenerationAnalyzer`'s `gc.generation-top-allocator`/`-top-accumulator`) — a future generic
     scoped differ could reach these via `Subjects`; (b) only a per-*object* `Observation` exists
     (e.g. `GCHandleAnalyzer`'s `handle.pinned`, one per pinned handle) and the comparer sums by
     type name at comparison time — a materially different, harder mechanism (grouping, not lookup)
     than the headline case; (c) no `Observation` backs it at all (`MemoryAnalyzer`'s per-type
     top-10 and size-bucket histogram — a Stage-1 gap, not a differ problem).
   - **Decision: this step's generic differ targets headline metrics only.** Scoped/per-entity
     trending (all three sub-cases above) is real, separately-scheduled follow-up work, not folded
     into step 4 and not a "step 5 exception" either — forcing it in here is exactly how a bounded
     step becomes an open-ended one. This also isn't a new split invented for this decision: the
     codebase already treats headline and scoped metrics as two different mechanisms today
     (`TrendAnalyzer.ExtractTimeline` vs. `ExtractScopedTimeline`), so the migration follows an
     existing seam rather than cutting a new one.
   - **Consequence for "migrate comparers one domain at a time" above**: only the 11 all-headline
     comparers retire completely. The other 25 keep existing, trimmed to just their scoped
     `ExtractMetrics`/`Compare` branches once their headline branches are reproduced by the generic
     differ and verified equal — a partial retirement, mirroring step 3's `FindingGeneratorType`
     nullability pattern (a comparer with only scoped work left is not a bug, it has a real job).
   - **Registry `direction` field added and rolled out across all 8 packages, done 2026-09-20** —
     new public `DumpDetective.Sdk.Observations.MeasureDirection` enum (`HigherIsWorse`/
     `LowerIsWorse`/`Neutral`/`ContextDependent`), declared per `(ObservationType, measure key)` in
     `observation-type-registry.json` next to that key's existing `unit`/`semantics` (not a field on
     `Measure` itself — direction is domain knowledge about a measure's meaning, not per-instance
     data). Mined from the 36 comparers' existing `MetricTrendDirection` values, not guessed: a
     measure with no matching comparer metric is left without a `direction` key rather than
     defaulted, since an absent key means "not yet classified" and `neutral` is itself an assertion.
     `SdkRegistryConformanceTests` guards it two ways — a hand-maintained expected table
     (`KnownMeasureDirections`, same style as the existing `KnownEmittedObservationTypes`) catches
     drift, and a second fact asserts every declared `direction` anywhere in the registry parses as
     a real `MeasureDirection`. registryVersion 1.54.0 → 1.62.0 (one bump per package: Memory → Gc →
     Threads → Async → Types → Leaks → Runtime → Infra, same order as the earlier
     observation-emission and step-3 retirement commits). Final count: 149 measures annotated across
     25 observation types. Sharper finding than the scoping work above anticipated: **6 comparers
     back zero registry measures despite being 100% headline by `AnalyzerMetric.Scope` convention**
     — `AsyncStateMachineTrendComparer`, `ArrayTrendComparer`, `HeapTopologyTrendComparer`,
     `SqlConnectionPoolTrendComparer`, and `TimerLeakTrendComparer` each trend run-wide totals whose
     analyzer never actually promoted a matching whole-run aggregate to an `Observation` — verified
     directly against each analyzer's source, not assumed; `LeakCandidateTrendComparer` is a 7th,
     already-known case (zero observations emitted at all, deferred to step 6). "Headline by the
     comparer's `Scope` convention" and "backed by a headline `Observation`" turned out to be
     different claims throughout — every package left some residue of registry measures with no
     comparer opinion to mine, and comparer metrics with no registry measure to attach to (derived/
     composite scores like `alloc.gc.pressure`, percent-of-total figures computed only at
     comparison time, values only reachable by summing scoped per-type data). See the CHANGELOG
     entries in `schema/DumpDetective.Schema/CHANGELOG.md` (1.55.0 through 1.62.0) for the
     per-package detail.
   - **The differ itself, done 2026-09-20** — `ObservationTrendDiffer`
     (`src/DumpDetective.Analysis/Trend/ObservationTrendDiffer.cs`), pairwise over headline
     Observations, dispatching on `MeasureSemantics`, direction sourced from
     `MeasureDirectionRegistry`. One real behavioral difference implemented so far: `Ratio`
     measures diff as a point difference, never percent-of-a-percent — the concrete,
     now-demonstrated-in-a-test bug fix (`AllocationPatternTrendComparer.Compare`'s
     `gen2.count-fraction`: 10%→15% reports "+50%" today via `Absolute`-shaped math, should be "+5
     points"). `Rate`/`Count`/`Duration`'s richer per-`MeasureSemantics` treatment (rate-of-rate
     acceleration, growth-curve fit across ≥ 3 anchors, distribution shift) needs a real temporal
     series, not two snapshots — honestly out of reach for a *pairwise* differ, so those three fall
     back to `Absolute`-shaped delta+percent rather than faking series-aware math; a genuinely
     series-aware differ over `TrendAnalyzer.ExtractTimeline`'s shape is separate, not-yet-built
     work, not scoped into this step. `MeasureDirectionRegistry`
     (`src/DumpDetective.Sdk/Observations/MeasureDirectionRegistry.cs`) is the new runtime
     companion the registry rollout above needed all along — same split as `CapabilityVocabulary`/
     `capability-registry.json` (a JSON file can't be queried without a loader this project has no
     precedent for building), so the C# table is what the differ actually reads and the JSON stays
     the checked-in, test-cross-checked mirror. Proven in isolation
     (`tests/DumpDetective.Tests/Unit/Analysis/ObservationTrendDifferTests.cs`, 7 tests) — **not yet
     wired into `TrendAnalyzer`/`TrendReportComposer`**, matching this phase's mechanism-first
     discipline (same shape as step 3 landing `FindingGeneratorType` nullability before any
     generator was actually deleted).
   - **Wired in, 8 of the 11 all-headline comparers retired, done 2026-09-20.** `TrendDumpExecution`/
     `TrendBuildReportNode` now thread each artifact's Observations alongside its
     `AnalysisSnapshot` (not added as a new field on `AnalysisSnapshot` itself — `Core.Models` has
     zero project references today, not even to `Sdk`, and this wasn't the moment to start that);
     `TrendAnalyzer.CompareAll`/`CompareSeries`/`ExtractTimeline` all gained optional
     Observation-list parameters routing through `ObservationTrendDiffer` via new
     `ObservationDifferAnalyzerOwnership` (the `ObservationType → AnalyzerName` map the flat differ
     output needs, since one shared differ instance covers every retired analyzer — unlike
     `FindingGeneratorType`/`SynthesisRuleType` there's no per-analyzer companion type to record on
     the module for this). `AnalyzerFeatureModule.TrendComparerType` is nullable now, same shape as
     step 3, `IsShapeValid()` allowing null with no "or" requirement (nothing per-module to check —
     `StartupValidator`'s coverage check is the real backstop). WeakReference/EventLeak/
     ReferenceChain/LohFragmentation/SegmentReservation/Jit/LockGraph/ThreadStackCluster deleted —
     class, DI registration, and dedicated tests together.
   - **The remaining 3 all-headline comparers — retired 2026-09-21.** AsyncStateMachine,
     HeapTopology, and SqlConnectionPool were each found during the registry rollout to back zero
     registry measures — their analyzer never promoted a whole-run aggregate to an `Observation` at
     all. Resolved by extending emission rather than accepting the gap
     (schema/DumpDetective.Schema/CHANGELOG.md 1.69.0): `AsyncStateMachineAnalyzer` and
     `SqlConnectionPoolAnalyzer` each gained a new whole-run, no-entity-subject observation type
     (`type.async-state-machine-population`, `infra.sql-connection-pool-population`) built from
     totals each analyzer already computed; `HeapTopologyAnalyzer`'s existing `heap.topology-population`
     gained 9 measures, all exact sums over the 5 `heap.segment-kind-composition` rows it already
     emits — no new heap traversal in any of the three. All 3 comparer classes (and
     `AsyncStateMachineTrendComparer`'s dedicated test, the only one of the three with one) deleted;
     `ObservationDifferAnalyzerOwnership` and `PluginCatalogBuilderTests`'s expected-coverage list
     extended to match. 11 of 11 all-headline comparers now retired.
   - **The 24 mixed comparers (headline + scoped metrics) — audited 2026-09-21, all retired
     2026-09-22.** Full per-comparer headline/scoped split and Observation-registry backing status
     recorded in
     [phase-5-step4-comparer-retirement-audit.md](phase-5-step4-comparer-retirement-audit.md), once
     it didn't fit inline — the audit doc's own 4 buckets (A: fully backed, B: small emission
     extension, C: classifier-derived composite exceptions, D: needed a real new whole-run
     Observation type) are all closed: Buckets A/B/D's comparers mechanically trimmed onto
     `ObservationTrendDiffer`, Bucket C's 4 composite metrics recorded as permanent exceptions under
     step 5 below. Bucket D, expected to be the heaviest lift, turned out lighter in practice — every
     item's missing totals were already sitting in locals or on the constructed domain result, no
     genuinely new heap traversal anywhere. `ObservationDifferAnalyzerOwnership` now covers all
     retired analyzers; every comparer still standing keeps only its scoped and (for the 3 Bucket C
     comparers) composite branches.
   - **Found and fixed a live, unrelated CLI-startup bug along the way**: `StartupValidator`'s
     step-3 finding-generator coverage check joined retired analyzers to real `IAnalyzer.Name` via
     `AnalyzerFeatureModule.DisplayName` — not guaranteed to match (`ThreadStackClusterAnalyzer`:
     DisplayName "Thread Stack Cluster Analysis" vs. Name "Thread Stack Signature Clustering", a
     real, live divergence, not hypothetical). The real CLI would throw "Missing finding generators
     for analyzers: Thread Stack Signature Clustering" at startup today — never caught, since
     `ValidateRegistrations` had zero direct test coverage before this. Fixed by joining on
     `AnalyzerType` (real `Type` identity) instead of the display string, same fix applied to the
     new trend-comparer check before it shipped with the identical bug.
     `ServiceRegistrationTests.ValidateRegistrations_DoesNotThrow_ForTheRealProductionCatalog` now
     exercises the real production DI graph end to end — the test that would have caught this the
     first time.
5. **Keep the genuine exceptions.** Distinct from the headline/scoped split above — this is about
   *headline* metrics where "worse" still isn't monotonic in the measure (thread count, handle
   count, where both directions can be bad in context), not about scoped metrics (which are handled
   by whatever the scoped-trending follow-up becomes, not by this step at all). These stay as
   explicit rules. Expect roughly a handful, not thirty; if the count stays high, the measure
   semantics are under-specified and that's the real bug.
   - **Done, 2026-09-21 — Bucket C of the step 4 mixed-comparer audit.** The genuine exceptions
     Bucket A/B's mechanical work actually turned up weren't the anticipated "non-monotonic
     direction" case above (no comparer in the 24-mixed-comparer audit needed that treatment) —
     they were a narrower, different category: **classifier-derived composite scores**, banned from
     raw `Observation` measures outright by the purity rule (§2a of
     observation-and-correlation-model.md), not just hard to classify a direction for. A composite
     score computed from other fields (e.g. a weighted severity/pressure formula) isn't itself a raw
     domain fact, so `ObservationTrendDiffer` can never diff it no matter how emission is extended —
     unlike the headline/scoped split, more emission work can't close this one. 3 comparers, 4
     metrics total, all confirmed during
     [phase-5-step4-comparer-retirement-audit.md](phase-5-step4-comparer-retirement-audit.md)'s
     Bucket A/B mechanical trims (the comparer stays registered in the DI comparer list, keeps only
     the composite branch hand-written in `Compare`/`ExtractMetrics`, while
     `ObservationDifferAnalyzerOwnership` routes every other now-backed headline metric through the
     differ — same "comparer + differ share one analyzer name" composition Bucket A first proved):
     - `HangTrendComparer.hang.health.score` — `HangHealthClassifier.ComputeHealthScore` (also the
       comparer's only `LowerIsWorse` metric, unlike the rest of `runtime.hang-pressure`).
     - `GCRootTrendComparer.gcroot.top.severity.score` — `GCRootSeverityClassifier.ComputeSeverity`.
     - `AllocationPatternTrendComparer.alloc.gc.pressure` /
       `AllocationPatternTrendComparer.alloc.promotion.pressure` —
       `AllocationPatternPressureClassifier.ClassifyPressure` /
       `.ComputePromotionPressureScore`.

     No code changes from this step itself — the trims that made each comparer's composite branch
     the *only* one left were already done as part of step 4's Bucket A/B work; this is the explicit
     write-up marking all 4 as permanent, not a to-do. If a future analyzer's headline metric turns
     out to be genuinely non-monotonic (the case this step was originally scoped for) rather than
     classifier-derived, it belongs in this same exceptions list — the mechanism (comparer keeps a
     hand-written branch, stays registered for it) is identical either way, only the *reason* differs.
6. **Fold in `InsightEngine`.** Its cross-analyzer synthesis becomes multi-observation synthesis
   rules — same mechanism, wider match, no separate engine. **Also picks up Leak Candidate's
   deferred Stage-1-purity fix here**: `SuspicionScore`/`Severity`/`Classification` join
   `LeakCandidate`'s own per-type raw facts with `GCHandleDomainResult.TopPinnedTargetTypes` and
   `TimerLeakDomainResult.LogicalTimerCount` as a synthesis rule, replacing the informal
   cross-analyzer read `LeakCandidateAnalyzer` does inside its own stage 1 today. See the audit
   doc's "Leak Candidate — deferred to Phase 5" section. **Full implementation plan, split out
   2026-09-22 once it stopped fitting inline (same reason step 2 and step 4 got their own docs):
   [phase-5-step6-insight-engine-plan.md](phase-5-step6-insight-engine-plan.md).** **Done
   2026-09-26** — 38 of 39 rules migrated, 1 dropped by user call, `InsightEngine` deleted; details
   in that doc.
6a. **Sections derive from observations, not from domain results.** **Done 2026-09-24** (step 6
   doc § 6.3). This resolves the open question
   the earlier draft deferred, and it's the audit's central complaint: as long as
   `IAnalyzerSectionBuilder` reads `AnalyzerDomainResult` independently of the finding path, two
   passes over the same data can disagree — which is already happening in 6 of 8 audited builders.
   After this step, section builders are **presentation-only**: they shape already-decided
   observations and findings into blocks/tables/charts and never re-derive severity, banding, or
   row selection. `SectionLeadFinding` is always derived from the top-severity finding, never
   constructed inline. Implementation steps in
   [phase-5-step6-insight-engine-plan.md § 6.3](phase-5-step6-insight-engine-plan.md#63--section-builders-become-presentation-only-6a).
6b. **Consolidate the confidence ladders.** **Done 2026-09-24** (step 6 doc § 6.4) — into
   Reporting's `ConfidenceLadder`, not `ConfidenceBreakdown` as written below; that doc explains
   why. The three near-identical band/symbol tables
   (`SectionBuilderBase`, `ReportSectionAssembler.NormalizeSectionContractSlots`,
   `LeakAnalysisSectionBuilder`) collapse into `ConfidenceBreakdown` — though per the audit this is
   worth doing *now* rather than waiting for this phase. Implementation steps in
   [phase-5-step6-insight-engine-plan.md § 6.4](phase-5-step6-insight-engine-plan.md#64--consolidate-confidence-ladders-6b).
7. **Confidence plumbing.** Implement `ConfidenceBreakdown` with the caps from
   [observation-and-correlation-model.md § 4](observation-and-correlation-model.md) — identity
   fidelity, temporal alignment, capability fidelity. In a dump-only session most caps are 1.0, but
   building them now means Phase 7 turns them on rather than inventing them.
   **Done, 2026-09-26**, in two commits:
   - **Parity first — fixed a live regression.** Step 2 left every rule at
     `ConfidenceBreakdown.MeasurementCertain`, and `FindingSynthesisAdapter` passed its 1.0 straight
     through, so every finding reached the report at confidence 1.0. Before the phase, most
     generators left the score unset (`InsightFinding`'s severity default: 0.9/0.7/0.5), and five
     computed one from evidence. That had silently changed `ExplainableScoringEngine`'s weighting,
     `ActionPriorityService`'s base confidence, and `CorrelationBuilder`'s 0.6 filter.
     - The adapter now passes `null` for an unlimited breakdown, which restores the severity default.
     - Timer/StaticRoot/Dominator/EventLeak: the population observations carry the top entity's raw
       root-path facts; `RootPathEvidenceStrength` reproduces the retired `EvidenceConfidence`.
     - Crash: `runtime.crash-thread-candidate` carries the stack-trace inference tier, which restores
       the weighted confidence and the tier caveats.
     - Pinned to the retired formulas by `FindingConfidenceParityTests`. Registry 1.89.0.
   - **Then the calculation.** `ConfidenceCalculator` (SDK) implements the § 4 scoring.
     `SynthesisEngine` applies it to every finding from its `DerivedFrom` lineage, so rules only
     state how well their own evidence supports the claim. What the calculation does is recorded in
     [observation-and-correlation-model.md § 4](observation-and-correlation-model.md) ("As
     implemented").
     - **Intended output change:** a finding whose lineage includes an observation with confidence
       below 1.0, or one from a degraded analyzer run, now reports that lower confidence instead of
       the severity default. Fully confident dump-only lineage is unchanged.
     - **Measured 2026-09-26** on the 3.35 GB reference dump
       (`FindingConfidenceRealDumpTests`, opt-in). 36 analyzers ran with 0 failures, emitting
       97,408 observations: 3 degraded, 14,095 below confidence 1.0.
       - **3 of 83 findings moved**, all from Finalizable Object Analysis, all Critical, all
         0.9 → 0.7. In each case some finalizer-queue entries had no exact dominator-retained size,
         so the analyzer marks the whole observation 0.7.
       - `queue-retained-bytes` (a claim about retained bytes): the drop is correct.
       - `critical-finalizer-accumulation` (a count claim) and `queue-backlog` (a count claim that
         also cites retained bytes): the drop is mostly spurious. Observation confidence covers the
         whole observation, so an exact count inherits the retained-bytes estimate's uncertainty.
         The analyzer's own comment on `finalizer.queue-population` already notes this convention.
       - **Fixed for Finalizable Object only** (registry 1.90.0). Both queue observations are
         confidence 1.0 and carry an exactness flag (`is-retained-exact` /
         `is-retained-estimate-partial`). `FinalizableObjectSynthesisRule` gives 0.7 only to
         `queue-retained-bytes`, from that flag. Re-measured: the two count findings are back to
         0.9, `queue-retained-bytes` stays 0.7, and lineage now moves 0 of 83 findings.
       - **Not done:** per-measure confidence across the SDK. Other observations below 1.0 on this
         dump back retained-bytes claims, where the lower confidence is correct.
     - **Still open: conflict penalty** — fixed at 1.0 until a rule detects contradicting
       observations.

## Exit criteria

Checked 2026-09-26 against the code and the 3.35 GB reference dump
(`FindingConfidenceRealDumpTests`, opt-in). Status is on each item.

- Every analyzer emits observations (**Met — 36/36**: `LeakCandidateAnalyzer` joined in step 6.1; re-run 2026-09-26: 97,408 observations); `ObservationStore` handles a full
  large-dump run within bounded memory. **Measured, not just tested, 2026-09-17** against the
  3.35 GB reference dump: 36 analyzers emitted **81,316 observations** in 61 s (heap index build
  0.55 s). `finalizer.queue-entry` alone accounts for 68,576 of those (84%) — the one analyzer that
  deliberately emits its whole, uncapped population (see its own step-1 remarks); every other type
  is in the low thousands or less. Managed-memory delta across the run (forced GC before/after) was
  ~1.85 GB, but that figure is an upper bound on the observations themselves — it includes every
  analyzer's own (unchanged) domain results and working memory, not an isolated observation-only
  measurement. Conclusion: an in-memory `List<Observation>` (`CollectingObservationSink`) is
  sufficient at this scale — nowhere near the "analyzer emits a million observations" failure mode
  § below warns about — so the disk-backed `ObservationStore` design remains deferred, not built on
  a guess. See `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/ObservationVolumeRealDumpTests.cs`.
- Findings produced via synthesis rules are **byte-identical** to pre-phase findings across the
  golden corpus. **Met for analyzer findings; checked end to end 2026-09-26.**
  - The per-analyzer baselines were deleted with the generators in step 3, by design, so this was
    re-checked once instead: the last pre-phase commit `64943407` and HEAD, each run once on the
    3.35 GB reference dump (CrashAnalysis profile) and diffed on the report JSON.
  - **Analyzer findings:** 63 in each. 62 are identical in severity, title, details,
    recommendation, tags and category. The two differences are both deliberate:
    - The LOH finding was reworded; step 6.3 moved the builder's wording into the rule.
    - `queue-retained-bytes` confidence went 0.9 → 0.7 (step 7).
  - **Insights:** the pre-phase report carried none; it dropped them, and only the console listed
    its 19. HEAD fires the same 19, except that the one rule dropped by user call is gone and
    `JitModuleHotspotInsightRule` now fires (see `docs/known-issues.md`). With insights in the
    report, top actions and correlation events include them.
  - **Two regressions found, both in insight presentation:** see `docs/known-issues.md`.
- Trend reports identical, with ≤ ~5 bespoke comparers remaining and each justified in writing.
  **Met for headline metrics only.**
  - Step 5 lists 3 composite-score comparers as the only hand-written headline branches. There's a
    fourth it missed: `LeakCandidateTrendComparer`'s `leak.candidates.top.score` trends the stage-1
    `SuspicionScore` (see the Smell A item below).
  - 22 more comparer classes survive for scoped (per-type/per-entity) metrics, which step 4
    deliberately deferred to separate follow-up work. That is 25 classes against a target of about 5.
- `IFindingGenerator` and `InsightEngine` no longer exist as separate concepts. **InsightEngine
  met; `IFindingGenerator` not.**
  - `InsightEngine` is gone. The remaining `"InsightEngine"` strings are the analyzer name that
    cross-analyzer findings report under.
  - `IFindingGenerator` has zero implementations (it's `internal`, so plugins can't add one), but
    its plumbing is still live and always empty: `FindingGenerationPipeline`,
    `AnalyzerFeatureModule.FindingGeneratorType`, DI registration, and `StartupValidator`'s check.
  - **Open:** delete that plumbing.
- **No section builder constructs `SectionLeadFinding` inline**, and no builder contains a
  threshold, band, or severity comparison — verifiable by grep, and worth encoding as an
  architecture-conformance rule so it can't regress. **Lead finding met; thresholds not.**
  - `FitnessEnforcementTests` guards `new SectionLeadFinding(` and `Severity: "` only.
  - Judgment still in about 10 builders:
    - LohFragmentation: 30%/15% bands and an 80% small-gap check.
    - LockGraph: recursion and deadlock thresholds.
    - TimerLeak: a hard-coded 50, duplicating the rule's `QueuePressureInfoThreshold`.
    - Array and String: size and coverage cut-offs.
    - `SectionBuilderBase.Interpret` tier banding, used by Dominator.
    - `ConfidenceScoring` in Dominator, GCRoot, LeakCandidate and String.
  - Grouping by an already-decided finding severity (ExecutiveSummary, FindingNarrative,
    Insights) is presentation, not judgment.
  - **Open.**
- No `AnalyzerDomainResult` carries a composite score, `FindingSeverity`, or classification enum
  (audit Smell A closed). **Leak Candidate fixed 2026-09-26; one item open.**
  - **Leak Candidate — done.** `LeakCandidateRecord` no longer carries
    `SuspicionScore`/`Severity`/`Classification`/`RootKind`, and the domain result no longer carries
    `CandidatesByClass`. They now carry only raw inputs (`IsPinned`, `IsDependent`,
    `LogicalTimerCount`). `LeakCandidateClassifier` (`Plugins.Leaks/Utilities`) is the sole owner;
    the analyzer, section builder, trend comparer and `LeakCandidateSynthesisRule` all call it.
    - The rule's own verbatim copy of `Classify`/`Score`/`GetSeverity` is gone.
    - The analyzer still ranks by score internally, because it picks the top 20 for root-chain
      enrichment. It still reads the GC Handle and Timer results through its options to do that;
      that cross-analyzer read stays in stage 1 for this bounded selection.
    - Output is the same, except that the "groups by leak class" table now breaks count ties by
      class order. There are no ties on the reference dump.
  - **Open: Event Leak.** `EventLeakGroupSnapshot.MaxSeverityScore` is a composite that is also
    emitted as the `event.publisher-leak-group` measure `max-severity.score`, against the § 2a
    purity rule. It's a streaming max over every scanned instance, so a rule can't recompute it
    from bounded observations. Fixing it needs a design decision.
  - `GCPressureLevel` is not a violation: it's the return type of the sole-owner
    `AllocationPatternPressureClassifier`, not a domain-result field. (An earlier version of this
    item called it dead code; that was wrong.)
- `ExplainableScoringEngine` either reuses synthesis outputs or is gone — not a surviving
  independent scoring path. **Met** (see the prerequisites section: it reads finding severity and
  confidence only).
- Full observation lineage: every finding resolves to observations → analyzer → capabilities →
  artifact. **Met at the `Finding` level.**
  - Reference dump: all 83 findings resolve fully.
  - `AnalyzerFailuresInsightRule` has an empty `DerivedFrom` by design (pipeline metadata, not an
    observation). It didn't fire on this run.
  - Lineage stops at the report: `FindingSynthesisAdapter` doesn't carry `DerivedFrom` into
    `InsightFinding`. Closing that is report-phase work.

## Risk / effort

High effort, medium risk — mitigated almost entirely by the equality requirement. Because expected
output is exactly known, this is verifiable in a way most of the plan isn't.

Two real risks:
- **Observation volume on large dumps.** A dump with 40 M objects could produce far more
  observations than expected if analyzers emit per-object. Guidance: observations are *conclusions*,
  not records — per-object data stays in the index and is referenced via `EvidenceRef`, never
  copied into observations. If an analyzer wants to emit a million observations, that's a modeling
  error. **Measured, not just guarded against, 2026-09-17**: 81,316 observations on the 3.35 GB
  reference dump (see the exit-criteria entry above for the full breakdown) — three orders of
  magnitude below the "40 M objects" failure case this risk describes.
- **Temptation to improve findings while rewriting them.** Every "while we're here, this wording is
  better" breaks the equality check that makes this phase safe. Improvements land *after*, as
  separate changes.
