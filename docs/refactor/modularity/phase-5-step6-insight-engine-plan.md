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

### Design decisions (resolved 2026-09-22)

- **Golden baseline**: `InsightEngineTests.cs` (1,270 lines, 31 tests) already existed, but only
  covers roughly 15 of the 39 rules — the newer/harder correlation rules, not the simpler
  `BaselineRuleGroup` methods. It is not a complete baseline and was never treated as one; migration
  instead verifies each rule against the *specific* tests that covered it (moved/adapted into the
  new rule's own test file) plus new tests for previously-uncovered rules, then a full-suite run
  after each migration. No single frozen-fixtures gate blocks starting — this is closer to step 2's
  actual practice (per-analyzer tests, full suite as the safety net) than a literal "freeze first"
  step.
- **Rule-count shape: one `ISynthesisRule` per `Detect*` method**, not clustered by theme. The
  per-analyzer precedent (one rule per analyzer, several findings per rule) doesn't map cleanly onto
  `InsightEngine`, which never had a single owning analyzer to begin with; one rule per method keeps
  each `ObservationQuery` tightly scoped to what that method actually reads, and `SynthesisEngine`'s
  per-rule cost is sub-millisecond regardless of rule count (see `SynthesisEngine`'s own remarks) —
  clustering would only add merge complexity for no measured benefit.
- **Migrate incrementally, in the same class**, mirroring the single-analyzer retirement discipline
  exactly: each `Detect*` method is removed from `InsightEngine.cs` (its rule group's `Apply` call
  site first, then the method body) in the *same change* that adds its `*InsightRule` replacement —
  never left coexisting, which would double-fire the finding. `InsightEngine` shrinks group by group
  as rules migrate, rather than being deleted in one shot at the end.
- **Naming/location**: new rules live under `DumpDetective.Reporting/SynthesisRules/Insight/`, named
  `{ShortName}InsightRule`, registered explicitly in `DefaultSynthesisRuleCatalog.RuleTypes` (they
  have no backing `IAnalyzer` for `PluginCatalogBuilder`'s naming convention to resolve). All keep
  `Finding.Analyzer = "InsightEngine"` exactly, so `InsightsSectionBuilder.GetCrossDomainFindings`'s
  existing filter (`Analyzer == "InsightEngine"`) keeps picking them up with no section-builder
  change needed — confirmed, not just assumed, by the full-suite test run after each migration.
- **`InsightByteFormat.FormatBytes`** (new, `SynthesisRules/Insight/`) is `InsightEngine.FormatBytes`
  moved verbatim and shared by every migrated rule — deliberately not
  `DumpDetective.Core.Utilities.FormatHelper.FormatBytes`, which rounds differently. All these rules
  descend from the one `InsightEngine` source method, unlike per-analyzer rules elsewhere in this
  folder which each replaced a distinct generator that may have had its own distinct formatting.

### Progress tracker

39 `Detect*` methods across 3 groups (`BaselineRuleGroup`: 11, `MemoryAndRuntimeRuleGroup`: 11,
`CorrelationRuleGroup`: 17).

- [x] **`BaselineRuleGroup`: 10 of 11 done, shipped 2026-09-22.** Migrated:
      `LohPressureInsightRule`, `LohFragmentationInsightRule`, `PohGrowthInsightRule`,
      `ThreadContentionInsightRule`, `FinalizerQueueBacklogInsightRule`,
      `PinnedHandlePressureInsightRule`, `ActiveCrashInsightRule`, `LeakSuspicionInsightRule`,
      `WastefulCollectionsInsightRule`, `OrphanedTaskAccumulationInsightRule`. All 10 were
      observation-gap-free except two (`DetectLohFragmentation`, `DetectPinnedHandlePressure`),
      which shared one small gap: `LohFragmentationDomainResult`'s whole-run
      `FragmentationPercent`/`FreeBytes`/`FreeBlockCount`/`LargestFreeBlock` had no observation of
      their own (only the per-kind `heap.loh-fragmentation` breakdown existed, which doesn't carry
      `FreeBlockCount` at all) — closed by adding a new whole-run `heap.loh-fragmentation-summary`
      observation to `LohFragmentationAnalyzer`. `DetectThreadContention`'s `HangDomainResult.
      ThreadsHoldingLocks` turned out not to need a new observation either — it's the same
      lock-holding-thread count `ThreadAnalyzer`'s `runtime.thread-triage` already observes
      independently, so the rule reads that instead of adding a duplicate measure.
      Tests: `tests/DumpDetective.Tests/Unit/Synthesis/InsightBaselineRulesTests.cs` (13 tests, one
      nested class per rule). Full suite: 1357 passed, 0 failed, 4 skipped (real-dump/trace
      integration, expected) after this batch.
- [ ] **`DetectAnalyzerFailures` — held, not migrated.** Unlike every other rule, it reads raw
      `AnalyzerRunResult` execution status (`Runs`), not any observation — there is no
      `runtime-status`-shaped fact for an analyzer to emit; this is pipeline metadata, not a heap or
      runtime fact. `SynthesisContext` (a currently-empty record) carries no run data at the real
      call site either (`SynthesisFindingEnricher.cs` always constructs `new SynthesisContext()`).
      Migrating this one needs a real design decision — extend `SynthesisContext` to optionally
      carry `IReadOnlyList<AnalyzerRunResult>`, or accept it as a permanent hand-written exception
      outside the observation model (the same shape as step 5's "genuine exception" bucket, just for
      pipeline health instead of a domain composite). Left in `InsightEngine.BaselineRuleGroup` for
      now — it's the sole remaining method in that group.
- [x] **`MemoryAndRuntimeRuleGroup`: 10 of 11 done, shipped 2026-09-22.** Migrated:
      `GCRootLargeRetentionInsightRule`, `AllocationPressureCrossCorrelationInsightRule`,
      `StringDuplicationRatioInsightRule`, `LohArrayPressureInsightRule`,
      `AsyncStateMachineFireAndForgetInsightRule`, `StaleWeakReferenceAccumulationInsightRule`,
      `AddressSpacePressureInsightRule`, `DynamicAssemblyAccumulationInsightRule`,
      `JitHeapBloatInsightRule`, `BoxingGCCorrelationInsightRule`. Same gap-rate as the first
      slice — 6 of 10 needed zero new emission, and two rules (`AllocationPressureCrossCorrelation`,
      `AddressSpacePressure`) needed no new emission either, just their classifier formulas
      duplicated locally (`AllocationPatternPressureClassifier`/the pressure-risk formula
      `SegmentReservationAnalyzer` already computes — both `internal` to a different assembly, so
      re-derived from raw measures the same way `AllocationPatternSynthesisRule`/
      `SegmentReservationSynthesisRule` already did for their own findings). Two real small gaps,
      each closed with one new measure: `heap.root` gained `root-kind.ordinal` (per-root
      `RootKind` was deliberately left off when that observation type was first designed, with a
      "revisit if a synthesis rule needs to group by it" note — this is that); `type.array-population`
      gained `loh.count` (it had `loh.bytes` but not the paired count). `weakreference.population`
      gained `stale-wrapper.count`/`stale-wrapper.is-exact` (the per-candidate
      `weakreference.stale-wrapper` observation is bounded, so summing it wouldn't reconstruct the
      true whole-run count). `DetectAsyncStateMachineFireAndForget` needed no new emission at
      all — `type.async-state-machine`'s `MethodRef` subject (already added specifically to let a
      consumer reconstruct `SuspendedMethodMap`'s coarser grouping) is grouped by `JoinKey` in the
      rule, summing `instance.count` across every state-machine-type sharing a declaring method.
      Tests: `tests/DumpDetective.Tests/Unit/Synthesis/InsightMemoryAndRuntimeRulesTests.cs` (15
      tests). Full suite: 1372 passed, 0 failed, 4 skipped (real-dump/trace integration, expected)
      after this batch.
- [ ] **`DetectJitModuleHotspot` — held, not migrated.** Needs a genuinely new capability, not a
      routine gap: `JitDomainResult.TopActiveModulesByFrameHits` (a per-module frame-hit ranking)
      has no observation of its own today, and this rule cross-references it against
      `ModuleAnalyzer`'s per-module size/conflict data (`TopModulesBySize`/
      `ConflictingAssemblyNames`), which also has no clean per-module join target yet. Left in
      `InsightEngine.MemoryAndRuntimeRuleGroup` for now — it's the sole remaining method in that
      group.
- [x] **`CorrelationRuleGroup`: 7 of 17 done, shipped 2026-09-22.** Migrated:
      `FatalExceptionOnHeapInsightRule`, `EventLeakPatternInsightRule`,
      `RecurringTimeoutPatternInsightRule`, `DbConnectionLeakInsightRule`,
      `WcfChannelFaultInsightRule`, `WcfOpeningTimeoutCorrelationInsightRule`,
      `HttpClientAccumulationInsightRule`. This group's gap profile is different from the first
      two — fewer clean single-measure gaps, more structural blockers:
      - **One gap unblocked 5 rules at once.** `CrashDomainResult.ExceptionTypeCounts` (every
        exception type on heap, not just active ones) had no observation at all — only
        `runtime.active-exception` (per-instance, active-only) existed. `CrashAnalyzer` now also
        emits a bounded `runtime.exception-type` observation per distinct exception type
        (bounded by `unique-types.count`, not instance count), which is what actually unblocked
        `FatalExceptionOnHeap`, `RecurringTimeoutPattern`, `DbConnectionLeak`, `WcfChannelFault`,
        and `WcfOpeningTimeoutCorrelation` together — worth closing before auditing rule-by-rule
        next time a shared dependency like this shows up.
      - `EventLeakPatternInsightRule` needed one more small gap closed:
        `event.publisher-leak-population` gained `subscriber.count` (`EventLeakDomainResult.
        TotalSubscribers` was already an exact field, just not yet promoted to that observation).
      - `HttpClientAccumulationInsightRule`, `DbConnectionLeakInsightRule` (once the exception-type
        gap closed), and `WcfChannelFaultInsightRule`/`WcfOpeningTimeoutCorrelationInsightRule`
        (same) needed zero further emission work — their own whole-run population observations
        already carried everything needed.
      - **Also closed groundwork gaps for `ReferenceChainDominatorCorrelation` while in the
        neighborhood**, even though that rule itself stayed held (see below):
        `type.retention-population` gained `total.bytes` (only had the derived `pressure.ratio`
        before); `reference-chain.type-sample` gained an optional `retained.bytes` measure
        (present only when the dominator tree was available, mirroring the domain record's own
        nullable field). Both are real, tested additions — not wasted work, just not enough to
        unblock the rule on their own (see the `Finding.EvidenceTables` gap below).
      Tests: `tests/DumpDetective.Tests/Unit/Synthesis/InsightCorrelationRulesTests.cs` (13 tests).
      Removed 5 now-stale `InsightEngineTests.cs` tests that exercised the migrated methods via
      `InsightEngine.Analyze` directly (`FatalExceptionTypePresent` and 4 WCF-opening-timeout
      tests), plus the `BuildWcfResult` helper that only they used. Full suite: 1379 passed, 0
      failed, 4 skipped (real-dump/trace integration, expected) after this batch.
- [ ] **10 of 17 `CorrelationRuleGroup` methods held, for three distinct reasons** — none are a
      routine "add one measure" gap the way most of steps 6.2's earlier work was:
      1. **`Finding` has no `EvidenceTables` equivalent at all — a real SDK-level gap, not a
         per-rule one.** `InsightFinding.EvidenceTables` has no field on `Sdk.Synthesis.Finding`,
         and nothing in `FindingSynthesisAdapter`/the report pipeline carries one through even if a
         rule tried to attach it. Confirmed zero existing `*SynthesisRule` in this codebase uses
         evidence tables today — this was never exercised by steps 2/3's single-analyzer rules
         either. Blocks `DetectReferenceChainDominatorCorrelation`,
         `DetectMemoryTypeGenerationCorrelation`, `DetectStringMemoryConcentration`, and (jointly
         with reason 3) `DetectJitModuleHotspot`/`DetectCrashModuleHotspot`. Extending `Finding`
         and the adapter/pipeline to carry evidence tables is a prerequisite for all of these, not
         something to solve inside one rule.
      2. **`MemoryDomainResult.TopTypes` is an unbounded per-type list deliberately never promoted
         to an observation** (the same volume-risk reasoning `GCGenerationAnalyzer`'s own per-type
         list already got flagged for). `DetectDataTableLifecyclePattern`, `DetectKnownLeakPatterns`,
         and `DetectMemoryTypeGenerationCorrelation` all scan it for known substrings
         (DataColumn/DataTable/DataRow, TdsParser-closures, reflection-metadata types) or cross-join
         it against another unbounded per-type list. A bounded, known-vocabulary observation (the
         same shape as `TimerWrapperTypeNames`) could close the substring-scan cases; the full
         cross-join case (`MemoryTypeGenerationCorrelation`) needs a bigger design decision, not a
         quick fix. `DetectStringMemoryConcentration` also needs `TopTypes` (for `System.String`'s
         size rank) and independently needs the `EvidenceTables` fix above.
      3. **Large/complex enough to need a dedicated audit pass, not a quick fix squeezed into this
         batch**: `DetectClusterHangCorrelation` (four-way cross-reference —
         `ThreadStackClusterDomainResult`/`HangDomainResult`/`ThreadDomainResult`/
         `LockGraphDomainResult` — with OS-thread-ID-level joins and a deadlock-candidate escalation
         path); `DetectCrashModuleHotspot`/`DetectJitModuleHotspot` (both need a genuinely new
         per-module `LoadedModuleSnapshot.Size` observation — `runtime.module-heap-footprint`
         measures heap footprint, a different quantity — and `DetectJitModuleHotspot` separately
         needs a new per-module JIT frame-hit observation on top of that);
         `DetectStaticRootFinalizableCorrelation` (nested per-root × per-type cross join, no clean
         observation shape yet); `DetectLongHeldTransactionOnOpenConnection` (address-level join
         between `SqlTransactionDomainResult`'s active-transaction connection addresses and
         `DbConnectionDomainResult`'s open-connection addresses — needs checking whether those
         addresses are even observable as stable identity, not assumed); `DetectPinnedStringLeak`
         (needs a per-type *bytes* measure for pinned targets — `handle.pinned-target-type` only
         carries counts, and `TopPinnedObjectsBySize`/`TopPinnedTargetTypes` are two independently-
         sorted rankings that may not share membership, so aligning them needs care, not a blind
         index-zip).
      All 10 stay in `InsightEngine.CorrelationRuleGroup` for now, each with its own reason
      documented inline in that class's leading comment.
- [ ] **Retire `InsightEngine` only once every rule is ported (except the held
      `DetectAnalyzerFailures`, whose fate is a separate decision).** Delete the class down to
      whatever `DetectAnalyzerFailures`'s resolution leaves, `DumpOrchestrationService.cs`'s
      `new InsightEngine().Analyze(...)` call, `sessionState.Insights`, and
      `SingleDumpConsolePresenter.PrintInsights` — update this once the last group ships, since the
      exact shape depends on how `DetectAnalyzerFailures` gets resolved.
- [ ] This closes the phase's own exit criterion — "`IFindingGenerator`/`InsightEngine` no longer
      exist as separate concepts" — jointly with 6.1 (6.1 closed the last surviving
      `IFindingGenerator`; 6.2 closes `InsightEngine`, `DetectAnalyzerFailures`'s fate permitting).

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

6.1 is done (see § 6.1). 6.2 is the large item: 39 rule methods, ~1,300 lines of detection logic.
All three groups have started; 27 of 39 methods are migrated, 12 held. `BaselineRuleGroup` (10/11)
and `MemoryAndRuntimeRuleGroup` (10/11) had a low gap rate — 8/11 and 6/10 needed zero new emission,
most of the rest needed one new measure on an existing observation, and two needed only a classifier
formula duplicated locally. `CorrelationRuleGroup` (7/17 so far) confirmed the sizing note's own
warning: its gap profile is different and harder — fewer single-measure gaps, more structural
blockers. The two biggest surprises there: one shared gap (`CrashDomainResult.ExceptionTypeCounts`
never observed) unblocked 5 rules at once when closed, which is worth checking for again in any
future group before auditing rule-by-rule; and a real SDK-level gap surfaced —
`Sdk.Synthesis.Finding` has no `EvidenceTables` equivalent at all, blocking every remaining rule
that renders a supplementary table (`ReferenceChainDominatorCorrelation`,
`MemoryTypeGenerationCorrelation`, `StringMemoryConcentration`, and `CrashModuleHotspot`, plus
`MemoryAndRuntimeRuleGroup`'s already-held `JitModuleHotspot`, which shares the same
`EvidenceTables` need on top of its own separate per-module-JIT-observation gap). The other held
`CorrelationRuleGroup` methods split into "needs `MemoryDomainResult.TopTypes`, an unbounded
per-type list deliberately never observed" (4 methods) and "large/complex enough to need its own
dedicated audit pass" (`ClusterHangCorrelation`, `StaticRootFinalizableCorrelation`,
`LongHeldTransactionOnOpenConnection`, `PinnedStringLeak`, `CrashModuleHotspot` — the last also
needs a genuinely new per-module `LoadedModuleSnapshot.Size` observation, the same gap
`JitModuleHotspot` needs). See § "CorrelationRuleGroup" above for the full per-method breakdown.
`DetectAnalyzerFailures` (`BaselineRuleGroup`) is the remaining held method from an earlier
group — needs pipeline run-status, not a heap/runtime fact.
