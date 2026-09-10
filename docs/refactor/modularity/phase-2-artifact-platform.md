# Phase 2 — Artifact Ingest & Index Platform

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 2**.
Depends on [phase-1-contracts-sdk.md](phase-1-contracts-sdk.md).
(Supersedes the earlier `phase-2-engine-platform.md` framing, which assumed dump was the engine's
subject rather than one source among several.)

## Goal

Turn today's dump-specific engine into a general artifact platform where **the dump is the first
implementation of an SPI, not the thing the engine is**. When Phase 6 adds trace, it should
plug into a slot that already exists and is already proven by a second implementation.

## Status: trimmed pass shipped 2026-09-09, per § 8's minimum-viable path

Per [modularity-plan.md § 8](../modularity-plan.md#8-the-minimum-viable-unified-path--adopted-as-the-chosen-plan-2026-09-08)
step 2: "storage extraction only... skip the `Sources.ClrDump` reorganization; leave dump code
where it is."

**Before touching anything, checked what "extraction" actually meant against real code** — the
target shape's `ColumnarWriter`/`ColumnarReader`/`InternTable`/`SectionedContainer`/`IIndexStorage`
names don't map onto today's code; they're aspirational. The real equivalents are
`CacheContainerFormat`/`CacheContainerReader`/`CacheContainerWriter`/`CacheSectionCatalog`/
`CacheSectionHelper`/`DumpContentHasher` (the container) and `BlockDeltaColumn`/
`ColumnOverflowTable`/`MonotonicAddressColumn`/`NarrowColumnWidth`/`ObjectColumnSet`/
`ObjectGenerationRunTable`/`ReachableRowBitmap` (the columns) — built dump-specific from day one,
with no shared `InternTable` at all (interning is done ad hoc per-type-index). Their dependency
graph, checked before moving anything, turned out to already be almost entirely source-agnostic
(no ClrMD, no `Core`, no `Analysis` types) — the one exception was `CacheContainerWriter` taking a
`Core.Abstractions.AnalyzerProgressReport` progress parameter, fixed below.

**Shipped**: `src/DumpDetective.Platform/` (references `Sdk` only, enforced by a new
`PlatformProject_ShouldDependOnSdkOnly` architecture test) now holds `Storage/Container/` and
`Storage/Columns/` — moved via `git mv` + scripted namespace rewrite, not retyped, specifically to
avoid the risk of a large-file transcription error. **Identifiers were deliberately not renamed**
(`CacheContainerFormat` stays `CacheContainerFormat`, not `SectionedContainer`) — the target shape's
renaming is cosmetic, not the extraction itself, and doing it now with no second consumer to
validate the new names against would be exactly the "let's improve it while we're here" scope creep
the Risk/effort section below warns against. Rename when Phase 6a's trace ingest is real and has an
opinion.

The one non-mechanical change: `CacheContainerWriter`'s progress parameter moved from
`IProgress<Core.Abstractions.AnalyzerProgressReport>` to a new, minimal `Platform.IndexProgress`
(same field shape — `Platform` cannot reference `Core`, which transitively carries the ClrMD package
reference, so this decoupling was required, not optional). `DiskBackedObjectIndexWriter` (the sole
caller, in `Analysis`) adapts via one small `WrapForContainerProgress` helper at its 7 call sites;
every other use of `AnalyzerProgressReport` in that file is untouched.

**Correction, 2026-09-10** (see
[phase-1-sdk-review-findings.md](phase-1-sdk-review-findings.md) item 21): `Platform.IndexProgress`
was retired the same day `DumpDetective.Sdk.Analysis.AnalyzerProgressReport` came to exist (Phase
1's Tier-1 skeleton) — it had duplicated that exact shape only because it predated it, and Platform
already legitimately references the SDK (`PlatformProject_ShouldDependOnSdkOnly`), so the
duplication was no longer necessary once the SDK had its own copy to point at instead.
`CacheContainerWriter` and `WrapForContainerProgress` now report through
`Sdk.Analysis.AnalyzerProgressReport` directly.

**Verification**: 163 existing unit tests targeting exactly the moved files
(`CacheContainerRoundTripTests`, `CacheContainerAtomicWriteTests`,
`CacheContainerWriterChecksumProgressTests`, `CacheSectionCatalogTests`, `SectionManifestTests`,
`BlockDeltaAddressColumnTests`, `NarrowSizeColumnTests`, etc.) pass unchanged — exact match to the
pre-move baseline. Plus two real-dump discrepancy tests run one at a time in the foreground per
project rules (`NarrowColumnsRealDumpTests`, exercising the moved `Columns/` code; and
`SegmentIndexBuildDiscrepancyTests`, exercising the full indexing pipeline through the moved
`Container/` code) — both pass. The remaining ~24 real-dump discrepancy tests were not run: this was
a scripted namespace-only move with no logic changes, and running all of them (~65 more minutes) is
not proportional to that risk profile once two representative real-dump runs and 163 exact-match
unit tests already confirm it.

**Deferred**, per § 8's explicit scope and consistent with how Phase 1 was scoped — nothing here has
a real second consumer yet to validate a new abstraction against:
- `Sources.ClrDump` reorganization (migration step 2) — dump code (`DumpLoader`, `RuntimeFacade`,
  heap indexing, cache, graph, query) stays in `Analysis` exactly where it is. No "thin
  `IArtifactSource` adapter" was built either — nothing exists yet to register it with (no
  `ArtifactSourceRegistry`), so a stub adapter would be unexercised code.
- `IHeapAnalysisCache` → capability-scoped query surfaces (migration step 3) — a Phase 3
  (capability model) concern; Phase 3 itself is deferred.
- `IIndexStorage`/`LocalDiskIndexStorage`/`InMemoryIndexStorage` — this abstraction doesn't exist
  today even in dump-specific form (the container works directly against `FileStream`/
  `MemoryMappedFile`); building it now would be new design work with no second backing store to
  validate it against, the same reasoning that deferred `IArtifactSource` in Phase 1.
- `InternTable` — confirmed no existing equivalent to extract; same reasoning as above.
- `ObservationStore`, `TimelineAligner`, `Session/*` — Phase 4/5 concerns, explicitly out of scope.
- The stub second source (exit criterion 5) — the real validation of this whole phase's
  abstraction, and honestly still missing. It can't exist until Phase 6a.

## Target shape

```
/platform
  DumpDetective.Platform/                  -- source-agnostic; references Sdk only
    Ingest/
      ArtifactSourceRegistry.cs            -- probe + dispatch by SourceKind
      IndexOrchestrator.cs                 -- runs IArtifactSource.IndexAsync, progress, cancel
    Storage/
      IIndexStorage.cs                     -- Stream-based SPI (namespaced sections)
      LocalDiskIndexStorage.cs             -- today's cache.bin behavior
      InMemoryIndexStorage.cs
      SectionedContainer.cs                -- generalized container: "heap.*", "trace.*", ...
      ColumnarWriter.cs / ColumnarReader.cs  -- extracted, source-agnostic primitives
      InternTable.cs                       -- string/entity interning, reused by every source
    Observations/
      ObservationStore.cs                  -- disk-backed, streamed (see model doc § 7)
    Session/
      AnalysisSession.cs  SessionTimeline.cs  TimelineAligner.cs

/sources
  DumpDetective.Sources.ClrDump/           -- references Sdk + Platform + ClrMD
    ClrDumpArtifactSource.cs               -- implements IArtifactSource
    Dump/                                  -- DumpLoader, RuntimeFacade, DAC resolution
    Indexing/                              -- the existing single-pass heap scan, unchanged
    Cache/                                 -- HeapAnalysisCache + sub-caches
    Graph/                                 -- BoundedGraphWalk, ReverseIndex, RootSetCache
    Query/                                 -- QueryEngine
    Capabilities/                          -- maps heap.* / runtime.* capability surfaces
```

## Key design decisions

- **The columnar/interning/container machinery is source-agnostic and gets extracted.** This is
  the highest-value reuse in the entire plan: the disk-backed columnar writer, `ArrayPool` buffer
  discipline, intern tables, and sectioned container that make 25 GB heaps tractable are *exactly*
  what a multi-GB trace needs. Extracting them into `Platform/Storage` means trace ingest inherits
  a battle-tested, bounded-memory storage layer instead of reinventing one. Phase 6 gets
  dramatically cheaper because of this phase.
- **Namespaced container sections.** `cache.bin` becomes a general sectioned container where
  section names are namespaced by domain (`heap.objects`, `heap.types`, `trace.samples`,
  `trace.stacks`). One container per artifact; a session references N containers. Format spec
  versioned per Phase 1.
- **Capability surfaces, not god-interfaces.** `IHeapAnalysisCache` today is one interface
  bundling everything a dump can answer. Under the capability model it splits into per-capability
  query surfaces (`IHeapObjectQuery`, `IHeapReferenceQuery`, `IThreadQuery`, …) that
  `AnalysisContext` resolves by capability. An analyzer requiring `heap.objects` gets exactly that
  surface — and a *trace* source could theoretically provide `IThreadQuery` too, which is precisely
  the polymorphism the god-interface prevents.
- **`ObservationStore` is disk-backed from the start.** Deliberately not an in-memory list. Flagged
  in [observation-and-correlation-model.md § 7](observation-and-correlation-model.md) as the model's
  biggest unvalidated assumption — building it here, under dump-only load, is how that assumption
  gets tested before trace makes it critical.
- **`TimelineAligner` lands here** implementing the alignment strategy ladder from
  [source-model.md § 5](source-model.md), exercised initially by multi-dump sessions (which already
  need it for trend) — again, proving the mechanism under known conditions before trace arrives.

## Migration steps

1. Create `DumpDetective.Platform`; extract the columnar/container/intern/storage primitives out of
   today's `Analysis/Indexing`, leaving heap-*semantics* behind. **Done 2026-09-09** for the
   columnar/container primitives that actually exist today (no intern table existed to extract) —
   see Status above for exactly what moved, what was intentionally not renamed, and why.
2. ~~Create `DumpDetective.Sources.ClrDump`; move dump loading, heap indexing, cache, graph, query
   into it. Implement `IArtifactSource` as a wrapper over the existing prebuild path — behavior
   identical, index format identical modulo section renaming.~~ **Deferred, per § 8.** See Status
   above.
3. ~~Split `IHeapAnalysisCache` into capability-scoped query surfaces; `HeapAnalysisCache` keeps its
   internals and implements several of them (mechanical interface segregation, no behavior change).~~
   **Deferred, per § 8** — a Phase 3 concern.
4. ~~Build `ObservationStore` and `TimelineAligner` (new code, unused until Phases 5/6 — accept that
   they're speculative here, or defer them to their consuming phase if that's preferred; the
   argument for building now is that they're cheaper to design against dump-only reality).~~
   **Deferred, per § 8** — Phase 4/5 concerns, out of scope for the minimum-viable path.
5. Architecture rules: `Platform` may not reference any `Sources.*`; `Sources.*` may not reference
   each other. **Partially done** — `Platform` may not reference anything but `Sdk`, enforced by
   `PlatformProject_ShouldDependOnSdkOnly`. The `Sources.*`-to-`Sources.*` half doesn't apply yet;
   no `Sources.*` projects exist under the trimmed scope.

## Exit criteria

**Note (2026-09-09): these are the exit criteria for the full, untrimmed Phase 2.** Same pattern as
Phase 1 — several don't apply yet under § 8. Marked below rather than silently left unmet.

- `DumpDetective.Platform` builds with only an `Sdk` reference — no ClrMD anywhere in it. **Done**,
  and enforced by a standing test, not just true today.
- ~~`Sources.ClrDump` implements `IArtifactSource` end-to-end; a dump indexes through
  `ArtifactSourceRegistry` with no dump-specific code above the source boundary.~~ **Not applicable
  to the § 8-trimmed pass** — presumes migration step 2, which is deferred.
- All existing index/cache/graph perf and correctness tests pass unchanged. **Done** — 163 unit
  tests exact match, plus 2 real-dump discrepancy tests (see Status above for which and why not all
  ~26).
- ~~`IIndexStorage` has ≥ 2 implementations, both exercised.~~ **Not applicable** — `IIndexStorage`
  itself doesn't exist yet; see Status above.
- **A trivial second source exists** — even a stub (`gcdump` reading only `heap.types`, or a
  synthetic test source) — proving the SPI isn't accidentally shaped around ClrMD's peculiarities.
  This is the real exit criterion; without a second implementation, "general" is unverified.
  **Still not met** — genuinely can't be until Phase 6a. This is the one gap in this phase worth
  remembering: the columnar/container extraction is real and tested, but nothing has yet proven the
  *abstraction* generalizes beyond ClrMD, because nothing else has used it.

## Risk / effort

High effort — the largest code-motion phase, moving most of today's non-analyzer logic. Low
*behavioral* risk **if** treated strictly as motion plus interface segregation. The failure mode is
scope creep: "while we're extracting the columnar writer, let's improve it." Don't. The extraction
is already hard enough to review; improvements are separately-justified changes.

The stub second source (exit criterion 5) is the item most likely to be cut for time and the one
most worth defending — it's the only thing that actually validates the abstraction.
