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
6. **Architecture-conformance harness.** A lightweight boundary test (NetArchTest or a hand-rolled
   Roslyn/reflection check) asserting today's intended dependency direction. Every later phase adds
   a rule to this same harness rather than inventing a new enforcement mechanism.

## Exit criteria

- Contract-surface inventory, de-dump-ification catalog (with dispositions), and capability map all
  checked in.
- `InternalsVisibleTo` entries catalogued with a disposition each.
- Analyzer namespaces match eventual package grouping.
- Every analyzer domain's distinct severity tiers and decision branches each have a covering
  characterization test — not just ≥ 1 test per domain.
- Architecture-conformance test green in CI against current `main`.

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
