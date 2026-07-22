# Phase 0 — Deliverable 3: Responsibility Matrix

> Scope: **Deliverable 3 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Builds on [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md) (catalog) and
> [Deliverable 2](phase0-deliverable-2-capability-matrix.md) (capability gaps/fragmentation) to
> answer, per analyzer: what problem it solves, what it must never solve, and what diagnostics or
> statistics belong to a different owner. Findings are then rolled up into overlap, gaps, and
> hidden-coupling sections as required by the doc.

## Per-Analyzer Responsibility

| Analyzer | Problem It Solves | Problem It Must Never Solve | Belongs Elsewhere | Notes |
|---|---|---|---|---|
| `MemoryAnalyzer` | Executive heap summary (bytes, count, top types) | Per-type deep-dive, leak scoring | — | Clean boundary |
| `GCGenerationAnalyzer` | Per-generation object/byte distribution | Allocation-behavior classification | — | Clean boundary |
| `AllocationPatternAnalyzer` | Classify allocation pressure/profile (churn vs. LOH-heavy) | Exact LOH fragmentation math; generation counting | Fragmentation → `LohFragmentationAnalyzer`; gen counts → `GCGenerationAnalyzer` | Should flag "LOH-heavy" as a coarse signal only, not attempt to quantify fragmentation itself |
| `ObjectShapeAnalyzer` | Type hierarchy/field-layout shape stats | Object counts/sizes | — | Clean boundary |
| `GCRootAnalyzer` | Aggregate root enumeration & categorization | Per-object "why is this alive" narrative | Per-object root path → `ReferenceChainAnalyzer` | Clean boundary today |
| `HeapTopologyAnalyzer` | Segment layout description (committed/reserved, per-segment top types) | VM-waste scoring | Waste scoring → `SegmentReservationAnalyzer` | **Hidden coupling**: depends on `Analysis.Pipeline`, an orchestration-layer namespace — an analyzer should never depend on the layer that orchestrates it |
| `ModuleAnalyzer` | Module/assembly inventory + version-conflict detection | Per-domain type/object stats | Type/object-by-module stats → arguably its own job or `AppDomainAnalyzer`'s, not both | Overlaps `AppDomainAnalyzer` — see Overlap section |
| `CrashAnalyzer` | Exception/crash evidence (active exceptions, chains, crash-thread identification) | Full thread-stack analysis beyond identifying the crash thread; exception-pressure trending | Thread stack detail → `ThreadAnalyzer`; trending → `TrendComparer` | Verify (per Deliverable 2) whether it reads the minidump exception stream or only heap-resident exception objects — these solve different problems and both may be needed |
| `HangAnalyzer` | Threadpool starvation/hang health scoring | Per-thread categorization; lock ownership graph; deep continuation chains | Categorization → `ThreadAnalyzer`; lock graph → `LockGraphAnalyzer`; continuations → `AsyncTaskAnalyzer` | Reimplements `DetectWaitPattern` instead of consuming `ThreadAnalyzer`'s output |
| `AsyncTaskAnalyzer` | Task status/continuation/faulted-task inventory | Exception detail formatting; owning a bespoke on-disk index format | Exception detail → `CrashAnalyzer` | **Hidden coupling**: private "task index" binary format bypasses the shared `Indexing` layer entirely |
| `LeakCandidateAnalyzer` | Rank/score leak candidates from multiple signals | Independently re-collecting the underlying signals via its own heap scan | Signal collection → `DominatorAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer`, `TimerLeakAnalyzer` | **Major finding**: per Deliverable 1's Heap Scan Mode column, this analyzer scans the index itself rather than composing over other analyzers' `AnalyzerDomainResult`s — it should be a pure aggregator, not a peer scanner |
| `DominatorAnalyzer` | Dominator-tree / retained-size computation (canonical source of "retained bytes"), plus high-fan-in / highly-referenced object detection (absorbed from the merged `RetentionAnalyzer`) | Leak scoring/ranking | Scoring → `LeakCandidateAnalyzer` | Is now the sole retained-size provider — the former duplication with `RetentionAnalyzer` is resolved |
| `StringAnalyzer` | String duplication/waste | General (non-string) object duplication | Non-string duplicate detection → future analyzer (Deliverable 2 gap) | Correctly scoped; resist generalizing |
| `CollectionAnalyzer` | BCL collection fill-rate/waste, all kinds | Logging/telemetry concerns; owning a bespoke reflection field-layout cache | Shared field-layout reflection cache → shared infra (also needed by `EventLeakAnalyzer`) | **Hidden coupling**: only analyzer with a `Microsoft.Extensions.Logging` dependency — inconsistent with every other analyzer being logging-free |
| `StaticRootLeakDetector` | Generic static-field root sweep for large retained subgraphs | Event/delegate-specific leak classification | Delegate/event classification → `EventLeakAnalyzer` | Should expose its static-field sweep as shared infra rather than have `EventLeakAnalyzer` reimplement it |
| `ReferenceChainAnalyzer` | On-demand root-path evidence for one object/type (shared `Traversal` BFS) | Aggregate root categorization; leak scoring | Categorization → `GCRootAnalyzer`; scoring → `LeakCandidateAnalyzer` | Should be the canonical path-finding provider; several leak/retention analyzers do **not** depend on the shared `Traversal` namespace and appear to implement their own graph walks instead (see Hidden Coupling) |
| `GCHandleAnalyzer` | Handle-table enumeration across all kinds, including `DependentHandle` target/dependent pair resolution (absorbed from the merged `DependentHandleAnalyzer`) | Weak-reference wrapper semantics | — (see boundary question below) | Whether the remaining 2-analyzer handle cluster (`GCHandleAnalyzer` + `WeakReferenceAnalyzer`) is justified is a Deliverable 6 question — `DependentHandleAnalyzer` merge already resolved |
| `LohFragmentationAnalyzer` | LOH free-block/fragmentation measurement | Coarse allocation-behavior classification; VM reservation waste | Classification → `AllocationPatternAnalyzer`; VM waste → `SegmentReservationAnalyzer` | Clean 3-way boundary as long as each stays scoped |
| `ThreadStackClusterAnalyzer` | Cluster/dedupe similar thread stacks (storm detection) | Per-thread categorization; hang scoring | Categorization → `ThreadAnalyzer`; scoring → `HangAnalyzer` | Independent stack walk instead of reusing `ThreadAnalyzer`'s pass |
| `ThreadAnalyzer` | Canonical per-thread inventory/categorization | Threadpool health scoring; lock ownership graph; stack clustering | Health → `HangAnalyzer`; lock graph → `LockGraphAnalyzer`; clustering → `ThreadStackClusterAnalyzer` | Should be the single stack-walk source of truth for the other three thread analyzers — currently isn't |
| `LockGraphAnalyzer` | Monitor/lock ownership graph, deadlock candidates | General per-thread wait/blocking classification | Wait classification → `ThreadAnalyzer`/`HangAnalyzer` | Should consume thread wait state, not re-derive it |
| `EventLeakAnalyzer` | Event-handler subscriber leak detection via delegate field scanning | Generic static-field sweeping | Static sweep → `StaticRootLeakDetector` | Duplicates static-field sweep instead of consuming it |
| `FinalizableObjectAnalyzer` | Finalizable/undisposed object detection | Literal finalization-queue enumeration (distinct GC structure) | Finalizer queue → itself, if scope is clarified, or a new capability (Deliverable 2 gap) | Risk of conflating "has finalizer, undisposed" with "currently queued for finalization" — different questions |
| `AsyncStateMachineAnalyzer` | State-machine instance inventory by method | Task status/continuation analysis | Task status → `AsyncTaskAnalyzer` | Necessary data-sharing with `AsyncTaskAnalyzer` (continuations reference state machines) should be explicit, not duplicated classification |
| `ArrayAnalyzer` | Array size/shape stats, large-array detection | LOH fragmentation analysis for large arrays | Fragmentation → `LohFragmentationAnalyzer` | Should hand off, not compute |
| `AppDomainAnalyzer` | Per-domain/module type & object stats | Module/assembly version-conflict detection | Version conflicts → `ModuleAnalyzer` | Overlaps `ModuleAnalyzer` — see Overlap section |
| `SegmentReservationAnalyzer` | Reserved vs. committed VM per GC segment | Fragmentation measurement; per-type segment topology | Fragmentation → `LohFragmentationAnalyzer`; topology → `HeapTopologyAnalyzer` | Correctly isolated — reference example for the rest of the catalog |
| `WeakReferenceAnalyzer` | `WeakReference`/`WeakReference<T>` inventory | Raw handle counting; dependent-handle pairing | Handle counts and pairs → `GCHandleAnalyzer` | Same boundary question as the (now 2-analyzer) handle cluster |
| `BoxingAnalyzer` | Boxed value-type detection & waste | General type/object stats | — | Clean boundary |
| `JitAnalyzer` | JIT-compiled method code-size inventory | Object-heap statistics | — | Correctly isolated — reference example |
| `DbConnectionAnalyzer` | `DbConnection` object state sampling | EF Core–specific diagnostics (DbContext, change tracker) | EF Core → future analyzer (Deliverable 2 gap) | Shares "resource state sampler" shape with the next 3 rows — see Overlap section |
| `WcfChannelAnalyzer` | WCF channel/proxy state sampling | Full WCF service-host diagnostics | — | Same sampler-shape note |
| `HttpObjectAnalyzer` | HttpClient/handler categorization | ASP.NET server-side diagnostics | ASP.NET → future analyzer (Deliverable 2 gap) | Same sampler-shape note |
| `TimerLeakAnalyzer` | Timer instance categorization & leak signal | Leak severity scoring | Scoring → `LeakCandidateAnalyzer` | Same sampler-shape note; should emit evidence only |

---

## Responsibility Overlap

Grouped by cluster (individual analyzer rows above cross-reference back to these):

1. **Module/domain inventory** — `ModuleAnalyzer` vs. `AppDomainAnalyzer`. Both compute
   per-module/type/object statistics; nothing distinguishes their outputs. Should merge, or split
   along a hard line (e.g., `ModuleAnalyzer` owns *assembly identity/version*, `AppDomainAnalyzer`
   owns *per-domain object ownership*) — see Deliverable 6.
2. **Leak/retention scoring** — **Resolved**: `RetentionAnalyzer` was merged into
   `DominatorAnalyzer`, which is now the sole retained-size/high-fan-in provider.
   `LeakCandidateAnalyzer` remains the sole scorer/ranker.
3. **Handle-table pair** — **Partially resolved**: `DependentHandleAnalyzer` was merged into
   `GCHandleAnalyzer`. `GCHandleAnalyzer` and `WeakReferenceAnalyzer` still walk overlapping parts
   of the handle table with no documented ownership boundary between them.
4. **Thread-domain quartet** — `ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`,
   `LockGraphAnalyzer` each perform an independent thread/stack walk and partially re-derive wait
   state. `ThreadAnalyzer` should be upstream of the other three.
5. **Static-field sweep** — `StaticRootLeakDetector` and `EventLeakAnalyzer` both scan static
   fields for retained subgraphs; `EventLeakAnalyzer` should consume the former's sweep rather
   than duplicate it.
6. **"Resource state sampler" quartet** — `DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
   `HttpObjectAnalyzer`, `TimerLeakAnalyzer` implement the identical
   classify-by-type-name → sample-state-field → bucket pattern four separate times.

## Responsibility Gaps

(Cross-references Deliverable 2's capability gaps where a *missing owner* rather than an
*overlapping owner* is the problem.)

- No analyzer owns **DI container / scoped-service leak detection**, **EF Core diagnostics**,
  **cache health**, **native/COM interop**, **runtime configuration reporting**, or
  **AssemblyLoadContext-specific unload-leak detection** — all currently unowned.
- **Resolved**: `DominatorAnalyzer` is now the canonical retained-size provider — the former
  competing `RetentionAnalyzer` heuristic was merged into it rather than left as a separate signal.
- No analyzer is the **canonical thread stack-walk provider** — despite `ThreadAnalyzer` being
  the obvious candidate, three other analyzers independently walk stacks rather than consuming it.

## Hidden Coupling

- `HeapTopologyAnalyzer` → `DumpDetective.Analysis.Pipeline`: an analyzer depending on the
  orchestration layer is a cross-layer violation waiting to surface as a cycle (relevant to
  Deliverable 7).
- `AsyncTaskAnalyzer` → private on-disk "task index" format: a second, undocumented binary format
  living outside `docs/binary-format.md`'s scope, coupling this analyzer to disk layout details no
  other analyzer needs to know about.
- `CollectionAnalyzer` → `Microsoft.Extensions.Logging`: the only analyzer with a
  logging dependency. Either every analyzer should have a consistent diagnostics/tracing story, or
  this one dependency is accidental scope creep that should be removed.
- Analyzers that **should** depend on the shared `DumpDetective.Analysis.Traversal` primitive —
  `StaticRootLeakDetector`, `EventLeakAnalyzer`, `DominatorAnalyzer` (now also owning the merged
  `RetentionAnalyzer` logic) — now do, via `BoundedGraphWalk` (see
  [Deliverable 5](phase0-deliverable-5-shared-infrastructure.md#3-root--retention-graph-service---done)),
  resolving the ad hoc traversal duplication flagged here.
- The "resource state sampler" quartet (`DbConnectionAnalyzer`/`WcfChannelAnalyzer`/
  `HttpObjectAnalyzer`/`TimerLeakAnalyzer`) has no shared base or helper at all — the coupling
  that *should* exist (one sampler, four configurations) is absent, and each analyzer instead
  carries its own copy of the same logic forward independently, meaning any future bug fix to the
  sampling approach must be applied four times.
