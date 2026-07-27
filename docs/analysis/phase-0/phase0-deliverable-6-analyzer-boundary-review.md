# Phase 0 — Deliverable 6: Analyzer Boundary Review

> Scope: **Deliverable 6 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> For every analyzer: Kept / Merged / Split / Replaced / Removed, justified. Builds on
> [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md),
> [Deliverable 3](phase0-deliverable-3-responsibility-matrix.md), and
> [Deliverable 5](phase0-deliverable-5-shared-infrastructure.md).
>
> **Verdict discipline**: the verdict column reflects the analyzer's *boundary/existence* only.
> Internal refactors already recommended in Deliverable 4/5 (e.g. "consume the shared sampler",
> "stop re-walking statics") are **not** by themselves grounds for Replaced — they're
> implementation debt against an otherwise-correct boundary, tracked as "Required changes" instead.
> Replaced is reserved for analyzers whose fundamental *strategy*, not just internals, is wrong.

## Merge Recommendations

### `ModuleAnalyzer` + `AppDomainAnalyzer` → merge into `ModuleAnalyzer`

**Status: done** — see [phase0-deliverable-10-platform-roadmap.md P1 item 9](phase0-deliverable-10-platform-roadmap.md#near-term-p1).
Options, domain-result model, analyzer logic, finding generator, trend comparer, and section
builder were merged into their `Module*` equivalents; `AppDomain*`-specific files deleted; CLI
wiring, `SectionIdDomainMap`, `InsightEngine`, and catalog registrations updated to match.
Verified against source: `AppDomainAnalyzer` no longer exists in the codebase.

Both compute per-module/type/object statistics with no defensible boundary between them
(Deliverable 1, Deliverable 3 overlap #1). Beyond the duplication: in modern .NET (Core/5+),
`AppDomain` is largely vestigial — a process has exactly one (default) AppDomain, and the concept
that actually matters today is `AssemblyLoadContext`. A dedicated "AppDomain analyzer" is
analyzing a mostly-retired abstraction. Recommendation: merge the useful per-module ownership
stats into `ModuleAnalyzer`, drop the AppDomain framing, and treat real
`AssemblyLoadContext`-aware leak detection (unloadable ALC not being collected — a genuine, common
.NET Core leak pattern) as the *actual* missing capability from Deliverable 2, to be added fresh
rather than mislabeled onto the old AppDomain analyzer.

### `RetentionAnalyzer` (file `MemoryLeakAnalyzer.cs`) → merge into `DominatorAnalyzer`

**Status: done** — see [phase0-deliverable-10-platform-roadmap.md P0 item 3](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0).

"Highly-referenced object" (incoming-ref fan-in) and "retained size" (dominator tree) are two
angles on the same question: how much does this object anchor in memory. Deliverable 3 flagged
these as computing overlapping heuristics independently; Deliverable 5 named `DominatorAnalyzer`
as the canonical retained-size provider. Recommendation: fold high-fan-in detection into
`DominatorAnalyzer` as one of its signals, and resolve the file/class naming mismatch as part of
the merge rather than as a separate fix — there's no reason to preserve a differently-named
standalone analyzer for a signal that belongs inside the canonical retention analyzer.

### `DependentHandleAnalyzer` → merge into `GCHandleAnalyzer`

**Status: done** — see [phase0-deliverable-10-platform-roadmap.md P0 item 3](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0).

A `DependentHandle` is literally one `HandleKind` in ClrMD's handle enumeration
(`HandleKind.Dependent`), not a conceptually separate data source. `DependentHandleAnalyzer` reads
no satellite/container index beyond the standard cache (Deliverable 1) — there's no technical
reason it needs to be a separate full handle-table walk. Recommendation: fold it into
`GCHandleAnalyzer` as a per-kind sub-report (dependent-handle target/dependent pairs), eliminating
a redundant handle-table enumeration.

`WeakReferenceAnalyzer` is **not** included in this merge — see its row below; it reads a
distinct satellite index for target-liveness resolution that gives it a real technical reason to
stay separate, once de-duplicated against `GCHandleAnalyzer`'s raw counting.

## Split Recommendation (conditional)

### `CollectionAnalyzer` — no structural split recommended now; revisit if scope keeps growing

At 1702 lines/107 symbols it's the largest analyzer and the only one with a
`Microsoft.Extensions.Logging` dependency (Deliverable 1 scope-creep flag). However, "BCL
collection introspection" is a single coherent capability domain (Deliverable 2 lists Dictionaries/
Lists/Concurrent/Immutable under one Collections category) — splitting by collection family (e.g.
generic vs. concurrent vs. immutable) would trade one large analyzer for three thin ones with no
clear boundary of their own, which is the same mistake `ModuleAnalyzer`/`AppDomainAnalyzer` made
in the other direction (that merge has since shipped — see above). **Recommendation: Keep as one
analyzer**, but treat the size as a signal to extract its reflection-based field-layout cache into
shared infrastructure (Deliverable 5 item 4/5 — still outstanding). The logging-dependency flag is
**resolved, not by removal**: investigated per
[P1 item 10](phase0-deliverable-10-platform-roadmap.md#near-term-p1) and found legitimate — ~29
real call sites logging per-object scan failures on malformed heap data in the platform's
largest/most complex analyzer. The optional `ILogger<T>? logger = null` constructor pattern is now
formalized platform-wide (see
[docs/architecture.md § 14 Observability](../../architecture.md#14--observability) and
[CLAUDE.md](../../../CLAUDE.md)), so `CollectionAnalyzer` is no longer an outlier — it's the first
analyzer to use a sanctioned pattern, not scope creep. Revisit splitting only if a future addition
(e.g. deep `System.Threading.Channels` support) meaningfully changes its shape.

## Replace Recommendation

### `LeakCandidateAnalyzer` — replace scanning strategy with aggregation strategy

**Status: done, but narrower than originally scoped** — see
[phase0-deliverable-10-platform-roadmap.md P0 item 5](phase0-deliverable-10-platform-roadmap.md#immediate-priorities-p0).
The inter-analyzer result bus (Deliverable 5 item 11) landed as
`AnalyzerRunResultsExtensions.GetResult<T>` plus the new `IDeferredAnalyzer` marker interface,
which `AnalysisPipeline` runs in a second pass after every non-deferred analyzer completes.
`LeakCandidateAnalyzer` now implements `IDeferredAnalyzer` and no longer independently walks
`runtime.EnumerateHandles()` — it reads the already-completed `GCHandleDomainResult` off
`AnalysisContext.CompletedRunResults` for that signal. **What did not change**: it still reads
`TypeAggregateIndexEntry`/`TypeShapeCache` directly off the heap index for its other signals
(Gen2%, finalizable, static-rooted, container, reference-field-ratio) rather than consuming
`DominatorAnalyzer`/`StaticRootLeakDetector`/`EventLeakAnalyzer`/`TimerLeakAnalyzer`'s domain
results as this section originally envisioned — it is a partial aggregator (bus-consumer for one
signal), not the pure aggregator the original recommendation described. That gap is not tracked as
an open item anywhere in the roadmap and should be if the platform still wants one shared
confidence/ranking authority across all six leak-adjacent analyzers.

Per Deliverable 3's central finding: this analyzer's *job* — rank/score leak candidates from
multiple signals — is correct and necessary (Deliverable 5 item 8, P0 priority). Its original
*strategy*, independently re-scanning the index for its own signals rather than consuming other
analyzers' output, was architecturally wrong given the platform's stated goal of a single shared
confidence/ranking authority. **Verdict: Replaced** (done for the handle signal; the remaining
signals are tracked above as follow-up, not as a reason to revert the verdict).

## No-Removal Analyzers, With Justification

Every remaining analyzer maps to a real, distinct diagnostic capability once the Deliverable 4/5
refactors are applied — duplication was found in *how* they scan and compute, not in *whether*
their capability is worth having. No analyzer is recommended for outright removal. This was
checked deliberately rather than assumed (per the review's "do not assume the current architecture
is optimal" mandate) — candidates considered and rejected for removal:

- `JitAnalyzer` — borderline relevance to a "memory" dump analyzer, but cheap (no index
  dependency) and answers a real question (native/JIT code-heap size vs. managed heap size) that
  WinDbg/SOS users expect. **Kept.**
- `DependentHandleAnalyzer` / weak-reference coverage — dependent handles back
  `ConditionalWeakTable`, a real and common leak vector. **Kept** (merged into `GCHandleAnalyzer`,
  not removed).
- `FinalizableObjectAnalyzer` — scope-ambiguity (Deliverable 3) is a clarification issue, not a
  value issue. **Kept**, with an action item to confirm whether it should also cover the literal
  finalization queue.

## Full Verdict Table

| Analyzer | Verdict | Justification |
|---|---|---|
| `MemoryAnalyzer` | **Keep** | Clean single responsibility; correctly runs first |
| `GCGenerationAnalyzer` | **Keep** | Clean boundary |
| `AllocationPatternAnalyzer` | **Keep** | Clean, provided it stays a coarse classifier (D3) |
| `ObjectShapeAnalyzer` | **Keep** | Clean boundary |
| `GCRootAnalyzer` | **Keep** | Clean; complements `ReferenceChainAnalyzer`'s per-object job |
| `HeapTopologyAnalyzer` | **Keep** | Distinct from `SegmentReservationAnalyzer` (layout vs. waste); `Analysis.Pipeline` dependency **fixed** — confirmed dead import, removed (P0 item 1) |
| `ModuleAnalyzer` | **Merged** (absorbed `AppDomainAnalyzer`) | See Merge section — done |
| `CrashAnalyzer` | **Keep** | Minidump exception-stream gap **investigated and confirmed real**; ClrMD 4.0 exposes no API for it, direct DBGHELP P/Invoke required — **deferred to a future phase**, not a boundary problem (P1 item 11) |
| `HangAnalyzer` | **Keep** | Distinct capability (threadpool health scoring); now consumes `ThreadAnalyzer`'s stack walk via the shared `IThreadStackScanParticipant`/`ThreadStackScanDispatcher` contract instead of re-deriving it — **done** (P1 item 8) |
| `AsyncTaskAnalyzer` | **Keep** | Distinct capability; private on-disk task-index format **moved** behind `TaskIndexReader`/`Indexing` layer — **done** (P1 item 10) |
| `RetentionAnalyzer` (`MemoryLeakAnalyzer.cs`) | **Merged** into `DominatorAnalyzer` | See Merge section |
| `LeakCandidateAnalyzer` | **Replaced** (strategy) | Done for the handle signal via `IDeferredAnalyzer` + result bus; other signals still index-derived, not bus-consumed — see Replace section |
| `DominatorAnalyzer` | **Keep**, becomes canonical retained-size provider | Absorbs `RetentionAnalyzer`'s signal |
| `StringAnalyzer` | **Keep** | Clean, appropriately-scoped despite size |
| `CollectionAnalyzer` | **Keep** (no split now) | See Split section |
| `StaticRootLeakDetector` | **Keep** | Distinct capability; roots now read through the shared `RootSetCache` (P0 item 2) alongside `GCRootAnalyzer`/`EventLeakAnalyzer` — **done** |
| `ReferenceChainAnalyzer` | **Keep**, elevate to canonical root-path provider | On-demand evidence engine other analyzers should depend on; its bidirectional shortest-root-path search was intentionally left as its own thing, and `SampleRootPathFinder` was extracted from its cheap Fast-mode path search for the evidence builder (P0 item 4) |
| `GCHandleAnalyzer` | **Keep**, absorbs `DependentHandleAnalyzer` | See Merge section |
| `DependentHandleAnalyzer` | **Merged** into `GCHandleAnalyzer` | See Merge section |
| `LohFragmentationAnalyzer` | **Keep** | Clean structural boundary vs. `AllocationPatternAnalyzer`/`SegmentReservationAnalyzer` |
| `ThreadStackClusterAnalyzer` | **Keep** | Distinct analytical technique (signature clustering vs. per-thread state); now consumes the shared stack walk via `IThreadStackScanParticipant` instead of re-walking — **done** (P1 item 8) |
| `ThreadAnalyzer` | **Keep**, elevate to canonical thread/stack-walk provider | `HangAnalyzer`/`ThreadStackClusterAnalyzer`/`LockGraphAnalyzer` now depend on it via `ThreadStackScanDispatcher` — **done** (P1 item 8) |
| `LockGraphAnalyzer` | **Keep** | Distinct capability (lock ownership graph, deadlock candidates); now consumes `ThreadAnalyzer`'s wait state via the same dispatcher — **done** (P1 item 8) |
| `EventLeakAnalyzer` | **Keep** | Distinct, valuable capability (event/delegate leak pattern); reads roots through the shared `RootSetCache` alongside `StaticRootLeakDetector` — **done** (P0 item 2); reflection field-layout cache sharing with `CollectionAnalyzer` still outstanding (D5 item 4/5) |
| `FinalizableObjectAnalyzer` | **Keep** | Real capability; finalizer-queue vs. has-finalizer-undisposed scope clarification still open (P2 item 5) |
| `AsyncStateMachineAnalyzer` | **Keep** | Distinct from `AsyncTaskAnalyzer` (compiler-generated instances vs. Task status); should share classification data where continuations reference state machines (D3) |
| `ArrayAnalyzer` | **Keep** | Clean; should hand off LOH-fragmentation detail to `LohFragmentationAnalyzer` rather than compute it (D3) |
| `AppDomainAnalyzer` | **Merged** into `ModuleAnalyzer` | See Merge section — **done**; verified no longer present in the codebase |
| `SegmentReservationAnalyzer` | **Keep** | Reference example of correct isolation, no changes needed |
| `WeakReferenceAnalyzer` | **Keep** | Real technical justification for staying separate (satellite index for target-liveness resolution); must de-duplicate raw handle counting with `GCHandleAnalyzer` (D3) |
| `BoxingAnalyzer` | **Keep** | Clean boundary |
| `JitAnalyzer` | **Keep** | Reference example of correct isolation; see No-Removal justification |
| `DbConnectionAnalyzer` | **Keep** | Distinct resource type; migrated to the shared typed-resource sampler and its compiler-checked `ITypedResourceCandidateSource`/`ITypedResourceInstanceSampler` contract — **done** (P1 items 7-8) |
| `WcfChannelAnalyzer` | **Keep** | Distinct resource type; same sampler migration — **done** |
| `HttpObjectAnalyzer` | **Keep** | Distinct resource type; same sampler migration (candidate-source half only, no runtime state to sample) — **done** |
| `TimerLeakAnalyzer` | **Keep** | Distinct resource type; same sampler migration — **done**; still computes its own leak severity — `LeakCandidateAnalyzer`'s ranking engine only consumes `GCHandleDomainResult` today, not `TimerLeakDomainResult`, so this action item remains open |

## Net Effect

- **36 analyzers → 33 — done, verified against source.** All three merges have shipped
  (`AppDomainAnalyzer` into `ModuleAnalyzer`, `RetentionAnalyzer` into `DominatorAnalyzer`,
  `DependentHandleAnalyzer` into `GCHandleAnalyzer`); a direct count of `: IAnalyzer` and
  `: IDeferredAnalyzer` classes under `src/DumpDetective.Analysis/Analyzers/` confirms 33.
- **1 analyzer** (`LeakCandidateAnalyzer`) has its strategy replacement **done** — the inter-analyzer
  result bus (Deliverable 5 item 11) shipped and `LeakCandidateAnalyzer` consumes it for the
  GC-handle signal via `IDeferredAnalyzer`. It is a partial, not pure, aggregator: its other signals
  (Gen2%, finalizable, static-rooted, container) are still read directly off the heap index rather
  than off `DominatorAnalyzer`/`StaticRootLeakDetector`/`EventLeakAnalyzer`/`TimerLeakAnalyzer`'s
  domain results — see Replace section.
- **0 analyzers** recommended for removal — the architecture's problem is duplication and
  coupling, not dead capability. Still true; no removal candidates surfaced since.
- Of the remaining "Keep" verdicts' internal-refactor action items: most have since **landed**
  (thread-domain quartet contract, resource-sampler quartet contract, `RootSetCache`, `HeapTopologyAnalyzer`'s
  dead `Pipeline` import, `AsyncTaskAnalyzer`'s index format, `CollectionAnalyzer`'s logging
  dependency resolved as a sanctioned pattern). Open ones: `CollectionAnalyzer`'s reflection
  field-layout cache extraction, `WeakReferenceAnalyzer`/`GCHandleAnalyzer` raw-count
  de-duplication, `AsyncStateMachineAnalyzer`/`AsyncTaskAnalyzer` classification sharing,
  `ArrayAnalyzer`/`LohFragmentationAnalyzer` LOH-detail handoff, `FinalizableObjectAnalyzer`'s
  scope clarification, and `TimerLeakAnalyzer` joining the ranking engine. Boundary correctness and
  implementation cleanliness are still tracked separately by design (see verdict discipline note
  above) — see
  [phase0-deliverable-10-platform-roadmap.md](phase0-deliverable-10-platform-roadmap.md) for the
  authoritative, continuously-updated status of each.
