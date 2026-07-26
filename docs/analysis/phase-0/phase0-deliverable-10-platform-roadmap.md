# Phase 0 — Deliverable 10: Platform Roadmap

> Scope: **Deliverable 10**, the final deliverable, from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Consolidates [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md) through
> [Deliverable 9](phase0-deliverable-9-industry-benchmark.md) into a roadmap, and closes by
> explicitly answering the review's seven Success Criteria questions.
>
> This document describes **current state and remaining work only**. It does not track dated
> history of how these conclusions were reached — see individual Deliverable docs and git history
> for that.

---

## Current State

- **Heap-scan footprint (verified against source, not estimated)**: of 35 `IAnalyzer`
  implementations, **9** stream the on-disk `HeapEntry` object index via
  `HeapAnalysisCache.EnumerateIndexedEntries()` / `EnumerateIndexedEntriesAsTuples()`
  (`DbConnectionAnalyzer`, `CrashAnalyzer`, `CollectionAnalyzer`, `AsyncTaskAnalyzer`,
  `HangAnalyzer`, `EventLeakAnalyzer`, `MemoryLeakAnalyzer`, `WcfChannelAnalyzer`,
  `StringAnalyzer`), and a further **5** perform a full live `ClrHeap.EnumerateObjects()` sweep with
  no index path at all (`TimerLeakAnalyzer`, `HttpObjectAnalyzer`, `FinalizableObjectAnalyzer`,
  `LohFragmentationAnalyzer`, `HeapTopologyAnalyzer` — the last two per-segment, not whole-heap).
  **14 of 35** analyzers do some form of full/broad heap traversal; only the **9** index-scanning
  ones are addressable by a single shared index-scan dispatcher — the 5
  `EnumerateObjects()`-based analyzers are architecturally distinct and need a separate mechanism
  if they're ever addressed (see [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md)).
- **Inter-analyzer result bus (Deliverable 5 item 11): implemented.** A post-hoc bus:
  `AnalyzerRunResultsExtensions.GetResult<T>(this IReadOnlyList<AnalyzerRunResult> runs)`
  (`src/DumpDetective.Core/Models/AnalyzerRunResult.cs`), `internal` and usable from any assembly
  with `InternalsVisibleTo` on `DumpDetective.Core` (Analysis, Reporting, Cli, Tests,
  BenchmarkSuite1). `InsightEngine.FindResult<T>` (`InsightEngine.cs:1574`) delegates to it instead
  of duplicating the scan. No live/mid-run channel exists or is planned — `AnalysisContext` is
  unchanged; consumers call this only after the pipeline finishes, same as `InsightEngine.Analyze`
  already did. A live, mid-run bus keyed off `Order` was considered and rejected: it would make
  correctness depend on execution order staying stable as analyzers are added/reordered, the same
  risk as the `HeapTopologyAnalyzer → Pipeline` violation below. This is the unlocking prerequisite
  for the Evidence builder / Ranking engine / Confidence scoring chain in [P0](#immediate-priorities-p0).
- **`HeapTopologyAnalyzer` → `Pipeline` dependency (P0 item 1): fixed.** Verified directly against
  source: the `using DumpDetective.Analysis.Pipeline;` import in `HeapTopologyAnalyzer.cs`
  referenced no symbol from that namespace — it was dead code, not a real structural coupling. It
  has been removed; no other analyzer carried the same leftover import
  ([phase0-deliverable-7-dependency-graph-review.md](phase0-deliverable-7-dependency-graph-review.md#cycles)).
- **Heap index single-pass dispatcher (Deliverable 5 item 1): proof-of-concept implemented for
  `DbConnectionAnalyzer`.** Full design in
  [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md) — an
  opt-in `IHeapIndexScanParticipant` interface plus `HeapIndexScanDispatcher`, wired into
  `AnalysisPipeline.ExecuteAsync`. The former test-bypass design problem is resolved via option
  (b): `DbConnectionAnalyzerDiscrepancyTests.cs` now drives the analyzer through a real
  `AnalysisPipeline` instance instead of calling `analyzer.AnalyzeAsync()` directly, and
  `DbConnectionAnalyzer.AnalyzeAsync` no longer carries a per-call "primed" self-scan fallback — it
  trusts the pipeline dispatcher to have already run `BeforeHeapIndexScan`/`OnHeapEntry`. See
  [Near-term (P1)](#near-term-p1) item 1 for details. **All 9 disk-index-streaming analyzers have
  now been migrated** to the same pattern — see [P1](#near-term-p1) item 2 for the full list and
  the open architectural findings from the migration.

---

## Current Architecture Assessment

### Strengths

- **The core architectural bet is right.** An extensible `IAnalyzer` catalog producing automated,
  cross-cutting, single-pass analysis of a static dump is closest in spirit to dotMemory's
  automated-inspection philosophy — the highest-value approach of the four industry tools
  benchmarked (Deliverable 9). Nothing in this review suggests abandoning that bet.
- **Genuinely strong extensibility relative to the industry.** Adding a new analyzer is "implement
  an interface and register it," versus WinDbg's native-extension-DLL friction or PerfView's
  internal-event-model friction (Deliverable 9).
- **Breadth no single comparison tool matches.** Memory, GC, threads, locks, exceptions, and leak
  candidates in one pass is a real differentiator over WinDbg + PerfView + dotMemory run
  separately (Deliverable 9).
- **The type-metadata caching layer is correctly designed.** `HeapAnalysisCache` is shared by
  nearly every analyzer for `MethodTable → ClrType` resolution — the one piece of the caching
  story that was already right before this review (Deliverable 4 §3, Deliverable 8 §4).
- **Several analyzers are reference examples of correct scoping**: `SegmentReservationAnalyzer`
  and `JitAnalyzer` are correctly isolated from the object index they don't need
  (Deliverable 1/3/6); `StringAnalyzer` stays appropriately large-but-single-purpose without
  drifting into scope creep (Deliverable 1).
- **No analyzer earned an outright removal verdict** (Deliverable 6) — the platform's problem is
  duplication and coupling, not wasted capability.

### Weaknesses

- **9 of 35 analyzers independently stream the full on-disk object index**, with no shared
  single-pass dispatcher (Deliverable 4 §1, Deliverable 8 §1). A further 5 analyzers do a full
  live `ClrHeap.EnumerateObjects()` sweep, which this dispatcher cannot address. Still the single
  largest weakness in the platform in kind, though the blast radius is smaller than the platform's
  original architectural estimate of "up to 26 of 36."
- **Leak/retention evidence is fragmented across 6 analyzers** with no unified scoring or
  confidence model (Deliverable 3, 5, 7, 9) — the platform's weakest point relative to the
  industry benchmark specifically.
- **At least 4 duplicate-logic clusters exist by convention, not by contract**: the resource-state
  sampler quartet, the thread-domain quartet, the static-field sweep pair, and the handle-table
  trio (Deliverable 1, 3, 4, 7).
- **One analyzer boundary is simply wrong**: `ModuleAnalyzer`/`AppDomainAnalyzer` overlap, compounded
  by `AppDomain` being a largely vestigial concept in modern .NET (Deliverable 6).
- **A handful of infrastructure-leakage outliers**: `CollectionAnalyzer`'s lone logging dependency,
  `AsyncTaskAnalyzer`'s private on-disk index format (Deliverable 3, 7, 8). (`HeapTopologyAnalyzer`'s
  dependency on the orchestration layer was resolved as P0 item 1 — see
  [Current State](#current-state).)
- **Real capability gaps remain**, most notably DI-container leak detection, EF Core awareness,
  and crash minidump exception-stream triage (Deliverable 2, 9).

### Biggest Risks

1. **The ~9x on-disk index-scan multiplier is a direct threat to the project's own definition of
   done** ("works on 10GB+ dumps... reasonable runtime," CLAUDE.md). This is not a theoretical
   concern — it's a structural mismatch between the platform's stated performance goal and its
   current execution model. It is smaller in magnitude than the platform's original architectural
   estimate (~26x) and is not unambiguously the single biggest risk on this list without weighing
   it against risk #2.
2. **Fragmented leak evidence threatens the product's credibility**, not just its code quality —
   Deliverable 9 showed this is the one gap that actually undermines DumpDetective's core value
   proposition against the tool it's most philosophically similar to (dotMemory).
3. ~~The `HeapTopologyAnalyzer` → `Pipeline` dependency is a small violation today that risks
   metastasizing~~ — **resolved** (P0 item 1): the import was confirmed dead and removed, so the
   dependency-direction precedent this risk warned about no longer exists.
4. **The 4x registration fan-out compounds every future analyzer addition and every Deliverable 6
   merge/split**, and nothing currently prevents it from growing unchecked as the analyzer count
   increases past 36 (Deliverable 7, 9).

---

## Priority ordering rationale

Two independent P0 tracks exist — the **Correctness track** (inter-analyzer bus → root graph
service → evidence builder → ranking engine → confidence scoring) and the **Performance track**
(heap-index dispatcher and what pairs with it). Neither blocks the other. The Correctness track
leads because its risk (#2 above) is a product-credibility issue undiminished by any measurement,
while the Performance track's risk (#1 above) was found to be ~3x smaller in blast radius than
originally estimated and had an additional implementation blocker (the direct-`AnalyzeAsync`-call
test bypass) that has since been resolved — see [P1](#near-term-p1) item 1. Within each track, items below
are ordered by dependency, not just by value, so **build top-to-bottom within a track**.

---

## Immediate Priorities (P0) — Correctness track

1. ~~Fix the `HeapTopologyAnalyzer` → `Pipeline` dependency~~ (Deliverable 3, 7) — **done.** The
   import was confirmed dead (no symbol from `Pipeline` was consumed) and removed; see
   [Current State](#current-state). The dependency-direction discipline this established should
   still be respected by item 2 below and the Performance track's dispatcher.
2. ~~**Root/retention graph service**~~ (Deliverable 5 item 3) — **done.** `RootSetCache`
   (`src/DumpDetective.Analysis/Cache/RootSetCache.cs`) replaces `RootCache` as the single
   canonical root-set service: builds `RootRecord` (`TargetAddr`, `RootAddr`, `Kind`) once per run
   from the Phase-1 disk index, falling back to a live `heap.EnumerateRoots()` walk when no index
   is present. `GCRootAnalyzer`, `StaticRootLeakDetector`, and `EventLeakAnalyzer` all read roots
   through it instead of each independently re-enumerating stack/static/handle roots.
   `BoundedGraphWalk` (`src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs`) replaces
   `HeapTypePathTraversal`, `BoundedRetainedSizeBfs`, and `HeapAnalysisCache.GetRetainedObjects`
   (all deleted) as the single canonical forward-BFS primitive, enforcing the 20-depth cap
   internally; `GCRootAnalyzer`, `RetentionAnalyzer`, `DominatorAnalyzer`, and
   `StaticRootLeakDetector` all call into it. `RootPathFinder`/`ReferenceChainAnalyzer`'s
   bidirectional shortest-root-path search was intentionally left untouched — a different problem
   shape, out of scope here. See
   [docs/architecture.md § Graph and traversal](../../architecture.md#graph-and-traversal) for the
   full design.
3. ~~**Execute two of the three Deliverable 6 merges alongside item 2**~~ — **done.**
   `RetentionAnalyzer` merged into `DominatorAnalyzer` (folds the high-fan-in signal into the
   canonical retained-size provider, resolving the `MemoryLeakAnalyzer.cs`/`RetentionAnalyzer`
   file/class-name mismatch as part of the merge; `MemoryLeakAnalyzer.cs` deleted), and
   `DependentHandleAnalyzer` merged into `GCHandleAnalyzer` (a `DependentHandle` is one
   `HandleKind`, not a separate data source — no technical reason for a standalone handle-table
   walk; `DependentHandleAnalyzer.cs` deleted). Each merge folds the domain result, finding
   generator, trend comparer, and section builder into the surviving analyzer's files, with the
   catalog and `SectionIdDomainMap` registrations for the removed analyzers deleted. (The third
   merge, `AppDomainAnalyzer` into `ModuleAnalyzer`, has since been completed — see
   [P1](#near-term-p1).)
4. ~~**Evidence builder**~~ (Deliverable 5 item 6) — **done.** `Evidence`/`EvidenceSignal`
   (`src/DumpDetective.Analysis/Models/Evidence.cs`) is the shared "why alive / why matter" shape:
   estimated retained bytes, a formatted sample root path (with a truncation flag), and a list of
   contributing signals. `DominatorAnalyzer` (post-merge), `StaticRootLeakDetector`, and
   `EventLeakAnalyzer` all populate it for their top-K items instead of their own ad hoc DTOs
   (`StaticRootLeakDetector`'s generic `NameBytesEntry` rows were replaced by a proper
   `StaticRootSnapshot` type; `EventLeakAnalyzer`'s raw `RootHint` string is now backed by
   `Evidence.SampleRootPath`). Sample root paths are found via the new
   `SampleRootPathFinder` (`src/DumpDetective.Analysis/Traversal/SampleRootPathFinder.cs`), a
   per-root BFS extracted from `ReferenceChainAnalyzer`'s cheap Fast-mode path search (not the
   heavier `RootPathFinder`/`BoundedGraphWalk` machinery) and shared 20-depth cap enforced
   internally.
5. ~~**Ranking / leak-scoring engine — replace `LeakCandidateAnalyzer`'s scanning strategy with an
   aggregation strategy**~~ (Deliverable 5 item 8; Deliverable 6's Replace Recommendation) —
   **done.** `LeakCandidateAnalyzer` no longer independently walks `runtime.EnumerateHandles()`;
   it now implements the new `IDeferredAnalyzer` marker interface
   (`src/DumpDetective.Core/Abstractions/IDeferredAnalyzer.cs`) and reads the already-completed
   `GCHandleDomainResult` via `AnalyzerRunResultsExtensions.GetResult<T>` against
   `AnalysisContext.CompletedRunResults`. `AnalysisPipeline` runs `IDeferredAnalyzer`
   implementations in a second pass after every non-deferred analyzer has finished, populating
   `CompletedRunResults` in between — so the result is order-independent by construction rather
   than depending on `IAnalyzer.Order`, matching the constraint called out in
   [Current State](#current-state) against a live, `Order`-keyed bus.
6. ~~**Confidence scoring wired to the existing `ConfidenceSectionBuilder`**~~ (Deliverable 5 item 9)
   — **done.** Confirmed against the implementation that `ConfidenceSectionBuilder` neither consumed
   a structured per-finding confidence value nor re-derived one — its "Measured/Heuristic/Partial/
   Speculative" legend was decorative text with no number ever produced against it, and three section
   builders (`DominatorSectionBuilder`, `GCRootIntelligenceSectionBuilder`,
   `LeakAnalysisSectionBuilder`) called `BuildConfidenceBand` with hardcoded literal scores instead of
   their already-computed scan-quality caveats. Fixed with two shared helpers: `ConfidenceScoring`
   (`src/DumpDetective.Reporting/Services/ConfidenceScoring.cs`) computes a section-level score from a
   base tier minus penalties for active scan-quality flags, wired into those three section builders
   and into `ConfidenceSectionBuilder`'s Z3 "Known Limitations" table (now renders a real numeric
   confidence column per flagged limitation instead of just the legend line); `EvidenceConfidence`
   (`src/DumpDetective.Analysis/Models/Evidence.cs`) computes a finding-level score directly from an
   `Evidence` record's resolved/truncated sample root path and contributing-signal count, wired into
   `InsightFinding.ConfidenceScore` for `DominatorFindingGenerator`, `StaticRootFindingGenerator`,
   `EventLeakFindingGenerator`, and `TimerLeakFindingGenerator` (replacing the severity-only default
   for those findings). `LeakCandidateFindingGenerator` has no per-finding `Evidence` to draw on today
   (per item 5, it reads `GCHandleDomainResult` directly) and stays on the section-level
   `LeakAnalysisSectionBuilder` score. `dotnet build`/`dotnet test` pass with no regressions; no golden
   file updates were needed since the fixture-based golden tests don't exercise the newly-wired paths.

---

## Near-term (P1) — Performance track and independent infra

**Performance track (dependency order)**

1. **Resolve the direct-`AnalyzeAsync`-call test-bypass design problem — done, option (b)
   implemented.** `DbConnectionAnalyzerDiscrepancyTests.cs` previously invoked
   `analyzer.AnalyzeAsync()` directly, bypassing `AnalysisPipeline`, the mechanism that would prime
   dispatcher-participant state first. Of the three options that were on the table (see
   [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md)'s "Open
   design problem" section): (a) a per-call "primed" flag with fallback to a self-contained scan
   when not primed, (b) migrate direct-call tests to drive analyzers through a
   pipeline/dispatcher invocation, or (c) leave `DbConnectionAnalyzer` unmigrated and prove the
   dispatcher with a synthetic participant only — **(b) is what was implemented**:
   `DbConnectionAnalyzer.AnalyzeAsync` no longer carries a `_primedContext`/self-priming dual-mode
   branch (the now-dead `ScanFullHeapFallback` was deleted too), and
   `DbConnectionAnalyzerDiscrepancyTests.cs` now builds a real `AnalysisPipeline` (via a
   `RunThroughPipelineAsync` helper, one fresh analyzer instance per pipeline run) to drive the
   analyzer through `HeapIndexScanDispatcher` for both the in-memory and disk-backed cache cases.
   Verified: `dotnet build DumpDetective.slnx` (0 errors) and a filtered `dotnet test` run covering
   `DbConnectionAnalyzer`, `AnalysisPipelineTests`, and `HeapIndexScanDispatcherTests` (7 passed, 1
   skipped — the discrepancy test still skips without a real dump via `DD_BENCHMARK_DUMP`,
   unchanged from before this change). No other production or test call site invokes
   `DbConnectionAnalyzer.AnalyzeAsync` directly; the only other `src/` references are the domain
   model, the report section-ID map, and `DefaultAnalyzerFeatureModuleCatalog`'s
   `typeof(DbConnectionAnalyzer)` registration entry (used by `DefaultAnalyzerFactory` to construct
   analyzer instances that are fed into `AnalysisPipeline` — not a bypass).
2. **Heap index single-pass dispatcher** (Deliverable 5 item 1, Deliverable 8 §1) — item 1 above is
   resolved, and should also wait for either a profiling run confirming the ~9x
   scan cost is significant on a representative 10GB+ dump (Deliverable 8's own open item), or a
   decision to proceed without that confirmation. Addresses 9 of 35 analyzers (verified); proof of
   concept scoped to `DbConnectionAnalyzer` initially. **Migration status: complete.**
   `DbConnectionAnalyzer`, `CrashAnalyzer`, `CollectionAnalyzer`, `HangAnalyzer`,
   `WcfChannelAnalyzer`, `StringAnalyzer`, `DominatorAnalyzer` (post-merge, formerly
   `MemoryLeakAnalyzer`), `AsyncTaskAnalyzer`, and `EventLeakAnalyzer` — all 9 verified
   disk-index-streaming analyzers — are now migrated to `IHeapIndexScanParticipant`.
   `EventLeakAnalyzer` was originally flagged as an architectural mismatch (its scanner uses
   internal `Parallel.For` chunking), but turned out not to need a dispatcher design change: its
   `EventLeakFastScanner` gained a single-entry `ScanEntry(...)` method factored out of the
   existing per-object loop body, which both the dispatcher's serial callback and the scanner's
   own internal chunked loop now call — see
   [phase0-analyzer-heap-scan-migration-status.md](phase0-analyzer-heap-scan-migration-status.md)
   for the per-analyzer detail. **(Fixed) No progress-reporting hook.**
   `HeapIndexScanDispatcher.Run` previously had no progress-reporting hook, so the per-object
   `ObjectScanCounter` progress messages (e.g. "scanning heap objects") that a migrated analyzer's
   scan loop used to report were lost for the dispatcher path. Fixed: the dispatcher now drives its
   own `ObjectScanCounter` over the shared scan and publishes `AnalyzerStarted` /
   `AnalyzerProgress` / `AnalyzerCompleted` events to `context.DiagnosticsSink` under a synthetic
   `"Shared heap index scan"` name (via `AnalysisDiagnosticsPublisher`, the same mechanism
   `AnalyzerExecutionRunner` uses for individually-run analyzers), so the CLI console and verbose
   diagnostics log both show live progress during the shared pass without per-analyzer plumbing
   changes. `Run`'s public signature is unchanged.

   **Architect review findings (dispatcher + all migrated analyzers):** a scrutiny pass against
   `HeapIndexScanDispatcher.cs`, `IHeapIndexScanParticipant.cs`, `AnalysisPipeline.cs`, and the
   migrated analyzers surfaced five open issues, two of them blocking. **Both blocking issues are
   now fixed:**
   - **(Fixed) No failure isolation around the shared scan.** `HeapIndexScanDispatcher.Run` now
     wraps each participant's `BeforeHeapIndexScan` and `OnHeapEntry` call in its own try/catch
     (`HeapIndexScanDispatcher.cs`), tracking a per-participant `failed` flag rather than letting an
     exception propagate out of the shared loop. One participant throwing (e.g. a null ClrMD field
     read on a corrupt object) no longer fails every other analyzer in the run — it degrades only
     the offending participant, which then observes `OnHeapIndexScanCompleted(succeeded: false)` and
     falls back to its own self-contained scan path.
   - **(Fixed) The "was the shared scan actually primed" gate is no longer duplicated.**
     `IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded)` is now the single source of
     truth the dispatcher hands each participant after the shared pass finishes (or fails), instead
     of every migrated analyzer independently re-deriving `cache.TryGetHeapIndex(out _)` a second
     time in `AnalyzeAsync`. Analyzers store the callback's `succeeded` value (e.g.
     `AsyncTaskAnalyzer._participantScanSucceeded`) and gate on it directly — closing the same bug
     class as the direct-`AnalyzeAsync`-call test bypass already fixed once for `DbConnectionAnalyzer`
     (item 1 above).
   - **Concrete-type coupling silently disables the optimization.** `AnalysisPipeline` only wires up
     the dispatcher when `context.Cache is HeapAnalysisCache`, not the `IHeapAnalysisCache`
     interface every analyzer otherwise programs against. Any other `IHeapAnalysisCache`
     implementation silently skips the shared scan with no error or diagnostics event — every
     participant just falls back to its own scan. Correctness-preserving today, but an invisible
     perf cliff and a sign the "single-pass" architecture only exists for one concrete class.
   - **The "one shared pass beats N independent scans" premise is unverified.** The dispatcher path
     is a single-threaded `foreach` over `EnumerateIndexedEntries()` fanning out to every
     participant per entry. It replaced fallback paths like `CrashAnalyzer`'s
     `Parallel.ForEach(heap.Segments, ...)`, which used full core parallelism. Trading N parallel
     full scans for one sequential shared scan is only a net win if the disk-index read, not CPU,
     is the bottleneck — plausible, but not demonstrated against a `BenchmarkSuite1` run on a
     representative large dump. Given CLAUDE.md's "reasonable runtime on 10GB+ dumps" as a
     definition-of-done criterion, this should be measured, not assumed, before broader rollout.
   - **Each migrated analyzer now carries three parallel implementations of the same logic**:
     the participant path, a parallel-segment no-index fallback, and (for `AsyncTaskAnalyzer`) a
     third raw-heap fallback. The `*DiscrepancyTests` suite exists specifically to catch these
     paths disagreeing — structural duplication the dispatcher was meant to remove, not add to.
     Worth exploring whether the no-index fallback could drive the same `OnHeapEntry` logic over a
     live `ClrHeap.EnumerateObjects()` loop (one behavior, two drivers) instead of a fourth
     hand-duplicated code path per analyzer.
3. ~~**Per-type statistics engine**~~ (Deliverable 5 item 2) — **premise already satisfied, no longer
   blocked on the dispatcher.** This item assumed the 9x-duplicated per-type reduction (deliverable-8
   review §5) would need to be computed once as a `HeapIndexScanDispatcher` participant. In practice
   it's already solved one layer earlier, at Phase 1: `TypeIndexBuilder` (`Indexing/TypeIndexBuilder.cs`)
   accumulates count/size/LOH/gen-bucket per `MethodTable` during the single-pass index build and
   persists it as `TypeAggregateIndexEntry`; `StatisticsCache.GetOrBuildTypeStatistics` hydrates a
   name-keyed view from that persisted data in O(distinct types), memoized once per pipeline run
   because `HeapAnalysisCache`/`StatisticsCache` is a single shared instance across all analyzers in
   an `AnalysisPipeline.ExecuteAsync` call. An audit of the analyzers still calling
   `heap.EnumerateObjects()` outside the dispatcher-migrated 9 (`FinalizableObjectAnalyzer`,
   `HttpObjectAnalyzer`, `TimerLeakAnalyzer`) found no genuine duplication: all three already read
   `TypeAggregates` as their primary path, with `EnumerateObjects()` only as the same
   index-absent-fallback pattern already accepted for the migrated analyzers (finding #5 above).
   **What remains is narrower than originally scoped**: `TypeAggregates` (MT-keyed) and
   `StatisticsCache`'s `CachedTypeStatistics` (name-keyed) are two independently-maintained
   representations of the same data, and `HttpObjectAnalyzer`/`TimerLeakAnalyzer`/
   `FinalizableObjectAnalyzer`/`StatisticsCache` each separately call `heap.GetTypeByMethodTable(mt)`
   to resolve a type name per aggregate entry — redundant resolution work and two shapes that could
   in principle drift apart. Tracked as a smaller consolidation task, not the original "run per-type
   stats as a dispatcher participant" item.

   **(Done) Type-name-resolution consolidation.** Added `TypeAggregateNameResolver`
   (`Cache/TypeAggregateNameResolver.cs`) as the single MT→name/module resolution point
   (MethodTable lookup → sample-instance fallback → placeholder). `StatisticsCache`,
   `HttpObjectAnalyzer`, `TimerLeakAnalyzer`, and `FinalizableObjectAnalyzer` now all call it
   instead of independently reimplementing the fallback chain. Full test suite green (328
   passed / 0 failed) after the change; no behavior change to the resolution order, only
   `FinalizableObjectAnalyzer`'s no-match placeholder text changed from `MT:0x...` to
   `MethodTable@0x...` to match the shared format (no test asserted the old string). Item 3 is
   now fully closed.
4. ~~**Object metadata classification**~~ (generation/segment bucket, Deliverable 5 item 5) — **scope
   narrower than originally framed, now closed.** `GCGenerationAnalyzer` already consumed per-type
   `Gen0Count/Gen1Count/Gen2Count` from `TypeAggregateIndexEntry` (built once during index scan), and
   `AllocationPatternAnalyzer`/`LohFragmentationAnalyzer`/`SegmentReservationAnalyzer` only needed
   segment-*kind* classification, already served by `SegmentKindMapper`. Neither needed further work.
   The real duplication was three independent per-*address* generation resolvers (each wrapping
   `heap.GetSegmentByAddress(address)` → `segment.GetGeneration(address)`) in
   `FinalizableObjectAnalyzer`, `EventLeakAnalyzer`, and `CollectionAnalyzer`, differing only in their
   unknown/failure fallback value. `heap.GetSegmentByAddress` already does its own efficient lookup
   internally, so no new segment-boundary table was needed — no dispatcher, interface, or on-disk
   format changes were required either; each analyzer already holds a live `ClrHeap` from
   `BeforeHeapIndexScan`.

   **(Done) Shared generation resolver.** Added `SegmentKindMapper.ResolveGeneration(ClrHeap, ulong)`
   (`Analyzers/SegmentKindMapper.cs`) as the single per-object generation resolution point, returning
   `-1` for unresolvable addresses (invalid address, no owning segment, or a ClrMD exception).
   `FinalizableObjectAnalyzer` and `EventLeakAnalyzer` now call it instead of maintaining their own
   copies. `CollectionAnalyzer`'s copy was dead/broken: it resolved generation via reflection
   (`typeof(ClrObject).GetProperty("Generation")`, `typeof(ClrHeap).GetMethod("GetGeneration", ...)`),
   neither of which exists on ClrMD 4's public API, so both handles were always `null` and every call
   silently fell through to a hardcoded `return 2` — **`CollectionAnalyzer`'s generation breakdown had
   been reporting every collection as Gen2 regardless of actual generation.** Switching to the shared
   resolver fixed this as a side effect; the per-kind bucketing was also corrected to skip unresolvable
   (`-1`) addresses instead of silently folding them into the Gen0 bucket. Full build clean, relevant
   analyzer test suite green (27 passed / 0 failed, 4 skipped) after the change. Item 4 is now fully
   closed.
5. ~~**Confirm container/satellite index build-once-vs-per-invocation behavior**~~ (Deliverable 8
   §2/consolidation #3) — a verification task, independent of the dispatcher chain above. **Confirmed
   safe, no code fix required.** The index — object index and all `Indexing.Container`/
   `Indexing.Satellite` sections — is built exactly once per unit of work (one dump, one pipeline
   run), never per analyzer invocation.

   **(Done) Verification.** `DiskBackedObjectIndexWriter` is only ever constructed inside
   `HeapIndexCache.PrebuildHeapIndex` (`Cache/HeapIndexCache.cs`), which guards against rebuilding
   with `if (_heapIndex is not null) return _heapIndex;`; no analyzer or satellite reader constructs
   the writer directly. `DiskBackedObjectIndexWriter.Build` (`Indexing/DiskBackedObjectIndexWriter.cs`)
   writes the container and all satellite sections (task/event/large-object/LOH-free-block indexes)
   together in one call, with `TypeAggregates` written last as a completion marker — there is no
   separate, deferred build path triggered by whichever analyzer happens to touch a satellite section
   first. `TryLoadFromCache` additionally short-circuits to the on-disk `cache.bin` when the
   dump-content hash matches, so even a fresh process run against the same dump skips rebuilding
   entirely. On the caching side, `BuildHeapIndexStage` (single-dump pipeline) and
   `PerDumpExecutionService` (trend/batch mode) each construct exactly one `HeapAnalysisCache` per
   dump, and `RunAnalyzersPipelineStage` passes that single shared instance into
   `AnalyzerExecutionService.BuildContext`, so every analyzer in a run shares one cache — never a
   fresh one per analyzer. (A minor, out-of-scope observation: `EventLeakAnalyzer` and
   `DominatorAnalyzer` still call `EnumerateIndexedEntries()` directly outside `HeapIndexScanDispatcher`,
   but only as guarded fallback paths when the shared dispatcher scan didn't run; `QueryEngine`'s
   direct call is its separate ad hoc query tool, covered by item 13 below.) Item 5 is now fully
   closed.

**Independent infra (no blocking dependencies — can start any time)**

6. ~~**Shared type-classification layer**~~ for the 8 analyzers currently rolling their own type-name
   pattern matching (Deliverable 5 item 4) — cheap, and directly reduces the cost of the Deliverable
   2 capability gaps in [P2](#medium-term-p2) that need the same classification (EF Core, DI,
   Channels). **Done.**

   **(Done) Shared type-classification layer.** Added `TypeNamePatternMatcher`
   (`Analyzers/TypeNamePatternMatcher.cs`) as the single home for the namespace-prefix / suffix /
   contains-token / short-name-extraction shape shared across the 8 analyzers, exposing four
   ordinal-comparison primitives (`HasAnyPrefix`, `ContainsAny`, `HasPrefixAndSuffixOrContains`,
   `GetShortName`) with no LINQ, no `Regex`, and no universal "classify into one enum" API — each
   caller keeps its own literal pattern lists and category enum, only the matching boilerplate
   moved. Migrated `DbConnectionAnalyzer.IsConnectionType`, `WcfChannelAnalyzer.IsWcfChannelType`,
   `HttpObjectAnalyzer.IsHttpMessageHandler`, `TimerLeakAnalyzer.ClassifyType`'s `OtherTimer`
   fallback, and both of `CollectionAnalyzer`'s BCL-namespace-check/short-name-extraction call
   sites (removing its duplicated inline copy and dead `s_typeNameCutChars` field), and
   `AsyncTaskAnalyzer`'s task-type prefix check. `WeakReferenceAnalyzer` (single `StartsWith` call)
   and `AsyncStateMachineAnalyzer` (genuinely dynamic `<...>d__N` `Regex` suffix, not shared by
   anything else) were left untouched, as scoped. Added
   `TypeNamePatternMatcherTests.cs` covering all four primitives. Full build clean, full test suite
   green (346 passed / 0 failed, 39 skipped) after the change; no golden-file or discrepancy-test
   regressions, since classification behavior is unchanged — only the implementation moved. Item 6
   is now fully closed.
7. ~~**Shared typed-resource sampler**~~ for the `DbConnectionAnalyzer`/`WcfChannelAnalyzer`/
   `HttpObjectAnalyzer`/`TimerLeakAnalyzer` quartet (Deliverable 5 item 7) — self-contained
   extraction, four existing call sites to migrate. **Done.**

   **(Done) Shared typed-resource sampler.** Added `TypedResourceSampler.cs`
   (`Analyzers/TypedResourceSampler.cs`) containing two independent helpers, since the
   duplication across the quartet splits into two layers of different scope: `internal static
   class TypedResourceCandidateScanner` (`DiscoverCandidates`) for the candidate-MT discovery
   logic shared by all four analyzers — TypeAggregates-primed lookup (now uniformly resolving
   names via `TypeAggregateNameResolver`, fixing a pre-existing inconsistency where
   `DbConnectionAnalyzer`/`WcfChannelAnalyzer` used a plainer `heap.GetTypeByMethodTable(mt)?.Name`
   lookup with no sample-instance fallback) with an `EnumerateObjects()` fallback when no index is
   present — and `internal sealed class InstanceStateSampler<TSnapshot>` for the per-type-capped
   instance state-field sampling shared only by `DbConnectionAnalyzer`/`WcfChannelAnalyzer` (the
   two quartet members with a runtime state field to read), covering per-type read caps, the
   `ScanCapped` flag, and the bounded top-N "interesting instance" list. `HttpObjectAnalyzer` and
   `TimerLeakAnalyzer` only needed the candidate-scanner half (layer A); `TimerLeakAnalyzer`'s
   root-path evidence population was left untouched, as scoped. No domain-result or
   finding-generator changes — output shape is unchanged. Added
   `InstanceStateSamplerTests.cs` covering per-type cap boundaries, independent per-MT tracking,
   and top-N cap boundaries. Full build clean, full test suite green (350 passed / 0 failed, 39
   skipped) after the change; no golden-file or discrepancy-test regressions. Item 7 is now fully
   closed.
8. **Shared contracts (compiler-checked interfaces, not conventions) for the resource-sampler and
   thread-domain quartets** (Deliverable 7) — the resource-sampler contract naturally pairs with
   item 7 above (same quartet); the thread-domain contract is independent and is what lets
   `ThreadAnalyzer` become the canonical stack-walk provider that `HangAnalyzer`,
   `ThreadStackClusterAnalyzer`, and `LockGraphAnalyzer` consume instead of each independently
   walking stacks and re-deriving wait state (Deliverable 3, 4, 6).

   **(Done) Resource-sampler quartet contract.** Added `ITypedResourceCandidateSource` and
   `ITypedResourceInstanceSampler<TSnapshot>` (`Analyzers/ITypedResourceCandidateSource.cs`) plus
   `TypedResourceScanDriver` (`Analyzers/TypedResourceScanDriver.cs`), which turns the item-7
   sampler's by-convention static-helper call order into a compiler-checked one: candidate
   discovery only runs through `ITypedResourceCandidateSource.IsCandidateType`, and
   `ITypedResourceInstanceSampler<TSnapshot>.TrySample` is only reachable after
   `TypedResourceScanDriver.TryGetSample` has confirmed a sample slot was reserved via
   `InstanceStateSampler<TSnapshot>.TryReserveSample`. `DbConnectionAnalyzer` and
   `WcfChannelAnalyzer` implement both interfaces (they have a runtime state field to sample);
   `HttpObjectAnalyzer` and `TimerLeakAnalyzer` implement only `ITypedResourceCandidateSource`, as
   scoped in item 7. All four quartet members now call `TypedResourceScanDriver.DiscoverCandidates`/
   `CreateSampler`/`TryGetSample` instead of the item-7 static helpers directly; no remaining
   direct calls to `TypedResourceCandidateScanner.DiscoverCandidates` or `new
   InstanceStateSampler<T>(...)` outside the driver. No domain-result or finding-generator
   changes — output shape is unchanged. Verified: `dotnet build DumpDetective.slnx` (0 errors) and
   a filtered `dotnet test` run covering the quartet plus `TypedResourceSampler`/
   `InstanceStateSampler` (4 passed, 4 skipped, 0 failed). The thread-domain half of item 8 is a
   separate, independent piece of work and is not covered by this update.

   **(Done) Thread-domain quartet contract.** Added `IThreadStackScanParticipant`
   (`Pipeline/IThreadStackScanParticipant.cs`) and `ThreadStackScanDispatcher`
   (`Pipeline/ThreadStackScanDispatcher.cs`), which run a single
   `EnumerateStackTrace()` pass per thread and hand each participant a
   `ThreadStackSnapshot` (`Pipeline/ThreadStackSnapshot.cs`) — a thread plus its
   already-materialized top-N frames — instead of each of `ThreadAnalyzer`,
   `HangAnalyzer`, `ThreadStackClusterAnalyzer`, and `LockGraphAnalyzer`
   independently walking `runtime.Threads`/`EnumerateStackTrace()`.
   `ThreadAnalyzer` remains the frame-count driver via
   `GetRequiredFrameCount`/`MaxSampledStackSnapshots`; the other three only need
   the top frame. Because `IThreadStackScanParticipant` and `ThreadStackSnapshot`
   are `internal` but the analyzer classes are `public`, `OnThreadStack` is
   implemented explicitly (`void IThreadStackScanParticipant.OnThreadStack(...)
   => OnThreadStack(...)` delegating to a `private` overload) — the same pattern
   `IHeapIndexScanParticipant.OnHeapEntry` already uses in this quartet's
   heap-scan counterpart. Each analyzer keeps a non-participant fallback path
   (its old independent walk) for direct invocation outside
   `AnalysisPipeline`'s dispatcher (tests, benchmarks). No domain-result or
   finding-generator changes — output shape is unchanged. Verified: full-solution
   `dotnet build` (0 errors) and a filtered `dotnet test` run covering all four
   analyzers (11 passed, 4 skipped — the skipped tests require live dump
   fixtures, 0 failed). Item 8 is now fully closed.
9. ~~**Merge `AppDomainAnalyzer` into `ModuleAnalyzer`**~~ (Deliverable 6) — DONE. Options,
   domain-result model, analyzer logic, finding generator, trend comparer, and section builder
   all merged into their `Module*` equivalents; `AppDomain*`-specific files deleted; CLI wiring,
   `SectionIdDomainMap`, `InsightEngine`, and catalog registrations updated to match. Verified:
   full-solution `dotnet build` (0 errors) and `dotnet test` (349 passed, 0 failed, 38 skipped —
   the skips require live dump fixtures).
10. ~~**Move `AsyncTaskAnalyzer`'s private task-index format fully behind `Indexing.Container`**~~
    (Deliverable 7) — **DONE (both halves).** 
    
    *AsyncTaskAnalyzer half:* Extracted `ReadTaskIndexFile` method and
    `TaskIndexMagic`/`TaskIndexVersion`/`RecordSize` constants into new `TaskIndexReader.cs`
    (`internal static class`, mirroring `RootIndexReader`'s pattern), leaving `AsyncTaskAnalyzer` to
    depend only on the typed reader interface. `LoadTaskEntries` now calls `TaskIndexReader.ReadTaskIndexFile`
    instead of its own private method. Added `TaskIndexReaderTests.cs` (5 tests covering round-trip
    write/read, max-tasks limiting, error cases). Verified: full-solution `dotnet build` (0 errors)
    and full test suite (354 passed, 38 skipped, 0 failed).
    
    *CollectionAnalyzer logging half:* Investigated `CollectionAnalyzer`'s `Microsoft.Extensions.Logging`
    dependency flagged by Deliverable 7 as an "outlier." Found it legitimate, not accidental: ~29 real
    call sites logging per-object scan failures (Dictionary/Queue/List/HashSet parsing errors), expected
    issues (missing optional fields, generation-lookup fallbacks), and user cancellation — real diagnostic
    value for malformed heap data in the platform's largest/most complex analyzer. The mechanism is
    platform-wide, not special: every analyzer can take `ILogger<T>? logger = null` constructor parameter,
    resolved automatically via `ActivatorUtilities` in `DefaultAnalyzerFactory`; CLI host and compatibility
    fallback both wire up logging. No code change needed — the pattern was already consistent. Formalized
    in [docs/architecture.md § 14 Observability](#14--observability) and [phase0-deliverable-7-dependency-graph-review.md](#infrastructure-leakage)
    as a sanctioned, not ad-hoc, practice.
11. ~~**Close the crash-triage gap**: confirm and, if needed, add minidump exception-stream parsing
    to `CrashAnalyzer`~~ (Deliverable 2, 3, 9) — **INVESTIGATION COMPLETE; DEFERRED TO FUTURE PHASE.**
    Gap is validated as real and closeable against WinDbg's `!analyze -v`. ClrMD 4.0 does not expose
    minidump exception stream APIs; direct DBGHELP P/Invoke required (same approach as WinDbg/debuggers).
    Recommendation: implement via Windows DBGHELP for Phase 2, estimated 2–4 days. See
    [p1-item-11-minidump-exception-stream-investigation.md](p1-item-11-minidump-exception-stream-investigation.md)
    for full research, options analysis, and implementation roadmap. Independent, no blocking dependency.
12. **Add runtime-configuration reporting** (GC mode, heap count, TieredCompilation) — cheap, high
    value, currently unowned by any analyzer (Deliverable 2). Independent, no blocking dependency.
13. ~~**Verify the actual depth of `QueryEngine`** (ad hoc object inspection) **and
    `Analysis.Trend.Comparers`** (snapshot diffing)~~ (Deliverable 9) — **VERIFICATION COMPLETE.**
    `QueryEngine` (`src/DumpDetective.Analysis/Query/QueryEngine.cs`) is shallow, confirmed against
    source: exactly two methods, `TopTypesBySize` (type statistics cache lookup) and `ObjectsOfType`
    (index stream filtered by resolved type name) — no arbitrary object/field inspection, no
    address→object lookup, no type-hierarchy walk. `docs/architecture.md` §5.5 lists "Reference
    paths" as an example `QueryEngine` capability; that capability does not exist on the class at all
    — it lives entirely in `ReferenceChainAnalyzer`/`RootPathFinder`, a separate layer — so the doc is
    inaccurate and has been left as a known gap for a future docs pass. More significant than the
    shallowness: `RuntimeAnalysisContext.Query` (`Pipeline/RuntimeAnalysisContext.cs:30`) constructs a
    `QueryEngine`, but no analyzer, CLI command, report, or test anywhere in `src/`/`tests/` reads
    `context.Query` or references `IQueryEngine` — it is wired into the context but has zero
    consumers, i.e. unreachable capability today, not just a shallow one.
    `Analysis.Trend.Comparers` is real and wired, not shallow in the way the same critique would apply
    to `QueryEngine`: `IAnalyzerTrendComparer.Compare` (35 registered comparers, one per analyzer) 
    produces `MetricDelta` records (`src/DumpDetective.Core/Models/AnalyzerTrendContracts.cs`) carrying
    delta, percent, growth-rate, and regression-severity classification between two pipeline runs —
    genuine % growth ranking. Its ceiling is architectural, not an implementation gap: it compares
    aggregate scalar metrics per analyzer (counts, byte totals) between two whole
    `AnalyzerDomainResult`s, with no per-object identity tracking across snapshots (no "this object
    survived run A → run B"), unlike VS Memory Usage/dotMemory's object-level diff. Object addresses
    aren't stable across two separate dump captures of the same process, so closing this gap would
    need a new matching mechanism (e.g. type+field-shape heuristics), not more comparer code — out of
    scope for this verification task. **Net result for [P3](#long-term-p3) item 4**: "deepen
    `QueryEngine`" is confirmed real work, and it has a prerequisite the P3 item didn't originally
    scope — `QueryEngine` needs a consumer (e.g. a CLI query subcommand) before depth matters, since
    today's implementation is unreachable regardless of how deep it is.

---

## Medium-term (P2)

1. **Dependency-injection scoped-service leak detection** (Deliverable 2) — highest-value missing
   capability, but real engineering effort (walking `IServiceProvider` internals). Sequence after
   the [P0](#immediate-priorities-p0-—-correctness-track) ranking engine and
   [P1](#near-term-p1) type-classification layer exist, so DI-leak signals feed the same shared
   ranking/evidence model rather than becoming a 7th independently-scored leak source.
2. **EF Core–aware diagnostics and cache-health analysis** (`IMemoryCache`/static caches)
   (Deliverable 2, 5) — depends on the P1 shared type-classification layer (item 6) and sampling
   framework (item 7) landing first; both naturally reuse that shape, so are cheaper once those
   land.
3. **Native/unmanaged memory and COM interop (RCW/CCW) tracking** (Deliverable 2, 9) — independent
   net-new capability, no blocking dependency.
4. **Reporting-helper consolidation**: collapse the resource-sampler quartet's near-identical
   `SectionBuilder`s — depends on the P1 typed-resource sampler (item 7) landing first, since the
   sampler quartet's sections can't be collapsed until the sampler itself is unified. Separately,
   confirm whether per-analyzer "top types" sections are redundant against the global
   `TypeSystemSectionBuilder` (Deliverable 4 §6, Deliverable 9) — independent verification, no
   blocker.
5. **Resolve `FinalizableObjectAnalyzer`'s scope ambiguity** — confirm whether "has finalizer,
   undisposed" and "on the finalization queue" are being conflated (Deliverable 3, 6). Independent
   clarification task.
6. **Simplify the 4x analyzer-registration fan-out** (sensible defaults for
   generator/comparer/section-builder types) before the analyzer count grows materially past 36
   (Deliverable 7, 9) — independent of the three Deliverable 6 merges already executed in
   P0/P1 (those pay the current 4x cost once each regardless); this item is about reducing the cost
   of *future* analyzer additions, not making the already-completed merges cheaper.

---

## Long-term (P3)

1. ASP.NET-specific diagnostics, `System.Threading.Channels` support, reflection-cache growth
   detection, resurrection detection, native (non-managed) thread enumeration, general
   object-ownership / non-string duplicate-object detection — all real Deliverable 2 gaps, but
   lowest urgency and/or novel engineering (Deliverable 2, 9). No blocking dependencies.
2. Pinned-object/POH-specific reporting (Deliverable 2). No blocking dependencies.
3. **A future interactive visualization layer for retention-path evidence** — explicitly deferred,
   not rejected, by Deliverable 9. This has a real sequencing dependency, not just low urgency:
   worth building only once the P0 Correctness track (evidence/ranking/confidence quality) is
   solid, so it complements rather than competes with that work.
4. **Deepen `QueryEngine` into a full ad hoc exploration capability** — contingent on the P1
   verification item (`QueryEngine`/`Trend.Comparers` depth) finding today's version shallow
   relative to WinDbg's manual exploration power. If that verification finds it already adequate,
   this item drops out.

---

## Success Criteria

Answering the review protocol's seven closing questions directly:

**1. Does every analyzer have a clearly defined owner and responsibility?**
Not today, but close after the fixes above. Deliverable 3 found clean, unambiguous ownership for
roughly two-thirds of the 36 analyzers. The rest fall into a small number of well-defined overlap
clusters (module/domain, leak/retention scoring, the handle trio, the thread quartet, the
resource-sampler quartet) rather than being scattered ambiguity — meaning the fix is scoped and
tractable, not a sign of pervasive architectural confusion.

**2. Are any analyzers redundant?**
No analyzer is wholly redundant — Deliverable 6 found zero removal candidates after deliberately
checking rather than assuming. Three pairs are duplicative enough to merge, and one
(`LeakCandidateAnalyzer`) needs a strategy replacement, but every one of the 36 maps to a real,
distinct diagnostic capability.

**3. Which analyzers should merge or split?**
Merge: `AppDomainAnalyzer` into `ModuleAnalyzer`, `RetentionAnalyzer` into `DominatorAnalyzer`,
`DependentHandleAnalyzer` into `GCHandleAnalyzer` (36 → 33 analyzers). No mandatory splits;
`CollectionAnalyzer`'s size is a scope-creep flag addressed by extracting shared infrastructure
(reflection cache) rather than splitting the analyzer itself, with a literal split left as a
conditional future option only if its scope keeps growing (Deliverable 6).

**4. Which platform capabilities are missing?**
Ranked by validated priority (Deliverable 2, filtered through Deliverable 9's "don't chase parity
blindly" test): DI-container leak detection, crash minidump-stream triage, runtime-configuration
reporting, EF Core diagnostics, cache health, native/COM interop, ASL-specific leak detection
(distinct from the legacy AppDomain framing being retired), POH reporting, ASP.NET diagnostics,
and lowest-priority: `System.Threading.Channels`, reflection-growth detection, resurrection
detection, native thread enumeration, general object-ownership/duplicate detection. Explicitly
excluded as non-goals: allocation call-stack hotspots and live ETW timelines (architecturally
impossible from a static dump) and a full interactive GUI (strategically premature).

**5. Which expensive operations should become shared infrastructure?**
In priority order (Deliverable 5, 8): the object-index scan itself (dispatcher — addresses 9 of
35 analyzers, not all of them), per-type statistics reduction, root/static enumeration, the handle-
table walk, the thread-stack walk, type classification, reflection field-layout caching, and the
typed-resource sampler.

**6. What architectural changes would most improve correctness, scalability, and maintainability?**
Scalability: the single-pass index dispatcher — high-leverage for the 9 index-scanning analyzers
it covers, though 5 analyzers (`EnumerateObjects()`-based) sit outside its reach entirely.
Correctness: the inter-analyzer result bus (done) feeding a shared evidence/ranking/confidence
engine, which turns 6 independently-scored leak signals into one credible answer — this is worth
weighing as co-equal with, not automatically subordinate to, the dispatcher, given the dispatcher's
verified blast radius is smaller than originally estimated. Maintainability: the sole confirmed
Deliverable 7 dependency-direction violation (`HeapTopologyAnalyzer` → `Pipeline`) is fixed; what
remains is holding that direction (no analyzer depends on Pipeline or Reporting) as new analyzers
are added, and reducing the 4x registration fan-out before the analyzer count grows further.

**7. If DumpDetective were redesigned today, what would its analyzer architecture look like?**
Roughly 33 analyzers (post-merge); of those, the 9 verified index-scanning analyzers would each
expose a per-object visitor callback consumed by one shared dispatcher instead of independently
streaming the index — the 5 `EnumerateObjects()`-based analyzers would need an analogous but
distinct live-heap fan-out mechanism, not this same dispatcher. A per-type statistics artifact and
per-object generation/segment classification computed once per run and handed to every analyzer,
rather than re-derived. A single canonical root/retention graph service (built on the existing
`Traversal` primitive) that every leak-adjacent analyzer depends on instead of implementing its own
walk. Leak-adjacent analyzers emit structured evidence into one evidence/ranking/confidence engine
that is the platform's sole scoring authority, rather than each computing and reporting its own
severity. Analyzer registration carries sensible defaults so adding a new analyzer doesn't
necessarily require four coordinated types. And a strictly enforced dependency direction — Core →
shared infra → analyzers → trend comparers → reporting → orchestration — with no exceptions of the
kind `HeapTopologyAnalyzer` currently represents. Notably, this is an evolution of the current
design, not a rewrite: every piece of it already exists in some form in today's codebase
(`Traversal`, `HeapAnalysisCache`, `TypeIndexBuilder`, `InsightEngine.FindResult<T>` as the
post-hoc-bus precedent, `ConfidenceSectionBuilder`) — the work is consolidation and enforcement,
not reinvention. `Order` itself is not part of this list — it is execution/report sequencing only,
not a data channel between analyzers.
