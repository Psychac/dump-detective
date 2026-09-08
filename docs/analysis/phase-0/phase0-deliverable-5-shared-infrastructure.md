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

## 1. Heap Index Single-Pass Dispatcher — **done**

**Re-verified 2026 (this pass)**: implemented, not just designed. `IHeapIndexScanParticipant`
(`src/DumpDetective.Analysis/Pipeline/IHeapIndexScanParticipant.cs`) is the opt-in per-object
visitor interface (`BeforeHeapIndexScan(AnalysisContext)` / `OnHeapEntry(in HeapEntry)`) that
option (a) below recommended, and `HeapIndexScanDispatcher.Run(HeapAnalysisCache, AnalysisContext,
IReadOnlyList<IHeapIndexScanParticipant>, CancellationToken)`
(`src/DumpDetective.Analysis/Pipeline/HeapIndexScanDispatcher.cs`) is wired directly into
`AnalysisPipeline.ExecuteAsync` (`src/DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs`) ahead
of the per-analyzer loop. 9 analyzers now implement the interface — `DbConnectionAnalyzer`,
`WcfChannelAnalyzer`, `CrashAnalyzer`, `CollectionAnalyzer`, `AsyncTaskAnalyzer`, `HangAnalyzer`,
`EventLeakAnalyzer`, `StringAnalyzer`, and `DominatorAnalyzer` (the last wasn't on the original
9-analyzer index-streaming list, so this is one analyzer beyond the originally scoped set).
Covered by `HeapIndexScanDispatcherTests.cs` and
`AnalysisPipelineTests.ExecuteAsync_ScansHeapIndexExactlyOnce_WhenMultipleParticipantsRegistered`.

**Residual gap — worth a follow-up, not a re-open**: the "standalone `AnalyzeAsync` bypass"
problem flagged as *blocking* in
[phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md) (tests or
other call sites invoking `analyzer.AnalyzeAsync(context, ...)` directly, without the pipeline
having called `BeforeHeapIndexScan`/`OnHeapEntry` first) does not appear to have been resolved with
a dual-mode fallback — e.g. `DbConnectionAnalyzer.AnalyzeAsync`
(`src/DumpDetective.Analysis/Analyzers/DbConnectionAnalyzer.cs:138`) just assumes the pipeline has
already populated its instance fields, with a code comment to that effect rather than a guard.
That plan doc itself is now stale (still headed "Status: Not started") and should be updated or
retired in the same pass that closes this item out — see
[docs/analysis/phase-0/phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md).

<details>
<summary>Original analysis (superseded)</summary>

Verified against actual `IAnalyzer` implementations (see
[Deliverable 10, Current State](phase0-deliverable-10-platform-roadmap.md#current-state)): the
real count is **9 of 35** analyzers streaming the on-disk index. This item still stands on its own
merits (9 sequential full-index reads is still real waste on a 10GB+ dump) but is weighed against
the correctness-track items below rather than assumed automatically dominant, since its blast
radius is ~3x smaller than the catalog's original Index/Index+Container labels implied.

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

**Priority**: was **P0**.

</details>

## 2. Statistics Engine (per-type count/bytes) — **partially done**

**Re-verified 2026 (this pass)**: a shared engine exists and is genuinely shared by most, but not
all, of the originally-named consumers — this is progress, not a full close.
`StatisticsCache.GetOrBuildTypeStatistics(ClrHeap)`
(`src/DumpDetective.Analysis/Cache/StatisticsCache.cs:24`) hydrates `TypeId → (count, bytes)` from
`HeapIndexBuildResult.TypeAggregates` via `TryHydrateTypeStatisticsFromIndex`, falling back to a
parallel full heap walk only when no index is present. Confirmed callers: `MemoryAnalyzer`,
`GCGenerationAnalyzer`, `LeakCandidateAnalyzer`, `DominatorAnalyzer`, `ReferenceChainAnalyzer`,
`EventLeakAnalyzer`, and `QueryEngine` — a materially larger consumer set than the four originally
named.

**Gaps found**: `AppDomainAnalyzer` no longer exists as a standalone analyzer — it was merged into
`ModuleAnalyzer` (`AnalyzeAppDomains`, `src/DumpDetective.Analysis/Analyzers/ModuleAnalyzer.cs:103`),
so the original catalog entry for it is stale. But `ModuleAnalyzer` itself does **not** consume
`StatisticsCache` — it has its own separate index-reduction path, `ModuleAggregator.Aggregate`
(`src/DumpDetective.Analysis/Indexing/ModuleAggregator.cs:10`), which reduces the same
`TypeAggregateIndexEntry` data independently. That's a second, still-duplicated implementation of
"reduce index into type→(count,bytes)", just no longer duplicated *four* ways. `ObjectShapeAnalyzer`
also does not consume `StatisticsCache` — it needs per-field shape data (reference/value field
counts, base-type depth) that the shared cache doesn't carry, so its independence looks
legitimate rather than unaddressed duplication.

**Remaining work**: fold `ModuleAggregator` into `StatisticsCache` (or have it consume
`StatisticsCache`'s hydrated dictionary rather than re-reducing `TypeAggregates` itself) to close
the one confirmed remaining duplication.

<details>
<summary>Original analysis (superseded)</summary>

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

**Priority**: was **P0** — pairs with item 1, low incremental cost once item 1 exists.

</details>

## 3. Root / Retention Graph Service — **done**

`RootSetCache` (`src/DumpDetective.Analysis/Cache/RootSetCache.cs`) replaces `RootCache` as the
single canonical root-set service, and `BoundedGraphWalk`
(`src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs`) replaces `HeapTypePathTraversal`,
`BoundedRetainedSizeBfs`, and `HeapAnalysisCache.GetRetainedObjects` as the single canonical
forward-BFS primitive, enforcing the 20-depth cap internally regardless of caller-requested depth.
`GCRootAnalyzer`, `RetentionAnalyzer`, `DominatorAnalyzer`, `StaticRootLeakDetector`, and
`EventLeakAnalyzer` all consume these shared services instead of independently re-enumerating
roots or re-implementing BFS. `RootPathFinder`/`ReferenceChainAnalyzer`'s bidirectional
shortest-root-path search was intentionally left untouched — a different problem shape, out of
scope for this item. See
[docs/architecture.md § Graph and traversal](../../architecture.md#graph-and-traversal) and
[phase0-deliverable-10-platform-roadmap.md P0 item 2](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0)
for the full design and status.

<details>
<summary>Original analysis (superseded)</summary>

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

**Priority**: was **P1**.

</details>

## 4. Type Metadata Classification Layer

**Status**: Done — see [P1 item 6](phase0-deliverable-10-platform-roadmap.md#near-term-p1) in the
platform roadmap for the shipped `TypeNamePatternMatcher` and migration details.

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

## 5. Object Metadata Classification (generation/segment bucket) — **partially done**

**Re-verified 2026 (this pass)**: a shared classifier exists —
`SegmentKindMapper` (`src/DumpDetective.Analysis/Analyzers/SegmentKindMapper.cs`), exposing
`IsEphemeral(ClrSegment)` and `ResolveGeneration(ClrHeap, ulong address)` — but adoption is
narrower than the item's own framing implies, and doesn't overlap much with the analyzers
originally named as duplicating this logic. Confirmed callers:
- `IsEphemeral` — only `SegmentReservationAnalyzer`
- `ResolveGeneration` — `EventLeakAnalyzer`, `FinalizableObjectAnalyzer`, `CollectionAnalyzer`

Of the five analyzers originally named below as re-deriving this independently, only
`FinalizableObjectAnalyzer` is a confirmed `SegmentKindMapper` consumer. `GCGenerationAnalyzer`,
`AllocationPatternAnalyzer`, `LohFragmentationAnalyzer`, and `WeakReferenceAnalyzer` were not found
as callers of either method — they likely still carry their own generation/segment derivation,
meaning the correctness-drift risk described below is not yet fully closed for that set.

**Remaining work**: migrate `GCGenerationAnalyzer`, `AllocationPatternAnalyzer`,
`LohFragmentationAnalyzer`, and `WeakReferenceAnalyzer` onto `SegmentKindMapper` to close the
duplication this item originally targeted.

<details>
<summary>Original analysis (superseded)</summary>

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

**Priority**: was **P1**, sequenced after item 1.

</details>

## 6. Evidence Builder — **done**

`Evidence`/`EvidenceSignal` (`src/DumpDetective.Analysis/Models/Evidence.cs`) is the shared
"why alive / why matter" model — estimated retained bytes, a formatted sample root path with a
truncation flag, and a list of contributing signals. `DominatorAnalyzer` (post-merge with
`RetentionAnalyzer`), `StaticRootLeakDetector`, and `EventLeakAnalyzer` all populate it for their
top-K items instead of their own ad hoc DTOs. Sample root paths come from the new
`SampleRootPathFinder` (`src/DumpDetective.Analysis/Traversal/SampleRootPathFinder.cs`), a per-root
BFS extracted from `ReferenceChainAnalyzer`'s cheap Fast-mode path search — not the heavier
`RootPathFinder`/`BoundedGraphWalk` machinery — with the shared 20-depth cap enforced internally.
`LeakCandidateAnalyzer` was intentionally left out of scope; it gets evidence via item 8 (ranking
engine) instead. See
[phase0-deliverable-10-platform-roadmap.md P0 item 4](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0)
for the full design and status.

<details>
<summary>Original analysis (superseded)</summary>

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

**Priority**: was **P1**.

</details>

## 7. Sampling Framework — **done**

**Re-verified 2026 (this pass)**: implemented and shared across exactly the quartet flagged
below. `TypedResourceCandidateScanner.DiscoverCandidates`
(`src/DumpDetective.Analysis/Analyzers/TypedResourceSampler.cs`) is the shared candidate-type
discovery routine — reads `TypeAggregates` off the Phase-1 heap index when available (falling back
to a live `heap.EnumerateObjects()` sweep otherwise), and `InstanceStateSampler<TSnapshot>` in the
same file is the shared capped per-instance state-field sampler (`TryReserveSample`,
`AddTopSample`, `TryReadIntField`). Confirmed consumers via `ITypedResourceCandidateSource`
(candidate discovery only) and `ITypedResourceInstanceSampler<T>` (state sampling, where
applicable):
- `DbConnectionAnalyzer` — both interfaces (state sampling: open/closed/other connection tally)
- `WcfChannelAnalyzer` — both interfaces (state sampling: channel state)
- `HttpObjectAnalyzer` — candidate discovery only (no per-instance state field to sample)
- `TimerLeakAnalyzer` — candidate discovery (per Deliverable 4's original duplication callout)

This is exactly the quartet the original analysis below named as duplicating this logic
independently — confirmed closed, not just partially addressed.

<details>
<summary>Original analysis (superseded)</summary>

 (typed resource sampler)

**Current duplication**: `DbConnectionAnalyzer`, `WcfChannelAnalyzer`, `HttpObjectAnalyzer`,
`TimerLeakAnalyzer` each independently implement classify-by-type-name → sample-state-field →
bucket (Deliverable 4 §7).

**Estimated impact**: Low runtime cost, meaningful maintenance value — and directly reduces the
cost of closing Deliverable 2's EF Core / DI / ASP.NET gaps, which are naturally the same "typed
resource, sample a field, bucket by state" shape.

**Difficulty**: Low. Self-contained extraction, four existing call sites to migrate.

**Priority**: was **P1** — cheap win, high leverage for future capability additions.

</details>

## 8. Ranking / Leak-Scoring Engine — **done**

**Was**: `LeakCandidateAnalyzer` computed its own leak score while *also* independently
re-scanning `runtime.EnumerateHandles()` for GC-handle signals rather than consuming the other
leak-adjacent analyzers' output (Deliverable 3's major finding). `TimerLeakAnalyzer`,
`EventLeakAnalyzer`, `StaticRootLeakDetector`, `RetentionAnalyzer` still each compute their own
severity — this item only covers `LeakCandidateAnalyzer`'s scanning strategy, not a unified
severity formula across all five (see item 9 below, still open).

**Now**: `LeakCandidateAnalyzer` implements the new `IDeferredAnalyzer` marker interface
(`src/DumpDetective.Core/Abstractions/IDeferredAnalyzer.cs`) instead of `IAnalyzer` directly, and
reads `GCHandleDomainResult` off `AnalysisContext.CompletedRunResults` via
`AnalyzerRunResultsExtensions.GetResult<T>` (item 11's bus) rather than re-walking handles itself.
`AnalysisPipeline.ExecuteAsync` runs `IDeferredAnalyzer` implementations in a second pass, after
every non-deferred analyzer has completed and `CompletedRunResults` has been populated — so
correctness does not depend on `IAnalyzer.Order` or pipeline registration order, per the standing
constraint in item 11 above.

## 9. Confidence Scoring — **mostly done**

**Re-verified 2026 (this pass)**: the open question this item ended on is now answered, and the
answer is largely favorable — there are two distinct, genuinely shared confidence mechanisms
already in place, not the five independent heuristics the original analysis assumed:

1. **Per-finding confidence**: `EvidenceConfidence.Compute(Evidence?)`
   (`src/DumpDetective.Analysis/Models/Evidence.cs:22`) derives a 0.4–0.9 score from item 6's
   shared `Evidence` model (root-path resolution/truncation + contributing-signal count) — one
   formula, not five. Confirmed consumers: `TimerLeakFindingGenerator`, `EventLeakFindingGenerator`,
   `StaticRootFindingGenerator`, and `DominatorFindingGenerator` — i.e. 4 of the 5 analyzers this
   item originally named (`TimerLeakAnalyzer`, `EventLeakAnalyzer`, `StaticRootLeakDetector`,
   `RetentionAnalyzer`/`DominatorAnalyzer` post-merge). `LeakCandidateAnalyzer` is the one gap —
   it's covered by item 8's ranking engine instead, and doesn't appear to route through
   `EvidenceConfidence` yet; worth confirming whether its own score should be reconciled against
   this formula or is intentionally a different (ranking, not per-finding-confidence) concept.
2. **Section-level confidence**: `ConfidenceScoring.Compute(double baseScore, params Flag[] flags)`
   (`src/DumpDetective.Reporting/Services/ConfidenceScoring.cs`) is a separate shared
   base-score-minus-penalties formula, but its only confirmed caller is
   `ConfidenceSectionBuilder.AddLimitation` (`src/DumpDetective.Reporting/SectionBuilders/ConfidenceSectionBuilder.cs:134`)
   itself — i.e. it's shared in the sense of being one formula, but only one call site uses it
   today, so "shared" hasn't yet been tested against a second consumer.

**Remaining work**: reconcile `LeakCandidateAnalyzer`'s scoring with `EvidenceConfidence`, and — if
`ConfidenceScoring` is meant to generalize beyond the "Known Limitations" section, per-analyzer
severity scoring (i.e. what item 9 originally meant by "confidence") isn't necessarily the same
axis as this bounded-scan-quality score. Worth a design pass to confirm they're deliberately
distinct concepts (per-finding confidence vs. scan-completeness caveats) rather than accidentally
divergent ones.

<details>
<summary>Original analysis (superseded)</summary>

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

**Priority**: was **P0/P1**, tied to item 8 — design them together, since a ranking engine without
a shared confidence formula just moves the inconsistency rather than removing it.

</details>

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

## Status summary (re-verified 2026, this pass)

| Item | Status |
|---|---|
| 1. Heap index dispatcher | **Done** — residual gap: standalone-`AnalyzeAsync` bypass not fully closed |
| 2. Statistics engine | **Partially done** — `ModuleAnalyzer` still has its own parallel path |
| 3. Root/retention graph service | **Done** |
| 4. Type metadata classification | **Done** |
| 5. Object metadata classification | **Partially done** — 1 of 5 originally-named analyzers migrated |
| 6. Evidence builder | **Done** |
| 7. Sampling framework | **Done** |
| 8. Ranking/leak-scoring engine | **Done** |
| 9. Confidence scoring | **Mostly done** — per-finding formula shared by 4/5 analyzers; section-level formula has 1 caller |
| 10. Reporting helpers | Open |
| 11. Inter-analyzer result bus | **Done** |

## Sequencing (feeds Deliverable 10)

```
P0 foundation:  [1] Index dispatcher (done) ──┬─→ [2] Statistics engine (partial: close ModuleAnalyzer gap)
                                               └─→ [5] Object metadata classification (partial: migrate remaining 4)

P0 foundation:  [11] Inter-analyzer result bus (done) ─→ [6] Evidence builder (done) ─→ [8] Ranking engine (done)
                                                                                       └─→ [9] Confidence scoring (mostly done)

P1 independent: [3] Root graph service (done), [4] Type classification (done), [7] Sampling framework (done)

P2 last:        [10] Reporting helpers (depends on 7, which is now done — unblocked)
```

Most of what this document originally scoped as open, foundational work turned out to already be
shipped by the time of this re-verification pass. What's left is narrower and more surgical: close
`ModuleAnalyzer`'s parallel statistics path (item 2), migrate the remaining four
generation/segment-classification call sites onto `SegmentKindMapper` (item 5), decide whether
`LeakCandidateAnalyzer` should route through `EvidenceConfidence` (item 9), and pick up item 10
(reporting helpers), which was always last in the sequence and is now fully unblocked since item 7
landed.
