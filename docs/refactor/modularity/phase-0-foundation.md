# Phase 0 — Foundation, Inventory & De-Dump-ification Audit

Part of [../modularity-plan.md](../modularity-plan.md). Prerequisite groundwork for every later
phase. No behavior change.

## Goal

Make later physical moves mechanical rather than exploratory, and — new in the multi-source
rework — find every place where "a dump" is baked into a name, type, or assumption that will need
to become source-neutral.

## Work items

1. **Contract surface audit. Done 2026-09-11.** For every `IAnalyzer` implementation, enumerate
   every type it touches outside its own file: constructor params, `AnalysisContext` members read,
   `IHeapAnalysisCache` methods called, `internal` types reached via `InternalsVisibleTo`. Output a
   generated (scripted, not hand-maintained) table at
   `docs/refactor/modularity/contract-surface-inventory.md`. This defines exactly what Phase 1's
   SDK must expose and nothing more. **Shipped**: `tools/ContractSurfaceAudit` (Roslyn
   syntax-only, no MSBuild/semantic model — same grep-derived-not-exhaustive standard
   [modularity-plan.md § 10 point 8](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)
   already used for the 35-analyzer Tier split) walks all 36 `IAnalyzer` implementations (35 plus
   `LeakCandidateAnalyzer`, which implements the marker sub-interface `IDeferredAnalyzer :
   IAnalyzer` — missed on the first pass, found and fixed while building item 3) and regenerates
   the inventory on demand (`dotnet run --project tools/ContractSurfaceAudit`). Confirms the split
   already found empirically during retyping: the 11 analyzers already ported onto
   `Sdk.Analysis.IAnalyzer` touch only the new capability-scoped surfaces (`Heap*`/`Runtime*`
   properties, no `Cache`/`Heap`/`Runtime` raw access), while the remaining 25 still read
   `AnalysisContext.Cache`/`.Heap`/`.Runtime` directly and, between them, call 17 of
   `IHeapAnalysisCache`'s 23 members — real evidence for what the Tier-1 surface table in
   [phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md) already
   claimed, not yet cross-checked against a full inventory until now.
2. **De-dump-ification audit** *(new)*. **Done 2026-09-11.** Catalog everything that assumes a
   single ClrMD dump is the only input. Expect at minimum: `DumpLoadContext`,
   `SingleDumpPipelineState`, `SingleDumpOrchestrationService` / `TrendOrchestrationService`,
   `SingleDumpReportDocument` / `TrendReportDocument`, `DumpIndexPaths`, `--baseline`/`--trend` CLI
   semantics, and every analyzer that takes `RuntimeFacade` directly rather than going through the
   cache. Each entry gets a disposition: *generalize* (becomes artifact-neutral),
   *becomes-a-dump-source-detail* (moves behind `IArtifactSource`), or *deleted* (subsumed by the
   session model). **Shipped**: hand-compiled (not scripted — assigning a disposition is judgment,
   unlike item 1's mechanical extraction) at
   [de-dump-ification-catalog.md](de-dump-ification-catalog.md), 13 entries — the 7 the doc named
   plus 6 more found in the process (`IAnalysisStage`/`StagedPipelineRunner`, `PerDumpExecutionService`,
   the CLI request/options' own `DumpPath`-shaped fields, `RuntimeAnalysisContext`, `DumpAnalysisService`,
   `AnalysisSnapshot`). Two of the doc's named items turned out smaller than expected on inspection:
   the storage/container layer (`DumpDetective.Platform`) is *already* source-neutral and shared with
   trace, and zero analyzers touch `RuntimeFacade` directly (item 1's SDK retyping already intercepts
   that path) — both reduce the estimated de-dump-ification effort rather than add to it. Every
   "deleted" disposition found was already independently named in
   [modularity-plan.md § 5](../modularity-plan.md#5-what-this-deletes)/§ 8; no new deletion
   candidates surfaced at the orchestration/pipeline layer. The one gap not previously named
   anywhere in the plan: the CLI's own `AnalysisCommandRequest`/`ResolvedExecutionOptions` records
   have no artifact-list concept at all, only scalar `DumpPath`/`BaselineDumpPath`/`TrendDumpPaths`
   fields — flagged for whichever phase ends up owning session construction from CLI args, not
   assigned here.
3. **Capability mapping** *(new)*. **Done 2026-09-11.** For each analyzer, record which
   capabilities from [source-model.md § 3](source-model.md) it actually needs — and, importantly,
   which it would *optionally* benefit from once trace exists. This table is the direct input to
   Phase 3's `[RequiresCapability]` attributes and to the graded-fidelity design; doing it now,
   while the dump-only behavior is the only behavior, avoids retrofitting guesses later.
   **Shipped**: [capability-mapping.md](capability-mapping.md), built by `tools/CapabilityMappingAudit`.
   Required capabilities are scripted (SDK analyzers: 1:1 from the `AnalysisContext` properties
   they read; legacy analyzers: `IHeapAnalysisCache` members plus raw ClrMD calls, each mapped to
   the capability its dump-side `*Query.cs` bridge actually exposes it through — ground-truthed
   against `CapabilityVocabulary.cs`, not guessed); optional trace capabilities are a hand-curated
   table embedded in the generator, since that's design judgment, not something derivable from
   dump-only source. Found and fixed two bugs while building this: `AllocationPatternAnalyzer`
   bypasses `IHeapAnalysisCache` entirely (casts `context.Cache` to the concrete `HeapAnalysisCache`
   to read `TypeAggregates` directly) — traced to `heap.types` by checking what
   `IHeapTypeStatisticsQuery` actually exposes, since it carries the identical fields; and
   `LeakCandidateAnalyzer : IDeferredAnalyzer` was invisible to both this item's tool and item 1's
   (base-list detection only matched `IAnalyzer` literally) — fixed in both tools, item 1's counts
   corrected from 35/11/24 to 36/11/25. 6 of 36 analyzers have no optional trace capability today:
   2 because their concern is inherently a snapshot (`ModuleAnalyzer`, `ObjectShapeAnalyzer`), 4
   because no matching capability exists yet — all four DB/SQL analyzers
   (`DbConnectionAnalyzer`, `SqlCommandAnalyzer`, `SqlConnectionPoolAnalyzer`,
   `SqlTransactionAnalyzer`) want something like `trace.db-events`, not currently in the
   vocabulary. Both are real inputs for Phase 3/6, not gaps in this audit. Item 3a (judgment-site
   inventory) is a separate, larger item — not started here.
3a. **Judgment-site inventory** *(new — from the
   [analyzer-pipeline audit](../analyzer-pipeline-stages-and-leadfinding-dedup.md))*. That audit
   already catalogued judgment duplication across stages 1–3 but explicitly left gaps: only 8 of 39
   section builders were audited for `SectionLeadFinding`, ~20 domain models were checked at
   field-shape level but not deep-read, `LeakCandidateRecord.Severity`'s computation site is
   untraced, and `ExplainableScoringEngine`'s independence is unconfirmed. Close those gaps here —
   every location that computes a severity, band, threshold, composite score, or row-selection
   ranking, across all four layers. This is the definitive input to Phase 5's synthesis-rule
   authoring, and Phase 5 cannot be scoped without it.
4. **Namespace re-org in place. Done 2026-09-11.** Move analyzers into per-domain namespaces
   mirroring the eventual plugin packages (see
   [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md)). Pure move, reviewable as a
   rename-only diff. **Shipped**: all 36 `IAnalyzer` implementations plus their `LegacyAdapter`
   siblings and domain-private helpers moved (`git mv`, history preserved) from the flat
   `Analyzers/` into `Analyzers/{Memory,Gc,Threads,Async,Types,Leaks,Infra,Runtime}/`, namespaced
   `DumpDetective.Analysis.Analyzers.<Package>` to match. `Plugins.Cpu` stays empty per the doc —
   no dump-fed analyzer belongs there yet. Two things the source doc's table didn't cover, resolved
   by inspection rather than guessed: (1) `SqlCommandAnalyzer`/`SqlConnectionPoolAnalyzer`/
   `SqlTransactionAnalyzer` aren't in phase-3's package table (post-dates it) — placed in
   `Infra` alongside `DbConnectionAnalyzer`, the existing precedent for DB/SQL connectivity
   analyzers, along with the typed-resource-quartet helpers they share
   (`ITypedResourceCandidateSource`, `TypedResourceScanDriver`, `TypedResourceSampler`,
   `ConnectionStringAnonymiser` — confirmed single-domain via a full-repo reference sweep before
   moving). (2) Three helpers (`AnalyzerHelpers`, `SegmentKindMapper`, `TypeNamePatternMatcher`)
   are consumed across 3+ target packages or by the `Sdk/` bridge layer itself — moving any one of
   them into a single package would violate phase-3's own "packages may not reference each other"
   rule, so they stay at the `Analyzers/` root, unmoved. Every consumer across
   `DumpDetective.Cli`/`.Reporting`/`.Tests`/`BenchmarkSuite1` fixed (mix of missing `using`
   directives, two catalog-style files enumerating every analyzer by name, and one file using
   fully-qualified `nameof(Analysis.Analyzers.X)` simplified to rely on the new `using`s). Also
   fixed: both this phase's own audit tools (`ContractSurfaceAudit`, `CapabilityMappingAudit`)
   scanned `Analyzers/` non-recursively and would have silently found zero analyzers after this
   move — switched to recursive scan, regenerated both docs, confirmed byte-identical per-row
   content (only row order changed, now naturally grouped by package). Verified: full solution
   builds clean (`Analysis`, `Cli`, `Reporting`, `Tests`, `BenchmarkSuite1`,
   `Sources.NetTrace`); full non-real-dump suite green, 1241 passed/0 failed/41 skipped; the 9
   architecture-conformance tests (item 6's harness) green, confirming the namespace move didn't
   cross a dependency-direction boundary.
5. **Characterization test coverage.** Any analyzer domain lacking a golden/snapshot test gets one
   *before* it moves. The safety net must exist before the motion, not after. **Bound by scenario
   diversity, not just domain presence** (per
   [modularity-plan.md § 10 point 6](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)):
   one snapshot per domain only proves the happy path survived the move, and this codebase's own
   history has a case of exactly that gap — a regex-drift regression in `AsyncStateMachineAnalyzer`
   that slipped past existing tests in the same session it was introduced, because the specific
   branch it broke wasn't covered. For each analyzer domain, enumerate the distinct severity tiers
   (Critical/Warning/Info/etc.) and distinct decision branches its finding-generation logic can
   reach, and cover each with a scenario before the domain moves — not one snapshot exercising
   whichever branch the sample dump happens to hit.
6. **Architecture-conformance harness — already exists, verified and cleaned up 2026-09-08.** A
   lightweight boundary test asserting today's intended dependency direction. This isn't new work:
   `tests/DumpDetective.Tests/Unit/Architecture/DependencyDirectionTests.cs` (project-reference
   direction: `Core` ← `Analysis` ← `Reporting` ← `Cli`, matching
   [architecture.md § 2](../../architecture.md#2-project-layout-and-dependency-graph)) and
   `FitnessEnforcementTests.cs` (source-level namespace-boundary checks for
   Core/Analysis/Reporting, plus a hotspot-guardrail file-existence check) already do this — built
   for an earlier, unrelated, now-concluded refactor program
   ([consolidated-refactor-program.md](../../improvements/consolidated-refactor-program.md)), but
   still real, still running, and directly reusable here. **Every later phase should add a rule to
   this existing harness rather than inventing a new one** — starting with Phase 1 step 7's
   SDK-boundary and registry-conformance rules.

   One stale leftover found and removed while verifying this: `FitnessEnforcementTests` had a fifth
   test, `BaselineHarness_ShouldExistForCiFitnessGate`, asserting a baseline script existed at
   `tools/Phase0/Invoke-Phase0Baseline.ps1`. That script (and the CI workflow that ran it) had been
   deliberately deleted in two prior commits once the older program concluded (`9f1aaebd` "remove
   concluded spike/validator tools and Phase0 harness", `16b99725` "removed phase8 workflow which
   just always fails anyway") — the test was never updated to match, leaving a permanently-red
   assertion for tooling that was retired on purpose. Removed rather than resurrected; the four
   remaining checks were all green before and after.

   **Residual gap, not closed**: there is currently no CI workflow running these tests
   automatically (`.github/workflows/` is empty — the old one was removed for always failing).
   "Green in CI" below currently means "green when run locally"; standing up real CI automation is a
   separate decision, not assumed here.

## Exit criteria

- Contract-surface inventory, de-dump-ification catalog (with dispositions), and capability map all
  checked in.
- `InternalsVisibleTo` entries catalogued with a disposition each.
- Analyzer namespaces match eventual package grouping.
- Every analyzer domain's distinct severity tiers and decision branches each have a covering
  characterization test — not just ≥ 1 test per domain.
- Architecture-conformance test green locally against current `main` (done, 2026-09-08 — 4/4 in
  `Unit/Architecture/`); CI automation to run it on every push/PR remains open, see the residual gap
  noted above.

## Risk / effort

Low risk, low-to-medium effort — but two items are genuinely intellectual work, not mechanical, and
are the ones most likely to be rushed:
- The capability map (item 3). Getting it wrong means Phase 3 ships analyzers with mis-declared
  requirements, which surfaces as "analyzer silently skipped" bugs that are annoying to diagnose.
- Characterization test coverage at the tightened bar (item 5). Enumerating each domain's severity
  tiers and branches is real analysis work per analyzer, not a mechanical snapshot-and-move — this
  is the phase's only safety net for Phases 4 and 5, the two highest-behavioral-risk phases in the
  plan, so under-scoping it here is the kind of gap that doesn't surface until much later.

Budget real time for both.
