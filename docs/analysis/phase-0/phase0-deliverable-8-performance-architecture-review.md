# Phase 0 — Deliverable 8: Performance Architecture Review

> Scope: **Deliverable 8 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Reviewed as a Performance Engineer against DumpDetective's own stated bar in
> [CLAUDE.md](../../CLAUDE.md): "works on 10GB+ dumps, bounded memory usage, reasonable runtime,
> no unnecessary allocations." This is an architectural-level review (no profiler run, no
> benchmark numbers) — it globalizes and quantifies the per-item findings from
> [Deliverable 4](phase0-deliverable-4-duplicate-work-analysis.md) and states what should be
> confirmed empirically once these are actionable.

## 1. Number of Full Heap Scans

Restated at the global level: **26 of 36 analyzers** independently stream the full on-disk object
index per run (`Index` or `Index+Container` mode, Deliverable 1). This is a direct violation, at
the architecture level, of CLAUDE.md's own hot-path guidance — not because any single analyzer
uses `.ToList()` or builds a full graph eagerly (nothing observed suggests that), but because the
platform as a whole performs the equivalent of ~26 full heap enumerations where its own design
intent (Phase 1 single-pass streaming index build, Phase 2 *scoped* analysis on subsets) clearly
assumes far fewer. For a 10GB+ dump, this is the difference between the project meeting its own
"reasonable runtime" bar and not.

## 2. Repeated Index Construction

Two distinct things can be meant by "index construction," and they should not be conflated:

- **The on-disk Phase 1 object index itself** appears to be built once, correctly, per
  `architecture.md`'s single-pass design — no evidence found of the disk-level index being
  rebuilt redundantly.
- **In-memory secondary indexes built by each analyzer while consuming that stream** — this is
  where the duplication actually lives. Every one of the 26 index-scanning analyzers builds its
  own working structure (typically a `Dictionary` keyed by type id or address) while streaming,
  then discards it at the end of `AnalyzeAsync`. There is no shared in-memory intermediate result
  passed between analyzers, so the same reduction (e.g. `TypeId → (count, bytes)`, Deliverable 4
  §5) is constructed from scratch up to 26 times per run.
- **Container/satellite indexes** (`Indexing.Container` for arrays/LOH/tasks, `Indexing.Satellite`
  for weak references) — whether these are built once during Phase 1 alongside the main index, or
  lazily constructed the first time a consuming analyzer runs, could not be confirmed from the
  available import-level data. **This should be verified directly against the index-writer
  implementation** before scoping fix work: if lazy-built per-analyzer-run rather than cached
  across runs within a session, this is a second, independent instance of "repeated index
  construction" beyond the in-memory case above.

## 3. Repeated Root Enumeration

A more specific case than Deliverable 4 §2's general traversal duplication. `GCRootAnalyzer` is
the canonical root enumerator — but ClrMD root enumeration itself (walking every thread's stack,
every static field across every loaded type, and every GC handle) is not free, and it appears to
be independently repeated by:

- `StaticRootLeakDetector` — enumerates static-field roots on its own
- `EventLeakAnalyzer` — enumerates static-field roots again, for its publisher sweep
- `DominatorAnalyzer` (and `RetentionAnalyzer`, pre-merge) — a dominator tree is rooted at the GC
  root set by construction; computing one without consuming `GCRootAnalyzer`'s already-enumerated
  roots means re-deriving the same root set a third and fourth time

**Estimated cost**: root enumeration scales with thread count and static-field count, not object
count, so it's cheaper per-invocation than a full index scan — but at up to 4-5 independent
invocations per run, and given static-field enumeration in particular requires walking every
loaded type's statics (which can be thousands of types in a large application), this is not
negligible, and it's pure waste: the root set does not change during a single analysis run.

## 4. Duplicate Caching

Raw `MethodTable → ClrType` resolution is correctly centralized in `HeapAnalysisCache` (confirmed
in Deliverable 4 §3) — this is the one part of the caching story that's already right. Everything
layered on top of it is where duplication reappears:

- **Type-name classification caches** — the 8 analyzers doing type-name pattern matching
  (Deliverable 4 §3, Deliverable 5 item 4) each plausibly cache their own classification result
  per type rather than sharing one classifier cache keyed by `MethodTable`/type id.
- **`CollectionAnalyzer`'s reflection-based field-layout cache** — a second cache, independent of
  `HeapAnalysisCache`, for a data shape (`ClrType` → field layout) that overlaps with what a
  well-factored `HeapAnalysisCache` extension could already provide, and that `EventLeakAnalyzer`
  likely needs an equivalent of for its own field probing (Deliverable 4 §7).
- **Handle target resolution** — `GCHandleAnalyzer`, `DependentHandleAnalyzer`, and
  `WeakReferenceAnalyzer` each independently resolve and plausibly cache handle-target addresses
  while walking overlapping parts of the same handle table (Deliverable 1/3/6 handle-trio finding).

**Cost note**: caching duplication is a smaller memory-pressure concern than the heap-scan
duplication above (these caches are bounded by type/handle count, not object count), but it works
directly against CLAUDE.md's explicit caching rule ("Allowed: type metadata, MethodTable → Type
maps; Avoid: ... redundant per-analyzer state") and is a correctness risk if the independent
caches can drift (e.g., two different classifications of the same type in two reports).

## 5. Duplicate Allocations

Architectural risk vectors, not profiled measurements:

- **Per-analyzer aggregation structures.** Each of the ~26 index-scanning analyzers allocates its
  own `Dictionary`/list sized proportional to type count or object count for its own reduction.
  With potentially thousands of distinct types in a large application heap, this is on the order
  of 26x the dictionary allocation volume that a single shared reduction (Deliverable 5 item 2)
  would require.
- **Redundant `ArrayPool` rent/return cycles.** Each analyzer's own `ObjectIndexReader` instance
  rents and returns its own buffers independently. Because they go through the shared pool, this
  isn't a leak, but it is 26x the rent/return churn and, more importantly, 26x the CPU cost of
  re-deserializing the same on-disk bytes into `HeapEntry` structs — pure redundant work, not just
  redundant allocation.
- **Sample-buffer duplication.** The resource-sampler quartet (`DbConnectionAnalyzer`/
  `WcfChannelAnalyzer`/`HttpObjectAnalyzer`/`TimerLeakAnalyzer`) each maintain their own bounded
  sample collection (`MaxStateSamples`-shaped) — correctly bounded individually per CLAUDE.md's
  allocation guidance, but 4 independent implementations of the same bounded-sampling structure
  where one configurable one would do (Deliverable 4 §7, Deliverable 5 item 7).
- **Possible string-interning duplication.** CLAUDE.md mandates a `string → int` type-id map to
  intern/dedupe type names. If any of the 8 type-classifying analyzers (item 4 above) maintain
  their own local string keys instead of referencing the canonical interned type-id map, that's
  redundant string retention working against the project's own explicit anti-pattern list.

## Consolidation Opportunities (ranked by expected impact)

| # | Consolidation | Addresses | Owner (Deliverable 5) |
|---|---|---|---|
| 1 | Single-pass index scan dispatcher, with per-type statistics computed once inside the same pass | §1 heap scans, §2 in-memory index construction, §5 aggregation-structure allocations — the single highest-leverage fix, addressing three of five review categories at once | Item 1 + item 2 |
| 2 | Canonical root-set artifact from `GCRootAnalyzer`, consumed by `DominatorAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer` instead of each re-enumerating | §3 repeated root enumeration | Item 3 (root/retention graph service) |
| 3 | Confirm container/satellite index build-once-per-session behavior; fix if lazily rebuilt per analyzer invocation | §2 container index construction | New — flagged here for Deliverable 9/10, not previously scoped |
| 4 | Shared type-classification cache and shared reflection field-layout cache, both layered on `HeapAnalysisCache` rather than reinvented per analyzer | §4 duplicate caching | Items 4 and 5 |
| 5 | One handle-table walk shared by the (post-merge) `GCHandleAnalyzer` and `WeakReferenceAnalyzer` | §3/§4 (root/handle enumeration and caching) | Deliverable 6 merge + item 3 |
| 6 | Shared typed-resource sampler for the DbConnection/Wcf/Http/Timer quartet | §5 sample-buffer duplication | Item 7 |

## What This Review Could Not Determine (flag for Deliverable 9/10)

This was a static, architecture-level pass — the following require empirical confirmation before
prioritizing fix work with confidence:

- Actual wall-clock/I/O cost of the ~26x index-scan multiplier on a representative 10GB+ dump.
- Whether container/satellite indexes are truly rebuilt per-analyzer-invocation or already cached
  across a session (item 3 above).
- Actual peak memory usage contribution from duplicate per-analyzer aggregation structures on a
  heap with a very large distinct-type count.

Recommend a profiling pass (dotnet-trace/dotMemory against a representative large dump) as a
prerequisite to committing engineering time to the dispatcher redesign (Deliverable 5 item 1),
since it is the highest-effort item on this list and the estimate here is architectural, not
measured.
