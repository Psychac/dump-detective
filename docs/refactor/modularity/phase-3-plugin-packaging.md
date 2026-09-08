# Phase 3 — Capability-Driven Plugin Packaging & Discovery

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 3**
(in-process). Depends on [phase-2-artifact-platform.md](phase-2-artifact-platform.md).

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
| `Plugins.Leaks` | LeakCandidate, StaticRootLeakDetector, ReferenceChain, Dominator, EventLeak | `trace.alloc-samples` |
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

1. Add the capability attributes to the SDK (Phase 1 reserved them).
2. Build `PluginCatalogBuilder` (scan → catalog), producing the same shape the old catalog did so
   consumers don't change yet.
3. Split analyzers + their reporting pieces into the packages above; annotate each with the
   capability declarations from Phase 0's capability map.
4. Delete `DefaultAnalyzerFeatureModuleCatalog.cs`, verified by a before/after catalog snapshot
   diff.
5. Add the `plugins/` drop-in + ALC loader path with at least one test plugin.
6. Architecture rule: plugin packages reference `Sdk` + `Platform` only — never `Sources.*`, and
   never each other. **An analyzer must not reference `Sources.ClrDump`** — if one needs something
   only ClrMD can answer, that's a missing capability surface in Phase 2, not a license to
   reference the dump source directly. This rule is what keeps analyzers source-neutral, and it
   will be the most-violated rule in the plan.

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
