# Phase 1 full SDK extraction — capability surfaces + the analyzer retyping step

## Status (as of 2026-09-13)

**33 of 36 analyzers retyped** (verified against `src/DumpDetective.Analysis/Analyzers/**/*LegacyAdapter.cs`
in the tree, not just this doc's own narrative — this doc's original "35" count in the section below
was an approximation from § 10 point 8's grep-based measurement; the real count is 36). The
`dump.object-fields` Tier-2 escape hatch **is built** (see "Tier 2 wave" below) — the "still not
built" language later in this doc is stale as of the pilot, 2026-09-11.

**Done:**
`AllocationPatternAnalyzer`, `ArrayAnalyzer`, `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer`, `BoxingAnalyzer`,
`CollectionAnalyzer`, `CrashAnalyzer`, `DbConnectionAnalyzer`, `FinalizableObjectAnalyzer`,
`GCGenerationAnalyzer`, `GCHandleAnalyzer`,
`GCRootAnalyzer`, `HangAnalyzer`, `HeapTopologyAnalyzer`, `HttpObjectAnalyzer`, `JitAnalyzer`,
`LockGraphAnalyzer`, `LohFragmentationAnalyzer`, `MemoryAnalyzer`, `ObjectShapeAnalyzer`,
`SegmentReservationAnalyzer`, `SqlCommandAnalyzer`, `SqlConnectionPoolAnalyzer`,
`SqlTransactionAnalyzer`, `StringAnalyzer`, `ThreadAnalyzer`, `ThreadStackClusterAnalyzer`, `TimerLeakAnalyzer`,
`WcfChannelAnalyzer`, `WeakReferenceAnalyzer`, `ModuleAnalyzer`, `StaticRootLeakDetector`,
`EventLeakAnalyzer`.

**Remaining (3):**
`DominatorAnalyzer`,
`ReferenceChainAnalyzer`,
and **`LeakCandidateAnalyzer`** —
explicit user direction: do `LeakCandidateAnalyzer` last of all, since it needs cross-analyzer
result access (`context.CompletedRunResults`-equivalent) and deferred-execution-ordering modeling
that don't exist in the SDK yet, on top of everything the other 2 need.

Detailed plan for the two items [phase-1-contracts-sdk.md](phase-1-contracts-sdk.md) leaves
deferred (`Analysis/`, `Presentation/`) and the ownerless gap recorded at
[modularity-plan.md § 10 point 8](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned).
Supersedes that point's open question with a concrete answer. Depends on nothing existing being
retired — every step here is additive until the retyping step itself begins.

## This is bigger than § 10 point 8 scoped it — a new finding

Point 8 measured that all 35 analyzers touch `context.Heap`/`context.Runtime` directly and sized
`IHeapAnalysisCache` at 18 members. Going one level deeper, grepping actual ClrMD API calls across
`src/DumpDetective.Analysis/Analyzers/`:

- All 35 call coarse heap operations directly: `heap.EnumerateObjects()`, `heap.GetObject()`,
  `heap.GetTypeByMethodTable()`, `heap.EnumerateRoots()`, `.EnumerateHandles()`,
  `heap.GetSegmentByAddress()`, `heap.EnumerateFinalizableObjects()`, `heap.EnumerateSyncBlocks()`,
  `Runtime.EnumerateJitManagers()`, `Runtime.Threads`, `Runtime.AppDomains`.
- **24 of 35 also do field-level object introspection** (`.Fields`, `GetFieldByName`,
  `ReadObjectField`/`ReadStringField`, or equivalent) — reading named fields off specific heap
  objects to recover application-level state (WCF channel state, SQL command text, HTTP object
  shape, timer callback targets, event-handler delegate chains, async state-machine fields, ...).

That second number is the real finding. It means "capability-scoped query surfaces" can't stop at
coarse enumeration — two-thirds of analyzers need something isomorphic to `ClrType`/`ClrObject`
field reflection to do their actual job. Building a full source-neutral object/field model in the
SDK (replacing `ClrType.Fields`, generic-argument resolution, boxed-value reads, etc.) would be the
single largest design-and-engineering investment in this entire plan — and there is no real second
consumer for it: no trace source has, or will ever have, live object field layouts to read. Building
it now would be exactly the kind of speculative work this project's own conventions reject.

**Recommendation: don't build it. Split the surface into two tiers instead.**

## Two-tier capability surface design

### Tier 1 — SDK-safe, source-neutral, ships in `DumpDetective.Sdk` + `DumpDetective.Platform`

Covers every analyzer's coarse heap/runtime traversal. Each surface is a capability-gated interface
`AnalysisContext` exposes as `TSurface?` (nullable — absent means the capability wasn't available,
same pattern `IHeapAnalysisCache.TryGetReverseIndexProvider()` etc. already use today), keyed to an
existing `CapabilityVocabulary` entry:

| Surface | Capability | Replaces |
|---|---|---|
| `IHeapObjectStream` | `heap.objects` | `heap.EnumerateObjects()` — streams `HeapObjectRef(Address, TypeRef, Size)`, the SDK-typed equivalent of today's `HeapEntry`, using `TypeRef` for identity instead of a raw `MethodTable` |
| `IHeapObjectLookup` | `heap.objects` | `heap.GetObject()`, `IHeapAnalysisCache.TryGetObjectMetadata` |
| `IHeapRootQuery` | `heap.roots` | `heap.EnumerateRoots()`, `GetStaticRootedAddresses`, `GetPinnedRootedAddresses`, `GetStaticFieldsByRootAddress`, `TryResolveStackFrameOwner` |
| `IHeapHandleQuery` | `heap.handles` | `.EnumerateHandles()` |
| `IHeapSegmentQuery` | `heap.segments` | `heap.GetSegmentByAddress()`, `SegmentKindMapper` |
| `IHeapFinalizerQueueQuery` | `heap.finalizer-queue` | `heap.EnumerateFinalizableObjects()` |
| `IHeapSyncBlockQuery` | `runtime.locks` — already declared, unconsumed until this surface (not a new `heap.sync-blocks`; see the "New SDK types" section below) | `heap.EnumerateSyncBlocks()` (`LockGraphAnalyzer`) |
| `IHeapTypeStatisticsQuery` | `heap.types` | `GetOrBuildTypeStatistics`, `TryGetDistinctMethodTables`, `TryGetGlobalSizeBuckets` |
| `IHeapReferenceQuery` / `IHeapReverseReferenceQuery` | `heap.references` / `heap.reverse-references` | `TryGetForwardIndexProvider`/`TryGetReverseIndexProvider`, re-signed to take `ulong`/`TypeRef` instead of `ClrHeap` |
| `IHeapReachabilityQuery` | `heap.reachability` (new, split from `heap.dominators` 2026-09-10 — see [phase-1-sdk-review-findings.md](phase-1-sdk-review-findings.md) item 4) | `TryGetReachableAddressProvider` — a Stage A product, independently gated from Stage B below |
| `IHeapDominatorQuery` | `heap.dominators` (new capability, narrowed 2026-09-10 to Stage B only) | `TryGetDominatorTreeProvider`, `TryGetThreadRetentionProvider` |
| `IRuntimeThreadQuery` | `runtime.threads` | `Runtime.Threads`, thread-stack-root counting |
| `IRuntimeModuleQuery` | `runtime.modules` | `Runtime.AppDomains`/module enumeration |
| `IRuntimeJitQuery` | `runtime.jit` | `Runtime.EnumerateJitManagers()` |

All of this is a real, mechanical re-signature of `IHeapAnalysisCache` plus the raw `ClrHeap`/
`ClrRuntime` calls analyzers make today — every one of the above already exists in some form in
`Sources.ClrDump`-to-be code; nothing here is new capability, only a new source-neutral shape for
capability already provided.

### Tier 2 — stays dump-specific, never enters the SDK

A new capability, e.g. `dump.object-fields`, exposing something close to today's raw
`ClrType`/`ClrObject` field API (`TryReadField(ulong address, string fieldName, out FieldValue)`,
where `FieldValue` can be primitive/string/nested-object-address). Lives in `Sources.ClrDump`
(dump-only), not `DumpDetective.Sdk`. Any analyzer that needs it declares
`RequiresCapability("dump.object-fields")` alongside its `heap.*` requirements — meaning it
correctly never runs on a source that can't provide it, which is honest: `WcfChannelAnalyzer`,
`SqlCommandAnalyzer`, etc. are reading live in-memory object layouts a trace fundamentally does not
carry. This is not a shortfall of the capability model, it's the model working as intended — the
same reasoning [phase-3-plugin-packaging.md](phase-3-plugin-packaging.md) already applies to
`Plugins.Cpu` having zero dump-fed analyzers, mirrored.

Splitting each of the 35 analyzers by tier (grep-derived, not exhaustively verified line-by-line —
confirm per-analyzer during actual migration):

- **Tier 1 only** (~11): `GCGenerationAnalyzer`, `GCRootAnalyzer`, `GCHandleAnalyzer`,
  `SegmentReservationAnalyzer`, `LohFragmentationAnalyzer`, `MemoryAnalyzer`,
  `HeapTopologyAnalyzer`, `ThreadAnalyzer`, `JitAnalyzer`, `LockGraphAnalyzer`,
  `ThreadStackClusterAnalyzer`.
- **Tier 1 + Tier 2** (~24): everything doing field introspection — `WcfChannelAnalyzer`,
  `HttpObjectAnalyzer`, `DbConnectionAnalyzer`, `SqlCommandAnalyzer`, `SqlTransactionAnalyzer`,
  `SqlConnectionPoolAnalyzer`, `TimerLeakAnalyzer`, `CrashAnalyzer`, `ObjectShapeAnalyzer`,
  `CollectionAnalyzer`, `StringAnalyzer`, `BoxingAnalyzer`, `ArrayAnalyzer`,
  `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer`, `EventLeakAnalyzer` (+ its
  `EventLeak/*` helpers), `WeakReferenceAnalyzer`, `StaticRootLeakDetector`, `DominatorAnalyzer`,
  `ReferenceChainAnalyzer`, `HangAnalyzer`,
  `LeakCandidateAnalyzer`, `FinalizableObjectAnalyzer`, `ModuleAnalyzer`.

  **Four corrections to this original grep-derived split, all found during actual migration, not
  guessed at up front** (the split's own caveat above, now cashed in four times):
  `FinalizableObjectAnalyzer` (found while scoping Batch 5) reads a live object's "disposed" `bool`
  field via `ClrInstanceField.Read`; `ModuleAnalyzer` (found while scoping the batch after Batch 7)
  reads a live `AssemblyLoadContext` object's `"_name"` field via `ClrObject.TryReadStringField` to
  resolve its display name. Both are unambiguous field-*value* extraction, not the coarse/structural
  queries a Tier-1 surface covers — both moved from Tier-1-only into Tier-1+Tier-2 above, and both
  wait on the `dump.object-fields` escape hatch, which **is now built** (see "Tier 2 wave" below,
  first shipped 2026-09-11 for the pilot, `SqlConnectionPoolAnalyzer`) — neither has been migrated
  yet, but the hatch itself is no longer the blocker for either.
  Conversely, `LockGraphAnalyzer` and `ThreadStackClusterAnalyzer` (found while planning the
  thread-domain quartet — see
  [phase-1-thread-quartet-plan.md](phase-1-thread-quartet-plan.md)) were originally bucketed into
  Tier 1 + Tier 2 but read no field values at all — moved into Tier-1-only above.

## New SDK types — shipped 2026-09-09, additive, zero behavior change

Step 1 below is now done: `src/DumpDetective.Sdk/Analysis/` exists with all 13 Tier-1 interfaces
from the table above, `HeapObjectRef`, `AnalysisContext`, `IAnalyzer`, and the three attributes.
Nothing existing was touched; all 1216 non-real-dump tests and the architecture-conformance suite
pass unchanged.

**Correction to this plan's own earlier claim about `AnalysisOptions`**: reading the real type
before mirroring it (rather than assuming from its name, the mistake this project's conventions
exist to catch) found it's not a small plain-data record — `Core.Options.AnalysisOptions` is a
23-sub-record, reflection-keyed options bag (`RetentionOptions`, `EventLeakOptions`,
`CrashAnalysisOptions`, 20 more, plus a reflection-based `TryGet<T>`), one per analyzer domain.
Mirroring it wholesale into the SDK now would mean porting 23 more types on a guess, not a measured
need. **`AnalysisContext` therefore does not carry `AnalysisOptions`/`DiagnosticsOptions`/
`IAnalysisDiagnosticsSink` in this first cut** — left out per this project's hard-need-basis
convention until the pilot migration (step 3 below) shows what a capability-scoped analyzer actually
needs from it. `AnalyzerProgressReport` *was* mirrored (small, same shape as `Platform.IndexProgress`,
genuinely needed by nearly every heap-scanning analyzer for progress reporting on large dumps).

**Correction: `IAnalyzer.AnalyzeAsync` returns bare `ValueTask`, not
`ValueTask<AnalyzerDomainResult>`.** `AnalyzerDomainResult` itself still lives in `Core.Models`, not
the SDK, and every analyzer's concrete result type derives from it and is consumed throughout
`Reporting`. Moving the base type is its own decision (does `Core`'s type become a re-export of an
SDK one, or does every consumer retarget?) — left for the pilot migration to force a real answer
against, not guessed at here.

**Capability vocabulary gap found and fixed while wiring `IHeapDominatorQuery`/
`IHeapSyncBlockQuery`**: added `heap.dominators` (genuinely new) to `CapabilityVocabulary`, and
initially added a new `heap.sync-blocks` for `LockGraphAnalyzer`'s `EnumerateSyncBlocks` usage
before discovering `runtime.locks` was already declared, unconsumed, for exactly this — backed out
the duplicate and wired `IHeapSyncBlockQuery` to the existing capability instead.
`capability-registry.json` bumped to 1.1.0 accordingly; `SdkRegistryConformanceTests` caught the
registry/vocabulary drift immediately, as designed.

- `Analysis/IAnalyzer.cs` — same member shape as `Core.Abstractions.IAnalyzer` today
  (`Name`/`Category`/`Tags`/`Order`/`IsThreadSafe`/`AnalyzeAsync`/`Dispose`), retargeted to the new
  `AnalysisContext`, with the `AnalyzerDomainResult` return-type question left open per the
  correction above.
- `Analysis/AnalysisContext.cs` — capability-resolved: nullable properties per Tier-1 surface above
  (`IHeapObjectStream? HeapObjects { get; }`, etc.), plus `IObservationSink` and progress reporting.
- `Analysis/RequiresCapabilityAttribute.cs`, `OptionalCapabilityAttribute.cs`,
  `AnalyzerModuleAttribute.cs` — as already named in the target shape.
- `Presentation/IAnalyzerSectionBuilder.cs` — moved once analyzers are far enough along that
  section builders can target the new domain-result shape; not needed for the retyping step itself
  and can trail it.

This part is safe to build now: purely additive, zero behavior change, matches Phase 0/1's own
rule, touches no existing analyzer.

## The retyping step itself — detailed plan

This is the piece [modularity-plan.md § 10 point 8](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)
found no phase owns. Concrete sequencing:

1. **Ship Tier-1 SDK surfaces + new `IAnalyzer`/`AnalysisContext`/attributes, additively. Done
   2026-09-09** — see the correction notes above for exactly what shipped vs. was deliberately left
   open. **Their dump-side implementations (the actual ClrMD-backed classes implementing the 13
   Tier-1 interfaces) are not yet built** — this step shipped only the SDK-side contracts, which is
   as far as "additive, zero behavior change, no existing analyzer touched" can go without starting
   to touch `Sources.ClrDump`-to-be code. Old `Core.Abstractions.IAnalyzer`/`Models.AnalysisContext`
   untouched; the existing pipeline keeps running exactly as today.
2. **Build one bridging adapter**, e.g. `LegacyAnalyzerAdapter : Core.Abstractions.IAnalyzer`,
   wrapping an `Sdk.Analysis.IAnalyzer` and translating the old `AnalysisContext` into the new one
   per call. This lets the existing pipeline run a new-style analyzer with zero pipeline changes,
   so step 3's pilot doesn't also have to prove pipeline-wiring changes at the same time. **Done
   2026-09-10** — see pilot notes below; `LegacyAnalyzerAdapter<TSdkAnalyzer>` lives in
   `DumpDetective.Analysis/Sdk/`.
3. **Pilot: migrate exactly one Tier-1-only analyzer. Done 2026-09-10 — `GCGenerationAnalyzer`.**
   The original "two `heap.GetTypeByMethodTable` calls, no field introspection" premise turned out
   wrong on inspection — checking six of the eleven Tier-1-only candidates
   (`GCGenerationAnalyzer`, `GCHandleAnalyzer`, `FinalizableObjectAnalyzer`, `ModuleAnalyzer`,
   `JitAnalyzer`, `SegmentReservationAnalyzer`) found every one needs data richer than the shipped
   Tier-1 interfaces exposed (generation-segmented per-type counts, dominator retained bytes,
   root-path search, AppDomain/module cross-referencing, or per-thread JIT stack walks) — there was
   no analyzer that migrates "for free." Proceeded with `GCGenerationAnalyzer` anyway since the
   gap was already fully field-mapped. What actually shipped, beyond what this plan anticipated:
   - **`IHeapTypeStatisticsQuery`/`HeapTypeStatistics` extended**, not just implemented: added
     `Gen0Count`/`Gen1Count`/`Gen2Count`/`LohCount`/`LohSize`/`Gen2TotalSize`/`IsFinalizableType`
     per type, a `HasExactGenerationData` flag (mirrors the old fast-index-vs-fallback fork), and
     `GetExactGenerationByteTotals()` (segment-based, always available). Sized to exactly what
     `GCGenerationAnalyzer` measurably needed, not speculatively further.
   - **`Sdk.Analysis.AnalysisContext` gained `AnalyzerOptions` (`object?`)** — a single untyped
     slot for an analyzer's own options record, since `AnalysisOptions` deliberately wasn't ported
     (see that type's own remarks). One slot, not a keyed bag — only one analyzer needs it so far.
   - **The `AnalyzerDomainResult` hand-off question `Sdk.Analysis.IAnalyzer`'s own remarks left
     open — SDK's `AnalyzeAsync` returns bare `ValueTask`, and the SDK can't reference
     `AnalyzerDomainResult` at all — got its answer**: a legacy-bridge-only
     `IProducesAnalyzerDomainResult` interface (`DumpDetective.Analysis/Sdk/`, not part of the SDK)
     that a migrated analyzer implements alongside `Sdk.Analysis.IAnalyzer`, setting `LastResult` as
     the last step of `AnalyzeAsync`; `LegacyAnalyzerAdapter` reads it back out. Retires once Phase 5
     gives analyzers a real way to report results.
   - **Dump-side `heap.types` implementation** (`HeapTypeStatisticsQuery`) lives in
     `DumpDetective.Analysis/Sdk/`, not a `Sources.ClrDump` project — that project doesn't exist yet;
     written to move unchanged once it does. `DumpDetective.Analysis` now references
     `DumpDetective.Sdk` (updated `DependencyDirectionTests`).
   - **Every per-analyzer subclass (`LegacyAnalyzerAdapter<TSdkAnalyzer>` and
     `IProducesAnalyzerDomainResult`) had to be made public, not internal** — `AnalyzerBenchmarkBase<T>`'s
     `where T : IAnalyzer, new()` constraint needs a public parameterless constructor on the concrete
     adapter subclass, which forces its base and the base's own generic constraints to be at least as
     accessible (CS0060/CS0703). Affects every future per-analyzer adapter the same way.
   - **Known, accepted gap, documented on the type itself, not fixed speculatively**: the index-path
     dictionary is keyed by resolved type *name*, not `MethodTable` — two distinct method tables
     resolving to the same display name would collide (last write wins). Rare, unexercised by the
     characterization test, left for a real dump to surface if it ever does.
   - **Gate met**: no prior per-analyzer unit test existed for `GCGenerationAnalyzer` to diff
     against, so a new one was written —
     `tests/.../GCGenerationAnalyzerRetypingCharacterizationTests.cs`, covering both the
     fast (exact-generation) and fallback paths, hand-computing expected arithmetic from injected
     fixture data (real method tables from a live self-attached test-process heap, per the existing
     `LiveHeapSnapshotFixture`/`AnalysisPipelineTests.InjectHeapIndex` patterns) rather than diffing
     against the old implementation, which no longer exists in-tree to diff against. Full
     non-real-dump suite (1229 tests) passes unchanged.
   - Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `GCGenerationAnalyzerBenchmark`,
     `FullPipelineBenchmark`, `SmallDumpLatencyBenchmark` all now construct
     `GCGenerationAnalyzerLegacyAdapter`, never `GCGenerationAnalyzer` directly.
   - **Real-dump verification (step 5 below) done 2026-09-10** — new `[DiscrepancyFact]`
     `GCGenerationAnalyzerRealDumpTests` (one dump, foreground, per the project's one-at-a-time
     real-dump rule) runs the adapter through a real `HeapAnalysisCache.PrebuildHeapIndex` scan of
     the 3.5 GB `Crash_IIS_BALTSTPRD` reference dump — hundreds of thousands of distinct real types,
     not the synthetic 3-type fixture above. Passed in 42s: index-backed (non-fallback) path taken,
     no exceptions, internally consistent output (`SohTotal` identity, LOH-sorted-descending,
     `FinalizableGen2Count`/`Bytes` cross-checked against the per-type profile list). Step 3 (pilot)
     is now fully closed, including its real-dump gate.
4. **Batch the remaining 34, Tier-1-only first, 3–5 analyzers per batch/session**, each batch gated
   the same way as the pilot. Tier-1+Tier-2 analyzers wait until the `dump.object-fields` escape
   hatch (Tier 2) exists — build that once the first Tier-1+Tier-2 analyzer is reached, not
   speculatively up front.

   **Batch 1: `SegmentReservationAnalyzer`. Done 2026-09-11.** Sized down from the plan's own 3–5
   suggestion to one analyzer this session — checking `HeapTopologyAnalyzer` (its natural pairing,
   since both already share one `HeapAnalysisCache.GetOrBuildSegmentSummaries` pass) found it needs
   materially more beyond the segment surface built here: per-segment object enumeration scoped to
   POH/Frozen segments only (for its per-type breakdown), and cross-referencing the heap index's
   `TypeAggregates`/`ObjectCount` for exact SOH derivation. Queued for the next batch rather than
   rushed into this one.
   - **`IHeapSegmentQuery`/`HeapSegmentRef` extended**, not just implemented: added
     `Address` (verified via decompiling the installed ClrMD package that this is genuinely distinct
     from `Start`/`End` — the segment object's own address, not the object range it holds), `Kind`,
     `RegionKind` (two new SDK enums, `HeapSegmentKind`/`RegionGenerationKind`, mirroring the
     dump-side ones 1:1), `CommittedBytes`, `ReservedBytes`, `LogicalHeapIndex`, `IsEphemeral`,
     `Gen0Bytes`/`Gen1Bytes`/`Gen2Bytes`, plus `DumpPointerSize` and `IsServerGc` on the interface
     itself (heap-wide facts bundled alongside segment enumeration for the same reason
     `IHeapTypeStatisticsQuery` already bundles `HasExactGenerationData`/exact-gen-bytes — no
     better-fitting existing capability). Sized to exactly what `SegmentReservationAnalyzer`
     measurably needed, deliberately including the extra fields `HeapTopologyAnalyzer` will also
     need (its shared `SegmentSummary` pass means this surface should already cover it), not
     further.
   - **Dump-side `heap.segments` implementation** (`HeapSegmentQuery`) + a new
     `SdkSegmentKindMapper` centralizing the two-way SDK-enum ↔ dump-side-enum mapping (both
     directions are real call sites: the dump-side implementation maps dump→SDK, the retyped
     analyzer maps SDK→dump since its `*DomainResult` output is unchanged and still dump-side-typed)
     — both in `DumpDetective.Analysis/Sdk/`.
   - **New recurring gotcha, will hit again on every future segment/type-adjacent analyzer**:
     `DumpDetective.Analysis.Models` and `DumpDetective.Sdk.Analysis` deliberately declare
     same-named enums (`HeapSegmentKind`, `RegionGenerationKind`) — any file needing both
     unqualified must alias one side (`using DumpHeapSegmentKind = ...`) to avoid CS0104. Distinct
     from the earlier `DumpDetective.Analysis.Sdk` vs. `DumpDetective.Sdk` *namespace* collision
     found during the pilot — this one is a *type-name* collision between two namespaces that don't
     shadow each other.
   - **Gate met**: new `SegmentReservationAnalyzerRetypingCharacterizationTests` — unlike the
     pilot's hand-crafted-fixture approach, `SegmentSummary` wraps a real, live `ClrSegment` that
     can't be synthesized via reflection injection, so this cross-checks the retyped output directly
     against ClrMD ground truth (`heap.Segments`, `DataReader.PointerSize`, `heap.IsServer`) on a
     self-attached live process heap, plus internal-consistency checks (per-kind/per-heap sums equal
     totals, segment table sorted descending, fill % in range). New
     `SegmentReservationAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground) repeats
     the same cross-check against the real 3.5 GB reference dump — passed, 8 segments, Server GC,
     classic (non-regions) layout, 707 ms. Full non-real-dump suite (1230 tests) passes.
   - Call site updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
     `SegmentReservationAnalyzerLegacyAdapter`. No benchmark referenced this analyzer directly
     (unlike the pilot's three benchmark call sites).

   **Batch 2: `HeapTopologyAnalyzer`. Done 2026-09-11.** The pairing Batch 1 queued — its segment
   needs were already covered by Batch 1's `IHeapSegmentQuery` extension; what it needed beyond that:
   - **`IHeapSegmentQuery` extended again**: `EnumerateObjects(HeapSegmentRef)` (per-segment object
     enumeration — this analyzer walks LOH/POH/Frozen/Unknown segments individually, deliberately
     never the whole heap, since SOH's ~87M+ objects are never walked per-object at all) and
     `LogicalHeapCount`. `EnumerateObjects` excludes invalid/free/untyped objects at the capability
     boundary (documented side effect: progress-report cadence can differ slightly from the
     pre-retyping version on segments with free blocks — the domain-result fields it feeds do not,
     since progress is a side channel).
   - **`IHeapTypeStatisticsQuery` extended**: `ExactObjectCount`/`GetTotalIndexedBytes()`, computed
     directly from the index (not derived from `GetTypeStatistics()`'s name-keyed dictionary, to
     avoid inheriting that dictionary's documented method-table-collision gap) — needed for the
     exact-SOH-object/byte derivation this analyzer already did pre-retyping.
   - **`HeapObjectRef` gained `TypeDisplayName`**, a real, if narrow, SDK-wide implication: its
     existing `Type: TypeRef` field is a *canonical* identity — cross-source join key, deliberately
     rewritten by `EntityCanonicalizer` for compiler-generated names (async state machines unwrap to
     their declaring method, closures/lambdas lose their ordinal). This analyzer's per-type
     POH/Frozen breakdown needs the dump's exact display name unchanged (it's a report label, not a
     join key) — using `CanonicalName` instead would have silently renamed exactly the type names a
     report must show verbatim. First real construction site for `HeapObjectRef`
     (`HeapSegmentQuery.EnumerateObjects`); no prior caller existed to break.
   - New shared `SdkTypeRefFactory` (`DumpDetective.Analysis/Sdk/`) — the one `EntityCanonicalizer`
     call site for every dump-side Tier-1 implementation, replacing `HeapTypeStatisticsQuery`'s
     private copy.
   - **Gate met**: new `HeapTopologyAnalyzerRetypingCharacterizationTests` — two tests, one exercising
     the no-index fallback (catching a real test-authoring mistake along the way: `SohObjects` is
     `-1`, not `0`, when no index is available — a pre-existing sentinel-propagation quirk the
     assertion initially got wrong, not a retyping regression, fixed once traced), one exercising the
     exact-SOH-derivation branch via a *synthetic* injected index sized from real ClrMD ground-truth
     counts (deliberately not a real `PrebuildHeapIndex` scan against the test process's own live
     heap — avoids that scan's disk side effects and runtime cost for a assertion that only needs
     the derivation arithmetic, not a real index). New `HeapTopologyAnalyzerRealDumpTests`
     (`[DiscrepancyFact]`, one dump, foreground) repeats the ClrMD ground-truth cross-check against
     the real 3.5 GB reference dump with a real prebuilt index — passed: 8 segments, ~1.46M SOH
     objects derived exactly, 4 LOH segments, Server GC across 4 logical heaps, 2 s. Full
     non-real-dump suite (1232 tests) passes.
   - Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
     `SmallDumpLatencyBenchmark` (both benchmarks already needed the same fix for the pilot's
     `GCGenerationAnalyzer` — same two files, same pattern, second time).
   **Batch 3: `GCRootAnalyzer`. Done 2026-09-11.** Chosen over the other seven remaining Tier-1-only
   analyzers because its needs mapped 1:1 onto capabilities the SDK had already declared but never
   implemented dump-side (`IHeapRootQuery`, `IHeapDominatorQuery`, `IHeapReferenceQuery`,
   `IHeapObjectLookup`) — each a thin pass-through over an already-existing
   `IHeapAnalysisCache`/`IDominatorTreeProvider` method, the same low-risk shape as the pilot's
   `HeapTypeStatisticsQuery`. Four brand-new dump-side implementations shipped (all in
   `DumpDetective.Analysis/Sdk/`), none of them Tier 2 — root enumeration, dominator-tree lookups, and
   forward reference walks are all structural (address/type/size), never field-value extraction.
   - **`HeapRootRef` extended**: added `AppDomainId` — the pre-retyping analyzer's static/thread-static
     field-description string included a `[AppDomain#N]` suffix for non-default AppDomains
     (`GetStaticFieldsByRootAddress`'s `AppDomainId` tuple member), which `OwnerTypeName`/`FieldName`
     alone couldn't reproduce.
   - **`IHeapSegmentQuery` extended a third time**: `GetGeneration(address)` and a new
     `HeapGenerationTag` enum (Gen0/Gen1/Gen2/Loh/Poh/Frozen/Unknown, mirroring dump-side
     `GenerationTag` 1:1) — a genuinely different primitive from the existing `HeapSegmentRef` fields:
     per-*object*, not per-segment (an ephemeral/Workstation-GC segment holds Gen0/Gen1/Gen2 objects
     together, so two objects on the same segment can report different tags). Needed for this
     analyzer's by-kind Gen0/Gen1/Gen2/LOH fraction breakdown. Implemented as a faithful, deliberately
     separate port of the existing `Traversal.Dominator.GenerationTagResolver` rather than a shared
     call site — that resolver has its own `ClrHeap`-shaped caller and no benefit from coupling the two.
   - **New `HeapDominatorQuery`/`HeapRootQuery`/`HeapReferenceQuery`/`HeapObjectLookup`** — the first
     three are pure pass-throughs (no branching beyond an optional-provider null check); `HeapRootQuery`
     is the only one with real logic (byte-kind → `HeapRootKind` mapping, lazy static-field resolution).
     `HeapDominatorQuery` is constructed with an already-resolved `IDominatorTreeProvider` (not a
     `Func`/cache reference) precisely so `AnalysisContext.HeapDominators` itself can be the single
     "is Stage B available for this session at all" gate — matching the pre-retyping analyzer's own
     one-time `treeProvider is not null` check exactly, rather than re-deriving per-call.
   - **`LegacyAnalysisContextTranslator` now populates five capabilities**, not two — the first batch to
     grow it since the pilot.
   - **Preserved but not reused**: the pre-retyping analyzer's `BoundedGraphWalk`/
     `RetainedSizeCandidateSelector`/`DominatorRetainedSetAggregator`/`GCRootAnalysisProjection` helpers
     all take a raw `ClrHeap`/`IHeapAnalysisCache` and could not be called from SDK-side code; their BFS
     and dominator-chain-walk algorithms were ported as private methods on the new `GCRootAnalyzer`
     against the capability interfaces instead, not deleted (still used by not-yet-migrated Tier
     1+Tier 2 analyzers `ReferenceChainAnalyzer`/`StaticRootLeakDetector`/`FinalizableObjectAnalyzer`).
   - **One accepted, documented simplification**: dropped `RetainedSizeCandidateSelector`'s
     `MethodTableHasOutgoingRefs` shape pre-filter (an optimization skipping a walk for known-leaf
     types) — since this analyzer's walk was already always uncapped
     (`maxCandidatesToWalk: walkCandidates.Count`), dropping the filter costs at most one extra
     negligible-size BFS iteration per leaf candidate and changes no output value.
   - **One accepted, narrow fidelity gap**: `HeapObjectLookup`'s fallback type name for an
     unresolvable method table uses the SDK-wide `MT:0x{mt:x}` convention (matching
     `HeapTypeStatisticsQuery`/`HeapSegmentQuery`), not this one analyzer's pre-retyping
     `0x{address:X}` fallback — a practically-unreachable case (a root's target method table failing
     to resolve at all), accepted for one shared lookup surface's naming consistency.
   - **Marker-interface relocation**: the pre-retyping analyzer implemented
     `IRequiresReachableGraphIndex`/`IRequiresDominatorTreeIndex` directly — pipeline gating
     (`DiskBackedObjectIndexWriter.Build`'s Stage B build check) inspects the *registered*
     `Core.Abstractions.IAnalyzer` instances, so these markers moved to
     `GCRootAnalyzerLegacyAdapter` instead of the inner SDK analyzer, which the pipeline never sees
     directly. Missing this would have silently stopped Stage B from being pre-built for this
     analyzer's benefit.
   - **Gate met**: new `GCRootAnalyzerRetypingCharacterizationTests` — like the segment batch, root
     data can't be hand-crafted via reflection injection (a `HeapRootRef`'s target is a real, live
     address), so it cross-checks internal-consistency invariants (count/byte accounting, sort order,
     fraction bounds) on a self-attached live process heap, with a small fully-controlled
     `TypeAggregateIndexEntry` fixture only to make the "% of managed heap" arithmetic independently
     recomputable; a second test confirms the pre-retyping empty-result gate (no heap index → no
     analysis) still holds. New `GCRootAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump,
     foreground, no Stage B requested explicitly) passed against the real 3.5 GB reference dump —
     discovered along the way that this dump's already-persisted `cache.bin` carries Stage B from
     earlier work regardless, so the test asserts internal consistency under *either* exact-or-shallow
     state rather than assuming one. `DominatorAnalyzerExactTreeRealDumpTests` (pre-existing, updated
     to use `GCRootAnalyzerLegacyAdapter` instead of constructing `GCRootAnalyzer` against the old
     context type directly) re-ran standalone against the same dump with Stage B explicitly built —
     passed, ~100 s. Full non-real-dump suite (1234 tests) passes.
   - Call site updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
     `GCRootAnalyzerLegacyAdapter`. No benchmark referenced this analyzer directly.

   **Batch 4: `MemoryAnalyzer`. Done 2026-09-11.** Picked as the natural pairing to Batch 3 — its
   retained-size walk reuses the exact same `IHeapObjectLookup`/`IHeapReferenceQuery` primitives
   `GCRootAnalyzer` just built, and its segment/type needs mostly reuse surfaces Batches 1–3 already
   shipped.
   - **`HeapTypeStatistics` extended**: added `SampleAddress` and `ModuleName` — carried alongside
     each type's stats rather than as a separate by-name lookup method, so a caller already holding a
     `HeapTypeStatistics` never needs a second round trip. `ModuleName` is `string` (not `string?`,
     matching the dump-side `CachedTypeStatistics.ModuleName` field it mirrors) — the `null`
     normalization for "unresolved" happens at the analyzer/report boundary, same place it already
     did pre-retyping.
   - **`IHeapSegmentQuery` extended a fourth time**: `CanWalkHeap` (heap-wide, bundled alongside
     `DumpPointerSize`/`IsServerGc` for the same reason those are) — replaces `ClrHeap.CanWalkHeap`,
     gating whether the retained-size enrichment runs at all. `EnumerateObjects` gained an
     `includeFree` parameter (default `false`, preserving `HeapTopologyAnalyzer`'s existing call
     unchanged) — needed because the pre-retyping LOH fragmentation ratio summed *every* valid
     object on a LOH segment, free pseudo-objects included, to derive a free-byte delta against
     committed bytes; `EnumerateObjects` had excluded free objects since Batch 2, and silently
     reusing it as-is would have changed this metric's actual values, not just its plumbing.
     Preserved exactly rather than "fixed" during a retyping-only change.
   - **Extracted `CapabilityBoundedGraphWalk`** (`DumpDetective.Analysis/Sdk/`) — the
     capability-scoped counterpart to `Traversal.BoundedGraphWalk`, holding
     `ComputeExclusiveRetained`/`CollectForwardTypeNames` ported against
     `IHeapObjectLookup`/`IHeapReferenceQuery`. Batch 3 had ported these as private methods on
     `GCRootAnalyzer`; with `MemoryAnalyzer` now a second real consumer of the exact same walk, they
     were pulled out and `GCRootAnalyzer` refactored to call the shared copy instead of carrying its
     own — extracted once a second consumer existed, not speculatively ahead of one.
   - **Reused, not re-derived**: `Utilities.MemoryAnalysisProjection.Build` (histogram bucketing,
     top1/5/10 bytes, all four pressure scores) is pure arithmetic over
     `Dictionary<string, CachedTypeStatistics>` — no `ClrHeap`/`IHeapAnalysisCache` dependency at
     all — so it's called unchanged via a small bridge dictionary built from
     `IHeapTypeStatisticsQuery.GetTypeStatistics()`, rather than ported the way `GCRootAnalyzer`'s
     dominator/BFS logic had to be (that logic took `ClrHeap`/`IHeapAnalysisCache` directly and
     couldn't be called from retyped code unchanged). `DumpDetective.Analysis` — unlike
     `DumpDetective.Sdk` — has no restriction against referencing dump-side, ClrMD-touching types, so
     this reuse is a plain internal call, not a layering violation. This cut the amount of new
     arithmetic needing a from-scratch port (and re-verification) by roughly 130 lines.
   - **One accepted simplification, same shape as Batch 3's**: dropped the pre-retyping selector's
     `MethodTableHasOutgoingRefs` shape pre-filter for the same reason — this analyzer's walk was
     always uncapped (`maxCandidatesToWalk: walkCandidates.Count`), so skipping it costs at most one
     extra negligible-size BFS iteration per leaf candidate and changes no output value.
   - **One discovered, preserved quirk**: pre-retyping `GetSegmentUsedBytes` computed a value
     byte-identical to committed bytes (both derive from the same `CommittedMemory` range) — so
     `GCSegmentSummary.UsedBytes` has always equaled `CommittedBytes` exactly, not a net-of-free-space
     figure the field name might suggest. Preserved as-is (not fixed) for the same "byte-identical
     through retyping" reason as the free-object inclusion above; both are pre-existing
     characteristics of the shipped analyzer, not something this batch introduced or is scoped to
     correct.
   - **Gate met**: new `MemoryAnalyzerRetypingCharacterizationTests` — the no-index (live heap scan)
     path only, self-attached process, cross-checking segment byte totals against ClrMD ground truth
     (segment/type data can't be hand-crafted via reflection injection, same reasoning as Batches 1–3)
     plus internal-consistency invariants (type byte/count sums, pressure-score bounds, walked-count
     ≤ 20). New `MemoryAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground, real
     prebuilt index) passed against the real 3.5 GB reference dump. Re-ran
     `GCRootAnalyzerRealDumpTests` standalone afterward too, since the `CapabilityBoundedGraphWalk`
     extraction touched `GCRootAnalyzer`'s internals — still passes. Full non-real-dump suite
     (1235 tests) passes.
   - Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
     `SmallDumpLatencyBenchmark`, `MemoryAnalyzerBenchmark` (retargeted at
     `AnalyzerBenchmarkBase<MemoryAnalyzerLegacyAdapter>` and `AnalyzeAsync`, the same fix
     `GCGenerationAnalyzerBenchmark` needed for the pilot — its old `Analyze(heap, cache)`
     convenience overload doesn't exist on the retyped analyzer).

   **Batch 5: `JitAnalyzer`. Done 2026-09-11.** Picked over the other four remaining Tier-1-only
   analyzers (`GCHandleAnalyzer`, `ModuleAnalyzer`, `ThreadAnalyzer`, `LohFragmentationAnalyzer`)
   because its own analyzer logic is a single linear accumulation pass — no BFS, no dominator tree,
   no disk-format satellite-file reads — even though its capability gap turned out to be the largest
   of any batch so far: this is the first batch touching the `runtime.*` side of the SDK at all, and
   `IRuntimeJitQuery`/`IRuntimeThreadQuery` had zero real consumers or dump-side implementations
   before this. (Re-examining `FinalizableObjectAnalyzer` while scoping this batch found it reads an
   actual field *value* — a `bool` "disposed" field off a live object via `ClrInstanceField.Read`,
   not just field/type shape — meaning it's a Tier-1+Tier-2 analyzer, mis-scoped into the Tier-1-only
   list by the original grep-based split; moved out of consideration for a Tier-1-only batch.)
   - **`IRuntimeJitQuery` redesigned, not just implemented**: the shipped shape
     (`EnumerateJitCompilations()` returning a flat per-method `JitCompilationRef`) had zero callers
     and didn't match reality — `JitAnalyzer` never enumerates JIT-compiled methods directly at all;
     every per-method fact it reports comes from walking thread stacks instead. Replaced with exactly
     what's needed: `JitManagerCount`/`TotalJitHeapBytes`, mirroring
     `ClrRuntime.EnumerateJitManagers()`'s heap-level totals only.
   - **`IRuntimeThreadQuery` extended substantially**: `RuntimeThreadRef` gained `IsAlive` (the
     pre-retyping analyzer's alive-only filtering moved from baked-into-the-stream to the analyzer's
     own responsibility, keeping the stream itself "everything, unfiltered" like other Tier-1
     streams). Added `EnumerateStackFrames(RuntimeThreadRef)` and a new `ThreadStackFrameRef` —
     `HasMethod` distinguishes "not a managed frame" from "a managed frame ClrMD couldn't resolve a
     method for" (a real, if rare, pre-retyping edge case); `DeclaringTypeName`/`ModuleName` are raw
     ClrMD display names (not canonicalized `TypeRef`/`ModuleRef`), same reasoning as
     `HeapObjectRef.TypeDisplayName` (Batch 2) — a report-facing hotspot count needs the exact name,
     not a cross-source join key. `ModuleName` is deliberately the raw, unshortened `ClrModule.Name`
     (typically a full path), matching the pre-retyping analyzer's own module-hotspot key exactly so
     it stays joinable against `ModuleDomainResult`'s equally-raw names.
   - **New dump-side `RuntimeJitQuery`/`RuntimeThreadQuery`** (`DumpDetective.Analysis/Sdk/`).
     `RuntimeJitQuery` computes both totals eagerly in its constructor (a live, non-cached ClrMD
     walk with exactly one real reader). `RuntimeThreadQuery.EnumerateStackFrames` resolves the
     `ClrThread` for a given `RuntimeThreadRef` via a lazily-built `OSThreadId → ClrThread` map
     (built once, not re-scanned per call).
   - **One ClrMD API-shape correction found while implementing, not guessed**: the SDK's
     `IsGCSuspendPending` field (declared speculatively pre-Batch-5, never implemented) doesn't
     correspond to any single `ClrThread` property — reflecting the installed package (ilspycmd
     against v4.0.732401, same verification discipline as prior batches' enum mirrors) found it's
     one bit of the `[Flags] ClrThreadState` enum (`TS_GCSuspendPending`), not a boolean property;
     wired as `(thread.State & ClrThreadState.TS_GCSuspendPending) != 0`.
   - **`LegacyAnalysisContextTranslator` now populates seven capabilities**, adding `RuntimeThreads`/
     `RuntimeJit` to Batch 4's five.
   - **Gate met**: new `JitAnalyzerRetypingCharacterizationTests` — self-attached live process (the
     current test host's own thread stacks can't be hand-crafted any more than segment/root data
     could), checking internal-consistency invariants (frame-count/method-count orderings, sorted-list
     invariants, a live process always having JIT-compiled code and at least one resolvable managed
     frame on its own stack). New `JitAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump,
     foreground) passed against the real 3.5 GB reference dump. Full non-real-dump suite (1236 tests)
     passes — one transient failure in an unrelated, pre-existing self-attached-heap test
     (`WcfChannelAnalyzerLiveHeapTests`) reproduced as a pass in isolation on immediate re-run, not a
     regression from this batch.
   - Call sites updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
     `JitAnalyzerLegacyAdapter`. No benchmark referenced this analyzer directly.

   **Batch 6: `LohFragmentationAnalyzer`. Done 2026-09-11.** Picked over the other three remaining
   Tier-1-only analyzers after finding `ThreadAnalyzer` carries a real, discovered perf-regression
   risk this plan hadn't anticipated: it's the shared stack-walk provider for a "thread-domain
   quartet" (`ThreadAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`, `LockGraphAnalyzer`),
   registered as `IThreadStackScanParticipant` so the pipeline walks every thread's stack exactly
   once and fans the frames out to all four — retyping `ThreadAnalyzer` alone (the other three are
   Tier-1+Tier-2, out of scope for this wave) would mean the pipeline walks stacks twice until the
   rest of the quartet is also retyped, not just a design nicety. Deferred until the quartet can be
   planned together. Of the remaining three, `LohFragmentationAnalyzer` won out over `ModuleAnalyzer`
   (needs an entirely new AppDomain capability domain, nothing today models AppDomains at all) and
   `GCHandleAnalyzer` (the densest remaining analyzer logic — COM RCW tracking, dependent-handle
   source/target pairs, weak-handle generation breakdown) because it directly reuses Batch 4's
   `includeFree` flag, and its "hard" parts — two satellite disk files — turned out to need only
   narrow, bounded additions once actually designed, not a new domain.
   - **A real, pre-existing semantic split found and preserved, not merged away**: the pre-retyping
     analyzer's fast (disk-index) and fallback (live-scan) paths compute *different* things for the
     large-object list and per-type LOH/POH view, not just different implementations of the same
     contract — fast: a size-based `LohCount`/`LohSize` classification across the whole heap, capped
     to a 100-entry captured sample for individual objects; fallback: an unconditional-size,
     LOH/POH-*segment*-scoped aggregation (which can include small pinned objects the fast path's
     size threshold excludes), uncapped. Retyping preserved this split explicitly rather than
     picking one "correct" behavior — see the analyzer's own remarks.
   - **`IHeapSegmentQuery` extended a fifth time**: `EnumerateLohFreeBlocks()` — unlike the split
     above, free-block data *is* identical in substance between modes (every free block, no cap
     either way), so this one hides its fast/fallback fork entirely, the same shape as
     `EnumerateSegments`/`GetTypeStatistics` already do. `EnumerateCapturedLargeObjects()` (disk-only,
     the capped top-100 sample) and a new `HasLohSatelliteIndex` signal — deliberately *not* the same
     flag as `IHeapTypeStatisticsQuery.HasExactGenerationData`, since that one is `true` whenever any
     `HeapIndexBuildResult` exists, including a memory-mode one with no backing directory for these
     LOH-specific satellite files (a real bug caught before it shipped: an earlier draft of this
     batch used `HasExactGenerationData` for both the free-block and large-object/type-profile
     routing, which would have silently returned an empty large-object list in memory mode instead
     of the exact, live-scanned one the pre-retyping analyzer actually produced there).
   - **`HeapObjectRef` gained `IsFree`** — a first-class boolean mirroring `ClrObject.IsFree`, added
     because this batch is the first real consumer of `EnumerateObjects(includeFree: true)`'s output
     needing to tell free and live objects apart per-object (rather than just wanting their combined
     byte total, `MemoryAnalyzer`'s only use in Batch 4); replaces detecting a free object by string-
     comparing `TypeDisplayName` against the literal `"Free"`.
   - **Dump-side `HeapSegmentQuery` extended**, not a new type — same file as Batches 1/2/4's segment
     work. Its own raw free-block disk reader is a deliberate small duplication of the pre-retyping
     analyzer's `ReadFreeBlocks` (which stays on `LohFragmentationAnalyzer` itself, unchanged, for its
     own existing direct unit-test coverage) rather than a shared call site — the two read the same
     format but produce different shapes (aggregated-by-segment vs. raw per-block records), and nothing
     outside the retyped analyzer needs the raw form.
   - **Pure, ClrMD-independent helpers kept as-is, not ported**: `BuildFreeGapHistogram`,
     `BuildKindBreakdown`, `IsLohSegment(GCSegmentKind)`, and `ReadFreeBlocks` all take no
     `ClrHeap`/`IHeapAnalysisCache` at all and have their own direct unit tests
     (`LohFragmentationAnalyzerTests`) — retyping only replaced what actually touched dump types.
   - **Gate met**: new `LohFragmentationAnalyzerRetypingCharacterizationTests` covers only the
     no-index fallback path against a self-attached live process (cross-checked against ClrMD ground
     truth, matching Batches 1–5's approach) — deliberately does not also hand-build a synthetic
     `LohFreeBlockIndex.bin`/`LargeObjectIndex.bin` pair for a fast-path unit test, since faking that
     well-formed binary format correctly is a materially bigger lift than a plain `Dictionary`
     fixture, and the real-dump test below already exercises that path with strictly better (real)
     evidence. New `LohFragmentationAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground,
     real prebuilt index) passed against the real 3.5 GB reference dump. Full non-real-dump suite
     (1237 tests) passes.
   - Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
     `SmallDumpLatencyBenchmark`, `LohFragmentationAnalyzerBenchmark` (retargeted at
     `AnalyzerBenchmarkBase<LohFragmentationAnalyzerLegacyAdapter>`).

   **Batch 7: `GCHandleAnalyzer`. Done 2026-09-11.** Picked over `ModuleAnalyzer` for this round —
   `GCHandleAnalyzer` reuses `IHeapDominatorQuery`/`IHeapObjectLookup`/`IHeapSegmentQuery.GetGeneration`
   already built, plus an already-*declared*-but-unimplemented `IHeapHandleQuery`; `ModuleAnalyzer`
   needs an AppDomain capability domain that doesn't exist in any form yet, a bigger, more novel gap.
   - **`IHeapHandleQuery` redesigned, not just implemented** — same story as Batch 5's
     `IRuntimeJitQuery`: the shipped shape (`HeapHandleRef(Kind, Address, DependentTargetAddress)`,
     zero real consumers) didn't carry a target type at all. Redesigned to
     `HeapHandleRef(Kind, TargetAddress, TargetTypeDisplayName, DependentTargetAddress)`.
   - **A genuinely important, easy-to-miss correctness requirement, found and preserved**:
     `TargetTypeDisplayName` resolves from the handle record's own *captured* method table, not a
     live lookup at `TargetAddress` — this makes it robust to a since-collected weak-handle target
     (a method table is a per-*type* EE structure that outlives any specific collected instance,
     unlike the object at that address). Getting this wrong (e.g. routing through
     `IHeapObjectLookup.TryGetObject(TargetAddress)` instead) would have silently broken type
     attribution for exactly the handles most likely to reference collected objects — weak handles.
     Size resolution (for pinned/async-pinned retained bytes) is correctly *not* given this same
     treatment — it stays address-based via `IHeapObjectLookup`, matching the pre-retyping analyzer's
     own `ResolveSize`, which naturally returns 0 for a collected target (sensible: nothing to
     retain).
   - **One narrow, deliberately un-reused capability call**: dependent-handle source/target
     resolution doesn't reuse `IHeapObjectLookup.TryGetObject` as a plain passthrough — that method
     returns `true` (with a placeholder name) when the resolved method table is 0, but the
     pre-retyping analyzer's own `TryResolveTypeNameStrict` treated a zero method table as
     unresolved, which changes whether an edge counts as resolved or not. Wrapped in a small
     `TryResolveDependentTypeName` that adds back the zero-method-table check.
   - **New dump-side `HeapHandleQuery`** (`DumpDetective.Analysis/Sdk/`) reuses the pre-retyping
     analyzer's own three-tier handle source unchanged (`HeapIndexBuildResult.InMemoryHandleSnapshot`,
     then disk/live `IHandleSnapshotReader`, then a raw `runtime.EnumerateHandles()` last resort —
     the last of which is unreachable through the legacy adapter, since heap/runtime are always
     non-null there, kept only for parity).
   - **Existing pre-retyping test suite required real updates, not just a call-site swap** —
     `GCHandleAnalyzerFunctionalTests` (disk-snapshot injection against fake/unresolvable addresses)
     called the old analyzer's `Analyze(runtime, heap: null, cache)` convenience overload directly.
     Investigating why this worked found `Core.Abstractions.AnalysisContext.Heap` is a *computed*,
     never-null property of a `required Runtime` — meaning the "heap == null" scenario these tests
     exercised was never reachable through the real `AnalyzeAsync` pipeline in the first place, only
     through that now-removed test-only bypass. Ported to run through
     `GCHandleAnalyzerLegacyAdapter` against a real self-attached heap instead (the tests' fake
     target addresses still don't resolve against a *real* heap either, so almost every assertion
     held unchanged); the one exception —
     `Analyze_WithHeapNull_DoesNotTrackDependentHandleTopology`, which asserted dependent handles
     were *not counted at all* without a heap — was rewritten as
     `Analyze_DependentHandleWithUnresolvableAddresses_CountsHandleButProducesNoResolvedEdge`, since
     dependent handles are now counted unconditionally (a live heap is always present through the
     real pipeline) and only the resolved-vs-unresolved edge outcome still varies.
   - **Gate met**: new `GCHandleAnalyzerRetypingCharacterizationTests` — the live
     `runtime.EnumerateHandles()` fallback path (no heap index) against a self-attached process's
     real handle table, cross-checked against ClrMD ground truth for total/per-kind counts (handle
     data can't be hand-crafted via reflection injection any more than other batches' segment/root
     data could) — complementing, not replacing, the pre-existing functional tests' fake-address
     coverage. New `GCHandleAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground, real
     prebuilt index) passed against the real 3.5 GB reference dump. Full non-real-dump suite
     (1238 tests) passes.
   - Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
     `SmallDumpLatencyBenchmark`, `GCHandleAnalyzerBenchmark` (retargeted at
     `AnalyzerBenchmarkBase<GCHandleAnalyzerLegacyAdapter>`).

   **`ModuleAnalyzer` scoped for Batch 8, then deferred, 2026-09-11** — reading the full source
   before starting (not just grepping it, per this project's own convention) found
   `TryResolveAssemblyLoadContext` reads a live `AssemblyLoadContext` object's `"_name"` field via
   `ClrObject.TryReadStringField` to resolve the module's owning load-context display name — Tier 2,
   not Tier 1 (see the corrected tier split above). Deferred alongside `FinalizableObjectAnalyzer`
   rather than either (a) building the `dump.object-fields` escape hatch now, a materially bigger,
   unplanned scope expansion, or (b) retyping the Tier-1 parts and silently dropping
   `AssemblyLoadContextName`/`HasAssemblyLoadContext`/`IsCollectibleAssemblyLoadContext` from the
   report, a real feature loss disguised as a plumbing change. Everything else in `ModuleAnalyzer`
   (module enumeration, AppDomain iteration, `EnumerateTypeDefToMethodTableMap`, assembly-ref
   metadata probing) is genuinely Tier-1-shaped and would still need its own new AppDomain
   capability domain (nothing today models AppDomains) once the Tier-2 hatch exists to unblock the
   rest of it.

   **Remaining Tier-1-only after Batches 3–7: none, standalone.** `ThreadAnalyzer` was deferred for
   the shared-dispatcher reason found scoping Batch 6; every other originally-Tier-1-only analyzer
   is now retyped. Planned in
   [phase-1-thread-quartet-plan.md](phase-1-thread-quartet-plan.md): the shared-dispatcher concern
   turns out to be fully addressable (an adapter can implement `IThreadStackScanParticipant` itself,
   the same way `GCRootAnalyzerLegacyAdapter` already carries `IRequiresDominatorTreeIndex`), and
   re-checking the other three quartet members' full source (not just grepping them) found two more
   tier-split corrections: `LockGraphAnalyzer` and `ThreadStackClusterAnalyzer` are actually Tier-1
   only, both retyped 2026-09-11 (see the thread-quartet plan's §§5–6). `ThreadAnalyzer` itself was
   re-checked too, resolving the thread-quartet plan's own open question: `ClrException.Message`
   (reflected via ilspycmd, not guessed) reads a raw field offset off the exception object, making it
   genuinely Tier 2 — and `ThreadAnalyzer` uses exactly that, in `ThreadExceptionSnapshot.ExceptionMessage`.
   So `ThreadAnalyzer` joins `HangAnalyzer` as the quartet's two genuinely-Tier-2 members, deferred
   alongside `ModuleAnalyzer`/`FinalizableObjectAnalyzer` until the `dump.object-fields` hatch exists —
   **Tier 1 is now fully finished**: every originally-Tier-1-only analyzer, plus both quartet members
   that turned out to be Tier-1-only on re-inspection, are retyped.

## Tier 2 wave — `dump.object-fields` hatch built, seven analyzers migrated so far

With Tier 1 finished, this wave opened the `dump.object-fields` escape hatch (§ "New SDK types"
above) and started migrating the Tier-1+Tier-2 list. Unlike the Tier-1 batches, most of these
analyzers share one discovery shape — "match candidate type names against a known set, read a few
fields per instance" — so a shared adapter base was extracted early (`SqlConnectionPoolAnalyzer`,
the pilot) and reused by every consumer after it, rather than each batch re-deriving its own
scan-participant plumbing the way several Tier-1 batches had to.

- **Pilot: `SqlConnectionPoolAnalyzer`. Done 2026-09-11.** Shipped `IDumpObjectFieldQuery`
  (`dump.object-fields`) and `FieldValue`/`FieldValueKind` (private-ctor + named-factory, not a
  positional record, to avoid the adjacent-same-typed-parameter transposition risk found elsewhere
  in the SDK). Two design smells found during review and fixed same-session: candidates were
  originally threaded through `AnalyzerOptions` (its own documented contract reserves that slot for
  analyzer *configuration*) — fixed via `PrecomputedHeapObjectStream` over `context.HeapObjects`
  instead, mirroring `LockGraphAnalyzerLegacyAdapter`'s existing `PrecomputedRuntimeThreadQuery`
  precedent. The scan-participant plumbing itself was un-abstracted — extracted into
  `TypedResourceCandidateStreamingLegacyAdapter<TSelf, TSdkAnalyzer>` once a second consumer was
  confirmed coming, not speculatively ahead of one.
- **`SqlCommandAnalyzer` + `SqlTransactionAnalyzer`. Done 2026-09-11.** Second/third consumers of the
  shared adapter; found its `CandidateTypeNames: IReadOnlySet<string>` shape too narrow (these two
  classify by namespace-prefix + suffix pattern, not exact-name-set membership) — changed to an
  abstract `IsCandidateType(string)` method.
- **`HttpObjectAnalyzer`. Done 2026-09-11.** Fourth adapter consumer; added
  `IDumpObjectFieldQuery.TryGetStringValue` (a second `dump.object-fields` primitive for chasing an
  object-reference field's *result* into a string, e.g. a `Uri`, which `TryReadField` alone can't
  express) and a `_scanSucceeded` tracking fix to the shared adapter base so a failed shared scan
  degrades to an empty candidate list instead of silently looking like "found candidates." Verified
  against the 3.35 GB reference dump: 102 HTTP-shaped objects.
- **`heap.root-paths` capability. Done 2026-09-11.** Built to unblock `DbConnectionAnalyzer`. Turned
  out to be a thin wrapper over the pre-existing `RootPathFinder`/`ReferenceGraph` machinery, not a
  reimplementation — both real consumers (`DbConnectionAnalyzer`, `TimerLeakAnalyzer`) already agreed
  on every search policy except numeric bounds, so `IHeapRootPathQuery` bakes in the shared policy
  and exposes only `RootPathSearchOptions`' bounds per call, returning an already-formatted path
  string rather than raw addresses.
- **`WcfChannelAnalyzer`. Done 2026-09-11.** Fifth adapter consumer. Found and fixed three real bugs
  by decompiling the real .NET Framework `System.ServiceModel.dll`/`System.dll` (GAC) rather than
  guessing: the pre-retyping `ClrElementType.Object` branch for a hypothetical boxed
  `CommunicationState` was dead code (the real field is a plain `Int32`-backed enum) and was
  implemented incorrectly besides; remote-address extraction's `ClrObject.ToString()` fallback never
  actually returned a real URL (fixed by chasing into `System.Uri`'s own internal string field, whose
  name differs by runtime — `m_String` vs. `_string`); and `IsFactoryType` matched nearly every
  *nested channel* instance, not just real factories, silently dropping real (including Faulted)
  channels from every count — found by writing the first-ever unit test for that method.
- **`DbConnectionAnalyzer`. Done 2026-09-12.** Sixth adapter consumer, first real `heap.root-paths`
  consumer. Real-dump run surfaced a pre-existing (not introduced by retyping) limitation, decompile-
  verified: `Microsoft.Data.SqlClient.SqlConnection` v5.2.2 has no `_connectionState`/`_state`/
  `m_connectionState` field of its own — `State` delegates to a separate `_innerConnection` object,
  which this analyzer was never written to chase into. Documented, left unfixed pending its own
  verification pass across the provider matrix this analyzer targets.
- **`TimerLeakAnalyzer`. Done 2026-09-12.** Seventh adapter consumer and the last of the
  "typed-resource quartet" (`DbConnectionAnalyzer`/`WcfChannelAnalyzer`/`HttpObjectAnalyzer`/
  `TimerLeakAnalyzer`). The only member whose pre-retyping design used a different discovery shape —
  type-aggregate counts for the main tally plus an entirely separate second full-heap pass just to
  read `TimerQueueTimer._period` per instance — collapsed onto the same single shared scan the rest
  of the quartet uses. Decompiling `TimerQueueTimer` across .NET Framework and .NET 7–10 found two
  more pre-existing gaps (missing `.NET Framework` field names; `_period` is genuinely `uint`, not
  the `long` the old dual-width fallback defensively but incorrectly guessed at) — fixed by adding a
  real `FieldValueKind.UInt32` case to `dump.object-fields` (`SdkVersion` 8→9).

Along the way, `HeapTypeStatistics`/`IHeapTypeStatisticsQuery` picked up several more fields reused
across this wave (`SampleAddress`, `ModuleName`, array-shape fields for `ArrayAnalyzer`, shape
fields for `ObjectShapeAnalyzer`) — same "extend the existing surface to exactly what a real
consumer needs" pattern the Tier-1 batches established, not new capabilities each time.

**`BoxingAnalyzer`. Done 2026-09-12.** Picked next after the typed-resource quartet closed —
smallest remaining Tier-1+Tier-2 candidate (303 lines) and, on inspection, a second tier-split
correction of the same shape `ObjectShapeAnalyzer`/`LockGraphAnalyzer`/`ThreadStackClusterAnalyzer`
already were: it reads no per-instance field *value* at all, only per-type structural metadata
(`ClrType.IsEnum`/`StaticSize`/`BaseType.Name`/`EnumerateInterfaces()`/`Fields`), every bit of it
already isomorphic to what `IHeapTypeStatisticsQuery` exposes for other Tier-1-only analyzers. Never
needed the `dump.object-fields` hatch this wave built.
- **`IHeapTypeStatisticsQuery`/`HeapTypeStatistics` extended, not a new capability**: added
  `IsEnum`/`StaticSize`/`BaseTypeName` (always available, same as `IsValueType`/`IsArray` — come from
  the type itself, not the shape cache) and `HasIEquatableInterface`/`DeclaredFieldByteSize`
  (independent of `HasTypeShapeData`, since the pre-retyping analyzer always computed these from
  `TypeAggregates` alone, with no shape-cache dependency at all — bundling them behind the same gate
  as `InterfaceCount` would have been a real behavior change, not just plumbing). `SdkVersion` 9→10.
- **Dump-side `HeapTypeStatisticsQuery` extended**, not a new type — same file every prior batch in
  this wave touched. `EnumerateInterfaces()`/`Fields` walks wrapped in the same defensive try/catch
  shape the pre-retyping analyzer's own `EnumerateInterfacesSafe`/`ComputeTotalFieldBytes` used
  (ClrMD can fail on corrupt dumps or partially-loaded metadata).
- **A real perf regression found and fixed before the real-dump run, not after**: the first cut
  computed `HasIEquatableInterface`/`DeclaredFieldByteSize` for *every* entry in `TypeAggregates` —
  i.e. every distinct type on the heap, reference types included — not just value types. On the 3.5 GB
  reference dump this stalled the real-dump test for 10+ minutes (never crashed, never finished either
  run before being stopped) walking full interface/field metadata for a type population BoxingAnalyzer
  was always going to discard immediately (only value types ever pass its `isBoxed` check). Fixed by
  gating both computations to `type is { IsValueType: true }` inside `HeapTypeStatisticsQuery`, matching
  exactly what the pre-retyping analyzer's own `EnumerateInterfacesSafe`/`ComputeTotalFieldBytes` calls
  were already scoped to (they only ran after `isBoxed` was confirmed true). A concrete instance of this
  wave's own stated risk — "extend the surface to exactly what a real consumer needs," where "needs"
  includes *which subset of the type population* needs it, not just which fields.
- **A helper relocation, not a behavior change**: `HasIEquatableInterface(IEnumerable<ClrInterface>)`
  moved from `BoxingAnalyzer` (a public/internal API surface a ClrMD-typed SDK analyzer can no longer
  expose) into `HeapTypeStatisticsQuery` as a private, dump-side-only helper — same treatment as
  `HeapTypeStatisticsQuery`'s own `ComputeArrayRank`/`ComputeBaseTypeDepth`, neither unit-tested
  directly either. The three direct unit tests exercising it were removed from `BoxingAnalyzerTests`;
  coverage moves to the new characterization test below. `IsNullableTypeName`/`SafeInstanceCount`/
  `ComputePaddingWaste` stayed on `BoxingAnalyzer` unchanged (pure, no ClrMD dependency) — their
  existing unit tests pass unmodified.
- **Inherited, not introduced**: the method-table-name-collision gap every other `GetTypeStatistics()`
  consumer already documents (two distinct method tables resolving to the same display name collapse
  to one dictionary entry, last write wins) now also applies here — not a new gap this batch created,
  just inherited from the shared surface.
- **Gate met**: new `BoxingAnalyzerRetypingCharacterizationTests` — type-shape data (`IsEnum`/
  `StaticSize`/`HasIEquatableInterface`) can't be hand-crafted via reflection injection any more than
  segment/root/shape data in earlier batches could, so it cross-checks against real boxed value-type
  method tables found on a self-attached live process heap (three tests: classification arithmetic,
  oversized-threshold detection, and the `!HasExactGenerationData` empty-result gate). New
  `BoxingAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground, real prebuilt index)
  written against the same 3.5 GB reference dump pattern every prior batch uses. Two attempts against
  the real dump this session were stopped mid-run (10+ minutes, no completion) before the perf
  regression above was found and fixed — not the analyzer hanging, the pre-fix
  `HeapTypeStatisticsQuery` walking every reference type's interface/field metadata needlessly. Third
  attempt, post-fix, passed in 4s (6.5s total including build/discovery) — confirms the gating fix was
  the actual cost driver, not a real hang: 3,052,736 boxed objects, ~75.5 MB boxed bytes, 393 top
  boxed types, 266 padding-waste types, 13 oversized types. Full non-real-dump suite (1440 tests, up
  from 1238 at the last count in this doc) passes.
- Call site updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
  `BoxingAnalyzerLegacyAdapter`. No benchmark referenced this analyzer directly.

**`FinalizableObjectAnalyzer`. Done 2026-09-12.** Ninth Tier-2 wave consumer, first real
`heap.finalizer-queue` consumer — the capability was declared but had zero dump-side implementation
and zero real consumers before this, same "declared but never checked against a real consumer" gap
Batch 5/7's `IRuntimeJitQuery`/`IHeapHandleQuery` redesigns hit.
- **`IHeapFinalizerQueueQuery` redesigned, not just implemented**: the shipped shape (raw `ulong`
  addresses, zero consumers) carried no type identity at all. Redesigned to yield
  `FinalizerQueueObjectRef(Address, IsValid, Type, TypeDisplayName, ShallowSize, IsDisposableType,
  IsCriticalFinalizerType)` — `IsDisposableType`/`IsCriticalFinalizerType` are structural
  (interface-implements / base-type-chain) facts, computed dump-side and cached per method table,
  deliberately scoped to the finalizer queue's own bounded population rather than folded into
  `heap.types`' whole-heap per-type dictionary — the exact shape of the mistake found and fixed in
  `BoxingAnalyzer`'s retyping earlier this wave does not apply here by construction. The disposed
  field's *value* stays a Tier-2 concern: the analyzer reads it directly via
  `IDumpObjectFieldQuery.TryReadField`, trying each of five candidate field names once per method
  table (cached), keeping the tier boundary honest rather than smuggling a field-value read into a
  Tier-1 interface.
- **A real, deliberate behavior extension, checked for blast radius before shipping**: the
  pre-retyping analyzer's no-index fallback (a full heap walk when no prebuilt index exists) is now
  handled by extending the *shared* `heap.types` no-index fallback
  (`HeapTypeStatisticsQuery.BuildFromCachedStatistics`, backed by `StatisticsCache`'s full-heap-walk
  branch that `DominatorAnalyzer`/`EventLeakAnalyzer`/`LeakCandidateAnalyzer`/`ReferenceChainAnalyzer`/
  `QueryEngine` already share) with `IsFinalizableType`/`Gen0Count`/`Gen1Count`/`Gen2Count` on
  `CachedTypeStatistics` — checked first, not assumed: the extension is purely additive (new
  properties none of the 5 other consumers read) and adds marginal per-object cost to a walk that's
  already happening (that branch already iterates every live object to compute count/size), not a
  second full heap walk. Verified by running all 5 other consumers' tests (90 tests) unchanged after
  the extension. This means the analyzer itself needs no fallback logic at all — one
  `IHeapTypeStatisticsQuery.GetTypeStatistics()` call serves both the index-backed and no-index
  paths.
- **One accepted, documented fidelity gap**: per-entry `Generation` (an `int`, 0/1/2/3=LOH/-1=unknown,
  feeding the report's `FormatGeneration` display) is derived from `IHeapSegmentQuery.GetGeneration`'s
  `HeapGenerationTag` enum, which distinguishes Poh/Frozen from Loh — the pre-retyping analyzer's raw
  `ClrSegment.GetGeneration()` int cast may or may not have. Poh/Frozen map to the same "-1 = Unknown"
  sentinel an unresolvable address already used, rather than guessing which legacy int value ClrMD
  would have produced for them — the same conservative exclusion `GCRootAnalyzer`'s retyping already
  made for these two rare tags.
- **Gate met**: new `FinalizableObjectAnalyzerRetypingCharacterizationTests` — no prior per-analyzer
  unit test existed (only a finding-generator test, unaffected). Population-sweep filter/sort
  arithmetic is fully synthetic-controllable (real method tables, injected `TypeAggregates`, same
  shape prior batches used); queue-analysis correctness is deliberately *not* asserted against a
  specific expected count on a live self-attached process — unlike the static type/segment/root data
  earlier batches relied on, a live process's actual finalizer queue content is GC-timing-dependent
  and transient, so only internal-consistency invariants are checked there (count/generation bounds,
  sort order), with real behavioral verification left to the real-dump test. New
  `FinalizableObjectAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground, streamed to a
  file per the user's request rather than blind-waited-on) passed in 7s against the real 3.5 GB
  reference dump: 71,902 total finalizable objects, 68,576 in the finalizer queue, 2,038
  critical-finalizer objects, no undisposed disposables found, 147 distinct finalizable types by
  Gen2 count, 0 of the top 10 retained-size entries got a sampled root path — expected and correct,
  not a bug: objects sitting in the finalizer queue are by definition unreachable from ordinary GC
  roots (that's why they're eligible for finalization at all), matching the pre-retyping analyzer's
  own behavior exactly. Full non-real-dump suite (1443 tests, up from 1440) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
  `FinalizableObjectAnalyzerLegacyAdapter`, carrying the pre-retyping analyzer's own
  `IRequiresReachableGraphIndex`/`IRequiresDominatorTreeIndex` markers (same relocation
  `GCRootAnalyzerLegacyAdapter` needed in Batch 3). No benchmark referenced this analyzer directly.

**`WeakReferenceAnalyzer`. Done 2026-09-12.** Bigger than `FinalizableObjectAnalyzer` — closer to
`GCHandleAnalyzer`-scale — but almost everything it needed already existed: `heap.handles`
(Phase A/C, weak + dependent handle enumeration) replaced ~150 lines of duplicated in-memory/disk-
reader iteration logic entirely; `heap.dominators`/`heap.segments`/`heap.objects` (random-access)
were all already built. Two genuinely new pieces:
- **`heap.reachability` implemented for the first time** — declared in the SDK, zero dump-side
  implementation before this. `HeapReachabilityQuery` is a pure pass-through over the already-
  existing `IReachableAddressProvider` Stage A product, same shape `HeapDominatorQuery`/`HeapRootQuery`
  already are — wired into `AnalysisContext.HeapReachability` as nullable/optional, same precedent
  `HeapDominators` set for "Stage A/B might not be built this session."
- **`FieldValueKind.NativeInt` added** — `WeakReference`/`WeakReference<T>`'s `m_handle` field is a
  genuine pointer-sized `IntPtr` (`ClrElementType.NativeInt`, decompile-verified against the
  installed ClrMD package rather than assumed), an opaque GC handle value distinct from
  `ObjectReference` (the GC doesn't scan/relocate it) and not safely representable as `Int64` across
  32-bit dumps.
- **Phase B (WeakReference&lt;T&gt;/WeakReference object discovery) uses `TypedResourceCandidateStreamingLegacyAdapter`**,
  the same shared base 7 other Tier-2 analyzers already use, rather than the raw `TypeAggregates`
  name-matching loop the pre-retyping analyzer hand-rolled — `context.HeapObjects` gives every
  WeakReference-shaped instance directly from the pipeline's one shared heap-index scan.
- **A real, deliberate behavior simplification, not silently dropped**: the pre-retyping analyzer's
  `PhaseBFallbackUsed`/`PhaseBSkipped`/degraded (non-exact) `StaleWrapperCountIsExact` branch existed
  to distinguish three data-source states (disk-index-backed exact scan, live-heap-scan exact
  fallback, and a degraded one-sample-per-type extrapolation when `TypeAggregates` existed but the
  disk object index didn't). The candidate-streaming mechanism is backed by the pipeline's shared
  heap-index *scan itself* (not a post-hoc read of its persisted output), so it produces the same
  exact per-instance data regardless of storage mode — the degraded case can no longer occur by
  construction. All three fields are always reported as their non-degraded value now (kept in the
  domain result, not removed, since the report/finding-generator layer already branches on them and
  stays correct either way).
- **A real fidelity investigation, not an assumption**: the pre-retyping analyzer's disk-reader path
  checked `HandleRecord.IsAlive` for dependent handles as an extra liveness signal beyond "address is
  zero." Traced every construction site of `HandleRecord` in the codebase — no producer anywhere ever
  sets `IsAlive` to `false`; it's declared `= true` and never overridden, making that branch dead
  code today. Relying on `TargetAddress == 0` alone (all `IHeapHandleQuery` exposes) therefore loses
  no real behavior.
- **Two pre-retyping analyzers' genuinely different semantics, both preserved rather than merged**:
  `TrackDependentDeadKeyValueType`'s dead-key value-type resolution counts a resolved address as a
  hit (reporting `"(unknown)"` when its method table is 0) and only an unresolvable address as
  "unresolved" — a deliberately looser bar than `GCHandleAnalyzer`'s own `TryResolveDependentTypeName`
  helper, which treats a zero method table as unresolved too. Checked both pre-retyping
  implementations directly rather than assuming they were interchangeable; each retyping keeps its
  own analyzer's original behavior.
- **Gate met**: new `WeakReferenceAnalyzerRetypingCharacterizationTests` (20 tests) — no pre-existing
  test coverage existed. Uses fake, dictionary-backed capability implementations (the shape
  `TimerLeakAnalyzerRetypingCharacterizationTests` established), not a live/real heap — this
  analyzer's logic is purely capability-interface-driven, so every branch (alive/dead weak handles,
  dependent dead-key resolution and its unresolved-count, generation bucketing including the
  Poh/Frozen exclusion, held-only-via-weak-reference with and without a reachability provider,
  dominator-backed retained bytes with and without a provider, stale-wrapper detection via the new
  `NativeInt` field kind) is exactly controllable without ClrMD at all. New
  `WeakReferenceAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground, streamed to a file)
  passed in 2s against the real 3.5 GB reference dump: 8,196 weak handles (all alive, 0 dead), 0
  dependent dead-keys, 0 `WeakReference`/`WeakReference<T>` wrapper objects found (this dump's weak
  handles come from framework/`ConditionalWeakTable` usage rather than explicit wrapper objects — an
  internally consistent, plausible result, not a bug), 1,545 of 8,196 alive targets held only via
  weak reference, both Stage A and Stage B data available. Full non-real-dump suite (1463 tests, up
  from 1443) passes.
- Call site updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
  `WeakReferenceAnalyzerLegacyAdapter`, carrying the pre-retyping analyzer's own
  `IRequiresReachableGraphIndex` marker only (never `IRequiresDominatorTreeIndex` — Stage B is
  consumed opportunistically when already available, never required). No benchmark referenced this
  analyzer directly.
- **`SdkVersion` 11** — also folds in an untracked bump owed from `FinalizableObjectAnalyzer`'s
  retyping the same day (`IHeapFinalizerQueueQuery`'s redesign), missed at the time.

**`ThreadAnalyzer`. Done 2026-09-12.** Another tier-split correction, same shape as `ObjectShapeAnalyzer`/
`LockGraphAnalyzer`/`BoxingAnalyzer` before it — this doc's own earlier text classified it as
genuinely Tier-2 (needing `ClrException.Message`), but that field is a ClrMD *convenience property*
over the exception object's own fields (`ClrException.Type`/`.Message`), not a raw
`ClrInstanceField.Read` — exactly as source-neutral as everything else `runtime.threads` already
exposes. Retyped entirely Tier-1, no `dump.object-fields` dependency at all. Reused the
thread-domain quartet's already-proven scan-participant shape wholesale (`LockGraphAnalyzerLegacyAdapter`'s
pattern, 2 of 4 quartet members already retyped) rather than inventing a new one:
- **`RuntimeThreadRef` extended with seven fields**: `IsBackground`, `ThreadStateDisplay`,
  `GcModeDisplay`, `AppDomainName`, `HasCurrentException`, `ExceptionTypeName`, `ExceptionMessage`,
  `StackSizeBytes` — all computed once per thread in the single shared choke point
  (`ThreadStackTranslator.ToThreadRef`), all safe ClrMD convenience-property reads on a population
  that's always small (thread count, never heap-object-count-scale), so none of this risks the
  per-population cost mistake found in `BoxingAnalyzer` earlier this session.
- **A real fidelity gap found and fixed during design, not shipped broken**: originally planned to
  expose only a nullable `ExceptionTypeName`/`ExceptionMessage`, inferring "has an exception" from
  non-null. Caught before writing the analyzer that this collapses two genuinely different
  pre-retyping states — no exception at all, vs. a real non-null exception whose `Type` still fails
  to resolve — that the pre-retyping analyzer's own `currentException != null` check distinguishes.
  Added `HasCurrentException: bool` as an explicit, independent flag instead.
- **`ThreadAnalyzerLegacyAdapter`** mirrors `LockGraphAnalyzerLegacyAdapter` exactly (accumulate
  real `RuntimeThreadRef`/frame data during the pipeline's shared `ThreadStackScanDispatcher` pass,
  hand the inner analyzer a `PrecomputedRuntimeThreadQuery`, fall back to a live query outside the
  pipeline) with one difference: it computes a **real** per-thread stack-root count via
  `IHeapAnalysisCache.GetOrCountThreadStackRoots` during `OnThreadStack`, where
  `LockGraphAnalyzerLegacyAdapter` hardcodes `0` (a fact it never needed).
- **Every pure, ClrMD-independent helper reused unchanged**: `ThreadWaitClassifier.ClassifySignature`
  (frame-signature substring matching), `CountMoveNextDepth`/`IsThreadPoolWorker`/
  `TrackTopFrameHotspot`/`IsFrameworkFrameSignature`/`BuildStackMemorySummary` all took only strings
  or plain value lists to begin with — only their frame-signature *extraction* expression changed
  (`ThreadStackFrameRef.RawMethodSignature ?? FrameName ?? ""` instead of a `ClrStackFrame`-typed
  helper), the classification/counting logic itself is byte-for-byte identical.
- **Gate met**: new `ThreadAnalyzerRetypingCharacterizationTests` (20 tests) — no pre-existing test
  coverage existed. Fake, list-backed `IRuntimeThreadQuery` (same shape
  `WeakReferenceAnalyzerRetypingCharacterizationTests` established), covering alive/dead counting,
  lock tracking and sort order, exception tracking, wait classification, hotspot tracking (including
  the framework-frame-skip rule), threadpool detection via both the state flag and the frame
  heuristic, finalizer-thread capture, async-chain depth, background-thread counting, GC-thread
  exclusion from hotspots, the "other threads" fallback bucket, and the stack-memory summary. New
  `ThreadAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground, streamed to a file) —
  deliberately run standalone (outside the pipeline, exercising the live `RuntimeThreadQuery`
  fallback path rather than the precomputed one) — passed in 18s against the real 3.5 GB reference
  dump: 135 total threads, 38 alive, 0 GC threads, 3 blocked, 5 lock-holding, 1 with an active
  exception, 38 background, 13 threadpool workers, exactly 1 finalizer thread, 0 async-chain
  threads. Full non-real-dump suite (1483 tests, up from 1463) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark` now construct `ThreadAnalyzerLegacyAdapter`; `ThreadAnalyzerBenchmark`
  retargeted at `AnalyzerBenchmarkBase<ThreadAnalyzerLegacyAdapter>` (a dedicated benchmark class
  using the generic-constraint form, not a `new ThreadAnalyzer()` call site — missed by the initial
  grep sweep, caught by building `BenchmarkSuite1` before committing).
- **`SdkVersion` 12**.

**`HangAnalyzer`. Done 2026-09-12.** Completes the thread-domain quartet (all 4 of 4:
`LockGraphAnalyzer`, `ThreadStackClusterAnalyzer`, `ThreadAnalyzer`, now `HangAnalyzer`). The
biggest batch yet by volume (735 lines) but not by novel-capability difficulty — dual-scan
complexity, not a new hard capability like `AsyncStateMachineAnalyzer`'s still-open field-enumeration
gap.
- **The one real architectural wrinkle**: this is the only analyzer needing to be *both* a
  heap-index-scan participant (discovering Task/QueuedWorkItem/continuation-shaped objects) and a
  thread-stack-scan participant (detecting waiting threads) at once. The shared
  `TypedResourceCandidateStreamingLegacyAdapter` was built for heap-scan-only consumers and doesn't
  compose with `IThreadStackScanParticipant` without duplicating its private state, so
  `HangAnalyzerLegacyAdapter` is bespoke — implements both scan-participant interfaces directly,
  mirroring what each role does individually elsewhere (candidate-list accumulation shaped like the
  shared adapter's own internals; thread/frame accumulation shaped like
  `ThreadAnalyzerLegacyAdapter`/`LockGraphAnalyzerLegacyAdapter`).
- **`runtime.thread-pool` implemented for the first time** — `IRuntimeThreadPoolQuery` wraps
  `ClrRuntime.ThreadPool`'s `ClrThreadPool`, another ClrMD convenience-property wrapper (like
  `ClrException`), not a raw field read — entirely Tier-1 despite reading thread-pool internals.
  `QueueLength` needed the same defensive reflection probe the pre-retyping analyzer's own
  `GetIntProperty` used (not a public property on every ClrMD version), ported unchanged.
- **`AsyncTypeProfile` promoted from a private nested type to a shared, namespace-level `internal`
  type** — the pre-retyping analyzer's own pure, ClrMD-independent name classifier
  (Task/QueuedWorkItem/continuation), now used by both `HangAnalyzerLegacyAdapter` (deciding whether
  to collect a heap-scan candidate at all, via `IsPotentiallyRelevant`) and `HangAnalyzer` itself
  (re-deriving which specific category a collected candidate belongs to from its type name — a pure
  string operation needing no further ClrMD access once collected).
- **The known-relevant field read** (`m_stateFlags` on `System.Threading.Tasks.Task`) goes through
  `dump.object-fields` unchanged — `Int32`, already supported, no new `FieldValueKind` needed.
- **A real, deliberate behavior simplification, documented not dropped**: the pre-retyping
  analyzer's independent parallel-heap-walk fallback (when no shared scan ran) has no equivalent —
  same accepted simplification `TimerLeakAnalyzer`/`WeakReferenceAnalyzer`'s Phase B already made:
  a real analysis session always goes through the pipeline's shared scan, so an empty candidate list
  when it didn't run is accepted rather than re-implementing a second, independent scan path.
- **A capability-registry drift caught immediately, as designed**: `SdkRegistryConformanceTests`
  failed the moment `runtime.thread-pool` was added to `CapabilityVocabulary` without a matching
  `capability-registry.json` entry — fixed by bumping the registry to `1.7.0` and adding the entry,
  exactly the mechanism this test exists for.
- **Test fallout, not just a call-site swap**: the pre-retyping `HangAnalyzerHeapIndexScanTests`
  exercised the old analyzer's own private accumulator fields (a per-method-table profile cache,
  separate running counters) directly via reflection — none of that state exists post-retyping (the
  adapter's own state is a flat candidate list, nothing else), so the old test file was deleted
  outright rather than adapted, replaced by `HangAnalyzerRetypingCharacterizationTests`.
- **Gate met**: new `HangAnalyzerRetypingCharacterizationTests` (15 tests) — fakes for the analyzer's
  own classification/scoring logic (wait detection, task state-flag classification, health-score
  arithmetic, runtime-thread-pool-unavailable handling), plus one live-self-attached-process test
  exercising `HangAnalyzerLegacyAdapter`'s actual `BeforeHeapIndexScan`/`OnHeapEntry`/`MergePartial`
  end to end (candidate data needs real ClrMD type resolution, same constraint every dump-side query
  characterization test in this project hits). New `HangAnalyzerRealDumpTests`
  (`[DiscrepancyFact]`, one dump, foreground, streamed to a file) — heap-index scan driven explicitly
  via `HeapIndexScanDispatcher` (matching `TimerLeakAnalyzerRealDumpTests`'s own pattern; without it,
  the adapter's "invoked outside the pipeline" default silently reports zero tasks regardless of what
  the dump contains), thread-stack side left on its live fallback (matching
  `LockGraphAnalyzerRealDumpTests`'s own precedent) — passed in 24s against the real 3.5 GB reference
  dump: 38 alive threads, 5 lock-holding (both cross-check exactly against `ThreadAnalyzer`'s own
  real-dump result on the same dump), 0 waiting (top-frame-only classification, unlike
  `ThreadAnalyzer`'s whole-stack wait detection), health score 95 (100 − 5 for `FaultedTasks > 0`,
  matching the scoring formula by hand), 12,071 tasks scanned, 39 pending, 1 faulted, 2 canceled, 203
  queued work items, 147 continuations, runtime thread-pool data available, not starved. Full
  non-real-dump suite (1494 tests, up from 1463) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark` now construct `HangAnalyzerLegacyAdapter`; `HangAnalyzerBenchmark`
  retargeted at `AnalyzerBenchmarkBase<HangAnalyzerLegacyAdapter>`.
- **`SdkVersion` 13**; `capability-registry.json` `1.7.0`.

**`AsyncStateMachineAnalyzer`. Done 2026-09-12.** The one this doc's own "Batch 6" entry flagged as
harder than the thread-domain quartet — a genuine "still-open field-enumeration gap," not just
another discovery-shape variant. Closed by extending `IDumpObjectFieldQuery` (Tier 2) with two new,
narrowly-scoped primitives rather than a general field-reflection surface:
- **`TryGetFieldTypeName(address, fieldName)`** — a field's *declared* type name, no value read at
  all. Needed because `<>t__builder` is a struct field (`AsyncVoidMethodBuilder`/
  `AsyncTaskMethodBuilder`), which `TryReadField` cannot represent a value for at all (falls to its
  `default: return false` case) — detecting fire-and-forget `async void` state machines needs the
  field's *type*, not a runtime value.
- **`EnumerateObjectReferenceFields(address)`** — every reference-typed instance field as
  `(Name, TargetAddress)`. This is the gap itself: nothing shipped before this let a retyped analyzer
  ask "what are this instance's reference fields called," only "read this *named* field's value."
  Target size/type resolve through the existing `IHeapObjectLookup`, not duplicated here.
- **`HeapTypeStatistics.IsAsyncStateMachineType` added**, mirroring `IsFinalizableType`'s existing
  two-path precedent exactly (`FinalizableObjectAnalyzer`'s retyping) — populated on both the
  index-backed path (`TypeAggregateFlags.IsAsyncStateMachineType`) and the no-index fallback path
  (`CachedTypeStatistics.IsAsyncStateMachineType`, computed via the same
  `AsyncStateMachineNamePattern.Regex` the Phase 1 flag and the shared adapter's `IsCandidateType`
  already trust as ground truth — a plain name-pattern check, no ClrMD field/interface walk, so
  there's no cost reason to gate it behind an index the way `HasIEquatableInterface`/
  `DeclaredFieldByteSize` are).
- **The pre-retyping analyzer's own second, hand-rolled full heap-index pass is gone** — its
  suspend-state histogram (`DominantState`/`StateDistribution`) used to need every state-machine
  instance's `<>1__state` value, gathered via a dedicated `EnumerateIndexedEntriesAsTuples`/
  `LiveHeapEntries` pass separate from candidate discovery. Retyped onto
  `TypedResourceCandidateStreamingLegacyAdapter` like the rest of the Tier-2 wave, `context.HeapObjects`
  already streams every instance once during the pipeline's one shared Phase-1 scan — the histogram
  is now built in the same pass as everything else. The single biggest win of this batch, not just a
  mechanical retype.
- **Captured-closure-byte reads stay bounded to one sample instance per type** — the first streamed
  instance encountered — preserving the pre-retyping analyzer's own explicit "one object access per
  type" cost bound exactly; only `<>1__state` gets read on every instance (needed for the histogram,
  no more expensive than the pre-retyping analyzer's own second pass did per instance).
- **One accepted, documented fidelity gap**: `StateMachineTypeProfile.ReferenceFieldCount` now comes
  from `HeapTypeStatistics.ReferenceFields` (shipped for `ObjectShapeAnalyzer`), which reads 0
  whenever `HasTypeShapeData` is `false` (no prebuilt per-type shape cache) — unlike the pre-retyping
  analyzer's unconditional `ClrType.Fields` count. Accepted per hard-need-basis: no Tier-2 primitive
  exists, or is needed elsewhere, for an unconditional per-type reference-field count outside the
  shape cache.
- **`AsyncStateMachineAnalyzerLegacyAdapter` overrides `ResolveOptions`** (`options.AsyncStateMachineAnalysis`)
  — easy to miss on a streaming-adapter subclass since neither `HttpObjectAnalyzer` nor
  `TimerLeakAnalyzer` (the two most recent adapters read for reference) needed an options record at
  all; skipping it would have silently dropped `LargeCaptureThresholdBytes` back to its default on
  every real pipeline run.
- **Gate met**: new `AsyncStateMachineAnalyzerRetypingCharacterizationTests` (11 tests) — no
  pre-existing per-analyzer test coverage existed. Fake, dictionary-backed
  `IDumpObjectFieldQuery`/`IHeapObjectLookup`/`IHeapTypeStatisticsQuery` cover: histogram construction
  across multiple streamed instances, the sample-only capture-byte bound (a second instance's
  reference field is set up to fail the test if it's ever read), `IsAsyncVoid` detection via the new
  `TryGetFieldTypeName`, the large-capture-threshold split, `SuspendedMethodMap` grouping across two
  distinct compiler-generated types sharing one originating method, and the totals-vs-per-type-profile
  split (a flagged-but-never-streamed type still contributes to grand totals). New
  `AsyncStateMachineAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground) passed in 9s
  against the real 3.35 GB reference dump: 11 total state machines, 952 bytes, 1 Gen2, 7 distinct
  types, 7 high-capture entries, 7 suspended-method-map entries. Full non-real-dump suite (1505 tests,
  up from 1494) passes — one unrelated, pre-existing flaky self-attached-heap test
  (`ArrayAnalyzerRetypingCharacterizationTests.AnalyzeAsync_RealSparseArray_ComputesExactNullOrZeroDensity`)
  reproduced as a pass in isolation on immediate re-run, not a regression from this batch.
- Call site updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
  `AsyncStateMachineAnalyzerLegacyAdapter`. No benchmark referenced this analyzer directly.
- **`SdkVersion` 14** (two `IDumpObjectFieldQuery` primitives plus `HeapTypeStatistics.IsAsyncStateMachineType`,
  bumped in one entry since both were found by, and shipped in, this same retyping). No
  `capability-registry.json` bump — both additions extend existing capabilities
  (`dump.object-fields`, `heap.types`), no new vocabulary entry.

**`AsyncTaskAnalyzer`. Done 2026-09-12.** Largest and structurally different from every prior wave
member — three object shapes (`Task`, `TaskCompletionSource`, `IValueTaskSource`) discovered off one
shared heap-index scan, a bounded BFS over each task's continuation graph (including `List<object>`
multi-continuation fan-out), and a field-priority search for faulted-task exception objects. Two
genuinely new Tier-2 gaps found by reading the full 1226-line source before scoping, not grepping it:
- **`IDumpArrayShapeQuery.EnumerateObjectReferenceElements(address, maxElements)`** — array element
  *address* enumeration, replacing the pre-retyping analyzer's own `List<object>._items`/`_size`
  field read plus a hand-rolled `ClrArray.GetObjectValue` loop. Reports a null slot as address 0
  rather than omitting it (the retyped analyzer filters those, matching the pre-retyping helper's
  own "skip null/invalid elements" behavior exactly).
- **`IDumpObjectFieldQuery.TryReadNestedStructField`** — one level of embedded-struct field read
  (`ClrObject.ReadValueTypeField` + `ClrValueType.ReadField<T>`, decompile-verified against the
  installed ClrMD package before implementing), needed for
  `ManualResetValueTaskSourceCore<TResult>._completed`, which plain `TryReadField` can't reach
  (falls into its `default: return false` struct case). Deliberately one level only, not a general
  field-chase API — exactly what this one real consumer needs.
- **`HeapTypeStatistics.IsTaskType`/`IsTaskCompletionSourceType`/`ImplementsValueTaskSource`/
  `ValueTaskSourceCoreFieldName`** — `IsTaskType` is two-path like `IsAsyncStateMachineType`
  (index-backed via the already-reserved `TypeAggregateFlags.IsTaskType` bit, no-index fallback via
  `TaskTypeNamePattern.IsTaskType`); the other three have no `TypeAggregateFlags` bit at all (same
  gap the pre-retyping analyzer already lived with) and are always computed — `IsTaskCompletionSourceType`
  a pure name check, `ImplementsValueTaskSource` a real `ClrType.EnumerateInterfaces()`/field-type
  walk. The interface walk is **not** a new cost the way `BoxingAnalyzer`'s first cut was — it
  replaces an *existing* per-distinct-type walk the pre-retyping analyzer's own `BeforeHeapIndexScan`
  already did at the same scope (`TypeAggregates`, once per distinct method table), just moved into
  the shared `heap.types` surface. Verified, not assumed: the real-dump run below completed in 10s,
  no `BoxingAnalyzer`-style stall.
- **Candidate discovery is bespoke, not `TypedResourceCandidateStreamingLegacyAdapter`** — that
  base's `IsCandidateType(string)` is pure name-based, but `IValueTaskSource` detection is structural
  (interface + embedded-field-type match). `AsyncTaskAnalyzerLegacyAdapter` implements
  `IParallelHeapIndexScanParticipant` directly, reproducing the pre-retyping analyzer's own three-MT-set
  `BeforeHeapIndexScan`/`OnHeapEntry` shape verbatim for discovery, then unions all three candidate
  categories into **one** `HeapObjectRef` list (dropping the pre-retyping analyzer's separate
  `_participantEntries`/`_participantTcsEntries`/`_participantVtsEntries` three-way `MergePartial`
  entirely) — the SDK analyzer re-classifies each candidate via `HeapTypeStatistics.IsTaskType`/etc.,
  the same "classify from the shared per-type stats map" pattern `AsyncStateMachineAnalyzer`
  established for `IsAsyncStateMachineType`.
- **One preserved fast path found by reading `LoadTaskEntries` closely**: the pre-retyping analyzer's
  Task discovery preferred a dedicated `TaskIndex.bin` satellite index (or
  `HeapIndexBuildResult.InMemoryTaskCandidates` in memory-backed mode) over the shared scan's
  participant-collected list — `TaskCompletionSource`/`IValueTaskSource` never had an equivalent disk
  fast path. The adapter reproduces this exact three-way priority for Task candidates specifically,
  dropping only the pre-retyping analyzer's fourth tier (an independent raw `heap.EnumerateObjects()`
  fallback when the shared scan itself never ran) — the same accepted simplification
  `TimerLeakAnalyzer`/`WeakReferenceAnalyzer`/`HangAnalyzer`'s own Phase-B-fallback removals already
  made. One accepted, documented trade: `TaskIndex.bin` records carry each task's `m_stateFlags` for
  free (read once at index-build time); `HeapObjectRef` has no slot for that extra field, so the
  retyped analyzer always re-reads `m_stateFlags` fresh via `TryReadField` regardless of which
  candidate source won — the same per-task cost three of the pre-retyping analyzer's four load paths
  already paid, just no longer skipped on the one path that happened to have it for free.
- **Type-name resolution simplification**: dropped the pre-retyping analyzer's own MT-to-name cache
  (`ResolveTypeName`) entirely — every candidate arrives as a `HeapObjectRef`, which already carries
  `TypeDisplayName` resolved once by the adapter.
- **Exception-object search ported onto existing capabilities, no new one needed**: new
  `CapabilityObjectFieldTraversal` (mirrors `ObjectGraphTraversal.TryFindByPredicate`) built entirely
  from already-shipped `IDumpObjectFieldQuery.TryReadField` (prioritized named fields) and
  `IHeapReferenceQuery.EnumerateReferences` (generic fallback walk) — the `heap.references`
  capability's first Tier-2-adjacent consumer.
- **One accepted, documented fidelity gap**: pending/unresolved generation bucketing kept the
  pre-retyping analyzer's own 0/1/2/3(LOH) scheme, which has no POH/Frozen bucket;
  `IHeapSegmentQuery.GetGeneration`'s `HeapGenerationTag` distinguishes them, and both fold into the
  legacy LOH bucket (`ToLegacyGeneration`), matching `GCRootAnalyzer`'s own prior treatment of the
  same gap.
- **Gate met**: new `AsyncTaskAnalyzerRetypingCharacterizationTests` (11 tests, dictionary-backed
  fakes) — no pre-existing per-analyzer test coverage existed for the classification/BFS/exception
  logic itself; the old `AsyncTaskAnalyzerHeapIndexScanTests.cs` reflected the pre-retyping analyzer's
  own three-list/three-`HashSet` internals directly and was deleted outright rather than adapted
  (same treatment `HangAnalyzerHeapIndexScanTests` got), since none of that state exists post-retyping.
  New `AsyncTaskAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground) passed in 10s
  against the real 3.5 GB reference dump. Full non-real-dump suite (1510 tests, up from 1505) passes.
- Call site updated: `DefaultAnalyzerFeatureModuleCatalog` now constructs
  `AsyncTaskAnalyzerLegacyAdapter`. No benchmark referenced this analyzer directly.
- **`SdkVersion` 15** (`TryReadNestedStructField`, `EnumerateObjectReferenceElements`, and
  `HeapTypeStatistics`'s four new Task/TCS/VTS fields, bumped in one entry since all were found by,
  and shipped in, this same retyping). No `capability-registry.json` bump — both new primitives
  extend already-registered capabilities (`dump.object-fields`, `dump.array-elements`).

**`CollectionAnalyzer`. Done 2026-09-12.** Built on `TypedResourceCandidateStreamingLegacyAdapter`
like the rest of the Tier-2 wave — unlike `AsyncTaskAnalyzer`/`HangAnalyzer`, this analyzer's own
pre-retyping `OnHeapEntry` already classified every heap object by name, exactly the discovery shape
the shared adapter assumes, so no bespoke participant was needed. Closes two real capability gaps:
- **`IDumpObjectFieldQuery.EnumerateInstanceFields`** — per-type field name+kind discovery without a
  real `ClrType`/`ClrInstanceField`, needed because this analyzer resolves a *different* field name
  per collection kind, per distinct type, via well-known names first, then a full-field
  classify-and-guess fallback.
- **`IHeapReverseReferenceQuery.TryGetReverseReferences`** — a new, truncation-aware overload for the
  `OwnerTypeHint` cheap-alternative-to-full-BFS feature; the pre-existing `EnumerateReverseReferences`
  stream can't carry the "did the index hit its fanout cap" signal this needed.
- **A force-expand overload was built, then removed the same session**: the pre-retyping search
  force-expanded types matching `ReferenceChainOptions.KnownLeakTypePatterns`, whose *defaults* are
  `"...List"`/`"...Dictionary"` — this analyzer's own target family — so wiring that policy through
  `IHeapRootPathQuery` hung a real 3.35 GB dump's root-path search indefinitely (force-expanding every
  List/Dictionary-shaped node defeats the hub-fanout cap almost everywhere in a real graph). No prior
  real-dump test exercised this analyzer's root-path search at all, so this was a latent, pre-existing
  cost the real-dump gate caught for the first time, not a regression. Fixed by adopting the same
  "never force-expand" policy `DbConnectionAnalyzer`/`TimerLeakAnalyzer` already use — see
  `IHeapRootPathQuery`'s own remarks for the full account.
- **A real, serious perf bug caught by the real-dump gate, not guessed at**:
  `IHeapTypeStatisticsQuery.GetTypeStatistics()` rebuilds its whole per-type dictionary from the
  index's full `TypeAggregates` on every call, no internal memoization — calling it once per wasteful
  candidate (799,795 collections on the reference dump) was an O(candidates × distinct-types) blowup
  that hung indefinitely. Fixed by fetching it once per analysis run, matching every other retyped
  analyzer's own precedent.
- **A real simplification, not a mechanical port**: four nearly-identical per-kind array-shaped probes
  (`AnalyzeDictionary`/`AnalyzeList`/`AnalyzeHashSet`/`AnalyzeArrayBackedCollection`) collapsed into
  one `AnalyzeArrayShapedCollection`, since the retyped field-layout/array-shape resolution is
  identical across all four kinds; `AnalyzeQueue` stays separate (needs head/tail contiguous-free-
  segment math no other kind has).
- **A real fidelity improvement**: `IHeapRootPathQuery` returns an already-formatted root-to-target
  path string, so `WastefulCollection.RootDescription` now carries the real path text instead of the
  pre-retyping analyzer's generic `"{rootKind}: (reference path found)"` placeholder.
- **One accepted, documented behavior drop**: `CollectionAnalysisOptions.MaxDegreeOfParallelism`/
  `SerializeHeapAccess` no longer apply — the pre-retyping analyzer's own parallel-segment-walk
  fallback (used only when no shared scan ran) no longer exists post-retyping, same "degraded case can
  no longer occur by construction" simplification every other Tier-2-wave adapter's own fallback
  removal already made.
- **Gate met**: new `CollectionAnalyzerRetypingCharacterizationTests` (607 lines) replacing the deleted
  `CollectionAnalyzerHeapIndexScanTests`/`CollectionScanPercentTests` (which reflected pre-retyping
  internals that no longer exist). New `CollectionAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one
  dump, foreground) passed against the real 3.35 GB reference dump after the two bugs above were
  fixed. Full non-real-dump suite passes. Also added a CLAUDE.md rule to kill any real-dump test run
  against the reference dump that hasn't progressed past collection start within 5 minutes — the
  force-expand hang above is exactly the failure mode that rule now catches early.
- Call sites updated: `CollectionAnalyzerBenchmark`, `FullPipelineBenchmark`, `SmallDumpLatencyBenchmark`
  now construct `CollectionAnalyzerLegacyAdapter`.
- **`SdkVersion` 16** (`EnumerateInstanceFields`/`InstanceFieldDescriptor`,
  `IHeapReverseReferenceQuery.TryGetReverseReferences`). No `capability-registry.json` bump — both
  extend already-registered capabilities (`dump.object-fields`, `heap.reverse-references`).

**`CrashAnalyzer`. Done 2026-09-13.** A full-heap exception scanner — `IParallelHeapIndexScanParticipant`
with both a disk-index-scan participant path and (pre-retyping) an independent parallel no-index
fallback — plus live field extraction on exception objects, Gen2/LOH root-retention paths, and
active-per-thread-exception correlation against scanned heap instances. Bespoke adapter, not
`TypedResourceCandidateStreamingLegacyAdapter`, for the same reason `AsyncTaskAnalyzer`/`HangAnalyzer`
needed one: exception detection is `ClrType.IsException`, a structural type fact, not a name
predicate.
- **`CrashAnalyzerLegacyAdapter` owns every structural ClrType-level fact** the pre-retyping
  analyzer's `OnHeapEntry`/`ResolveExceptionType` needed (is-exception, is-`AggregateException`,
  resolved display name) — no Tier-1/Tier-2 surface exists for "is this type an exception" and
  building one now, with a single consumer, would be speculative. The `IsAggregateException` fact is
  handed to the retyped analyzer via a new `CrashAnalyzerOptions.AggregateExceptionAddresses` side-set
  (`HeapObjectRef` has no slot for an arbitrary per-object flag), the same "wrapper record for one
  slot" shape `CollectionAnalyzerOptions` already established.
- **`RuntimeThreadRef.ExceptionAddress` added (Tier 1)** — `HasCurrentException`/`ExceptionTypeName`/
  `ExceptionMessage` (from `ThreadAnalyzer`'s retyping) can't correlate a thread's live exception back
  to the specific heap-scanned `ExceptionInstance` by address; this closes that gap.
- **`IDumpObjectFieldQuery.EnumerateExceptionStackTrace` added (Tier 2)** — restores the pre-retyping
  analyzer's third stack-trace-recovery tier (`_stackTraceString` text → this → `_remoteStackTraceString`),
  needed for an exception that's been thrown/unwound but never had `.ToString()`/`.StackTrace` called
  to materialize `_stackTraceString` as text. Implemented as a thin delegation to
  `ClrObject.AsException()?.EnumerateExceptionStackTrace()` (already a public ClrMD API), not a
  hand-rolled port of its binary trace-array walk. **`Message`/`HResult`/`Inner` need no equivalent
  primitive** — confirmed by decompiling the installed ClrMD package (v4.0.732401) that
  `ClrException`'s own properties are already exactly `"_message"`/`"_HResult"`/`"_innerException"`
  field reads under the hood, so `TryReadField` alone is already equivalent, not a fidelity drop.
- **Root-path search retargeted onto `IHeapRootPathQuery`**, replacing the pre-retyping analyzer's
  direct `RootPathFinder`/`ReferenceGraph`/`cache.GetOrBuildValidRoots` usage — the returned
  already-formatted `"RootKind: path"` string is split back into `ExceptionRetentionPath`'s separate
  `RootKind`/`FormattedPath` columns (the report shows both), matching
  `RootPathSearchSupport.FormatPath`'s exact shape.
- **`AggregateException.InnerExceptions` unwrapping retargeted onto `IDumpArrayShapeQuery.EnumerateObjectReferenceElements`**
  plus `IHeapObjectLookup`, replacing the pre-retyping analyzer's hand-rolled `ClrArray.GetObjectValue`
  loop.
- **Per-object generation now resolved via `IHeapSegmentQuery.GetGeneration`**, replacing the
  pre-retyping analyzer's own `HeapEntry.Generation`/`seg.GetGeneration(address)` reads — same
  0/1/2/3(LOH)/4(POH)/5(Frozen) ordinal scheme as before, `HeapGenerationTag`-mapped.
- **One accepted simplification, same shape as every other Tier-2-wave adapter's own removal**: the
  pre-retyping analyzer's independent parallel no-index fallback scan (`RunParallelExceptionScan`) and
  its direct `Analyze(runtime, heap)` overload no longer exist post-retyping — every candidate now
  always arrives via the pipeline's one shared scan.
- **Internal model types retyped**: `CrashThreadCandidate`/`ActiveExceptionContext`/`ExceptionInstance`
  (`src/DumpDetective.Analysis/Models/CrashAnalyzerHelpers.cs`) now hold
  `IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>` instead of `List<ClrStackFrame>`.
- **Gate met**: new `CrashAnalyzerRetypingCharacterizationTests` (fake, dictionary-backed capability
  implementations) covering message/HResult/inner-exception reads, `AggregateException` unwrapping,
  active-thread correlation, generation-gated retention-path search, rethrow detection, and the new
  binary-trace-unwind fallback tier. The pre-retyping `CrashAnalyzerHeapIndexScanTests` reflected the
  old analyzer's own per-type-count accumulation/merge internals directly via reflection — none of
  that state exists post-retyping (the adapter's own merge is now just a flat candidate-list
  concatenation plus an aggregate-address-set union), so it was rewritten against
  `CrashAnalyzerLegacyAdapter`'s new, much simpler merge shape rather than adapted in place.
  `CrashAnalyzerRetentionPathTests` updated for `BuildGen2RetentionPaths`'s new
  `IHeapRootPathQuery`-based signature. New `CrashAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one
  dump, foreground) passed in 23s against the real 3.5 GB reference dump: 750 exceptions across 20
  distinct types, 1 active exception correctly correlated via `ExceptionAddress` (original stack trace
  recovered, no inference needed), 26 crash buckets, 7 Gen2/LOH retention paths with cleanly-split
  root kinds, 0 `AggregateException` instances (a fact about this dump, not a gap — covered separately
  by the fake-backed test). Full non-real-dump suite (1520 tests) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark` now construct `CrashAnalyzerLegacyAdapter`; `CrashAnalyzerBenchmark`
  retargeted at `AnalyzerBenchmarkBase<CrashAnalyzerLegacyAdapter>`.
- **`SdkVersion` 17** (`RuntimeThreadRef.ExceptionAddress`, `IDumpObjectFieldQuery.EnumerateExceptionStackTrace`,
  bumped in one entry since both were found by, and shipped in, this same retyping). No
  `capability-registry.json` bump — both extend already-registered capabilities (`runtime.threads`,
  `dump.object-fields`).

**`StringAnalyzer`. Done 2026-09-13.** Unlike every prior Tier-2-wave member, its P1-3
"string-field-ownership" feature needs to inspect *every* heap object's type (which types, across
the whole heap, own string-typed fields), not just objects matching one name/structural candidate
filter — so `TypedResourceCandidateStreamingLegacyAdapter` doesn't fit. `StringAnalyzerLegacyAdapter`
is bespoke, following `CrashAnalyzer`/`HangAnalyzer`'s own precedent for full-population scan
participants.
- **Split responsibilities, not two heap walks**: the adapter streams every `System.String` instance
  as a `HeapObjectRef` candidate (same `heapObjectsOverride` mechanism every Tier-2-wave member
  uses) for the inner analyzer to fingerprint/dedup/bucket via
  `IDumpObjectFieldQuery.TryGetStringValue`; P1-3's owner-type-byte accounting (reservoir-sampled,
  Algorithm-R, ported near-verbatim from the pre-retyping analyzer's own
  `AccumulateStringOwnerType`) is a whole-heap-population aggregate with no per-instance
  SDK-capability expression, so it stays dump-side in the adapter and is handed to the inner
  analyzer pre-aggregated through a new `StringAnalyzerOptions` wrapper — the same
  "options wrapper carries adapter-precomputed data alongside real options" shape
  `CrashAnalyzerOptions` already established. Deliberately *not* streamed as objects: materializing
  a `HeapObjectRef` per heap object regardless of type (to inspect every type's field shape) would
  be a real memory regression this design avoids up front rather than discovering mid-batch, the
  way `BoxingAnalyzer`'s and `CollectionAnalyzer`'s batches each did for related reasons.
- **`IHeapTypeStatisticsQuery`/`HeapTypeStatistics.IsStringType` added** — two-path like
  `IsAsyncStateMachineType`/`IsTaskType` (index-backed via the already-existing
  `TypeAggregateFlags.IsStringType` bit, no-index fallback via a plain name check on
  `CachedTypeStatistics`/`StatisticsCache`). Covers every scalar aggregate stat (total strings/bytes,
  Gen0/1/2, LOH bytes) with zero adapter dependency, available whether or not a disk index exists.
- **`IDumpObjectFieldQuery.TryGetStringValue` extended with a bounded-length overload** — the
  existing overload called ClrMD's unbounded `ClrObject.AsString()`, unlike the pre-retyping
  analyzer's own `AsString(maxLength:)` calls, which deliberately avoid materializing full content
  for huge strings just to hash a fingerprint or build a preview. A real, closable gap, not
  speculative — every one of the 13 existing fake `IDumpObjectFieldQuery` implementations across
  other analyzers' characterization tests needed the new member added (delegating to the existing
  overload; none of them exercise string content).
- **A real, deliberate simplification, confirmed with the user before implementing**: dropped the
  pre-retyping analyzer's separate "prebuilt disk `StringDedupIndex`" fast path and its "index-only
  estimation" fallback. Once the adapter's shared scan pass always streams every string instance
  fresh (same cost it already pays today), those two paths only existed to skip a scan that now
  always runs anyway — the same call `AsyncTaskAnalyzer`'s retyping made for its own now-redundant
  fast path. `DedupSource` now only ever reports `"IndexScan"` (scan ran) or `null` (it didn't).
- **Also dropped, matching universal Tier-2-wave precedent**: the pre-retyping analyzer's
  independent live `heap.EnumerateObjects()` fallback for when no shared scan ran — no live default
  `IHeapObjectStream` implementation exists outside a scan participant (confirmed by inspection, not
  assumed), so an empty per-instance result when `context.HeapObjects` is null is accepted, same as
  `TimerLeakAnalyzer`/`WeakReferenceAnalyzer`/`AsyncTaskAnalyzer`/`HangAnalyzer` already do.
- **FOH/interned-string detection retargeted onto `IHeapSegmentQuery`** (`Kind == HeapSegmentKind.Frozen`
  segment ranges), replacing the pre-retyping analyzer's own `BuildFohSegments`/`IsSegmentInFoh`.
- **Retention-path search retargeted onto `IHeapRootPathQuery.TryFindRootPath`**, replacing the
  pre-retyping analyzer's own `RootPathFinder`/`ReferenceGraph`/`cache.GetOrBuildValidRoots` usage —
  same retarget `CrashAnalyzer`/`CollectionAnalyzer`/`DbConnectionAnalyzer` already made.
- **`DuplicateStringSnapshot.DominantType` simplified to a constant** `"System.String"` instead of a
  method-table-to-name dictionary lookup — every dedup candidate is a `System.String` instance by
  construction (the adapter only streams string-typed candidates), so the pre-retyping analyzer's
  more generic (but, for this analyzer, always-degenerate) `DominantMethodTable`-keyed resolution
  added no real information.
- **Pure, ClrMD-independent helpers kept as-is**: `SelectDuplicates`/`SelectRetentionPathCandidates`/
  `StringFingerprint` preserved with identical names/signatures specifically so the pre-existing
  `StringAnalyzerDuplicateSelectionTests`/`StringAnalyzerRetentionPathTests` (reflection-based)
  keep passing unchanged — only `BuildRetentionPaths`'s signature genuinely changed (now takes
  `IHeapRootPathQuery` instead of `ClrHeap`/`IHeapAnalysisCache`), so those three affected test
  cases were updated to match, not deleted.
- **Gate met**: new `StringAnalyzerRetypingCharacterizationTests` (7 tests, dictionary-backed fakes)
  covering the no-candidate-stream empty-dedup gate, duplicate detection/ranking, distinct strings
  correctly excluded, very-long-string capture, FOH/interned detection, owner-type-bytes pass-through,
  and retention-path search. Old `StringAnalyzerHeapIndexScanTests`/`StringAnalyzerGenerationStatsTests`
  (reflecting removed private internals — `OnHeapEntry`/`MergePartial`/`AggregateStringTypeStats` no
  longer exist on the inner analyzer) deleted outright, same treatment `HangAnalyzerHeapIndexScanTests`/
  `CollectionAnalyzerHeapIndexScanTests` got in their own batches. New `StringAnalyzerRealDumpTests`
  (`[DiscrepancyFact]`, one dump, foreground) passed in 20s against the real 3.5 GB reference dump —
  consistent with every other batch's sub-30s real-dump gate, not a `BoxingAnalyzer`-style stall.
  Full non-real-dump suite (1517 tests) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark`, and `BenchmarkSuite1/Program.cs`'s standalone `RunSingleStringAnalyzer`
  debug harness now construct `StringAnalyzerLegacyAdapter`.

**`ModuleAnalyzer`. Done 2026-09-13.** Previously scoped for Batch 8 (2026-09-11) then deferred —
`dump.object-fields` didn't exist yet, and the doc's own assessment was that this analyzer needs a
brand-new AppDomain capability domain ("nothing today models AppDomains"). Both blockers resolved
here: the ALC-name read goes through the now-existing `IDumpObjectFieldQuery.TryReadField` (no new
Tier-2 primitive needed), and the AppDomain/module surface is folded into `IRuntimeModuleQuery`
(declared 2026-09-09, zero real consumers until now) rather than a wholly separate capability.
- **No bespoke scan participant needed at all** — the only analyzer in this wave that doesn't need
  one. Unlike `StringAnalyzer`/`CrashAnalyzer`/`HangAnalyzer`, nothing here needs data accumulated
  during a shared heap-index or thread-stack scan pass; every fact comes from `IRuntimeModuleQuery`
  or the already-available `IHeapTypeStatisticsQuery`. `ModuleAnalyzerLegacyAdapter` is a plain
  `LegacyAnalyzerAdapter<ModuleAnalyzer>`.
- **`IRuntimeModuleQuery` redesigned, not just implemented** — same story every zero-consumer
  stub's first real batch has hit (`IRuntimeJitQuery`/`IHeapHandleQuery`). The shipped shape
  (`EnumerateModules() -> IEnumerable<ModuleRef>`, an identity-only join key) carried none of the
  facts a module-inventory report needs. Redesigned around a new `RuntimeModuleInfo` (identity +
  facts combined in one shape, same as `HeapSegmentRef`/`RuntimeThreadRef` — deliberately not reusing
  `ModuleRef`, the same `HeapObjectRef.TypeDisplayName`-vs-`TypeRef.CanonicalName` split already
  established) and a new `AppDomainInfo`. Extended with: `EnumerateAppDomains()`,
  `EnumerateModules(AppDomainInfo)` (a deliberately separate, non-deduplicated per-domain walk —
  collapsing it into the address-deduplicated `EnumerateModules()` would silently drop the
  cross-domain-load signal), `EnumerateDeclaredTypeNames(ulong)` (mirrors
  `ClrModule.EnumerateTypeDefToMethodTableMap()`, resolved to names — covers types with zero live
  heap instances, which `IHeapTypeStatisticsQuery` can't enumerate), `TryResolveAssemblyLoadContext`
  (structural per-module fact: `ClrType.AssemblyLoadContextAddress`/`IsCollectible`, decompile-verified
  against the installed ClrMD package), and `GetAssemblyReferences` (the one genuinely new dump-only
  primitive — parses a module's raw ECMA-335 AssemblyRef table via the existing, unchanged
  `AssemblyRefProbe.TryEnumerateAssemblyRefs`, called dump-side so the SDK layer never sees
  `IMemoryReader`). New dump-side `RuntimeModuleQuery` (`DumpDetective.Analysis/Sdk/`), wired into
  `LegacyAnalysisContextTranslator.Translate`'s previously-always-null `RuntimeModules` property.
- **A real, evidence-backed simplification, not assumed**: the pre-retyping analyzer's per-module
  heap footprint (`ModuleAggregator`, now deleted) joined a Phase-1 disk-index module registry
  (`HeapIndexBuildResult.Modules`) against `TypeAggregateIndexEntry` by `ModuleId`. Replaced by
  grouping `IHeapTypeStatisticsQuery.GetTypeStatistics()` by `HeapTypeStatistics.ModuleName` —
  confirmed, by reading `TypeAggregateNameResolver.ResolveModuleName` directly, to already resolve
  to the exact same `Path.GetFileName(ClrType.Module.Name)` normalization the old join produced.
  This also means the heavy-module top-types breakdown no longer needs a separate
  method-table-to-name resolution pass: the per-type dictionary's key already *is* the resolved name.
- **A real bug caught by the real-dump gate, not guessed at**: the first cut gated the heavy-module
  top-types breakdown on a single type's own `TotalSize` meeting `HeavyModuleWarningThresholdBytes`,
  instead of the module's own aggregate total across all its types — silently dropping the
  breakdown for any module whose heaviness comes from many small types rather than one big one
  (`System.Runtime.Serialization.dll` on the real reference dump). Fixed with a genuine two-pass
  design (module aggregate totals first, then a second pass collecting per-type entries only for
  modules that cleared the aggregate threshold), matching the pre-retyping `ModuleAggregator`'s own
  two-pass shape exactly — caught by the pre-existing `ModuleAnalyzerTopTypesRealDumpTests` (updated
  to construct `ModuleAnalyzerLegacyAdapter`, not deleted, since it already exercised exactly this
  feature), not a newly-written test.
- **Gate met**: new `ModuleAnalyzerRetypingCharacterizationTests` (13 tests, dictionary-backed fakes)
  — module inventory/dedup, version-conflict vs. unknown-identity-duplicate classification,
  AssemblyRef mismatch detection, ALC-name resolution via `TryReadField`, AppDomain/cross-domain
  analysis (including the framework-module exclusion), per-module live-type-count cross-referencing,
  and a regression test for the two-pass heavy-module gate fix. Pre-existing
  `ModuleAnalyzerTopTypesRealDumpTests` updated (not deleted) to construct
  `ModuleAnalyzerLegacyAdapter` — passed in 9s against the real 3.5 GB reference dump after the
  two-pass fix. New `ModuleAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground)
  covering module inventory/AppDomains/AssemblyRef audit passed in 8s against the same dump. Full
  non-real-dump suite (1530 tests) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark` now construct `ModuleAnalyzerLegacyAdapter`; `ModuleAnalyzerBenchmark`
  retargeted at `AnalyzerBenchmarkBase<ModuleAnalyzerLegacyAdapter>` and its `ModuleAggregator.Aggregate`-only
  benchmark removed along with `ModuleAggregator` itself (dead code once nothing called it).

**`StaticRootLeakDetector`. Done 2026-09-13.** Unusual batch: exploration surfaced a critical,
pre-existing, unrelated bug — this analyzer has never produced a single real finding on any dump.
The root cause lives upstream in `RootIndexWriter`/`IHeapAnalysisCache.GetStaticRootedAddresses`
(filters for `ClrRootKind` values 9/10, which don't exist in ClrMD 3+ — statics aren't reported as
GC roots at all there, only reachable by walking `ClrType.StaticFields` per AppDomain). A verified
fix exists on branch `fix/static-root-detection` (commit `4d7e267b`) but is deliberately unmerged —
switching this analyzer on for the first time costs ~453s/63.7 GB allocated on a 3.3 GB dump
(uncapped, per this project's "no sampling" convention), a cost that needs addressing before
merging. **Confirmed with the user before implementing**: this batch retypes the analyzer's
consumption logic only, preserving today's real (broken, zero-output) behavior exactly, with a
prominent doc comment on the retyped class documenting the bug so nobody mistakes zero output for a
retyping regression — it does not fix the bug or its perf prerequisite.
- **No bespoke scan participant needed** — like `ModuleAnalyzer`, this analyzer iterates a bounded
  root population (`IHeapRootQuery.EnumerateRoots()`), not a heap-index scan.
  `StaticRootLeakDetectorLegacyAdapter` is a plain `LegacyAnalyzerAdapter<StaticRootLeakDetector>`.
- **`IHeapRootQuery`'s second real consumer** — `HeapRootRef`'s `OwnerTypeName`/`FieldName`/
  `AppDomainId` (built for `GCRootAnalyzer`'s Batch 3 retyping) already cover exactly what the
  pre-retyping analyzer's own `GetOrBuildRootTriples`/`GetStaticRootedAddresses`/
  `GetStaticFieldsByRootAddress` combined provided — no gap to close there.
- **`IHeapDominatorQuery.EnumerateRetainedObjects` added** (new, uncapped — first real consumer of
  a per-object dominator-subtree enumeration; every prior `IHeapDominatorQuery` consumer only
  needed the aggregate byte total via the already-existing `TryGetRetainedSize`). Mirrors
  `IDominatorTreeProvider.EnumerateRetainedSet` exactly, deliberately with no `maxNodes`/`maxDepth`
  parameter — matching this project's "no sampling" convention and the pre-retyping analyzer's own
  unbounded walk. The ~453s cost this incurs once real data exists is a known, already-discussed
  cost of correct behavior, not something this batch quietly capped.
- **`IDumpObjectFieldQuery.HasDelegateField` added** (new Tier-2 primitive) — ports the pre-retyping
  analyzer's own `HasDelegateFields` (checks both `ClrType.Fields` and `ClrType.StaticFields`, since
  a field-like event compiles to a static backing delegate field, not an instance one) verbatim,
  cached per method table dump-side.
- **Dropped, matching established precedent**: `RetainedSizeCandidateSelector.RequiresWalk`'s shape
  pre-check — the same accepted simplification `GCRootAnalyzer`'s and `MemoryAnalyzer`'s own
  batches already made for the identical `MethodTableHasOutgoingRefs` pre-filter, for the same
  reason (the walk must stay uncapped anyway, so skipping the pre-check costs at most one trivial
  single-object iteration for a true leaf root).
- **A real, pre-existing test-setup gap found and fixed, not introduced**: the pre-existing
  `StaticRootLeakDetectorDominatorTreeDiscrepancyTests` never built a heap index (the pre-retyping
  analyzer's own `GetTotalManagedBytes` had a cheap segment-byte-sum fallback for that case). The
  retyped version reuses the shared `IHeapTypeStatisticsQuery` surface for this figure, whose own
  no-index fallback (`StatisticsCache`'s live parallel type-classification walk) hit a real,
  pre-existing regex-timeout on the reference dump under parallel load. Fixed by building the index
  up front in the test, matching every other real-dump test's own setup — not a production
  behavior change, since the real pipeline always builds an index before running analyzers anyway.
- **Gate met**: new `StaticRootLeakDetectorRetypingCharacterizationTests` (12 tests, dictionary-backed
  fakes) — carrying essentially all the real behavioral coverage for this batch, since the upstream
  bug means the real-dump gate can't exercise positive data: significant-root filtering, root
  description (owner type/field name, non-default-AppDomain suffix), per-type/namespace retained-set
  breakdown, collection/event-handler (instance and static field) detection, Gen2-or-larger fraction,
  root-path evidence, and the no-dominator-tree fallback. Pre-existing
  `StaticRootLeakDetectorDominatorTreeDiscrepancyTests` updated (not deleted) to construct
  `StaticRootLeakDetectorLegacyAdapter`, assert `TotalManagedHeapBytes` is real and non-zero (since
  it doesn't depend on the broken path) while `RootCount == 0` is documented as the expected value —
  passed in 6s against the real 3.5 GB reference dump. Full non-real-dump suite (1542 tests) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark` now construct `StaticRootLeakDetectorLegacyAdapter`;
  `StaticRootLeakDetectorBenchmark` retargeted at
  `AnalyzerBenchmarkBase<StaticRootLeakDetectorLegacyAdapter>`.

**`EventLeakAnalyzer`. Done 2026-09-13.** By far the largest batch in the wave (~3,000 lines
pre-retyping across `EventLeakAnalyzer.cs`, `EventLeakFastScanner.cs`, and six
`Analyzers/Leaks/EventLeak/*.cs` helpers) — its hot scan path deliberately reads delegate
`_target`/`_invocationList` fields via raw `IMemoryReader.ReadPointer(address + precomputedOffset, ...)`
rather than named-field lookups, specifically to avoid ClrMD overhead the source's own history
documents as a real, measured regression (a scope-widening mistake once pushed
`PublisherRegistry.Build` from ~22.8s to ~48s on the 3.3GB reference dump; that build is documented
as ~57% of wall time on a 25.6GB dump).
- **Everything scan-and-detail-building stays dump-side, unchanged, in a bespoke serial
  `IHeapIndexScanParticipant`** (`EventLeakAnalyzerLegacyAdapter` — not
  `IParallelHeapIndexScanParticipant`; the pre-retyping analyzer never parallelized this scan
  either, genuinely simpler to port than `StringAnalyzer`/`CrashAnalyzer`/`HangAnalyzer` in that one
  respect). `PublisherRegistry`, `EventLeakFastScanner`, `DelegateChainWalker`,
  `FieldBackedDelegateShape`, `EventNameResolver`, `DelegateLayoutDiscovery`, `BuildRootHintMap`,
  `SweepRegistryStatics`, `CreateLeakInfo`, and every delegate-chain/subscriber-resolution helper
  ported essentially verbatim. Retargeting this path onto the Tier-2
  `IDumpObjectFieldQuery.TryReadField` (named-field, per-call `ClrType.GetFieldByName` resolution)
  would very likely have reintroduced exactly the class of regression this project already hit and
  fixed twice (`BoxingAnalyzer`, `CollectionAnalyzer`) — deliberately not attempted.
- **The retyped inner `Sdk.Analysis.EventLeakAnalyzer` needed zero brand-new Tier-2 primitives** —
  the adapter hands it a finished `List<EventGroupInfo>` (already-resolved subscriber type/method
  names, disposal state, root hints — Core.Models POCOs with no ClrMD dependency, the natural
  hand-off boundary) plus scalar scan counts via a new `EventLeakAnalyzerOptions` wrapper (mirrors
  `CrashAnalyzerOptions`/`StringAnalyzerOptions`). The inner analyzer does only the capability-light
  tail: retained-bytes backfill via the already-existing `IHeapDominatorQuery.TryGetRetainedSize`,
  average-size-per-type via `IHeapTypeStatisticsQuery.GetTypeStatistics()` (replacing
  `IHeapAnalysisCache.GetOrBuildTypeStatistics`/`BuildTypeSizeMap` directly), root-path evidence via
  `IHeapRootPathQuery.TryFindRootPath` (replacing `RootPathFinder`/`ReferenceGraph`/
  `cache.GetOrBuildValidRoots` — same retarget every prior batch's evidence search made), severity
  scoring, correlation folds, and `EventLeakDomainResult` assembly — the same "adapter accumulates
  real data, hands its inner analyzer the finished result" shape `HangAnalyzer`'s own retyping
  established, applied at a larger scale here since more of this analyzer's own logic (not just
  field reads) is genuinely dump-side-shaped.
- **Dropped, confirmed dead by search, not assumed**: `HasDelegateFields(ClrType)`,
  `MergeAccumulatorEntry`, and `IsFieldBackedDelegate` — zero callers anywhere in the codebase even
  before this retyping. `AddFindings` (an `InsightFinding` builder) was likewise dead — defined but
  never invoked from `AnalyzeAsync` or anywhere else.
- **Mechanical port verified by re-running every pre-existing test unchanged in place, not just
  compiling**: `EventLeakAnalyzerAccuracyTests` (90 EventLeak-area tests total, including
  `PublisherRegistryTests`/`EventLeakTrendComparerTests`/`EventLeakFindingGeneratorTests`/
  `EventLeakSectionBuilderTests`) reference dozens of static helpers by name
  (`CalculateSeverity`/`GroupAccumulator`/`AddToAccumulator`/`LooksLikeEventFieldName`/
  `GetSubscriberCountBucketIndex`/`SubscriberCountHistogramBuckets`/`ParseRootPublisher`/
  `BuildEventNameSet` moved to the adapter; `BuildSubscriberCountHistogram`/
  `BuildTopHandlerMethodsAcrossGroups`/`BuildTopSubscriberTypesAcrossGroups`/
  `EstimateGroupRetainedBytes`/`IsTimerEvent`/`IsPropertyChangedEvent` stayed on the inner analyzer)
  — updating each test call site to its new home, all 90 passed unchanged, giving real confidence
  the mechanical split preserved behavior exactly, not just compiled.
- **Gate met**: new `EventLeakAnalyzerRetypingCharacterizationTests` (5 tests) covering only what's
  genuinely new (the capability-driven entry point, the dominator-vs-type-size-map backfill, the
  root-path evidence retarget) — the pre-existing accuracy-test suite already covers the pure logic
  exhaustively. New `EventLeakAnalyzerRealDumpTests` (`[DiscrepancyFact]`, one dump, foreground) —
  the single highest-value real-dump gate in this batch given the documented perf history — passed
  in 21s against the real 3.5 GB reference dump, well within every other batch's sub-30s baseline,
  confirming the hot-path-stays-dump-side design didn't reintroduce the historical regression.
  Pre-existing `PublisherRegistryTests` (6 tests, real-dump) re-ran standalone afterward, still
  passing (~1m26s total — unchanged from its own historical cost, since `PublisherRegistry.cs`
  itself was untouched). Full non-real-dump suite (1547 tests) passes.
- Call sites updated: `DefaultAnalyzerFeatureModuleCatalog`, `FullPipelineBenchmark`,
  `SmallDumpLatencyBenchmark` now construct `EventLeakAnalyzerLegacyAdapter`;
  `EventLeakAnalyzerBenchmark` retargeted at `AnalyzerBenchmarkBase<EventLeakAnalyzerLegacyAdapter>`.

Remaining Tier-1+Tier-2 analyzers (3, see "Status" at the top) haven't been started yet.
`LeakCandidateAnalyzer` stays last per explicit direction — it needs cross-analyzer result access
and deferred-execution-ordering modeling neither this wave nor Tier 1 has built.

5. **Real-dump verification stays one-at-a-time, in the foreground**, per this project's standing
   rule — run it once per batch on the reference dumps, not once per analyzer, to keep measurement
   cost proportional to what a mechanical retype-and-characterize change actually risks.
6. **Cutover.** Once all 36 are on the new types (29 done as of 2026-09-13, see "Status" at the top),
   delete the `LegacyAnalyzerAdapter`, delete `Core.Abstractions.IAnalyzer`/`Models.AnalysisContext`,
   update `DependencyDirectionTests`/`SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl`-adjacent
   rules, and Phase 3's capability-attribute discovery and Phase 5's observation emission — both
   already written assuming this exists — finally have real ground to build on.

## Explicitly out of scope for this plan

- Phase 3's package split (`Plugins.Memory`, `Plugins.Gc`, ...) — analyzers keep their current file
  locations; only their interface/context type changes here.
- Phase 5's observation emission — `AnalyzerDomainResult` output must stay byte-identical through
  every step above; no analyzer starts emitting `Observation`s as a side effect of retyping.
- Phase 4's session DAG.
- A full source-neutral object/field model (Tier 2 stays dump-specific, deliberately, per the
  finding above) — revisit only if a second source ever needs field-level introspection, which no
  current or planned source does.

## Risk / effort

Larger than § 10 point 8 estimated, now that the field-introspection number is in. Tier 1 (11
analyzers, new SDK types, adapter) is genuinely Phase-1/2-sized additive work. Tier 1+Tier 2 (24
analyzers) is comparable in size to Phase 3's entire "touches all ~30 analyzers" estimate on its
own, before Phase 3's packaging work even begins — meaning this retyping step and Phase 3 are not
sequential-and-separate the way the plan currently implies; they will likely need to happen
together per-analyzer (retype + declare capabilities in the same pass) to avoid touching each of
the 35 files twice.
