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
- [x] **`DetectAnalyzerFailures` — migrated, shipped 2026-09-23.** Unlike every other rule, it read
      raw `AnalyzerRunResult` execution status, not any observation — pipeline metadata, not a heap
      or runtime fact, so it has no natural `Observation` shape (the model exists for claims about
      the artifact, not about the tool's own execution). Investigated the two options this doc had
      floated: extending `SynthesisContext`, or a permanent hand-written exception (the same shape
      as step 5's "genuine exception" bucket). The "real design decision" framing turned out to
      overstate the actual cost — `SynthesisFindingEnricher.EnrichAsync`, the one real call site,
      already receives `IReadOnlyList<AnalyzerRunResult> runs` as a parameter, just never threaded
      it into `SynthesisContext`. Added `SynthesisContext.AnalyzerRunSummary`, a `(int Total, int
      Failed)` count pair rather than the full `AnalyzerRunResult` list (`internal` to
      `DumpDetective.Core`, an assembly the `Sdk` project must not depend on, and a count is all the
      rule needs) — `SynthesisContext`'s own doc comment already said it "grows as \[later phases]
      land," and this is exactly that moment, not a speculative addition. `AnalyzerFailuresInsightRule`
      declares an empty `ObservationTypes` and relies on `SynthesisEngine`'s documented "no
      zero-match skip" behavior (every rule runs every pass, regardless of match count) to still
      fire. One real regression surfaced and was reverted: widening `EnrichAsync`'s
      `observations.Count == 0` early-return guard (so the new rule could fire on a synthetic
      zero-observation pass) broke `SynthesisFindingEnricherTests`' `StubRule`, which unconditionally
      returns a finding ignoring its match set — that test's "nothing to synthesize, return runs
      unchanged" contract turned out to depend on the original blanket guard for every rule, not
      just checking whether *this* rule needed observations. Left as the original guard; real dumps
      always have tens of thousands of observations, so the zero-observation case this leaves
      unhandled is a synthetic-test-only edge case, not a production concern. `BaselineRuleGroup`
      itself is now gone from `InsightEngine.cs` — this was its last method. Tests:
      `tests/DumpDetective.Tests/Unit/Synthesis/InsightBaselineRulesTests.cs` gained an `AnalyzerFailures`
      nested class (4 tests); one stale `InsightEngineTests.cs` case removed. Full suite: 1401
      passed, 0 failed, 4 skipped (real-dump/trace, expected).
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
- [x] **`CorrelationRuleGroup`: 15 of 17 done, shipped 2026-09-22/23.** Migrated:
      `FatalExceptionOnHeapInsightRule`, `EventLeakPatternInsightRule`,
      `RecurringTimeoutPatternInsightRule`, `DbConnectionLeakInsightRule`,
      `WcfChannelFaultInsightRule`, `WcfOpeningTimeoutCorrelationInsightRule`,
      `HttpClientAccumulationInsightRule`, `ReferenceChainDominatorCorrelationInsightRule`,
      `DataTableLifecycleInsightRule`, `KnownLeakPatternsInsightRule`,
      `PinnedStringLeakInsightRule`, `ClusterHangCorrelationInsightRule`,
      `StaticRootFinalizableCorrelationInsightRule`,
      `LongHeldTransactionOnOpenConnectionInsightRule`, `CrashModuleHotspotInsightRule`. This
      group's gap profile is different from the first two — fewer clean single-measure gaps, more
      structural blockers:
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
        neighborhood**: `type.retention-population` gained `total.bytes` (only had the derived
        `pressure.ratio` before); `reference-chain.type-sample` gained an optional
        `retained.bytes` measure (present only when the dominator tree was available, mirroring
        the domain record's own nullable field). Both were real, tested additions on their own —
        and, per the decision below, turned out to be *all* that rule needed.
      Tests: `tests/DumpDetective.Tests/Unit/Synthesis/InsightCorrelationRulesTests.cs` (13 tests).
      Removed 5 now-stale `InsightEngineTests.cs` tests that exercised the migrated methods via
      `InsightEngine.Analyze` directly (`FatalExceptionTypePresent` and 4 WCF-opening-timeout
      tests), plus the `BuildWcfResult` helper that only they used. Full suite: 1379 passed, 0
      failed, 4 skipped (real-dump/trace integration, expected) after this batch.
- [x] **`Finding.EvidenceTables` question resolved 2026-09-22 — not an SDK extension.** The gap
      (`Sdk.Synthesis.Finding` has no equivalent of `InsightFinding.EvidenceTables`, and nothing in
      `FindingSynthesisAdapter`/the report pipeline would carry one through even if a rule tried)
      blocked `DetectReferenceChainDominatorCorrelation`, `DetectMemoryTypeGenerationCorrelation`,
      `DetectStringMemoryConcentration`, `DetectJitModuleHotspot`, and `DetectCrashModuleHotspot`.
      Ran the fix against [ReportSystemVision.md](../../ReportStructure/ReportSystemVision.md)
      before building it: § 6.2 already decided **not** to pre-add `Claim`-shaped/report-rendering
      fields to `Finding` ahead of the report phase ("no real synthesis rule exists yet to design
      those fields against... revisit when this report phase actually starts"), and Appendix C
      lists `CompactTable` — what an `EvidenceTable` becomes downstream — as exactly what the
      future `dd-table` widget replaces, rendered live from observations a `Claim` cites rather
      than carried as data on the finding. Adding an `EvidenceTables` field to `Finding` now would
      mean building, and then discarding, the structure that decision already deferred.
      **Resolution: don't extend `Finding`.** Per rule, decide whether the table is optional
      supplementary detail or the finding's entire payload:
      - `ReferenceChainDominatorCorrelationInsightRule` **migrated without its table** — the
        original's own `Evidence` prose already stated the headline (offender count, the single
        worst offender's name/bytes/%); only the full ranked list beyond the top offender is lost,
        and every offending type is still individually cited via `Finding.DerivedFrom` as a
        `reference-chain.type-sample` observation, so a future `dd-table` can render the exact same
        list without this rule changing. Shipped 2026-09-22 alongside the other 7 — see its own
        `<remarks>` for the full reasoning.
      - `MemoryTypeGenerationCorrelation`/`StringMemoryConcentration`/`CrashModuleHotspot`/
        `JitModuleHotspot` stay held — their tables aren't optional supplementary detail the way
        `ReferenceChainDominatorCorrelation`'s was (see the per-method reasons below), so dropping
        the table would mean dropping most of the finding's value, not a disclosed simplification.
- [x] **Closed the substring-scan half of the `MemoryDomainResult.TopTypes` gap, shipped
      2026-09-22.** Scoped against
      [docs/ReportStructure/ReportSystemVision.md](../../ReportStructure/ReportSystemVision.md)
      first: §5.4's "no top-N, no cap" is a store-level policy against sampling an unbounded
      ranking, not against a fixed, known-vocabulary check — the same distinction
      `TimerWrapperTypeNames` already relies on — so a bounded observation for a handful of named
      patterns doesn't reopen that rule. `MemoryAnalyzer` now emits
      `type.known-pattern-population` (bounded to DataRow, SqlClient `TdsParser` async-closures, and
      four uncached-reflection-metadata type names) scanned from `MemoryDomainResult.TopTypes`,
      which — despite its name — was already the full per-type population, not a top-N slice; only
      the observation was missing. `DetectDataTableLifecyclePattern`'s finalizer-side half
      (DataColumn/DataTable via `FinalizableObjectDomainResult`) already had full observation
      coverage (`type.finalizable-population`, `finalizer.queue-entry`, both exhaustive) and needed
      no new emission at all. Migrated as `DataTableLifecycleInsightRule` and
      `KnownLeakPatternsInsightRule`. `DetectMemoryTypeGenerationCorrelation`/
      `DetectStringMemoryConcentration` stay held — those need a full cross-join against another
      unbounded per-type list, which §10.1/§10.4 of the same vision doc assigns to the future
      `dd-scatter` Correlation widget over the entity/observation store, not a hand-rolled per-rule
      join built ahead of that model landing.
      Tests: `tests/DumpDetective.Tests/Unit/Synthesis/InsightCorrelationRulesTests.cs` (9 rules
      now covered in this file); full suite 1388 passed, 0 failed, 4 skipped (real-dump/trace,
      expected).
- [x] **Closed `DetectPinnedStringLeak`'s gap, shipped 2026-09-22.** `handle.pinned-target-type`
      only carried per-type pinned *counts*; `TopPinnedObjectsBySize`/`TopAsyncPinnedObjectsBySize`
      (the analyzer's own independent top-N-by-bytes rankings) had no observation at all.
      `GCHandleAnalyzer` now also emits `handle.pinned-target-bytes` from those two lists directly
      — deliberately not summed from the per-address `handle.pinned` observation (that one is
      capped to `GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow` addresses, so a per-type
      sum over it would silently undercount) and deliberately not index-zipped against
      `handle.pinned-target-type` (the original's own `FindBytes`/`FindCount` helpers already
      treated the two rankings as independent, exact-name lookups — that semantics is preserved,
      not "fixed"). Migrated as `PinnedStringLeakInsightRule`; the two stale
      `InsightEngineTests.cs` cases for the old method removed, coverage moved to
      `InsightCorrelationRulesTests.PinnedStringLeak`. Full suite: 1390 passed, 0 failed, 4 skipped
      (real-dump/trace, expected) — one unrelated pre-existing flaky test
      (`ArrayAnalyzerRetypingCharacterizationTests.AnalyzeAsync_RealSparseArray_...`) failed once in
      the full run, passed in isolation; not touched by this change.
- [x] **Closed `DetectClusterHangCorrelation`'s gap, shipped 2026-09-23.** Investigated deeper than
      the other "audit pass" items and found two separate real gaps, not one:
      `ThreadStackClusterAnalyzer`'s `thread.stack-cluster` had `Subjects=[]` (no entity subject at
      all — a cluster spans many threads) and the dominant cluster's member OS thread ids
      (`SampleOsThreadIds`) were domain-result-only; closed with a new `thread.stack-cluster-member`
      observation (`Subjects=[ThreadRef]`) plus a shared `cluster.rank` measure (0 = dominant) on
      both it and `thread.stack-cluster`, so the rule can find the dominant cluster and its members
      without a positional assumption. `HangAnalyzer`'s `thread.wait-state` deliberately never
      carried `WaitType`/`WaitReason` (categorical strings, no home in numeric `Measures`) — but
      investigation showed `WaitReason` is just a fixed 1:1 display label per `WaitType` case (a
      small, code-defined 7-value enum), so a `wait-type.ordinal` measure closes it with the same
      `root-kind.ordinal` pattern `GCRootAnalyzer` already uses; scoped against
      [docs/ReportStructure/ReportSystemVision.md](../../ReportStructure/ReportSystemVision.md)
      first — §14.2's column-store spec has a first-class `"kind": "enum"` column (ordinal + fixed
      label dictionary), independently validating the ordinal-measure shape as forward-compatible,
      not a stopgap. The rule keeps its own copy of the ordinal-to-label table rather than
      referencing `HangAnalyzer`'s `WaitType` enum directly — it's `internal` to a different
      assembly, the same "re-derive, don't share the internal type" call `LeakCandidateSynthesisRule`
      made for `AllocationPatternPressureClassifier`. Deadlock-candidate lock-object-type names ride
      on `thread.blocked`'s `Evidence` (narrative-only in the original) rather than a new `Subjects`
      shape. The wait-reason breakdown table was dropped, not ported — same "supplementary, not the
      whole payload" call `ReferenceChainDominatorCorrelation` made for its own table; the headline
      already states the dominant reason. Migrated as `ClusterHangCorrelationInsightRule`; six stale
      `InsightEngineTests.cs` cases (two tests plus a shared helper) removed, coverage moved to
      `InsightCorrelationRulesTests.ClusterHangCorrelation`. Full suite: 1390 passed, 0 failed, 4
      skipped (real-dump/trace, expected).
- [x] **Closed `DetectStaticRootFinalizableCorrelation`'s gap, shipped 2026-09-23.** Turned out
      simpler than the original "nested per-root × per-type cross join, no clean observation shape
      yet" held-reason suggested — both sides of the join are already small, bounded, domain-result
      lists (`TopRootsByRetainedBytes` and each root's `TopRetainedTypes`), not a full heap-wide
      cross join. The finalizer-queue side needed no new emission — `finalizer.queue-entry` (already
      exhaustive) is grouped by type name in the rule, the same trick `DataTableLifecycleInsightRule`
      already uses. The static-root side needed one new observation:
      `StaticRootAnalyzer`'s existing `heap.static-root` subjects on the root object's own type, not
      the `TopRetainedTypes` breakdown. There's no `root` `EntityKind` in the SDK yet
      ([docs/ReportStructure/ReportSystemVision.md](../../ReportStructure/ReportSystemVision.md)
      §4.1's root entity is aspirational, not shipped), so the new
      `heap.static-root-retained-type` observation subjects on the retained `TypeRef` instead
      (joinable against `finalizer.queue-entry` by type name) and carries the root's description as
      narrative-only `Evidence` — the same call already made for `thread.stack-cluster`'s
      `Signature` and `thread.blocked`'s lock-object-types. Bounded by `TopRetainedTypes`' own
      existing cap, not a new unbounded emission. Migrated as
      `StaticRootFinalizableCorrelationInsightRule`; two stale `InsightEngineTests.cs` cases
      removed, coverage moved to `InsightCorrelationRulesTests.StaticRootFinalizableCorrelation`.
      Full suite: 1392 passed, 0 failed, 4 skipped (real-dump/trace, expected). Note:
      `StaticRootAnalyzer`'s pre-existing "never actually fires on a real dump" bug (see
      [docs/analysis/phase1/static-root-leak-detector-audit.md](../../analysis/phase1/static-root-leak-detector-audit.md))
      was deliberately left untouched here, same as when it was retyped — this migration is
      mechanical and doesn't depend on that bug being fixed.
- [x] **Closed `DetectLongHeldTransactionOnOpenConnection`'s gap, shipped 2026-09-23.** The
      held-reason — "needs checking whether the addresses are even observable as stable identity,
      not assumed" — turned out to already be answered by the SDK: `ObjectRef`'s identity is
      `(artifact, address)`, explicitly scoped to within-artifact joins (never cross-artifact — see
      its own remarks). Both `SqlTransactionAnalyzer` and `DbConnectionAnalyzer` always read the
      same dump in the same run, so this is exactly the supported case, not the cross-dump case the
      SDK warns against. Neither `infra.sql-transaction` nor `infra.db-connection` previously
      carried a heap address at all (`Subjects` was `TypeRef`-only on both); each now carries a
      second `ObjectRef` subject (the referenced connection's address on the transaction side, the
      connection's own address on the other) — same multi-subject relation shape `lock.contested`
      already uses for type+owner. No JSON schema registry update — `lock.contested` itself (a real,
      already-shipped multi-subject observation) isn't registered there either, so the registry is
      evidently a curated subset, not something every real emission gets synced into. Migrated as
      `LongHeldTransactionOnOpenConnectionInsightRule`; two stale `InsightEngineTests.cs` cases
      removed, coverage moved to
      `InsightCorrelationRulesTests.LongHeldTransactionOnOpenConnection`. Full suite: 1395 passed, 0
      failed, 4 skipped (real-dump/trace, expected).
- [x] **Closed `DetectCrashModuleHotspot`'s gap, shipped 2026-09-23.** The original held-reason only
      named one of two real gaps, and turned out not to be blocked by the `EvidenceTables` decision
      at all despite being grouped with the three that are. `CrashAnalyzer`'s per-candidate module
      attribution (`CrashThreadCandidateSnapshot.TopUserFrameModule`) had no observation of its own
      — its existing `runtime.crash-thread-candidate` subjects on the exception *type*, resolved
      before module attribution happens; closed with a new `runtime.crash-thread-candidate-module`
      observation, emitted once `BuildDomainResult` has run (module resolution happens later than
      the rest of `CrashAnalyzer`'s observation emissions). `ModuleAnalyzer`'s on-disk module size —
      the gap the held-reason did name — had no observation either (`runtime.module-heap-footprint`
      measures heap footprint, a different quantity); closed with a new
      `runtime.module-disk-footprint` observation from the already-capped `TopModulesBySize` list.
      The version-conflict flag needed *no* new emission — `runtime.module-conflict-group` already
      subjects per module with empty `Measures`, so presence alone is the signal. Its ranked module
      table was dropped, not ported — same "supplementary, not the whole payload" call
      `ReferenceChainDominatorCorrelation` made for its own table; the headline already states the
      dominant module, its share, count, size and conflict status. Migrated as
      `CrashModuleHotspotInsightRule`; two stale `InsightEngineTests.cs` cases (plus their shared
      `CrashCandidate` helper) removed, coverage moved to
      `InsightCorrelationRulesTests.CrashModuleHotspot`. Full suite: 1398 passed, 0 failed, 4 skipped
      (real-dump/trace, expected).
- [ ] **2 of 17 `CorrelationRuleGroup` methods remain held, for two distinct reasons, one method
      held on both** — neither is a routine "add one measure" gap the way most of steps 6.2's
      earlier work was:
      1. **The `EvidenceTables` decision above** holds `DetectMemoryTypeGenerationCorrelation` and
         `DetectStringMemoryConcentration` (and `DetectJitModuleHotspot` in `MemoryAndRuntimeRuleGroup`)
         — for all three, the table is the finding's main payload, not supplementary detail the way
         `ReferenceChainDominatorCorrelation`'s/`CrashModuleHotspot`'s were, so dropping it isn't a
         reasonable disclosed simplification.
      2. **`MemoryDomainResult.TopTypes` cross-joined against another unbounded per-type list needs
         a bigger design decision, not a quick fix.** `DetectMemoryTypeGenerationCorrelation` and
         `DetectStringMemoryConcentration` are the two remaining cases (the substring-scan cases,
         `DetectDataTableLifecyclePattern`/`DetectKnownLeakPatterns`, are closed above).
      Both stay in `InsightEngine.CorrelationRuleGroup` for now, each with its own reason documented
      inline in that class's leading comment.
- [ ] **Retire `InsightEngine` only once every rule is ported.** `DetectAnalyzerFailures` — the one
      open question about whether this could even happen without a separate design decision — is
      resolved (see above); `BaselineRuleGroup` is now gone entirely. What's left blocking full
      retirement is purely the 3 still-held methods (`DetectMemoryTypeGenerationCorrelation`,
      `DetectStringMemoryConcentration`, `DetectJitModuleHotspot`) — once those land, delete the
      class, `DumpOrchestrationService.cs`'s `new InsightEngine().Analyze(...)` call,
      `sessionState.Insights`, and `SingleDumpConsolePresenter.PrintInsights`.
- [ ] This closes the phase's own exit criterion — "`IFindingGenerator`/`InsightEngine` no longer
      exist as separate concepts" — jointly with 6.1 (6.1 closed the last surviving
      `IFindingGenerator`; 6.2 closes `InsightEngine`).

## 6.3 — Section builders become presentation-only (6a)

- [x] **Inline `SectionLeadFinding` constructions removed, shipped 2026-09-24.**
      - The re-run audit found two builders left, not the one expected: `LeakCandidateSectionBuilder`
        and `LohFragmentationSectionBuilder` (whose ≥30%/≥15% ladder the 2026-09-08 audit marked
        "duplicated, currently consistent" and never fixed). Both inline leads were deleted; both
        sections now get their lead from `NormalizeSectionContractSlots`.
      - No wording lost: the LOH builder's severity-specific title, byte/free-block summary, and
        recommendations moved into `LohFragmentationSynthesisRule` (it now also reads
        `heap.loh-fragmentation-summary` for free-block count and largest free block). The leak rule
        already carried everything the builder's lead had.
      - `NormalizeSectionContractSlots` no longer derives a lead from an `Info` top finding —
        previously the leak builder alone suppressed `Info` leads; now it's one rule for every
        section.
      - Confidence now comes from the rules (`MeasurementCertain`) instead of the builders' hard-coded
        0.9 / 0.75 — folding the ladder itself is § 6.4.
- [x] **Conformance test added, shipped 2026-09-24** — narrower than originally worded, by design.
      - `FitnessEnforcementTests.SectionBuilders_ShouldNotDecideLeadFindingsOrSeverity` bans
        `new SectionLeadFinding(` and hard-coded `Severity: "..."` literals under `SectionBuilders/`.
      - The original wording ("no threshold/band/severity comparison") isn't enforceable by grep:
        ~99 numeric comparisons across 25 builders, nearly all display logic (counts, byte-unit loops,
        truncation). Reading `FindingSeverity` off an already-synthesized finding (executive
        summary, narrative, insights) is presentation and stays allowed.
      - Full suite: 1402 passed, 0 failed, 65 skipped (same skip count as the pre-change baseline).
- [ ] Remaining builder-side judgment, each needs its own call (not caught by the test above):
      - [x] ~~`GetSeverityBand` in `LohFragmentationSectionBuilder` and
        `FinalizableObjectSectionBuilder`~~ — **removed 2026-09-24.** The LOH copy fed a
        `severity_band` key metric duplicating the rule's ≥30%/≥15% thresholds (the derived lead
        already shows severity; nothing else read it). The Finalizable copy had no callers at all.
      - [x] ~~Threshold-gated advice text in `HttpObjectSectionBuilder` (≥5/≥20/≥50/≥20)~~ —
        **removed 2026-09-24.** All four cut-offs were exact copies of `HttpObjectSynthesisRule`'s
        warning thresholds, and each matching finding already carries the same (or richer) advice in
        its `Narrative`/`Recommendation`, so the builder text was a second copy, not unique content.
      - [x] ~~Threshold-gated advice text in `GCGenerationSectionBuilder` (Gen2 ≥40%, LOH ≥35%)~~ —
        **removed 2026-09-24.** LOH ≥35% was an exact copy of `GCGenerationSynthesisRule`'s LOH
        Warning threshold. Gen2 ≥40% *diverged* from the rule's ≥50% Warning / ≥75% Critical, so
        40–50% Gen2 dumps got a "Gen2 dominates" note the rule never backed; resolved in the rule's
        favour (user call) — no note below 50%, rule unchanged.
      - Row-flagging reasons in `TypeSystemSectionBuilder` (>1% heap, >80% Gen2, >500 finalizable,
        >50 MB).
      - `LeakCandidateSectionBuilder.GetImpactBand` — size band, orthogonal to severity.

## 6.4 — Consolidate confidence ladders (6b)

- [ ] Confirm `SectionBuilderBase.SymbolForScore` is still the only remaining band/symbol ladder
      copy (per the audit doc, the other two were already folded into it) after 6.1/6.2 land.
- [ ] Fold that last copy into `ConfidenceBreakdown` per the parent doc's item 6b; delete the local
      ladder if `SymbolForScore` becomes a trivial `ConfidenceBreakdown` call.

## Sizing note

6.1 is done (see § 6.1). 6.2 is the large item: 39 rule methods, ~1,300 lines of detection logic.
All three groups have started; 36 of 39 methods are migrated, 3 held. `BaselineRuleGroup` is fully
done (11/11 — `DetectAnalyzerFailures` was the last, closed 2026-09-23) and
`MemoryAndRuntimeRuleGroup` (10/11) had a low gap rate — 8/11 and 6/10 needed zero new emission,
most of the rest needed one new measure on an existing observation, and two needed only a classifier
formula duplicated locally. `CorrelationRuleGroup` (15/17 so far) confirmed the sizing note's own
warning: its gap profile is different and harder — fewer single-measure gaps, more structural
blockers.

Two things surfaced there worth remembering for future groups:

- **One shared gap can unblock several rules at once.** `CrashDomainResult.ExceptionTypeCounts`
  had never been observed at all; closing it once unblocked 5 rules together. Worth checking for a
  shared dependency like this before auditing method-by-method next time.
- **A real SDK-level question surfaced, and got resolved by checking the report vision, not by
  extending the SDK.** `Sdk.Synthesis.Finding` has no equivalent of `InsightFinding.EvidenceTables`,
  which several rules' original methods used to attach a supplementary table. Ran that question
  against [ReportSystemVision.md](../../ReportStructure/ReportSystemVision.md) before building
  anything — § 6.2 already decided not to grow `Finding` toward `Claim`/report-rendering shape
  ahead of the report phase, and Appendix C marks `CompactTable` (what an `EvidenceTable` becomes)
  as exactly what the future `dd-table` widget replaces. So the SDK stays untouched; instead, each
  blocked rule was judged individually on whether its table was optional supplementary detail or
  the finding's whole payload. `ReferenceChainDominatorCorrelation` was the former — migrated
  without its table, headline preserved in `Narrative`, every offending type still individually
  cited via `DerivedFrom` for whenever a future `dd-table` wants to render the same list from
  observations directly. `CrashModuleHotspot` turned out to be the former too once actually
  migrated (2026-09-23) — its held-reason had grouped it with the latter category, but the headline
  already carried everything essential, so it dropped its table the same way.
  `MemoryTypeGenerationCorrelation`/`StringMemoryConcentration`/`JitModuleHotspot` are the latter —
  stay held.

The `DataTableLifecyclePattern`/`KnownLeakPatterns` half of the `MemoryDomainResult.TopTypes` gap,
`PinnedStringLeak`'s own separate gap, `ClusterHangCorrelation`'s two separate gaps (cluster
membership had no subject at all; wait-type was a categorical string with no home in numeric
`Measures`), `StaticRootFinalizableCorrelation`'s gap (a per-root retained-type breakdown with no
observation, closed without needing a new `root` `EntityKind`),
`LongHeldTransactionOnOpenConnection`'s gap (neither side carried a heap address at all, closed with
a second `ObjectRef` subject on each — `ObjectRef`'s `(artifact, address)` identity is explicitly
scoped to within-artifact joins, which this is), and `CrashModuleHotspot`'s two gaps (crash-candidate
module attribution never observed at all; on-disk module size a different quantity from heap
footprint — plus turning out not to be `EvidenceTables`-blocked after all) all closed 2026-09-22/23
via bounded/ordinal/`Evidence`/`ObjectRef`-carried observations, each scoped first against
[docs/ReportStructure/ReportSystemVision.md](../../ReportStructure/ReportSystemVision.md) — §5.4's
"no top-N, no cap" targets sampling an unbounded ranking, not a fixed small vocabulary or an
analyzer's own already-bounded top-N-by-bytes ranking; §14.2's column-store `"kind": "enum"` spec
(ordinal + fixed label dictionary) independently validated the `wait-type.ordinal` shape as
forward-compatible, not a stopgap — so none of these reopened settled decisions. The remaining two
held `CorrelationRuleGroup` methods (`MemoryTypeGenerationCorrelation`, `StringMemoryConcentration`)
need `MemoryDomainResult.TopTypes` cross-joined against another unbounded per-type list — a bigger
design decision the vision doc assigns to the future `dd-scatter` widget, not a routine emission
gap. See § "CorrelationRuleGroup" above for the full per-method breakdown.
`DetectAnalyzerFailures` (`BaselineRuleGroup`), the remaining held method from an earlier group,
closed 2026-09-23 — see its own entry above; `BaselineRuleGroup` is now fully migrated (11/11).
