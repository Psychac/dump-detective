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

**Resolved.** `HeapTopologyAnalyzer` imported a `Pipeline` namespace (Deliverable 1/3) — the layer
that *executes* analyzers. Verified directly against source (`HeapTopologyAnalyzer.cs`'s `using`
list and the symbols it actually consumed): the import was dead — no symbol from `Pipeline` was
referenced anywhere in the file. It has been removed
([P0 item 1](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0-—-correctness-track)),
so there was no real circular-reference risk to unwind, just a stale `using`.

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

- ~~**`CollectionAnalyzer` → `Microsoft.Extensions.Logging`.** The only analyzer with a logging
  dependency (Deliverable 1/3). Either this is an accidental leftover from debugging that should
  be removed, or the platform has an undocumented, inconsistently-applied cross-cutting logging
  story — both are infrastructure-leakage smells and worth resolving explicitly rather than
  leaving as an outlier.~~ **Resolved (P1 item 10).** Investigated and found the dependency
  legitimate, not accidental: `CollectionAnalyzer` has ~29 real logging call sites for per-object
  scan failures and expected issues (missing optional fields, generation-lookup fallbacks). The
  mechanism is platform-wide by design — any analyzer can take `ILogger<T>? logger = null` parameter,
  resolved automatically via `ActivatorUtilities` in `DefaultAnalyzerFactory`. Formalized as a
  sanctioned pattern in [docs/architecture.md § 14 Observability](#14--observability) for analyzers
  scanning large populations that expect malformed data (not routine control flow).
- **`AsyncTaskAnalyzer`'s bespoke index format** (see Tight Coupling above) is also, from a
  layering perspective, storage infrastructure leaking upward into analyzer logic — it should sit
  entirely behind `Indexing`, with the analyzer only ever seeing typed records.
- ~~`HeapTopologyAnalyzer` → `Pipeline`~~ — resolved (see Cycles above).

## Cross-Layer Violations

**Resolved.** `HeapTopologyAnalyzer` → `Pipeline` was the one concrete cross-layer violation
identified: an analyzer (a leaf, execution-time participant) depending on the layer that
orchestrates execution (a root, composition-time participant) inverted the intended dependency
direction described below. The import was confirmed dead and removed (see Cycles above). No other
analyzer in the catalog exhibits an equivalent upward dependency — every other analyzer's imports
stay within `Core.Abstractions` and the `Analysis.*` infra namespaces, which is the correct
direction.

## Feature Entanglement

- **4x registration fan-out per analyzer.** `DefaultAnalyzerFeatureModuleCatalog` wires exactly
  four types per module: `AnalyzerType`, `FindingGeneratorType`, `TrendComparerType`,
  `AnalyzerSectionBuilderType`. Every boundary decision from
  [Deliverable 6](phase0-deliverable-6-analyzer-boundary-review.md) (merge/split/replace) has a
  **4x blast radius** — confirmed by actually merging `RetentionAnalyzer` into `DominatorAnalyzer`
  (and `DependentHandleAnalyzer` into `GCHandleAnalyzer`), which required reconciling each pair's
  `FindingGenerator`s, `TrendComparer`s, and `SectionBuilder`s, not just the two analyzer classes
  (see [Deliverable 10 P0 item 3](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0)).
  This isn't a defect by itself (it's a reasonable composition pattern), but it means the true cost
  of every Deliverable 6 recommendation is roughly 4x the analyzer-level description — worth
  pricing in explicitly for Deliverable 10's roadmap.
- **Leak/retention feature is entangled across 5 analyzer modules** (`DominatorAnalyzer` — now
  also owning the merged `RetentionAnalyzer`'s signal — `LeakCandidateAnalyzer`,
  `StaticRootLeakDetector`, `EventLeakAnalyzer`, `TimerLeakAnalyzer`), each with its own 4x
  fan-out — meaning "what counts as a leak" is currently expressed independently in up to ~20
  places across the codebase (5 analyzers × 4 registered types each) rather than one shared
  policy. This is the most severe entanglement found in the review and is precisely what
  Deliverable 5's evidence builder / ranking engine / inter-analyzer result bus are meant to
  collapse.
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

1. ~~Remove `HeapTopologyAnalyzer`'s dependency on `Pipeline`~~ — **done.** Confirmed the import
   was unused (no symbol from `Pipeline` was consumed) and deleted it.
2. Move `AsyncTaskAnalyzer`'s private task-index format fully behind `Indexing.Container`, so the
   analyzer depends only on the abstraction, not on the format's constants.
3. ~~Remove or formally justify `CollectionAnalyzer`'s `Microsoft.Extensions.Logging` dependency~~ —
   **done (P1 item 10).** Formally justified as option (b): the platform has a deliberate,
   consistently-applied logging layer that every analyzer can depend on the same way. Analyzed and
   validated the dependency is not noise — it's ~29 real call sites for per-object error/debug
   diagnostics in the analyzer scanning the largest object population. Documented in
   [docs/architecture.md § 14 Observability](#14--observability).
4. Introduce shared contracts (interfaces, not just conventions) for the resource-sampler quartet
   and the thread-domain quartet, so their current "coupled by copy-paste" relationship becomes an
   enforced, compiler-checked one — this is the dependency-graph framing of Deliverable 5 items 3
   and 7.
