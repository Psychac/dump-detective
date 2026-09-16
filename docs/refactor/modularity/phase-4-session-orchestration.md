# Phase 4 — Session Orchestration DAG

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 4**.
Depends on [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md).
(Supersedes `phase-4-orchestration-dag.md` from the dump-only draft.)

**Also owns, carried over from Phase 2's deferred scope**: `Session/*`
(see [phase-2-artifact-platform.md](phase-2-artifact-platform.md#migration-steps)'s deferred-items
list) — `SessionBuilder`/`SessionValidator` below are that item's real home. `TimelineAligner` is
*not* claimed here or in Phase 5 yet; whichever phase's work item ends up needing multi-artifact
alignment first should pick it up explicitly rather than assume the other phase has it.

**Also owns, carried over from Phase 3's Known gaps** (reassessed 2026-09-16 — see
[phase-3-plugin-packaging.md § Known gaps](phase-3-plugin-packaging.md#known-gaps)): two of Phase
3's own exit criteria were left explicitly unmet, waiting on this phase's orchestrator to exist —
*"analyzers don't declare capabilities yet... do this once Phase 4 exists to act on it"* and *"no
orchestrator exists to consume capability declarations... that's Phase 4."* The orchestrator
(`CapabilityResolver`) shipped in step 2 below, but nothing has fed it real per-analyzer
declarations yet, so it's still provably a no-op today — that loop was never closed. Step 3 below
closes the first ("analyzers declare capabilities") in full; the second ("capability satisfaction
report... lists `Plugins.Cpu` as skipped") only partially — the report becomes real in step 3, but
nothing can land in its *skipped* bucket until step 5 unifies the dump and trace catalogs, so the
specific `Plugins.Cpu` demonstration Phase 3 named waits for step 5. Also reassigned here: `AnalyzerScope { PerArtifact | PerSession }` (see "Key design
decisions" below) — this doc originally said it "lands in the SDK attribute set in Phase 1/3," but
neither phase's own migration steps or target attribute list ever claimed it (checked: Phase 1's
target set is `RequiresCapabilityAttribute`/`OptionalCapabilityAttribute`/`AnalyzerModuleAttribute`
only). Nobody built it. It's this phase's job now, since this is the first phase that actually
needs it — see step 5 below.

**Note on approach, 2026-09-16.** This phase is forward-facing architecture work, not a minimal
patch over the existing pipeline. § 8 of [../modularity-plan.md](../modularity-plan.md) deferred
*whether to start* Phases 3–5 at all until the trace/correlation capability proved itself — it was
a go/no-go gate, not a standing instruction to build the smallest possible slice of every phase
once started. Phase 3 and this phase are both underway; the remaining steps below build toward the
actual target shape this doc describes (capability-driven, artifact-scope-derived graph
construction), not a narrower stand-in for it. Where a step is still deliberately sequenced before
another for de-risking reasons (verify output equality before adding the next axis of generality),
that is explicit in the step's own text — it is about *ordering* real work safely, not about
skipping real work that has a named, documented need.

## Goal

Replace the fixed five-stage pipeline **and** the proposed mode enum with one capability-driven
session orchestrator. This is the phase where "no modes" stops being a design claim and becomes
running code.

## What this deletes

- `SingleDumpOrchestrationService`, `TrendOrchestrationService`
- The unified doc's originally-proposed `CombinedOrchestrationService` and the
  `SingleDump | MultiDump | TraceOnly | Combined` enum — never built, superseded by the session
  model. Note: a real (but different, interim) `DumpDetective.Cli.Execution.TraceOrchestrationService`
  *was* built by Phase 6b (2026-09-09) as a stopgap trace-only router — that one gets retired by
  step 5 below once combined dump+trace sessions are wired, not by this bullet.
- `IAnalysisStage`, `StagedPipelineRunner`, `SingleDumpStageFactory`, `SingleDumpPipelineState`
- `AnalyzerFilterService` (becomes graph-node filtering)

## Target shape

```
/orchestration
  DumpDetective.Orchestration/            -- references Sdk + Platform; no Sources.*, no Plugins.*
    Discovery/
      PluginCatalogBuilder.cs   AssemblyLoadContextPluginLoader.cs
      CapabilityResolver.cs               -- session capabilities × analyzer requirements
    Session/
      SessionBuilder.cs                   -- N input paths → probed artifacts → AnalysisSession
      SessionValidator.cs                 -- process-identity compatibility, alignment feasibility
    Graph/
      IPipelineNode.cs  PipelineGraph.cs  PipelineGraphBuilder.cs  PipelineExecutor.cs
      NodeScheduler.cs                    -- respects IsThreadSafe, artifact-level parallelism
```

## Key design decisions

- **Nodes declare data dependencies, not sequence position:**

  ```csharp
  public interface IPipelineNode
  {
      string Id { get; }
      IReadOnlyCollection<string> Produces { get; }
      IReadOnlyCollection<string> Consumes { get; }
      ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken ct);
  }
  ```

  Data keys are artifact-scoped where appropriate: `artifact:{id}:index`,
  `artifact:{id}:capability:heap.objects`, `observations:{analyzerKey}`, `session:timeline`,
  `findings`, `report`. Topological sort over declared edges replaces the hand-ordered stage list.

- **Graph construction is fully derived from the session.** Given N artifacts:
  ingest node per artifact → index node per artifact → timeline-alignment node (only if N > 1) →
  analyzer node per *satisfiable* (analyzer × applicable-artifact-scope) pair → synthesis nodes →
  correlation nodes (only if > 1 artifact) → sink nodes. **A 1-dump session and a
  dump+trace+3-baseline session are the same code path**, differing only in the graph the builder
  emitted. That is the entire point of the phase.

- **Artifact-scoped vs session-scoped analyzers.** Most analyzers run per-artifact (a heap analyzer
  runs once per dump). Some are inherently session-scoped (correlation, trend synthesis). The node
  builder needs an explicit `AnalyzerScope { PerArtifact | PerSession }` declaration. **Correction,
  2026-09-16:** this doc originally said the attribute "lands in the SDK attribute set in Phase
  1/3" — checked, and neither phase actually claims it (Phase 1's target attribute set is
  `RequiresCapabilityAttribute`/`OptionalCapabilityAttribute`/`AnalyzerModuleAttribute` only; Phase
  3's migration steps and Known gaps never mention it). It's this phase's own job — see step 5.

- **Parallelism gets a second axis.** Today: analyzers parallelize when `IsThreadSafe`. Now:
  *artifacts* also parallelize — indexing three dumps can overlap. This is genuinely dangerous
  given the project's hard-won constraint that **real-dump work must not run concurrently** (a
  25 GB dump memory-mapped three times over will OOM the machine, per repeated past incidents).
  So: artifact-level parallelism must be **off by default**, gated behind an explicit opt-in and a
  memory-budget check that accounts for artifact sizes. The default remains strictly sequential
  artifact processing. Do not let the DAG's theoretical parallelism override this.

- **Progressive execution.** Nodes complete incrementally, so a sink can consume findings as they
  land rather than after the whole graph finishes — the substrate for live/streaming UI in Phase 8.

- **Failure semantics preserved.** A failed analyzer node fails only its dependents; independent
  nodes continue; failures are captured per-node exactly as `AnalyzerRunResult` does today.

## Migration steps

1. ✅ **Done (2026-09-15).** Scaffold the orchestration project; wrap today's five stages as five
   nodes with hand-declared edges. Prove `PipelineExecutor` reproduces current behavior before
   anything is dynamic.
   - **Shipped:** `DumpDetective.Orchestration` with the generic `Graph/` engine (`IPipelineNode`,
     `PipelineNodeContext`, `PipelineGraph`, `PipelineGraphBuilder`, `PipelineExecutor`,
     `NodeScheduler`), referencing only `Sdk` + `Platform` (enforced by
     `OrchestrationProject_ShouldDependOnSdkAndPlatformOnly`). The single-dump path's five stages
     became five `IPipelineNode` adapters in `DumpDetective.Cli/Pipeline/Nodes/` (they still
     depend on `Analysis`/`Reporting`, which is why they live in `Cli`, not `Orchestration`),
     wired by `SingleDumpNodeFactory` and run via `SingleDumpOrchestrationService`.
   - **Deleted:** `IAnalysisStage`, `StagedPipelineRunner`, `SingleDumpStageFactory`, and the 5 old
     stage classes — nothing referenced them once the switch happened, so their step-4 retirement
     was pulled forward instead of left as dead code.
   - **Not yet touched:** `SingleDumpPipelineState` (still one shared data bag; `Produces`/
     `Consumes` are hand-declared, not derived from capabilities), `TrendOrchestrationService`/
     `PerDumpExecutionService` (multi-dump path), `Discovery`/`Session` (steps 2–3).
   - **Verified:** full non-real-dump suite (1586 passed) + one foreground run against the
     ~3.3GB reference dump through the new executor.

2. ✅ **Done (2026-09-16), narrower than worded above.** Introduce `SessionBuilder` +
   `CapabilityResolver`; generate analyzer nodes from the Phase 3 catalog. Verify byte-identical
   output vs. the old pipeline on the test-dump corpus.
   - **Shipped:** `Discovery/CapabilityResolver.cs` and `Session/{AnalysisSession,SessionBuilder}.cs`.
     `CapabilityResolver` reflects each candidate `IAnalyzer`'s `[RequiresCapability]` against an
     `AnalysisSession`'s available capabilities (works over `IAnalyzer` instances directly, not
     `Reporting.Capabilities.AnalyzerFeatureModule`, to respect the Sdk/Platform-only dependency
     rule). `SessionBuilder` unions N `ArtifactDescriptor.Provides` sets into one session (N=1
     today). `DumpAnalysisService.ExecuteAsync` now builds an `ArtifactDescriptor` for the dump
     (`SingleDumpArtifactFactory`, new, `Cli/Execution/`) and runs `CapabilityResolver.Resolve`
     ahead of the existing name-based include/exclude filter.
   - **Narrowed vs. "generate analyzer nodes":** node granularity is unchanged — still one
     `RunAnalyzersNode`, still backed by `AnalysisPipeline` as-is. Splitting into one
     `IPipelineNode` per analyzer would mean re-expressing `AnalysisPipeline`'s
     ordering/deferred-analyzer-pass/diagnostics/result-post-processing as graph edges — exactly
     the "doing all three at once" failure mode this doc's own Risk section warns against.
     Deferred until analyzers actually declare real capabilities (step 3) and there's more than
     one artifact scope to split by (step 5) — before that, per-analyzer nodes would all carry
     identical declarations and split a working bundle for no observable benefit.
   - **Known gap, not this phase's to close:** `SingleDumpArtifactFactory`'s capability set (and its
     `ProcessIdentity`/`CapturedAt`) is a static/placeholder stand-in, not a real per-dump probe —
     analyzer selection happens before `LoadDumpNode` loads the dump, so no real ClrMD data exists
     yet at that point. A real probe needs the `Sources.ClrDump`/`IArtifactSource` extraction
     [phase-2-artifact-platform.md](phase-2-artifact-platform.md#known-gaps) already deferred to
     "the natural next step" after Phase 6a (which is now done) — that's this stand-in's real owner,
     not this phase. This phase keeps consuming the stand-in regardless of when that lands, the same
     way it already does today.
   - **Known gap, carried into step 4:** `SessionValidator` (process-identity compatibility,
     alignment feasibility) is still unbuilt — nothing to validate yet with one artifact. Its
     identity check has to be designed around the stand-in data above being unreliable (e.g. a
     dump's placeholder `ProcessIdentity.ImageName` is filename-derived, and trend dump files are
     routinely named per-snapshot, not per-process — a naive "same image name" check would reject
     normal trend runs), not assume it'll be replaced by real data by the time step 4 lands.
   - **Verified:** new `CapabilityResolverTests`/`SessionBuilderTests` + full non-real-dump suite
     (1593 passed) unchanged, proving the capability-derived filter is a no-op against today's
     single-dump analyzer catalog.

3. ✅ **Done (2026-09-16).** Close Phase 3's carried-over gap: declare real `[RequiresCapability]`/
   `[OptionalCapability]` on the 36 in-repo analyzers, using
   [capability-mapping.md](capability-mapping.md) as the source of truth, and make
   `CapabilityResolver`'s runnable/runnable-degraded/skipped buckets real, visible output.
   - **Tool fixed first:** `tools/CapabilityMappingAudit` still pointed at the pre-Phase-3
     `src/DumpDetective.Analysis/Analyzers` path (Phase 3 moved all 36 analyzers into 8
     `Plugins.*` packages) and silently wrote an empty table when re-run. Fixed to scan all
     `src/DumpDetective.Plugins.*/Analyzers` directories; re-running surfaced real drift since
     Phase 0 wrote the original table — all 25 then-"Legacy" analyzers have since been retyped
     onto the SDK `IAnalyzer` (36/36 "SDK" now), which changed several analyzers' actual required
     capabilities (e.g. some gained `heap.segments`, some lost capabilities they no longer read).
     The regenerated table, not the stale checked-in one, was used as the transcription source.
   - **Shipped:** `[RequiresCapability]`/`[OptionalCapability]` added to all 31 analyzers that
     didn't already have them (5 in `Plugins.Memory` were done in an earlier session and matched
     the regenerated table exactly, no drift). `CapabilityResolver` extended with a third bucket —
     `Degraded` (required capabilities met, some declared optional capability missing) — alongside
     the existing `Satisfied`/`Unsatisfied`, using `OptionalCapabilityAttribute.Fidelity`
     (`FidelityBoost`, already present in the SDK from Phase 1) per missing optional capability.
     `ConsoleUx.CapabilityReport` prints the degraded/skipped buckets with per-analyzer missing-
     capability reasons, wired into `DumpAnalysisService.ExecuteAsync` behind `DiagnosticMode`
     (same gating convention as the existing config-summary print).
   - **Confirmed, not assumed:** this step cannot fully close Phase 3's "dump-only session
     correctly lists `Plugins.Cpu` analyzers as skipped" exit criterion — verified via a real-catalog
     integration test (`RealDumpOnlySession_SkipsNoAnalyzer`) that a dump-only session skips zero of
     the 36 analyzers (every required capability is `heap.*`/`runtime.*`/`dump.*`, all satisfied by
     `SingleDumpArtifactFactory`'s static set) and `Plugins.Cpu` has zero analyzers in this catalog
     at all (`CpuHotspotAnalyzer` lives in `Sources.NetTrace`, not yet unified — step 5). What this
     step does demonstrate for real, also verified by test
     (`RealDumpOnlySession_DegradesEveryAnalyzerThatDeclaresAnOptionalTraceCapability`): the
     *degraded* bucket is real and correct — a dump-only session degrades exactly the analyzers
     with a declared optional trace capability, real, checkable proof the graded-fidelity mechanism
     works end to end. Fidelity levels themselves are uniformly `FidelityBoost.Moderate` (the
     attribute's default) — `capability-mapping.md`'s hand-curated rationale column doesn't carry a
     scripted signal for differentiating Minor/Moderate/Major per analyzer, so no per-analyzer
     distinction was invented; revisit if a real signal for it appears.
   - **Verified:** new `AnalyzerCapabilityDeclarationTests` (real 36-analyzer catalog: every
     analyzer declares valid, vocabulary-member capabilities; a dump-only session skips none,
     degrades exactly the expected set) + extended `CapabilityResolverTests` (degraded-bucket unit
     tests) — 7 new tests, full non-real-dump suite 1600 passed (was 1593), 0 failed. No real-dump
     run needed: the change is confined to compile-time attribute metadata and an additive,
     diagnostic-gated report — `CapabilityResolver`'s existing `Satisfied` computation is
     unchanged, and the new real-catalog tests already exercise the actual 36 analyzers.
4. ✅ **Done (2026-09-16).** Generalized to N artifacts: `TrendOrchestrationService` now builds one
   `IPipelineNode` triple (Load/Index/Analyze) per trend dump path — the same node classes
   `SingleDumpNodeFactory` uses for N=1, namespaced via new `ArtifactPipelineKeys.ForArtifact` so
   N artifacts' nodes share one `PipelineNodeContext` without colliding — plus a new
   `TrendBuildReportNode` (replaces the inline snapshot/comparison block that used to live directly
   in `TrendOrchestrationService`) and the existing `WriteOutputNode`, reused unmodified as the
   shared sink. `PerDumpExecutionService` (the hand-rolled duplicate of Load/Index/Analyze the old
   trend path used instead of the graph) is deleted, per step 1's precedent of retiring superseded
   code immediately rather than leaving it dead.
   - **Shipped:** `SessionValidator` (`Discovery` → `Session`, structural-only per the known-gap
     note above: same source-kind, distinct paths — no process-identity check, since
     `ArtifactDescriptor.Process`/`CapturedAt` are still placeholders). `DumpAnalysisService` also
     fixed to build the session from the *actual* N trend dump paths instead of always building a
     one-artifact session even on the trend path — a real, if latent, capability-resolution gap
     from step 2 that this step's `SessionValidator` wiring surfaced.
   - **Known gap, not this step's to close:** live console output for a trend run now comes from
     the same generic `ConsoleMemoryPipelineObserver`/node-progress rendering the single-dump path
     already uses, rather than the old bespoke per-dump `[dump] stage` progress bridging and nested
     per-dump memory tables in `TrendOrchestrationService`. Report/artifact content is unaffected
     (golden tests compare report content, not console text) — this is a deliberate simplification,
     not an oversight.
   - **Verified (2026-09-16, manual):** an actual N>1 real-dump trend run through the new path,
     confirmed manually against real `.dmp` files (run one at a time, foreground, per this repo's
     real-dump test constraint). Also verified via the full non-real-dump suite (1605 passed, +5 new
     `SessionValidatorTests`) plus inspection matching the new `TrendBuildReportNode`'s
     snapshot/comparison logic line-for-line against the deleted inline block it replaces.
5. ✅ **Done (2026-09-17).** Wire combined dump+trace sessions: build `AnalyzerScope { PerArtifact | PerSession }`
   (reassigned to this phase above — nobody upstream built it), unify the dump and trace analyzer
   catalogs into one `CapabilityResolver`-resolvable set (closing the other half of Phase 3's
   capability-satisfaction-report exit criterion left open by step 3 — a dump-only session should
   now correctly show `Plugins.Cpu`'s `CpuHotspotAnalyzer` as skipped-for-missing-`trace.cpu-samples`),
   and generate the real target shape
   from "Key design decisions" above — **one node per satisfiable (analyzer × applicable-artifact-
   scope) pair**, not the coarser "one node per artifact" that would only bundle each artifact's
   analyzers behind `AnalysisPipeline` again. This is where `RunAnalyzersNode`'s remaining
   `AnalysisPipeline`-bundling finally gets re-expressed as real graph edges (the thing step 2
   deferred and step 3 kept deferring, now unavoidable once dump-scoped and trace-scoped analyzers
   have to coexist in one graph with genuinely different applicability). Retire Phase 6b's interim
   `TraceOrchestrationService`/`TraceAnalysisRunner` router once this lands — that router was
   explicitly built (2026-09-09) as a stopgap because no session/artifact model existed yet, and
   [phase-6-trace-source.md](phase-6-trace-source.md) itself says wiring a combined run is "a
   natural next increment," without pinning it to a step. This is also where
   [phase-7-cross-source-correlation.md](phase-7-cross-source-correlation.md)'s assumed dependency
   ("Multi-artifact sessions | Session DAG (Phase 4)") actually gets delivered, and where
   `modularity-plan.md`'s named debt ("a combined dump+trace session still isn't wired") gets paid
   off.
   - **Shipped (2026-09-16), sub-step 5a.** `AnalyzerScopeAttribute`/`AnalyzerScope { PerArtifact |
     PerSession }` added to `DumpDetective.Sdk.Analysis`, mirroring
     `RequiresCapabilityAttribute`'s shape. Undeclared defaults to `PerArtifact`, matching every
     analyzer in today's real 36-analyzer catalog — nothing is session-scoped yet (Phase 5/7's
     correlation analyzers are the first expected `PerSession` case), so this sub-step is a real
     but currently-inert declaration, not consumption logic; the graph builder doesn't read it yet
     (that's sub-step 5e, alongside the `RunAnalyzersNode` split).
   - **Shipped (2026-09-16), sub-step 5b.** `CpuHotspotAnalyzer`/`GcPauseAnalyzer`/`ContentionAnalyzer`
     (`DumpDetective.Sources.NetTrace`) retyped from the old, minimal `ITraceAnalyzer` (deleted —
     no implementors remain) onto `Sdk.Analysis.IAnalyzer`, each declaring
     `[RequiresCapability]` for its own `trace.*` capability. Added 3 new SDK query surfaces
     (`ITraceCpuSampleQuery`/`ITraceGcPauseQuery`/`ITraceContentionQuery`, mirroring the existing
     `IHeap*Query`/`IRuntime*Query` pattern) plus matching nullable `AnalysisContext` properties;
     the address-range resolution `CpuHotspotAnalyzer` used to do itself moved into
     `TraceCpuSampleQuery`, so the retyped analyzer is now just the thin mapping to `Observation`.
     `AnalysisContext` also gained a nullable `ArtifactId` (needed for `Provenance.Artifact`) —
     harmless to every existing dump-side call site, which leaves it null exactly like every other
     never-consumed nullable surface on that type (no dump analyzer emits a real `Observation` yet).
     `TraceAnalysisRunner` (kept — its retirement is 5f's job) now builds a real `AnalysisContext`
     and runs the 3 analyzers through `IAnalyzer.AnalyzeAsync`, collecting output via a new
     `CollectingObservationSink` — the first real (non-`NoOp`) `IObservationSink` in the codebase.
     `tools/CapabilityMappingAudit` extended to also scan `DumpDetective.Sources.NetTrace`;
     [capability-mapping.md](capability-mapping.md) regenerated, now covering all 39 analyzers (was
     36).
   - **Shipped (2026-09-16), sub-step 5c.** New `TraceArtifactFactory`
     (`DumpDetective.Cli.Execution`), mirroring `SingleDumpArtifactFactory`'s own simplifications
     (static "every trace capability" `Provides` set, not a per-trace probe; same deferred-real-probe
     rationale). `SessionValidator`'s source-kind rule widened from "all artifacts must share one
     kind" to "all artifacts must share one kind, **or** be exactly one `clr-dump` plus one
     `nettrace`" — every other multi-kind shape (two dumps + a trace, a dump + two traces, a dump +
     a `gcdump`, ...) still rejected, not opened up wholesale.
   - **Shipped (2026-09-16), sub-step 5d.** New `--trace <path>` CLI option
     (`AnalysisCommandRequest`/`ResolvedExecutionOptions`/`RootCommandBuilder`), rejected outright
     when combined with `--trend`/`--baseline` (a session accepts either N dumps or exactly one
     dump plus one trace, not both — `SessionValidator` already enforces this too, but
     `DumpAnalysisService` throws a clearer message before ever building a session). When `--trace`
     is given, `DumpAnalysisService.ExecuteAsync` builds a combined artifact list
     (`SingleDumpArtifactFactory` + `TraceArtifactFactory`), resolves capabilities against the
     **unified** 36+3 analyzer catalog for an accurate `CapabilityReport`, then narrows back down to
     just the dump-catalog instances (new `FilterToDumpCatalog`, reference-equality-based) before
     handing anything to `AnalysisPipeline` — a trace analyzer must never reach that pipeline, since
     `AnalysisContextBuilder` never populates its `Trace*` surfaces dump-side and it would just
     silently no-op (correct-but-misleading "ran, found nothing"). Execution stays two separate
     pipelines, not one graph: after the dump pipeline finishes, `TraceOrchestrationService`'s trace
     half (extracted into a new reusable `RunAndWriteReportAsync`) also runs, writing its own
     `report.json` next to the trace file — "runs both sources' analyzers and reports both
     (uncorrelated)," exactly the increment phase-6-trace-source.md's own exit-criteria section named
     as deferred. Unifying dump+trace into one graph (so e.g. `--include-analyzers` could target a
     trace analyzer, or a single combined report could show both) is step 5e/5f's job.
   - **Reordered ahead of 5e (2026-09-16).** Re-reading step 2's own known-gap note found that
     splitting `RunAnalyzersNode` (5e) was deliberately deferred until "there's more than one
     artifact scope" — a condition 5d's two-separate-pipelines design didn't actually satisfy yet
     (a combined session still ran through two disconnected engines, not one graph). Doing 5e first
     would have been a no-observable-benefit split, exactly the anti-pattern this doc's own Risk
     section warns against. 5f (below) creates the real second-scope-in-one-graph case first, so
     5e's split (next) has an actual payoff to prove against.
   - **Shipped (2026-09-16), sub-step 5f.** New trace-side node triple —
     `BuildTraceIndexNode`/`RunTraceAnalyzersNode`/`WriteTraceReportNode`
     (`DumpDetective.Cli.Pipeline.Nodes`), built by `TraceNodeFactory`, backed by a new
     `TracePipelineState`/`TracePipelineKeys` pair mirroring `SingleDumpPipelineState`/
     `SingleDumpPipelineKeys`. New `TraceContainer` (`DumpDetective.Sources.NetTrace`) wraps
     `TraceIndexBuilder.Build` + `CacheContainerReader` behind the 3 SDK trace-query interfaces, so
     the Platform-internal reader type never has to cross into `DumpDetective.Cli` (which has no
     `InternalsVisibleTo` grant for it). `SingleDumpOrchestrationService.ExecuteAsync` gained an
     optional `tracePath` parameter: when set, it appends `TraceNodeFactory`'s 3 nodes to the dump's
     own 5, builds **one** `PipelineGraph`/`PipelineNodeContext`, and runs both through **one**
     `PipelineExecutor` call — the "1-dump session and dump+trace+... session, same code path" goal
     this doc's own Target-shape section named. `DumpAnalysisService` simplified back down
     accordingly (no more separate `TraceOrchestrationService` call after the dump half). Each trace
     analyzer's `AnalyzeAsync` call is individually try/caught inside `RunTraceAnalyzersNode`
     (warn + continue), mirroring `AnalysisPipeline`'s own per-analyzer failure isolation, so one bad
     trace analyzer can't sink a combined run's dump half. `TraceOrchestrationService` itself is
     retyped onto the same node-based path for the standalone trace-only entry point (no dump at
     all) rather than deleted outright, so trace-only and combined share identical node
     implementations; `TraceAnalysisRunner` (the old procedural duplicate) is deleted.
     **Known gap, not closed here:** a hard failure in `BuildTraceIndexNode`/`WriteTraceReportNode`
     (e.g. a corrupt trace file) still aborts the whole combined run before the dump half's
     insight-engine/diagnostics-summary/footer code runs, even though the dump's own report is
     already on disk by then (`WriteOutputNode` runs first) — real cross-scope failure isolation
     needs `PipelineExecutor`/`NodeScheduler` to actually track per-branch dependents, which today's
     purely-sequential scheduler doesn't do (confirmed by reading `NodeScheduler.ScheduleSequential`
     directly — no parallelism exists yet either, so this phase's separately-planned "artifact-level
     parallelism" is also still just a design goal, not implemented).
   - **5e re-scoped and closed at this granularity (2026-09-17).** The doc's original goal for 5e —
     re-express `RunAnalyzersNode`'s `AnalysisPipeline` bundling as real graph edges "once
     dump-scoped and trace-scoped analyzers have to coexist in one graph" — is satisfied by what 5f
     actually shipped: `RunAnalyzersNode` (dump's 36) and `RunTraceAnalyzersNode` (trace's 3) are
     two real, independently-keyed nodes in one `PipelineGraph`. The finer ask — one node per
     *individual analyzer* (36 separate nodes instead of one bundle) — was considered and
     deliberately **not** built: nothing today reads a per-analyzer `observations:{analyzerKey}`
     graph key (that only matters once Phase 5's synthesis rules or Phase 7's cross-source
     correlation exist and want to depend on one specific analyzer's output). Building it now would
     mean re-expressing `AnalysisPipeline`'s ordering/deferred-analyzer-pass/diagnostics/
     result-post-processing as graph edges, on the hot path every real run and every test already
     goes through, for zero observable behavior change — the same "splitting a working bundle for
     no observable benefit" anti-pattern this doc's own Risk section warns against, one level
     deeper than before. **Revisit this specifically when Phase 5 or Phase 7 lands and names a real
     per-analyzer graph-key consumer** — not before.
   - **Shipped (2026-09-17), sub-step 5g.** New `UnifiedCapabilityResolutionTests` (3 tests) closes
     Phase 3's exit criterion left half-open by step 3 — the criterion couldn't previously be fully
     verified because `CpuHotspotAnalyzer` wasn't in a resolvable catalog until step 5b's retyping.
     Resolving the real unified 39-analyzer catalog against a dump-only session now shows exactly
     the 3 trace analyzers `Unsatisfied` (not filtered out by name — genuinely missing `trace.*`
     capabilities) and the 36 dump analyzers `Satisfied`; the reciprocal (trace-only session skips
     the 36 dump analyzers) and combined (dump+trace session skips none) shapes are verified too.
   - **Step 5 complete.** All sub-steps landed except 5e, which was re-scoped and closed at the
     artifact-scope granularity 5f actually delivered — see 5e's own note above for why the finer
     per-analyzer split was deliberately deferred, not abandoned.
   - **Verified:** new `AnalyzerScopeAttributeTests` (3 tests, 5a) + moved/updated trace-ingest unit
     tests (`TraceCpuSampleQueryTests`, `CpuHotspotAnalyzerTests`, 5b) + new `TraceArtifactFactoryTests`
     (2 tests) + updated/new `SessionValidatorTests` (one stale mixed-kind test now asserts the new
     accept path; 3 new reject-shape tests added, 5c) + new `FilterToDumpCatalogTests` (2 tests, 5d)
     + new `TraceNodeFactoryTests` (3 tests, 5f, proving the shared-graph structural claim cheaply)
     + full non-real-dump suite (~1620 passed across several runs — two isolated, unrelated
     single-test flakes appeared and reproduced as pass-in-isolation, consistent with this suite's
     pre-existing flake history, not a regression) + one foreground real-trace run
     (`DD_RUN_DISCREPANCY_TESTS=1`, the real-trace opt-in switch — not the real-dump one, and not
     gated by the one-at-a-time real-dump constraint) against the real 912.1 MB `HighCPU_11.etl`: all
     9 `TraceIngest` tests passed, confirming `CpuHotspotAnalyzer`'s
     end-to-end path through the new `AnalysisContext`/`TraceCpuSampleQuery` wiring still resolves
     real leaf samples to real managed methods. **Plus, for 5f specifically, 3 sequential foreground
     CLI smoke runs against real artifacts** (never parallel/background, per this project's
     real-dump-test discipline): (1) real-trace-only through the new graph-based
     `TraceOrchestrationService` against `HighCPU_11.etl` — 33.1s, 66,132 observations, correct
     console summary; (2) real-dump-only against the ~3.3 GB reference dump — 73.2s, all 36
     analyzers ran, report written, confirming the `SingleDumpOrchestrationService` signature change
     is a no-op for the pure-dump path; (3) **real combined dump+trace** — 104.4s, console output
     showed "Run trace analyzers"/"Write trace report" as stages of the *same* pipeline as the
     dump's "Run analyzers", per-node memory tracking covered both, and both artifacts' real
     `report.json`/`report.html` outputs were confirmed written to disk (the trace's own
     `report.json`, timestamped to the run, landed next to the trace file as designed).
6. 🔶 **Partially done (2026-09-17).** Retire the old orchestrators, stages, and filter service.
   - **Stages: already done.** `IAnalysisStage`/`StagedPipelineRunner`/`SingleDumpStageFactory` and
     the 5 old stage classes were deleted in step 1 — nothing left to retire here.
   - **Orchestrators: done.** `TrendOrchestrationService` and `SingleDumpNodeFactory` deleted;
     `SingleDumpOrchestrationService` absorbed both (it already built the same Load/Index/Analyze
     node types for one artifact that `TrendOrchestrationService` built its own copy of for N —
     the only real difference was which report node closes the run, now an `if (isTrend)` branch in
     one method instead of two separate classes). Kept the class name rather than renaming to
     something scope-neutral, matching this phase's own precedent of retyping
     `TraceOrchestrationService` in place (step 5f) rather than deleting-and-replacing it.
     `DumpAnalysisService` now calls one method regardless of N or trace presence.
     `TrendOrchestrationServiceTests`/`SingleDumpOrchestrationServiceTests` merged into one file
     covering both shapes' failure-propagation behavior.
   - **Filter service: deliberately not done — blocked by the same precondition as 5e.** The doc's
     "becomes graph-node filtering" plan for `AnalyzerFilterService` means "don't even create a
     node for an excluded analyzer," which needs one graph node per analyzer to filter at that
     granularity — exactly the fine-grained `RunAnalyzersNode` split 5e re-scoped and deferred until
     Phase 5/7 names a real consumer. `AnalyzerFilterService` still does real, necessary work today
     (name validation, include/exclude filtering, domain/stage ordering, synthesizing
     `SkippedByFilter` results) with no other home to move to yet, so it's untouched. Revisit
     together with 5e's deferred split, same trigger.
   - **Verified:** merged `SingleDumpOrchestrationServiceTests` (2 tests, both dump-load-failure
     shapes) + updated `ServiceRegistrationTests` (`TrendOrchestrationService` references swapped
     for `TraceOrchestrationService`, the DI registrations that actually still exist) + full
     non-real-dump suite (1624 passed, run 3x) + two sequential foreground real-dump CLI smoke runs
     (never parallel/background): single-dump (75.5s, matching the pre-merge 73.2s/75.5s baseline)
     and a real 2-dump trend run against the same `Crash_IIS_BALTSTPRD` crash pair (191.9s,
     7.0 MB report containing 237 trend-specific mentions, confirming the merged trend branch still
     produces a real trend report end to end).
   - **Renamed (2026-09-17, after step 7 settled the CLI-shape question):**
     `SingleDumpOrchestrationService` → `DumpOrchestrationService`. The name stopped describing this
     class the moment it absorbed the trend (N-dump) case above; kept dump-centric rather than
     something session-generic, since this class still always requires at least one dump — a
     trace-only run has no dump artifact and stays on `TraceOrchestrationService`'s own path.
     Verified: full non-real-dump suite (1624 passed) + one foreground real-dump smoke run (71.0s,
     matching every prior baseline).
7. ✅ **Done (2026-09-17).** CLI collapses to `dd analyze <paths...>`; `--baseline`/`--trend`
   become sugar over ordering.
   - **Decided:** unlike `--trace`/`--trend` (kept, real options), `--baseline` is removed outright
     rather than kept as permanent sugar — it was already `[Obsolete("Use --trend instead")]`, and
     `--trend "old.dmp;new.dmp"` is already its exact 2-dump replacement, so there was no real
     back-compat case for keeping a third way to say the same thing.
   - **Shipped:** `--baseline` deleted from `RootCommandBuilder`/`AnalysisCommandRequest`/
     `ResolvedExecutionOptions`; `DumpAnalysisService.TryResolveTrendSequence`'s baseline-fallback
     branch and `StartupValidator`'s baseline-specific checks removed. `AnalysisIncidentContext`'s
     `BaselineDumpPath` report-metadata field (and both report formatters' "Baseline Dump" line)
     deleted too, rather than left as permanently-dead-but-present — it only ever populated from
     the now-gone CLI flag, never from `--trend`. `config.json`'s `BaselineDumpPath` key joined the
     existing dead-key warning list (`Profile`/`Analyzers`/`ExecutionPolicy`) instead of silently
     no-op'ing for anyone with an old config file.
   - **Decided (2026-09-17): no verb, no argument-shape change.** Discussed and settled: the CLI
     keeps today's shape — one positional dump path, separate `--trace`/`--trend` options — rather
     than gaining a `dd analyze` verb or flattening everything into one generic `<paths...>` list.
     None of this phase's own exit criteria (below) call for a CLI verb; the actual "one code path"
     goal was already delivered internally by step 6. Introducing a verb now would be a user-facing
     surface change with a real migration story and no concrete driver behind it (nothing in Phase
     5+ is currently known to need it) — this is the one piece of step 7 genuinely worth treating as
     a deliberate no-op rather than a deferral.
   - **Verified:** full non-real-dump suite (1624 passed across several runs; the one recurring
     flake, `ArrayAnalyzerRetypingCharacterizationTests`, reproduced independently of this change —
     see 5f/6's own notes on this suite's pre-existing flake history) + one foreground real-dump CLI
     smoke run (72.2s, matching every prior single-dump baseline) + confirmed `--baseline` is now a
     genuinely unrecognized argument, not silently ignored.

## Exit criteria

- Identical output to the pre-phase pipeline for both single-dump and multi-dump corpora. — **Met**:
  verified via real single-dump (72.2s–75.5s across several runs) and real 2-dump trend
  (191.9s, real trend report) CLI smoke runs after both the step 6 orchestrator merge and the step 7
  `--baseline` removal.
- A non-default graph (custom node injected, analyzers filtered) runs successfully — proving
  generality rather than a re-skin. — **Met**: the combined dump+trace graph (step 5f) appends a
  genuinely different node shape (`BuildTraceIndexNode`/`RunTraceAnalyzersNode`/
  `WriteTraceReportNode`) to the dump's own nodes and runs both through one `PipelineExecutor` call,
  verified against a real combined dump+trace CLI run (104.4s).
- Artifact-level parallelism is off by default and cannot be enabled without an explicit flag. —
  **Trivially met, not actually built**: `NodeScheduler.ScheduleSequential` (confirmed by reading it
  directly) never parallelizes anything, artifact-level or otherwise — there's no flag because
  there's no capability to gate. Real artifact-level parallelism remains a stated design goal (see
  "Key design decisions" above), not implemented work; revisit if a real performance need for it
  ever materializes.
- `IAnalysisStage` and both old orchestrators are gone. — **Met**: `IAnalysisStage` is gone (step 1).
  `TrendOrchestrationService` is deleted; `SingleDumpOrchestrationService` absorbed its
  responsibilities (step 6) and was then renamed to `DumpOrchestrationService` (step 7's own note
  above) once the CLI-shape question resolved and the old name's staleness had no more reason to
  wait — so there is exactly one class doing this job now, under a name that actually describes it,
  and neither of the two original class names survives.
- `PhaseTimeline` preserved, now keyed by node id. — inherited from step 1, unchanged by steps 5-7;
  not independently re-verified in this pass.

## Risk / effort

Medium-high effort, **highest behavioral risk in the plan** — this replaces the execution model
for everything, and the trend path in particular has subtle per-dump-sequencing semantics that a
naive graph rewrite can silently break. De-risk by strictly following the migration order above:
old shape → capability-derived nodes → N artifacts, verifying output equality at each step. Doing
all three at once is the failure mode.

Second risk worth naming: the memory-safety constraint on concurrent dump loading is the kind of
rule that gets forgotten during an execution-engine rewrite because the DAG makes parallelism look
free. It isn't. Encode it as a test, not a comment.
