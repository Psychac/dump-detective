# Phase 3 — Capability-Driven Plugin Packaging & Discovery

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 3**
(in-process). Depends on [phase-2-artifact-platform.md](phase-2-artifact-platform.md).

**Status, 2026-09-15: migration steps 1–5 done, step 6 is a standing architecture rule (no action
item).** All 36 analyzers live in 8 domain packages (`Plugins.Memory/Gc/Threads/Async/Types/Leaks/
Runtime/Infra`), discovered by attribute scan (`PluginCatalogBuilder`) rather than the deleted
hand-maintained `DefaultAnalyzerFeatureModuleCatalog`; a `plugins/` drop-in + ALC loader path is
built and proven against a real out-of-tree test plugin. See "Known gaps" below for what's
deliberately not done yet and why. Started ahead of the modularity plan's own
[§8 minimum-viable path](../modularity-plan.md#8-the-minimum-viable-unified-path--adopted-as-the-chosen-plan-2026-09-08),
which had deferred the plugin split until the trace/correlation capability proved itself — per
explicit instruction this session, not a formal revisiting of that deferral. Made possible by the
36/36 analyzer retyping to the SDK `IAnalyzer`/`AnalysisContext` contract finishing first (commit
`b713c165`), since step 2 needs every analyzer to already be a real `Sdk.Analysis.IAnalyzer`.

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
| `Plugins.Infra` | DbConnection, WcfChannel, HttpObject, TimerLeak, SqlCommand, SqlConnectionPool, SqlTransaction | `trace.http-events` |
| `Plugins.Runtime` | Module, Crash, Jit, HeapTopology | `trace.jit-events`, `trace.exception-events` |
| `Plugins.Cpu` | *(none — dump can't provide this)* | `trace.cpu-samples` |

`Plugins.Cpu` existing with zero dump-fed analyzers is the model working as intended: a package
whose analyzers simply don't run in a dump-only session, no special-casing required.

Each package carries its analyzers plus their `AnalyzerDomainResult` subtypes and trend comparers.
Section builders and finding generators stay behind in `Reporting` for now — see "Known gaps".
Packages may not reference each other.

## Key design decisions

- **Discovery by attribute + assembly scan**, replacing the hardcoded catalog:

  ```csharp
  [AnalyzerModule(key: "gc-pressure", displayName: "GC Pressure", order: 110, tags: ["gc"])]
  [RequiresCapability("heap.generations", "heap.objects")]
  [OptionalCapability("trace.gc-events", Fidelity = FidelityBoost.Major)]
  internal sealed class GcPressureAnalyzer : IAnalyzer { ... }
  ```

- **Capability satisfaction is computed, not configured.** At session start the orchestrator
  (Phase 4, not yet built) intersects `session.AvailableCapabilities` with each analyzer's
  requirements and produces three buckets: *runnable at full fidelity*, *runnable degraded*
  (required met, optional missing), *skipped* (required unmet, with the specific missing
  capability recorded). That third bucket becomes user-facing output — "supply a `.nettrace` to
  enable 12 more analyzers."
- **Fidelity is reported, not hidden.** An analyzer running degraded stamps
  `Provenance.CapabilitiesUsed` and `FidelityLevel` on every observation it emits, which flows into
  confidence scoring. A user must be able to tell whether "GC pressure: moderate" came from heap
  composition alone or from measured pause times.
- **Validated at discovery, not silently ignored.** SDK-compatibility and capability-vocabulary
  checks (`DropInPluginLoader`) fail fast with a clear message naming the plugin/analyzer/value,
  rather than a plugin referencing a nonexistent capability just never running.
- **ALC isolation for drop-in plugins only.** Built-in packages load into the default context
  (no overhead); anything in a `plugins/` directory gets a collectible `AssemblyLoadContext`.
  Crash-level containment is Phase 9, not this phase.

## Migration steps

1. **Done (Phase 1).** Capability attributes (`AnalyzerModuleAttribute`, `RequiresCapabilityAttribute`,
   `OptionalCapabilityAttribute`) and `CapabilityVocabulary` shipped in `DumpDetective.Sdk` during
   Phase 1, ahead of any analyzer using them.
2. **Done, 2026-09-15 (commit `541c1317`).** Built `PluginCatalogBuilder`
   ([src/DumpDetective.Reporting/Capabilities/PluginCatalogBuilder.cs](../../../src/DumpDetective.Reporting/Capabilities/PluginCatalogBuilder.cs)),
   which scans assemblies for `[AnalyzerModule]`-attributed analyzers and resolves each one's
   `FindingGenerator`/`TrendComparer`/`SectionBuilder` companions by naming convention
   (`{AnalyzerName minus trailing "Analyzer"}{Suffix}`) rather than declaring them on the
   attribute — checked first: the convention already matched 31/35 analyzers' real names, so the
   handful that didn't (plus `StaticRootLeakDetector` → `StaticRootAnalyzer`) were renamed to fit,
   rather than growing the attribute with redundant `typeof()` references. All 36 analyzers now
   carry `[AnalyzerModule]`.
3. **Done, 2026-09-15.** Split all 36 analyzers into the 8 packages above, one package per commit,
   in this order: `Memory` → `Gc` → `Threads` → `Async` → `Types` → `Leaks` → `Runtime` → `Infra`.
   Recurring patterns worth remembering (not per-package operational checklist items anymore, since
   there's no package left to apply them to):
   - **Colocated models with an unrelated consumer.** Several domain results had a small type
     (`RegionGenerationKind`/`TypeGenerationProfile` in `Memory`, `DistributionSummary` in `Types`,
     `HeapSegmentKind` in `Runtime`, `EventLeakEvidence` in `Leaks`) colocated in the same file but
     actually consumed by still-in-`Analysis` infrastructure unrelated to the moving analyzer.
     Each was split into its own file and left behind in `Analysis.Models` rather than moved.
   - **Cross-analyzer `DomainResult` reads, fixed the same way twice.** `LeakCandidateAnalyzer`
     (reading `GCHandleDomainResult`/`GCRootDomainResult`/`TimerLeakDomainResult`) and
     `TrendAnalyzer`'s new-leak-signal heuristic (reading `DominatorDomainResult`/
     `StaticRootDomainResult`/`LeakCandidateDomainResult`) both read another domain's result by
     concrete type, which would have forced a forbidden `Analysis → Plugins.*` dependency once
     those models moved. Both were fixed the same way (commits ahead of `Plugins.Gc` and
     `Plugins.Leaks` respectively): narrow read-only structural signal interfaces
     (`IHandleRetentionSignal`, `IDirectRootSignal`, `ILogicalTimerCountSignal`,
     `IRetainedObjectsByTypeSignal`, `IStaticRootsByDescriptionSignal`,
     `ILeakCandidatesByTypeSignal`) added to `DumpDetective.Core.Models`, which the producing
     `DomainResult`s implement and the neutral consumer reads via `GetResult<TInterface>()` instead
     of the concrete type.
   - **Shared pipeline infra stays in `Analysis`.** Query implementations and scan-orchestration
     types consumed by analyzers across multiple packages (`IRuntimeThreadQuery` family for
     `Threads`/`Runtime`, the typed-resource scan infra for `Async`/`Gc`/`Types`/`Infra`) stayed put
     in `DumpDetective.Analysis`, reached by plain forward references — moving any of them into one
     package would have created a `Plugins.* → Plugins.*` dependency the architecture forbids.
   - **`InternalsVisibleTo` grants.** Every package needed `DumpDetective.Analysis → Plugins.X`
     (internal pipeline-participant interfaces) and `DumpDetective.Core → Plugins.X`
     (`IAnalyzerTrendComparer`/`AnalyzerMetric`/`MetricDelta`, all internal in `Core.Models`), plus
     package-specific internal surfaces as needed (e.g. `Plugins.Leaks` also needed
     `EventGroupInfo`/`EventLeakInfo`/`SubscriberInfo`).
   - **`InsightEngine`** (cross-analyzer scoring) directly references every analyzer's
     `DomainResult` type, so it moved from `Analysis/Insight/` to `Reporting/Insight/` once, ahead
     of `Plugins.Memory` — `Reporting` already needed to depend on every plugin package anyway to
     wire up the catalog, so this cost nothing per package after the first move.
   - Every package with real-dump characterization tests was re-verified against the actual
     reference dump, one test at a time, foreground, per this doc's own real-dump testing rule —
     no behavioral regressions found across all 8 packages.

   **The one durable, still-open gap this step surfaced: `FindingGenerator`/`SectionBuilder`
   stayed behind in `DumpDetective.Reporting`, not the target shape.** `IAnalyzerSectionBuilder`,
   `IFindingGenerator`, and the ~40-record report-block DSL all live inside `Reporting` itself —
   moving a package's companions with its analyzer would create a `Plugins.X → Reporting` /
   `Reporting → Plugins.X` cycle (Reporting needs every package to build the catalog). Neither
   `DumpDetective.Core` (deliberately still dump-runtime-aware until Phase 7) nor
   `DumpDetective.Sdk` (its `Synthesis` namespace is reserved for the *new* observation/finding
   schema Phase 5 introduces to replace this exact DSL) is the right permanent home. This closes
   either when Phase 5 ships that replacement, or sooner if a real need forces it — not resolved
   here, and directly the reason step 5's drop-in plugins can only contribute a bare `IAnalyzer`
   today (see "Known gaps").
4. **Done, 2026-09-15.** Deleted `DefaultAnalyzerFeatureModuleCatalog.cs` — `PluginCatalogBuilder`
   is now the only `IAnalyzerFeatureModuleCatalog` implementation, exposed via
   `PluginCatalogBuilder.BuildDefault()` (scans the 8 domain-package assemblies), used by DI wiring
   (`ServiceRegistration.BuildHost`) and both factories' parameterless compatibility constructors
   (`DefaultAnalyzerFactory`, `DefaultSectionBuilderFactory`). The "before/after catalog snapshot
   diff" this step called for was taken literally:
   [PluginCatalogBuilderTests](../../../tests/DumpDetective.Tests/Unit/Reporting/PluginCatalogBuilderTests.cs)
   no longer diffs against a live old-catalog instance (which no longer exists) — it asserts
   against a frozen snapshot array (key/displayName/order/tags for all 36 modules) captured from
   the old catalog's source immediately before deletion, plus an explicit test that companion
   types follow the naming convention `PluginCatalogBuilder` relies on.
5. **Done, 2026-09-15.** Added `DropInPluginLoader`
   ([src/DumpDetective.Reporting/Capabilities/DropInPluginLoader.cs](../../../src/DumpDetective.Reporting/Capabilities/DropInPluginLoader.cs)):
   scans a `plugins/` root for `<PluginName>/<PluginName>.dll` subdirectories, loads each into its
   own collectible `AssemblyLoadContext` (standard `AssemblyDependencyResolver`-plus-fallback-to-
   `Default` pattern, so `DumpDetective.Sdk` type identity stays shared between host and plugin),
   then discovers `[AnalyzerModule]`-attributed `IAnalyzer` types inside it, validating SDK-major-
   version compatibility and `[RequiresCapability]`/`[OptionalCapability]` names against
   `CapabilityVocabulary.Known` at discovery time. Proven against a real out-of-tree fixture, not a
   mock — [tests/Fixtures/DumpDetective.TestPlugin](../../../tests/Fixtures/DumpDetective.TestPlugin)
   references `DumpDetective.Sdk` only, and
   [DropInPluginLoaderTests](../../../tests/DumpDetective.Tests/Unit/Reporting/DropInPluginLoaderTests.cs)
   copies its build output into a scratch drop-in layout and loads it for real. See "Known gaps"
   for what this step deliberately stops short of.
6. Architecture rule (standing, not a one-time action): plugin packages reference `Sdk` + `Platform`
   only — never `Sources.*`, and never each other. **An analyzer must not reference
   `Sources.ClrDump`** — if one needs something only ClrMD can answer, that's a missing capability
   surface in Phase 2, not a license to reference the dump source directly. This rule is what keeps
   analyzers source-neutral, and it will be the most-violated rule in the plan. Note:
   `Sources.ClrDump` as a distinct project doesn't exist yet — that reorganization is still
   deferred, tracked in [phase-2-artifact-platform.md](phase-2-artifact-platform.md#migration-steps)
   with its actual owner now [phase-6-trace-source.md](phase-6-trace-source.md#phase-6a--trace-ingest).
   Until it lands, this rule applies to referencing dump-specific code under `Analysis` directly,
   not a literal `Sources.*` project boundary — re-check this rule's wording once the reorganization
   happens.

## Known gaps

Carried forward, not resolved by this phase — each is explicitly Phase 4 or Phase 5's to close:

- **Drop-in plugins can only contribute a bare `IAnalyzer`.** `IFindingGenerator`,
  `IAnalyzerTrendComparer`, and `IAnalyzerSectionBuilder` are all `internal` to `Core`/`Reporting`
  — unimplementable by a genuinely out-of-tree assembly with no `InternalsVisibleTo` grant. A
  discovered plugin analyzer is never turned into an `AnalyzerFeatureModule` or wired into
  `ServiceRegistration`/report output today. Closing this needs Phase 5's synthesis/observation
  schema to replace the `FindingGenerator`/`SectionBuilder`/`TrendComparer` DSL with something a
  plugin can genuinely implement against a public contract.
- **No orchestrator exists to consume capability declarations.** `[RequiresCapability]`/
  `[OptionalCapability]` are validated for syntactic correctness (known vocabulary members) but
  nothing computes the runnable/degraded/skipped buckets described under "Key design decisions" —
  that's Phase 4. **Closed, 2026-09-16** — `CapabilityResolver` (Phase 4 step 2) now computes all
  three buckets for real declarations (Phase 4 step 3), and `ConsoleUx.CapabilityReport` surfaces
  them. See [phase-4-session-orchestration.md step 3](phase-4-session-orchestration.md#migration-steps).
- **Analyzers don't declare capabilities yet.** Step 1 shipped the attributes ahead of any real
  use; the 36 in-repo analyzers carry `[AnalyzerModule]` but not `[RequiresCapability]`/
  `[OptionalCapability]` — there's no consumer for those declarations yet (previous bullet), so
  adding them now would be speculative. Do this once Phase 4 exists to act on it. **Closed,
  2026-09-16** — all 36 analyzers now declare real `[RequiresCapability]`/`[OptionalCapability]`,
  transcribed from a re-verified `capability-mapping.md` (the generating tool itself was found
  stale — still scanning the pre-Phase-3 analyzer location — and fixed first). See
  [phase-4-session-orchestration.md step 3](phase-4-session-orchestration.md#migration-steps).
- **`AnalyzerScope { PerArtifact | PerSession }` — missed here, not caught until Phase 4, corrected
  2026-09-16.** [phase-4-session-orchestration.md](phase-4-session-orchestration.md)'s design
  section assumed this SDK attribute would land in "Phase 1/3," but neither this phase's migration
  steps nor its target attribute list (`AnalyzerModuleAttribute`/`RequiresCapabilityAttribute`/
  `OptionalCapabilityAttribute`, step 1 above) ever actually claimed it — a real gap between what
  Phase 4 assumed upstream and what upstream delivered. Reassigned to Phase 4 itself
  ([step 5](phase-4-session-orchestration.md#migration-steps)), which is also the first phase with
  a real consumer for it (mixed dump+trace sessions).

## Exit criteria

- ✅ `DefaultAnalyzerFeatureModuleCatalog.cs` gone; adding a built-in analyzer touches only its
  package.
- ✅ CLI behavior (`--tags`/`--only`/`--skip`, ordering) unchanged, verified by existing
  integration tests.
- ✅ Every analyzer declares capabilities (2026-09-16, via
  [phase-4-session-orchestration.md step 3](phase-4-session-orchestration.md#migration-steps)); no
  analyzer references any `Sources.*` package — still true, unchanged by this.
- ⬜ Capability satisfaction report is produced for a dump-only session and correctly lists
  `Plugins.Cpu` analyzers as skipped-for-missing-`trace.cpu-samples` — split in two, 2026-09-16:
  the report itself (runnable/degraded/skipped buckets, visible output) is **done**, via
  [phase-4-session-orchestration.md step 3](phase-4-session-orchestration.md#migration-steps); the
  specific `Plugins.Cpu`-skipped case can't be demonstrated until the dump and trace analyzer
  catalogs are unified (`Plugins.Cpu` has zero analyzers in today's dump-only catalog —
  `CpuHotspotAnalyzer` lives in `Sources.NetTrace`), which is
  [phase-4-session-orchestration.md step 5](phase-4-session-orchestration.md#migration-steps).

## Risk / effort

Retrospective: the touches-all-analyzers scope was the real cost, not any single step's
difficulty, as expected going in. The no-`Sources.ClrDump`-reference rule (step 6) never actually
triggered a violation during the migration — every analyzer already reached dump-specific data
only through SDK-defined capability interfaces by the time this phase started, a consequence of
the Phase 1 retyping that preceded it rather than anything enforced during this phase itself.
