# Phase 4 — Session Orchestration DAG

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 4**.
Depends on [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md).
(Supersedes `phase-4-orchestration-dag.md` from the dump-only draft.)

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

1. Scaffold the orchestration project; wrap today's five stages as five nodes with hand-declared
   edges. Prove `PipelineExecutor` reproduces current behavior before anything is dynamic.
2. Introduce `SessionBuilder` + `CapabilityResolver`; generate analyzer nodes from the Phase 3
   catalog. Verify byte-identical output vs. the old pipeline on the test-dump corpus.
3. Generalize to N artifacts: multi-dump sessions now flow through graph construction rather than
   `TrendOrchestrationService`. Verify trend output matches the old path.
4. Retire the old orchestrators, stages, and filter service.
5. CLI collapses to `dd analyze <paths...>`; `--baseline`/`--trend` become sugar over ordering.

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
