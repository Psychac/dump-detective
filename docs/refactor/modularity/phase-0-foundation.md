# Phase 0 — Foundation, Inventory & De-Dump-ification Audit

Part of [../modularity-plan.md](../modularity-plan.md). Prerequisite groundwork for every later
phase. No behavior change.

## Goal

Make later physical moves mechanical rather than exploratory, and — new in the multi-source
rework — find every place where "a dump" is baked into a name, type, or assumption that will need
to become source-neutral.

## Work items

1. **Contract surface audit.** For every `IAnalyzer` implementation, enumerate every type it
   touches outside its own file: constructor params, `AnalysisContext` members read,
   `IHeapAnalysisCache` methods called, `internal` types reached via `InternalsVisibleTo`. Output a
   generated (scripted, not hand-maintained) table at
   `docs/refactor/modularity/contract-surface-inventory.md`. This defines exactly what Phase 1's
   SDK must expose and nothing more.
2. **De-dump-ification audit** *(new)*. Catalog everything that assumes a single ClrMD dump is the
   only input. Expect at minimum: `DumpLoadContext`, `SingleDumpPipelineState`,
   `SingleDumpOrchestrationService` / `TrendOrchestrationService`, `SingleDumpReportDocument` /
   `TrendReportDocument`, `DumpIndexPaths`, `--baseline`/`--trend` CLI semantics, and every
   analyzer that takes `RuntimeFacade` directly rather than going through the cache. Each entry
   gets a disposition: *generalize* (becomes artifact-neutral), *becomes-a-dump-source-detail*
   (moves behind `IArtifactSource`), or *deleted* (subsumed by the session model).
3. **Capability mapping** *(new)*. For each analyzer, record which capabilities from
   [source-model.md § 3](source-model.md) it actually needs — and, importantly, which it would
   *optionally* benefit from once trace exists. This table is the direct input to Phase 3's
   `[RequiresCapability]` attributes and to the graded-fidelity design; doing it now, while the
   dump-only behavior is the only behavior, avoids retrofitting guesses later.
3a. **Judgment-site inventory** *(new — from the
   [analyzer-pipeline audit](../analyzer-pipeline-stages-and-leadfinding-dedup.md))*. That audit
   already catalogued judgment duplication across stages 1–3 but explicitly left gaps: only 8 of 39
   section builders were audited for `SectionLeadFinding`, ~20 domain models were checked at
   field-shape level but not deep-read, `LeakCandidateRecord.Severity`'s computation site is
   untraced, and `ExplainableScoringEngine`'s independence is unconfirmed. Close those gaps here —
   every location that computes a severity, band, threshold, composite score, or row-selection
   ranking, across all four layers. This is the definitive input to Phase 5's synthesis-rule
   authoring, and Phase 5 cannot be scoped without it.
4. **Namespace re-org in place.** Move analyzers into per-domain namespaces mirroring the eventual
   plugin packages (see [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md)). Pure move,
   reviewable as a rename-only diff.
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
