# Phase 5 Step 6 — Fold `InsightEngine` Into Synthesis Rules

Part of [../modularity-plan.md](../modularity-plan.md), split out of
[phase-5-observations-synthesis.md](phase-5-observations-synthesis.md) on 2026-09-22, the same way
[phase-5-step2-per-analyzer-findings.md](phase-5-step2-per-analyzer-findings.md) and
[phase-5-step4-comparer-retirement-audit.md](phase-5-step4-comparer-retirement-audit.md) were split
out of that doc earlier — step 6 covers two things the parent doc's own item 6/6a/6b only summarize:
`LeakCandidateAnalyzer`'s deferred Stage-1-purity fix (the one analyzer of 36 still not emitting
observations, per that doc's "Carve out `LeakCandidateAnalyzer` explicitly" item), and folding all
39 of `InsightEngine`'s cross-analyzer rules into `ISynthesisRule`s. This is the live plan for both;
update it in place as work completes, per the same discipline
[phase-5-step2-per-analyzer-findings.md](phase-5-step2-per-analyzer-findings.md) uses.

## Prerequisites (all already satisfied)

- The synthesis machinery (`ISynthesisRule`, `ObservationQuery`, `SynthesisEngine`,
  `DefaultSynthesisRuleCatalog`) — shipped 2026-09-17, see parent doc's "Before step 2".
- 35/36 analyzers already emit observations, including the two step 6.1 needs data from:
  `GCHandleAnalyzer` (for pinned/dependent-target facts) and `TimerLeakAnalyzer` (for logical timer
  count) — both already migrated (see parent doc's per-analyzer table).
- Steps 4/5 (generic trend differ + genuine composite exceptions) — done 2026-09-20/21/22.
- **Confirmed 2026-09-22: nothing external blocks starting step 6.** The only remaining work is
  inside step 6 itself.

## 6.1 — `LeakCandidateAnalyzer`: first observation emission + its synthesis rule

**Done, shipped 2026-09-22.**

Unlike every other analyzer in the retyping wave, this one never emitted observations at all —
deliberately deferred here rather than fixed as a same-shaped Stage-1 extraction, because
`SuspicionScore`/`Severity`/`Classification`/`RootKind` are cross-analyzer synthesis (need
`GCHandleDomainResult`/`TimerLeakDomainResult`, not just `LeakCandidateAnalyzer`'s own raw facts).
See
[analyzer-pipeline-stages-and-leadfinding-dedup.md § "Leak Candidate — deferred Phase 5"](../analyzer-pipeline-stages-and-leadfinding-dedup.md)
for the full original reasoning.

- [x] **Emit raw per-type facts, not the composite.** New `leak.candidate-type` observation per
      candidate in `LeakCandidateDomainResult.TopCandidates` (the complete ranked population, no
      Top-N cap — so one observation per distinct type with a resolvable sample address), carrying
      `shallow.bytes`, `instance.count`, `gen2.percent`, `gen2.count`, `reference-field.ratio`,
      `is-finalizable.flag`, `is-container.flag`, `is-static-rooted.flag`, `is-delegate.flag`.
      `SuspicionScore`/`Severity`/`Classification`/`RootKind` are **not** ported — they stay
      synthesis-rule/domain-result output only, per the purity rule
      (`observation-and-correlation-model.md` § 2a). `LeakCandidateRecord` gained three new raw
      fields (`IsStaticRooted`, `IsDelegate`, `Gen2Count`) to carry these facts out of `Analyze`,
      since they were previously local-only variables never persisted onto the record.
- [x] **`IDirectRootSignal` (GCRootAnalyzer) turned out not to be needed by the rule at all** —
      the planning pass assumed all three cross-analyzer signals fed `Classify`/`Score`, but
      re-reading `LeakCandidateFindingGenerator.Generate` showed it never reads `RootKind`/
      `RootChain`, only `TypeName`/`SuspicionScore`/`TotalSize`/`InstanceCount`/`Gen2Pct`/
      `Classification`/`Severity`. `IDirectRootSignal`/`GCRootAnalyzer` observations are irrelevant
      to the finding output — root-chain-text enrichment stays exactly as it was, dump-side only,
      untouched by this work.
- [x] **`ICustomizesAnalysisContext`/`CompletedRunResults` wiring in `BuildAnalysisContext` was
      *not* deleted** — kept exactly as-is. `LeakCandidateAnalyzer.Analyze` still computes
      `Classify`/`Score`/`Severity`/`Classification` itself for its own `LeakCandidateDomainResult`
      (unchanged), which `LeakAnalysisSectionBuilder` still reads for section display — the same
      "benign parallel selection" duplication the audit doc's Smell A table already flagged as
      acceptable, not new here. `LeakCandidateSynthesisRule` independently recomputes the same
      classification from observations for its `Finding` output; both copies use the same
      thresholds so they can't drift silently.
- [x] **Closed two real observation-emission gaps in `GCHandleAnalyzer`**, found during this work,
      not anticipated by the original plan: `TopPinnedTargetTypes`/`DependentTopTargetTypes` had no
      per-type observation at all before this (only per-*address* `handle.pinned`, bounded to
      `TopPinnedHandleAddressesToShow`, too narrow a population). Added `handle.pinned-target-type`
      and `handle.dependent-target-type`, one observation per distinct target type, mirroring
      `TopRefCountedTargetTypes`'s existing `handle.refcounted-target` shape exactly.
      `infra.timer-population`'s existing `timer-queue.count` measure already covered the timer-count
      input with no changes needed.
- [x] **Wrote `LeakCandidateSynthesisRule`** ([LeakCandidateSynthesisRule.cs](../../../src/DumpDetective.Reporting/SynthesisRules/LeakCandidateSynthesisRule.cs))
      with `Classify`/`Score`/`GetSeverity` moved in verbatim (byte-identical scoring), querying
      `leak.candidate-type` + `handle.pinned-target-type` + `handle.dependent-target-type` +
      `infra.timer-population` — the same multi-observation-type-in-one-query mechanism
      `GCHandleSynthesisRule` already used across two types, just spanning analyzer boundaries here.
      Registered in `DefaultSynthesisRuleCatalog.RuleTypes`.
- [x] **Test coverage**: no `Golden/Baselines/Synthesis/<Analyzer>/` directory was ever actually
      created for any of the other 35 rules despite the parent doc's playbook describing one — the
      real precedent every prior migration followed was "delete the old `*FindingGeneratorTests.cs`,
      no replacement test file." Added one anyway here
      ([LeakCandidateSynthesisRuleTests.cs](../../../tests/DumpDetective.Tests/Unit/Synthesis/LeakCandidateSynthesisRuleTests.cs)),
      covering the same scenarios the old generator's tests did (no candidates, multi-candidate
      aggregate, critical-candidate, plus a new timer-leak-classification case exercising the
      cross-analyzer `infra.timer-population` join) plus a fix to
      `LeakCandidateAnalyzerRetypingCharacterizationTests`'s context-building helper, which never set
      `AnalysisContext.ArtifactId` and started throwing once `EmitObservationsAsync` needed it.
- [x] **Retired `LeakCandidateFindingGenerator`** — deleted it and its test file. It was the *last*
      file in `DumpDetective.Reporting/FindingGenerators/` (every other one had already gone in
      steps 2/3), so the directory and its now-empty-namespace `using` in `ServiceRegistration.cs`/
      `ServiceRegistrationTests.cs` were removed too.
      `services.GetServices<IFindingGenerator>()` in `ServiceRegistrationTests` now asserts
      **empty**, not non-empty — every `AnalyzerFeatureModule` has a `SynthesisRuleType` now,
      confirming this half of the phase's own exit criterion
      ("`IFindingGenerator` no longer exists as a separate concept") is fully met.
- [x] Full `dotnet build` (0 errors) and `dotnet test --filter "FullyQualifiedName!~Integration.CacheDiscrepancies"`
      (1345 passed, 0 failed, 4 skipped — real-dump/trace integration tests, expected without a dump
      loaded) both clean after this change. The real-dump `LeakCandidateAnalyzerRealDumpTests`
      discrepancy test was not run as part of this change — per this project's standing rule, that
      needs a human-confirmed dump path and must run alone, in the foreground, never in parallel.
- [ ] **Still open**: update the `Leak Candidate` row in
      [analyzer-pipeline-stages-and-leadfinding-dedup.md](../analyzer-pipeline-stages-and-leadfinding-dedup.md)'s
      Smell A table and its "Leak Candidate — deferred Phase 5" section to reflect that the deferred
      fix has now shipped, and update the "Carve out `LeakCandidateAnalyzer` explicitly" item in
      [phase-5-observations-synthesis.md](phase-5-observations-synthesis.md).

## 6.2 — Fold `InsightEngine`'s 39 rules into `ISynthesisRule`s

Mechanically the same move steps 2-3 already made for single-analyzer generators — cross-analyzer
matching needs no `SynthesisEngine` changes, just an `ObservationQuery` whose `ObservationTypes`
spans multiple analyzers, which the engine already treats uniformly (it indexes observations by
`ObservationType` regardless of which analyzer produced them).

`InsightEngine.cs` (2,360 lines) has 39 `Detect*` rule methods across three groups
(`BaselineRuleGroup`, `MemoryAndRuntimeRuleGroup`, `CorrelationRuleGroup`), producing 46 finding
sites total. Findings reach the report today via a synthetic `"InsightEngine"`-named pseudo-run
plus a `"cross-analyzer"` tag, picked up by `InsightsSectionBuilder`'s `X1` "Cross-Domain Insights"
section — any migration needs to either keep that convention or update the section builder's filter
to match whatever the migrated rules actually set.

- [ ] **Freeze a golden baseline for `InsightEngine.Analyze` first.** Check whether an existing
      `InsightEngineTests.cs` fixture set already covers all 39 rules' trigger conditions; if it's
      thin, build fixtures from each rule's own threshold constants before touching any migration
      code — same as step 2's playbook item (a)/(b), just for one engine instead of one analyzer.
- [ ] **Decide the rule-count/clustering shape before writing code.** Several `Detect*` methods
      already share domain-result inputs (e.g. `DetectLohPressure`/`DetectLohFragmentation`/
      `DetectPohGrowth`/`DetectLohArrayPressure`/`DetectPinnedHandlePressure` all touch LOH/handles/
      segments together). Choose between one `ISynthesisRule` per `Detect*` method (39 small rules)
      or fewer rules clustered by theme (Memory pressure, GC/handles, Threads/hang, Async,
      Crash/modules, Sql/Wcf/Http) with several findings each — the per-analyzer precedent (one rule
      per analyzer, several findings per rule) leans toward clustering, but confirm against how each
      rule's `EvidenceTables`/ranking logic reads most naturally before committing.
- [ ] **Audit every field each `Detect*` method reads against what's already an `Observation`** —
      same audit shape as step 2's playbook item (c)/(d), run once per rule/cluster rather than
      once per analyzer. Expect gaps: several methods reach into fields not obviously promoted yet
      (e.g. `MemoryDomainResult.TopTypes`, `ModuleDomainResult.ConflictingAssemblyNames`,
      `CrashDomainResult.TopCrashThreadCandidates`, `ThreadDomainResult.FinalizerThreadBlocked`).
      Extend the owning analyzer's emission (and its `SdkRegistryConformanceTests.
      KnownEmittedObservationTypes` entry) before writing the rule that needs it — never let a new
      rule reach back into the old domain result for a value its observations don't carry. Expect
      this audit to be the largest single piece of 6.2's work, larger than the rule-writing itself.
- [ ] **Migrate rule-by-rule (or cluster-by-cluster), diffing against the frozen baseline each
      time.**
- [ ] **Reconcile `InsightsSectionBuilder`'s pickup filter.** Confirm migrated findings still land
      in the `X1` section — either keep every migrated rule setting `Analyzer: "InsightEngine"` and
      the `"cross-analyzer"` tag, or update `InsightsSectionBuilder.GetCrossDomainFindings`'s filter
      condition to match the new convention. Verify via the frozen baseline diff, not by inspection.
- [ ] **Retire `InsightEngine` only once every rule is ported and diff-clean.** Delete the class,
      `DumpOrchestrationService.cs`'s `new InsightEngine().Analyze(...)` call, `sessionState.
      Insights`, and `SingleDumpConsolePresenter.PrintInsights`.
- [ ] This closes the phase's own exit criterion — "`IFindingGenerator`/`InsightEngine` no longer
      exist as separate concepts" — jointly with 6.1 (6.1 closes the last surviving
      `IFindingGenerator`; 6.2 closes `InsightEngine`).

## 6.3 — Section builders become presentation-only (6a)

- [ ] Re-run the `grep "new SectionLeadFinding("` audit from
      [analyzer-pipeline-stages-and-leadfinding-dedup.md](../analyzer-pipeline-stages-and-leadfinding-dedup.md)
      to confirm zero inline constructions remain outside `NormalizeSectionContractSlots`'s
      derivation path — the doc's own tracking says only `LeakAnalysisSectionBuilder`'s benign
      parallel read (`Severity` read independently by both builder and generator, not re-derived
      logic) was still open, and 6.1 above removes the reason that was allowed to stand.
- [ ] Add the architecture-conformance test the phase's exit criteria call for: no section builder
      contains a threshold/band/severity comparison. Grep-based, same style as
      `SynthesisRuleRegistryConformanceTests`.

## 6.4 — Consolidate confidence ladders (6b)

- [ ] Confirm `SectionBuilderBase.SymbolForScore` is still the only remaining band/symbol ladder
      copy (per the audit doc, the other two were already folded into it) after 6.1/6.2 land.
- [ ] Fold that last copy into `ConfidenceBreakdown` per the parent doc's item 6b; delete the local
      ladder if `SymbolForScore` becomes a trivial `ConfidenceBreakdown` call.

## Sizing note

6.1 is scoped and bounded — one analyzer, one new rule, an existing template
(`GCHandleSynthesisRule`) to follow. 6.2 is the large item: 39 rule methods, ~1,300 lines of
detection logic, and an unknown-until-audited number of observation-emission gaps across a
dozen-plus analyzers. Treat 6.2's observation-gap audit as its own checkpoint before committing to a
rule-count/clustering design — the same way step 2's per-analyzer audit surfaced real gaps in 32 of
35 analyzers before any rule got written.
