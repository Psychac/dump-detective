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
- **Heap index single-pass dispatcher (Deliverable 5 item 1): designed, not started.** Full design
  in [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md) — an
  opt-in `IHeapIndexScanParticipant` interface plus a shared dispatcher, proof-of-concept scoped to
  `DbConnectionAnalyzer`. Blocked on a real, unbudgeted design problem: discrepancy/unit tests
  (e.g. `DbConnectionAnalyzerDiscrepancyTests.cs`) call `analyzer.AnalyzeAsync()` directly,
  bypassing `AnalysisPipeline` — the mechanism that would prime dispatcher-participant state before
  `AnalyzeAsync` runs. See [Near-term (P1)](#near-term-p1) for the resolution options.

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
originally estimated and carries an additional, currently-unresolved implementation blocker (the
direct-`AnalyzeAsync`-call test bypass — see [P1](#near-term-p1)). Within each track, items below
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
   merge, `AppDomainAnalyzer` into `ModuleAnalyzer`, is independent of this chain — see
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
5. **Ranking / leak-scoring engine — replace `LeakCandidateAnalyzer`'s scanning strategy with an
   aggregation strategy** (Deliverable 5 item 8; Deliverable 6's Replace Recommendation) — depends
   on item 4 above for its input shape (the Evidence model) and on the bus (done, see
   [Current State](#current-state)) for reading other analyzers' results via
   `AnalyzerRunResultsExtensions.GetResult<T>`. This is the item Deliverable 6 verdicts as
   **Replaced**: `LeakCandidateAnalyzer`'s job (rank/score leak candidates) is correct and
   necessary; its strategy (independently re-scanning the index for its own signals) is not.
6. **Confidence scoring wired to the existing `ConfidenceSectionBuilder`** (Deliverable 5 item 9) —
   design together with item 5, not sequenced strictly after it: a ranking engine without a shared
   confidence formula just moves the inconsistency rather than removing it. Whether
   `ConfidenceSectionBuilder` already consumes a structured per-finding confidence value or
   re-derives it per section needs to be confirmed directly against its implementation.

---

## Near-term (P1) — Performance track and independent infra

**Performance track (dependency order)**

1. **Resolve the direct-`AnalyzeAsync`-call test-bypass design problem** — blocks item 2 below.
   `DbConnectionAnalyzerDiscrepancyTests.cs` (and likely other call sites) invoke
   `analyzer.AnalyzeAsync()` directly, bypassing `AnalysisPipeline`, the mechanism that would prime
   dispatcher-participant state first. Three options are on the table (see
   [phase0-heap-index-scan-dispatcher-plan.md](phase0-heap-index-scan-dispatcher-plan.md)'s "Open
   design problem" section): (a) a per-call "primed" flag with fallback to today's self-contained
   scan when not primed, (b) migrate direct-call tests to drive analyzers through a
   pipeline/dispatcher invocation, or (c) leave `DbConnectionAnalyzer` unmigrated and prove the
   dispatcher with a synthetic participant only. Needs a decision before any dispatcher code is
   written for a real analyzer.
2. **Heap index single-pass dispatcher** (Deliverable 5 item 1, Deliverable 8 §1) — depends on
   item 1 above being resolved, and should also wait for either a profiling run confirming the ~9x
   scan cost is significant on a representative 10GB+ dump (Deliverable 8's own open item), or a
   decision to proceed without that confirmation. Addresses 9 of 35 analyzers (verified); proof of
   concept scoped to `DbConnectionAnalyzer` only.
3. **Per-type statistics engine** (Deliverable 5 item 2) — depends on item 2 above (the dispatcher)
   existing; the per-type reduction is designed to run as an accumulator inside the same single
   pass, so it is cheap once the dispatcher exists and removes a correctness risk (disagreeing
   "total bytes" numbers across report sections).
4. **Object metadata classification** (generation/segment bucket, Deliverable 5 item 5) — sequenced
   after item 2 above (the dispatcher); most of its value is only realized once objects are
   classified once per object inside the shared single pass and handed to every visitor.
5. **Confirm container/satellite index build-once-vs-per-invocation behavior** (Deliverable 8
   §2/consolidation #3) — a verification task, independent of the dispatcher chain above. If
   `Indexing.Container`/`Indexing.Satellite` indexes are lazily rebuilt per analyzer invocation
   rather than cached across a session, this is a second, distinct instance of "repeated index
   construction" that would need its own fix, separate from the object-index dispatcher.

**Independent infra (no blocking dependencies — can start any time)**

6. **Shared type-classification layer** for the 8 analyzers currently rolling their own type-name
   pattern matching (Deliverable 5 item 4) — cheap, and directly reduces the cost of the Deliverable
   2 capability gaps in [P2](#medium-term-p2) that need the same classification (EF Core, DI,
   Channels).
7. **Shared typed-resource sampler** for the `DbConnectionAnalyzer`/`WcfChannelAnalyzer`/
   `HttpObjectAnalyzer`/`TimerLeakAnalyzer` quartet (Deliverable 5 item 7) — self-contained
   extraction, four existing call sites to migrate.
8. **Shared contracts (compiler-checked interfaces, not conventions) for the resource-sampler and
   thread-domain quartets** (Deliverable 7) — the resource-sampler contract naturally pairs with
   item 7 above (same quartet); the thread-domain contract is independent and is what lets
   `ThreadAnalyzer` become the canonical stack-walk provider that `HangAnalyzer`,
   `ThreadStackClusterAnalyzer`, and `LockGraphAnalyzer` consume instead of each independently
   walking stacks and re-deriving wait state (Deliverable 3, 4, 6).
9. **Merge `AppDomainAnalyzer` into `ModuleAnalyzer`** (Deliverable 6) — independent of the
   Retention/DependentHandle merges in [P0](#immediate-priorities-p0-—-correctness-track); no
   shared blocker.
10. **Move `AsyncTaskAnalyzer`'s private task-index format fully behind `Indexing.Container`**;
    separately, **resolve `CollectionAnalyzer`'s logging dependency** one way or the other
    (Deliverable 7) — two independent fixes with no dependency on each other or on anything above.
11. **Close the crash-triage gap**: confirm and, if needed, add minidump exception-stream parsing
    to `CrashAnalyzer` (Deliverable 2, 3, 9) — validated as a real, closeable gap against WinDbg's
    `!analyze -v`, not a case of chasing parity blindly. Independent, no blocking dependency.
12. **Add runtime-configuration reporting** (GC mode, heap count, TieredCompilation) — cheap, high
    value, currently unowned by any analyzer (Deliverable 2). Independent, no blocking dependency.
13. **Verify the actual depth of `QueryEngine`** (ad hoc object inspection) **and
    `Analysis.Trend.Comparers`** (snapshot diffing) (Deliverable 9) — a verification task, independent
    of everything above. Its result determines whether the [P3](#long-term-p3) "deepen `QueryEngine`"
    item is real work or already done.

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
