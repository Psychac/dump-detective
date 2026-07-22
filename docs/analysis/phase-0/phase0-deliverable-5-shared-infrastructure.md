# Phase 0 — Deliverable 5: Shared Infrastructure Opportunities

> Scope: **Deliverable 5 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Reviewed as a Principal .NET Runtime Engineer / CLR-GC Expert / Production Memory Diagnostics
> Architect — challenging whether the current per-analyzer independence is actually buying
> anything, given that DumpDetective's own definition of done is "works on 10GB+ dumps, bounded
> memory, reasonable runtime." Builds directly on the duplication findings in
> [Deliverable 4](phase0-deliverable-4-duplicate-work-analysis.md).

For each candidate service: current duplication, estimated impact, implementation difficulty,
and priority (P0 = foundational/highest value, P2 = polish).

## 1. Heap Index Single-Pass Dispatcher

> **Correction (2026-07-21)**: verified against actual `IAnalyzer` implementations — see the
> [Deliverable 10 correction note](phase0-deliverable-10-platform-roadmap.md#correction--2026-07-21-verified-heap-scan-analyzer-count).
> The real count is **9 of 35** analyzers streaming the on-disk index, not 26 of 36. This item
> still stands on its own merits (9 sequential full-index reads is still real waste on a 10GB+
> dump) but should be re-weighed against the correctness-track items below rather than assumed
> automatically dominant now that its blast radius is ~3x smaller than originally stated.

**Current duplication**: 9 of 35 analyzers independently open and fully stream the on-disk
object index (Deliverable 4 §1, corrected) — still the single largest *coordinated* cost in the
platform among analyzers that share this access pattern, though a further 5 analyzers do a full
live `ClrHeap.EnumerateObjects()` sweep that this dispatcher shape cannot address.

**Estimated impact**: High (revised down from "Very High"). Collapsing ~9 redundant sequential
reads into 1 is still a high-leverage change and directly serves the project's stated 10GB+ dump
performance goal, but it no longer dwarfs everything else on this list at scale — the
correctness-track items (evidence bus, confidence scoring) are worth prioritizing on their own
merits now rather than by default deferring to this item.

**Difficulty**: High. This isn't a helper-extraction — it requires rethinking the
`IAnalyzer.AnalyzeAsync(AnalysisContext, CancellationToken)` execution model. Two shapes are
plausible: (a) add a per-object visitor callback to `IAnalyzer` that a shared dispatcher invokes
once per index record, fanning out to every registered analyzer that opts in; or (b) keep
`AnalyzeAsync` as-is but have `AnalysisContext` hand out a single already-open, position-tracked
reader that analyzers cooperatively share — riskier, since it couples analyzer execution order to
stream position. (a) is the safer design and should be the default recommendation.

**Priority**: **P0**.

## 2. Statistics Engine (per-type count/bytes)

**Current duplication**: `MemoryAnalyzer`, `ModuleAnalyzer`, `AppDomainAnalyzer`,
`ObjectShapeAnalyzer` each independently reduce the index into `TypeId → (count, bytes)`
(Deliverable 4 §5).

**Estimated impact**: High. Removes a second-order cost layered on top of item 1, and — more
importantly — removes a correctness risk: four independently-computed "total bytes by type"
numbers can silently disagree across report sections, which is worse for user trust than being
slow.

**Difficulty**: Low–Medium. `TypeIndexBuilder` already exists as part of the Phase 1 index build
per [architecture.md](../architecture.md) — this is promoting a near-artifact that likely already
exists in some form into a persisted, queryable one, not inventing new machinery. Should be
designed together with item 1's dispatcher (the per-type reduction is a natural accumulator to run
inside the same single pass).

**Priority**: **P0** — pairs with item 1, low incremental cost once item 1 exists.

## 3. Root / Retention Graph Service

**Current duplication**: `GCRootAnalyzer`, `AsyncTaskAnalyzer`, `ReferenceChainAnalyzer` already
share the `Traversal` BFS primitive. `RetentionAnalyzer`, `DominatorAnalyzer`,
`StaticRootLeakDetector`, `EventLeakAnalyzer` do not, and each has grown its own ad hoc graph-walk
instead (Deliverable 3 hidden coupling, Deliverable 4 §2).

**Estimated impact**: Medium–High. Beyond removing the ~4x redundant walk, this is a correctness
issue: CLAUDE.md mandates a specific traversal discipline (BFS, depth limit 20, `HashSet<ulong>`
visited set, early stop). Four independent implementations of that discipline is four chances to
get the depth limit or cycle guard subtly wrong on a production-scale graph.

**Difficulty**: Medium. The primitive already exists; the work is refactoring four call sites to
depend on it and, more usefully, building one shared "retained subgraph" API layered on top of
`Traversal` (walk-and-summarize: size, count, sample paths) that all four can call instead of each
owning their own summarization logic too.

**Priority**: **P1**.

## 4. Type Metadata Classification Layer

**Current duplication**: Raw `MethodTable → ClrType` resolution is already correctly shared via
`HeapAnalysisCache`. What's duplicated is the *classification* layer on top — 8 analyzers
(`CollectionAnalyzer`, `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer`, `WeakReferenceAnalyzer`,
`DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`, `TimerLeakAnalyzer`) each
independently pattern-match type names (Deliverable 4 §3).

**Estimated impact**: Low runtime impact (operates on cached metadata, not the disk index), real
maintenance impact — closing Deliverable 2's framework-specific gaps (EF Core, DI, Channels) will
each need this same kind of classification, and today that means writing it from scratch each
time rather than registering a pattern once.

**Difficulty**: Low. Pure logic extraction; no data-flow or execution-model change required.

**Priority**: **P1** — cheap, and directly reduces the cost of closing capability gaps from
Deliverable 2.

## 5. Object Metadata Classification (generation/segment bucket)

**Current duplication**: Any analyzer that needs "which generation / LOH / SOH / POH bucket is
this object in" (`GCGenerationAnalyzer`, `AllocationPatternAnalyzer`, `FinalizableObjectAnalyzer`,
`LohFragmentationAnalyzer`, `WeakReferenceAnalyzer`, others) re-derives it from address and
segment bounds independently rather than consuming a shared per-object classification.

**Estimated impact**: Medium. Real correctness risk — segment-boundary/threshold logic duplicated
across analyzers can drift (e.g., an off-by-one at the LOH threshold behaving differently in two
places). Naturally complements item 1: if this classification runs once per object inside the
shared single pass and is handed to every visitor, it's nearly free.

**Difficulty**: Medium, and sequencing-dependent — most of the value is only realized once item 1
exists; before that, extracting the classifier alone only fixes the maintenance risk, not the cost.

**Priority**: **P1**, sequenced after item 1.

## 6. Evidence Builder

**Current duplication**: `RetentionAnalyzer`, `DominatorAnalyzer`, `LeakCandidateAnalyzer`,
`StaticRootLeakDetector`, `EventLeakAnalyzer` each produce their own flavor of "why is this
object/type suspicious" evidence, with no shared evidence model (Deliverable 2: "retention graphs"
flagged as fragmented; Deliverable 3: "no unified indicator").

**Estimated impact**: High for report quality and product credibility — this is close to the core
value proposition of the tool (comparable to how dotMemory/WinDbg present consistent retention
evidence). Inconsistent evidence shapes across 5 analyzers directly undermines trust in findings.

**Difficulty**: Medium–High. Requires designing a shared `Evidence`/proof model (retained size,
sample root paths, contributing signals) and rewiring five analyzers to emit it instead of their
own ad hoc DTOs. Depends on item 3 (root graph service) to be well-founded.

**Priority**: **P1**.

## 7. Sampling Framework (typed resource sampler)

**Current duplication**: `DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`,
`TimerLeakAnalyzer` each independently implement classify-by-type-name → sample-state-field →
bucket (Deliverable 4 §7).

**Estimated impact**: Low runtime cost, meaningful maintenance value — and directly reduces the
cost of closing Deliverable 2's EF Core / DI / ASP.NET gaps, which are naturally the same "typed
resource, sample a field, bucket by state" shape.

**Difficulty**: Low. Self-contained extraction, four existing call sites to migrate.

**Priority**: **P1** — cheap win, high leverage for future capability additions.

## 8. Ranking / Leak-Scoring Engine

**Current duplication**: `LeakCandidateAnalyzer` computes its own leak score while *also*
apparently independently re-scanning for signals rather than consuming the other leak-adjacent
analyzers' output (Deliverable 3's major finding). `TimerLeakAnalyzer`, `EventLeakAnalyzer`,
`StaticRootLeakDetector`, `RetentionAnalyzer` each compute their own severity too.

**Estimated impact**: Very High — "is this actually a leak, and how confident are we" is
arguably the platform's core deliverable to a user. Five independently-computed, inconsistent
severities directly undermines the credibility of the "leak candidates" report.

**Difficulty**: High. This requires more than extraction — it requires `LeakCandidateAnalyzer` to
change from an independent scanner into an aggregator over other analyzers' `AnalyzerDomainResult`
outputs, which in turn requires the platform to support **inter-analyzer result consumption within
a single run**. See item 11 below — this is a prerequisite, not a detail.

**Priority**: **P0** on value, but **blocked on item 11** — sequence accordingly.

## 9. Confidence Scoring

**Current duplication**: A global `ConfidenceSectionBuilder` already exists in
`DefaultAnalyzerFeatureModuleCatalog.GlobalReportSectionBuilderTypes`, suggesting a shared
confidence *presentation* already exists — but the analyzers feeding it (`TimerLeakAnalyzer`,
`EventLeakAnalyzer`, `StaticRootLeakDetector`, `LeakCandidateAnalyzer`, `RetentionAnalyzer`) each
compute their own severity/confidence independently rather than through one formula.

**Estimated impact**: High, same reasoning as item 8 — a single, explainable confidence formula
that every finding can point to is a materially better user experience than five silently
different heuristics.

**Difficulty**: Medium. Whether `ConfidenceSectionBuilder` already consumes a structured,
per-finding confidence value or re-derives it per section needs to be confirmed directly against
its implementation in Deliverable 7's dependency graph review — flagged here as an open question
rather than assumed.

**Priority**: **P0/P1**, tied to item 8 — design them together, since a ranking engine without a
shared confidence formula just moves the inconsistency rather than removing it.

## 10. Reporting Helpers

**Current duplication**: Four near-identical `SectionBuilder`s for the resource-sampler quartet
(item 7); possible overlap between per-analyzer "top types by size" rendering
(`MemoryAnalysisSectionBuilder`, `ModuleSectionBuilder`, `AppDomainSectionBuilder`) and the global
`TypeSystemSectionBuilder` (Deliverable 4 §6).

**Estimated impact**: Medium — presentation-layer only, no correctness risk, but real
inconsistency in report UX and real duplicated maintenance effort.

**Difficulty**: Low, and depends on item 7 landing first for the sampler quartet's sections.

**Priority**: **P2**.

## 11. Inter-Analyzer Result Bus *(added — not in the doc's suggested list, but a prerequisite for items 8–9)*

**Why this belongs here**: Items 6, 8, and 9 all assume analyzers can consume *other analyzers'*
results within the same run rather than each re-deriving signals independently.
`LeakCandidateAnalyzer`'s independent re-scanning isn't a mistake by that analyzer — it's the only
option the current architecture gives it.

**Confirmed — 2026-07-21**: `Order` does **not** provide this. It is consumed in exactly one place,
`AnalyzerFilterService.Order()` (`src/DumpDetective.Cli/Execution/AnalyzerFilterService.cs:53`),
purely to sort execution/report sequence. `AnalysisContext` carries no field holding prior
analyzers' results, and `AnalysisPipeline.ExecuteAsync`
(`src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs:24`) never threads a running
`AnalyzerRunResult` collection back into the context for later analyzers to read. This is confirmed
new work, not a repurposing of an existing field.

A **post-hoc** bus (not a live/mid-run one) is the right shape, and there's already a working
precedent for it: `InsightEngine.FindResult<T>(IReadOnlyList<AnalyzerRunResult> runs)`
(`src/DumpDetective.Analysis/Insight/InsightEngine.cs:1574`) already did the exact typed lookup —
scanning the completed run list and type-matching on `AnalyzerDomainResult` — that the bus needs.
It's invoked from `InsightEngine.Analyze`, which the orchestrator calls only after the full
pipeline finishes (`SingleDumpOrchestrationService.ExecuteAsync`,
`src/DumpDetective.Cli/Execution/SingleDumpOrchestrationService.cs:52`, after
`StagedPipelineRunner.RunAsync` returns — not via `AnalyzerResultPostProcessor.Enrich`, which only
runs `FindingGenerationPipeline.Generate`). A post-hoc bus gives every analyzer/evidence-builder
symmetric access to all other analyzers' results regardless of run order, and keeps analyzers
independent and safely parallelizable during their own `AnalyzeAsync` — a live, mid-run bus keyed
off `Order` would instead make correctness depend on execution order staying stable, which is the
same "precedent" risk flagged for `HeapTopologyAnalyzer → Pipeline` (Biggest Risk #3).

**Current duplication**: N/A — this is a missing capability, not a duplicated one, but it's the
root cause enabling items 6/8/9's duplication to exist in the first place.

**Estimated impact**: Very High — unlocks the entire "evidence builder / ranking engine /
confidence scoring" cluster, which is otherwise architecturally impossible to fix cleanly.

**Difficulty**: Medium — smallest-diff path is generalizing/promoting `FindResult<T>` (or
extracting an equivalent typed-lookup helper onto a shared `AnalyzerRunResults` query object) into
a public post-run query surface analyzers and the Evidence builder can call, rather than adding a
`PriorResults` field to `AnalysisContext`.

**Priority**: **Done — 2026-07-21.** Implemented as `AnalyzerRunResultsExtensions.GetResult<T>(this
IReadOnlyList<AnalyzerRunResult> runs)` in `src/DumpDetective.Core/Models/AnalyzerRunResult.cs`, an
`internal` extension visible to `DumpDetective.Analysis`, `.Reporting`, `.Cli`, `.Tests`, and
`BenchmarkSuite1` (all already have `InternalsVisibleTo` on `DumpDetective.Core`).
`InsightEngine.FindResult<T>` now delegates to it (`InsightEngine.cs:1574`). `AnalysisContext` was
left unchanged — no live/mid-run channel was added. Items 6, 8, 9 can now consume
`AnalyzerRunResultsExtensions.GetResult<T>` directly.

---

## Sequencing (feeds Deliverable 10)

```
P0 foundation:  [1] Index dispatcher ──┬─→ [2] Statistics engine
                                        └─→ [5] Object metadata classification

P0 foundation:  [11] Inter-analyzer result bus ─→ [6] Evidence builder ─→ [8] Ranking engine
                                                                        └─→ [9] Confidence scoring

P1 independent: [3] Root graph service, [4] Type classification, [7] Sampling framework
                (no blocking dependencies — can start any time, feed item 6 when ready)

P2 last:        [10] Reporting helpers (depends on 7, benefits from 2)
```

Two independent P0 tracks exist — the **performance track** (items 1/2/5, unblocks 10GB+ dump
viability) and the **product-correctness track** (item 11 → 6/8/9, unblocks trustworthy leak
findings). Neither blocks the other; both should be prioritized over every P1/P2 item.
