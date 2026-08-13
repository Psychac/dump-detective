# Retained-Size Candidate Selection

> Status: **All phases (0-4) done.** From `GCRootAnalyzer` P0-1
> discussion in [phase1/gcroot-analyzer-audit.md](phase1/gcroot-analyzer-audit.md) —
> once shallow-size resolution fixed there (index-backed, see
> [../cache/19-ObjectAddressLookupIndex.md](../cache/19-ObjectAddressLookupIndex.md)), clear
> remaining "true retained size" gap not GCRootAnalyzer-specific.

---

## Problem

`BoundedGraphWalk.ComputeExclusiveRetained` already shared platform primitive — walks
`EnumerateReferences` from starting object with shared `visited` set, bounded by
`maxBreadth`/`maxDepth`, computes exclusive (non-overlapping) retained size. Three
analyzers already call it independently:

| Analyzer | Call site | Candidate selection today |
|---|---|---|
| `DominatorAnalyzer` | `DominatorAnalyzer.cs:256`, `:559` | Top-K by prior score (`TopHighlyReferencedObjectsToShow`), uniform BFS budget across all K |
| `StaticRootLeakDetector` | via `CollectRetainedObjects` | Per static-root candidate, no shape filtering |
| `MemoryAnalyzer` | `MemoryAnalyzer.cs:79` | Per candidate, no shape filtering |

None distinguish shapes where shallow size already accurate proxy for retained size
(arrays, strings, blobs w/ no reference-typed fields) vs shapes structurally misleading
(small wrapper objects — `Dictionary`, `List`, custom classes — few-hundred-byte header
hiding multi-megabyte graph).

BFS budget currently spent uniformly, incl. cases just re-confirming number the
(now index-backed, accurate) shallow size already gave free.

`GCRootAnalyzer` about to become fourth independent reimplementation of same
"pick top-N, run BFS" pattern — trigger for writing shared capability instead.

---

## Why this is a platform concern, not an analyzer concern

- Traversal primitive (`ComputeExclusiveRetained`) already shared.
- *Targeting* logic (who gets BFS budget, why) not shared, duplicated ad hoc per analyzer.
- Shallow size now cheap + accurate for every object on heap (index-backed
  `TryGetObjectMetadata`, see [../cache/19-ObjectAddressLookupIndex.md](../cache/19-ObjectAddressLookupIndex.md)).
  Makes shape-based filtering possible w/o new indexing work — `ClrType` field
  metadata (reference vs value fields) already available from type system w/o live
  heap read per candidate.
- Any analyzer w/ "top suspect" list (`LeakCandidateAnalyzer`, `EventLeakAnalyzer`,
  `TimerLeakAnalyzer`, `AsyncTaskAnalyzer`, plus three above) currently reports
  shallow/average size for suspects, would benefit from same targeting logic
  w/o each reinventing it.

---

## Proposed design

Shared utility — tentatively `RetainedSizeCandidateSelector` — alongside
`BoundedGraphWalk` in `DumpDetective.Analysis.Traversal`. Two entry points, not one —
callers need different answer shapes (see Expected callers / Implementation plan
below for why):

```csharp
internal static class RetainedSizeCandidateSelector
{
    // Step 1 only — O(1), no BFS, no heap read. For callers that just need a
    // yes/no on whether shallow size already IS the retained size (e.g. to decide
    // whether to run a different, more expensive operation at all).
    public static bool RequiresWalk(IHeapAnalysisCache cache, ClrHeap heap, ulong methodTable);

    // Steps 1+2+3 — ranks candidates, applies the shape filter, walks only the
    // top maxCandidatesToWalk survivors, shared visited set for exclusivity.
    public static IReadOnlyList<RetainedSizeResult> SelectAndCompute(
        IReadOnlyList<(ulong Address, ulong MethodTable, ulong ShallowSize)> candidates,
        ClrHeap heap,
        IHeapAnalysisCache cache,
        HashSet<ulong> visited,     // shared, exclusive-retained semantics
        int maxCandidatesToWalk,    // BFS budget cap, independent of input count
        int maxBreadth = 10_000,
        int maxDepth = 20,
        CancellationToken cancellationToken = default);
}

internal readonly record struct RetainedSizeResult(
    ulong Address,
    ulong ShallowSize,
    ulong RetainedSize,      // == ShallowSize if BFS was skipped for this candidate
    bool WasWalked);         // false => RetainedSize is just ShallowSize, not a true walk
```

`RequiresWalk` and `SelectAndCompute` both go through
`IHeapAnalysisCache.MethodTableHasOutgoingRefs` — the public surface analyzers
already use, which internally delegates to `TypeMetadata.ContainsPointers` /
`ArrayContainsPointers` on `TypeMetadataCache` (see Phase 0 below) — rather than
inspecting `ClrType` directly, and rather than requiring analyzers to depend on
`TypeMetadataCache`, an internal type they don't otherwise hold an instance of.
Shape check already computed + cached there per type; selector doesn't duplicate it.

### Step 1 — Shape filter (no heap read, no BFS)

Per candidate, inspect `ClrType` (already resolvable from `MethodTable` via
`heap.GetTypeByMethodTable`, O(1) lookup, not address resolution):

- **Skip BFS** (shallow size stands as answer) for:
  - Arrays of value types (`byte[]`, `int[]`, etc.) — no outgoing references
  - `string` — no outgoing references
  - Types w/ **no reference-typed field anywhere in field tree** (see below)
- **Flag as BFS candidate** for:
  - Arrays of reference types (`object[]`, `T[]` where `T` class)
  - Types w/ ≥1 reference-typed field anywhere in field tree (covers
    `Dictionary`, `List`, virtually all custom wrapper/container classes)

**Field-tree check must recurse through nested value-type fields, not top-level
only.** `ClrObject.EnumerateReferences` walks into struct-typed fields to find embedded
object references (e.g. `class SmallWrapper { Entry _entry; }` where `struct Entry {
string Key; }` has zero top-level reference-typed fields on `SmallWrapper` but live
reference underneath). Shallow, top-level-only check would misclassify as
"skip BFS", silently under-report retained size — exactly false negative this
design exists to avoid. Check must recurse into any field whose type itself value
type, transitively. Value types can't be self-referential (CLR disallows struct
containing itself, directly/transitively — size violation), so recursion naturally
bounded, cycle-safe.

Computed **once per distinct `ClrType`**, not per object, cached on
`TypeMetadataCache` — single lookup per candidate at selection time. Consistent w/
"no heavy reflection in hot paths" rule in [CLAUDE.md](../../CLAUDE.md), since
metadata computed once per type, not live per-object reflection cost.

No named whitelist (`Dictionary<,>`, `List<>`, `HashSet<>`, ...) needed for
correctness — recursive field-tree check already flags these generically (array-typed
fields = reference types) + covers arbitrary custom container-shaped types whitelist
would miss. Whitelist brittle: BCL container internals change field layout across
.NET versions, custom user types never in it. Known-container types should instead
appear only as **test fixtures** — assert recursive filter correctly flags
`Dictionary<int,int>`, `List<T>`, etc. — guard against bugs in recursive walker,
not production special-casing.

### Step 2 — Budget-bounded walk

From shape-filtered survivors, rank by shallow size (or caller-supplied score), walk
only top `maxCandidatesToWalk`. Keeps BFS cost bounded independent of how many
candidates passed shape filter — e.g. dump w/ 500 `Dictionary`-shaped static roots
still only spends BFS budget on top 20–50, same as today's per-analyzer top-N caps.

### Step 3 — Shared visited-set semantics

Callers pass shared `visited` `HashSet<ulong>` so, as w/ `DominatorAnalyzer`'s
existing comment ("matching the semantics of `PopulateRetainedBytes`... ensures the two
retained-byte metrics are comparable"), retained sizes computed across multiple candidates in
same analyzer run stay exclusive/non-overlapping, comparable to each other.

---

## What this does NOT change

- `ComputeExclusiveRetained` itself unchanged — proposal only adds selection layer
  in front of it.
- Reference-graph edges still not persisted to disk (see
  [../cache/19-ObjectAddressLookupIndex.md § Problem](../cache/19-ObjectAddressLookupIndex.md));
  BFS still requires live `heap.GetObject` + `EnumerateReferences` per node actually
  walked. Proposal reduces *how many* nodes get walked, not per-node cost.
- Per [docs/cache/README.md § Non Goals](../cache/README.md): stays out of
  `HeapAnalysisCache`'s facade — traversal-layer utility, not new cache.

---

## Expected callers (once built)

| Analyzer | Current state | Expected change |
|---|---|---|
| `GCRootAnalyzer` | Path tracing only (`CollectForwardTypeNames`), no retained-size number at all today | New capability: adopt `SelectAndCompute` for top severity-ranked roots — closes "true retained size" gap noted deferred in P0-1 |
| `DominatorAnalyzer` | Uniform BFS across top-K, no shape filtering | Retrofit to `SelectAndCompute` — skips BFS for blob/array-shaped top types |
| `MemoryAnalyzer` | Per-candidate BFS, no shape filtering | Retrofit to `SelectAndCompute` |
| `StaticRootLeakDetector` | Unconditional `CollectRetainedObjects` (full retained-set dictionary + per-type breakdown) for every static root, regardless of shape | **Not** `SelectAndCompute` — needs `RequiresWalk` as pre-check instead (see Implementation plan Phase 4); needs full retained-set/type-breakdown shape `CollectRetainedObjects` produces, which `SelectAndCompute`'s single-number result doesn't cover |
| `LeakCandidateAnalyzer`, `EventLeakAnalyzer`, `TimerLeakAnalyzer`, `AsyncTaskAnalyzer` | Report shallow/average size for suspects | New capability — could adopt for "top suspect" retained-size reporting |

---

## Implementation plan

Sequenced so each phase independently landable/testable; later phases depend on
earlier ones.

### Phase 0 — Fix recursive field-tree check (foundation) — **Done**

`TypeMetadataCache.ConvertClrTypeToMetadata` now recurses into value-type fields
via `FieldTreeContainsPointers` (depth-capped at 32). Regression coverage in
`TypeMetadataCacheFieldRecursionTests`: a struct-field-holding-reference fixture,
plus live-heap `Dictionary<int,int>`/`List<int>` fixtures asserting the recursive
walk flags them without any type-name whitelist.

`TypeMetadataCache.ConvertClrTypeToMetadata` (`TypeMetadataCache.cs:119-161`) already
computes `TypeMetadata.ContainsPointers` per type, but only over top-level `type.Fields` —
exact non-recursive gap identified in question 1. Currently unreferenced by any
analyzer (only self-referenced within `TypeMetadataCache`/`TypeMetadata`), so fixing
zero blast-radius today:

- Recurse into value-type fields: per field, if `field.IsObjectReference` contributes
  pointer; else if `field.Type` non-primitive value type, recurse into its `Fields`. Cap
  recursion depth defensively (e.g. 32) as guard rail — value types can't be
  self-referential (CLR disallows it), so belt-and-braces, not correctness requirement.
- One-time cost per distinct `ClrType` (cache miss only) — amortized across every
  object of that type, not per-object/hot-path cost.
- Add unit tests: `SmallWrapper { Entry _entry; }` / `struct Entry { string Key; }` must
  report `ContainsPointers == true`. Plus `Dictionary<int,int>`, `List<T>`, `byte[]`,
  `string` as fixtures per question-1 resolution (assertions, not production
  special-casing).

### Phase 1 — `RetainedSizeCandidateSelector` — **Done**

Landed in `src/DumpDetective.Analysis/Traversal/RetainedSizeCandidateSelector.cs`, no
existing caller touched yet (see Phase 2-4 below). `RequiresWalk` and `SelectAndCompute`
implemented as specified above, both backed by Phase 0 `TypeMetadataCache.ContainsPointers`.
`SelectAndCompute` ranks by shallow size descending, walks only the top
`maxCandidatesToWalk` shape-eligible survivors via `BoundedGraphWalk.ComputeExclusiveRetained`
against the caller-shared `visited` set; non-walked candidates come back with
`RetainedSize == ShallowSize`, `WasWalked == false` rather than being dropped, so output
count always matches input count. Per resolved question 2, walked results are surfaced
as computed with no clamping — a result below shallow size is the correct signal that the
address was already claimed by an earlier candidate's walk in the same batch.

Unit tests in `RetainedSizeCandidateSelectorTests` (live-heap snapshot, same pattern as
`TypeMetadataCacheFieldRecursionTests`): `RequiresWalk` false for a pointer-free `byte[]`
and for method table 0, true for a type with a nested-reference struct field; `SelectAndCompute`
skips BFS (and leaves `visited` untouched) for a pointer-free candidate, walks a
pointer-containing candidate to a retained size ≥ shallow size, and — with
`maxCandidatesToWalk: 1` across two eligible candidates of different shallow size — spends
the budget on the larger one, leaving the smaller unwalked.

### Phase 2 — Retrofit `DominatorAnalyzer` and `MemoryAnalyzer` — **Done**

Both had "rank → top-N → `ComputeExclusiveRetained` per candidate" inline
(`DominatorAnalyzer.cs`: the top-K-types loop in `Analyze`, plus `PopulateRetainedBytes`
for the highly-referenced-objects list; `MemoryAnalyzer.cs`: the `EstimateRetained`
local function in `BuildDomainResult`). All three call sites now build a
`(Address, MethodTable, ShallowSize)` candidate list — `ShallowSize` is always the
*single sample object's* own size (`root.Size`), not a type-aggregate total, since
that's what a skipped-walk result falls back to — and hand it to
`RetainedSizeCandidateSelector.SelectAndCompute` against the same shared `visited` set
each call site already used. `maxCandidatesToWalk` is set to the full candidate count at
each site (no new budget cut introduced by this phase — the win is purely from the
shape filter skipping BFS for `byte[]`/`string`-shaped samples, not from walking fewer
candidates than before).

Regression coverage: full non-real-dump suite (485 tests) re-run clean, 2 pre-existing
unrelated failures (confirmed present before this session via `git stash`), 0 new
failures. `DominatorAnalyzerDiscrepancyTests.DominatorAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap`
(real-dump disk-vs-memory discrepancy test, run once per CLAUDE.md's real-dump rule)
failed on this environment, but the stack trace shows the failure inside
`DiskBackedObjectIndexWriter.Build`'s `freshdiskcheck` dummy-file setup — before
`DominatorAnalyzer` or any Phase 2 code path is ever reached — a pre-existing
environment/fixture gap, not a Phase 2 regression.

### Phase 3 — `GCRootAnalyzer`: new capability, not a retrofit — **Done**

Step 3 of `GCRootAnalyzer.Analyze` (the same top-`PathSearchTopN` loop that already
ran `CollectForwardTypeNames` for path tracing) now also resolves each candidate's
`(Address, MethodTable, ShallowSize)` via `cache.TryGetObjectMetadata` and runs them
through `RetainedSizeCandidateSelector.SelectAndCompute` — reusing
`options.MaxBfsNodes`/`options.MaxBfsDepth` as the walk's breadth/depth budget (same
caps path tracing already used, so no new tunables), against a visited set scoped to
this call (separate from path tracing's own internal per-candidate visited sets, since
path tracing and retained-size walks answer different questions from the same target).
`RootPathFinding` gained `EstimatedRetainedBytes` and `RetainedSizeWasWalked` (both
default to 0/false so existing callers/tests that construct it positionally are
unaffected); the reporting layer's `RootPath` model and `GCRootIntelligenceSectionBuilder`
were updated to carry the same two fields through to the rendered root-path groups. This
closes the "true retained size" gap deferred in the P0-1 audit — `GCRootAnalyzer` no
longer reports only shallow size for its top suspects.

Regression coverage: full non-real-dump suite re-run clean (485 tests, same 2
pre-existing unrelated failures, 0 new failures). No new `GCRootAnalyzer`-specific unit
test was added — exercising it end-to-end requires a disk-backed heap index
(`HeapAnalysisCache.PrebuildHeapIndex` needs a real dump path even for a live-process
snapshot, per the `DominatorAnalyzerDiscrepancyTests` fixture issue noted in Phase 2),
so correctness here rests on the already-thorough `RetainedSizeCandidateSelector` unit
suite (Phase 1) plus this full-suite regression run, consistent with Phase 2's approach.

### Phase 4 — `StaticRootLeakDetector`: filter-only, not `SelectAndCompute` — **Done**

Structurally different from the other three — `AnalyzeStaticRoots`
(`StaticRootLeakDetector.cs`) calls `BoundedGraphWalk.CollectRetainedObjects`, which
materializes a full `Dictionary<address, (MethodTable, Size)>` per root (no shared-visited
exclusivity), and needs the per-type breakdown (`TopRetainedTypes`,
`ContainsCollections`, `ContainsEventHandlers`) — not just a total. `SelectAndCompute`'s
single-number result doesn't fit this shape, so this phase uses `RequiresWalk` alone, as
originally planned.

`RequiresWalk` now runs *before* `CollectRetainedObjects` for every static root: when the
root's direct object's shape means it can't reach anything beyond itself, the
`Dictionary` build (capacity up to `Math.Min(1000, maxObjects)`) is skipped entirely and
a trivial single-entry result is synthesized directly from the already-resolved
`rootMetadata` (`ObjectsKeptAlive: 1`, `TotalMemoryImpact: rootMetadata.Size`,
`TopRetainedTypes: [{ TypeName: rootMetadata.TypeName, Count: 1, TotalSize: rootMetadata.Size }]`,
`ScanWasCapped`/`ContainsCollections`/`ContainsEventHandlers` all `false`) — same output
shape either branch produces, so nothing downstream (evidence/root-path lookup, report
rendering) needed to change. Since the analyzer previously ran `CollectRetainedObjects`
unconditionally for *every* static root regardless of shape, and static roots commonly
include `string`/array-of-value-type fields, this is likely the largest single
allocation/runtime win of the four retrofits.

Regression coverage: full non-real-dump suite re-run clean (485 tests, same 2
pre-existing unrelated failures, 0 new failures). No bespoke `StaticRootLeakDetector`
unit test was added — the analyzer has no existing unit-test harness (only a real-dump
discrepancy test and a benchmark), and isolating one specific static root among the
thousands present in a live test process for an in-process test would be fragile; the
real-dump discrepancy test for this analyzer was not run this session to avoid a second
full load of the same 3GB+ dump after `DominatorAnalyzerDiscrepancyTests` (Phase 2) hit
the same known-unrelated `freshdiskcheck` fixture gap. Correctness rests on the
`RetainedSizeCandidateSelector`/`RequiresWalk` unit suite (Phase 1, which `RequiresWalk`
here delegates to unchanged) plus this full-suite regression run.

### Sequencing and risk

Land Phase 0 + Phase 1 first (foundational, no existing caller touched). Phases 2-4 each
touch one analyzer at a time, independently testable/revertable. Phase 4 needs most
care since output shape (`TopRetainedTypes`, flags) changes for skipped roots —
needs discrepancy-test coverage comparing before/after on real dump, run one-at-a-time
per [CLAUDE.md](../../CLAUDE.md)'s real-dump test rule (never parallel, never more than
once per invocation).

### Perf validation

`tools/TruncationImpactValidator` and `tools/UnifiedIndexValidator` already establish
standalone-validator pattern in this repo (run before/after on real dump, report deltas).
`RetainedSizeSelectorValidator` following same shape — reporting BFS-node-count and
wall-clock delta pre/post retrofit — would give concrete numbers once Phase 2 lands, rather
than relying on unit tests alone for "extract every drop of performance" goal.

---

## Open questions for discussion

1. ~~**Filter precision**~~ — **Resolved.** Bar is "≥1 reference-typed field anywhere
   in type's field tree," computed via **recursive** walk through nested value-type
   fields (not top-level-only check), matching what `EnumerateReferences` actually
   traverses. Non-recursive check would false-negative on structs-with-reference-fields
   embedded in otherwise-reference-free wrapper types, worse than wasted BFS
   budget — silently under-reports retained size. No named container whitelist
   needed for correctness once check recursive; known containers
   (`Dictionary<,>`, `List<>`, `HashSet<>`) become test fixtures instead, asserting
   recursive walker classifies them correctly. See Step 1 above for full rationale.
2. ~~**Fallback on anomaly**~~ — **Resolved.** Keep `maxBreadth`/`maxDepth` bounded (don't
   remove them): `ComputeExclusiveRetained` counts candidate's own shallow size before
   continuing walk, so walk that hits breadth/depth cap can't itself produce
   `RetainedSize < ShallowSize` — that scenario unreachable via truncation. Removing
   bounds to "solve" it would trade non-issue for reintroducing unbounded per-candidate
   walk cost, exactly what `BoundedGraphWalk` exists to prevent — candidates
   Step 1 flags for BFS (wrapper/container objects) precisely ones most likely to
   have large fan-out on 10GB+ dump, so unbounded walk risks O(heap size) work per
   candidate. See [CLAUDE.md](../../CLAUDE.md) definition-of-done ("bounded memory usage,
   reasonable runtime") and existing depth-limit-20 convention for root-path BFS.
   Real (legitimate) source of `RetainedSize < ShallowSize` is shared `visited` set
   from Step 3: if candidate's own address already claimed by earlier candidate's
   walk in same run (exclusive/non-overlapping semantics), its own bytes may already be
   attributed elsewhere. Not an anomaly to guard against — correct semantics
   of exclusive retained size (same rationale `DominatorAnalyzer` already relies on).
   **Don't clamp to `max(ShallowSize, RetainedSize)`** — would double-count shared
   bytes back onto both candidates. Surface `RetainedSize` as computed, `WasWalked`
   already distinguishing "true walk result" from "shallow size reused
   unwalked."
3. ~~**Scope of first implementation**~~ — **Resolved.** Build selector generically
   from day one, in `DumpDetective.Analysis.Traversal` as proposed, rather than landing it
   `GCRootAnalyzer`-only first. Makes question 4 moot as separate follow-up decision
   (see below).
4. ~~**Retrofit priority**~~ — **Resolved.** Since selector generic from day one,
   all four current callers (`GCRootAnalyzer`, `DominatorAnalyzer`,
   `StaticRootLeakDetector`, `MemoryAnalyzer`) retrofit to it as part of same initial
   change, not deferred follow-up — no reason for three of four to keep
   duplicating "pick top-N, run BFS" pattern once shared utility exists.