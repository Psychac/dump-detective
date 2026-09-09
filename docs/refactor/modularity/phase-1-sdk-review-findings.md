# Phase 1 SDK review — findings & fix plan

Companion to [phase-1-contracts-sdk.md](phase-1-contracts-sdk.md) and
[phase-1-full-extraction-retyping-plan.md](phase-1-full-extraction-retyping-plan.md). A
field-by-field senior-architect pass over every type in `src/DumpDetective.Sdk/` (Artifacts,
Identity, Temporal, Observations, Synthesis, Analysis), done 2026-09-10 before starting any more
Phase 1 pending work — cheaper to fix these now, while zero analyzers depend on any of it, than
after the retyping plan's pilot migration starts building on top of it.

Status per item: **Open** (not fixed yet) or **Fixed** (with the commit/date once done). Fix order
is severity first (P0, then P1), otherwise in the order below; reorder freely.

## P0 — real bugs/inconsistencies

1. ~~`IAnalyzer.Category` has no default implementation.~~ **Resolved 2026-09-10 — not a bug, a
   deliberate improvement, kept as-is.** `Core.Abstractions.IAnalyzer.Category` defaults to
   `AnalyzerCategory.Infer(Name)` — a nine-keyword substring match on the class name that most of
   today's 35 analyzers (`WcfChannelAnalyzer`, `SqlCommandAnalyzer`, `AsyncStateMachineAnalyzer`,
   `ObjectShapeAnalyzer`, `DominatorAnalyzer`, and more) fall through to `"General"`, checked and
   confirmed by reading the real implementation. `.Category` is read in 26 files today (CLI
   `--only`/`--tags` filtering, report section builders, trend composition, TOC sidebar
   grouping) — real, load-bearing data populated by a heuristic that mostly doesn't categorize
   anything. Decision: require every analyzer to state its own category explicitly (no default),
   which the SDK version already does — kept a property rather than moved to an attribute, since
   none of those 26 call sites need discovery-time (pre-instantiation) access the way capability
   filtering does; moving it would trade 26 direct property reads for 26 reflection calls for no
   benefit. XML doc on `IAnalyzer.Category` updated to state this as intentional instead of falsely
   claiming shape-parity with `Core`'s version. **Status: Fixed (doc-only; no behavior change —
   `Core.Abstractions.IAnalyzer` and its 35 implementers are untouched, this only concerns the new
   SDK-side type).**

2. **`MatchFidelity`'s doc contradicts the code that depends on it.** `Identity/MatchFidelity.cs`'s
   remarks say ordinal ordering is "deliberately avoided as an assumption," but
   `EntityCanonicalizer.NormalizeSignature` does `if (fidelity < worst) worst = fidelity;`, which
   only works because declaration order happens to match the documented None→Exact ranking.
   Inserting a new member anywhere but the end silently breaks this. Needs either an explicit,
   test-pinned contract ("ordinal order IS the ranking, guaranteed") or a real ranking table instead
   of `<`. **Status: Open.**

3. **`EntityRef` subtypes use default record equality, including `Fidelity`.** Two `TypeRef`s with
   the same `CanonicalName` but different `Fidelity` are `!=` under default equality, even though
   they're the same identity by `JoinKey`. Nothing overrides `Equals`/`GetHashCode` to key off
   `JoinKey`. A future `Dictionary<EntityRef,_>`/`GroupBy`/`Distinct` (exactly what correlation code
   will do) will silently double-count entities resolved through two paths with different fidelity.
   **Status: Open.**

4. **`IHeapDominatorQuery` bundles two data tiers that aren't co-available.** Gated behind one
   capability (`heap.dominators`), but the real cache format has reachability as a **Stage A**
   product and retained-size/immediate-dominator/thread-retention as **Stage B**
   (`IRequiresDominatorTreeIndex`-gated, optional even when Stage A succeeds — see
   `CacheSectionCatalog`'s own remarks). The interface forces all-or-nothing when the underlying
   data doesn't. Split into `IHeapReachabilityQuery` (Stage A) + a narrower `IHeapDominatorQuery`
   (Stage B). **Status: Open.**

5. **Positional records with adjacent same-typed parameters — transposition risk.**
   - `HeapRootRef(HeapRootKind Kind, ulong TargetAddress, ulong RootAddress, ...)` — swapping
     `TargetAddress`/`RootAddress` compiles and produces a plausible-looking, silently-wrong root
     path.
   - `HeapSegmentRef(ulong Start, ulong End, int Generation)` — same risk for a segment range.
   - Pre-existing `ConfidenceBreakdown(double Composite, double EvidenceStrength, double
     IdentityFidelityCap, double TemporalAlignmentCap, double CapabilityFidelity, double
     ConflictPenalty, ...)` — six adjacent `double`s, zero compiler protection.
   
   `Observation`/`Provenance`/`Finding`/`TemporalExtent` all already use named
   `required X { get; init; }` construction specifically to avoid this; these three break that
   established convention. **Status: Open.**

6. **`IHeapTypeStatisticsQuery.TryGetGlobalSizeBuckets()` returns raw mutable `long[]?`.** Every
   other SDK collection type is `IReadOnlyList`/`IReadOnlySet`/`IReadOnlyDictionary`. Copied
   verbatim from `IHeapAnalysisCache` without reapplying the SDK's own stricter convention.
   **Status: Open.**

7. **`ArtifactId` vs `Capability` — inconsistent construction ergonomics.** `Capability` has
   `implicit operator Capability(string)`; `ArtifactId` has none, requires `new ArtifactId("x")`
   everywhere. Either intentional (should say why) or drift. **Status: Open.**

8. **`ArtifactId`/`Capability`/`ObservationId` serialize as one-key wrapper objects**
   (`{"value":"..."}`/`{"key":"..."}`), not bare strings — documented as "Gotcha 3" in
   `observation.schema.json` rather than fixed. A custom `JsonConverter` per type serializing as a
   plain string is more idiomatic JSON and removes the gotcha instead of footnoting it.
   **Status: Open.**

9. **`ObservationId.ToString()` disagrees with its own JSON form.** Override returns Guid `"N"`
   format (no hyphens); JSON serialization uses the default `"D"` format (hyphenated). The same
   class of bug already found and fixed for `EntityRef`'s `$kind` discriminator is still sitting,
   unfixed, in `ObservationId` itself. **Status: Open.**

10. **`SdkVersion` (0.1) hasn't moved across two sessions that both added public API** (the `$kind`
    fix, then the entire `Analysis/` namespace + two capability additions). Its own doc says
    "bumped on any breaking change" but doesn't say whether additive changes should bump `Minor` —
    which, given the field's name, they probably should. **Status: Open.**

## P1 — real tensions, no obviously-correct answer

11. `TimeAnchor` ("never all three" set) and `TemporalExtent` (`Kind == Point` vs. `End`
    nullability) state invariants in prose that nothing enforces at construction time.
12. `AnalysisContext`'s 13 nullable properties vs. a generic `TryGetCapability<T>()` resolver —
    current shape is IntelliSense-friendly but doesn't scale cleanly and doesn't distinguish
    "required, so trust it's non-null" from "optional, really check."
13. `IHeapObjectStream`/`IHeapReferenceQuery`/etc. are sync `IEnumerable<T>` with no
    `CancellationToken` — matches today's ClrMD-foreach pattern, but trace sources are naturally
    async/callback-driven; will resurface once a trace-backed implementation of these same
    interfaces is attempted.
14. `CapabilityVocabulary.Known` is hand-maintained (const + separately-listed set) — the exact
    drift risk hit firsthand adding `HeapDominators`. Reflection-populating `Known` would make
    drift structurally impossible instead of relying on `SdkRegistryConformanceTests` to catch it
    after the fact.
15. No `SourceKind` constants class, unlike `Capability`/`CapabilityVocabulary` — same open-string
    exposure, smaller surface (`ArtifactDescriptor.SourceKind`).
16. `ObservationQuery.AdditionalPredicate` is a raw `Func<Observation,bool>?` — already flagged as
    first-cut, but concretely: can't be validated at plugin-discovery time or survive an ALC
    boundary (Phase 9). Blocks those phases until replaced with something declarative.
17. `EntityRef.JoinKey`'s base contract says "cross-source-comparable" — `ObjectRef.JoinKey`
    explicitly is not (artifact-scoped, by its own doc). Same member name/contract meaning
    different things per subtype; nothing breaks today only because the values never happen to
    collide.

## P2 — polish, not blocking

18. `EnumerateReferences`/`EnumerateReferrers` naming asymmetry (forward/reverse would read
    clearer).
19. `ISynthesisRule.Match` (property) vs. `ObservationQuery.Matches()` (method) is mildly awkward
    read together.
20. `HeapRootKind`/`HeapHandleKind` were designed from memory/convention, **not measured against
    ClrMD's real `ClrRootKind`/`ClrHandleKind` enums.** Needs a real check before the dump-side
    implementation is built, or real values will silently collapse into `Other`.
21. Third copy-pasted `AnalyzerProgressReport`/`IndexProgress` shape (Core, Platform, Sdk) — fine
    for now; a 4th copy would be the signal to extract a shared micro-package instead.
