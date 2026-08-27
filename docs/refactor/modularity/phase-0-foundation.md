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
4. **Namespace re-org in place.** Move analyzers into per-domain namespaces mirroring the eventual
   plugin packages (see [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md)). Pure move,
   reviewable as a rename-only diff.
5. **Characterization test coverage.** Any analyzer domain lacking a golden/snapshot test gets one
   *before* it moves. The safety net must exist before the motion, not after.
6. **Architecture-conformance harness.** A lightweight boundary test (NetArchTest or a hand-rolled
   Roslyn/reflection check) asserting today's intended dependency direction. Every later phase adds
   a rule to this same harness rather than inventing a new enforcement mechanism.

## Exit criteria

- Contract-surface inventory, de-dump-ification catalog (with dispositions), and capability map all
  checked in.
- `InternalsVisibleTo` entries catalogued with a disposition each.
- Analyzer namespaces match eventual package grouping.
- Every analyzer domain has ≥ 1 characterization test.
- Architecture-conformance test green in CI against current `main`.

## Risk / effort

Low risk, low-to-medium effort — but the capability map (item 3) is genuinely intellectual work,
not mechanical, and it's the item most likely to be rushed. Getting it wrong means Phase 3 ships
analyzers with mis-declared requirements, which surfaces as "analyzer silently skipped" bugs that
are annoying to diagnose. Budget real time for it.
