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
closes it. Also reassigned here: `AnalyzerScope { PerArtifact | PerSession }` (see "Key design
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

3. ⬜ **Pending.** Close Phase 3's carried-over gap: declare real `[RequiresCapability]`/
   `[OptionalCapability]` on the 36 in-repo analyzers (Phase 3 shipped the attributes and the
   8 domain packages but deliberately left every analyzer's declarations unwritten, waiting for
   this phase's orchestrator to exist), and make `CapabilityResolver`'s runnable/degraded/skipped
   buckets real output, not just an internal computation — a dump-only session must visibly list
   `Plugins.Cpu`'s analyzers as skipped-for-missing-`trace.cpu-samples`, closing Phase 3's own named
   exit criterion. This step deliberately runs before N-artifact generalization: it exercises
   `CapabilityResolver` against real, differentiated declarations for the first time (today it's a
   provable no-op), and step 5's `AnalyzerScope`/combined-session work below is only meaningful once
   analyzers actually declare what they need.
4. ⬜ **Pending.** Generalize to N artifacts: multi-dump sessions now flow through graph
   construction rather than `TrendOrchestrationService`. Verify trend output matches the old path.
   Also builds `SessionValidator`, carried over unbuilt from step 2 — nothing to validate with one
   artifact, but real once N>1 dumps must be checked for process-identity compatibility. Scoped to
   N *homogeneous* dump artifacts (trend) — step 5 below is where mixed dump+trace sessions and
   `AnalyzerScope` land, sequenced after this step verifies the N-artifact graph-construction
   mechanics on the simpler, single-source case first.
5. ⬜ **Pending.** Wire combined dump+trace sessions: build `AnalyzerScope { PerArtifact | PerSession }`
   (reassigned to this phase above — nobody upstream built it) and generate the real target shape
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
6. ⬜ **Pending.** Retire the old orchestrators, stages, and filter service.
7. ⬜ **Pending.** CLI collapses to `dd analyze <paths...>`; `--baseline`/`--trend` become sugar
   over ordering.

## Exit criteria

- Identical output to the pre-phase pipeline for both single-dump and multi-dump corpora.
- A non-default graph (custom node injected, analyzers filtered) runs successfully — proving
  generality rather than a re-skin.
- Artifact-level parallelism is off by default and cannot be enabled without an explicit flag.
- `IAnalysisStage` and both old orchestrators are gone.
- `PhaseTimeline` preserved, now keyed by node id.

## Risk / effort

Medium-high effort, **highest behavioral risk in the plan** — this replaces the execution model
for everything, and the trend path in particular has subtle per-dump-sequencing semantics that a
naive graph rewrite can silently break. De-risk by strictly following the migration order above:
old shape → capability-derived nodes → N artifacts, verifying output equality at each step. Doing
all three at once is the failure mode.

Second risk worth naming: the memory-safety constraint on concurrent dump loading is the kind of
rule that gets forgotten during an execution-engine rewrite because the DAG makes parallelism look
free. It isn't. Encode it as a test, not a comment.
