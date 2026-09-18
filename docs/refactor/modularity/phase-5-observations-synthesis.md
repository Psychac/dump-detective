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

   **Why step f reads "Partial" below, for every analyzer, until a separate fix lands:**
   `AnalyzerFeatureModule.FindingGeneratorType` is a required field with no per-analyzer opt-out,
   so literally deleting an `IFindingGenerator` needs a bigger, separate change (touching
   `PluginCatalogBuilder` and every module registration). Until then, `SynthesisFindingEnricher`
   runs after `FindingGenerationPipeline` and unconditionally overrides that analyzer's findings
   with the rule's output — proven to reach the real report without touching any shared
   report-pipeline code (Option 1 from the `Finding`-vs-`InsightFinding` design discussion) —
   while the old generator stays registered, doing now-discarded work.

   ### Per-analyzer migration status

   | Analyzer | Steps a-e | Step f | Observation gap | Rule |
   |---|---|---|---|---|
   | `ArrayFindingGenerator` | Done, 2026-09-18 | Partial (see above) | Yes — 2 of 4 branches unobserved, fixed | `ArraySynthesisRule` |
   | `LohFragmentationFindingGenerator` | Done, 2026-09-18 | Partial (see above) | No — clean | `LohFragmentationSynthesisRule` |
   | `AsyncStateMachineFindingGenerator` | Done, 2026-09-18 | Partial (see above) | Yes — `type.async-state-machine` had no method identity, fixed | `AsyncStateMachineSynthesisRule` |
   | `AsyncTaskFindingGenerator` | Done, 2026-09-18 | Partial (see above) | Yes — 7 of 8 signals unobserved, fixed | `AsyncTaskSynthesisRule` |

   **`AsyncTaskFindingGenerator` — notable findings:**
   - Step c's audit found the analyzer emitted only `type.orphaned-task` (one per orphaned task,
     Phase 5 step 1) — the largest gap found so far in this playbook. Seven of the old generator's
     eight signals (cycle deadlock, Gen2/LOH pending tasks, unresolved TCS in Gen2/LOH, pending VTS
     in Gen2/LOH, faulted tasks, continuation-chain depth, high pending count) read
     `AsyncTaskDomainResult` fields with no observation backing at all.
   - Step d's fix: one new whole-run `async.task-population` observation (no entity subject — same
     "whole-process scalar" pattern as `gc.generation-composition`), carrying all thirteen measures
     those seven signals need. `type.orphaned-task` stays as-is; the rule reconstructs `OrphanedTasks`
     by counting matched instances rather than summing a measure.
   - Unlike `AsyncStateMachineSynthesisRule`, no re-ranking question arises: every signal but
     `orphaned` reads a single observation instance's scalar measures directly, and `orphaned` is a
     plain count, not a ranked list.
   - The domain result's own remarks note TCS/VTS analysis runs independent of the task scan (a heap
     can have TCS/VTS instances with zero `Task` objects), so `async.task-population` is emitted on
     both the normal path and the `total == 0` early-return (`EmptyWith`) path — omitting it there
     would have silently dropped the `tcs-unresolved-oldgen`/`vts-pending-oldgen` signals for that
     case.

   **`AsyncStateMachineFindingGenerator` — notable findings:**
   - Step c's audit found `type.async-state-machine`'s only `Subject` was the state-machine's own
     `TypeRef` — `DeclaringType`/`OriginatingMethod` existed only inside a private regex parser,
     never surfaced. That knocked out 3 of 4 branches (high-count's "top method", the entire
     fire-and-forget branch, async-void's "top method"), since all three need method identity.
   - Step d's fix: a second `Subject` on the observation — a `Low`-fidelity `MethodRef` (no real
     parameter signature is derivable from a type name alone, so the signature is left empty
     rather than guessed) — instead of duplicating the private parser into the rule.
   - Unlike `ArraySynthesisRule`, this rule **must** re-rank rather than trust emission order:
     checked (not assumed) that `AsyncStateMachineAnalyzer` emits inline per type, before its own
     post-loop sorts run — the opposite of `ArrayAnalyzer`'s structure, where emission happens in a
     separate pass over already-sorted lists. Different analyzers, different order guarantees;
     each has to be checked on its own.
   - The parity test surfaced that several of the old generator's own unit-test fixtures set
     `TotalStateMachines` and `SuspendedMethodMap` to independent, mutually inconsistent values
     (e.g. 12,000 suspended instances of one method with the total left at 0) — achievable only
     because the old generator reads them as separate fields. Real production can't produce that
     combination (both are exhaustive sums over the same type population), so the parity fixtures
     were rebuilt consistent with that coupling rather than reproducing the impossible combination.

   **`ArrayFindingGenerator` — notable findings:**
   - Step c's audit found `ArrayAnalyzer` only backed 2 of 4 finding branches with observations
     (LOH pressure, multi-dim, pinned-array accumulation had none) — despite its own doc comment
     already flagging the gap.
   - Step d's fix: extended `type.array-composition` with exhaustive per-type `loh.*`/`pinned.*`
     measures (summed in the rule, not a new whole-run scalar), plus two new bounded per-entry
     types (`heap.large-array`/`heap.pinned-array`).
   - Writing the rule surfaced two things this playbook hadn't anticipated: `Finding` itself was
     missing `Analyzer`/`Category`/`Tags`/`MetricValue`/`MetricUnit` — fields the report pipeline
     (`ActionPriorityService`, `CorrelationBuilder`, several section builders) actually reads —
     and the old generator trusted `ArrayDomainResult`'s pre-sorted lists rather than sorting
     itself, so the rule's initial explicit re-sort was a real (if invisible-on-real-data)
     behavior change, caught by the parity test itself.

   **`LohFragmentationFindingGenerator` — notable findings:**
   - Chosen deliberately as a contrast to Array: smallest generator in the set (one finding, two
     fields), to check the playbook isn't only exercised by gap-heavy cases.
   - `heap.loh-fragmentation` is emitted per segment-kind row (Large/Pinned, ≤2), each carrying
     that kind's own `fragmentation.percent` — reconstructing the old generator's combined-total
     percent means summing `total.bytes`/`free.bytes` across matched rows and recomputing, never
     averaging the per-kind percentages (two kinds at 50%/10% with unequal segment counts do not
     average to 30%). A fixture exercises exactly that combination.
   - Confirmed the enricher wiring generalizes for free: adding the rule to
     `DefaultSynthesisRuleCatalog` was the only change needed to reach the real report — no
     `RunAnalyzersNode`/`ServiceRegistration.cs` edits this time.
3. **Carve out `LeakCandidateAnalyzer` explicitly.** It doesn't emit observations until `## Work`
   step 6 below (see the Stage-1 purity bullet above — distinct from playbook step f just above),
   so its `IFindingGenerator` cannot be retired alongside the rest in this playbook — state this
   so "retire `IFindingGenerator`" doesn't implicitly assume 36/36.

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
3. **Retire `IFindingGenerator`** once every generator has an equivalent rule.
4. **Generic trend differ.** Implement diffing over `(ObservationType, Subjects)` keyed by
   `TemporalExtent`, dispatching on `MeasureSemantics` (Absolute → delta/%; Rate → acceleration;
   Ratio → point difference; Count → growth-curve fit; Duration → distribution shift). Migrate
   trend comparers one domain at a time, verifying trend-report equality per domain.
5. **Keep the genuine exceptions.** Some comparers encode domain knowledge the generic differ
   can't — cases where "worse" isn't monotonic in the measure (thread count, handle count, where
   both directions can be bad in context). These stay as explicit rules. Expect roughly a handful,
   not thirty; if the count stays high, the measure semantics are under-specified and that's the
   real bug.
6. **Fold in `InsightEngine`.** Its cross-analyzer synthesis becomes multi-observation synthesis
   rules — same mechanism, wider match, no separate engine. **Also picks up Leak Candidate's
   deferred Stage-1-purity fix here**: `SuspicionScore`/`Severity`/`Classification` join
   `LeakCandidate`'s own per-type raw facts with `GCHandleDomainResult.TopPinnedTargetTypes` and
   `TimerLeakDomainResult.LogicalTimerCount` as a synthesis rule, replacing the informal
   cross-analyzer read `LeakCandidateAnalyzer` does inside its own stage 1 today. See the audit
   doc's "Leak Candidate — deferred to Phase 5" section.
6a. **Sections derive from observations, not from domain results.** This resolves the open question
   the earlier draft deferred, and it's the audit's central complaint: as long as
   `IAnalyzerSectionBuilder` reads `AnalyzerDomainResult` independently of the finding path, two
   passes over the same data can disagree — which is already happening in 6 of 8 audited builders.
   After this step, section builders are **presentation-only**: they shape already-decided
   observations and findings into blocks/tables/charts and never re-derive severity, banding, or
   row selection. `SectionLeadFinding` is always derived from the top-severity finding, never
   constructed inline.
6b. **Consolidate the confidence ladders.** The three near-identical band/symbol tables
   (`SectionBuilderBase`, `ReportSectionAssembler.NormalizeSectionContractSlots`,
   `LeakAnalysisSectionBuilder`) collapse into `ConfidenceBreakdown` — though per the audit this is
   worth doing *now* rather than waiting for this phase.
7. **Confidence plumbing.** Implement `ConfidenceBreakdown` with the caps from
   [observation-and-correlation-model.md § 4](observation-and-correlation-model.md) — identity
   fidelity, temporal alignment, capability fidelity. In a dump-only session most caps are 1.0, but
   building them now means Phase 7 turns them on rather than inventing them.

## Exit criteria

- Every analyzer emits observations (**35/36 done — see step 1**); `ObservationStore` handles a full
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
  golden corpus.
- Trend reports identical, with ≤ ~5 bespoke comparers remaining and each justified in writing.
- `IFindingGenerator` and `InsightEngine` no longer exist as separate concepts.
- **No section builder constructs `SectionLeadFinding` inline**, and no builder contains a
  threshold, band, or severity comparison — verifiable by grep, and worth encoding as an
  architecture-conformance rule so it can't regress.
- No `AnalyzerDomainResult` carries a composite score, `FindingSeverity`, or classification enum
  (audit Smell A closed).
- `ExplainableScoringEngine` either reuses synthesis outputs or is gone — not a surviving
  independent scoring path.
- Full observation lineage: every finding resolves to observations → analyzer → capabilities →
  artifact.

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
