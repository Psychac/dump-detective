# Phase 0 — Deliverable 7: Dependency Graph Review

> Scope: **Deliverable 7 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Maps dependencies across the platform using the per-analyzer import data gathered in
> [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md), the hidden-coupling findings in
> [Deliverable 3](phase0-deliverable-3-responsibility-matrix.md), and the shared-infra design in
> [Deliverable 5](phase0-deliverable-5-shared-infrastructure.md). Static-analysis-level review of
> `using`/namespace dependencies, not a build-graph tool run — findings marked accordingly where
> they should be confirmed with an actual project-reference/namespace-dependency tool before
> acting on them.

## Observed Layering (as-built)

Based on namespaces referenced across the 36 analyzers and `DefaultAnalyzerFeatureModuleCatalog`:

```
DumpDetective.Core.Abstractions          — IAnalyzer, AnalysisContext, AnalyzerDomainResult
DumpDetective.Analysis.{Cache,Indexing,  — infra primitives: object index reader, type cache,
  Indexing.Container, Indexing.Satellite,   satellite indexes, BFS traversal, shared models
  Traversal, Models, Enums, Options}
DumpDetective.Analysis.Analyzers          — 36 IAnalyzer implementations
DumpDetective.Analysis.Trend.Comparers    — per-analyzer trend comparison
DumpDetective.Reporting.FindingGenerators — turns domain results into findings
DumpDetective.Reporting.SectionBuilders   — turns domain results into report sections
DumpDetective.Reporting.Capabilities      — composition root (DefaultAnalyzerFeatureModuleCatalog)
DumpDetective.Analysis.Pipeline (?)       — orchestration: executes the catalog in Order
```

`DefaultAnalyzerFeatureModuleCatalog` (in `Reporting.Capabilities`) references concrete analyzer
types (`typeof(MemoryAnalyzer)`, etc.) directly, plus their `FindingGenerator`, `TrendComparer`,
and `SectionBuilder` types — this is the intended composition root and is the **correct** place
for concrete-type knowledge to live. The violation below is a *different* analyzer reaching in the
opposite direction.

## Cycles

**One confirmed point-violation, not a namespace-wide cycle**: `HeapTopologyAnalyzer` (in the
Analyzers layer) imports a `Pipeline` namespace (Deliverable 1/3) — the layer that *executes*
analyzers. If `Pipeline` needs to reference `HeapTopologyAnalyzer` at all (directly, or
transitively through the catalog which lives above it), this is a real cycle at the
architectural level even if it doesn't trip a compiler circular-project-reference error (plausible
if both live in the same assembly). Concretely: `Pipeline` executes `HeapTopologyAnalyzer` →
`HeapTopologyAnalyzer` depends on `Pipeline`. This is the one item in this review that should be
verified directly against the source (`HeapTopologyAnalyzer.cs`'s actual `using` list and what
symbols it consumes from `Pipeline`) before scheduling a fix, since the cost of breaking a real
cycle is very different from the cost of a same-assembly namespace import.

No other cross-analyzer or cross-namespace cycles were identified from the available import data.

## Tight Coupling

- **`AsyncTaskAnalyzer` ↔ its own on-disk task-index format.** It depends on the shared
  `Indexing.Container` abstraction *and* appears to separately own private format constants
  (`TaskIndexMagic`/`TaskIndexVersion`) inside the analyzer itself (Deliverable 3/4). Storage
  format and analyzer logic should never be this inseparable — a format change requires touching
  the analyzer, and an analyzer change risks the format, when the two should be independently
  versionable behind the `Indexing` abstraction like every other container-index consumer
  (`ArrayAnalyzer`, `LohFragmentationAnalyzer`, `WeakReferenceAnalyzer`).
- **The "resource state sampler" quartet** (`DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
  `HttpObjectAnalyzer`, `TimerLeakAnalyzer`) has no shared reference between them at all — which
  is itself the coupling problem: they're coupled by convention (identical logic, copy-pasted)
  rather than by a shared contract. A bug fix to the sampling approach must be manually
  propagated to 4 places with no compiler assistance to catch a missed one. Each also has its own
  `Options` type carrying duplicate knobs (`MaxStateSamples`/`StateFieldNames`-shaped fields) — the
  configuration surface is duplicated exactly as many times as the logic is.
- **The thread-domain quartet** (`ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`,
  `LockGraphAnalyzer`) — same shape of problem: no shared dependency on a common thread-data
  provider, so each independently walks stacks and each is "coupled" to the others only in the
  sense that a change to wait-state classification in one silently doesn't apply to the rest.

## Infrastructure Leakage

- **`CollectionAnalyzer` → `Microsoft.Extensions.Logging`.** The only analyzer with a logging
  dependency (Deliverable 1/3). Either this is an accidental leftover from debugging that should
  be removed, or the platform has an undocumented, inconsistently-applied cross-cutting logging
  story — both are infrastructure-leakage smells and worth resolving explicitly rather than
  leaving as an outlier.
- **`AsyncTaskAnalyzer`'s bespoke index format** (see Tight Coupling above) is also, from a
  layering perspective, storage infrastructure leaking upward into analyzer logic — it should sit
  entirely behind `Indexing`, with the analyzer only ever seeing typed records.
- **`HeapTopologyAnalyzer` → `Pipeline`** (see Cycles above) is orchestration infrastructure
  leaking downward into an analyzer.

## Cross-Layer Violations

`HeapTopologyAnalyzer` → `Pipeline` is the one concrete cross-layer violation identified: an
analyzer (a leaf, execution-time participant) depending on the layer that orchestrates execution
(a root, composition-time participant) inverts the intended dependency direction described below.
No other analyzer in the catalog exhibits an equivalent upward dependency — every other analyzer's
imports stay within `Core.Abstractions` and the `Analysis.*` infra namespaces, which is the
correct direction.

## Feature Entanglement

- **4x registration fan-out per analyzer.** `DefaultAnalyzerFeatureModuleCatalog` wires exactly
  four types per module: `AnalyzerType`, `FindingGeneratorType`, `TrendComparerType`,
  `AnalyzerSectionBuilderType`. Every boundary decision from
  [Deliverable 6](phase0-deliverable-6-analyzer-boundary-review.md) (merge/split/replace) has a
  **4x blast radius** — merging `RetentionAnalyzer` into `DominatorAnalyzer`, for instance, means
  reconciling two `FindingGenerator`s, two `TrendComparer`s, and two `SectionBuilder`s, not just
  two analyzer classes. This isn't a defect by itself (it's a reasonable composition pattern), but
  it means the true cost of every Deliverable 6 recommendation is roughly 4x the analyzer-level
  description — worth pricing in explicitly for Deliverable 10's roadmap.
- **Leak/retention feature is entangled across 6 analyzer modules** (`RetentionAnalyzer`,
  `LeakCandidateAnalyzer`, `DominatorAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer`,
  `TimerLeakAnalyzer`), each with its own 4x fan-out — meaning "what counts as a leak" is
  currently expressed independently in up to ~24 places across the codebase (6 analyzers × 4
  registered types each) rather than one shared policy. This is the most severe entanglement found
  in the review and is precisely what Deliverable 5's evidence builder / ranking engine / inter-
  analyzer result bus are meant to collapse.
- **Thread-domain feature entangled across 4 modules** (`ThreadAnalyzer`, `HangAnalyzer`,
  `ThreadStackClusterAnalyzer`, `LockGraphAnalyzer`) with the same 4x multiplier — ~16 places
  expressing overlapping thread/wait-state logic.
- **Module/assembly feature entangled across 2 modules** (pre-merge) — smaller, but the same
  pattern, resolved by the Deliverable 6 merge.

## Ideal Dependency Direction

```
Core.Abstractions
   ↑
Analysis infra primitives (Cache, Indexing[.Container/.Satellite], Traversal, Models, Enums, Options)
   ↑
Analysis.Analyzers  (36 IAnalyzer implementations — depend ONLY on the two layers above + ClrMD;
                      never on Pipeline, Reporting, or cross-cutting infra like Logging)
   ↑
Analysis.Trend.Comparers
   ↑
Reporting.FindingGenerators / Reporting.SectionBuilders
   ↑
Reporting.Capabilities  (composition root — the only place allowed to reference concrete
                          analyzer/generator/comparer/section-builder types together)
   ↑
Pipeline / Orchestration  (executes the catalog in Order; owns the Deliverable-5 single-pass
                            dispatcher; depends on everything below, nothing below depends on it)
   ↑
CLI / Host / Report output
```

**Rule this enforces**: dependencies only point up the list. An analyzer may depend on Core and
Analysis infra; it may never depend on Reporting, Pipeline, or the composition root. Only the
composition root (`Reporting.Capabilities`) and `Pipeline` are allowed to know about concrete
analyzer types simultaneously with generator/comparer/section-builder types.

**Concrete actions this implies**:

1. Remove `HeapTopologyAnalyzer`'s dependency on `Pipeline` — whatever it currently gets from
   that namespace belongs in `Core.Abstractions` or `Analysis` infra instead, since analyzers must
   never depend on the orchestration layer that runs them (confirm the exact symbol first, per the
   caveat in Cycles).
2. Move `AsyncTaskAnalyzer`'s private task-index format fully behind `Indexing.Container`, so the
   analyzer depends only on the abstraction, not on the format's constants.
3. Remove or formally justify `CollectionAnalyzer`'s `Microsoft.Extensions.Logging` dependency —
   either it's noise to delete, or the platform needs a deliberate, consistently-applied logging
   layer that every analyzer can depend on the same way.
4. Introduce shared contracts (interfaces, not just conventions) for the resource-sampler quartet
   and the thread-domain quartet, so their current "coupled by copy-paste" relationship becomes an
   enforced, compiler-checked one — this is the dependency-graph framing of Deliverable 5 items 3
   and 7.
