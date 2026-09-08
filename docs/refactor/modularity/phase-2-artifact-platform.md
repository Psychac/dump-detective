# Phase 2 — Artifact Ingest & Index Platform

Part of [../modularity-plan.md](../modularity-plan.md). Implements north-star **Layer 2**.
Depends on [phase-1-contracts-sdk.md](phase-1-contracts-sdk.md).
(Supersedes the earlier `phase-2-engine-platform.md` framing, which assumed dump was the engine's
subject rather than one source among several.)

## Goal

Turn today's dump-specific engine into a general artifact platform where **the dump is the first
implementation of an SPI, not the thing the engine is**. When Phase 6 adds trace, it should
plug into a slot that already exists and is already proven by a second implementation.

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
   today's `Analysis/Indexing`, leaving heap-*semantics* behind.
2. Create `DumpDetective.Sources.ClrDump`; move dump loading, heap indexing, cache, graph, query
   into it. Implement `IArtifactSource` as a wrapper over the existing prebuild path — behavior
   identical, index format identical modulo section renaming.
3. Split `IHeapAnalysisCache` into capability-scoped query surfaces; `HeapAnalysisCache` keeps its
   internals and implements several of them (mechanical interface segregation, no behavior change).
4. Build `ObservationStore` and `TimelineAligner` (new code, unused until Phases 5/6 — accept that
   they're speculative here, or defer them to their consuming phase if that's preferred; the
   argument for building now is that they're cheaper to design against dump-only reality).
5. Architecture rules: `Platform` may not reference any `Sources.*`; `Sources.*` may not reference
   each other.

## Exit criteria

- `DumpDetective.Platform` builds with only an `Sdk` reference — no ClrMD anywhere in it.
- `Sources.ClrDump` implements `IArtifactSource` end-to-end; a dump indexes through
  `ArtifactSourceRegistry` with no dump-specific code above the source boundary.
- All existing index/cache/graph perf and correctness tests pass unchanged.
- `IIndexStorage` has ≥ 2 implementations, both exercised.
- **A trivial second source exists** — even a stub (`gcdump` reading only `heap.types`, or a
  synthetic test source) — proving the SPI isn't accidentally shaped around ClrMD's peculiarities.
  This is the real exit criterion; without a second implementation, "general" is unverified.

## Risk / effort

High effort — the largest code-motion phase, moving most of today's non-analyzer logic. Low
*behavioral* risk **if** treated strictly as motion plus interface segregation. The failure mode is
scope creep: "while we're extracting the columnar writer, let's improve it." Don't. The extraction
is already hard enough to review; improvements are separately-justified changes.

The stub second source (exit criterion 5) is the item most likely to be cut for time and the one
most worth defending — it's the only thing that actually validates the abstraction.
