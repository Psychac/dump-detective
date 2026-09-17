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
