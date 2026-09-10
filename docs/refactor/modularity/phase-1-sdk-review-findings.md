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

2. ~~`MatchFidelity`'s doc contradicts the code that depends on it.~~ **Fixed 2026-09-10.** Flipped
   the remarks from "avoid assuming ordinal order" to an affirmative, guaranteed contract (ordinal
   order *is* the ranking), pinned by a new test —
   `IdentityTests.MatchFidelity_DeclarationOrderMatchesDocumentedRanking` asserts
   `Enum.GetValues<MatchFidelity>()` equals the exact documented sequence, so a future reorder/insert
   fails loudly instead of silently miscomputing every downstream confidence cap. Added
   `Identity/MatchFidelityExtensions.Min(this MatchFidelity, MatchFidelity)` as the one named,
   doc-commented place that relies on the contract, and switched
   `EntityCanonicalizer.NormalizeSignature`'s inline `if (fidelity < worst)` to use it — one
   consumer instead of every future caller re-deriving "take the weaker fidelity" ad hoc. 25 SDK
   unit tests pass (4 new); full suite 1220/1220 non-real-dump tests pass.

3. ~~`EntityRef` subtypes use default record equality, including `Fidelity`.~~ **Fixed 2026-09-10.**
   Turned out worse than originally scoped once implemented: default equality also compares
   source-local handles (`MethodTable`/`TypeToken` for `TypeRef`, etc.), which are essentially
   *never* both populated across a dump-side and trace-side ref for the same real entity — so
   `EntityRef` couldn't correctly identify the same entity across sources at all, not just in the
   fidelity-mismatch edge case originally described.
   
   Added `virtual bool Equals(EntityRef? other)` + `override GetHashCode()` on `EntityRef` itself,
   comparing `Kind`+`JoinKey` only (per source-model.md § 4: handles are "carried along for
   drill-down but never used for joining"; `Fidelity` is a trust rating *of* the identity, not part
   of it). Every sealed subtype (`TypeRef`, `MethodRef`, `ModuleRef`, `ThreadRef`, `ObjectRef`) needed
   its own two-line override too — C# records generate a separate typed `Equals`/`GetHashCode` pair
   at *each* level of a record hierarchy; a derived record doesn't inherit a base's override as its
   own, so without this every subtype would still silently fall back to comparing its own extra
   fields even with the base fixed.
   
   **Real bug caught by the tests written for this fix, not by inspection**: the first version
   delegated via `Equals((EntityRef?)other)` from each subtype, which — because `Equals(EntityRef?)`
   is virtual and the compiler *also* synthesizes a derived-level override of it (to support
   polymorphic `EqualityContract` comparison) that calls back down into the subtype's own typed
   `Equals` — recursed infinitely and stack-overflowed the test host on the very first equality
   check. Fixed by calling `base.Equals(other)` (non-virtual dispatch straight to `EntityRef`'s
   implementation) instead. Left as a permanent reminder in this item that "should obviously work"
   record-inheritance equality patterns need a real test, not a read-through, before trusting them —
   exactly the kind of thing this review pass exists to catch, just recursively, in fixing its own
   fix.
   
   Also required `override GetHashCode() => base.GetHashCode();` in every subtype, not just the
   base: without it, each derived record's auto-generated `GetHashCode` would fold in its own extra
   fields on top of the base's, breaking the fundamental `Equals ⟹ same GetHashCode` contract for
   exactly the cases this fix exists to make equal.
   
   3 new tests (cross-source equality ignoring handles/fidelity + hash-code consistency, still
   differs by `JoinKey`, `ObjectRef` still differs by artifact despite same address). 1223/1223
   non-real-dump tests pass.

4. ~~`IHeapDominatorQuery` bundles two data tiers that aren't co-available.~~ **Fixed 2026-09-10.**
   Split into `IHeapReachabilityQuery` (`IsReachableFromRoot` — Stage A, the reverse-edge index +
   walk) and a narrowed `IHeapDominatorQuery` (`TryGetRetainedSize`/`TryGetImmediateDominator`/
   `TryGetThreadRetainedSize` — Stage B, `IRequiresDominatorTreeIndex`-gated, optional even when
   Stage A succeeds). New capability `heap.reachability` added alongside the existing
   `heap.dominators` (now Stage-B-scoped only) — `capability-registry.json` bumped to 1.2.0. Also
   corrected a stale note in `phase-1-full-extraction-retyping-plan.md`'s Tier-1 table that still
   described the sync-blocks row as needing a new `heap.sync-blocks` capability, when it was
   actually wired to the pre-existing `runtime.locks` back when finding item's neighbor (the
   capability-vocabulary gap) was fixed. Additive, zero behavior change — no dump-side
   implementation exists yet for either interface, so nothing consumed the old bundled shape to
   migrate. 37/37 architecture+SDK tests pass; full suite 1223/1223 non-real-dump tests pass.

5. ~~Positional records with adjacent same-typed parameters — transposition risk.~~ **Fixed
   2026-09-10.** All three converted from positional construction to named `required X { get; init; }`
   properties, matching `Observation`/`Provenance`/`Finding`/`TemporalExtent`'s established
   convention:
   - `HeapRootRef` (`TargetAddress`/`RootAddress` were the adjacent-`ulong` risk).
   - `HeapSegmentRef` (`Start`/`End`).
   - `ConfidenceBreakdown` (six adjacent `double`s).
   
   `HeapRootRef`/`HeapSegmentRef` stayed `readonly record struct` — value-type, matching
   `HeapObjectRef`/`HeapEntry`'s existing precedent for hot-path streamed DTOs; only the
   construction syntax changed, not the perf characteristics. Zero call-site impact: grepped first
   and confirmed nothing anywhere in the codebase constructs any of the three types yet, so this
   was a zero-risk mechanical change. Full suite 1223/1223 non-real-dump tests pass.

6. ~~`IHeapTypeStatisticsQuery.TryGetGlobalSizeBuckets()` returns raw mutable `long[]?`.~~ **Fixed
   2026-09-10.** Changed to `IReadOnlyList<long>?`, matching the SDK's own convention everywhere
   else. Confirmed zero usages of the SDK interface's method before changing it (only
   `Core.Abstractions.IHeapAnalysisCache`'s original — untouched, unaffected — is actually called
   anywhere today), so this was a zero-risk mechanical change. Full suite 1223/1223 non-real-dump
   tests pass.

7. ~~`ArtifactId` vs `Capability` — inconsistent construction ergonomics.~~ **Resolved 2026-09-10 —
   not a bug, justified by different real usage, kept as-is.** Checked actual construction sites
   before deciding: `Capability`'s implicit conversion is genuinely load-bearing — all three
   shipped analyzers (`GcPauseAnalyzer`/`ContentionAnalyzer`/`CpuHotspotAnalyzer`) write
   `new HashSet<Capability> { CapabilityVocabulary.TraceGcEvents }`, relying on it every time.
   `ArtifactId` is constructed explicitly exactly once in production code (`TraceAnalysisRunner`,
   one identity per real artifact) plus test fixtures — never as a repeated literal. Added an XML
   remark on `ArtifactId` explaining the asymmetry instead of "fixing" it either direction, same
   resolution shape as finding 1.

8 & 9. ~~`ArtifactId`/`Capability`/`ObservationId` serialize as one-key wrapper objects; `ObservationId.ToString()`
   disagrees with its own JSON form.~~ **Both fixed together 2026-09-10** — implementing 8's fix
   forces a decision on ObservationId's exact JSON string format anyway, so doing them separately
   would have meant touching `ObservationId` twice. Each type now carries its own
   `JsonConverter<T>`, serializing as a bare JSON string instead of a one-key wrapper object
   (`{"value":"..."}"`/`{"key":"..."}"`); `ObservationId`'s converter uses Guid `"N"` format (no
   hyphens), matching `ToString()` exactly instead of silently disagreeing with it (previously the
   wire form used the Guid default `"D"` format via the wrapper object). Verified empirically with
   a throwaway probe (not just read-through) that all three round-trip correctly and that
   `ObservationId`'s JSON now equals its `ToString()` byte-for-byte.
   
   `observation.schema.json` updated to match — `wrappedString`/`capability` `$defs` collapsed into
   one `idString: { "type": "string" }`, re-validated against a fresh live-build probe covering all
   five `EntityRef` subtypes (same discipline the schema was originally built with), bumped to
   `schemaVersion` 2.0.0 (a real breaking wire-format change, scoped to that one file —
   `capability-registry.json`/`observation-type-registry.json` stay at 1.2.0). No real consumer
   existed for the 1.0.0 shape (no UI, no persisted `report.json` corpus depending on it), so this
   was the right time to fix it rather than a migration to plan around. Full suite 1223/1223
   non-real-dump tests pass unchanged — no existing test asserted the old wrapper shape.

10. ~~`SdkVersion` (0.1) hasn't moved across two sessions that both added public API.~~ **Resolved
    2026-09-10 — policy decided: per-finding.** `Minor` now bumps once per fix that changes the
    SDK's public API surface, additive or breaking, in the same commit as the fix — not batched
    later, since "bump it eventually" is exactly what let this field sit untouched across two whole
    sessions of real API changes. `Major` reserved for an actual breaking change once something
    real depends on this assembly. Bumped to 2 now as one catch-up for everything already shipped
    this session (not a precise per-change reconstruction — nothing depends on intermediate values
    enough to make that worth simulating). Every P0 fix from here on carries its own bump.

## P1 — real tensions, no obviously-correct answer

11. ~~`TimeAnchor` ("never all three" set) and `TemporalExtent` (`Kind == Point` vs. `End`
    nullability) state invariants in prose that nothing enforces at construction time.~~ **Resolved
    2026-09-10, three separate outcomes:**
    - **Not enforced at construction, by design.** Both types' producers are trusted first-party
      analyzer code, not external input — per this project's own convention (CLAUDE.md: "only
      validate at system boundaries"), a throw-on-invalid check here would be exactly the kind of
      defensive validation this codebase deliberately avoids. Left as a documented, trusted
      contract instead.
    - **`TimeAnchor`'s "never all three populated" half was dropped, not kept.** Having all three
      fields set isn't actually harmful, just unusual — there was nothing real to protect against by
      forbidding it. Only "at least one must be populated" survives as the real invariant.
    - **Bigger finding: `TemporalKind.Series` was removed, not built out — a real redesign, not a
      validation gap.** Discussion surfaced that a `TemporalExtent` lives on one `Observation`,
      which is scoped to exactly one artifact (`Provenance.Artifact` is singular) — it can only ever
      describe a point or a bounded interval for that one artifact, never a series across several.
      `Series` never had a valid representation here and trying to give it one (an anchor list, for
      example) would have been modeling the wrong owner for the concept. The real "ordered view
      across N artifacts" belongs on `AnalysisSession.Timeline : SessionTimeline`
      (source-model.md § 6) — Phase 4 territory, not yet built, correctly still deferred. Confirmed
      zero code anywhere constructed `TemporalKind.Series` before removing it; `source-model.md` § 5
      corrected in place (dated note, not silently rewritten) rather than left describing a shape
      the shipped type no longer has.
    
    Guarded by `TemporalTests.cs` (enum-member pin + example shapes) and a real-producer
    characterization assertion added to `CpuHotspotAnalyzerTests.cs`'s existing boundary test — the
    actual analyzer's actual output, not just a hand-built example. Full suite 1227/1227 non-real-dump
    tests pass (one pre-existing flaky, order-dependent test unrelated to this change — passes
    standalone and on retry).
    
    **Follow-on, 2026-09-10 — `CapabilityVocabulary.TemporalSeries` had the identical flaw, found
    while working on finding 14.** Same reasoning applies exactly: a `Capability` is declared
    per-artifact (`ArtifactDescriptor.Provides`), and no single artifact can provide "a series" any
    more than a single `Observation` can hold one. Confirmed zero real usage anywhere (only
    self-referential: the const declaration, its registry mirror, and one test spot-checking the
    Temporal group's presence in `Known`) before removing it — same standard applied as the
    `TemporalKind.Series` removal itself. `capability-registry.json` bumped to 1.3.0;
    `source-model.md` § 3's vocabulary table corrected in place (dated note, same pattern as § 5).
    `temporal.point`/`temporal.interval` are also currently unconsumed by any real code but were
    left alone — a broader question about whether the whole Temporal capability group is
    under-justified, not raised or resolved here. `SdkVersion.Minor` bumped to 4 (a real public API
    removal).
12. ~~`AnalysisContext`'s 14 nullable properties (13 at review time; finding 4 added a 14th) vs. a
    generic `TryGetCapability<T>()` resolver — current shape is IntelliSense-friendly but doesn't
    scale cleanly and doesn't distinguish "required, so trust it's non-null" from "optional, really
    check."~~ **Resolved 2026-09-10, two separate outcomes, both documentation-only:**
    - **Fixed properties stay — the "doesn't scale" framing was overstated.** Phase 3's
      "avoid hardcoded catalogs" concern is about the *analyzer* count (large, plugin-extensible);
      capability *surfaces* are a different, much smaller, SDK-curated axis that no plugin invents.
      A generic resolver would trade real IntelliSense/type-safety for an extensibility need that
      doesn't exist.
    - **Required-vs-optional nullability stays undistinguished, genuinely deferred, not fixed.**
      Whether a property is actually guaranteed non-null for a given run depends on the
      orchestrator having filtered that analyzer in — a promise only Phase 4's orchestrator can
      make, and it doesn't exist yet. Nothing at this layer can encode that guarantee in the
      meantime.
    
    Both documented directly on `AnalysisContext`'s own remarks rather than left as open questions
    elsewhere. No code/behavior change — no `SdkVersion` bump.
13. ~~`IHeapObjectStream`/`IHeapReferenceQuery`/etc. are sync `IEnumerable<T>` with no
    `CancellationToken` — matches today's ClrMD-foreach pattern, but trace sources are naturally
    async/callback-driven; will resurface once a trace-backed implementation of these same
    interfaces is attempted.~~ **Resolved 2026-09-10 — the premise was wrong, not just the
    conclusion.** This finding conflated ingest-time and analysis-time concerns: by the time
    analysis calls any Tier-1 streaming method, the underlying data has already been ingested into
    a disk-backed, sequential index during an earlier, separate phase (the dump's single-pass heap
    scan, or a trace's ingest — `GcPauseIndexer` et al. already turn live
    `TraceEventDispatcher` callbacks into disk sections before any analyzer runs). The real
    already-shipped trace-side reader (`TraceMethodIndexReader.ReadAll(stream)`, used by
    `CpuHotspotAnalyzer` today) is itself a plain synchronous sequential stream read, not a live
    callback subscription — async ingest is real, async analysis-time query was never actually
    needed. Sync `IEnumerable<T>` with no `CancellationToken` parameter matches both real
    precedents exactly: ClrMD's own `ClrHeap.EnumerateObjects()` (sync, no cancellation parameter,
    what all 35 existing dump analyzers are built on) and the real trace-side reader above.
    Cancellation flows through the outer `AnalyzeAsync(AnalysisContext, CancellationToken)` call and
    is checked inside the consuming loop, the same pattern used everywhere today.
    
    Documented in full on `IHeapObjectStream`'s own remarks (the canonical explanation, not repeated
    per interface) with a one-line cross-reference added to the other nine Tier-1 streaming
    interfaces (`IHeapRootQuery`, `IHeapHandleQuery`, `IHeapSegmentQuery`,
    `IHeapFinalizerQueueQuery`, `IHeapSyncBlockQuery`, `IHeapReferenceQuery`/
    `IHeapReverseReferenceQuery`, `IRuntimeThreadQuery`, `IRuntimeModuleQuery`, `IRuntimeJitQuery`).
    Documentation-only, no shape change — no `SdkVersion` bump.
14. ~~`CapabilityVocabulary.Known` is hand-maintained (const + separately-listed set) — the exact
    drift risk hit firsthand adding `HeapDominators`.~~ **Fixed 2026-09-10.** Considered a source
    generator and a Roslyn analyzer first — both rejected as disproportionate ceremony for one
    ~30-member class (the right tool if this exact pattern needed repeating across many types, not
    here). `Known` now reflects over the class's own `const string` fields
    (`IsLiteral` + `GetRawConstantValue()`) instead of a hand-listed `HashSet` initializer — drift is
    now structurally impossible rather than caught after the fact by
    `SdkRegistryConformanceTests`. The fields stay `const` (not `static readonly`) deliberately, so
    a future analyzer can write `[RequiresCapability(CapabilityVocabulary.HeapObjects)]` — a
    compile-time-constant attribute argument — which is exactly what rules out any non-reflection
    enumeration mechanism. Verified `SdkRegistryConformanceTests` still passes (confirms the
    reflected set is identical to the old hand-maintained one); full suite 1227/1227 non-real-dump
    tests pass. No `SdkVersion` bump — `Known`'s public type and contents are unchanged, only its
    internal population mechanism.
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
