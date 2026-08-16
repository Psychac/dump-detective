# Dominator Tree — Implementation Plan

Implementation plan for [dominator-tree-lengauer-tarjan.md](dominator-tree-lengauer-tarjan.md)
(design/decision doc — read that first). This doc is the "how to build it" companion, mirroring the
[event-leak-analyzer.md](event-leak-analyzer.md) / [event-leak-analyzer-implementation-plan.md](event-leak-analyzer-implementation-plan.md)
split already used in this repo.

**Scope for this pass**: D1-D9 in full, including D5 (persisted forward-edge index — required
groundwork, not deferred). D7 (persisted computed tree) has its section *format* implemented and
tested, but the "append to an already-finalized `cache.bin`" integration is deliberately on hold —
see Phase 4/6 below. Report integration (swapping the P2-4 sub-table's heuristic column for
`ExactRetainedBytes`) was originally deferred past "ship dark" but has since landed too — see
Phase 7.

**Order matters**: D5 must land before the rest is meaningfully testable end-to-end, since it's the
edge source everything downstream consumes. Phases below are numbered in build order, not decision
number order.

---

## Phase 1 — D5: Persisted forward-edge index (Phase 1 pipeline change) — ✅ DONE

**Real-dump validation complete** (2026-08-16): ran the production `DiskBackedObjectIndexWriter.Build()`
with and without `DD_SKIP_FORWARD_INDEX_BUILD=1` on both dumps (redirected to a scratch cache dir
via `DumpIndexPaths.ResolveCacheDirectory` to avoid touching either dump's real `cache.bin`). True
incremental cost: **~1.94s (~8%) on the 3GB dump, ~103.26s (~16.7%) on the 25GB dump** — ~5.7x
smaller than the spike's from-scratch upper bound (588.85s), confirming the "reads already happen,
this is close to free" premise. This is the number that answers the design doc's former Open
Question 1. All 5 new round-trip unit tests and the full existing suite (556 tests) pass.

**Where**: hooks into the exact same streaming pass that already builds the capped reverse index.

- `src/DumpDetective.Analysis/Indexing/DiskBackedObjectIndexWriter.cs` — the `Parallel.For` loop
  already calls `reverseEdgeExtractor.RecordEdgesBatch(...)` per object's forward references during
  the Phase 1 scan (confirmed at `DiskBackedObjectIndexWriter.cs:275-298`). Add a second, parallel
  accumulation path in the same loop that batches `(parent, child)` pairs keyed by **parent** instead
  of child — no new ClrMD reads, the fields are already being enumerated for the existing reverse
  extractor call.
- New `src/DumpDetective.Analysis/Indexing/ForwardIndex/ForwardEdgeExtractor.cs`, mirroring
  `ReverseIndex/ReverseEdgeExtractor.cs`'s shape (hash-partitioned scratch buckets, per-bucket locks)
  but **no fanout cap** (D3: out-degree has no hub problem, avg 2.35 measured) and keyed by parent
  address for CSR-style grouping instead of child.
- New `src/DumpDetective.Analysis/Indexing/ForwardIndex/ForwardEdgeIndexReader.cs`, mirroring
  `ReverseEdgeIndexReader.cs` — exposes an `IForwardReferenceProvider`-shaped API (new interface in
  `DumpDetective.Core.Abstractions`, mirroring `IBackwardReferenceProvider`): `successors(address) ->
  addresses`, plus a bulk/sequential enumeration mode for the graph-builder in Phase 2 below.
- `CacheContainerFormat.cs`: add `ForwardEdgeBuckets`, `ForwardEdgeDirectories`,
  `ForwardEdgeMetadata` to `CacheSectionId` (values 18-20 — confirmed additive: `TOC` sizing is
  `Enum.GetValues<CacheSectionId>().Length`-derived, not a hardcoded constant, and `SegmentIndex`
  (17) already established the "no `FormatVersion` bump for a purely-optional section" precedent).
- Skip flag: `DD_SKIP_FORWARD_INDEX_BUILD=1`, mirroring `DD_SKIP_REVERSE_INDEX_BUILD=1`. Absent
  section → downstream falls back to a live walk (Phase 2 below), same graceful-degradation contract
  every other satellite index already has.
- Cache-hit integration: extend `HeapAnalysisCache`'s satellite-section lazy-open pattern (see
  `TryGetReverseIndexProvider()` at `HeapAnalysisCache.cs:306`) with a matching
  `TryGetForwardIndexProvider()`.

**Validation milestone (real dump, resolves design doc Open Question 1)**: run the pipeline's cache
build with and without `DD_SKIP_FORWARD_INDEX_BUILD=1` on both dumps, diff wall-clock. This is the
*actual* incremental-cost number — replaces the spike's 588.85s from-scratch upper bound with the
real marginal cost inside the real (parallel) Phase 1 pass.

**Tests**: unit tests for `ForwardEdgeExtractor`/`ForwardEdgeIndexReader` round-trip (mirror the
existing `tests/DumpDetective.Tests/Unit/Indexing/ReverseEdgeContainerWriterTests.cs` shape). One
new discrepancy-style integration test (see `CacheDiscrepancies/ReverseIndexBuildIntegrationTests.cs`
for the pattern) asserting forward-index-derived edges match live `ClrObject.EnumerateReferences`
edges exactly, on a real dump — **run one dump at a time, foreground**, per this project's real-dump
test rule.

---

## Phase 2 — Reachable-graph builder (D2, D4, D8) — ✅ DONE (unit-tested; real-dump N/E regression check still pending)

Implemented under
[`src/DumpDetective.Analysis/Traversal/Dominator/`](../../../src/DumpDetective.Analysis/Traversal/Dominator/),
split heap-agnostic-core / ClrHeap-adapter (mirroring the `LengauerTarjan`/`BidirectionalGraphSearch`
pattern) rather than one monolithic class, so the graph algorithm itself is directly unit-testable:

- [`DenseIdMap.cs`](../../../src/DumpDetective.Analysis/Traversal/Dominator/DenseIdMap.cs) — promoted
  from the spike prototype. Same design: linear probing, power-of-two capacity, ~13 bytes/slot.
- [`ReachableGraphWalker.cs`](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphWalker.cs) —
  the heap-agnostic D2/D4/D6 core: injected root addresses + a `successors` function, single-pass
  edge capture into a flat buffer, O(N+E) counting-sort forward+reverse CSR build, D6's mid-walk cap
  enforcement (returns a `Capped()` result with no partial state on overflow).
- [`ReachableGraphBuilder.cs`](../../../src/DumpDetective.Analysis/Traversal/Dominator/ReachableGraphBuilder.cs) —
  the thin ClrHeap/cache adapter: resolves roots via `cache.GetOrBuildValidRoots`, picks the
  `successors` source (`TryGetForwardIndexProvider()` when available — §D5 zero-ClrMD consumption
  path — else the §D4 live `ClrObject.EnumerateReferences(carefully: true)` fallback), then resolves
  each node's `MethodTable`/`ShallowSize` (via `cache.TryGetObjectMetadata`) and `GenerationTag`
  (segment-based, mirrors `SegmentKindMapper.ResolveGeneration`).
- [`LeafFolder.cs`](../../../src/DumpDetective.Analysis/Traversal/Dominator/LeafFolder.cs) — D8's
  post-CSR-build pass, pure arrays (no ClrHeap dependency): identifies out-degree-0/in-degree-1
  nodes, folds their shallow size into their sole parent (guaranteed to survive — any node with an
  outgoing edge has out-degree ≥ 1, so it can never itself be foldable), rebuilds a renumbered
  reduced forward+reverse CSR over the surviving node set for LT to consume.
- `GenerationTag` moved to `DumpDetective.Core.Enums` (public) since both this layer and the eventual
  Phase 3 output model need it.
- D6's cap enforcement lives directly in `ReachableGraphWalker.Walk` (mid-walk abort, no separate
  `NodeCapGuard` file needed — the check was simple enough to inline).

**Tests**: 12 new unit tests
([`ReachableGraphWalkerTests.cs`](../../../tests/DumpDetective.Tests/Unit/Traversal/Dominator/ReachableGraphWalkerTests.cs),
[`LeafFolderTests.cs`](../../../tests/DumpDetective.Tests/Unit/Traversal/Dominator/LeafFolderTests.cs))
using the same hand-built-graph pattern as `LengauerTarjanTests` — dense-id assignment, diamond/cycle/
multi-root graphs, cap-exceeded behavior, single-vs-shared-leaf fold correctness, reduced-CSR edge
preservation. All pass, plus the full existing suite (568 tests total). One real bug caught while
writing the tests: an early version of a hand-built test graph had an unintended *second* foldable
node (a chain tail also satisfies out-degree-0/in-degree-1), which was the test's own mistaken
expectation, not a `LeafFolder` bug — fixed by giving the "should survive" node its own outgoing edge.

**Still pending** (not done in this pass): the real-dump integration test asserting `N`/`E` match the
design doc's measured baseline exactly (6.69M/17.37M and 58.34M/137.03M) — a regression guard against
silently reintroducing either bug already caught and fixed during spiking (struct-array undercounting,
phantom root nodes). Deferred to Phase 6's real-dump validation pass rather than run here in isolation.

---

## Phase 3 — LT wiring + rollup (D8 continued, Phase 4/5 of the algorithm) — ✅ DONE (unit-tested; real-scale measurement deferred to Phase 6)

Implemented as
[`DominatorTreeComputer.cs`](../../../src/DumpDetective.Analysis/Traversal/Dominator/DominatorTreeComputer.cs):

- One structural gap caught and fixed *before* writing any code: Phase 2's `ReachableGraphWalker`
  does plain multi-root BFS (no single "root" node), but `LengauerTarjan.ComputeImmediateDominators`
  needs exactly one root id. Fixed by adding `IsRoot` tracking to `ReachableGraphWalkResult` (which
  node ids were seeded directly from root addresses) and wiring a synthetic virtual-root id
  (`n`, one past the reduced id space) with an edge to each real root — the "standard LT
  construction for multi-root graphs" the design doc always called for, just not yet built in
  Phase 2.
- A second correctness gap this surfaced: `LeafFolder` (§D8) would have folded a GC root that
  happens to have out-degree 0 and in-degree 1 from some other real object — losing its
  directly-rooted status, since a root has an "invisible" incoming edge from the virtual root the
  CSR doesn't represent. Fixed by threading `IsRoot` through to `LeafFolder.Fold` (now takes an
  optional `bool[] isRoot` and never folds a root node regardless of degree) — covered by
  `Compute_RootWithSingleParentDegreeShape_IsNeverFoldedEvenThoughItLooksFoldable`.
- Feeds `LeafFolder`'s reduced CSR into the already-implemented
  [`LengauerTarjan.ComputeImmediateDominators`](../../../src/DumpDetective.Analysis/Traversal/LengauerTarjan.cs)
  unmodified (heap-agnostic, injected successors/predecessors — exactly the shape this needed).
- Retained-bytes rollup: iterative (no recursion) preorder-then-reverse subtree-sum over the
  dominator tree, folded-leaf bytes (§D8) included in each surviving node's own contribution before
  summing.
- Added the output-model records from the design doc's Output Model section
  (`DominatorTreeResult`, `DominatorNodeSnapshot`, `DominatorTreeMode`, `DominatorTypeRollup`) to
  [`src/DumpDetective.Analysis/Models/DominatorTreeResult.cs`](../../../src/DumpDetective.Analysis/Models/DominatorTreeResult.cs)
  — `GenerationTag` itself already lives in `DumpDetective.Core.Enums` from Phase 2. Not yet wired
  to `DominatorTreeComputer`'s output (that mapping — resolving `TypeName` from `MethodTable`, which
  needs `ClrHeap` — belongs to Phase 5).

**Tests**: 4 new unit tests
([`DominatorTreeComputerTests.cs`](../../../tests/DumpDetective.Tests/Unit/Traversal/Dominator/DominatorTreeComputerTests.cs))
asserting retained-bytes rollup correctness (not just `idom[]`) on diamond/single-parent-leaf/
multi-root/root-degree-edge-case graphs. All pass, plus the full existing suite (572 tests total).

**Deliberately deferred to Phase 6**: the first real-scale measurement of `LengauerTarjan` itself
(design doc Open Question 3) — only ever exercised against ≤7-node hand-built graphs so far, still
true after this phase. Watch specifically for the `List<int>?[]` bucket-allocation pattern flagged
as a possible hidden cost at 58M-node scale; don't assume "architecturally cheap" holds until
measured. This needs `DominatorAnalyzer` actually wired up (Phase 5) to get a realistic end-to-end
number, not run in isolation here.

---

## Phase 4 — D9 flag + D7 persistence

- `RetentionOptions.cs`: add `EnableExactDominatorTree` (bool, default `true`), independent of
  `AnalysisProfile.Preset(...)` per D9's decision (not profile-branched — the profile system is
  expected to be simplified later).
- New `src/DumpDetective.Analysis/Indexing/Dominator/DominatorTreeIndexWriter.cs` /
  `DominatorTreeIndexReader.cs` implementing D7's format: two parallel `ulong[]` columns
  (`DominatorReachableAddresses[]` sorted, `DominatorImmediateDominatorAddresses[]` aligned),
  new `CacheSectionId` values past 20 (following Phase 1's forward-index additions), written
  unconditionally when `DominatorAnalyzer` computes `Mode == Exact`, validated via the standard
  `DumpContentHash` check (no extra options-dependent invalidation — D7's reasoning: an exact result
  doesn't depend on what cap value was active).
- Reader path: on cache hit, skip straight to `RetainedBytesRollup` using the persisted `idom[]` +
  already-persisted `ObjectSizes` column — no walk, no CSR build, no LT.

## Phase 4 status — ✅ D9 done; D7 format done, but a real integration blocker was found

- `RetentionOptions.EnableExactDominatorTree` (bool, default `true`) and
  `ExactDominatorTreeMemoryBudgetBytes` (default 6GB, §D6) added — not yet consumed anywhere
  (that's Phase 5).
- `DominatorTreeIndexWriter.cs`/`DominatorTreeIndexReader.cs` implement D7's exact format (two
  columnar `ulong[]` sections, `CacheSectionId` 21-22, sorted-address binary search) and are
  round-trip tested (4 new unit tests: section presence, exact round-trip on unsorted input,
  unknown-address miss, 10,000-entry binary-search correctness).

**Found, and deliberately deferred (2026-08-16), not solved speculatively: `CacheContainerWriter` is
write-once.** It always creates a brand-new file and atomically renames it on `Finish()` — there's
no support for reopening an already-finalized `cache.bin` to append a section. D7's whole premise (a
*second* pipeline run finds the persisted tree and skips recomputation) needs the section written
*after* Phase 1's container write already completed, since `DominatorAnalyzer` runs in Phase 2
(on-demand, post-Phase-1). As designed, that would mean either a full container rewrite (copying the
entire existing `cache.bin` — multi-GB on the dumps this plan measures against) or a real change to
`CacheContainerWriter` to support incremental append. Decision: don't solve this yet — whether it's
worth solving depends on how expensive computing the tree turns out to be once measured end-to-end
in Phase 5/6. Cheap compute means drop D7; expensive compute means solve the append problem for
real. This is now design doc Open Question 5. The originally-planned "cold-vs-warm regression test
on a real dump" is on hold for the same reason.

---

## Phase 5 — Wire into `DominatorAnalyzer`, ship dark — ✅ DONE

- `DominatorAnalyzer.AnalyzeAsync` now attempts the exact path when `EnableExactDominatorTree` is
  set: derives a node cap from `ExactDominatorTreeMemoryBudgetBytes` via the ~76 bytes/node ratio
  (§D6), calls `ReachableGraphBuilder.Build` → `DominatorTreeComputer.Compute`, and logs a
  comparison against the existing heuristic's `TotalEstimatedRetainedBytes` — **`DominatorDomainResult`'s
  report-visible output is untouched**, matching the design doc's "ship dark" plan. Deliberately
  scoped to exclude any D7/`DominatorTreeIndexWriter`/`Reader` wiring (persistence stays deferred
  per the Phase 4 decision above) and any report/section-builder changes (deferred to a later
  release per the design doc).
- Structured diagnostic logging via the `ILogger<DominatorAnalyzer>?` pattern (constructor-injected,
  optional, per [architecture.md § 14](../../architecture.md#14--observability)) — logs node count,
  cap, folded-leaf count (§D8), wall-clock, and the exact-vs-heuristic total-retained-bytes
  comparison on success; logs a cap-exceeded info message or a warning + exception on failure. Any
  exception in the exact path is caught (except `OperationCanceledException`, which propagates) so
  a bug in the new code can never affect the analyzer's actual output — this is the safety property
  "ship dark" depends on.
- Not yet exercised at real scale — first real-scale `LengauerTarjan`/full-pipeline measurement is
  Phase 6's job, run once, foreground, one dump at a time.

---

## Phase 6 — Real-dump validation (performance and correctness) — ✅ DONE

Run via a new opt-in real-dump test,
[`DominatorAnalyzerExactTreeRealDumpTests`](../../../tests/DumpDetective.Tests/Integration/CacheDiscrepancies/DominatorAnalyzerExactTreeRealDumpTests.cs)
(`DiscrepancyFact`-gated, `DD_RUN_DISCREPANCY_TESTS=1`), which loads the real dump, prebuilds the
heap index into a scratch cache directory (`DD_SCRATCH_DIR`, never the dump's real `cache.bin`), and
runs `DominatorAnalyzer.AnalyzeAsync` with a test `ILogger<DominatorAnalyzer>` that forwards to
xunit's test output — capturing the exact-vs-heuristic comparison line the Phase 5 wiring logs. Both
dumps, one at a time, foreground, never concurrent, per the project rule for any test loading a real
`.dmp`. (The 25GB run's scratch cache directory had to be redirected off `%TEMP%`/`C:` via
`DD_SCRATCH_DIR` — the default temp drive didn't have enough free space for a 25GB dump's scratch
index files.)

**Results:**

| | 3GB dump | 25GB dump |
|---|---:|---:|
| Phase 1 index build (cold, scratch dir) | 27.76s | 570.81s |
| Exact tree computed (LT + rollup + fold) | 13.75s | 218.49s |
| Reachable `N` | 6,686,490 | 58,339,936 |
| Leaves folded (D8) | 2,115,540 (31.6%) | 27,100,729 (46.5%) |
| Exact retained bytes at GC roots vs. heuristic estimate | 1.02GB vs. 6.9MB | 11.0GB vs. 3.0MB |
| `AnalyzeAsync` total | 16.00s | 244.87s |
| Managed memory delta | ~0 | ~1.95GB (budget: 6GB) |

**Correctness:**
- `N` matches the design doc's independently-measured D4/D5 baseline exactly on both dumps
  (6,686,490 / 58,339,936) — regression guard passed, no re-introduction of the two bugs caught
  during spiking.
- Cap never exceeded (84.77M cap vs. 58.34M actual on the 25GB dump) — the `EnableExactDominatorTree`
  default-on policy (D9) is safe at this scale.
- Exact retained bytes are, as expected, far larger than the heuristic's top-K-only estimate on both
  dumps (the heuristic structurally under-attributes — see design doc "Why not the current
  heuristic") — no case found where exact was smaller than the heuristic for a comparable candidate.
- dotMemory/VS Memory Profiler comparison (Open Question 4) — **not done**, still best-effort/external
  and outstanding; not a gate for closing this phase.
- **A real bug was caught by this run**: `TryLogExactDominatorTreeComparison` looped over
  `tree.Idom.Length` (= reduced node count + 1, includes the virtual root's own slot) while indexing
  `tree.RetainedBytes[]` (= reduced node count only), throwing `IndexOutOfRangeException` on the very
  first real-dump run. Fixed by bounding the loop to `tree.VirtualRoot` instead. This is exactly what
  "ship dark" is for: the bug was fully contained (heuristic result unaffected, caught + logged), and
  a targeted fix took one line. Full unit suite (576 tests) re-verified green after the fix, then both
  real-dump runs re-confirmed clean.

**Performance:**
- Confirmed cheap relative to Phase 1 on the 3GB dump; **not** cheap in absolute terms on the 25GB
  dump (218.49s) — this is the real number D7's decision was waiting on. See the design doc's Open
  Question 5: data now leans toward "worth solving," but the decision is **deliberately still held
  off** rather than acted on automatically.
- D5's real incremental Phase 1 cost (measured in Phase 1 above) was already folded into this run's
  cold-build numbers; no separate regression observed.

**Explicitly out of scope, still deferred**: report output changes, D7 persistence integration (on
hold, see above), dumps beyond the two already used throughout the design doc.

---

## Phase 7 — Report integration — ✅ DONE

Originally planned to stay deferred past "ship dark" for a release, but landed immediately after
Phase 6's real-dump validation confirmed the exact path is safe and correctly bounded.

- `DominatorDomainResult` gained one new field:
  [`ExactRetainedBytesByTypeName`](../../../src/DumpDetective.Analysis/Models/DominatorDomainResult.cs)
  (`IReadOnlyDictionary<string, ulong>?`) — null whenever the exact path wasn't attempted, was capped,
  or threw, in which case every other field (and the report) is byte-for-byte what it was before this
  phase.
- `DominatorAnalyzer.TryComputeExactDominatorTree` (renamed from the Phase 5 log-only method) now
  also builds this dictionary: one O(N) pass over the reachable graph aggregating exact retained
  bytes by `MethodTable` (a folded §D8 leaf's retained bytes = its own shallow size, since a leaf's
  subtree is just itself — no special-casing needed), then resolves type names only for the
  candidates the report will actually show (the heuristic top-K types already computed), not every
  reachable type — avoids wasted `ClrType` name resolution for types the report never displays.
- [`DominatorSectionBuilder`](../../../src/DumpDetective.Reporting/SectionBuilders/DominatorSectionBuilder.cs):
  the Gen2/LOH sub-table's "Retained" column uses the exact value per-row when a type name match
  exists, falls back to the existing heuristic estimate otherwise, and adds a one-line caveat when
  any row in the table used exact data. The main dominator-suspects table and the
  highly-referenced-objects table are untouched, exactly as the design doc scoped this.
- New unit coverage:
  [`DominatorSectionBuilderTests`](../../../tests/DumpDetective.Tests/Unit/Reporting/DominatorSectionBuilderTests.cs)
  (3 tests: no exact data falls back to heuristic; exact data for the displayed type overrides it;
  exact data present but for a *different* type name still falls back for the unmatched row).
- Verified against the real 3GB dump (smoke check, not a full Phase 6 re-run): exact retained bytes
  resolved for 14/15 top dominator types in one run, no exceptions, no measurable timing regression
  vs. the Phase 6 baseline (13.26s exact-tree compute vs. 13.75s previously — within noise). The 15th
  type's sample address didn't resolve to a live object at report time and fell back silently, as
  designed — not a bug, just the expected behavior for a stale sample address.
- D7 persistence remains untouched by this phase and is still deliberately on hold.

**Not done, still open**: retiring this work as a closed P3 audit item (docs/analysis/phase1/dominator-analyzer-audit.md) —
worth doing now that report integration has landed, but out of scope for this pass.
