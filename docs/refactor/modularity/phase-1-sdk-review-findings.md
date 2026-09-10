# Phase 1 SDK review — findings (closed 2026-09-10)

Companion to [phase-1-contracts-sdk.md](phase-1-contracts-sdk.md) and
[phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md). A
field-by-field senior-architect pass over every type in `src/DumpDetective.Sdk/` (Artifacts,
Identity, Temporal, Observations, Synthesis, Analysis), done before starting any more Phase 1
pending work — cheaper to fix these while zero analyzers depend on any of it than after the
retyping plan's pilot migration starts building on top.

**All 21 findings resolved.** This is the compact record — what was found, what was decided, and
why when the why isn't obvious. Full investigation detail (exact measurements, probes, the stack
overflow finding 3's tests caught mid-fix, etc.) lives in git history, one commit per finding:
`git log --grep "Resolve finding"`. `SdkVersion` moved 0.1 → 0.4 over the course of this review,
under the per-finding bump policy adopted at finding 10.

## P0 — real bugs/inconsistencies (10/10 fixed)

1. **`IAnalyzer.Category` had no default impl.** Not a bug — kept as-is. `Core`'s
   `AnalyzerCategory.Infer` is a nine-keyword heuristic most of today's 35 analyzers fall through to
   `"General"` on; `.Category` is real, load-bearing data (26 consumers: CLI filtering, report
   builders, trend composition). Requiring every analyzer to state it explicitly is correct; doc
   corrected to stop claiming shape-parity with `Core`'s version.
2. **`MatchFidelity`'s doc disclaimed the ordinal ranking `EntityCanonicalizer` actually relied
   on.** Made it an affirmative, test-pinned contract (`MatchFidelity_DeclarationOrderMatchesDocumentedRanking`)
   instead of a discouraged assumption. Added `MatchFidelityExtensions.Min` as the one named place
   that uses it.
3. **`EntityRef` default record equality compared source-local handles**, so a dump-side and
   trace-side ref to the same entity were never `==`. Added `Kind`+`JoinKey` equality on the base
   and every sealed subtype (records don't inherit a base's typed `Equals` — each level needs its
   own). The tests written for this caught a real stack-overflow bug the read-through missed: naive
   delegation recursed through a compiler-synthesized derived override; fixed with a non-virtual
   `base.Equals` call.
4. **`IHeapDominatorQuery` bundled a Stage A product (reachability) with Stage B ones** that aren't
   actually co-available. Split into `IHeapReachabilityQuery` (Stage A) + a narrower
   `IHeapDominatorQuery` (Stage B); new `heap.reachability` capability.
5. **Positional records with adjacent same-typed parameters** (`HeapRootRef`, `HeapSegmentRef`,
   `ConfidenceBreakdown`) — real transposition risk, e.g. swapping a root's target/root address
   silently compiles. Converted all three to named `required` properties.
6. **`TryGetGlobalSizeBuckets()` returned raw mutable `long[]?`** — changed to
   `IReadOnlyList<long>?`, matching the SDK's convention everywhere else.
7. **`ArtifactId`/`Capability` construction asymmetry** (one has an implicit string conversion, the
   other doesn't). Not a bug — `Capability`'s conversion is load-bearing (every shipped analyzer
   uses it); `ArtifactId` is constructed once per artifact, never as a repeated literal. Documented,
   not changed.
8. **`ArtifactId`/`Capability`/`ObservationId` serialized as one-key wrapper objects**, and
   **`ObservationId.ToString()` disagreed with its own JSON form** (fixed together — the same
   change forces both decisions). Each type now has a `JsonConverter` producing a bare JSON string;
   `ObservationId`'s wire form now matches `ToString()` exactly (`"N"` format). `observation.schema.json`
   bumped to 2.0.0 (real breaking wire change; no consumer existed yet, so the right time to fix it).
9. *(resolved together with 8 above.)*
10. **`SdkVersion` hadn't moved across two sessions of real API changes.** Adopted policy: `Minor`
    bumps once per fix that changes public API surface, in the same commit as the fix — not batched
    later. `Major` reserved for an actual breaking change once something real depends on this
    assembly.

## P1 — real tensions (7/7 resolved)

11. **`TimeAnchor`/`TemporalExtent` invariants are unenforced at construction.** Left unenforced,
    by design — both are built only by trusted first-party code, and this project validates at
    system boundaries, not internal invariants. Bigger outcome: **`TemporalKind.Series` was removed
    entirely, not built out** — a `TemporalExtent` is scoped to one artifact (via its owning
    `Observation`) and can never represent a multi-artifact series; that concept belongs on the
    unbuilt session model (`AnalysisSession.Timeline`) instead. Follow-on: found and removed the
    identical flaw in `CapabilityVocabulary.TemporalSeries` while working finding 14.
12. **`AnalysisContext`'s 14 nullable properties vs. a generic resolver.** Kept fixed properties —
    the "doesn't scale" concern doesn't transfer from Phase 3's actual worry (analyzer count, not
    capability-surface count). Required-vs-optional nullability stays genuinely undistinguished —
    depends on an orchestrator (Phase 4) that doesn't exist to make that guarantee.
13. **Tier-1 streaming interfaces are sync `IEnumerable<T>` with no `CancellationToken`.** The
    finding's premise was wrong, not just its conclusion: these are post-ingest, already-indexed
    reads on both the dump and trace sides (trace ingest already converts live event callbacks to
    disk sections before analysis runs), matching ClrMD's own shape exactly. Documented, not
    changed.
14. **`CapabilityVocabulary.Known` was hand-maintained** and drifted twice in one session. Now
    reflects over the class's own `const` fields — drift is structurally impossible instead of
    caught after the fact by a test.
15. **No `SourceKind` constants class.** Turned out unnecessary: `ArtifactDescriptor` is never
    constructed anywhere in the codebase — the real interim dump/trace router bypasses it entirely
    with extension-sniffing. No fix; see carried-forward items below.
16. **`ObservationQuery.AdditionalPredicate` (raw delegate) can't be validated at plugin-discovery
    time or cross an ALC boundary.** Left as-is — no real `ISynthesisRule` exists yet to design a
    declarative replacement against. Cost stated explicitly on the type so Phase 5/9 hit it
    directly instead of rediscovering it.
17. **`EntityRef.JoinKey`'s base contract overclaims "cross-source-comparable"**; `ObjectRef`
    doesn't honor it (artifact-scoped by nature). Doc corrected to name the exception explicitly;
    no restructure — `ObjectRef` needs to stay in the polymorphic hierarchy.

## P2 — polish (4/4 closed)

18. **`EnumerateReferences`/`EnumerateReferrers` naming asymmetry.** Only renamed the one with no
    real ClrMD API to mirror (`EnumerateReferrers` → `EnumerateReverseReferences`); left
    `EnumerateReferences` alone since it deliberately matches the real `ClrObject.EnumerateReferences`
    used at 18+ call sites in the dump-side codebase.
19. **`ISynthesisRule.Match` vs. `ObservationQuery.Matches()`** read awkwardly together. Renamed the
    property to `Query`.
20. **`HeapRootKind`/`HeapHandleKind` were guessed from memory, not measured.** Reflected the real
    installed ClrMD package directly — found real gaps (`StaticVar`/`ThreadStaticVar` collapsed
    into one value despite being actively distinguished in production code; `FinalizerQueue` had no
    representation at all) and rebuilt both as faithful 1:1 mirrors of the real enum names.
21. **Triple-duplicated `AnalyzerProgressReport`/`IndexProgress` shape.** Collapsed `Platform`'s
    copy into the SDK's — a dependency path that didn't exist when the duplicate was created.
    `Core`'s copy can't collapse the same way (ClrMD dependency); two copies is the permanent floor.

## Open items carried forward

Not part of this review's scope to resolve — flagged so they aren't silently rediscovered later:

- Whether `ArtifactDescriptor` (and `SourceKind`) is dead scaffolding worth removing, or worth
  keeping for when Phase 2/4 build real artifact discovery (finding 15).
- `AnalysisContext`'s required-vs-optional nullability distinction, blocked on Phase 4's
  orchestrator existing (finding 12).
- `ObservationQuery.AdditionalPredicate`'s declarative replacement, blocked on a real
  `ISynthesisRule` implementation existing (finding 16).
- `Sdk.Analysis.AnalyzerProgressReport` has the same adjacent-same-typed-parameter transposition
  risk finding 5 fixed elsewhere (`Phase`/`Detail`), inherited from `IndexProgress` — not yet fixed
  (finding 21).
- `temporal.point`/`temporal.interval` capabilities are also currently unconsumed by any real code;
  whether the whole Temporal capability group is under-justified wasn't examined (finding 11
  follow-on).
