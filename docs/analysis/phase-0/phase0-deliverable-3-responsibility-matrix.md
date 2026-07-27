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
| `HeapTopologyAnalyzer` | Segment layout description (committed/reserved, per-segment top types) | VM-waste scoring | Waste scoring → `SegmentReservationAnalyzer` | **Resolved**: the `Analysis.Pipeline` import was confirmed dead (no symbol from it was consumed) and removed — no longer depends on the orchestration layer |
| `ModuleAnalyzer` | Module/assembly inventory + version-conflict detection, plus per-domain/module type & object stats (absorbed from the merged `AppDomainAnalyzer`) | — | — | **Resolved**: `AppDomainAnalyzer` was merged into `ModuleAnalyzer` (Deliverable 6/10 item 9) — the former overlap is gone and `AppDomainAnalyzer.cs` was deleted |
| `CrashAnalyzer` | Exception/crash evidence (active exceptions, chains, crash-thread identification) | Full thread-stack analysis beyond identifying the crash thread; exception-pressure trending | Thread stack detail → `ThreadAnalyzer`; trending → `TrendComparer` | **Verified**: only reads heap-resident exception objects (`thread.CurrentException`/`ClrObject.AsException`) — it does not read the minidump's `MINIDUMP_EXCEPTION_STREAM` (faulting thread, exception code, fault address). Confirmed a real, closeable gap; investigation complete (P1 item 11) and recommends direct DBGHELP P/Invoke, since ClrMD 4.0 exposes no public API for it — implementation not yet started |
| `HangAnalyzer` | Threadpool starvation/hang health scoring | Per-thread categorization; lock ownership graph; deep continuation chains | Categorization → `ThreadAnalyzer`; lock graph → `LockGraphAnalyzer`; continuations → `AsyncTaskAnalyzer` | **Partially resolved**: the independent thread/stack *walk* is gone — `HangAnalyzer` now implements the shared `IThreadStackScanParticipant` contract (see Overlap section). Its own `DetectWaitPattern` wait-classification logic is unchanged and still not sourced from `ThreadAnalyzer` |
| `AsyncTaskAnalyzer` | Task status/continuation/faulted-task inventory | Exception detail formatting; owning a bespoke on-disk index format | Exception detail → `CrashAnalyzer` | **Resolved**: the task-index format (magic/version/record-size constants and the reader) was extracted into `Indexing/TaskIndexReader.cs`, mirroring `RootIndexReader`'s pattern — `AsyncTaskAnalyzer` now depends only on the public read interface, not the format internals |
| `LeakCandidateAnalyzer` | Rank/score leak candidates from multiple signals | Independently re-collecting the underlying signals via its own heap scan | Signal collection → `DominatorAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer`, `TimerLeakAnalyzer` | **Resolved**: no longer walks `runtime.EnumerateHandles()` itself — implements `IDeferredAnalyzer` and reads the already-completed `GCHandleDomainResult` via `AnalysisContext.CompletedRunResults`, running as a second pass after every non-deferred analyzer. Now a pure aggregator for that signal, as intended |
| `DominatorAnalyzer` | Dominator-tree / retained-size computation (canonical source of "retained bytes"), plus high-fan-in / highly-referenced object detection (absorbed from the merged `RetentionAnalyzer`) | Leak scoring/ranking | Scoring → `LeakCandidateAnalyzer` | Is now the sole retained-size provider — the former duplication with `RetentionAnalyzer` is resolved |
| `StringAnalyzer` | String duplication/waste | General (non-string) object duplication | Non-string duplicate detection → future analyzer (Deliverable 2 gap) | Correctly scoped; resist generalizing |
| `CollectionAnalyzer` | BCL collection fill-rate/waste, all kinds | Owning a bespoke reflection field-layout cache | Shared field-layout reflection cache → shared infra (also needed by `EventLeakAnalyzer`) | **Resolved/reclassified**: the `ILogger<T>? logger = null` dependency is now a deliberate, platform-wide pattern (any analyzer may take it, resolved via `ActivatorUtilities`) documented in [architecture.md §14 Observability](../../architecture.md#14--observability) — not accidental scope creep. The field-layout reflection cache is still not shared with `EventLeakAnalyzer` and remains a separate, open item |
| `StaticRootLeakDetector` | Generic static-field root sweep for large retained subgraphs | Event/delegate-specific leak classification | Delegate/event classification → `EventLeakAnalyzer` | Should expose its static-field sweep as shared infra rather than have `EventLeakAnalyzer` reimplement it |
| `ReferenceChainAnalyzer` | On-demand root-path evidence for one object/type (shared `Traversal` BFS) | Aggregate root categorization; leak scoring | Categorization → `GCRootAnalyzer`; scoring → `LeakCandidateAnalyzer` | Should be the canonical path-finding provider; several leak/retention analyzers do **not** depend on the shared `Traversal` namespace and appear to implement their own graph walks instead (see Hidden Coupling) |
| `GCHandleAnalyzer` | Handle-table enumeration across all kinds, including `DependentHandle` target/dependent pair resolution (absorbed from the merged `DependentHandleAnalyzer`) | Weak-reference wrapper semantics | — (see boundary question below) | Whether the remaining 2-analyzer handle cluster (`GCHandleAnalyzer` + `WeakReferenceAnalyzer`) is justified is a Deliverable 6 question — `DependentHandleAnalyzer` merge already resolved |
| `LohFragmentationAnalyzer` | LOH free-block/fragmentation measurement | Coarse allocation-behavior classification; VM reservation waste | Classification → `AllocationPatternAnalyzer`; VM waste → `SegmentReservationAnalyzer` | Clean 3-way boundary as long as each stays scoped |
| `ThreadStackClusterAnalyzer` | Cluster/dedupe similar thread stacks (storm detection) | Per-thread categorization; hang scoring | Categorization → `ThreadAnalyzer`; scoring → `HangAnalyzer` | **Resolved (stack-walk only)**: implements the shared `IThreadStackScanParticipant` contract, consuming the single stack walk driven by `ThreadStackScanDispatcher` instead of walking independently |
| `ThreadAnalyzer` | Canonical per-thread inventory/categorization | Threadpool health scoring; lock ownership graph; stack clustering | Health → `HangAnalyzer`; lock graph → `LockGraphAnalyzer`; clustering → `ThreadStackClusterAnalyzer` | **Resolved (stack-walk only)**: is now one of several `IThreadStackScanParticipant`s fed by the shared `ThreadStackScanDispatcher`/`ThreadStackSnapshot` pass, rather than each analyzer separately calling `EnumerateStackTrace()`. Wait-state/hang classification and lock-ownership derivation are still computed independently by `HangAnalyzer` and `LockGraphAnalyzer` |
| `LockGraphAnalyzer` | Monitor/lock ownership graph, deadlock candidates | General per-thread wait/blocking classification | Wait classification → `ThreadAnalyzer`/`HangAnalyzer` | **Partially resolved**: consumes the shared stack walk via `IThreadStackScanParticipant`, but still derives its own wait/blocking classification rather than consuming `ThreadAnalyzer`'s |
| `EventLeakAnalyzer` | Event-handler subscriber leak detection via delegate field scanning | Generic static-field sweeping | Static sweep → `StaticRootLeakDetector` | Duplicates static-field sweep instead of consuming it |
| `FinalizableObjectAnalyzer` | Finalizable/undisposed object detection | Literal finalization-queue enumeration (distinct GC structure) | Finalizer queue → itself, if scope is clarified, or a new capability (Deliverable 2 gap) | Risk of conflating "has finalizer, undisposed" with "currently queued for finalization" — different questions |
| `AsyncStateMachineAnalyzer` | State-machine instance inventory by method | Task status/continuation analysis | Task status → `AsyncTaskAnalyzer` | Necessary data-sharing with `AsyncTaskAnalyzer` (continuations reference state machines) should be explicit, not duplicated classification |
| `ArrayAnalyzer` | Array size/shape stats, large-array detection | LOH fragmentation analysis for large arrays | Fragmentation → `LohFragmentationAnalyzer` | Should hand off, not compute |
| `SegmentReservationAnalyzer` | Reserved vs. committed VM per GC segment | Fragmentation measurement; per-type segment topology | Fragmentation → `LohFragmentationAnalyzer`; topology → `HeapTopologyAnalyzer` | Correctly isolated — reference example for the rest of the catalog |
| `WeakReferenceAnalyzer` | `WeakReference`/`WeakReference<T>` inventory | Raw handle counting; dependent-handle pairing | Handle counts and pairs → `GCHandleAnalyzer` | Same boundary question as the (now 2-analyzer) handle cluster |
| `BoxingAnalyzer` | Boxed value-type detection & waste | General type/object stats | — | Clean boundary |
| `JitAnalyzer` | JIT-compiled method code-size inventory | Object-heap statistics | — | Correctly isolated — reference example |
| `DbConnectionAnalyzer` | `DbConnection` object state sampling | EF Core–specific diagnostics (DbContext, change tracker) | EF Core → future analyzer (Deliverable 2 gap) | **Resolved (shared shape)**: now composes over `TypedResourceCandidateScanner`/`TypedResourceScanDriver` and its own `ITypedResourceCandidateSource`/`ITypedResourceInstanceSampler<TSnapshot>` implementation instead of a hand-rolled classify/sample/bucket loop — see Overlap section |
| `WcfChannelAnalyzer` | WCF channel/proxy state sampling | Full WCF service-host diagnostics | — | Same shared-sampler resolution note |
| `HttpObjectAnalyzer` | HttpClient/handler categorization | ASP.NET server-side diagnostics | ASP.NET → future analyzer (Deliverable 2 gap) | Same shared-sampler resolution note |
| `TimerLeakAnalyzer` | Timer instance categorization & leak signal | Leak severity scoring | Scoring → `LeakCandidateAnalyzer` | Same shared-sampler resolution note; should still emit evidence only |

---

## Responsibility Overlap

Grouped by cluster (individual analyzer rows above cross-reference back to these):

1. **Module/domain inventory** — **Resolved**: `AppDomainAnalyzer` was fully merged into
   `ModuleAnalyzer` (Deliverable 6/10 item 9) — options, domain-result model, analyzer logic,
   finding generator, trend comparer, and section builder were all merged, `AppDomainAnalyzer.cs`
   was deleted, and CLI wiring/`SectionIdDomainMap`/`InsightEngine`/catalog registrations were
   updated. There is now a single per-module/per-domain inventory owner.
2. **Leak/retention scoring** — **Resolved**: `RetentionAnalyzer` was merged into
   `DominatorAnalyzer`, which is now the sole retained-size/high-fan-in provider.
   `LeakCandidateAnalyzer` remains the sole scorer/ranker.
3. **Handle-table pair** — **Partially resolved**: `DependentHandleAnalyzer` was merged into
   `GCHandleAnalyzer`. `GCHandleAnalyzer` and `WeakReferenceAnalyzer` still walk overlapping parts
   of the handle table with no documented ownership boundary between them — this is still an open
   Deliverable 6 question.
4. **Thread-domain quartet** — **Resolved for the stack-walk pass, still open for wait-state
   classification**. `ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`, and
   `LockGraphAnalyzer` now all implement `IThreadStackScanParticipant` and are fed a single shared
   stack walk by `ThreadStackScanDispatcher` (`ThreadStackSnapshot`) instead of each independently
   calling `EnumerateStackTrace()`. What is *not* yet unified: `HangAnalyzer` still has its own
   private `DetectWaitPattern` wait-classification logic, and `LockGraphAnalyzer` still derives its
   own wait/blocking state rather than consuming `ThreadAnalyzer`'s categorization — the
   sweep/classification duplication this bullet originally flagged remains open, only the
   underlying stack-walk enumeration was unified.
5. **Static-field sweep** — `StaticRootLeakDetector` and `EventLeakAnalyzer` both scan static
   fields for retained subgraphs; `EventLeakAnalyzer` should consume the former's sweep rather
   than duplicate it. A shared `RootSetCache` now exists for root *enumeration* (stack/static/handle
   roots), and both analyzers read through it, but that is a narrower, substrate-level resolution —
   the sweep/classification duplication this bullet flags is still open.
6. **"Resource state sampler" quartet** — **Resolved**: `DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
   `HttpObjectAnalyzer`, `TimerLeakAnalyzer` now share the classify/sample/bucket pattern through
   `TypedResourceCandidateScanner`, `InstanceStateSampler<TSnapshot>`, `TypedResourceScanDriver`,
   and the compiler-checked `ITypedResourceCandidateSource`/`ITypedResourceInstanceSampler<TSnapshot>`
   contracts, rather than each carrying its own copy of the same logic.

## Responsibility Gaps

(Cross-references Deliverable 2's capability gaps where a *missing owner* rather than an
*overlapping owner* is the problem.)

- No analyzer owns **DI container / scoped-service leak detection**, **EF Core diagnostics**,
  **cache health**, **native/COM interop**, **runtime configuration reporting**, or
  **AssemblyLoadContext-specific unload-leak detection** — all currently unowned.
- **Resolved**: `DominatorAnalyzer` is now the canonical retained-size provider — the former
  competing `RetentionAnalyzer` heuristic was merged into it rather than left as a separate signal.
- **Resolved**: `ThreadAnalyzer` is now, along with `HangAnalyzer`, `ThreadStackClusterAnalyzer`,
  and `LockGraphAnalyzer`, one of several consumers of the single stack walk driven by
  `ThreadStackScanDispatcher`/`IThreadStackScanParticipant` — no analyzer independently walks
  stacks anymore. Wait-state classification (`HangAnalyzer`) and lock-ownership derivation
  (`LockGraphAnalyzer`) are still computed independently rather than sourced from `ThreadAnalyzer`.

## Hidden Coupling

- **Resolved**: `HeapTopologyAnalyzer` → `DumpDetective.Analysis.Pipeline` — the import was dead
  (no symbol from it was consumed) and has been removed; the class now depends only on
  `Core.Abstractions`/`Core.Models`/`Core.Options`/`Analysis.Models`.
- **Resolved**: `AsyncTaskAnalyzer` → private on-disk "task index" format — the format
  (magic/version/record-size constants and reader) was extracted into
  `Indexing/TaskIndexReader.cs`, mirroring `RootIndexReader`'s pattern; `AsyncTaskAnalyzer` now
  depends only on the public read interface.
- **Resolved/reclassified**: `CollectionAnalyzer` → `Microsoft.Extensions.Logging` — this is no
  longer a one-off dependency. The `ILogger<T>? logger = null` mechanism is now a platform-wide,
  optional pattern (resolved via `ActivatorUtilities` in `DefaultAnalyzerFactory`) documented in
  [architecture.md §14 Observability](../../architecture.md#14--observability), used for
  per-object scan-failure diagnostics across ~29 call sites. Not accidental scope creep.
- Analyzers that **should** depend on the shared `DumpDetective.Analysis.Traversal` primitive —
  `StaticRootLeakDetector`, `EventLeakAnalyzer`, `DominatorAnalyzer` (now also owning the merged
  `RetentionAnalyzer` logic) — now do, via `BoundedGraphWalk` (see
  [Deliverable 5](phase0-deliverable-5-shared-infrastructure.md#3-root--retention-graph-service---done)),
  resolving the ad hoc traversal duplication flagged here.
- **Resolved**: the "resource state sampler" quartet (`DbConnectionAnalyzer`/`WcfChannelAnalyzer`/
  `HttpObjectAnalyzer`/`TimerLeakAnalyzer`) now shares `TypedResourceCandidateScanner`,
  `InstanceStateSampler<TSnapshot>`, and `TypedResourceScanDriver` behind the compiler-checked
  `ITypedResourceCandidateSource`/`ITypedResourceInstanceSampler<TSnapshot>` contracts — one
  sampler, four configurations, as intended.
