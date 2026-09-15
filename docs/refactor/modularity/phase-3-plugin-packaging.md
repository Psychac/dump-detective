# Phase 3 — Capability-Driven Plugin Packaging & Discovery

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 3**
(in-process). Depends on [phase-2-artifact-platform.md](phase-2-artifact-platform.md).

**Status, 2026-09-15: migration steps 1–2 done, step 3 in progress (`Plugins.Memory`,
`Plugins.Gc`, `Plugins.Threads`, and `Plugins.Async` shipped, 4 packages left), steps 4–6 not
started.** Started ahead of the
modularity plan's own [§8 minimum-viable path](../modularity-plan.md#8-the-minimum-viable-unified-path--adopted-as-the-chosen-plan-2026-09-08),
which had deferred the plugin split until the trace/correlation capability proved itself — per
explicit instruction this session, not a formal revisiting of that deferral. Made possible by the
36/36 analyzer retyping to the SDK `IAnalyzer`/`AnalysisContext` contract finishing first (commit
`b713c165`), since step 2 needs every analyzer to already be a real `Sdk.Analysis.IAnalyzer`.
Commit `541c1317`.

## Goal

Kill `DefaultAnalyzerFeatureModuleCatalog` as a hand-maintained list, and make analyzers declare
**what data they need** rather than **what source they belong to** — the mechanism that lets one
analyzer serve dump-only, trace-only, and combined sessions at graded fidelity.

## Target packaging

Grouped by diagnostic domain, deliberately *not* by source — a domain package may contain
analyzers fed by heap capabilities, trace capabilities, or both:

| Package | Analyzers (current dump-fed set) | Trace capabilities it will grow into |
|---|---|---|
| `Plugins.Memory` | Memory, GCGeneration, AllocationPattern, LohFragmentation, SegmentReservation | `trace.alloc-samples` |
| `Plugins.Gc` | GCRoot, GCHandle, FinalizableObject, WeakReference | `trace.gc-events` |
| `Plugins.Threads` | Thread, ThreadStackCluster, Hang, LockGraph | `trace.contention-events`, `trace.thread-timeline` |
| `Plugins.Async` | AsyncTask, AsyncStateMachine | `trace.thread-timeline` |
| `Plugins.Types` | ObjectShape, Array, Boxing, Collection, String | `trace.alloc-samples` |
| `Plugins.Leaks` | LeakCandidate, StaticRootAnalyzer, ReferenceChain, Dominator, EventLeak | `trace.alloc-samples` |
| `Plugins.Infra` | DbConnection, WcfChannel, HttpObject, TimerLeak | `trace.http-events` |
| `Plugins.Runtime` | Module, Crash, Jit, HeapTopology | `trace.jit-events`, `trace.exception-events` |
| `Plugins.Cpu` | *(none — dump can't provide this)* | `trace.cpu-samples` |

`Plugins.Cpu` existing with zero dump-fed analyzers is the model working as intended: a package
whose analyzers simply don't run in a dump-only session, no special-casing required.

Each package carries its analyzers plus their section builders, synthesis rules, and
`AnalyzerDomainResult` subtypes. Packages may not reference each other.

## Key design decisions

- **Discovery by attribute + assembly scan**, replacing the hardcoded catalog:

  ```csharp
  [AnalyzerModule(key: "gc-pressure", displayName: "GC Pressure", order: 110, tags: ["gc"])]
  [RequiresCapability("heap.generations", "heap.objects")]
  [OptionalCapability("trace.gc-events", Fidelity = FidelityBoost.Major)]
  internal sealed class GcPressureAnalyzer : IAnalyzer { ... }
  ```

- **Capability satisfaction is computed, not configured.** At session start the orchestrator
  (Phase 4) intersects `session.AvailableCapabilities` with each analyzer's requirements and
  produces three buckets: *runnable at full fidelity*, *runnable degraded* (required met, optional
  missing), *skipped* (required unmet, with the specific missing capability recorded). That third
  bucket becomes user-facing output — "supply a `.nettrace` to enable 12 more analyzers."
- **Fidelity is reported, not hidden.** An analyzer running degraded stamps
  `Provenance.CapabilitiesUsed` and `FidelityLevel` on every observation it emits, which flows into
  confidence scoring. A user must be able to tell whether "GC pressure: moderate" came from heap
  composition alone or from measured pause times.
- **Manifest per package**, carrying SDK compatibility and declared capability usage — validated at
  load against the Phase 1 registries, so a plugin referencing a nonexistent capability fails at
  discovery with a clear message rather than silently never running.
- **ALC isolation for drop-in plugins only.** Built-in packages load into the default context
  (no overhead); anything in a `plugins/` directory gets a collectible `AssemblyLoadContext`.
  Crash-level containment is Phase 9, not this phase.

## Migration steps

1. **Done (Phase 1).** Capability attributes (`AnalyzerModuleAttribute`, `RequiresCapabilityAttribute`,
   `OptionalCapabilityAttribute`) and `CapabilityVocabulary` shipped in `DumpDetective.Sdk` during
   Phase 1, ahead of any analyzer using them.
2. **Done, 2026-09-15 (commit `541c1317`).** Built `PluginCatalogBuilder`
   ([src/DumpDetective.Reporting/Capabilities/PluginCatalogBuilder.cs](../../../src/DumpDetective.Reporting/Capabilities/PluginCatalogBuilder.cs)),
   producing the same shape the old catalog does — not wired into DI yet, verified instead by a
   before/after equivalence test suite
   ([PluginCatalogBuilderTests.cs](../../../tests/DumpDetective.Tests/Unit/Reporting/PluginCatalogBuilderTests.cs))
   run against `DefaultAnalyzerFeatureModuleCatalog` while both still exist side by side, rather than
   deferring that diff to step 4 as originally planned. All 36 analyzers now carry `[AnalyzerModule]`.
   One design change from what this doc originally sketched: `FindingGenerator`/`TrendComparer`/
   `SectionBuilder` companion types are **not** declared on the attribute — `AnalyzerModuleAttribute`
   stayed `key`/`displayName`/`order`/`tags` only. Checked first: stripping a trailing `Analyzer` from
   the analyzer's own type name and appending `FindingGenerator`/`TrendComparer`/`SectionBuilder`
   already matched 31 of 35 analyzers' real names exactly, so `PluginCatalogBuilder` resolves
   companions by that convention via reflection instead, failing fast with a clear message on any
   miss. The 7 mismatched companion types (plus `StaticRootLeakDetector`, renamed to
   `StaticRootAnalyzer` so the "strip `Analyzer`" rule has zero exceptions) were renamed to fit,
   rather than growing the attribute to carry redundant `typeof()` references — e.g.
   `ExceptionAnalysisSectionBuilder` → `CrashSectionBuilder`, `LeakAnalysisSectionBuilder` →
   `LeakCandidateSectionBuilder`. Full list in the commit.
3. Split analyzers + their reporting pieces into the packages above; annotate each with the
   capability declarations from Phase 0's capability map.
   **`Plugins.Memory` done, 2026-09-15** — first package shipped end-to-end (`MemoryAnalyzer`,
   `GCGenerationAnalyzer`, `AllocationPatternAnalyzer`, `LohFragmentationAnalyzer`,
   `SegmentReservationAnalyzer`). Real, non-speculative dependency findings from doing this one:
   - **`FindingGenerator`/`SectionBuilder` stayed in `DumpDetective.Reporting` — interim, not the
     target shape.** `IAnalyzerSectionBuilder`, `AnalyzerDetailSection`, and the ~40-record
     report-block DSL (`SectionBuilderBase`, `SectionIdDomainMap`, `MetricUtils`) all live inside
     `Reporting` itself today, not in a shared contract layer a plugin package can implement
     against without `Plugins.Memory → Reporting` — but `Reporting` already needs
     `Reporting → Plugins.Memory` to wire up `DefaultAnalyzerFeatureModuleCatalog`/
     `PluginCatalogBuilder`, so moving the companions with the analyzer creates a cycle. Neither
     `DumpDetective.Core` (deliberately still dump-runtime-aware until Phase 7, see
     modularity-plan.md § 10 point 8) nor `DumpDetective.Sdk` (its `Synthesis` namespace already
     reserved for the *new* observation/finding schema Phase 5 introduces to replace this exact
     DSL) is the right permanent home. **This is a real gap, not resolved here** — every future
     package will hit the same cycle for its `SectionBuilder`. It should close either when Phase 5
     replaces `FindingGenerator`/`SectionBuilder`/`TrendComparer` with the generic synthesis
     differ, or sooner if a package needs its `SectionBuilder` to move before then — at which
     point extracting the DSL contract into a proper shared location (likely `Sdk`, once it has a
     second real consumer to shape it against) becomes non-speculative and should be done as its
     own change, not folded into the next package's migration.
   - Two model types (`RegionGenerationKind`, `TypeGenerationProfile`) were colocated with a
     specific analyzer's `DomainResult` but consumed by other, unrelated analyzers still in
     `Analysis` — split into their own files and kept in `Analysis.Models` rather than moved.
     Worth checking for on every future package: a `DomainResult` file moving isn't guaranteed to
     be self-contained.
   - `InsightEngine` (cross-analyzer scoring, lived in `Analysis/Insight/`) directly referenced
     every analyzer's `DomainResult` type, which would force `Analysis → Plugins.Memory` — the
     mirror-image cycle to the `SectionBuilder` one, but this one had a clean fix: `InsightEngine`
     is a pure, source-neutral consumer of already-produced domain results (no ClrMD/heap access
     of its own), so it moved to `Reporting` (which already depended on both `Analysis` and, now,
     `Plugins.Memory`). Each future package should check whether `InsightEngine` references its
     `DomainResult` types too — it does, for most domains — though the fix, once made once, does
     not need repeating per package.

   **`Plugins.Gc` done, 2026-09-15** — second package shipped end-to-end (`GCRootAnalyzer`,
   `GCHandleAnalyzer`, `FinalizableObjectAnalyzer`, `WeakReferenceAnalyzer`). Unlike
   `Plugins.Memory`, this one hit a real cross-*analyzer* dependency, not just a shared-model one:
   - **`LeakCandidateAnalyzer`** (`Analysis/Analyzers/Leaks/`, destined for the future
     `Plugins.Threads` package, not this one) is the only analyzer in the codebase that reads
     another analyzer's already-completed `DomainResult` directly — `GCHandleDomainResult` and
     `GCRootDomainResult` here, plus `TimerLeakDomainResult` and a static reference to
     `TimerLeakAnalyzer.NamedTimerWrapperTypeNames` for a different future package. Left as-is,
     this would have forced the same "leave the model behind in `Analysis.Models`" carve-out
     `Plugins.Memory` needed for `RegionGenerationKind`/`TypeGenerationProfile` — except recurring
     for every package `LeakCandidateAnalyzer` touches, not a one-off. Landed as its own commit
     ahead of the package move (commit `c724344`): three narrow structural signal interfaces
     (`IHandleRetentionSignal`, `IDirectRootSignal`, `ILogicalTimerCountSignal`) plus a shared
     `TimerWrapperTypeNames` vocabulary constant, added to `DumpDetective.Core.Models` (zero
     project references, already reachable from `Analysis` and every plugin package).
     `GCHandleDomainResult`/`GCRootDomainResult`/`TimerLeakDomainResult` implement the interfaces;
     `LeakCandidateAnalyzer` now reads via `GetResult<TInterface>()` instead of the concrete types,
     and `AnalyzerRunResultsExtensions.GetResult<T>`'s constraint relaxed from
     `where T : AnalyzerDomainResult` to `where T : class` to allow it. Pure refactor, no
     scheduling change — `LeakCandidateAnalyzer` still runs deferred exactly as before. This let
     **all four** `Gc` models move with no carve-out, and the same interface pattern is reusable
     the next time a package hits this shape of problem (e.g. whenever `TimerLeakAnalyzer` gets its
     own package).
   - `GCRootAnalysisProjection` (`Analysis/Utilities/`) turned out to depend on `RootFinding`/
     `RootKindSummary` (defined alongside `GCRootDomainResult`), so it moved into
     `Plugins.Gc/Utilities/` too, even though it currently has no callers anywhere — dead code, but
     moving it was cheaper and safer than deleting unrelated code as a side effect of this
     migration. `MemoryAnalysisProjection`'s equivalent stayed in `Analysis/Utilities` in the
     `Plugins.Memory` migration precisely because it *doesn't* depend on the moved models — the
     rule for these utility classes is "does it reference the domain's own models," not "which
     analyzer does it serve."
   - `PluginCatalogBuilderTests.BuildScannedCatalog` had been (accidentally) scanning all of
     `Analysis` via `typeof(GCRootAnalyzer).Assembly`, since `GCRootAnalyzer` used to live there.
     Moving it out silently dropped ~27 still-in-`Analysis` analyzers from the test's module-count
     check until it was pointed at `LeakCandidateAnalyzer`'s assembly instead — worth checking for
     on every future package: whatever anchors this test's scan of "the rest of `Analysis`" needs
     to keep pointing at something that stays there.

   **`Plugins.Threads` done, 2026-09-15** — third package shipped end-to-end (`ThreadAnalyzer`,
   `ThreadStackClusterAnalyzer`, `HangAnalyzer`, `LockGraphAnalyzer`, plus `ThreadWaitClassifier`).
   Unlike `Plugins.Gc`, this move found no cross-analyzer `DomainResult` reads and no colocated-
   model carve-out — the quartet's only real dependency risk was infrastructure, not models:
   - `JitAnalyzer` and `CrashAnalyzer` (destined for the future `Plugins.Runtime` package) also
     consume `IRuntimeThreadQuery` for exception/thread correlation, so the concrete query
     implementations (`RuntimeThreadQuery`, `PrecomputedRuntimeThreadQuery`,
     `RuntimeThreadPoolQuery`, `HeapSyncBlockQuery`, `ThreadStackTranslator`, all in
     `Analysis/Sdk/`) and the thread-stack scan orchestration (`IThreadStackScanParticipant`,
     `ThreadStackScanState`, `ThreadStackScanDispatcher`, `AnalysisPipeline`,
     `AnalysisContextBuilder`, all in `Analysis/Pipeline/`) stayed in `DumpDetective.Analysis`
     rather than moving into `Plugins.Threads`. No code changes were needed to make this work —
     every analyzer in the quartet already consumed this infra only through the SDK-defined
     interfaces, so the package boundary just falls where the interface already was.
   - As with `Plugins.Gc`/`Plugins.Memory`, `InsightEngine` (`Reporting/Insight/`) directly
     references all four `DomainResult` types, so `Reporting` now depends on `Plugins.Threads`
     too — consistent with, not a new instance of, the pattern already accepted for the prior two
     packages.
   - `ThreadStackClusterAnalysisOptions` (and `ThreadAnalysisOptions`) stayed in
     `DumpDetective.Core/Options/`, matching where `GCHandleAnalysisOptions`/
     `GCGenerationAnalysisOptions`/`MemoryAnalysisOptions` already live for the shipped packages —
     analyzer options are not part of what a package migration moves.
   - `PluginCatalogBuilderTests.BuildScannedCatalog` anchor list gained a fourth assembly
     (`ThreadAnalyzer`'s), alongside the still-in-`Analysis` `LeakCandidateAnalyzer` anchor and the
     two prior packages' anchors — unaffected otherwise, since none of the thread quartet's types
     were ever the scan's anchor.

   **`Plugins.Async` done, 2026-09-15** — fourth package shipped end-to-end (`AsyncTaskAnalyzer`,
   `AsyncStateMachineAnalyzer`). The cleanest move so far — no cross-analyzer `DomainResult` reads
   to unwind (unlike `Plugins.Gc`) and no shared infra to leave behind beyond what `Plugins.Threads`
   already established as the pattern:
   - Files moved (`git mv`, namespaces left unchanged — `DumpDetective.Analysis.Analyzers.Async`,
     `DumpDetective.Analysis.Models`, `DumpDetective.Analysis.Trend.Comparers` — matching every
     prior package's choice to avoid a cascading rename across `DefaultAnalyzerFeatureModuleCatalog`,
     `InsightEngine`, and tests) needed two more `InternalsVisibleTo` grants than the checklist
     implied: `DumpDetective.Analysis` → `Plugins.Async` (for the `internal` pipeline-participant
     interfaces: `IHeapIndexScanParticipant`, `IParallelHeapIndexScanParticipant`,
     `ICustomizesAnalysisContext`, `ITypedResourceCandidateSource`, `RuntimeAnalysisContext`,
     `TypedResourceHeapIndexScanState`) and `DumpDetective.Core` → `Plugins.Async` (for
     `IAnalyzerTrendComparer`/`AnalyzerMetric`/`MetricDelta`, all `internal` in `Core.Models`).
     Worth checking explicitly on every future package, not just assumed from the `.csproj`
     template: which `internal` surfaces the moved files reach into, on both `Analysis` and `Core`.
   - **`AsyncStateMachineNamePattern`** (`Analysis/Utilities/`) looked like a clean move — used by
     `AsyncStateMachineAnalyzer.IsCandidateType` and nothing else in `Analysis`'s own analyzer code —
     until `DiskBackedObjectIndexWriter.IsAsyncStateMachineType` (Phase 1 heap-index-build code,
     `Analysis/Indexing/`) turned out to share the exact same regex to compute
     `TypeAggregateFlags.IsAsyncStateMachineType` at scan time. Moving the utility into
     `Plugins.Async` would have created the reverse-direction dependency the architecture rule
     forbids (`Analysis` → a `Plugins.*` package). Left in `Analysis/Utilities/` instead — a plain
     forward reference from `Plugins.Async` (already an allowed direction) costs nothing. The
     `Plugins.Memory`-style "does it reference the domain's own models" heuristic doesn't catch this
     shape of coupling: check every utility a moving analyzer touches for callers *outside* the
     domain, not just for dependencies *within* it.
   - `DependencyDirectionTests.RefactoredProjects_ShouldFollowStrictDependencyDirection`
     (`tests/DumpDetective.Tests/Unit/Architecture/`) asserts `Reporting`'s exact project-reference
     set — and was never updated when `Plugins.Threads` shipped, so it was already silently wrong
     (missing `Plugins.Threads`) before this migration touched it. Fixed both gaps in the same edit.
     This test should be the first thing checked when landing any future package, since nothing
     else in the build catches a missing entry here.
   - `DumpDetective.slnx` had the same latent gap — `Plugins.Threads` was never added to the
     solution file either. Added both `Plugins.Threads` and `Plugins.Async` here.
   - `PluginCatalogBuilderTests.BuildScannedCatalog` anchor list gained a fifth assembly
     (`AsyncTaskAnalyzer`'s).
4. Delete `DefaultAnalyzerFeatureModuleCatalog.cs`, verified by a before/after catalog snapshot
   diff.
5. Add the `plugins/` drop-in + ALC loader path with at least one test plugin.
6. Architecture rule: plugin packages reference `Sdk` + `Platform` only — never `Sources.*`, and
   never each other. **An analyzer must not reference `Sources.ClrDump`** — if one needs something
   only ClrMD can answer, that's a missing capability surface in Phase 2, not a license to
   reference the dump source directly. This rule is what keeps analyzers source-neutral, and it
   will be the most-violated rule in the plan. Note: `Sources.ClrDump` as a distinct project
   doesn't exist yet — that reorganization is still deferred, tracked in
   [phase-2-artifact-platform.md](phase-2-artifact-platform.md#migration-steps) with its actual
   owner now [phase-6-trace-source.md](phase-6-trace-source.md#phase-6a--trace-ingest). Until it
   lands, this rule applies to referencing dump-specific code under `Analysis` directly, not a
   literal `Sources.*` project boundary — re-check this rule's wording once the reorganization
   happens.

## Exit criteria

- `DefaultAnalyzerFeatureModuleCatalog.cs` gone; adding an analyzer touches only its package.
- Every analyzer declares capabilities; no analyzer references any `Sources.*` package.
- CLI behavior (`--tags`/`--only`/`--skip`, ordering) unchanged, verified by existing integration
  tests.
- Capability satisfaction report is produced for a dump-only session and correctly lists
  `Plugins.Cpu` analyzers as skipped-for-missing-`trace.cpu-samples`.

## Risk / effort

High effort (touches all ~30 analyzers), medium risk. The scope is the problem, not the difficulty.
The rule most likely to break is the no-`Sources.ClrDump`-reference one: some analyzers today reach
for `RuntimeFacade` directly, and each such case needs a capability surface designed for it in
Phase 2. Phase 0's contract-surface inventory should have identified every one of these — if it
didn't, they surface here as painful mid-phase design work.
