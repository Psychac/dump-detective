# Phase 4 — Session Orchestration DAG

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 4**.
Depends on [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md).
(Supersedes `phase-4-orchestration-dag.md` from the dump-only draft.)

**Also owns, carried over from Phase 2's deferred scope**: `Session/*`
(see [phase-2-artifact-platform.md](phase-2-artifact-platform.md#migration-steps)'s deferred-items
list) — `SessionBuilder`/`SessionValidator` below are that item's real home. `TimelineAligner` is
*not* claimed here or in Phase 5 yet; whichever phase's work item ends up needing multi-artifact
alignment first should pick it up explicitly rather than assume the other phase has it.

## Goal

Replace the fixed five-stage pipeline **and** the proposed mode enum with one capability-driven
session orchestrator. This is the phase where "no modes" stops being a design claim and becomes
running code.

## What this deletes

- `SingleDumpOrchestrationService`, `TrendOrchestrationService`
- The unified doc's proposed `TraceOrchestrationService`, `CombinedOrchestrationService`, and the
  `SingleDump | MultiDump | TraceOnly | Combined` enum — never built
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
  builder needs an explicit `AnalyzerScope { PerArtifact | PerSession }` declaration — this is a
  detail that's easy to miss up front and awkward to retrofit, so it lands in the SDK attribute
  set in Phase 1/3.

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
     Deferred to whichever later step forces it (likely step 3).
   - **Known gaps, carried into step 3:** `SingleDumpArtifactFactory`'s capability set is a static
     "every dump capability" list (provably a no-op filter today, since every catalog analyzer is
     dump-capable) rather than a real per-dump probe — analyzer selection happens before
     `LoadDumpNode` loads the dump, so no real ClrMD data exists yet at that point. `SessionValidator`
     (process-identity compatibility, alignment feasibility) is still unbuilt — nothing to
     validate yet with one artifact.
   - **Verified:** new `CapabilityResolverTests`/`SessionBuilderTests` + full non-real-dump suite
     (1593 passed) unchanged, proving the capability-derived filter is a no-op against today's
     single-dump analyzer catalog.

3. ⬜ **Pending.** Generalize to N artifacts: multi-dump sessions now flow through graph
   construction rather than `TrendOrchestrationService`. Verify trend output matches the old path.
   Also builds `SessionValidator`, carried over unbuilt from step 2 — nothing to validate with one
   artifact, but real once N>1 dumps must be checked for process-identity compatibility.
4. ⬜ **Pending.** Retire the old orchestrators, stages, and filter service.
5. ⬜ **Pending.** CLI collapses to `dd analyze <paths...>`; `--baseline`/`--trend` become sugar
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
