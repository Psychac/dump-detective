# Phase 1 — Source-Neutral Contracts & Host SDK

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 0** (wire
contracts) and **Layer 1** (SDK). Depends on [phase-0-foundation.md](phase-0-foundation.md).

This is the most consequential phase in the plan. Everything downstream — trace, correlation,
plugins, UI — is shaped by what lands here, and getting the identity/temporal model wrong is the
one mistake that's genuinely expensive to undo.

## Goal

Establish a small, stable, source-neutral contract surface that any artifact source, any analyzer,
and any consumer can target without knowing that dumps exist.

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
    Analysis/
      IAnalyzer.cs            AnalysisContext.cs
      RequiresCapabilityAttribute.cs  OptionalCapabilityAttribute.cs
      AnalyzerModuleAttribute.cs
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

1. Create `DumpDetective.Sdk`; move `IAnalyzer`, `IAnalyzerSectionBuilder` from
   `Core.Abstractions`, trimmed to the Phase 0 inventory.
2. Author the new identity/temporal/observation/capability types. Genuinely new code — the largest
   greenfield chunk in the plan.
3. **Entity-join spike (do this before step 4, not after).** A throwaway probe that pulls
   method/type names out of a `.nettrace` and diffs them against ClrMD-side names from a dump of
   the *same process*, measuring join rate per entity kind. No trace source, no index, no
   analyzers — days of work. Two payoffs: it's the go/no-go signal for the entire multi-source
   thesis (if names don't join, Phase 7 is worthless regardless of engineering), and it replaces
   assumption with measurement in the canonicalizer design below. Without it, step 4 is built on
   guesses about how each source formats names.
4. Implement `EntityCanonicalizer` with the normalization rules and fidelity ratings from
   [source-model.md § 4](source-model.md), informed by the spike, with an extensive test corpus of
   real type/method names (generics, async state machines, lambdas, local functions, arrays) —
   this is the component most likely to be subtly wrong and most expensive to be wrong about.
5. Write `session-report.schema.json` (v3) and `observation.schema.json`; generalize
   `docs/binary-format.md` into the versioned container spec with namespaced sections.
6. Retire or shrink `DumpDetective.Core` per what Phase 0's inventory shows is left.
7. Add SDK-boundary and registry-conformance rules to the architecture test.

## Exit criteria

- `DumpDetective.Sdk` builds standalone, zero project references.
- Every existing analyzer compiles against the SDK (still emitting domain results; observations
  come in Phase 5).
- **Entity-join spike has produced a measured join rate per entity kind**, and that measurement —
  not an assumption — informs the canonicalizer's fidelity ratings. A poor result here is a
  legitimate trigger to stop and reconsider Phases 6–7 before investing in them.
- `EntityCanonicalizer` passes a real-world name corpus with documented fidelity per case.
- Schemas + registries exist, versioned, with conformance tests.

## Risk / effort

**High effort, high consequence, low immediate visible payoff** — the phase most at risk of being
skipped or rushed because it ships no user-facing value. Resist that. The identity model in
particular is load-bearing for every correlation claim the product will ever make; a weak
canonicalizer produces plausible-looking false correlations, which is worse than no correlation.
Recommend treating `EntityCanonicalizer` as its own reviewed, test-heavy deliverable.
