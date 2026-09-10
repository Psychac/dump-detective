# Phase 1 — Source-Neutral Contracts & Host SDK

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 0** (wire
contracts) and **Layer 1** (SDK). Depends on [phase-0-foundation.md](phase-0-foundation.md).

**Before picking up any more pending work here**, see
[phase-1-sdk-review-findings.md](phase-1-sdk-review-findings.md) — a 2026-09-10 architecture review
of every type shipped so far (21 findings, P0/P1/P2), being fixed before the retyping plan's pilot
migration starts building on top of any of it.

This is the most consequential phase in the plan. Everything downstream — trace, correlation,
plugins, UI — is shaped by what lands here, and getting the identity/temporal model wrong is the
one mistake that's genuinely expensive to undo.

## Goal

Establish a small, stable, source-neutral contract surface that any artifact source, any analyzer,
and any consumer can target without knowing that dumps exist.

## Status: trimmed pass shipped 2026-09-08; schemas/registries + Tier-1 Analysis/ skeleton added 2026-09-09, per § 8's minimum-viable path; retyping pilot (`GCGenerationAnalyzer`) shipped 2026-09-10 — see [phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md) step 3

Per [modularity-plan.md § 8](../modularity-plan.md#8-the-minimum-viable-unified-path--adopted-as-the-chosen-plan-2026-09-08)
(adopted, see [§ 10 point 7](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)):
"identity + capability + observation contracts only... skip the full SDK extraction; just add the
new types." `src/DumpDetective.Sdk/` now exists — net10.0, zero `ProjectReference`s, zero
`PackageReference`s (enforced by
`tests/DumpDetective.Tests/Unit/Architecture/DependencyDirectionTests.cs`'s
`SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl`, per migration step 7 below) — and is not yet
referenced by any existing project. Existing analyzers, `Core.Abstractions.IAnalyzer`, and
`Reporting.Abstractions.IAnalyzerSectionBuilder` are unchanged; this is purely additive, zero
behavior change, matching Phase 0's own rule. First real consumer is Phase 6a.

**Shipped**, matching the target shape below exactly except where noted:
- `Artifacts/`: `ArtifactId`, `ArtifactDescriptor`, `ProcessIdentity`, `Capability`,
  `CapabilityVocabulary` (the known-vocabulary constants from
  [source-model.md § 3](source-model.md), serving as the vocabulary source of truth until the
  registry below lands).
- `Identity/`: `EntityRef`, `TypeRef`, `MethodRef`, `ModuleRef` (not in the original target list —
  added because `TypeRef.Module` needs it), `ThreadRef`, `ObjectRef`, `EntityKind` (not in the
  original list — needed for `EntityRef.Kind`'s discriminator), `MatchFidelity`,
  `EntityCanonicalizer`.
- `Temporal/`: `TimeAnchor`, `TemporalExtent`, `AnchorConfidence`, `TemporalKind` (not in the
  original list — needed for `TemporalExtent.Kind`).
- `Observations/`: `Observation`, `Measure`, `Provenance`, `EvidenceRef`, `IObservationSink`, plus
  `ObservationId`, `MeasureUnit`, `MeasureSemantics`, `FidelityLevel` (supporting types the target
  shape's file list didn't spell out individually).
- `Synthesis/`: `ISynthesisRule`, `Finding`, `ConfidenceBreakdown`, plus `Severity` (a distinct
  SDK-owned enum — cannot reuse `Core.Enums.FindingSeverity` since the SDK has zero deps on Core),
  and `ObservationQuery`/`ObservationMatchSet`/`SynthesisContext` (the types `ISynthesisRule.Match`
  and `SynthesizeAsync` need; explicitly first-cut, not the final declarative-matching design —
  see their own XML doc remarks and
  [observation-and-correlation-model.md § 7](observation-and-correlation-model.md#7-open-questions)).
- `SdkVersion.cs`.
- `EntityCanonicalizer`'s ordinal-stripping logic is grounded in what `tools/EntityJoinSpike/Program.cs`
  already measured recovering real matches on real data, not a fresh guess — see the type's own XML
  doc remarks for exactly which table rows are fully handled vs. conservatively deferred (the
  biggest honest gap: dynamic/reflection-emitted type detection isn't attempted at all, since it
  needs module-level info a name-only canonicalizer doesn't have). Covered by
  `tests/DumpDetective.Tests/Unit/Sdk/EntityCanonicalizerTests.cs` and `IdentityTests.cs`
  (13 + 8 = 21 tests as of the fix below).
- **`EntityRef` JSON-polymorphism bug found and fixed 2026-09-09**, surfaced by Phase 6a/6b's trace
  `report.json` — the first real code outside the SDK's own tests to serialize an `EntityRef`
  through its base type (`Observation.Subjects` is `IReadOnlyList<EntityRef>`). The
  `[JsonPolymorphic]` discriminator was originally named `"kind"`, which collides with
  `EntityRef.Kind` itself (also `"kind"` in camelCase) and throws at serialize time; renamed the
  discriminator to `$kind`, matching the same pattern `AnalysisReportDocument` (Reporting project)
  already uses for its own polymorphic base. Regression-guarded by
  `IdentityTests.EntityRef_SerializesAndRoundTripsThroughThePolymorphicBaseType`, which round-trips
  through the base type specifically — serializing a concrete subtype directly wouldn't have
  exercised the polymorphic path that broke. This is the first real evidence that Phase 1's design
  benefits from an actual downstream consumer exercising it, not just unit tests against the SDK in
  isolation.
- Architecture-conformance harness (Phase 0 item 6, which turned out to already exist — see
  [phase-0-foundation.md](phase-0-foundation.md)) extended with the SDK-boundary rule per migration
  step 7 below.
- **`/schema/DumpDetective.Schema/` shipped 2026-09-09** (except `session-report.schema.json` v3 —
  see Deferred below): `capability-registry.json`, `observation-type-registry.json`,
  `observation.schema.json`, `index-container-format.md`, `CHANGELOG.md`. Unblocked by Phase 6a/6b
  actually landing — real capability/observation-type content and a real wire format to describe
  now exist, so this is no longer the "stubbing empty files now was considered and rejected as
  premature" case the previous version of this doc described. Every file describes what's actually
  shipped and running, not a forward design:
  - `capability-registry.json` mirrors `CapabilityVocabulary.Known` verbatim (29 entries).
  - `observation-type-registry.json` records the three `ObservationType` values real analyzers emit
    today (`gc.pause`, `contention.episode`, `cpu.sample-attribution`) with their real measure keys.
  - `observation.schema.json` was derived from an actual build of `DumpDetective.Sdk` (a throwaway
    probe serializing real SDK types through `TraceReportWriter`'s exact
    `JsonSerializerOptions`), then validated round-trip against that same live output with a Python
    `jsonschema` validator across all five `EntityRef` subtypes — not hand-derived from the C#
    source and never executed. That process surfaced five non-obvious wire-format facts now
    recorded in the schema's own `notes` array (mixed camelCase-property/PascalCase-enum-value
    casing; `$kind` only appearing on the polymorphically-typed `Subjects` field, never on a
    concretely-typed field like `MethodRef.DeclaringType`; `ArtifactId`/`Capability`/`ObservationId`
    serializing as one-key wrapper objects, never bare strings; `ObservationId.ToString()`
    disagreeing with its own JSON form; and 64-bit dump handles exceeding IEEE-754-safe integer
    precision, a real trap for any future JS/TS consumer).
  - `index-container-format.md` documents what the Phase 2/6a "generalized container" actually
    turned out to be: one flat, append-only `CacheSectionId` enum shared by dump and trace sections
    alike (not the string-namespaced `"heap.*"`/`"trace.*"` design the original target shape
    sketched), demonstrated by the four `Trace*` ids Phase 6a/6b added with no format-version bump.
    It also flags, without fixing, that `docs/binary-format.md`'s own header table is now
    significantly stale (`FormatVersion` "Current: 4" there vs. the real
    `CacheContainerFormat.CurrentFormatVersion = 10`, and its 17-entry section table is missing
    over 20 real ids) — pre-existing cache-subsystem debt, unrelated to this generalization, called
    out so it isn't mistaken for something this pass resolved.
  - Conformance enforced by
    `tests/DumpDetective.Tests/Unit/Architecture/SdkRegistryConformanceTests.cs` — this is
    migration step 7's registry-conformance half, previously blocked on the registries not existing.
- **`Analysis/` Tier-1 skeleton shipped 2026-09-09, revised 2026-09-10** — `IAnalyzer.cs`,
  `AnalysisContext.cs`, the three capability attributes, `HeapObjectRef.cs`,
  `AnalyzerProgressReport.cs`, and 14 capability-scoped query interfaces (`IHeapObjectStream`,
  `IHeapObjectLookup`, `IHeapRootQuery`, `IHeapHandleQuery`, `IHeapSegmentQuery`,
  `IHeapFinalizerQueueQuery`, `IHeapSyncBlockQuery`, `IHeapTypeStatisticsQuery`,
  `IHeapReferenceQuery`, `IHeapReverseReferenceQuery`, `IHeapReachabilityQuery`,
  `IHeapDominatorQuery`, `IRuntimeThreadQuery`, `IRuntimeModuleQuery`, `IRuntimeJitQuery` — not in
  the original target-shape file list below, added per the two-tier capability-surface design
  [phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md) worked out).
  `IHeapReachabilityQuery` split out of what was originally a single `IHeapDominatorQuery` on
  2026-09-10, per [phase-1-sdk-review-findings.md](phase-1-sdk-review-findings.md) item 4 — it
  bundled a Stage A product (reachability) with Stage B ones (retained size, immediate dominator,
  thread retention) that aren't actually co-available.
  **This is a new, parallel SDK-side contract, not the move the "Deferred" bullet immediately below
  describes** — `Core.Abstractions.IAnalyzer`/`Models.AnalysisContext` are untouched, no analyzer
  implements the new SDK `IAnalyzer` yet, and nothing in the existing pipeline references any of
  this. Purely additive, zero behavior change, verified by the full non-real-dump test suite passing
  unchanged. Capability-vocabulary items landed alongside it: `heap.dominators` (new, 2026-09-09,
  narrowed 2026-09-10), `heap.reachability` (new, 2026-09-10), and `runtime.locks` (already declared,
  previously unconsumed) — `capability-registry.json` now at 1.2.0. See the plan doc for what's
  still pending (dump-side implementations of these 14
  interfaces, a legacy adapter, and the actual analyzer retyping — none of which are additive/safe
  the way this skeleton was, so none of it has started).

**Deferred**, per § 8's explicit scope:
- `Analysis/`'s remaining pieces (dump-side implementations of the Tier-1 interfaces above, plus
  `Presentation/IAnalyzerSectionBuilder.cs`) and the analyzer retyping itself — this is the "full SDK
  extraction" § 8 explicitly skips, now that the skeleton above has separated "additive SDK
  contracts" (shipped) from "actually rewiring analyzers onto them" (not started). `Core`'s
  `IAnalyzer`/`AnalysisContext` and `Reporting.Abstractions.IAnalyzerSectionBuilder` remain what
  every analyzer actually runs against today.
  - **Investigated 2026-09-09, staying deferred: this is not actually a move.** The real
    `src/DumpDetective.Core/Models/AnalysisContext.cs` carries its own dated boundary decision:
    `// Intentional boundary decision (Phase 7): Core remains dump-runtime-aware. AnalysisContext
    carries ClrRuntime/ClrHeap as shared execution substrate.` — with `public required ClrRuntime
    Runtime { get; init; }` right below it. That directly conflicts with this doc's own design rule
    two sections below ("The SDK knows nothing about ClrMD... `AnalysisContext` exposes
    capability-scoped query surfaces, never `ClrRuntime`/`RuntimeFacade`"): the concrete
    `AnalysisContext` every analyzer uses today cannot move into a zero-`PackageReference` SDK
    without dragging ClrMD in and failing `SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl`.
    Doing this "for real" means designing a *new* capability-scoped `AnalysisContext`/`IAnalyzer` in
    the SDK — which is Phase 2 migration step 3 (splitting `IHeapAnalysisCache` into per-capability
    query surfaces, itself still deferred — see
    [phase-2-artifact-platform.md](phase-2-artifact-platform.md)) — and then migrating all existing
    analyzers onto it. Confirmed by reading the real `AnalysisContext`/`IAnalyzer` source directly,
    not assumed from the target shape's file list. Left deferred as originally scoped; revisiting it
    means reopening Phase 2/3/5, not extending Phase 1. **Scoped in full** — real numbers (35
    analyzers, all 35 touching `ClrHeap`/`ClrRuntime` directly) and a genuine phase-ownership gap for
    the retyping step itself, documented at
    [modularity-plan.md § 10 point 8](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned) —
    not resolved there either, just precisely bounded instead of hand-waved as "~30 analyzers,
    Phase 3/5 territory." **Full detailed plan written 2026-09-09**:
    [phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md) — a further,
    larger finding (24 of 35 analyzers also do field-level object introspection, not just coarse
    heap enumeration) drove a two-tier capability-surface design and a concrete, gated migration
    sequence for the retyping step itself. Still deferred pending execution; the SDK types + Tier-1
    surfaces are additive/safe to build now, the analyzer retyping itself needs a pilot and
    per-batch characterization gates before it touches production analyzer output.
- `Artifacts/IArtifactSource.cs` and `IArtifactIndex.cs` — their `IndexAsync` signature depends on
  `IIndexStorage`/a progress-report type, which are Phase 2 storage types. **Partially superseded
  2026-09-09, then again 2026-09-10**: Phase 2's own trimmed pass shipped `Platform.IndexProgress`
  on 2026-09-09 (see [phase-2-artifact-platform.md](phase-2-artifact-platform.md)) — since retired
  in favor of `Sdk.Analysis.AnalyzerProgressReport` (item 21 of
  [phase-1-sdk-review-findings.md](phase-1-sdk-review-findings.md)) once that SDK type existed to
  remove the duplication. `IIndexStorage` still doesn't exist. `IArtifactSource`/`IArtifactIndex`
  remain correctly deferred on that missing half; left for Phase 2/6a, which are their actual
  consumers.
- `session-report.schema.json` v3 — still needs Phase 4's session model
  (`sources[]`/`timeline`/per-finding source attribution can't be described honestly without a real
  session/artifact model to back them), which still doesn't exist. The rest of the schema directory
  shipped 2026-09-09 (see Shipped above); this one file is the sole remaining gap in it.

## Target shape

```
/sdk
  DumpDetective.Sdk/                      -- zero deps beyond BCL
    Artifacts/
      ArtifactDescriptor.cs   ArtifactId.cs   ProcessIdentity.cs
      IArtifactSource.cs      IArtifactIndex.cs
      Capability.cs           CapabilityVocabulary.cs
    Identity/
      EntityRef.cs  TypeRef.cs  MethodRef.cs  ModuleRef.cs
      ThreadRef.cs  ObjectRef.cs
      MatchFidelity.cs        EntityCanonicalizer.cs
    Temporal/
      TimeAnchor.cs  TemporalExtent.cs  AnchorConfidence.cs
    Observations/
      Observation.cs  Measure.cs  Provenance.cs  EvidenceRef.cs
      IObservationSink.cs                -- analyzers emit through this, streaming
    Analysis/                            -- skeleton shipped 2026-09-09, see Status above;
                                          -- dump-side implementations + analyzer retyping pending
      IAnalyzer.cs            AnalysisContext.cs
      RequiresCapabilityAttribute.cs  OptionalCapabilityAttribute.cs
      AnalyzerModuleAttribute.cs
      HeapObjectRef.cs  AnalyzerProgressReport.cs      -- not in the original list, added with the skeleton
      IHeapObjectStream.cs  IHeapObjectLookup.cs  IHeapRootQuery.cs  IHeapHandleQuery.cs
      IHeapSegmentQuery.cs  IHeapFinalizerQueueQuery.cs  IHeapSyncBlockQuery.cs
      IHeapTypeStatisticsQuery.cs  IHeapReferenceQuery.cs
      IHeapReachabilityQuery.cs  IHeapDominatorQuery.cs   -- split 2026-09-10, see Status above
      IRuntimeThreadQuery.cs  IRuntimeModuleQuery.cs  IRuntimeJitQuery.cs
    Synthesis/
      ISynthesisRule.cs  Finding.cs  ConfidenceBreakdown.cs
    Presentation/
      IAnalyzerSectionBuilder.cs
    SdkVersion.cs

/schema
  DumpDetective.Schema/
    session-report.schema.json      -- v3: session-scoped, N artifacts, source attribution
    observation.schema.json         -- the fusion wire format
    capability-registry.json        -- canonical capability vocabulary + versions
    observation-type-registry.json  -- canonical ObservationType namespace
    index-container-format.md       -- generalized from docs/binary-format.md
    CHANGELOG.md                    -- semver, extends docs/schema-versioning.md policy
```

## Key design decisions

- **The SDK knows nothing about ClrMD, heaps, or dumps.** If `DumpDetective.Sdk` needs a ClrMD
  reference, the boundary has failed. `AnalysisContext` exposes capability-scoped query surfaces,
  never `ClrRuntime`/`RuntimeFacade`. Enforced by the Phase 0 conformance test.
- **Identity and temporal model land here, fully** — `EntityRef` canonicalization rules,
  `MatchFidelity`, `TimeAnchor`, `AnchorConfidence`. These are *the* cross-source join primitives;
  they cannot be retrofitted cheaply once 30 analyzers and two sources depend on them. Detail in
  [source-model.md § 4–5](source-model.md).
- **Observations are streamed, not returned.** `IObservationSink` rather than
  `IReadOnlyList<Observation>` on the return type — a trace analyzer may emit millions, and the
  project's no-full-materialization rule applies to observations exactly as it does to heap
  objects. This is a small API decision with large consequences; getting it wrong forces a
  breaking change later.
- **Registries are data, not code.** Capability names and observation types live in checked-in
  JSON registries with versioning, so plugins can be validated against them at build time and the
  vocabulary can't fragment (see [observation-and-correlation-model.md § 7](observation-and-correlation-model.md)).
- **`AnalyzerDomainResult` stays, deliberately.** It remains in the SDK as a near-empty base for
  presentation payloads. Concrete subtypes travel with their plugin (Phase 3). Findings and trends
  stop being derived from it in Phase 5 — but not yet.
- **Schema v3 is session-scoped from day one.** Even though only dumps exist at this phase, the
  report schema models `sources[]`, `timeline`, and per-finding source attribution *now*. Adding
  those later is a breaking schema change; adding them now costs almost nothing while there's one
  source kind populating them.

## Migration steps

1. ~~Create `DumpDetective.Sdk`; move `IAnalyzer`, `IAnalyzerSectionBuilder` from
   `Core.Abstractions`, trimmed to the Phase 0 inventory.~~ **Skipped, per § 8** — this is the "full
   SDK extraction" the adopted minimum-viable path explicitly defers. `DumpDetective.Sdk` was
   created (see Status above), but `IAnalyzer`/`IAnalyzerSectionBuilder` were not moved. **Still
   accurate as a "move" 2026-09-09** — `Core.Abstractions.IAnalyzer` still hasn't moved anywhere.
   What changed: a new, parallel SDK `IAnalyzer`/`AnalysisContext` (Tier-1 skeleton, see Status
   above) now exists *alongside* the untouched `Core` one, per
   [phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md)'s staged
   approach — not a move, a second implementation nothing yet targets, so this step's "skipped"
   status stands until that plan's retyping step actually retires the `Core` original.
2. Author the new identity/temporal/observation/capability types. Genuinely new code — the largest
   greenfield chunk in the plan. **Done 2026-09-08** for the § 8-trimmed set — see Status above for
   exactly what shipped vs. what's still deferred (`IArtifactSource`/`IArtifactIndex`, the schema
   files).
3. **Entity-join spike (do this before step 4, not after).** A throwaway probe that pulls
   method/type names out of a `.nettrace` and diffs them against ClrMD-side names from a dump of
   the *same process*, measuring join rate per entity kind. No trace source, no index, no
   analyzers — days of work. Two payoffs: it's the go/no-go signal for the entire multi-source
   thesis (if names don't join, Phase 7 is worthless regardless of engineering), and it replaces
   assumption with measurement in the canonicalizer design below. Without it, step 4 is built on
   guesses about how each source formats names.
3a. ~~**TraceEvent dependency spike (same tier of risk, same cost to check).** Before Phase 6
   commits to `Microsoft.Diagnostics.Tracing.TraceEvent` / `EventPipeEventSource`, confirm: it
   actually streams a multi-GB `.nettrace` rather than buffering it whole, its licensing is
   compatible with this project, and its memory behavior holds up under the project's
   bounded-memory rules.~~ **Resolved 2026-09-08 — go, with a design correction.** See below.
4. Implement `EntityCanonicalizer` with the normalization rules and fidelity ratings from
   [source-model.md § 4](source-model.md), informed by the spike, with an extensive test corpus of
   real type/method names (generics, async state machines, lambdas, local functions, arrays) —
   this is the component most likely to be subtly wrong and most expensive to be wrong about.
   **First pass done 2026-09-08** — grounded in the ordinal-stripping technique
   `tools/EntityJoinSpike/Program.cs` already measured recovering matches on real data, covered by
   21 tests, but explicitly not the "extensive real-world corpus" hardening this step calls for
   (that needs Phase 6a's larger cross-source corpus). See the Status section above and the type's
   own XML doc remarks for the precise, honestly-stated scope — including one known gap (dynamic/
   reflection-emitted type detection isn't attempted).
5. Write `session-report.schema.json` (v3) and `observation.schema.json`; generalize
   `docs/binary-format.md` into the versioned container spec with namespaced sections. **Done
   2026-09-09, except `session-report.schema.json` v3** — Phase 6a/6b landing unblocked real
   content for `observation.schema.json`, `capability-registry.json`,
   `observation-type-registry.json`, and `index-container-format.md` (see Status above for what
   each actually contains and how it was validated). `session-report.schema.json` v3 stays deferred
   — still needs Phase 4's session model, which doesn't exist. "Namespaced sections" shipped as a
   flat, append-only `CacheSectionId` enum shared across artifact kinds rather than the string
   `"heap.*"`/`"trace.*"` namespacing originally sketched — see `index-container-format.md` for why
   that's the real, cheaper equivalent, not a shortfall.
6. ~~Retire or shrink `DumpDetective.Core` per what Phase 0's inventory shows is left.~~ **Not
   applicable to the § 8-trimmed pass** — step 1 (the extraction this cleanup follows from) was
   itself skipped, so there's nothing yet to retire from Core.
7. Add SDK-boundary and registry-conformance rules to the architecture test. **Done 2026-09-09** —
   SDK-boundary half done 2026-09-08, see `SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl` in
   `DependencyDirectionTests.cs`, extending the harness Phase 0 discovered already exists rather
   than inventing a new one. Registry-conformance half done 2026-09-09, once step 5's registries
   existed to validate against — see `SdkRegistryConformanceTests.cs`
   (`CapabilityRegistry_ShouldMatchCapabilityVocabularyExactly`,
   `ObservationTypeRegistry_ShouldContainEveryObservationTypeRealAnalyzersEmit`).

### TraceEvent dependency spike — measured, 2026-09-08

Ran `tools/TraceEventSpike` against a real ETW capture (`HighCPU.etl`, 54.9 MB, 1,529,978 events,
one of the artifacts alongside the entity-join spike's dump pair). No `.nettrace` (EventPipe)
sample was available, so this tests `ETWTraceEventSource` as a proxy for `EventPipeEventSource` —
both are `TraceEventDispatcher` subclasses in the same package sharing the same callback-dispatch
architecture, but this is not a direct test of the EventPipe reader. Re-run against a real
`.nettrace` before treating this as final for Phase 6.

**Licensing: clear.** `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.2 is MIT-licensed
(`license type="expression">MIT`, from the official PerfView feed). No concern.

**Streaming behavior: confirmed, and it's exactly what Phase 6 needs.** Raw single-pass streaming
via `ETWTraceEventSource` + `AllEvents` processed all 1,529,978 events in 0.6–0.9 s with a working-set
delta that stayed flat at **7 MB for the entire pass**, sampled every 100,000 events from the first
sample to the last — no growth correlated with events processed or bytes read. This is genuine
`O(1)`-relative-to-trace-size streaming, matching the bounded-memory discipline this project already
requires of heap scanning.

**Design correction: `TraceLog.OpenOrConvert` — used by `tools/EntityJoinSpike` — is *not* that API,
and Phase 6's ingest path must not be built on it.** Converting the same 54.9 MB trace from scratch
took 6.0 s, peaked at **232 MB working-set delta (4.2× the source file size)**, and wrote a **149.7 MB
`.etlx` index (2.73× the source file size) to disk**. Reloading an already-converted `.etlx` is cheap
(45 MB, 0.4 s) — the cost is front-loaded into the one-time conversion, not amortized away. Both
ratios are roughly constant per byte of input in this run, which means they're the kind of ratio
that gets dangerous at scale: extrapolated to a multi-GB trace, `TraceLog`'s conversion step alone
could need several times the trace size in RAM and produce a multi-GB `.etlx` file on disk before a
single analyzer runs — precisely the pattern this project's core philosophy forbids for dumps, and
there is no reason to accept it for traces. `TraceLog` remains reasonable for what
`tools/EntityJoinSpike` used it for (a one-off research spike, or later, targeted symbol/stack
resolution on an already-bounded subset) — it must not be the API `IArtifactSource.IndexAsync`
builds its bulk ingest on. That path belongs on the raw event-callback readers
(`ETWTraceEventSource`/`EventPipeEventSource`), extracting only the minimal per-event fields Phase 2's
columnar/intern primitives need, streamed straight to disk exactly as the heap scanner does today.

**Decision: go**, with that correction folded into [phase-6-trace-source.md](phase-6-trace-source.md)'s
ingest design.

### Cross-checked against a sibling implementation (`Rohit_DumpDetective`) — and against our own data

A second, independently-built tool in the same lineage (`d:\POC\Rohit_DumpDetective`) already ships
trace ingest and dump+trace correlation in production. Its `TraceOpener` does the opposite of what
this spike recommended: every trace opens via `TraceLog.OpenOrConvert` /
`TraceLog.CreateFromEventPipeDataFile` / `TraceLog.CreateFromEventTraceLogFile` — the exact
non-streaming, full-index-materializing API this document told Phase 6 not to build bulk ingest on.
Its `TraceOpener.cs` contains a hardcoded parser for `TraceLog`'s own conversion log referencing a
conversion example of a 27,687 MB ETL producing a 13,966 MB `.etlx` (~0.5×, i.e. *shrinking*) — the
opposite direction from this spike's 2.73×/4.2× (54.9 MB sample). An earlier version of this section
treated that number as scale-correcting evidence against this project's own spike. **That was wrong,
and a real measurement from this project's own data corrects it:**

`D:\Dumps\08-05\etls\HighCPU_11.etl` (912.1 MB, a real PerfView capture from 2026-05-08) has a
`HighCPU_11.etlx` sitting next to it on disk (2,191,615,082 bytes = 2090.1 MB, converted 2026-09-08,
the same day as this investigation) — **2.29× growth**, at near-GB scale, on real first-party data.
That's the same direction and the same rough magnitude as this spike's 54.9 MB sample (2.73×/4.2×),
not the sibling's 0.5×. Two real, same-direction measurements from this project's own data now exist
at two different scales; the sibling's single number, embedded in a log-format-parsing code comment
with no attached measurement methodology, cannot outweigh that — it may not even be a real captured
conversion (it could be an illustrative value written while implementing the regex, or a
differently-shaped workload where kernel-stack density or symbol resolution behaves very
differently). **Conclusion reverts to the original: `TraceLog.OpenOrConvert`'s non-streaming,
size-proportional growth is real and holds at the scales measured so far — do not build
`IArtifactSource.IndexAsync`'s bulk ingest on it.**

What still stands from reading the sibling's code, independent of the reverted numeric claim above:

1. **`TraceLog` gives working stack/symbol/method-name resolution for free.** The raw
   event-callback path this document recommends (`EventPipeEventSource`/`ETWTraceEventSource`) does
   not — building that resolution ourselves is real, previously-uncosted engineering work that
   Phase 6 must budget for explicitly, not assume away.
2. The sibling's single-pass fan-out dispatcher (`TraceEventDispatcher.Dispatch`: one iteration over
   `trace.Events`, N `ITraceEventConsumer`s, a `WantsEvent(meta)` filter evaluated once per unique
   event name so uninterested consumers pay nothing) is a good pattern to copy into Phase 6's ingest
   design regardless of which underlying API is chosen — it happens to run on top of `TraceLog` in
   their code, but the "one pass, many consumers, filter before you pay" shape is API-independent.
3. Their conversion is disk-cached per trace file (`.ddcache/<stem>/<stem>.etlx`, staleness-checked
   by timestamp) and reused across runs, which is the right mitigation *if* a non-streaming approach
   is ever used for anything — but it doesn't change the per-conversion cost, only how often it's
   paid, and this project already has two real measurements saying that cost is a multi-× blowup,
   not a reduction.

**Still worth doing before Phase 6 commits:** measure a real `.nettrace` (EventPipe), not just `.etl`
(ETW) — every measurement so far, on both sides, has been ETW. Folded into
[phase-6-trace-source.md § Ingest](phase-6-trace-source.md#ingest).

## Exit criteria

**Note (2026-09-08): these are the exit criteria for the full, untrimmed Phase 1.** Under the
adopted § 8 path, several don't apply yet — marked below rather than silently left unmet.

- `DumpDetective.Sdk` builds standalone, zero project references. **Done** — also zero package
  references, and enforced by a standing test (see migration step 7 above), not just true today.
- ~~Every existing analyzer compiles against the SDK (still emitting domain results; observations
  come in Phase 5).~~ **Not applicable to the § 8-trimmed pass** — this criterion presumes step 1's
  full extraction (moving `IAnalyzer` into the SDK), which § 8 explicitly skips. Existing analyzers
  are unchanged and don't reference the SDK at all yet. **Still true 2026-09-09** despite the new
  Tier-1 `Analysis/` skeleton (see Status above) existing now — zero analyzers implement it; this
  criterion becomes live only once
  [phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md)'s pilot
  migration begins.
- **Entity-join spike has produced a measured join rate per entity kind**, and that measurement —
  not an assumption — informs the canonicalizer's fidelity ratings. A poor result here is a
  legitimate trigger to stop and reconsider Phases 6–7 before investing in them. Caveat accepted as
  residual risk 2026-09-08 (see
  [modularity-plan.md § 10 point 3](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)):
  both measured pairs are the same WCF/EF-on-`w3wp.exe` app family; no non-WCF/EF sample was
  obtainable to widen the corpus, so the fidelity ratings this spike informs are validated for that
  shape only, not generalized.
- ~~TraceEvent dependency spike has confirmed streaming behavior and license compatibility, or has
  surfaced a blocker early enough to change the Phase 6 plan while that's still cheap.~~ **Done** —
  see the measured results above. Licensing clear, raw streaming API confirmed bounded-memory;
  Phase 6's ingest design corrected to avoid `TraceLog.OpenOrConvert` for bulk ingestion.
- ~~`EntityCanonicalizer` passes a real-world name corpus with documented fidelity per case.~~
  **Partially done** — passes a hand-written unit corpus (21 tests) grounded in the entity-join
  spike's proven technique; the "extensive real-world corpus" this criterion actually means is
  Phase 6a's job (see migration step 4 above).
- **Schemas + registries exist, versioned, with conformance tests.** **Done 2026-09-09**, except
  `session-report.schema.json` v3 (still genuinely blocked on Phase 4's session model, not
  deferrable-by-choice like the rest of this list was). See Status above.

## Risk / effort

**High effort, high consequence, low immediate visible payoff** — the phase most at risk of being
skipped or rushed because it ships no user-facing value. Resist that. The identity model in
particular is load-bearing for every correlation claim the product will ever make; a weak
canonicalizer produces plausible-looking false correlations, which is worse than no correlation.
Recommend treating `EntityCanonicalizer` as its own reviewed, test-heavy deliverable.
