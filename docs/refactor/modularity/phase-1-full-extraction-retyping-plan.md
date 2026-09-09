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
  `FinalizableObjectAnalyzer`, `HeapTopologyAnalyzer`, `ThreadAnalyzer`, `JitAnalyzer`,
  `ModuleAnalyzer`.
- **Tier 1 + Tier 2** (~24): everything doing field introspection — `WcfChannelAnalyzer`,
  `HttpObjectAnalyzer`, `DbConnectionAnalyzer`, `SqlCommandAnalyzer`, `SqlTransactionAnalyzer`,
  `SqlConnectionPoolAnalyzer`, `TimerLeakAnalyzer`, `CrashAnalyzer`, `ObjectShapeAnalyzer`,
  `CollectionAnalyzer`, `StringAnalyzer`, `BoxingAnalyzer`, `ArrayAnalyzer`,
  `AsyncStateMachineAnalyzer`, `AsyncTaskAnalyzer`, `EventLeakAnalyzer` (+ its
  `EventLeak/*` helpers), `WeakReferenceAnalyzer`, `StaticRootLeakDetector`, `DominatorAnalyzer`,
  `LockGraphAnalyzer`, `ReferenceChainAnalyzer`, `HangAnalyzer`, `ThreadStackClusterAnalyzer`,
  `LeakCandidateAnalyzer`.

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
   so step 3's pilot doesn't also have to prove pipeline-wiring changes at the same time.
3. **Pilot: migrate exactly one Tier-1-only analyzer.** Recommend `GCGenerationAnalyzer` — smallest
   Tier-1-only file, two `heap.GetTypeByMethodTable` calls, no field introspection, no dependency on
   any other analyzer. Gate: existing golden/characterization tests for it must pass byte-identical
   before/after (Phase 0's tightened per-branch-coverage exit criterion already requires this
   analyzer have that coverage). This is a real, if small, production-behavior change and is the
   right place to pause and confirm the pattern before committing to the other 34 — **not** a change
   to make in the same sweep as step 1's additive work.
4. **Batch the remaining 34, Tier-1-only first, 3–5 analyzers per batch/session**, each batch gated
   the same way as the pilot. Tier-1+Tier-2 analyzers wait until the `dump.object-fields` escape
   hatch (Tier 2) exists — build that once the first Tier-1+Tier-2 analyzer is reached, not
   speculatively up front.
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
