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
in the other direction. **Recommendation: Keep as one analyzer**, but treat the size as a signal
to extract its reflection-based field-layout cache into shared infrastructure (Deliverable 5 item
4/5) and remove the logging dependency. Revisit splitting only if a future addition (e.g. deep
`System.Threading.Channels` support) meaningfully changes its shape.

## Replace Recommendation

### `LeakCandidateAnalyzer` — replace scanning strategy with aggregation strategy

Per Deliverable 3's central finding: this analyzer's *job* — rank/score leak candidates from
multiple signals — is correct and necessary (Deliverable 5 item 8, P0 priority). But its current
*strategy*, independently re-scanning the index for its own signals rather than consuming
`RetentionAnalyzer`(→`DominatorAnalyzer`)/`StaticRootLeakDetector`/`EventLeakAnalyzer`/
`TimerLeakAnalyzer`'s output, is architecturally wrong given the platform's stated goal of a single
shared confidence/ranking authority. This isn't a tunable internal detail — it requires the
Deliverable 5 item 11 (inter-analyzer result bus) to exist and `LeakCandidateAnalyzer` to be
rebuilt against it as a pure aggregator. **Verdict: Replaced**, contingent on item 11 landing
first.

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
| `HeapTopologyAnalyzer` | **Keep** | Distinct from `SegmentReservationAnalyzer` (layout vs. waste); mandatory fix — remove its `Analysis.Pipeline` dependency (D3 hidden coupling) |
| `ModuleAnalyzer` | **Merge target** (absorbs `AppDomainAnalyzer`) | See Merge section |
| `CrashAnalyzer` | **Keep** | Verify minidump exception-stream coverage (D3) as an action item, not a boundary problem |
| `HangAnalyzer` | **Keep** | Distinct capability (threadpool health scoring); must consume `ThreadAnalyzer`'s wait-state instead of re-deriving it (D3/D5) |
| `AsyncTaskAnalyzer` | **Keep** | Distinct capability; must replace its private on-disk task-index format with the shared `Indexing` layer (D3/D4 hidden coupling) |
| `RetentionAnalyzer` (`MemoryLeakAnalyzer.cs`) | **Merged** into `DominatorAnalyzer` | See Merge section |
| `LeakCandidateAnalyzer` | **Replaced** (strategy) | See Replace section |
| `DominatorAnalyzer` | **Keep**, becomes canonical retained-size provider | Absorbs `RetentionAnalyzer`'s signal |
| `StringAnalyzer` | **Keep** | Clean, appropriately-scoped despite size |
| `CollectionAnalyzer` | **Keep** (no split now) | See Split section |
| `StaticRootLeakDetector` | **Keep** | Distinct capability; must expose its static-field sweep as shared infra for `EventLeakAnalyzer` to consume (D3/D5) |
| `ReferenceChainAnalyzer` | **Keep**, elevate to canonical root-path provider | On-demand evidence engine other analyzers should depend on (D5 evidence builder) |
| `GCHandleAnalyzer` | **Keep**, absorbs `DependentHandleAnalyzer` | See Merge section |
| `DependentHandleAnalyzer` | **Merged** into `GCHandleAnalyzer` | See Merge section |
| `LohFragmentationAnalyzer` | **Keep** | Clean structural boundary vs. `AllocationPatternAnalyzer`/`SegmentReservationAnalyzer` |
| `ThreadStackClusterAnalyzer` | **Keep** | Distinct analytical technique (signature clustering vs. per-thread state); must consume `ThreadAnalyzer`'s stack walk instead of re-walking (D3/D4) |
| `ThreadAnalyzer` | **Keep**, elevate to canonical thread/stack-walk provider | `HangAnalyzer`/`ThreadStackClusterAnalyzer`/`LockGraphAnalyzer` should depend on it |
| `LockGraphAnalyzer` | **Keep** | Distinct capability (lock ownership graph, deadlock candidates); must consume `ThreadAnalyzer`'s wait state (D3) |
| `EventLeakAnalyzer` | **Keep** | Distinct, valuable capability (event/delegate leak pattern); must consume `StaticRootLeakDetector`'s sweep instead of duplicating it, and share reflection field-layout cache with `CollectionAnalyzer` (D5) |
| `FinalizableObjectAnalyzer` | **Keep** | Real capability; action item to clarify finalizer-queue vs. has-finalizer-undisposed scope (D3) — conditionally revisit split if the two questions turn out to be conflated in implementation |
| `AsyncStateMachineAnalyzer` | **Keep** | Distinct from `AsyncTaskAnalyzer` (compiler-generated instances vs. Task status); should share classification data where continuations reference state machines (D3) |
| `ArrayAnalyzer` | **Keep** | Clean; should hand off LOH-fragmentation detail to `LohFragmentationAnalyzer` rather than compute it (D3) |
| `AppDomainAnalyzer` | **Merged** into `ModuleAnalyzer` | See Merge section |
| `SegmentReservationAnalyzer` | **Keep** | Reference example of correct isolation, no changes needed |
| `WeakReferenceAnalyzer` | **Keep** | Real technical justification for staying separate (satellite index for target-liveness resolution); must de-duplicate raw handle counting with `GCHandleAnalyzer` (D3) |
| `BoxingAnalyzer` | **Keep** | Clean boundary |
| `JitAnalyzer` | **Keep** | Reference example of correct isolation; see No-Removal justification |
| `DbConnectionAnalyzer` | **Keep** | Distinct resource type; must migrate to the shared typed-resource sampler (D5 item 7) |
| `WcfChannelAnalyzer` | **Keep** | Distinct resource type; same sampler migration |
| `HttpObjectAnalyzer` | **Keep** | Distinct resource type; same sampler migration |
| `TimerLeakAnalyzer` | **Keep** | Distinct resource type; same sampler migration; must stop computing independent leak severity once the ranking engine (D5 item 8) exists |

## Net Effect

- **36 analyzers → 33** after the three merges (`AppDomainAnalyzer` into `ModuleAnalyzer`,
  `RetentionAnalyzer` into `DominatorAnalyzer`, `DependentHandleAnalyzer` into `GCHandleAnalyzer`).
- **1 analyzer** (`LeakCandidateAnalyzer`) requires a strategy replacement contingent on
  Deliverable 5's inter-analyzer result bus.
- **0 analyzers** recommended for removal — the architecture's problem is duplication and
  coupling, not dead capability.
- The remaining ~30 "Keep" verdicts each still carry a required internal-refactor action item from
  Deliverable 4/5 — boundary correctness and implementation cleanliness are tracked separately by
  design (see verdict discipline note above).
