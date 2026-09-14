# Phase 1 full SDK extraction — capability surfaces + the analyzer retyping step

## Status

**36 of 36 analyzers retyped** — Phase 1's analyzer retyping step is complete.

**Next steps item 1 done (2026-09-14)**: `LegacyAnalyzerAdapter`, all 36 `XxxAnalyzerLegacyAdapter`
bridge classes, `Core.Abstractions.IAnalyzer`, and `Core.Models.AnalysisContext` are deleted. The
pipeline (`AnalysisPipeline`, `HeapIndexScanDispatcher`, `ThreadStackScanDispatcher`,
`AnalyzerExecutionRunner`, `DefaultAnalyzerFactory`) now runs `Sdk.Analysis.IAnalyzer` natively
against `RuntimeAnalysisContext`/the new `AnalysisContextBuilder` (`LegacyAnalysisContextTranslator`,
renamed/promoted). See [Legacy-adapter removal](#legacy-adapter-removal-2026-09-14) below. Next
steps items 2 (`DependencyDirectionTests`, SDK project rules) and 3 (Phase 3/5 follow-on) are still
open.

All analyzers migrated to SDK contracts via `LegacyAnalyzer` adapters (dump-side implementations in `DumpDetective.Analysis/Sdk/`):

`AllocationPatternAnalyzer`, `ArrayAnalyzer`, `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer`, `BoxingAnalyzer`,
`CollectionAnalyzer`, `CrashAnalyzer`, `DbConnectionAnalyzer`, `FinalizableObjectAnalyzer`,
`GCGenerationAnalyzer`, `GCHandleAnalyzer`, `GCRootAnalyzer`, `HangAnalyzer`, `HeapTopologyAnalyzer`, `HttpObjectAnalyzer`, `JitAnalyzer`,
`LockGraphAnalyzer`, `LohFragmentationAnalyzer`, `MemoryAnalyzer`, `ObjectShapeAnalyzer`,
`SegmentReservationAnalyzer`, `SqlCommandAnalyzer`, `SqlConnectionPoolAnalyzer`,
`SqlTransactionAnalyzer`, `StringAnalyzer`, `ThreadAnalyzer`, `ThreadStackClusterAnalyzer`, `TimerLeakAnalyzer`,
`WcfChannelAnalyzer`, `WeakReferenceAnalyzer`, `ModuleAnalyzer`, `StaticRootLeakDetector`,
`EventLeakAnalyzer`, `DominatorAnalyzer`, `ReferenceChainAnalyzer`, `LeakCandidateAnalyzer`.

## Key findings

- **Two-tier design forced by evidence, not architecture**: All 35 analyzers touch `context.Heap`/`context.Runtime` coarse operations. **Two-thirds (24) also read live field values** — ruling out a single source-neutral object/field model (no other source carries field layouts). Split instead into Tier 1 (coarse/structural, source-neutral) + Tier 2 (dump-specific field reads, `dump.object-fields` escape hatch).

- **LeakCandidateAnalyzer** (last, 2026-09-13): Cross-analyzer correlation via stopgap `LeakCandidateAnalyzerOptions` extraction in adapter's `BuildSdkContext`. Phase-4 dependency orchestrator will replace this.

- **ReferenceChainAnalyzer** (2026-09-13): Two behavior changes documented on `IHeapRootPathQuery` remarks (force-expand predicate, root-priority ordering) — both pre-existing decisions, not new.

- **DominatorAnalyzer** (2026-09-13): Re-classified Tier-1-only (no field-value reads). Dropped diagnostic logging.

- **Critical pre-existing bugs found and documented**:
  - `StaticRootLeakDetector` never fires (upstream `RootIndexWriter` filters for non-existent ClrMD 3+ root kinds). Fix on branch `fix/static-root-detection` deliberately unmerged (~453s/63.7 GB cost); batch preserves broken behavior with doc comment.
  - `BoxingAnalyzer` (2026-09-12): Perf regression found/fixed — first cut computed `HasIEquatableInterface` for *every* type on the heap instead of value types only; stalled real-dump test 10+ minutes before fix.
  - `CollectionAnalyzer` (2026-09-12): Two real bugs caught by real-dump gate: (1) `IHeapTypeStatisticsQuery.GetTypeStatistics()` rebuilds whole dictionary per call, no memoization — O(candidates × distinct-types) blowup on 799k+ collections; (2) force-expand root-path search policy hung indefinitely. Both fixed.

## Architecture: two-tier capability surface

### Tier 1 — SDK-safe, source-neutral (ships in `DumpDetective.Sdk`)

Covers coarse heap/runtime traversal. Each capability is nullable on `AnalysisContext` (absent = unavailable):

| Surface | Capability | Replaces |
|---|---|---|
| `IHeapObjectStream` | `heap.objects` | `heap.EnumerateObjects()` |
| `IHeapObjectLookup` | `heap.objects` | `heap.GetObject()` |
| `IHeapRootQuery` | `heap.roots` | `heap.EnumerateRoots()`, static/pinned root queries |
| `IHeapHandleQuery` | `heap.handles` | `.EnumerateHandles()` |
| `IHeapSegmentQuery` | `heap.segments` | `heap.GetSegmentByAddress()` |
| `IHeapFinalizerQueueQuery` | `heap.finalizer-queue` | `heap.EnumerateFinalizableObjects()` |
| `IHeapSyncBlockQuery` | `runtime.locks` | `heap.EnumerateSyncBlocks()` |
| `IHeapTypeStatisticsQuery` | `heap.types` | Type aggregates, per-type statistics |
| `IHeapReferenceQuery` / `IHeapReverseReferenceQuery` | `heap.references` / `heap.reverse-references` | Forward/reverse index providers |
| `IHeapReachabilityQuery` | `heap.reachability` | Stage A reachable-address set |
| `IHeapDominatorQuery` | `heap.dominators` | Stage B dominator tree, retained bytes |
| `IHeapRootPathQuery` | `heap.root-paths` | Root-path search (BFS with depth limit) |
| `IRuntimeThreadQuery` | `runtime.threads` | `Runtime.Threads`, stack frames |
| `IRuntimeModuleQuery` | `runtime.modules` | Module/AppDomain enumeration |
| `IRuntimeJitQuery` | `runtime.jit` | `Runtime.EnumerateJitManagers()` |
| `IRuntimeThreadPoolQuery` | `runtime.thread-pool` | Thread-pool statistics |

All are re-signatures of existing `IHeapAnalysisCache`/`ClrHeap`/`ClrRuntime` calls — no new capability, only new source-neutral shape.

### Tier 2 — dump-specific, never in SDK

A new capability `dump.object-fields` and related primitives (`IDumpObjectFieldQuery`, `IDumpArrayShapeQuery`, etc.) exposing field reads live in `Sources.ClrDump` (not in `DumpDetective.Sdk`). Analyzers declaring `RequiresCapability("dump.object-fields")` correctly never run on sources without field layouts.

## SDK types shipped (2026-09-09, then extended by retyping wave)

- `IAnalyzer` — same shape as `Core.Abstractions.IAnalyzer`, retargeted to new `AnalysisContext` (returns bare `ValueTask`, not `AnalyzerDomainResult`)
- `AnalysisContext` — capability-resolved, nullable properties per Tier-1 surfaces above
- `HeapObjectRef`, `TypeRef`, `FieldValue` — SDK-equivalent POCOs
- `Sdk.Analysis.Analysis` namespace + three attributes: `RequiresCapabilityAttribute`, `OptionalCapabilityAttribute`, `AnalyzerModuleAttribute`

Dump-side implementations wired into `LegacyAnalysisContextTranslator` (the bridge from old to new context).

## Retyping batches summary

### Tier 1 wave (7 batches, 11 analyzers total)

1. **Pilot: `GCGenerationAnalyzer`** (2026-09-10) — extended `IHeapTypeStatisticsQuery` with generation counts, added `AnalyzerOptions` slot for analyzer-specific options
2. **`SegmentReservationAnalyzer`** (2026-09-11) — extended `IHeapSegmentQuery` with segment-specific facts; found recurring "enum-name collision" gotcha
3. **`HeapTopologyAnalyzer`** (2026-09-11) — added per-segment object enumeration + `HeapObjectRef.TypeDisplayName` distinction (canonical vs. display name)
4. **`GCRootAnalyzer`** (2026-09-11) — first consumer of `IHeapRootQuery`, `IHeapDominatorQuery`, `IHeapReferenceQuery`; added `HeapGenerationTag` enum
5. **`MemoryAnalyzer`** (2026-09-11) — extracted shared `CapabilityBoundedGraphWalk` from `GCRootAnalyzer`; added `SampleAddress`/`ModuleName` to type statistics
6. **`JitAnalyzer`** (2026-09-11) — first `runtime.*` consumer; redesigned `IRuntimeJitQuery`/`IRuntimeThreadQuery` (declared but never implemented before)
7. **`LohFragmentationAnalyzer`** (2026-09-11) — added `IHeapSegmentQuery.EnumerateLohFreeBlocks()`, `HeapObjectRef.IsFree`

Tier 1 finished by thread-quartet audit (2026-09-11): `LockGraphAnalyzer`/`ThreadStackClusterAnalyzer` are Tier-1-only (no field-value reads); `ThreadAnalyzer`/`HangAnalyzer` are Tier 2.

### Tier 2 wave (27 analyzers, 2026-09-11–2026-09-13)

- **Pilot: `SqlConnectionPoolAnalyzer`** (2026-09-11) — shipped `IDumpObjectFieldQuery`/`FieldValue`; extracted `TypedResourceCandidateStreamingLegacyAdapter<TSelf, TSdkAnalyzer>` base
- **Typed-resource quartet** (2026-09-11–2026-09-12): `SqlCommandAnalyzer`, `SqlTransactionAnalyzer`, `HttpObjectAnalyzer`, `TimerLeakAnalyzer` — all reuse shared adapter; `HttpObjectAnalyzer` added `TryGetStringValue` (follow object-ref field into string)
- **Root-paths capability** (2026-09-11): Built `IHeapRootPathQuery` thin wrapper over existing `RootPathFinder`/`ReferenceGraph` machinery
- **`DbConnectionAnalyzer`** (2026-09-12) — first `heap.root-paths` consumer; documented pre-existing limitation (missing `_innerConnection` field chasing)
- **`WeakReferenceAnalyzer`** (2026-09-12) — added `heap.reachability`, `FieldValueKind.NativeInt`, dropped three pre-existing degraded data-source states
- **`BoxingAnalyzer`** (2026-09-12) — Tier-1-only tier-split correction; perf regression found/fixed (whole-heap interface walk instead of value-types-only)
- **`FinalizableObjectAnalyzer`** (2026-09-12) — redesigned `IHeapFinalizerQueueQuery`, added `IsFinalizableType`/`IsDisposableType` to type statistics
- **`ThreadAnalyzer`** (2026-09-12) — tier-split correction (ClrException.Message is convenience property, not raw field); extended `RuntimeThreadRef` with 7 new fields
- **`HangAnalyzer`** (2026-09-12) — large batch (~735 lines), implements both heap-scan and thread-stack-scan participants; added `runtime.thread-pool`
- **`AsyncStateMachineAnalyzer`** (2026-09-12) — closed field-enumeration gap with two new Tier-2 primitives (`TryGetFieldTypeName`, `EnumerateObjectReferenceFields`); eliminated duplicate heap pass
- **`AsyncTaskAnalyzer`** (2026-09-12) — added `IDumpArrayShapeQuery.EnumerateObjectReferenceElements()`, `TryReadNestedStructField()`; bespoke adapter (interface-based discovery)
- **`CollectionAnalyzer`** (2026-09-12) — two real bugs caught: (1) `GetTypeStatistics()` called per candidate (O(n×m) blowup), (2) force-expand hung indefinitely. Added `EnumerateInstanceFields`, new `TryGetReverseReferences` overload
- **`CrashAnalyzer`** (2026-09-13) — bespoke adapter (structural exception detection); added `RuntimeThreadRef.ExceptionAddress`, `EnumerateExceptionStackTrace()`
- **`StringAnalyzer`** (2026-09-13) — bespoke (full-population type scan); dropped disk `StringDedupIndex` fast path (redundant once shared scan always runs)
- **`ModuleAnalyzer`** (2026-09-13) — no scan participant needed; redesigned `IRuntimeModuleQuery` (declared but never implemented); found/fixed two-pass heavy-module gate bug
- **`StaticRootLeakDetector`** (2026-09-13) — plain adapter; added `IHeapDominatorQuery.EnumerateRetainedObjects()`, `HasDelegateField()` Tier-2 primitive; bug preserved with doc comment
- **`EventLeakAnalyzer`** (2026-09-13) — largest batch (~3000 lines); hot path stays dump-side (hand-rolled `IMemoryReader` field reads to avoid ClrMD overhead); real-dump verified sub-30s (no regression)
- **Remaining Tier-1+Tier-2**: Only `ObjectShapeAnalyzer`, `AllocationPatternAnalyzer`, `ArrayAnalyzer` left (all Tier-1+Tier-2 tier-split corrections similar to `BoxingAnalyzer`/`ThreadAnalyzer`)

## Test strategy

- **Characterization tests**: New per-analyzer suite covering SDK-contract behavior via dictionary-backed fakes; no real/live heap needed
- **Real-dump tests** (`[DiscrepancyFact]`): One dump, foreground, per batch; done after fix (not speculatively). Added 5-minute timeout rule for hangs
- **Existing test suites**: Pre-retyping unit tests retained/updated where possible; internal-state-reflection tests deleted when adapter makes them impossible

## Out of scope

- Phase 3 package split — analyzers keep current file locations
- Phase 5 observation emission — `AnalyzerDomainResult` stays unchanged
- Phase 4 session DAG
- Full source-neutral object/field model — Tier 2 stays dump-specific (no other planned source needs field introspection)

## Legacy-adapter removal (2026-09-14)

Next steps item 1 (below), fully executed:

- **Pipeline retargeted**: `RuntimeAnalysisContext` stopped deriving from the old
  `Core.Abstractions.AnalysisContext` and became a standalone class carrying the same fields
  (`Runtime`, `Cache`, `AnalysisOptions`, `Diagnostics`, `DiagnosticsSink`, `Progress`,
  `ReportSubPhase`, `CompletedRunResults`) plus its existing `RuntimeFacade`/`HeapIndexBuilder`/
  `Query` additions. `LegacyAnalysisContextTranslator.Translate` was promoted/renamed to
  `Pipeline/AnalysisContextBuilder.Build` — the primary (only) `Sdk.Analysis.AnalysisContext`
  builder. A new opt-in `ICustomizesAnalysisContext` interface covers the handful of analyzers that
  need bespoke context injection (precomputed thread/heap queries, cross-analyzer
  `CompletedRunResults` reads). `AnalyzerExecutionRunner` now builds context via
  `ICustomizesAnalysisContext` or `AnalysisContextBuilder`, awaits the bare `ValueTask`, then reads
  `IProducesAnalyzerDomainResult.LastResult`.
- **Scan-participant relocation**: `IHeapIndexScanParticipant`/`IThreadStackScanParticipant`/
  `IParallelHeapIndexScanParticipant`/`IRequiresDominatorTreeIndex`/`IRequiresReachableGraphIndex`
  retargeted from the old `AnalysisContext` to `RuntimeAnalysisContext`. Real scan/customization
  logic that lived on ~23 `XxxAnalyzerLegacyAdapter` classes moved onto the SDK analyzer classes
  themselves: a new shared `TypedResourceHeapIndexScanState` helper (`Analyzers/Infra/`) replaced the
  old `TypedResourceCandidateStreamingLegacyAdapter<TSelf, TSdkAnalyzer>` base for the 10
  typed-resource-shaped analyzers (SqlCommand/SqlTransaction/SqlConnectionPool/HttpObject/
  WcfChannel/TimerLeak/DbConnection/WeakReference/Collection/AsyncStateMachine); a shared
  `ThreadStackScanState` helper covers the thread quartet (LockGraph/Thread/ThreadStackCluster/Hang).
  `EventLeakAnalyzer`, `AsyncTaskAnalyzer`, and `StringAnalyzer` — whose adapters carried the largest
  bespoke scan bodies — were merged in via a `partial class` split (`EventLeakAnalyzer.HeapScan.cs`
  etc.) to keep the diff reviewable.
- **Real bug found and fixed during verification**: the deleted `LegacyAnalyzerAdapter<T>.AnalyzeAsync`
  always did `result.Stamp(this)`, populating `AnalyzerName`/`Category` on every domain result. No
  analyzer's own `AnalyzeAsync` set these fields itself — that was always the adapter's job. Missing
  this in the initial pass silently produced empty-named results everywhere. Fixed by having each of
  the 36 analyzers' `LastResult` setter stamp `AnalyzerName`/`Category` from its own `Name`/`Category`
  (matches the deleted adapter's behavior exactly, now analyzer-owned instead of adapter-owned).
- **Registration**: `DefaultAnalyzerFeatureModuleCatalog`'s 36 `Module(...)` entries now register
  `typeof(XxxAnalyzer)` directly (was `typeof(XxxAnalyzerLegacyAdapter)`); `DefaultAnalyzerFactory`/
  `IAnalyzerFactory` return `Sdk.Analysis.IAnalyzer`.
- **Verification**: full non-real-dump suite green (1576/1576). Every analyzer's `[DiscrepancyFact]`
  real-dump test run individually, foreground, sequentially against the 3.3 GB reference dump —
  timings match historical per-analyzer baselines (single-digit to low-double-digit seconds), no
  regressions found beyond the Stamp bug above (caught and fixed before the real-dump pass).

## Next steps

1. ~~Delete `LegacyAnalyzerAdapter` and old `Core.Abstractions.IAnalyzer`/`Models.AnalysisContext`~~ — done 2026-09-14, see above.
2. Update `DependencyDirectionTests`, SDK project rules
3. Phase 3's capability-attribute discovery and Phase 5's observation emission now have real ground to build on
