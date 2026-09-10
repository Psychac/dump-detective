# Phase 1 full SDK extraction — capability surfaces + the analyzer retyping step

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
  wait on the `dump.object-fields` escape hatch (still not built, per this plan's own hard-need-basis
  convention) rather than being force-fit into Tier-1-only surfaces that don't actually cover them.
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
   only, both retyped 2026-09-11 (see the thread-quartet plan's §§7–8). `ThreadAnalyzer` itself was
   re-checked too, resolving the thread-quartet plan's own open question: `ClrException.Message`
   (reflected via ilspycmd, not guessed) reads a raw field offset off the exception object, making it
   genuinely Tier 2 — and `ThreadAnalyzer` uses exactly that, in `ThreadExceptionSnapshot.ExceptionMessage`.
   So `ThreadAnalyzer` joins `HangAnalyzer` as the quartet's two genuinely-Tier-2 members, deferred
   alongside `ModuleAnalyzer`/`FinalizableObjectAnalyzer` until the `dump.object-fields` hatch exists —
   **Tier 1 is now fully finished**: every originally-Tier-1-only analyzer, plus both quartet members
   that turned out to be Tier-1-only on re-inspection, are retyped.

5. **Real-dump verification stays one-at-a-time, in the foreground**, per this project's standing
   rule — run it once per batch on the reference dumps, not once per analyzer, to keep measurement
   cost proportional to what a mechanical retype-and-characterize change actually risks.
6. **Cutover.** Once all 35 are on the new types, delete the `LegacyAnalyzerAdapter`, delete
   `Core.Abstractions.IAnalyzer`/`Models.AnalysisContext`, update
   `DependencyDirectionTests`/`SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl`-adjacent rules,
   and Phase 3's capability-attribute discovery and Phase 5's observation emission — both already
   written assuming this exists — finally have real ground to build on.

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
