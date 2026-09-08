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
3a. ~~**TraceEvent dependency spike (same tier of risk, same cost to check).** Before Phase 6
   commits to `Microsoft.Diagnostics.Tracing.TraceEvent` / `EventPipeEventSource`, confirm: it
   actually streams a multi-GB `.nettrace` rather than buffering it whole, its licensing is
   compatible with this project, and its memory behavior holds up under the project's
   bounded-memory rules.~~ **Resolved 2026-09-08 — go, with a design correction.** See below.
4. Implement `EntityCanonicalizer` with the normalization rules and fidelity ratings from
   [source-model.md § 4](source-model.md), informed by the spike, with an extensive test corpus of
   real type/method names (generics, async state machines, lambdas, local functions, arrays) —
   this is the component most likely to be subtly wrong and most expensive to be wrong about.
5. Write `session-report.schema.json` (v3) and `observation.schema.json`; generalize
   `docs/binary-format.md` into the versioned container spec with namespaced sections.
6. Retire or shrink `DumpDetective.Core` per what Phase 0's inventory shows is left.
7. Add SDK-boundary and registry-conformance rules to the architecture test.

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

- `DumpDetective.Sdk` builds standalone, zero project references.
- Every existing analyzer compiles against the SDK (still emitting domain results; observations
  come in Phase 5).
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
- `EntityCanonicalizer` passes a real-world name corpus with documented fidelity per case.
- Schemas + registries exist, versioned, with conformance tests.

## Risk / effort

**High effort, high consequence, low immediate visible payoff** — the phase most at risk of being
skipped or rushed because it ships no user-facing value. Resist that. The identity model in
particular is load-bearing for every correlation claim the product will ever make; a weak
canonicalizer produces plausible-looking false correlations, which is worse than no correlation.
Recommend treating `EntityCanonicalizer` as its own reviewed, test-heavy deliverable.
